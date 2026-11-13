using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

internal static class Approval
{
    public const string Approved = "Approved";

    /// <summary>Transisi workflow tidak membawa ToState ke AfterSave: deteksi lewat perubahan workflow_state.</summary>
    public static bool JustApproved(HookContext c) =>
        c.Doc.WorkflowState == Approved && c.Previous?.WorkflowState != Approved && c.Doc.DocStatus == DocStatus.Submitted;

    public static async Task<Document> ActiveEmployeeAsync(Masters m, string? employee, CancellationToken ct)
    {
        var emp = await m.RequireAsync("Employee", employee, "employee", "Employee", ct);
        if (emp.Data.Str("status") == "Left") throw Errors.One("employee", $"{emp.Name} has left");
        return emp;
    }
}

/// <summary>
/// Uang muka karyawan. Disetujui: Dr akun uang muka / Cr utang ke karyawan (keduanya open item milik dokumen ini).
/// Payment Entry "Pay" melunasi utangnya; Expense Claim mempertanggungjawabkan uang muka; sisa dikembalikan lewat Payment Entry "Receive".
/// </summary>
public sealed class EmployeeAdvanceHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<EmployeeAdvance>, IAfterSave<EmployeeAdvance>, IOnCancel<EmployeeAdvance>, IOnLoad<EmployeeAdvance>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Str("advance_account") is null)
        {
            var acc = (await m.QueryAsync("Account", [new FilterClause("account_type", "=", "Advance"), new FilterClause("is_group", "=", false)], ct, take: 5))
                .FirstOrDefault(a => !(a.Data.TryGetValue("is_active", out var v) && v is false));
            d["advance_account"] = acc?.Name;
        }
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (!Approval.JustApproved(c)) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var company = await CompanyInfo.LoadAsync(m, emp.Data.Str("company"), ct);
        var date = d.Date("posting_date") ?? DateOnly.FromDateTime(clock.GetLocalNow().Date);
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var advance = d.Str("advance_account") ?? throw Errors.One("advance_account", "The advance account is not set");
        var payable = company.Default("default_payable_account") ?? throw Errors.One("company", "The company's default payable account is not set");
        await Accounts.RequirePostableAsync(m, c.User, advance, "advance_account", ct);

        var posting = new Posting(date, $"Advance {emp.Name}: {d.Str("purpose")}");
        posting.Dr(advance, d.Dec("advance_amount"), "Employee", emp.Name, c.Doc.DocType, c.Doc.Name);
        posting.Cr(payable, d.Dec("advance_amount"), "Employee", emp.Name, c.Doc.DocType, c.Doc.Name);
        c.AddLedgers(posting.Build());
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw Errors.One("name", $"{c.Doc.Name} has been paid or settled; cancel those documents first");
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState != Approval.Approved) return;
        var d = c.Doc.Data;
        var rows = (await ledger.GetAgainstAsync(c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)).Where(e => e.Party == d.Str("employee")).ToList();
        var advanceAcc = d.Str("advance_account");
        var own = rows.Where(e => e.VoucherType == c.Doc.DocType && e.VoucherName == c.Doc.Name).ToList();
        var payableAcc = own.FirstOrDefault(e => e.Account != advanceAcc)?.Account;
        decimal Credits(string? acc, string voucherType) => rows.Where(e => e.Account == acc && e.VoucherType == voucherType).Sum(e => e.Credit - e.Debit);
        var paid = rows.Where(e => e.Account == payableAcc && e.VoucherType == "PaymentEntry").Sum(e => e.Debit - e.Credit);
        var settled = Credits(advanceAcc, "ExpenseClaim");
        var returned = Credits(advanceAcc, "PaymentEntry");
        d["paid_amount"] = paid; d["settled_amount"] = settled; d["returned_amount"] = returned;
        d["advance_status"] = paid == 0 ? "Unpaid"
            : settled + returned >= d.Dec("advance_amount") ? "Completed"
            : settled > 0 ? "Partly Settled" : "Paid";
    }
}

