using System.Globalization;
using Apps.Erp.Ess;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Home;

/// <summary>A direct report's day: status (tone for the pill) and a short detail (check-in time, leave type…).</summary>
public sealed record TeamMemberToday(string Employee, string FullName, string? Position, string Status, string Tone, string? Detail);

/// <summary>Someone away: approved time off (leave) or a scheduled day off, for one day or a period.</summary>
public sealed record AwayEntry(string Employee, string FullName, string? Unit, string Kind, string? Detail, DateOnly From, DateOnly To, bool HalfDay);

/// <summary>
/// Data of the "My team" and "Who is away" home panels. <see cref="CompanyWide"/>: the reader is HR and sees the whole
/// tenant in the away lists; otherwise the lists cover the reader's direct reports.
/// </summary>
public sealed record TeamOverview(
    DateOnly Date,
    IReadOnlyList<TeamMemberToday> DirectReports,
    IReadOnlyList<AwayEntry> AwayToday,
    int DayOffCountToday,
    IReadOnlyList<AwayEntry> UpcomingTimeOff,
    bool CompanyWide)
{
    public bool IsEmpty => DirectReports.Count == 0 && AwayToday.Count == 0 && DayOffCountToday == 0 && UpcomingTimeOff.Count == 0;
}

/// <summary>
/// "My team" for managers and "Who is away" for managers and HR.
/// - Direct reports = active employees whose <c>reports_to</c> is the reader, with today's status in this order:
///   approved leave → day off (daily schedule REST/HOLIDAY or public holiday; without a schedule the holiday list and
///   weekly off) → attendance / clock-in → absent → not checked in yet.
/// - Time off = approved Leave Applications (pending ones are not shown). Day off = daily schedule REST/HOLIDAY, plus for
///   direct reports the holiday list / weekly off. Day offs are listed by name up to <see cref="MaxDayOffNames"/>;
///   beyond that only the number is shown (a weekend or public holiday would otherwise list everyone).
/// - HR (HR-Employee-Management, HR-Time-Attendance, HR-Admin, administrators) see the away lists for the whole tenant.
/// Reads go straight to the store; nothing here changes data.
/// </summary>
public sealed class TeamHomeService(IDocumentStore store, TimeProvider clock, IServiceProvider services)
{
    public const int MaxDirectReports = 200;
    public const int MaxDayOffNames = 15;
    public const int UpcomingDays = 7;

