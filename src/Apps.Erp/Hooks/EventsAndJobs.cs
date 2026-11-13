using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

/// <summary>
/// Efek lintas dokumen dari hook ERP. Diproses setelah transaksi asal commit (outbox, dengan retry), jadi setiap cabang
/// harus idempoten: periksa dulu apakah efeknya sudah ada. DocumentService di-resolve lambat untuk menghindari siklus DI.
/// </summary>
public sealed class ErpEventHandler(IServiceProvider services, IDocumentStore store) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType.StartsWith(ErpEvents.Prefix, StringComparison.Ordinal);

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var tenant = message.TenantId;
        var p = message.Payload;
        var sys = new SystemDocs(services.GetRequiredService<DocumentService>(), tenant);
        var m = new Masters(store, tenant);
        var name = p.GetValueOrDefault("name") ?? "";

        switch (message.Type)
        {
            case ErpEvents.PositionSync:
                foreach (var pos in new[] { p.GetValueOrDefault("old_position"), p.GetValueOrDefault("new_position") }.OfType<string>().Distinct())
                    await sys.TouchAsync("Position", pos, ct); // PositionHook menghitung pemangku & status
                break;

            case ErpEvents.OrgManagerChanged:
            {
                var oldBoss = (await Hr.HolderAsync(m, p.GetValueOrDefault("old_position"), null, ct))?.Name;
                var newBoss = (await Hr.HolderAsync(m, p.GetValueOrDefault("new_position"), null, ct))?.Name;
                foreach (var emp in (await m.QueryAsync("Employee", [new FilterClause("organization_unit", "=", name)], ct, take: 5000))
                         .Where(e => e.Data.Str("status") != Hr.Left && e.Name != newBoss))
                {
                    var current = emp.Data.Str("reports_to");
                    if (current is not null && current != oldBoss) continue; // atasan diatur khusus: jangan ditimpa
                    var patch = new Dictionary<string, object?> { ["reports_to"] = newBoss };
                    foreach (var f in new[] { "leave_approver", "expense_approver" })
                        if (emp.Data.Str(f) is null || emp.Data.Str(f) == oldBoss) patch[f] = newBoss;
                    await sys.UpdateAsync("Employee", emp.Name, patch, ct);
                }
                break;
            }

            case ErpEvents.JobChangeApply:
                if (await m.GetAsync("JobChange", name, ct) is { } jc) await HrApply.JobChangeAsync(sys, jc, ct);
                break;

            case ErpEvents.PaymentRunExecute:
                await PaymentRunExecutor.ExecuteAsync(sys, m, name, ct);
                break;

            case ErpEvents.EmployeeUser:
            {
                var hs = await m.SingleAsync("HRSettings", ct);
                if (!hs.Bool("auto_create_user")) break;
                var emp = await m.GetAsync("Employee", name, ct);
                if (emp is null || emp.Data.Str("user_id") is not null || emp.Data.Str("status") == Hr.Left) break;
                if (emp.Data.Str("work_email") is not { } email) break;
                if (await HrApply.EnsureUserAsync(services, tenant, emp, email, ct) is { } userId)
                    await sys.UpdateAsync("Employee", emp.Name, new Dictionary<string, object?> { ["user_id"] = userId }, ct);
                break;
            }

            case ErpEvents.SeparationApply:
                if (await m.GetAsync("EmployeeSeparation", name, ct) is { } sep)
                    await HrApply.SeparationAsync(sys, services.GetService<IUserAccountRepository>(), sep, ct);
                break;

            case ErpEvents.FiscalYearStatus:
                if (p.GetValueOrDefault("fiscal_year") is { } fy && p.GetValueOrDefault("status") is { } status
                    && (await m.GetAsync("FiscalYear", fy, ct))?.Data.Str("status") != status)
                    await sys.UpdateAsync("FiscalYear", fy, new Dictionary<string, object?> { ["status"] = status }, ct);
                break;

            case ErpEvents.TaxInvoiceCreate:
            {
                var docType = p.GetValueOrDefault("docType") ?? "SalesInvoice";
                if ((await m.QueryAsync("TaxInvoice", [new FilterClause("reference_name", "=", name)], ct, take: 1)).Count > 0) break;
                var inv = await m.GetAsync(docType, name, ct);
                if (inv is null || inv.DocStatus != DocStatus.Submitted) break;
                await sys.InsertAsync("TaxInvoice", new Dictionary<string, object?>
                {
                    ["direction"] = "Output", ["company"] = inv.Data.Str("company"), ["party_type"] = "Customer",
                    ["party"] = inv.Data.Str("customer"), ["reference_doctype"] = docType, ["reference_name"] = name,
                    ["tax_period"] = inv.Data.Str("posting_date")?[..7],
                }, "Created automatically from invoice", ct);
                break;
            }

            case ErpEvents.PurchaseFollowUps:
            {
                var inv = await m.GetAsync("PurchaseInvoice", name, ct);
                if (inv is null || inv.DocStatus != DocStatus.Submitted) break;
                var d = inv.Data;
                if (d.Dec("wht_amount") > 0 && (await m.QueryAsync("WithholdingCertificate", [new FilterClause("reference_name", "=", name)], ct, take: 1)).Count == 0)
                    await sys.InsertAsync("WithholdingCertificate", new Dictionary<string, object?>
                    {
                        ["direction"] = "We withhold", ["company"] = d.Str("company"), ["tax_code"] = d.Str("wht_code"),
                        ["party_type"] = "Vendor", ["party"] = d.Str("vendor"), ["reference_doctype"] = "PurchaseInvoice", ["reference_name"] = name,
                        ["tax_period"] = d.Str("posting_date")?[..7], ["gross_amount"] = d.Dec("wht_base"),
                    }, "Created automatically from invoice", ct);

                var assetLines = d.Rows("items").Where(r => r.Str("asset_category") is not null).ToList();
                var existing = (await m.QueryAsync("Asset", [new FilterClause("purchase_invoice", "=", name)], ct, take: 500)).Count(a => a.DocStatus != DocStatus.Cancelled);
                var rate = d.Dec("exchange_rate") is > 0m and var x ? x : 1m;
                foreach (var line in assetLines.Skip(existing))
                    await sys.InsertAsync("Asset", new Dictionary<string, object?>
                    {
                        ["asset_name"] = line.Str("item_name"), ["asset_category"] = line.Str("asset_category"), ["company"] = d.Str("company"),
                        ["purchase_invoice"] = name, ["purchase_date"] = d.Str("posting_date"), ["available_for_use_date"] = d.Str("posting_date"),
                        ["depreciation_start_date"] = d.Str("posting_date"), ["gross_purchase_amount"] = D.R2(line.Dec("amount") * rate),
                        ["cost_center"] = line.Str("cost_center"),
                    }, "Created automatically from purchase invoice", ct);
                break;
            }

            case ErpEvents.QuotationWon:
            {
                if (p.GetValueOrDefault("opportunity") is not { } opp) break;
                var won = await Crm.StageAsync(m, "Won", ct);
                var current = await m.GetAsync("Opportunity", opp, ct);
                if (won is null || current is null) break;
                if (await m.StrAsync("SalesStage", current.Data.Str("sales_stage"), "stage_type", ct) == "Won") break;
                await sys.UpdateAsync("Opportunity", opp, new Dictionary<string, object?> { ["sales_stage"] = won.Name }, ct);
                break;
            }

            case ErpEvents.QuotationBilled:
                if (p.GetValueOrDefault("quotation") is { } q && (await m.GetAsync("Quotation", q, ct))?.WorkflowState == "Accepted")
                    await sys.ActionAsync("Quotation", q, "Mark billed", null, ct);
                break;
        }
    }
}

