using System.Globalization;
using Apps.Erp.Ess;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Preferences;
using Framework.Security;
using Framework.Web;
using Framework.Web.Home;
using Framework.Web.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Apps.Erp.Home;

/// <summary>The signed-in employee's day: schedule, attendance, leave balance and latest payslip.</summary>
public sealed record EmployeeToday(
    string Employee,
    string FullName,
    DateOnly Date,
    string? ShiftName,
    string? ShiftStart,
    string? ShiftEnd,
    string? DayCategory,
    bool IsHoliday,
    string? CheckIn,
    string? CheckOut,
    string? LastPunchType,
    string? AttendanceStatus,
    bool CanPunchFromWeb,
    string? WebPunchNote,
    IReadOnlyList<EssLeaveBalance> LeaveBalances,
    string? PayslipName,
    string? PayslipPeriod,
    string? PayslipNetPay);

/// <summary>One of the employee's own requests (leave, overtime, expense claim, advance, shift swap).</summary>
public sealed record MyRequest(string DocType, string DocTypeTitle, string Name, string Summary, string? WorkflowState, int DocStatus, string Tone, DateTimeOffset Modified);

/// <summary>Company information for the side column.</summary>
public sealed record CompanyInfo(DateOnly? NextHolidayDate, string? NextHolidayName, IReadOnlyList<string> TeamOnLeaveToday);

/// <summary>One step of the administrator's first-run checklist.</summary>
public sealed record SetupStep(string Id, string Title, string Hint, bool Done, string? Href, bool Manual);

/// <summary>
/// Data behind the ERP home panels. Reads go straight to the store for the signed-in employee's own records (the same
/// rule as the ESS mobile API); actions (punch, workflow) go through the regular services and their permission checks.
/// </summary>
public sealed class ErpHomeService(IDocumentStore store, IMetadataProvider metadata, TimeProvider clock, IServiceProvider services)
{
    /// <summary>The DocTypes listed under "My requests" and the field that holds the requesting employee.</summary>
    public static readonly IReadOnlyList<(string DocType, string EmployeeField)> RequestSources =
    [
        ("LeaveApplication", "employee"), ("OvertimeRequest", "employee"), ("ExpenseClaim", "employee"),
        ("EmployeeAdvance", "employee"), ("ShiftSwapRequest", "requester"),
    ];

    public const string SetupDismissId = "erp-setup";
    public const string RestoreDrillPreference = "home.setup.restore-drill";