    private EssAttendanceService? Ess => services.GetService<EssAttendanceService>();
    private TimeZoneInfo Zone => Ess?.TimeZone ?? FieldTypeSettings.DefaultTimeZone;
    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Zone).DateTime);

    public static bool IsHr(UserContext u) =>
        u.IsAdmin || u.HasRole(ErpRoles.HrEmployeeManagement) || u.HasRole(ErpRoles.HrTimeAttendance) || u.HasRole(ErpRoles.HrAdmin);

    public async Task<TeamOverview> OverviewAsync(UserContext user, CancellationToken ct = default, DateOnly? on = null)
    {
        var today = on ?? Today;
        var m = new Masters(store, user.TenantId);
        var me = ErpHomeService.EmployeeOf(user);
        var companyWide = IsHr(user);

        var directs = me is null ? [] : (await m.QueryAsync("Employee", [new FilterClause("reports_to", "=", me)], ct, "full_name", "asc", MaxDirectReports))
            .Where(e => e.Data.Str("status") != Hr.Left && e.Name != me).ToList();
        if (directs.Count == 0 && !companyWide) return new(today, [], [], 0, [], false);
        var directIds = directs.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        bool InScope(string? emp) => emp is not null && (companyWide || directIds.Contains(emp));

        // Approved leave from today to the end of the upcoming window (one query for the tenant, then the scope).
        var horizon = today.AddDays(UpcomingDays);
        var leaves = (await m.QueryAsync("LeaveApplication",
                [new FilterClause("from_date", "<=", D.Iso(horizon)), new FilterClause("to_date", ">=", D.Iso(today))], ct, "from_date", "asc", 5000))
            .Where(l => l.WorkflowState == Approval.Approved && InScope(l.Data.Str("employee")))
            .ToList();
        AwayEntry Leave(Document l) => new(l.Data.Str("employee")!, l.Data.Str("employee_name") ?? l.Data.Str("employee")!, l.Data.Str("organization_unit"),
            "Time off", l.Data.Str("leave_type"), l.Data.Date("from_date") ?? today, l.Data.Date("to_date") ?? today, l.Data.Bool("half_day"));
        bool Covers(Document l, DateOnly d) => l.Data.Date("from_date") is { } f && l.Data.Date("to_date") is { } t && f <= d && d <= t;

        var leaveToday = leaves.Where(l => Covers(l, today)).GroupBy(l => l.Data.Str("employee")!).ToDictionary(g => g.Key, g => g.First());
        var upcoming = leaves.Where(l => l.Data.Date("from_date") is { } f && f > today).Select(Leave).ToList();

        // Daily schedules of today (scheduled rest days and holidays).
        var schedules = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("work_date", "=", D.Iso(today))], ct, take: 20000))
            .Where(s => InScope(s.Data.Str("employee")))
            .GroupBy(s => s.Data.Str("employee")!).ToDictionary(g => g.Key, g => g.First());
        static bool IsOff(Document s) => s.Data.Str("schedule_type") is "REST" or "HOLIDAY" || s.Data.Bool("is_public_holiday");

        // Direct reports: one status each.
        var members = new List<TeamMemberToday>();
        var dayOff = new Dictionary<string, AwayEntry>(StringComparer.Ordinal);
        foreach (var e in directs)
        {
            var name = e.Data.Str("full_name") ?? e.Name;
            var position = await m.StrAsync("Position", e.Data.Str("position"), "position_title", ct)
                ?? await m.StrAsync("JobProfile", e.Data.Str("job_profile"), "job_title", ct);
            if (leaveToday.TryGetValue(e.Name, out var lv))
            {
                var half = lv.Data.Bool("half_day");
                members.Add(new(e.Name, name, position, half ? "Half-day leave" : "On leave", "pending",
                    $"{lv.Data.Str("leave_type")} · {D.Iso(lv.Data.Date("from_date") ?? today)} – {D.Iso(lv.Data.Date("to_date") ?? today)}"));
                continue;
            }
            var offDetail = await DayOffDetailAsync(m, e, schedules.GetValueOrDefault(e.Name), today, ct);
            if (offDetail is not null)
            {
                members.Add(new(e.Name, name, position, "Day off", "neutral", offDetail));
                dayOff[e.Name] = new(e.Name, name, e.Data.Str("organization_unit"), "Day off", offDetail, today, today, false);
                continue;
            }
            members.Add(await AttendanceStatusAsync(m, user.TenantId, e, name, position, today, ct));
        }

        // Day off outside the direct reports (HR view): from schedules only.
        if (companyWide)
        {
            var names = await NamesAsync(m, schedules.Keys.Where(k => !dayOff.ContainsKey(k) && IsOff(schedules[k])), ct);
            foreach (var (emp, s) in schedules)
            {
                if (dayOff.ContainsKey(emp) || !IsOff(s) || leaveToday.ContainsKey(emp)) continue;
                dayOff[emp] = new(emp, s.Data.Str("employee_name") ?? names.GetValueOrDefault(emp) ?? emp, s.Data.Str("organization_unit"), "Day off",
                    s.Data.Bool("is_public_holiday") ? "Public holiday" : s.Data.Str("day_category"), today, today, false);
            }
        }

        var away = leaveToday.Values.Select(Leave).OrderBy(a => a.FullName, StringComparer.CurrentCulture).ToList();
        var dayOffCount = dayOff.Count;
        if (dayOffCount <= MaxDayOffNames) away.AddRange(dayOff.Values.OrderBy(a => a.FullName, StringComparer.CurrentCulture));

        return new(today, members, away, dayOffCount > MaxDayOffNames ? dayOffCount : 0, upcoming, companyWide);
    }

    /// <summary>Why <paramref name="e"/> is off today, or null when it is a working day.</summary>
    private static async Task<string?> DayOffDetailAsync(Masters m, Document e, Document? schedule, DateOnly today, CancellationToken ct)
    {
        if (schedule is not null)
        {
            if (schedule.Data.Bool("is_public_holiday")) return "Public holiday";
            return schedule.Data.Str("schedule_type") is "REST" or "HOLIDAY" ? schedule.Data.Str("day_category") ?? "Rest day" : null;
        }
        var listName = e.Data.Str("holiday_list") ?? await m.StrAsync("Company", e.Data.Str("company"), "default_holiday_list", ct);
        var hl = await m.GetAsync("HolidayList", listName, ct);
        if (hl?.Data.Rows("holidays").FirstOrDefault(h => h.Date("holiday_date") == today) is { } holiday)
            return holiday.Str("description") ?? "Holiday";
        return (await LeaveMath.WeeklyOffAsync(m, e, hl, ct)).Contains(today.DayOfWeek) ? "Weekly day off" : null;
    }

    private async Task<TeamMemberToday> AttendanceStatusAsync(Masters m, string tenant, Document e, string name, string? position, DateOnly today, CancellationToken ct)
    {
        var att = (await m.QueryAsync("Attendance", [new FilterClause("employee", "=", e.Name), new FilterClause("attendance_date", "=", D.Iso(today))], ct, take: 1))
            .FirstOrDefault();
        var checkIn = att is null ? null : LocalTime(att.Data.GetValueOrDefault("check_in"));
        if (checkIn is null && services.GetService<IEssPunchStore>() is { } punches)
        {
            var first = (await punches.ListAsync(tenant, e.Name, today, today, ct))
                .Where(p => p.Counts && p.PunchType == "in").OrderBy(p => p.PunchTime).FirstOrDefault();
            if (first is not null) checkIn = TimeZoneInfo.ConvertTime(first.PunchTime, Zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        var code = att?.Data.Str("attendance_code");
        if (checkIn is not null)
        {
            var late = code is "LI" or "LI/EO";
            var minutes = att?.Data.Dec("late_minutes") ?? 0m;
            return new(e.Name, name, position, late ? "Late" : "Present", late ? "warning" : "positive",
                late && minutes > 0 ? $"In {checkIn} · {minutes:0} min late" : $"In {checkIn}");
        }
        if (att?.Data.Str("status") == "Absent" || code == "ABS")
            return new(e.Name, name, position, "Absent", "negative", null);
        return new(e.Name, name, position, "Not checked in", "neutral", null);
    }

    private static async Task<Dictionary<string, string>> NamesAsync(Masters m, IEnumerable<string> employees, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var emp in employees.Take(MaxDayOffNames + 1))
            if (await m.StrAsync("Employee", emp, "full_name", ct) is { } n) result[emp] = n;
        return result;
    }

    private string? LocalTime(object? value) =>
        DatetimeFieldType.ToZone(value, Zone) is { } dto ? dto.ToString("HH:mm", CultureInfo.InvariantCulture) : null;
}
