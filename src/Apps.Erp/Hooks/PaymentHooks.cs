using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

/// <summary>
/// Payment Entry (Terima / Bayar / Transfer Internal).
/// Simpan: validasi rekening & pihak, isi sisa tagihan tiap referensi dari buku besar, akun pihak default.
/// Submit: Terima = Dr bank + Dr potongan / Cr piutang per referensi (against = referensi) + Cr uang muka (against = pembayaran ini).
///         Bayar  = kebalikannya. Transfer = Dr rekening tujuan / Cr rekening sumber.
///         Valas: referensi dilunasi pada kurs aslinya, selisih ke akun laba/rugi selisih kurs perusahaan.
/// Batas: nota kredit/debit melunasi faktur asal, bukan dibayar langsung. Uang muka pelanggan/pemasok dialokasikan ke faktur berikutnya lewat Payment Reconciliation.
/// </summary>
public sealed class PaymentEntryHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PaymentEntry>, IAfterSave<PaymentEntry>, IOnCancel<PaymentEntry>
{
    private const string Receive = "Receive", Pay = "Pay", Transfer = "Internal Transfer";

    private static readonly Dictionary<string, string> PartyFieldOf = new()
    {
        ["SalesInvoice"] = "customer", ["PurchaseInvoice"] = "vendor",
        ["ExpenseClaim"] = "employee", ["EmployeeAdvance"] = "employee",
    };

