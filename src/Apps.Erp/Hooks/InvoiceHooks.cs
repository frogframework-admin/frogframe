using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

/// <summary>Perbedaan empat dokumen faktur: arah (jual/beli) dan apakah dokumen retur (nota) atas faktur asal.</summary>
internal sealed record InvoiceKind(
    string DocType, bool IsSales, bool IsReturn, string PartyField, string PartyType,
    string PartyAccountField, string LineAccountField, string ControlType, string LockField)
{
    public string OriginDocType => IsSales ? "SalesInvoice" : "PurchaseInvoice";
    public string PartyAccountOnParty => IsSales ? "receivable_account" : "payable_account";
    public string GroupDocType => IsSales ? "CustomerGroup" : "VendorGroup";
    public string GroupField => IsSales ? "customer_group" : "vendor_group";
    public string CompanyDefaultAccount => IsSales ? "default_receivable_account" : "default_payable_account";
    public string ItemAccountField => IsSales ? "income_account" : "expense_account";
    public string CompanyLineAccount => IsSales ? "default_income_account" : "default_expense_account";

    public static readonly InvoiceKind Sales = new("SalesInvoice", true, false, "customer", "Customer", "debit_to", "income_account", "Receivable", Periods.LockReceivable);
    public static readonly InvoiceKind SalesReturn = Sales with { DocType = "SalesCreditNote", IsReturn = true };
    public static readonly InvoiceKind Purchase = new("PurchaseInvoice", false, false, "vendor", "Vendor", "credit_to", "expense_account", "Payable", Periods.LockPayable);
    public static readonly InvoiceKind PurchaseReturn = Purchase with { DocType = "PurchaseDebitNote", IsReturn = true };
}

/// <summary>Total faktur, dihitung persis seperti compute metadata (lihat gen_fin.py) agar hook dan form selalu sama.</summary>
internal sealed record InvoiceTotals(decimal Total, decimal TaxBase, decimal Vat, decimal WhtBase, decimal Wht, decimal GrandTotal, decimal Payable)
{
    public static InvoiceTotals From(IDictionary<string, object?> d, decimal vatRate, decimal dppFactor, decimal whtRate)
    {
        decimal total = 0, taxable = 0, whtBase = 0;
        foreach (var r in d.Rows("items"))
        {
            var amount = D.R2(r.Dec("qty") * r.Dec("rate") * (1 - r.Dec("discount_percent") / 100m));
            r["amount"] = amount; // samakan nilai baris sebelum compute berjalan
            total += amount;
            if (r.Bool("is_vat_applicable")) taxable += amount;
            if (r.Bool("is_wht_applicable")) whtBase += amount;
        }
        var taxBase = D.R2(taxable * dppFactor);
        var vat = D.R2(taxBase * vatRate / 100m);
        var wht = D.R2(whtBase * whtRate / 100m);
        var grand = total + vat;
        return new(total, taxBase, vat, whtBase, wht, grand, grand - wht);
    }
}