/// <summary>Harian: Perubahan Jabatan dan Pemberhentian yang tanggal efektifnya sudah tiba.</summary>
public sealed class HrEffectiveDateJob : IScheduledJob
{
    public string Name => "Erp.HrEffectiveDate";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var m = new Masters(context.Services.GetRequiredService<IDocumentStore>(), context.TenantId);
        var sys = new SystemDocs(context.Docs, context.TenantId);
        var today = D.Iso(DateOnly.FromDateTime(context.ScheduledTime.Date));
        foreach (var jc in (await m.QueryAsync("JobChange", [new FilterClause("effective_date", "<=", today)], ct, "effective_date", "asc", 5000))
                 .Where(j => j.DocStatus == DocStatus.Submitted))
            await Safe(context, jc.Name, () => HrApply.JobChangeAsync(sys, jc, ct));
        var accounts = context.Services.GetService<IUserAccountRepository>();
        foreach (var sep in (await m.QueryAsync("EmployeeSeparation", [new FilterClause("last_working_date", "<=", today)], ct, take: 5000))
                 .Where(s => s.DocStatus == DocStatus.Submitted))
            await Safe(context, sep.Name, () => HrApply.SeparationAsync(sys, accounts, sep, ct));
    }

    internal static async Task Safe(ScheduledJobContext context, string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { context.Logger.LogJobFailed(ex, what, context.TenantId); }
    }
}