    private sealed record Allocation(int Index, string DocType, string Name, string Account, decimal Allocated, decimal ClearBase);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    /// <summary>Referensi yang valid beserta nilai pelunasan dalam mata uang fungsional. Dipakai saat simpan dan saat submit.</summary>
    private async Task<List<Allocation>> ResolveAsync(HookContext c, Masters m, string type, string partyType, string party, string currency,
        Errors errors, CancellationToken ct)
    {
        // Terima: piutang pelanggan, saldo awal, pengembalian sisa uang muka karyawan. Bayar: utang pemasok, saldo awal, klaim & pencairan uang muka, gaji (Payroll Run).
        var allowed = type == Receive
            ? new[] { "SalesInvoice", "JournalEntry", "EmployeeAdvance" }
            : new[] { "PurchaseInvoice", "JournalEntry", "ExpenseClaim", "EmployeeAdvance", "PayrollRun", "CommissionStatement" };
        var result = new List<Allocation>();
        var rows = c.Doc.Data.Rows("references");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"references[{i}]";
            var refType = r.Str("reference_doctype"); var refName = r.Str("reference_name");
            if (refType is null || refName is null) continue; // wajib diisi dicek metadata
            if (!allowed.Contains(refType))
            {
                errors.Add($"{p}.reference_doctype", refType is "SalesCreditNote" or "PurchaseDebitNote"
                    ? $"{refType} is not paid directly; the note reduces its original invoice"
                    : $"{refType} cannot be allocated on a {type} payment");
                continue;
            }
            if (result.Any(a => a.DocType == refType && a.Name == refName)) { errors.Add($"{p}.reference_name", $"{refName} is allocated more than once"); continue; }

            var doc = await m.GetAsync(refType, refName, ct);
            if (doc is null) { errors.Add($"{p}.reference_name", $"{refName} not found"); continue; }
            if (doc.DocStatus != DocStatus.Submitted || (doc.WorkflowState is not null && doc.WorkflowState != Approval.Approved))
            { errors.Add($"{p}.reference_name", $"{refName} is not submitted/approved or has been cancelled"); continue; }
            if (type == Pay && doc.Data.Bool("on_hold")) { errors.Add($"{p}.reference_name", $"Payments for {refName} are on hold"); continue; }
            if (PartyFieldOf.TryGetValue(refType, out var pf) && doc.Data.Str(pf) != party)
            { errors.Add($"{p}.reference_name", $"{refName} does not belong to {party}"); continue; }
            if ((doc.Data.Str("currency") ?? currency) != currency)
            { errors.Add($"{p}.reference_name", $"The currency of {refName} ({doc.Data.Str("currency")}) differs from the payment ({currency})"); continue; }

            // Terima melunasi saldo debit (piutang/uang muka), Bayar melunasi saldo kredit (utang).
            var open = await OpenItems.GetForDirectionAsync(ledger, c.Doc.TenantId, refType, refName, partyType, party, type == Receive, ct);
            if (open is null || open.NetBase == 0 || (type == Receive ? open.NetBase < 0 : open.NetBase > 0))
            { errors.Add($"{p}.reference_name", $"{refName} has no outstanding amount to {(type == Receive ? "receive" : "pay")} for {party}"); continue; }

            var refRate = doc.Data.Dec("exchange_rate") is > 0m and var x ? x : 1m;
            var openBase = Math.Abs(open.NetBase);
            var outstanding = D.R2(openBase / refRate);
            r["outstanding_amount"] = outstanding;
            r["due_date"] = doc.Data.Str("due_date") ?? doc.Data.Str("posting_date");

            var allocated = r.Dec("allocated_amount");
            if (allocated <= 0) { errors.Add($"{p}.allocated_amount", "The allocated amount must be greater than 0"); continue; }
            if (allocated > outstanding) { errors.Add($"{p}.allocated_amount", $"Exceeds the outstanding amount of {refName} ({outstanding:N2})"); continue; }
            // Pelunasan penuh memakai saldo dasar persis agar open item tertutup tanpa sisa pembulatan.
            var clearBase = allocated == outstanding ? openBase : D.R2(allocated * refRate);
            result.Add(new(i, refType, refName, open.Account, allocated, clearBase));
        }
        return result;
    }

    private static async Task<string?> DefaultPartyAccountAsync(Masters m, CompanyInfo company, string partyType, Document party, CancellationToken ct) =>
        partyType switch
        {
            "Customer" => party.Data.Str("receivable_account")
                ?? await m.StrAsync("CustomerGroup", party.Data.Str("customer_group"), "receivable_account", ct)
                ?? company.Default("default_receivable_account"),
            "Vendor" => party.Data.Str("payable_account")
                ?? await m.StrAsync("VendorGroup", party.Data.Str("vendor_group"), "payable_account", ct)
                ?? company.Default("default_payable_account"),
            _ => company.Default("default_payable_account"),
        };

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var date = d.Date("posting_date") ?? Today;
        var type = d.Str("payment_type") ?? Receive;

        var bank = await m.RequireAsync("BankAccount", d.Str("bank_account"), "bank_account", "Bank Account", ct);
        d.SetIfEmpty("currency", bank.Data.Str("currency"));
        var currency = d.Str("currency") ?? company.BaseCurrency;
        if (bank.Data.Str("company") != company.Name) errors.Add("bank_account", $"Bank account {bank.Name} belongs to another company");
        if (bank.Data.TryGetValue("is_active", out var active) && active is false) errors.Add("bank_account", $"Bank account {bank.Name} is inactive");
        if (bank.Data.Str("currency") != currency) errors.Add("currency", $"The payment currency must match the bank account ({bank.Data.Str("currency")})");
        await Rates.ApplyAsync(m, d, company, date, ct);

        if (type == Transfer)
        {
            var to = await m.RequireAsync("BankAccount", d.Str("to_bank_account"), "to_bank_account", "Target bank account", ct);
            if (to.Data.Str("company") != company.Name) errors.Add("to_bank_account", "Intercompany transfers are not supported yet");
            if (to.Data.Str("currency") != currency) errors.Add("to_bank_account", "Cross-currency transfers are not supported yet");
            if (d.Rows("references").Count > 0 || d.Rows("deductions").Count > 0)
                errors.Add("references", "Internal transfers do not use allocations or deductions");
            d["party_type"] = null; d["party"] = null; d["party_account"] = null;
            errors.ThrowIfAny();
            return;
        }

        var partyType = d.Str("party_type"); var partyName = d.Str("party");
        if (partyType is null || partyName is null) { errors.ThrowIfAny(); return; } // wajib diisi dicek metadata
        var party = await m.RequireAsync(partyType, partyName, "party", "Party", ct);
        if (type == Pay && partyType == "Vendor" && party.Data.Bool("on_hold"))
            errors.Add("party", $"Payments to {partyName} are on hold");

        var allocations = await ResolveAsync(c, m, type, partyType, partyName, currency, errors, ct);

        var deductions = d.Rows("deductions");
        for (var i = 0; i < deductions.Count; i++)
        {
            if (deductions[i].Str("account") is not { } acc) continue;
            try { await Accounts.RequirePostableAsync(m, c.User, acc, $"deductions[{i}].account", ct); }
            catch (ValidationFailedException ex) { foreach (var e in ex.Errors) errors.Add(e.Path, e.Message); }
        }

        if (d.Str("party_account") is null)
            d["party_account"] = allocations.FirstOrDefault()?.Account ?? await DefaultPartyAccountAsync(m, company, partyType, party, ct);
        if (d.Str("party_account") is null) errors.Add("party_account", "The party's receivable/payable account is not set");
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var type = d.Str("payment_type") ?? Receive;
        var partyType = d.Str("party_type"); var party = d.Str("party");
        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        var lockField = (type, partyType) switch
        {
            (Receive, "Customer") => Periods.LockReceivable,
            (Pay, "Vendor") => Periods.LockPayable,
            _ => null,
        };
        await Periods.CheckAsync(m, c.User, date, lockField, "posting_date", ct);
        await Periods.CheckFutureAsync(m, date, Today, "posting_date", ct);

        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var currency = d.Str("currency") ?? company.BaseCurrency;
        var rate = d.Dec("exchange_rate") is > 0m and var x ? x : 1m;
        decimal B(decimal v) => D.R2(v * rate);
        var bankGl = await m.StrAsync("BankAccount", d.Str("bank_account"), "gl_account", ct)
            ?? throw Errors.One("bank_account", "The bank account is not linked to a GL account");
        var paid = d.Dec("paid_amount");
        var posting = new Posting(date, d.Str("remarks") ?? $"Payment {c.Doc.Name}");

        if (type == Transfer)
        {
            var toGl = await m.StrAsync("BankAccount", d.Str("to_bank_account"), "gl_account", ct)
                ?? throw Errors.One("to_bank_account", "The target bank account is not linked to a GL account");
            posting.Dr(toGl, B(paid));
            posting.Cr(bankGl, B(paid));
            c.Ledger.AddRange(posting.Build());
            return;
        }
        if (partyType is null || party is null) throw Errors.One("party", "The party is required");

        // Saldo bisa berubah antara simpan dan submit: hitung ulang dari buku besar. Store tetap menjaga agar open item tidak berbalik tanda.
        var errors = new Errors();
        var allocations = await ResolveAsync(c, m, type, partyType, party, currency, errors, ct);
        errors.ThrowIfAny();

        var receive = type == Receive;
        void PartyLine(string acc, decimal amount, string againstType, string againstName)
        {
            if (receive) posting.Cr(acc, amount, partyType, party, againstType, againstName);
            else posting.Dr(acc, amount, partyType, party, againstType, againstName);
        }

        if (receive) posting.Dr(bankGl, B(paid)); else posting.Cr(bankGl, B(paid));
        foreach (var ded in d.Rows("deductions"))
        {
            var acc = ded.Str("account")!;
            if (receive) posting.Dr(acc, B(ded.Dec("amount")), costCenter: ded.Str("cost_center"), lineRemarks: ded.Str("description"));
            else posting.Cr(acc, B(ded.Dec("amount")), costCenter: ded.Str("cost_center"), lineRemarks: ded.Str("description"));
        }
        foreach (var a in allocations) PartyLine(a.Account, a.ClearBase, a.DocType, a.Name);

        var unallocated = paid + d.Rows("deductions").Sum(r => r.Dec("amount")) - allocations.Sum(a => a.Allocated);
        if (unallocated < 0) throw Errors.One("references", "Total allocated exceeds paid amount + deductions");
        if (unallocated > 0)
        {
            var partyAccount = d.Str("party_account") ?? throw Errors.One("party_account", "The party account is required for an advance");
            PartyLine(partyAccount, B(unallocated), c.Doc.DocType, c.Doc.Name); // uang muka: open item milik pembayaran ini
        }

        if (currency != company.BaseCurrency)
            posting.Balance(company.Default("exchange_gain_loss_account"), null, decimal.MaxValue, "exchange gain/loss");
        else
            posting.Balance(company.Default("round_off_account"), company.Default("round_off_cost_center"), 1m, "round-off");
        c.Ledger.AddRange(posting.Build());
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}