/// <summary>Logika bersama SalesInvoice, SalesCreditNote, PurchaseInvoice, PurchaseDebitNote.</summary>
internal static class InvoiceCore
{
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    /// <summary>Simpan draft: isi default dari master, hitung total & jadwal jatuh tempo, validasi lintas dokumen.</summary>
    public static async Task PrepareAsync(InvoiceKind k, HookContext c, IDocumentStore store, TimeProvider clock, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var date = d.Date("posting_date") ?? Today(clock);

        var party = await m.RequireAsync(k.PartyType, d.Str(k.PartyField), k.PartyField, k.IsSales ? "Customer" : "Supplier", ct);
        if (party.Data.TryGetValue("is_active", out var active) && active is false)
            errors.Add(k.PartyField, $"{party.Name} nonaktif");

        // Nota: mengikuti faktur asal (pihak, mata uang, kurs, PPN, jatuh tempo).
        Document? origin = null;
        if (k.IsReturn)
        {
            origin = await m.RequireAsync(k.OriginDocType, d.Str("return_against"), "return_against", "Original invoice", ct);
            if (origin.DocStatus != DocStatus.Submitted) errors.Add("return_against", $"{origin.Name} is not submitted or has been cancelled");
            if (origin.Data.Str(k.PartyField) != party.Name) errors.Add("return_against", $"{origin.Name} does not belong to {party.Name}");
            if (origin.Data.Str("company") != company.Name) errors.Add("return_against", $"{origin.Name} belongs to another company");
            d["currency"] = origin.Data.Str("currency");
            d["exchange_rate"] = origin.Data.Dec("exchange_rate");
            d["due_date"] = origin.Data.Str("due_date");
            d.SetIfEmpty(k.PartyAccountField, origin.Data.Str(k.PartyAccountField));
            d.SetIfEmpty("vat_code", origin.Data.Str("vat_code"));
            if (!k.IsSales) d.SetIfEmpty("wht_code", origin.Data.Str("wht_code"));
        }
        else await Rates.ApplyAsync(m, d, company, date, ct);

        // Akun piutang/utang: dokumen → pihak → grup pihak → default perusahaan.
        var partyAccount = d.Str(k.PartyAccountField)
            ?? party.Data.Str(k.PartyAccountOnParty)
            ?? await m.StrAsync(k.GroupDocType, party.Data.Str(k.GroupField), k.PartyAccountOnParty, ct)
            ?? company.Default(k.CompanyDefaultAccount);
        if (partyAccount is null) errors.Add(k.PartyAccountField, $"The {k.ControlType.ToLowerInvariant()} account is not set on the party, group, or company");
        else
        {
            d[k.PartyAccountField] = partyAccount;
            var acc = await m.GetAsync("Account", partyAccount, ct);
            if (acc?.Data.Str("account_type") != k.ControlType)
                errors.Add(k.PartyAccountField, $"Account {partyAccount} is not a {k.ControlType} account");
        }

        // PPN default: penjualan bila perusahaan PKP; pembelian bila pemasok PKP.
        if (d.Str("vat_code") is null && !k.IsReturn)
        {
            var fs = await m.SingleAsync("FinanceSettings", ct);
            if (k.IsSales && company.Data.Bool("is_pkp")) d["vat_code"] = fs.Str("default_output_vat");
            if (!k.IsSales && party.Data.Bool("is_pkp")) d["vat_code"] = fs.Str("default_input_vat");
        }
        if (!k.IsSales && !k.IsReturn && d.Str("payment_term") is null)
            d["payment_term"] = party.Data.Str("payment_term") ?? await m.StrAsync("VendorGroup", party.Data.Str("vendor_group"), "default_payment_term", ct);
        if (k.IsSales && !k.IsReturn && d.Str("payment_term") is null)
            d["payment_term"] = party.Data.Str("payment_term") ?? await m.StrAsync("CustomerGroup", party.Data.Str("customer_group"), "default_payment_term", ct);

        // Baris: data item, akun, cost center, proyek (fetchFrom tidak berjalan di child table).
        var rows = d.Rows("items");
        if (rows.Count == 0) errors.Add("items", "At least one item line is required");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"items[{i}]";
            var item = await m.GetAsync("Item", r.Str("item"), ct);
            if (item is not null)
            {
                r.SetIfEmpty("item_name", item.Data.Str("item_name"));
                r.SetIfEmpty("uom", item.Data.Str("uom"));
                if (r.Str(k.LineAccountField) is null)
                    r[k.LineAccountField] = item.Data.Str(k.ItemAccountField)
                        ?? await m.StrAsync("ItemGroup", item.Data.Str("item_group"), k.ItemAccountField, ct);
            }
            if (!k.IsSales && r.Str("asset_category") is { } assetCategory)
            {
                // Kapitalisasi: baris aset dibukukan ke akun aset tetap kategori; Asset draft dibuat saat faktur di-submit.
                var fa = await m.StrAsync("AssetCategory", assetCategory, "fixed_asset_account", ct);
                if (fa is null) errors.Add($"{p}.asset_category", $"The fixed asset account of category {assetCategory} is not set");
                else r[k.LineAccountField] = fa;
            }
            r.SetIfEmpty(k.LineAccountField, company.Default(k.CompanyLineAccount));
            r.SetIfEmpty("cost_center", d.Str("cost_center") ?? company.Default("default_cost_center"));
            r.SetIfEmpty("project", d.Str("project"));
            if (r.Dec("qty") <= 0) errors.Add($"{p}.qty", "Quantity must be greater than 0 (returns use a credit/debit note)");
            if (r.Str(k.LineAccountField) is null) errors.Add($"{p}.{k.LineAccountField}", "The line account is not set on the item, item group, or company");
        }

