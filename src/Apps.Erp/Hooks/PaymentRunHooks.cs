using Framework.Accounting;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class PaymentRun { }

internal static class PayRun
{
    public static readonly Dictionary<string, string> PartyField = new()
    {
        ["PurchaseInvoice"] = "vendor", ["ExpenseClaim"] = "employee", ["EmployeeAdvance"] = "employee",
    };

    /// <summary>Sisa utang (sisi kredit) dokumen untuk satu pihak, positif. Uang muka karyawan punya dua open item; yang diambil sisi pencairan.</summary>
    public static async Task<decimal> OpenPayableAsync(ILedgerReader ledger, string tenant, string docType, string name, string partyType, string party, CancellationToken ct)
    {
        var open = await OpenItems.GetForDirectionAsync(ledger, tenant, docType, name, partyType, party, false, ct);
        return open is null || open.NetBase >= 0 ? 0m : -open.NetBase;
    }

    /// <summary>Rekening tujuan default pihak (child bank_accounts: is_default, atau baris pertama).</summary>
    public static async Task<(string? Bank, string? No, string? Holder)> BeneficiaryAsync(Masters m, string partyType, string party, CancellationToken ct)
    {
        var doc = await m.GetAsync(partyType, party, ct);
        var rows = doc?.Data.Rows("bank_accounts") ?? [];
        var acc = rows.FirstOrDefault(r => r.Bool("is_default")) ?? rows.FirstOrDefault();
        if (acc is null) return (null, null, null);
        var bankName = await m.StrAsync("Bank", acc.Str("bank"), "bank_name", ct) ?? acc.Str("bank");
        return (bankName, acc.Str("account_no"), acc.Str("account_holder"));
    }
}

/// <summary>
/// Payment Run (SAP F110 / NetSuite Pay Bills). Simpan: tiap baris dicek (dokumen submit/disetujui, milik pihak, tidak ditahan, mata uang
/// rekening, jumlah ≤ sisa utang dari buku besar) dan rekening tujuan diisi dari data pihak. Submit: event membuat satu Payment Entry per
/// pihak (tipe Pay, referensi = baris run) lalu men-submit-nya; buku besar ditulis oleh Payment Entry, bukan oleh run.
/// </summary>
public sealed class PaymentRunHook(ILedgerReader ledger, IDocumentStore store)
    : IBeforeValidate<PaymentRun>, IAfterSave<PaymentRun>, IOnCancel<PaymentRun>, IOnLoad<PaymentRun>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var bank = await m.RequireAsync("BankAccount", d.Str("bank_account"), "bank_account", "Bank Account", ct);
        if (bank.Data.Str("company") != company.Name) errors.Add("bank_account", $"Bank account {bank.Name} belongs to another company");
        var currency = bank.Data.Str("currency") ?? company.BaseCurrency;
        if (currency != company.BaseCurrency) errors.Add("bank_account", "Payment runs from foreign-currency bank accounts are not supported yet");

        var rows = d.Rows("items");
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var p = $"items[{i}]";
            var refType = r.Str("reference_doctype"); var refName = r.Str("reference_name");
            var partyType = r.Str("party_type"); var party = r.Str("party");
            if (refType is null || refName is null || partyType is null || party is null) continue;
            if (!seen.Add((refType, refName))) { errors.Add($"{p}.reference_name", $"{refName} is listed more than once"); continue; }
            var doc = await m.GetAsync(refType, refName, ct);
            if (doc is null || doc.DocStatus != DocStatus.Submitted || (doc.WorkflowState is not null && doc.WorkflowState != Approval.Approved))
            { errors.Add($"{p}.reference_name", $"{refName} is not submitted/approved or has been cancelled"); continue; }
            var expectedType = refType == "PurchaseInvoice" ? "Vendor" : "Employee";
            if (partyType != expectedType) { errors.Add($"{p}.party_type", $"{refType} is paid to a {expectedType}"); continue; }
            if (PayRun.PartyField.TryGetValue(refType, out var pf) && doc.Data.Str(pf) != party) { errors.Add($"{p}.party", $"{refName} does not belong to {party}"); continue; }
            if ((doc.Data.Str("company") ?? company.Name) != company.Name) { errors.Add($"{p}.reference_name", $"{refName} belongs to another company"); continue; }
            if ((doc.Data.Str("currency") ?? currency) != currency) { errors.Add($"{p}.reference_name", $"{refName} is not in {currency}"); continue; }
            if (doc.Data.Bool("on_hold") || (partyType == "Vendor" && (await m.GetAsync("Vendor", party, ct))?.Data.Bool("on_hold") == true))
            { errors.Add($"{p}.reference_name", $"Payments for {refName} are on hold"); continue; }

            var outstanding = await PayRun.OpenPayableAsync(ledger, c.Doc.TenantId, refType, refName, partyType, party, ct);
            r["outstanding_amount"] = outstanding;
            r["due_date"] = doc.Data.Str("due_date") ?? doc.Data.Str("posting_date");
            if (outstanding <= 0) { errors.Add($"{p}.reference_name", $"{refName} has nothing left to pay"); continue; }
            if (r.Dec("amount") <= 0) { errors.Add($"{p}.amount", "Amount must be greater than 0"); continue; }
            if (r.Dec("amount") > outstanding) { errors.Add($"{p}.amount", $"Exceeds the outstanding amount of {refName} ({outstanding:N2})"); continue; }
            if (r.Str("account_no") is null)
            {
                var (bankName, no, holder) = await PayRun.BeneficiaryAsync(m, partyType, party, ct);
                r["bank_name"] = bankName; r["account_no"] = no; r["account_holder"] = holder;
            }
        }
        d["party_count"] = (long)rows.Select(r => (r.Str("party_type"), r.Str("party"))).Distinct().Count();
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft && rows.Count == 0)
            errors.Add("items", "Add at least one item; use 'Get open items'");
        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft)
            ErpEvents.Emit(c, ErpEvents.PaymentRunExecute, new());
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var live = (await new Masters(store, c.Doc.TenantId).QueryAsync("PaymentEntry", [new FilterClause("payment_run", "=", c.Doc.Name)], ct, take: 5000))
            .Where(p => p.DocStatus != DocStatus.Cancelled).ToList();
        if (live.Count > 0) throw Errors.One("name", $"{live.Count} payment entries were created by this run; cancel them first ({string.Join(", ", live.Take(5).Select(p => p.Name))})");
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted) return;
        c.Doc.Data["payments_created"] = (long)(await new Masters(store, c.Doc.TenantId)
            .QueryAsync("PaymentEntry", [new FilterClause("payment_run", "=", c.Doc.Name)], ct, take: 5000)).Count(p => p.DocStatus == DocStatus.Submitted);
    }
}

