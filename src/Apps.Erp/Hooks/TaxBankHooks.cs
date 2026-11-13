using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

/// <summary>Faktur Pajak: nilai, pihak, dan masa pajak dari dokumen sumber; satu faktur pajak aktif per dokumen (kecuali pengganti).</summary>
public sealed class TaxInvoiceHook(IDocumentStore store) : IBeforeValidate<TaxInvoice>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var refType = d.Str("reference_doctype"); var refName = d.Str("reference_name");
        if (refType is null || refName is null) return;
        var src = await m.RequireAsync(refType, refName, "reference_name", "Source document", ct);
        if (src.DocStatus != DocStatus.Submitted) throw Errors.One("reference_name", $"{refName} is not submitted or has been cancelled");
        var sales = refType.StartsWith("Sales", StringComparison.Ordinal);
        d["direction"] = sales ? "Output" : "Input";
        d["party_type"] = sales ? "Customer" : "Vendor";
        d["party"] = src.Data.Str(sales ? "customer" : "vendor");
        d["company"] = src.Data.Str("company");
        var taxable = src.Data.Rows("items").Sum(r => r.Dec("taxable_amount"));
        var dpp = src.Data.Dec("tax_base");
        d["tax_base"] = taxable;
        d["other_tax_base"] = dpp != taxable ? (object?)dpp : null;
        d["vat_amount"] = src.Data.Dec("vat_amount");
        var date = d.Date("tax_invoice_date") ?? src.Data.Date("posting_date");
        if (date is { } t) d.SetIfEmpty("tax_period", $"{t:yyyy-MM}");

        var others = (await m.QueryAsync("TaxInvoice", [new FilterClause("reference_name", "=", refName)], ct, take: 20))
            .Where(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Data.Str("status") is not ("Cancelled" or "Replaced")
                        && x.Name != d.Str("replaces")).ToList();
        if (others.Count > 0) throw Errors.One("reference_name", $"{refName} already has tax invoice {others[0].Name}; create a replacement by filling 'Replacement for'");

        // Nota kredit/debit: retur atas faktur pajak asal (faktur pajak aktif milik faktur yang diretur).
        if (refType is "SalesCreditNote" or "PurchaseDebitNote" && d.Str("return_of") is null && src.Data.Str("return_against") is { } origin)
        {
            var original = (await m.QueryAsync("TaxInvoice", [new FilterClause("reference_name", "=", origin)], ct, take: 20))
                .Where(x => x.DocStatus != DocStatus.Cancelled && x.Data.Str("status") is not ("Cancelled" or "Replaced"))
                .OrderByDescending(x => x.Name, StringComparer.Ordinal).FirstOrDefault();
            if (original is not null) d["return_of"] = original.Name;
            else c.Messages.Add($"The original invoice {origin} has no active tax invoice yet; fill 'Original tax invoice (return)' manually");
        }
    }
}

/// <summary>Bukti potong: kode PPh, dasar, pihak, dan masa pajak dari dokumen sumber bila diisi.</summary>
public sealed class WithholdingCertificateHook(IDocumentStore store) : IBeforeValidate<WithholdingCertificate>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var refType = d.Str("reference_doctype"); var refName = d.Str("reference_name");
        DateOnly? date = d.Date("certificate_date");
        if (refType is not null && refName is not null)
        {
            var src = await m.RequireAsync(refType, refName, "reference_name", "Source document", ct);
            if (src.DocStatus != DocStatus.Submitted) throw Errors.One("reference_name", $"{refName} is not submitted or has been cancelled");
            d.SetIfEmpty("company", src.Data.Str("company"));
            if (refType is "PurchaseInvoice" or "PurchaseDebitNote")
            {
                d["direction"] = "We withhold"; d["party_type"] = "Vendor"; d["party"] = src.Data.Str("vendor");
                d.SetIfEmpty("tax_code", src.Data.Str("wht_code"));
                if (d.Dec("gross_amount") == 0) d["gross_amount"] = src.Data.Dec("wht_base");
            }
            else if (refType is "SalesInvoice" or "SalesCreditNote")
            {
                d["direction"] = "Withheld by counterparty"; d["party_type"] = "Customer"; d["party"] = src.Data.Str("customer");
            }
            date ??= src.Data.Date("posting_date");
        }
        if (date is { } t) d.SetIfEmpty("tax_period", $"{t:yyyy-MM}");
        var code = await m.GetAsync("TaxCode", d.Str("tax_code"), ct);
        if (code is not null && !code.Data.Bool("is_withholding")) throw Errors.One("tax_code", $"{code.Name} bukan kode PPh potong/pungut");
    }
}