        var (vatRate, dpp, whtRate) = await TaxRatesAsync(m, d, ct);
        var totals = InvoiceTotals.From(d, vatRate, dpp, whtRate);
        if (totals.GrandTotal <= 0) errors.Add("items", "The total must be greater than 0");
        if (!k.IsSales && totals.WhtBase > 0 && d.Str("wht_code") is null) errors.Add("wht_code", "Some lines are subject to withholding tax: choose a withholding tax code");

        if (!k.IsReturn) await BuildScheduleAsync(m, d, date, k.IsSales ? totals.GrandTotal : totals.Payable, ct);

        if (k.DocType == "PurchaseInvoice" && d.Str("bill_no") is { } billNo)
        {
            var fs = await m.SingleAsync("FinanceSettings", ct);
            if (!fs.ContainsKey("unique_bill_no_per_vendor") || fs.Bool("unique_bill_no_per_vendor"))
            {
                var dup = (await m.QueryAsync(k.DocType, [new FilterClause("vendor", "=", party.Name), new FilterClause("bill_no", "=", billNo)], ct, take: 5))
                    .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled);
                if (dup is not null) errors.Add("bill_no", $"Supplier invoice {billNo} is already recorded in {dup.Name}");
            }
        }
        errors.ThrowIfAny();
    }

    public static async Task<(decimal VatRate, decimal DppFactor, decimal WhtRate)> TaxRatesAsync(Masters m, IDictionary<string, object?> d, CancellationToken ct)
    {
        var vat = await m.GetAsync("TaxCode", d.Str("vat_code"), ct);
        var wht = await m.GetAsync("TaxCode", d.Str("wht_code"), ct);
        var dpp = vat is null ? 1m : (vat.Data.TryGetValue("dpp_factor", out var f) && f is not null ? D.ToDec(f) : 1m);
        return (vat?.Data.Dec("rate") ?? 0m, dpp, wht?.Data.Dec("rate") ?? 0m);
    }

    /// <summary>Jadwal jatuh tempo dari termin; baris terakhir menampung sisa pembulatan; due_date header = jatuh tempo terakhir.</summary>
    private static async Task BuildScheduleAsync(Masters m, IDictionary<string, object?> d, DateOnly date, decimal amount, CancellationToken ct)
    {
        var term = await m.GetAsync("PaymentTerm", d.Str("payment_term"), ct);
        if (term is null) { d.SetIfEmpty("due_date", D.Iso(date)); return; }
        var schedule = new List<Dictionary<string, object?>>();
        var steps = term.Data.Rows("schedule");
        decimal allocated = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var days = (int)s.Dec("credit_days");
            var basis = s.Str("due_date_basis") == "Days after invoice month end"
                ? new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)) : date;
            var portion = s.Dec("invoice_portion");
            var part = i == steps.Count - 1 ? amount - allocated : D.R2(amount * portion / 100m);
            allocated += part;
            schedule.Add(new()
            {
                ["due_date"] = D.Iso(basis.AddDays(days)), ["description"] = s.Str("description"),
                ["invoice_portion"] = portion, ["payment_amount"] = part,
            });
        }
        d["payment_schedule"] = schedule;
        if (schedule.Count > 0) d["due_date"] = schedule.Max(x => (string)x["due_date"]!);
    }

    /// <summary>Submit: posting ke buku besar. Nota = kebalikan faktur dan melunasi faktur asal (against = faktur asal).</summary>
    public static async Task PostAsync(InvoiceKind k, HookContext c, IDocumentStore store, TimeProvider clock, ILedgerReader ledger, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        await Periods.CheckAsync(m, c.User, date, k.LockField, "posting_date", ct);
        await Periods.CheckFutureAsync(m, date, Today(clock), "posting_date", ct);
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var rate = d.Dec("exchange_rate") is > 0m and var x ? x : 1m;
        decimal B(decimal v) => D.R2(v * rate);

        var (vatRate, dpp, whtRate) = await TaxRatesAsync(m, d, ct);
        var t = InvoiceTotals.From(d, vatRate, dpp, whtRate);
        var party = d.Str(k.PartyField)!;
        var partyAccount = d.Str(k.PartyAccountField) ?? throw Errors.One(k.PartyAccountField, "The receivable/payable account is required");
        var against = k.IsReturn ? (k.OriginDocType, d.Str("return_against")!) : (c.Doc.DocType, c.Doc.Name);
        var vatAccount = await m.StrAsync("TaxCode", d.Str("vat_code"), "account", ct);
        var whtAccount = await m.StrAsync("TaxCode", d.Str("wht_code"), "account", ct);
        if (t.Vat != 0 && vatAccount is null) throw Errors.One("vat_code", "The VAT account is not set on the tax code");
        if (t.Wht != 0 && whtAccount is null) throw Errors.One("wht_code", "The withholding tax account is not set on the tax code");

        // Arah: faktur penjualan = Dr piutang / Cr pendapatan; pembelian = Dr beban / Cr utang. Nota membalik arah.
        var sign = (k.IsSales ? 1 : -1) * (k.IsReturn ? -1 : 1); // +1 = pihak di debit
        var posting = new Posting(date, $"{c.Meta.Def.Title} {c.Doc.Name}");
        void PartySide(string acc, decimal amt) { if (sign > 0) posting.Dr(acc, amt, k.PartyType, party, against.Item1, against.Item2); else posting.Cr(acc, amt, k.PartyType, party, against.Item1, against.Item2); }
        void OtherSide(string acc, decimal amt, string? cc = null) { if (sign > 0) posting.Cr(acc, amt, costCenter: cc); else posting.Dr(acc, amt, costCenter: cc); }
        void SameSideAsParty(string acc, decimal amt) { if (sign > 0) posting.Dr(acc, amt); else posting.Cr(acc, amt); }

        PartySide(partyAccount, B(k.IsSales ? t.GrandTotal : t.Payable));
        foreach (var g in d.Rows("items").GroupBy(r => (Acc: r.Str(k.LineAccountField)!, Cc: r.Str("cost_center"))))
        {
            await Accounts.RequirePostableAsync(m, c.User, g.Key.Acc, "items", ct);
            OtherSide(g.Key.Acc, B(g.Sum(r => r.Dec("amount"))), g.Key.Cc);
        }
        if (t.Vat != 0) OtherSide(vatAccount!, B(t.Vat));
        if (t.Wht != 0) SameSideAsParty(whtAccount!, B(t.Wht)); // pembelian: utang PPh di kredit bersama utang usaha

        posting.Balance(company.Default("round_off_account"), company.Default("round_off_cost_center"), 1m, "round-off");
        var lines = posting.Build();
        await BudgetControl.CheckAsync(c, m, ledger, lines, company.Name, ct);
        c.AddLedgers(lines);
    }

    public static async Task CheckCreditLimitAsync(HookContext c, IDocumentStore store, ILedgerReader ledger, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var customer = await m.GetAsync("Customer", d.Str("customer"), ct);
        var limit = customer?.Data.Dec("credit_limit") ?? 0m;
        if (customer is null || limit <= 0) return;

        decimal open = 0;
        var invoices = await m.QueryAsync("SalesInvoice", [new FilterClause("customer", "=", customer.Name)], ct, take: 500);
        foreach (var inv in invoices.Where(i => i.Name != c.Doc.Name && i.DocStatus == DocStatus.Submitted))
            open += (await OpenItems.GetAsync(ledger, c.Doc.TenantId, "SalesInvoice", inv.Name, "Customer", customer.Name, ct))?.NetBase ?? 0m;
        var thisInvoice = D.R2(d.Dec("grand_total") * (d.Dec("exchange_rate") is > 0m and var r ? r : 1m));
        if (open + thisInvoice <= limit) return;

        var msg = $"Receivable of {customer.Name} becomes {open + thisInvoice:N2}, exceeding the credit limit of {limit:N2}";
        if ((await m.SingleAsync("FinanceSettings", ct)).Str("credit_limit_action") == "Block" && !c.User.HasRole("Finance"))
            throw Errors.One("customer", msg);
        c.Messages.Add(msg);
    }

    /// <summary>Dibuka (OnLoad): sisa tagihan & status bayar dari buku besar, dalam mata uang dokumen. Tidak disimpan.</summary>
    public static async Task FillOutstandingAsync(InvoiceKind k, HookContext c, ILedgerReader ledger, CancellationToken ct)
    {
        if (k.IsReturn || c.Doc.DocStatus != DocStatus.Submitted) return;
        var d = c.Doc.Data;
        var item = await OpenItems.GetAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, k.PartyType, d.Str(k.PartyField), ct);
        if (item is null) return;
        var rate = d.Dec("exchange_rate") is > 0m and var x ? x : 1m;
        var outstanding = D.R2(Math.Abs(item.NetBase) / rate);
        var full = k.IsSales ? d.Dec("grand_total") : d.Dec("payable_amount");
        d["outstanding_amount"] = outstanding;
        d["payment_status"] = outstanding == 0 ? "Paid" : outstanding >= full ? "Unpaid" : "Partly Paid";
    }
}