/// <summary>
/// Klaim biaya. Simpan: akun & cost center baris dari jenis biaya/karyawan, batas & bukti, uang muka yang dipotong.
/// Disetujui: Dr beban / Cr utang ke karyawan (net, open item klaim ini) / Cr uang muka (open item uang muka).
/// </summary>
public sealed class ExpenseClaimHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<ExpenseClaim>, IAfterSave<ExpenseClaim>, IOnCancel<ExpenseClaim>, IOnLoad<ExpenseClaim>
{
    private async Task<(Document? Advance, string? AdvanceAccount)> AdvanceAsync(Masters m, HookContext c, decimal settle, decimal total, Errors errors, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var adv = await m.GetAsync("EmployeeAdvance", d.Str("employee_advance"), ct);
        if (adv is null) { if (settle != 0) errors.Add("advance_settled", "Choose the advance being settled"); return (null, null); }
        if (adv.Data.Str("employee") != d.Str("employee")) errors.Add("employee_advance", $"{adv.Name} belongs to another employee");
        if (adv.WorkflowState != Approval.Approved) errors.Add("employee_advance", $"{adv.Name} is not approved");
        if (settle > total) errors.Add("advance_settled", "The advance deduction cannot exceed the total claimed; return the unused advance through a Payment Entry");
        var open = await OpenItems.GetForDirectionAsync(ledger, c.Doc.TenantId, "EmployeeAdvance", adv.Name, "Employee", adv.Data.Str("employee")!, true, ct);
        if (settle > 0 && (open is null || settle > open.NetBase)) errors.Add("advance_settled", $"Exceeds the open balance of advance {adv.Name} ({(open is null ? 0m : open.NetBase):N2})");
        return (adv, adv.Data.Str("advance_account"));
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var company = await CompanyInfo.LoadAsync(m, emp.Data.Str("company"), ct);
        d.SetIfEmpty("payable_account", company.Default("default_payable_account"));
        var rows = d.Rows("items");
        decimal total = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"items[{i}]";
            var type = await m.GetAsync("ExpenseClaimType", r.Str("expense_type"), ct);
            if (type is null) continue;
            r.SetIfEmpty("account", type.Data.Str("default_account"));
            r.SetIfEmpty("cost_center", emp.Data.Str("cost_center") ?? company.Default("default_cost_center"));
            if (type.Data.Bool("requires_receipt") && r.Str("receipt") is null) errors.Add($"{p}.receipt", $"{type.Name} requires a receipt");
            var max = type.Data.Dec("max_amount_per_item");
            if (max > 0 && r.Dec("amount") > max) errors.Add($"{p}.amount", $"Exceeds the {type.Name} limit ({max:N2})");
            if (r.Dec("amount") <= 0) errors.Add($"{p}.amount", "Amount must be greater than 0");
            total += r.Dec("amount");
        }
        await AdvanceAsync(m, c, d.Dec("advance_settled"), total, errors, ct);
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (!Approval.JustApproved(c)) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var company = await CompanyInfo.LoadAsync(m, emp.Data.Str("company"), ct);
        var date = d.Date("posting_date") ?? DateOnly.FromDateTime(clock.GetLocalNow().Date);
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var total = d.Rows("items").Sum(r => r.Dec("amount"));
        var settle = d.Dec("advance_settled");
        var errors = new Errors();
        var (adv, advAccount) = await AdvanceAsync(m, c, settle, total, errors, ct);
        errors.ThrowIfAny();
        var payable = d.Str("payable_account") ?? throw Errors.One("payable_account", "The employee payable account is not set");

        var posting = new Posting(date, $"Expense claim {emp.Name}: {d.Str("purpose")}");
        foreach (var g in d.Rows("items").GroupBy(r => (Acc: r.Str("account"), Cc: r.Str("cost_center"))))
        {
            var acc = g.Key.Acc ?? throw Errors.One("items", "The line expense account is not set");
            await Accounts.RequirePostableAsync(m, c.User, acc, "items", ct);
            posting.Dr(acc, g.Sum(r => r.Dec("amount")), costCenter: g.Key.Cc);
        }
        posting.Cr(payable, total - settle, "Employee", emp.Name, c.Doc.DocType, c.Doc.Name);
        if (settle > 0 && adv is not null)
            posting.Cr(advAccount ?? throw Errors.One("employee_advance", "The advance account is not set"), settle, "Employee", emp.Name, "EmployeeAdvance", adv.Name);
        posting.Balance(company.Default("round_off_account"), company.Default("round_off_cost_center"), 1m, "round-off");
        var lines = posting.Build();
        await BudgetControl.CheckAsync(c, m, ledger, lines, company.Name, ct);
        c.AddLedgers(lines);
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw Errors.One("name", $"{c.Doc.Name} has been paid; cancel the payment first");
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState != Approval.Approved) return;
        var d = c.Doc.Data;
        var open = await OpenItems.GetForDirectionAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, "Employee", d.Str("employee") ?? "", false, ct);
        var due = d.Dec("net_payable");
        var outstanding = open is null ? 0m : Math.Abs(open.NetBase);
        d["payment_status"] = due <= 0 || outstanding == 0 ? "Paid" : outstanding >= due ? "Unpaid" : "Partly Paid";
    }
}