/// <summary>
/// Payment Reconciliation (alokasi uang muka pihak ke faktur, tanpa mutasi kas; NetSuite Apply Credit / SAP F-32, F-44).
/// Simpan: tiap baris dicek (pembayaran & faktur sudah submit, milik pihak & perusahaan yang sama, akun kontrol sama,
///         mata uang fungsional), sisa uang muka & sisa tagihan diisi dari buku besar, total per pembayaran/faktur tidak boleh lebih.
/// Submit: akun kontrol yang sama di-Dr/Cr dua kali: sisi pembayaran (against = PaymentEntry) menutup uang muka,
///         sisi faktur (against = faktur) mengurangi sisa tagihan. Pelanggan: Dr vs pembayaran, Cr vs faktur; pemasok: kebalikannya.
/// Cancel: baris pembalik standar.
/// </summary>
public sealed class PaymentReconciliationHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PaymentReconciliation>, IAfterSave<PaymentReconciliation>, IOnCancel<PaymentReconciliation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var partyType = d.Str("party_type"); var party = d.Str("party");
        if (partyType is null || party is null) return; // field wajib ditangani framework
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var isCustomer = partyType == "Customer";
        var invoiceType = isCustomer ? "SalesInvoice" : "PurchaseInvoice";
        var partyField = isCustomer ? "customer" : "vendor";
        var sign = isCustomer ? -1m : 1m; // uang muka pelanggan bersaldo kredit, uang muka ke pemasok bersaldo debit
        var errors = new Errors();
        var rows = d.Rows("allocations");
        if (rows.Count == 0) errors.Add("allocations", "Add at least one allocation");
        string? control = null;
        var perPayment = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var perInvoice = new Dictionary<string, decimal>(StringComparer.Ordinal);

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"allocations[{i}]";
            var payName = r.Str("payment_entry"); var invType = r.Str("invoice_type"); var invName = r.Str("invoice");
            if (payName is null || invType is null || invName is null) continue;
            if (invType != invoiceType) { errors.Add($"{p}.invoice_type", $"A {partyType} advance can only be allocated to a {invoiceType}"); continue; }

            var pay = await m.GetAsync("PaymentEntry", payName, ct);
            if (pay is null || pay.DocStatus != DocStatus.Submitted) { errors.Add($"{p}.payment_entry", $"{payName} is not submitted or has been cancelled"); continue; }
            if (pay.Data.Str("party_type") != partyType || pay.Data.Str("party") != party) { errors.Add($"{p}.payment_entry", $"{payName} does not belong to {party}"); continue; }
            if (pay.Data.Str("company") != company.Name) { errors.Add($"{p}.payment_entry", $"{payName} belongs to another company"); continue; }
            var inv = await m.GetAsync(invType, invName, ct);
            if (inv is null || inv.DocStatus != DocStatus.Submitted) { errors.Add($"{p}.invoice", $"{invName} is not submitted or has been cancelled"); continue; }
            if (inv.Data.Str(partyField) != party) { errors.Add($"{p}.invoice", $"{invName} does not belong to {party}"); continue; }
            if (inv.Data.Str("company") != company.Name) { errors.Add($"{p}.invoice", $"{invName} belongs to another company"); continue; }
            if ((pay.Data.Str("currency") ?? company.BaseCurrency) != company.BaseCurrency || (inv.Data.Str("currency") ?? company.BaseCurrency) != company.BaseCurrency)
            { errors.Add($"{p}.payment_entry", "Foreign-currency advances cannot be allocated here yet; use a journal with exchange gain/loss"); continue; }

            var payItem = await OpenItems.GetAsync(ledger, c.Doc.TenantId, "PaymentEntry", payName, partyType, party, ct);
            var invItem = await OpenItems.GetAsync(ledger, c.Doc.TenantId, invType, invName, partyType, party, ct);
            var advance = sign * (payItem?.NetBase ?? 0m);
            var outstanding = -sign * (invItem?.NetBase ?? 0m);
            r["payment_unallocated"] = advance;
            r["invoice_outstanding"] = outstanding;
            if (payItem is null || advance <= 0) { errors.Add($"{p}.payment_entry", $"{payName} has no unallocated advance balance"); continue; }
            if (invItem is null || outstanding <= 0) { errors.Add($"{p}.invoice", $"{invName} has no outstanding amount"); continue; }
            if (payItem.Account != invItem.Account)
            { errors.Add($"{p}.invoice", $"{payName} and {invName} use different control accounts ({payItem.Account} vs {invItem.Account})"); continue; }
            control ??= payItem.Account;
            if (payItem.Account != control) { errors.Add($"{p}.payment_entry", $"All allocations must use the same control account ({control})"); continue; }

            var amount = r.Dec("allocated_amount");
            if (amount <= 0) { errors.Add($"{p}.allocated_amount", "The allocated amount must be greater than 0"); continue; }
            perPayment[payName] = perPayment.GetValueOrDefault(payName) + amount;
            perInvoice[invName] = perInvoice.GetValueOrDefault(invName) + amount;
            if (perPayment[payName] > advance) errors.Add($"{p}.allocated_amount", $"Exceeds the advance balance of {payName} ({advance:N2})");
            if (perInvoice[invName] > outstanding) errors.Add($"{p}.allocated_amount", $"Exceeds the outstanding amount of {invName} ({outstanding:N2})");
        }
        d["receivable_payable_account"] = control;
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var partyType = d.Str("party_type")!; var party = d.Str("party")!;
        var isCustomer = partyType == "Customer";
        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        await Periods.CheckAsync(m, c.User, date, isCustomer ? Periods.LockReceivable : Periods.LockPayable, "posting_date", ct);
        var account = d.Str("receivable_payable_account") ?? throw Errors.One("receivable_payable_account", "The receivable/payable account is required");

        var posting = new Posting(date, d.Str("remarks") ?? $"Advance allocation {c.Doc.Name}");
        foreach (var r in d.Rows("allocations"))
        {
            var amount = r.Dec("allocated_amount");
            var pay = r.Str("payment_entry"); var invType = r.Str("invoice_type"); var inv = r.Str("invoice");
            if (isCustomer)
            {
                posting.Dr(account, amount, partyType, party, "PaymentEntry", pay);
                posting.Cr(account, amount, partyType, party, invType, inv);
            }
            else
            {
                posting.Cr(account, amount, partyType, party, "PaymentEntry", pay);
                posting.Dr(account, amount, partyType, party, invType, inv);
            }
        }
        c.Ledger.AddRange(posting.Build());
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}