// ---------------------------------------------------------------------------------------------
// Hook per DocType
// ---------------------------------------------------------------------------------------------

public sealed class SalesInvoiceHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<SalesInvoice>, IAfterSave<SalesInvoice>, IOnCancel<SalesInvoice>, IOnLoad<SalesInvoice>
{
    private static readonly InvoiceKind K = InvoiceKind.Sales;
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => InvoiceCore.PrepareAsync(K, c, store, clock, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        await InvoiceCore.CheckCreditLimitAsync(c, store, ledger, ct);
        await InvoiceCore.PostAsync(K, c, store, clock, ledger, ct);
        var d = c.Doc.Data;
        var fs = await new Masters(store, c.Doc.TenantId).SingleAsync("FinanceSettings", ct);
        if (d.Dec("vat_amount") > 0 && (!fs.ContainsKey("auto_create_tax_invoice") || fs.Bool("auto_create_tax_invoice")))
            ErpEvents.Emit(c, ErpEvents.TaxInvoiceCreate, new());
        if (d.Str("quotation") is { } q) ErpEvents.Emit(c, ErpEvents.QuotationBilled, new() { ["quotation"] = q });
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw Errors.One("name", $"{c.Doc.Name} has payments or credit notes. Cancel those documents first.");
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
    }