/// <summary>
/// Harian: alokasi cuti periode berjalan untuk karyawan aktif dari kebijakan cutinya (dengan syarat masa kerja & sisa periode lalu).
/// Idempoten: dilewati bila sudah ada alokasi (aktif) yang mencakup hari ini.
/// </summary>
public sealed class LeaveAllocationJob : IScheduledJob
{
    public string Name => "Erp.LeaveAllocation";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var m = new Masters(context.Services.GetRequiredService<IDocumentStore>(), context.TenantId);
        var sys = new SystemDocs(context.Docs, context.TenantId);
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);
        var hs = await m.SingleAsync("HRSettings", ct);
        var byHireDate = hs.Str("leave_year_start") == "Date of joining";
        var allocations = (await m.QueryAsync("LeaveAllocation", [], ct, take: 50000)).Where(a => a.DocStatus != DocStatus.Cancelled).ToList();
        var leaves = (await m.QueryAsync("LeaveApplication", [], ct, take: 50000)).Where(a => a.DocStatus == DocStatus.Submitted && LeaveMath.Active(a)).ToList();

        foreach (var emp in (await m.QueryAsync("Employee", [new FilterClause("status", "=", "Active")], ct, take: 50000)))
        {
            var policy = await m.GetAsync("LeavePolicy", emp.Data.Str("leave_policy") ?? hs.Str("default_leave_policy"), ct);
            var hire = emp.Data.Date("hire_date");
            if (policy is null || hire is null) continue;
            var (from, to) = Window(today, hire.Value, byHireDate);
            foreach (var detail in policy.Data.Rows("details"))
            {
                var type = await m.GetAsync("LeaveType", detail.Str("leave_type"), ct);
                if (type is null || !type.Data.Bool("is_annual_quota")) continue;
                if (AssetBook.MonthsBetween(hire.Value, today) < (int)detail.Dec("min_service_months")) continue;
                var mine = allocations.Where(a => a.Data.Str("employee") == emp.Name && a.Data.Str("leave_type") == type.Name).ToList();
                if (mine.Any(a => a.Data.Date("from_date") <= today && today <= a.Data.Date("to_date"))) continue;

                decimal carry = 0;
                var prev = mine.FirstOrDefault(a => a.DocStatus == DocStatus.Submitted && a.Data.Date("to_date") == from.AddDays(-1));
                if (prev is not null && type.Data.Bool("allow_carry_forward"))
                {
                    var used = leaves.Where(l => l.Data.Str("employee") == emp.Name && l.Data.Str("leave_type") == type.Name
                                                 && Hr.Overlaps(prev.Data.Date("from_date"), prev.Data.Date("to_date"), l.Data.Date("from_date"), l.Data.Date("to_date")))
                        .Sum(l => l.Data.Dec("total_days"));
                    carry = Math.Max(0, prev.Data.Dec("total_leaves") - used);
                    var cap = type.Data.Dec("max_carry_forward_days");
                    if (cap > 0) carry = Math.Min(carry, cap);
                }
                await HrEffectiveDateJob.Safe(context, $"{emp.Name}/{type.Name}", async () =>
                {
                    var draft = await sys.InsertAsync("LeaveAllocation", new Dictionary<string, object?>
                    {
                        ["employee"] = emp.Name, ["leave_type"] = type.Name, ["from_date"] = D.Iso(from), ["to_date"] = D.Iso(to),
                        ["leave_policy"] = policy.Name, ["new_leaves"] = detail.Dec("annual_allocation"), ["carry_forwarded"] = carry,
                    }, "Created automatically from leave policy", ct);
                    await sys.ActionAsync("LeaveAllocation", draft.Name, DocActions.Submit, null, ct);
                });
            }
        }
    }

    /// <summary>Periode cuti yang memuat hari ini: tahun kalender, atau tahun berjalan sejak ulang tahun tanggal masuk.</summary>
    internal static (DateOnly From, DateOnly To) Window(DateOnly today, DateOnly hire, bool byHireDate)
    {
        if (!byHireDate) return (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31));
        var day = Math.Min(hire.Day, DateTime.DaysInMonth(today.Year, hire.Month));
        var anniversary = new DateOnly(today.Year, hire.Month, day);
        var from = anniversary <= today ? anniversary : anniversary.AddYears(-1);
        return (from, from.AddYears(1).AddDays(-1));
    }
}