/// <summary>Rekening koran: cocokkan otomatis baris ke Payment Entry (jumlah sama, arah sama, tanggal ±3 hari, utamakan nomor referensi).</summary>
public sealed class BankStatementHook(IDocumentStore store) : IBeforeValidate<BankStatement>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var bank = d.Str("bank_account");
        if (bank is null) return;
        var payments = (await m.QueryAsync("PaymentEntry", [new FilterClause("bank_account", "=", bank)], ct, take: 5000))
            .Concat(await m.QueryAsync("PaymentEntry", [new FilterClause("to_bank_account", "=", bank)], ct, take: 5000))
            .Where(p => p.DocStatus == DocStatus.Submitted).DistinctBy(p => p.Name).ToList();
        var used = (await m.QueryAsync("BankStatement", [new FilterClause("bank_account", "=", bank)], ct, take: 1000))
            .Where(s => s.Name != c.Doc.Name && s.DocStatus == DocStatus.Submitted)
            .SelectMany(s => s.Data.Rows("lines")).Select(l => l.Str("matched_voucher")).OfType<string>().ToHashSet();
        var lines = d.Rows("lines");
        foreach (var l in lines) if (l.Str("matched_voucher") is { } v) used.Add(v);

        foreach (var l in lines.Where(l => l.Str("matched_voucher") is null && l.Str("match_status") != "Ignored"))
        {
            var date = l.Date("transaction_date");
            if (date is null) continue;
            var incoming = l.Dec("deposit") > 0;
            var amount = incoming ? l.Dec("deposit") : l.Dec("withdrawal");
            var text = $"{l.Str("description")} {l.Str("bank_reference")}";
            var match = payments
                .Where(p => !used.Contains(p.Name) && p.Data.Dec("paid_amount") == amount
                            && p.Data.Date("posting_date") is { } pd && Math.Abs(pd.DayNumber - date.Value.DayNumber) <= 3
                            && IsIncoming(p, bank) == incoming)
                .OrderByDescending(p => p.Data.Str("reference_no") is { } r && text.Contains(r, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => Math.Abs(p.Data.Date("posting_date")!.Value.DayNumber - date.Value.DayNumber))
                .FirstOrDefault();
            if (match is null) continue;
            l["matched_voucher_type"] = "PaymentEntry"; l["matched_voucher"] = match.Name; l["match_status"] = "Matched";
            used.Add(match.Name);
        }
    }

    internal static bool IsIncoming(Document p, string bank) => p.Data.Str("payment_type") switch
    {
        "Receive" => true,
        "Internal Transfer" => p.Data.Str("to_bank_account") == bank,
        _ => false,
    };
}