    public Task OnLoadAsync(HookContext c, CancellationToken ct) => InvoiceCore.FillOutstandingAsync(K, c, ledger, ct);
}

public sealed class SalesCreditNoteHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<SalesCreditNote>, IAfterSave<SalesCreditNote>, IOnCancel<SalesCreditNote>
{
    private static readonly InvoiceKind K = InvoiceKind.SalesReturn;
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => InvoiceCore.PrepareAsync(K, c, store, clock, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        await InvoiceCore.PostAsync(K, c, store, clock, ledger, ct);
        // Retur PPN: nota kredit ber-PPN juga dibuatkan draft Faktur Pajak (retur) yang menunjuk faktur pajak asal.
        var fs = await new Masters(store, c.Doc.TenantId).SingleAsync("FinanceSettings", ct);
        if (c.Doc.Data.Dec("vat_amount") > 0 && (!fs.ContainsKey("auto_create_tax_invoice") || fs.Bool("auto_create_tax_invoice")))
            ErpEvents.Emit(c, ErpEvents.TaxInvoiceCreate, new());
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}

public sealed class PurchaseInvoiceHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PurchaseInvoice>, IAfterSave<PurchaseInvoice>, IOnCancel<PurchaseInvoice>, IOnLoad<PurchaseInvoice>
{
    private static readonly InvoiceKind K = InvoiceKind.Purchase;
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => InvoiceCore.PrepareAsync(K, c, store, clock, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        await InvoiceCore.PostAsync(K, c, store, clock, ledger, ct);
        var d = c.Doc.Data;
        if (d.Dec("wht_amount") > 0 || d.Rows("items").Any(r => r.Str("asset_category") is not null))
            ErpEvents.Emit(c, ErpEvents.PurchaseFollowUps, new());
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw Errors.One("name", $"{c.Doc.Name} has payments or debit notes. Cancel those documents first.");
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
    }

    public Task OnLoadAsync(HookContext c, CancellationToken ct) => InvoiceCore.FillOutstandingAsync(K, c, ledger, ct);
}

public sealed class PurchaseDebitNoteHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PurchaseDebitNote>, IAfterSave<PurchaseDebitNote>, IOnCancel<PurchaseDebitNote>
{
    private static readonly InvoiceKind K = InvoiceKind.PurchaseReturn;
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => InvoiceCore.PrepareAsync(K, c, store, clock, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        await InvoiceCore.PostAsync(K, c, store, clock, ledger, ct);
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}