    private EssAttendanceService? Ess => services.GetService<EssAttendanceService>();
    private TimeZoneInfo Zone => Ess?.TimeZone ?? FieldTypeSettings.DefaultTimeZone;
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);

    public static string? EmployeeOf(UserContext user) => user.Attr("employee") is { Length: > 0 } e ? e : null;

    public async Task<EmployeeToday?> TodayAsync(UserContext user, CancellationToken ct = default)
    {
        if (EmployeeOf(user) is not { } emp || Ess is not { } ess) return null;
        var profile = await ess.GetProfileAsync(user.TenantId, emp, ct);
        if (profile is null) return null;
        var m = new Masters(store, user.TenantId);
        var today = Today;

        var schedTask = m.QueryAsync("EmployeeDailySchedule", [new("employee", "=", emp), new("work_date", "=", D.Iso(today))], ct, take: 1);
        var punchStore = services.GetService<IEssPunchStore>();
        var punchesTask = punchStore is null
            ? Task.FromResult<IReadOnlyList<EssPunch>>([])
            : punchStore.ListAsync(user.TenantId, emp, today, today, ct);
        var attendanceTask = m.QueryAsync("Attendance", [new("employee", "=", emp), new("attendance_date", "=", D.Iso(today))], ct, take: 1);
        var policyTask = ess.GetPolicyAsync(user.TenantId, ct);
        var balancesTask = EssLeave.BalancesAsync(m, emp, today, ct);
        var payslipTask = m.QueryAsync("Payslip", [new("employee", "=", emp), new(StandardFields.DocStatus, "=", (decimal)DocStatus.Submitted)],
            ct, "period", "desc", take: 1);

        await Task.WhenAll(schedTask, punchesTask, attendanceTask, policyTask, balancesTask, payslipTask);

        var sched = (await schedTask).FirstOrDefault();
        string? shift = profile.ShiftType, start = Hhmm(profile.ShiftStart), end = Hhmm(profile.ShiftEnd), category = null;
        var holiday = false;
        if (sched is not null)
        {
            shift = sched.Data.Str("shift_type") ?? sched.Data.Str("schedule_type");
            start = LocalTime(sched.Data.GetValueOrDefault("scheduled_start_datetime")) ?? start;
            end = LocalTime(sched.Data.GetValueOrDefault("scheduled_end_datetime")) ?? end;
            category = sched.Data.Str("day_category");
            holiday = sched.Data.Bool("is_public_holiday");
        }

        var punches = (await punchesTask).Where(p => p.Counts).OrderBy(p => p.PunchTime).ToList();
        var firstIn = punches.FirstOrDefault(p => p.PunchType == "in");
        var lastOut = punches.LastOrDefault(p => p.PunchType == "out");
        var attendance = (await attendanceTask).FirstOrDefault();

        var (canPunch, note) = WebPunchAllowed(profile, await policyTask);

        var balances = (await balancesTask).Where(b => b.IsAnnualQuota && b.Allocated is not null).ToList();

        var payslip = (await payslipTask).FirstOrDefault();
        string? net = null;
        if (payslip is not null && metadata.Current.TryGet("Payslip", out var pm) && pm.ByName.TryGetValue("net_pay", out var nf))
            net = Fmt.Value(nf, payslip.Data.GetValueOrDefault("net_pay"));

        return new EmployeeToday(emp, profile.FullName, today, shift, start, end, category, holiday,
            firstIn is null ? null : TimeZoneInfo.ConvertTime(firstIn.PunchTime, Zone).ToString("HH:mm", CultureInfo.InvariantCulture),
            lastOut is null ? null : TimeZoneInfo.ConvertTime(lastOut.PunchTime, Zone).ToString("HH:mm", CultureInfo.InvariantCulture),
            punches.LastOrDefault()?.PunchType, attendance?.Data.Str("status"), canPunch, note, balances,
            payslip?.Name, payslip?.Data.Str("period"), net);
    }

    /// <summary>
    /// The browser sends no GPS position, device id or selfie, so a punch from the home page is offered only when the
    /// policy needs none of them: attendance mode "Anywhere", device binding off and no photo required.
    /// </summary>
    public static (bool Allowed, string? Note) WebPunchAllowed(EssEmployeeProfile profile, EssAttendancePolicy policy)
    {
        if (profile.Status != "Active" || profile.AttendanceMode == EssAttendanceMode.NotAllowed)
            return (false, "Attendance is recorded by the office time clock.");
        if (profile.AttendanceMode != EssAttendanceMode.Anywhere || policy.BindDevice || policy.RequirePhoto)
            return (false, "Check in with the ESS mobile app (location and device are verified there).");
        return (true, null);
    }

    /// <summary>Records a check-in or check-out from the home page. Re-checks <see cref="WebPunchAllowed"/> on the server.</summary>
    public async Task<EssPunchResult> PunchFromWebAsync(UserContext user, string type, CancellationToken ct = default)
    {
        if (EmployeeOf(user) is not { } emp || Ess is not { } ess)
            throw new PermissionDeniedException("This account is not linked to an employee.");
        var profile = await ess.GetProfileAsync(user.TenantId, emp, ct) ?? throw new PermissionDeniedException("Employee not found.");
        var (allowed, note) = WebPunchAllowed(profile, await ess.GetPolicyAsync(user.TenantId, ct));
        if (!allowed) throw new PermissionDeniedException(note ?? "Web attendance is not allowed.");
        return await ess.PunchAsync(user, emp, new EssPunchRequest
        {
            Id = Guid.NewGuid(), Type = type, DeviceInfo = "Web browser (home page)", AppVersion = "web",
        }, ct);
    }

    /// <summary>The employee's latest requests across <see cref="RequestSources"/>, newest first.</summary>
    public async Task<IReadOnlyList<MyRequest>> MyRequestsAsync(UserContext user, int max = 5, CancellationToken ct = default)
    {
        if (EmployeeOf(user) is not { } emp) return [];
        var m = new Masters(store, user.TenantId);
        var tasks = RequestSources
            .Where(s => metadata.Current.TryGet(s.DocType, out _))
            .Select(async s =>
            {
                var meta = metadata.Current.Get(s.DocType);
                var docs = await m.QueryAsync(s.DocType, [new(s.EmployeeField, "=", emp)], ct, StandardFields.Modified, "desc", max);
                return docs.Select(d => new MyRequest(s.DocType, meta.Title, d.Name, Summary(d), d.WorkflowState, d.DocStatus, Fmt.Tone(meta, d), d.Modified)).ToList();
            });
        var batches = await Task.WhenAll(tasks);
        return batches.SelectMany(b => b).OrderByDescending(r => r.Modified).Take(max).ToList();
    }

    private static string Summary(Document d)
    {
        var x = d.Data;
        return d.DocType switch
        {
            "LeaveApplication" => $"{x.Str("leave_type")} · {x.Str("from_date")}{(x.Str("to_date") is { } to && to != x.Str("from_date") ? " – " + to : "")}",
            "OvertimeRequest" => $"{x.Str("overtime_date")} · {x.Dec("hours"):0.#} h",
            "ShiftSwapRequest" => $"{x.Str("swap_date")} · {x.Str("target_employee_name") ?? x.Str("target_employee")}",
            _ => x.Str("purpose") ?? "",
        };
    }

    /// <summary>Next holiday from the employee's (or the first company's) holiday list, and colleagues of the same unit on leave today.</summary>
    public async Task<CompanyInfo> CompanyInfoAsync(UserContext user, CancellationToken ct = default)
    {
        var m = new Masters(store, user.TenantId);
        var today = Today;
        var emp = EmployeeOf(user) is { } e ? await m.GetAsync("Employee", e, ct) : null;

        var listName = emp?.Data.Str("holiday_list");
        if (listName is null)
        {
            var company = emp?.Data.Str("company") is { } c ? await m.GetAsync("Company", c, ct)
                : (await m.QueryAsync("Company", [], ct, take: 1)).FirstOrDefault();
            listName = company?.Data.Str("default_holiday_list");
        }
        DateOnly? nextDate = null;
        string? nextName = null;
        if (listName is not null && await m.GetAsync("HolidayList", listName, ct) is { } hl)
        {
            var next = hl.Data.Rows("holidays")
                .Select(r => (Date: D.ToDate(r.GetValueOrDefault("holiday_date")), Name: r.Str("description"), Type: r.Str("holiday_type")))
                .Where(r => r.Date >= today && !string.Equals(r.Type, "Weekend", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Date).FirstOrDefault();
            nextDate = next.Date;
            nextName = next.Name;
        }

        var team = new List<string>();
        if (emp?.Data.Str("organization_unit") is { } unit)
        {
            var onLeave = await m.QueryAsync("LeaveApplication",
                [new("organization_unit", "=", unit), new("from_date", "<=", D.Iso(today)), new("to_date", ">=", D.Iso(today))], ct, take: 50);
            team.AddRange(onLeave.Where(l => l.WorkflowState == Approval.Approved && l.Data.Str("employee") != emp.Name) // not pending requests
                .Select(l => l.Data.Str("employee_name") ?? l.Data.Str("employee") ?? "").Where(n => n.Length > 0).Distinct());
        }
        return new CompanyInfo(nextDate, nextName, team);
    }

    /// <summary>First-run checklist for administrators. Most steps are detected from data; the restore drill is ticked by hand.</summary>
    public async Task<IReadOnlyList<SetupStep>> SetupStepsAsync(UserContext user, CancellationToken ct = default)
    {
        var m = new Masters(store, user.TenantId);
        var today = Today;
        async Task<bool> Any(string docType, IReadOnlyList<FilterClause>? filters = null) =>
            metadata.Current.TryGet(docType, out _) && (await m.QueryAsync(docType, filters ?? [], ct, take: 1)).Count > 0;

        var companyTask = Any("Company");
        var accountsTask = Any("Account");
        var fiscalTask = Any("FiscalYear", [new("start_date", "<=", D.Iso(today)), new("end_date", ">=", D.Iso(today))]);
        var holidaysTask = Any("HolidayList", [new("to_date", ">=", D.Iso(today))]);
        var linkedTask = Any("Employee", [new("user_id", "!=", null)]);
        var dir = services.GetService<IUserDirectory>();
        var usersTask = dir is not null
            ? dir.ListAsync(user.TenantId, ct).ContinueWith(t => t.Result.Count > 1, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default)
            : Task.FromResult(false);
        var twoFactor = services.GetService<IOptionsMonitor<TwoFactorOptions>>()?.CurrentValue.EffectiveRequiredRoles.Count > 0;
        var prefs = services.GetService<IUserPreferenceStore>();
        var drillTask = prefs is not null
            ? prefs.GetAsync<bool>(user.TenantId, user.Id, RestoreDrillPreference, ct)
            : Task.FromResult(false);

        await Task.WhenAll(companyTask, accountsTask, fiscalTask, holidaysTask, linkedTask, usersTask, drillTask);

        return
        [
            new("company", "Create the company", "Name, tax ID, base currency and default accounts.", await companyTask, "/app/Company/new", false),
            new("accounts", "Set up the chart of accounts", "Import or create the accounts the company posts to.", await accountsTask, "/app/Account", false),
            new("fiscal-year", "Open the current fiscal year", "Postings need a fiscal year that covers today.", await fiscalTask, "/app/FiscalYear/new", false),
            new("holidays", "Add this year's holiday list", "Used for leave days, schedules and the next holiday on the home page.", await holidaysTask, "/app/HolidayList/new", false),
            new("users", "Add users and give them roles", "Use specific roles (for example Finance-Payable) rather than group roles.", await usersTask, "/admin/users", false),
            new("employees", "Link users to employees", "Fill in the user on the employee record so self-service and approvals work.", await linkedTask, "/app/Employee", false),
            new("two-factor", "Require two-factor for admin roles", "Set FROGFRAME_2FA_REQUIRED_ROLES in .env (docs/guides/two-factor-and-sso.md).", twoFactor, null, false),
            new("restore-drill", "Test a database restore", "Run deploy/scripts/restore-drill.sh once before go-live and tick this when it passes.", await drillTask, null, true),
        ];
    }

    public async Task SetRestoreDrillAsync(UserContext user, bool done, CancellationToken ct = default)
    {
        if (services.GetService<IUserPreferenceStore>() is { } prefs)
            await prefs.SetAsync(user.TenantId, user.Id, RestoreDrillPreference, done, ct);
    }

    private static string? Hhmm(string? time) =>
        string.IsNullOrWhiteSpace(time) ? null : time.Length >= 5 ? time[..5] : time;

    private string? LocalTime(object? value)
    {
        return DatetimeFieldType.ToZone(value, Zone) is { } dto ? dto.ToString("HH:mm", CultureInfo.InvariantCulture) : null;
    }
}