/// <summary>Event: satu Payment Entry per pihak dari run yang sudah submit. Idempoten per (run, pihak); gagal sebagian dilempar ulang agar di-retry outbox.</summary>
internal static class PaymentRunExecutor
{
    public static async Task ExecuteAsync(SystemDocs sys, Masters m, string runName, CancellationToken ct)
    {
        var run = await m.GetAsync("PaymentRun", runName, ct);
        if (run is null || run.DocStatus != DocStatus.Submitted) return;
        var d = run.Data;
        var existing = (await m.QueryAsync("PaymentEntry", [new FilterClause("payment_run", "=", runName)], ct, take: 5000))
            .Where(p => p.DocStatus != DocStatus.Cancelled).ToList();
        var failures = new List<string>();
        foreach (var g in d.Rows("items").GroupBy(r => (Type: r.Str("party_type")!, Party: r.Str("party")!)))
        {
            var pe = existing.FirstOrDefault(p => p.Data.Str("party_type") == g.Key.Type && p.Data.Str("party") == g.Key.Party);
            try
            {
                if (pe is null)
                {
                    pe = await sys.InsertAsync("PaymentEntry", new Dictionary<string, object?>
                    {
                        ["payment_type"] = "Pay", ["company"] = d.Str("company"), ["posting_date"] = d.Str("posting_date"), ["mode_of_payment"] = "Bank transfer",
                        ["party_type"] = g.Key.Type, ["party"] = g.Key.Party, ["bank_account"] = d.Str("bank_account"),
                        ["paid_amount"] = g.Sum(r => r.Dec("amount")), ["payment_run"] = runName, ["reference_no"] = runName,
                        ["remarks"] = $"{runName}: {string.Join(", ", g.Select(r => r.Str("reference_name")))}",
                        ["references"] = g.Select(r => new Dictionary<string, object?>
                        {
                            ["reference_doctype"] = r.Str("reference_doctype"), ["reference_name"] = r.Str("reference_name"), ["allocated_amount"] = r.Dec("amount"),
                        }).ToList(),
                    }, $"Created by {runName}", ct);
                }
                if (pe.DocStatus == DocStatus.Draft) await sys.ActionAsync("PaymentEntry", pe.Name, DocActions.Submit, null, ct);
            }
            catch (Exception ex) when (ex is ValidationFailedException or InvalidPayloadException or InvalidStateException or ConcurrencyException)
            {
                failures.Add($"{g.Key.Party}: {ex.Message}");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException($"{runName}: {failures.Count} payments failed — {string.Join("; ", failures.Take(5))}");
    }
}

/// <summary>Aksi "Get open items": utang jatuh tempo per pemilihan run (faktur pemasok, klaim & uang muka disetujui, gaji dari Payroll Run).</summary>
public sealed class PaymentRunGetItemsAction(ILedgerReader ledger, IDocumentStore store) : IFormAction<PaymentRun>
{
    public string ActionId => "erp.paymentrun.get_items";
    public string Label => "Get open items";
    public string Icon => "list";
    public string Tone => "primary";

    public bool IsVisible(UserContext user, Document doc) => doc.DocStatus == DocStatus.Draft && (user.HasRole("Finance") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var d = ctx.Doc.Data;
        var tenant = ctx.Doc.TenantId;
        var company = d.Str("company");
        if (company is null) return FormActionResult.Fail("Set the company first");
        var dueUntil = d.Date("due_until") ?? d.Date("posting_date") ?? DateOnly.FromDateTime(DateTime.Today);

        // Dokumen yang sudah ada di run draft lain tidak diambil lagi.
        var taken = (await m.QueryAsync("PaymentRun", [new FilterClause("company", "=", company)], ct, take: 500))
            .Where(r => r.Name != ctx.Doc.Name && r.DocStatus == DocStatus.Draft)
            .SelectMany(r => r.Data.Rows("items")).Select(r => (r.Str("reference_doctype"), r.Str("reference_name"))).ToHashSet();
        var items = new List<Dictionary<string, object?>>();
        async Task AddAsync(string refType, string refName, string partyType, string party, string? due)
        {
            if (taken.Contains((refType, refName))) return;
            var open = await PayRun.OpenPayableAsync(ledger, tenant, refType, refName, partyType, party, ct);
            if (open <= 0) return;
            var (bank, no, holder) = await PayRun.BeneficiaryAsync(m, partyType, party, ct);
            items.Add(new()
            {
                ["party_type"] = partyType, ["party"] = party, ["reference_doctype"] = refType, ["reference_name"] = refName, ["due_date"] = due,
                ["outstanding_amount"] = open, ["amount"] = open, ["bank_name"] = bank, ["account_no"] = no, ["account_holder"] = holder,
            });
        }

        if (d.Bool("include_vendor_invoices"))
        {
            var held = (await m.QueryAsync("Vendor", [new FilterClause("on_hold", "=", true)], ct, take: 5000)).Select(v => v.Name).ToHashSet();
            foreach (var inv in (await m.QueryAsync("PurchaseInvoice", [new FilterClause("company", "=", company)], ct, take: 10_000))
                     .Where(i => i.DocStatus == DocStatus.Submitted && !i.Data.Bool("on_hold") && i.Data.Date("due_date") <= dueUntil
                                 && i.Data.Str("vendor") is { } v && !held.Contains(v)))
                await AddAsync("PurchaseInvoice", inv.Name, "Vendor", inv.Data.Str("vendor")!, inv.Data.Str("due_date"));
        }
        if (d.Bool("include_expense_claims"))
            foreach (var type in new[] { "ExpenseClaim", "EmployeeAdvance" })
                foreach (var doc in (await m.QueryAsync(type, [new FilterClause("company", "=", company)], ct, take: 10_000))
                         .Where(x => x.DocStatus == DocStatus.Submitted && x.WorkflowState == Approval.Approved && x.Data.Str("employee") is not null))
                    await AddAsync(type, doc.Name, "Employee", doc.Data.Str("employee")!, doc.Data.Str("posting_date"));
        if (d.Str("payroll_run") is { } pr)
            foreach (var emp in (await ledger.GetAgainstAsync(tenant, "PayrollRun", pr, ct)).Where(e => e.PartyType == "Employee").Select(e => e.Party!).Distinct())
                await AddAsync("PayrollRun", pr, "Employee", emp, d.Str("posting_date"));

        await ctx.Docs.UpdateAsync(ctx.User, "PaymentRun", ctx.Doc.Name, ctx.Doc.Version, new Dictionary<string, object?> { ["items"] = items }, ct);
        var missing = items.Count(i => i["account_no"] is null);
        return FormActionResult.Ok($"{items.Count} open items loaded, total {items.Sum(i => (decimal)i["amount"]!):N2}."
                                   + (missing > 0 ? $" {missing} have no beneficiary bank account." : ""));
    }
}