/// <summary>Harian: pengingat kontrak PKWT yang akan berakhir (ke HR dan atasan), sekali per kontrak.</summary>
public sealed class ContractExpiryReminderJob : IScheduledJob
{
    public string Name => "Erp.ContractExpiryReminder";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var m = new Masters(context.Services.GetRequiredService<IDocumentStore>(), context.TenantId);
        var users = context.Services.GetRequiredService<IUserDirectory>();
        var inbox = context.Services.GetRequiredService<INotificationStore>();
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);
        var daysAhead = (int)(await m.SingleAsync("HRSettings", ct)).Dec("contract_expiry_reminder_days");
        if (daysAhead <= 0) daysAhead = 30;
        var contracts = (await m.QueryAsync("EmploymentContract", [new FilterClause("contract_type", "=", "PKWT")], ct, take: 50000))
            .Where(x => x.DocStatus == DocStatus.Submitted).ToList();
        var renewed = contracts.Select(x => x.Data.Str("renewal_of")).OfType<string>().ToHashSet();
        foreach (var k in contracts.Where(x => !renewed.Contains(x.Name) && x.Data.Date("end_date") is { } e && e >= today && e <= today.AddDays(daysAhead)))
        {
            var emp = await m.GetAsync("Employee", k.Data.Str("employee"), ct);
            if (emp is null || emp.Data.Str("status") == Hr.Left) continue;
            var subject = $"Contract of {emp.Data.Str("full_name")} ends {k.Data.Str("end_date")}";
            const string body = "Decide on renewal (create a new contract with 'Renewal of') or process a separation.";
            await Notify.ToRoleAsync(users, inbox, context.TenantId, "HR", subject, body, "EmploymentContract", k.Name, $"contract-expiry|{k.Name}", ct);
            await Notify.ToEmployeesAsync(users, inbox, context.TenantId, [emp.Data.Str("reports_to")], subject, body, "EmploymentContract", k.Name, $"contract-expiry|{k.Name}", ct);
        }
    }
}

/// <summary>Harian: penawaran Terkirim yang lewat masa berlaku → Kedaluwarsa; opportunity terbuka tanpa aktivitas → pengingat ke pemiliknya.</summary>
public sealed class CrmDailyJob : IScheduledJob
{
    public string Name => "Erp.CrmDaily";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var m = new Masters(context.Services.GetRequiredService<IDocumentStore>(), context.TenantId);
        var sys = new SystemDocs(context.Docs, context.TenantId);
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);

        foreach (var q in (await m.QueryAsync("Quotation", [new FilterClause("valid_until", "<", D.Iso(today))], ct, take: 5000))
                 .Where(q => q.WorkflowState == "Sent"))
            await HrEffectiveDateJob.Safe(context, q.Name, () => sys.ActionAsync("Quotation", q.Name, "Mark expired", "Past validity date", ct));

        var staleDays = (int)(await Crm.SettingsAsync(m, ct)).Dec("stale_opportunity_days");
        if (staleDays <= 0) staleDays = 14;
        var users = context.Services.GetRequiredService<IUserDirectory>();
        var inbox = context.Services.GetRequiredService<INotificationStore>();
        var lastActivity = (await m.QueryAsync("Activity", [new FilterClause("reference_doctype", "=", "Opportunity")], ct, take: 50000))
            .GroupBy(a => a.Data.Str("reference_name") ?? "")
            .ToDictionary(g => g.Key, g => g.Max(a => a.Data.Str("start_at")?[..10]));
        var week = System.Globalization.ISOWeek.GetWeekOfYear(today.ToDateTime(TimeOnly.MinValue));
        foreach (var opp in await m.QueryAsync("Opportunity", [], ct, take: 50000))
        {
            if (await m.StrAsync("SalesStage", opp.Data.Str("sales_stage"), "stage_type", ct) != "Open") continue;
            var last = lastActivity.GetValueOrDefault(opp.Name) ?? opp.Modified.ToString("yyyy-MM-dd");
            if (string.CompareOrdinal(last, D.Iso(today.AddDays(-staleDays))) > 0) continue;
            await Notify.ToEmployeesAsync(users, inbox, context.TenantId, [opp.Data.Str("sales_rep")],
                $"Opportunity {opp.Data.Str("opportunity_name")} has had no activity since {last}",
                "Log the next activity or update its stage.", "Opportunity", opp.Name, $"stale|{opp.Name}|{today.Year}-{week}", ct);
        }

        // Kontrak pelanggan yang akan berakhir: satu notifikasi per kontrak ke pemiliknya.
        foreach (var k in (await m.QueryAsync("CustomerContract", [], ct, take: 50000)).Where(k => k.DocStatus == DocStatus.Submitted))
        {
            if (await CustomerContractHook.StatusAsync(m, k, today, ct) != "Expiring soon") continue;
            await Notify.ToEmployeesAsync(users, inbox, context.TenantId, [k.Data.Str("sales_rep")],
                $"Contract {k.Data.Str("contract_title")} with {k.Data.Str("customer_name")} ends {k.Data.Str("end_date")}",
                k.Data.Bool("auto_renew") ? "Auto-renew is on: prepare the renewal contract." : "Decide on renewal with the customer.",
                "CustomerContract", k.Name, $"contract-end|{k.Name}", ct);
        }
    }
}