/// <summary>
/// Rekonsiliasi bank: saldo buku dari ledger (GetBalancesAsync) per tanggal; daftar voucher bank (Payment Entry & Jurnal)
/// yang belum kliring di rekonsiliasi sebelumnya; ditandai kliring bila sudah dicocokkan di rekening koran s.d. tanggal itu.
/// Submit hanya bila selisih = 0. Rekonsiliasi valas belum didukung (ledger dalam mata uang fungsional).
/// </summary>
public sealed class BankReconciliationHook(ILedgerReader ledger, IDocumentStore store)
    : IBeforeValidate<BankReconciliation>, IAfterSave<BankReconciliation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var bank = await m.RequireAsync("BankAccount", d.Str("bank_account"), "bank_account", "Bank Account", ct);
        var company = await CompanyInfo.LoadAsync(m, bank.Data.Str("company"), ct);
        if (bank.Data.Str("currency") != company.BaseCurrency) throw Errors.One("bank_account", "Reconciling foreign-currency bank accounts is not supported yet");
        var gl = bank.Data.Str("gl_account") ?? throw Errors.One("bank_account", "The bank account is not linked to a GL account");
        var end = d.Date("period_end") ?? throw Errors.One("period_end", "The date is required");

        d["book_balance"] = (await ledger.GetBalancesAsync(c.Doc.TenantId, null, end, ct)).Where(b => b.Account == gl).Sum(b => b.Net);
        if (await m.GetAsync("BankStatement", d.Str("bank_statement"), ct) is { } st && d.Dec("statement_balance") == 0)
            d["statement_balance"] = st.Data.Dec("closing_balance");
        if (d.Rows("items").Count == 0) d["items"] = await ItemsAsync(m, c.Doc.Name, bank.Name, gl, end, ct);
    }

    private static async Task<List<Dictionary<string, object?>>> ItemsAsync(Masters m, string self, string bank, string gl, DateOnly end, CancellationToken ct)
    {
        var clearedBefore = (await m.QueryAsync("BankReconciliation", [new FilterClause("bank_account", "=", bank)], ct, take: 1000))
            .Where(r => r.Name != self && r.DocStatus == DocStatus.Submitted)
            .SelectMany(r => r.Data.Rows("items")).Where(i => i.Bool("is_cleared")).Select(i => i.Str("voucher_no")).OfType<string>().ToHashSet();
        var statementMatch = new Dictionary<string, string>();
        foreach (var s in (await m.QueryAsync("BankStatement", [new FilterClause("bank_account", "=", bank)], ct, take: 1000))
                 .Where(s => s.DocStatus == DocStatus.Submitted && s.Data.Date("to_date") <= end))
            foreach (var l in s.Data.Rows("lines"))
                if (l.Str("matched_voucher") is { } v) statementMatch[v] = l.Str("transaction_date") ?? "";

        var items = new List<Dictionary<string, object?>>();
        void Add(string type, string name, string? date, decimal amount)
        {
            if (amount == 0 || clearedBefore.Contains(name)) return;
            var cleared = statementMatch.TryGetValue(name, out var on);
            items.Add(new()
            {
                ["voucher_type"] = type, ["voucher_no"] = name, ["posting_date"] = date, ["amount"] = amount,
                ["is_cleared"] = cleared, ["clearance_date"] = cleared ? on : null,
            });
        }

        var payments = (await m.QueryAsync("PaymentEntry", [new FilterClause("bank_account", "=", bank)], ct, take: 10000))
            .Concat(await m.QueryAsync("PaymentEntry", [new FilterClause("to_bank_account", "=", bank)], ct, take: 10000))
            .Where(p => p.DocStatus == DocStatus.Submitted && p.Data.Date("posting_date") <= end).DistinctBy(p => p.Name);
        foreach (var p in payments)
        {
            var amt = D.R2(p.Data.Dec("paid_amount") * (p.Data.Dec("exchange_rate") is > 0m and var r ? r : 1m));
            Add("PaymentEntry", p.Name, p.Data.Str("posting_date"), BankStatementHook.IsIncoming(p, bank) ? amt : -amt);
        }
        foreach (var j in (await m.QueryAsync("JournalEntry", [new FilterClause("posting_date", "<=", D.Iso(end))], ct, take: 10000))
                 .Where(j => j.DocStatus == DocStatus.Submitted))
            Add("JournalEntry", j.Name, j.Data.Str("posting_date"),
                j.Data.Rows("accounts").Where(r => r.Str("account") == gl).Sum(r => r.Dec("base_debit") - r.Dec("base_credit")));
        return items.OrderBy(i => (string?)i["posting_date"]).ToList();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft && c.Doc.Data.Dec("difference") != 0)
            throw Errors.One("difference", $"There is still a difference of {c.Doc.Data.Dec("difference"):N2}; the reconciliation cannot be submitted yet");
        return Task.CompletedTask;
    }
}
