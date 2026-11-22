using System.Globalization;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

public sealed class Attendance { }
public sealed class OvertimeRequest { }

/// <summary>Overtime calculation under PP 35/2021: tier multipliers (Art. 31), hour caps (Art. 26), and hourly wage 1/173 (Art. 32).</summary>
internal static class Overtime
{
    public const string WorkingDay = "Working day", RestDay = "Rest day or public holiday", ShortestDay = "Public holiday on the shortest working day";
    public const decimal MaxPerWorkingDay = 4, MaxPerWeek = 18;

    /// <summary>Paid hours (hours × tier multiplier) and daily hour cap for a day type and 5/6-day work week.</summary>
    public static (decimal Weighted, decimal MaxHours) Weigh(string dayType, int workWeekDays, decimal hours)
    {
        static decimal Tiers(decimal h, decimal t2) =>
            Math.Min(h, t2) * 2 + Math.Clamp(h - t2, 0, 1) * 3 + Math.Clamp(h - t2 - 1, 0, 3) * 4;
        return dayType switch
        {
            WorkingDay => (Math.Min(hours, 1) * 1.5m + Math.Max(hours - 1, 0) * 2, MaxPerWorkingDay),
            ShortestDay => (Tiers(hours, 5), 9),
            _ when workWeekDays == 5 => (Tiers(hours, 8), 12),
            _ => (Tiers(hours, 7), 11),
        };
    }

    public static async Task<int> WorkWeekAsync(Masters m, Document emp, CancellationToken ct, Document? dailySchedule = null)
    {
        if (dailySchedule?.Data.Str("shift_pattern") is { } pat
            && await m.StrAsync("ShiftPattern", pat, "work_week_days", ct) is { } patWeek)
            return patWeek == "6" ? 6 : 5;
        var shift = dailySchedule?.Data.Str("shift_type")
            ?? emp.Data.Str("shift_type")
            ?? (await m.SingleAsync("HRSettings", ct)).Str("default_shift_type");
        return await m.StrAsync("ShiftType", shift, "working_days_per_week", ct) == "6" ? 6 : 5;
    }

    /// <summary>
    /// Monthly overtime pay for payslip: Σ approved paid overtime hours × hourly wage. Monthly base = regular earnings marked
    /// in BPJS base (basic salary + fixed allowances); if less than 75% of total regular earnings, 75% of total is used (PP 35/2021 Art. 32(4)).
    /// </summary>
    public static async Task<(decimal WeightedHours, decimal HourlyWage, decimal Amount)> PayAsync(Masters m, string employee, string period, Document assignment, CancellationToken ct)
    {
        var approved = (await m.QueryAsync("OvertimeRequest", [new FilterClause("employee", "=", employee), new FilterClause("period", "=", period)], ct, take: 500))
            .Where(o => o.DocStatus == DocStatus.Submitted && o.WorkflowState == Approval.Approved).ToList();
        if (approved.Count == 0) return (0, 0, 0);
        decimal fixedPay = 0, regular = 0;
        foreach (var r in assignment.Data.Rows("earnings"))
        {
            var comp = await m.GetAsync("SalaryComponent", r.Str("salary_component"), ct);
            if (comp is null || !comp.Data.Bool("is_regular")) continue;
            regular += r.Dec("amount");
            if (comp.Data.Bool("in_bpjs_base")) fixedPay += r.Dec("amount");
        }
        var monthly = fixedPay < regular * 0.75m ? regular * 0.75m : fixedPay;
        var hourly = monthly / 173m;
        var weighted = approved.Sum(o => o.Data.Dec("weighted_hours"));
        return (weighted, D.R2(hourly), PayrollCalc.Rupiah(weighted * hourly));
    }

    public static async Task CheckNotPaidAsync(Masters m, string? employee, string period, string path, CancellationToken ct)
    {
        var paid = await SubmittedPayslipAsync(m, employee, period, ct);
        if (paid is not null) throw Errors.One(path, $"Payslip {paid.Name} for {period} is already submitted; this month is locked");
    }

    /// <summary>The submitted payslip of <paramref name="employee"/> for <paramref name="period"/> (yyyy-MM), if any: that month is locked.</summary>
    public static async Task<Document?> SubmittedPayslipAsync(Masters m, string? employee, string period, CancellationToken ct)
    {
        if (employee is null) return null;
        return (await m.QueryAsync("Payslip", [new FilterClause("employee", "=", employee), new FilterClause("period", "=", period)], ct, take: 10))
            .FirstOrDefault(p => p.DocStatus == DocStatus.Submitted);
    }
}

/// <summary>
/// Attendance: one per employee per work date; resolves shift &amp; cross-midnight window from materialized EmployeeDailySchedule
/// (falling back to Employee shift / HRSettings); links to approved leave; locked once the month's payslip is submitted.
/// </summary>
public sealed class AttendanceHook(IDocumentStore store, TimeProvider? clock = null) : IBeforeValidate<Attendance>, IBeforeDelete<Attendance>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        var date = d.Date("attendance_date") ?? throw Errors.One("attendance_date", "The date is required");
        await Overtime.CheckNotPaidAsync(m, emp.Name, $"{date:yyyy-MM}", "attendance_date", ct);
        var dup = (await m.QueryAsync("Attendance", [new FilterClause("employee", "=", emp.Name), new FilterClause("attendance_date", "=", D.Iso(date))], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) throw Errors.One("attendance_date", $"{emp.Name} already has attendance {dup.Name} on {D.Iso(date)}");
        await ClassifyAsync(m, emp, date, d, c.Messages, ct);
    }

    /// <summary>
    /// Fills schedule, working hours, late/early minutes and the attendance code of one employee-day into <paramref name="d"/>
    /// (the Attendance fields). Also used on a copy of the data to preview what an attendance correction would change.
    /// </summary>
    internal async Task ClassifyAsync(Masters m, Document emp, DateOnly date, Dictionary<string, object?> d, ICollection<string>? messages, CancellationToken ct)
    {
        var schedule = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name), new FilterClause("work_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();
        if (schedule is not null)
        {
            d["daily_schedule"] = schedule.Name;
            d["schedule_type"] = schedule.Data.Str("schedule_type");
            d["scheduled_start_datetime"] = schedule.Data.GetValueOrDefault("scheduled_start_datetime");
            d["scheduled_end_datetime"] = schedule.Data.GetValueOrDefault("scheduled_end_datetime");
            if (schedule.Data.Str("shift_type") is { } schedShift)
                d.SetIfEmpty("shift_type", schedShift);
            if (schedule.Data.Str("schedule_type") is "REST" or "HOLIDAY" && d.Str("status") == "Present")
                messages?.Add($"Scheduled {schedule.Data.Str("day_category")?.ToLowerInvariant() ?? "rest/holiday day"} on {D.Iso(date)}; attendance qualifies for rest-day or holiday overtime.");
        }

        if (schedule is null || schedule.Data.Str("schedule_type") == "WORK")
            d.SetIfEmpty("shift_type", emp.Data.Str("shift_type") ?? (await m.SingleAsync("HRSettings", ct)).Str("default_shift_type"));

        var shift = await m.GetAsync("ShiftType", d.Str("shift_type"), ct);
        if (shift is not null && d.GetValueOrDefault("scheduled_start_datetime") is null)
        {
            var (winStart, winEnd, _) = WsmTime.ComputeWindow(date, shift);
            if (winStart is not null) d["scheduled_start_datetime"] = winStart;
            if (winEnd is not null) d["scheduled_end_datetime"] = winEnd;
        }

        var inAt = LocalTime(d.GetValueOrDefault("check_in"));
        var outAt = LocalTime(d.GetValueOrDefault("check_out"));
        d["working_hours"] = inAt is { } wi && outAt is { } wo && wo > wi
            ? Math.Round((decimal)(wo - wi).TotalHours - (shift?.Data.Dec("break_minutes") ?? 0) / 60m, 2)
            : null;

        var leave = (await m.QueryAsync("LeaveApplication", [new FilterClause("employee", "=", emp.Name), new FilterClause("to_date", ">=", D.Iso(date))], ct, take: 500))
            .FirstOrDefault(l => l.WorkflowState == Approval.Approved && l.Data.Date("from_date") <= date && l.Data.Date("to_date") >= date);
        d["leave_application"] = leave?.Name;
        var leaveType = leave is null ? null : await m.GetAsync("LeaveType", leave.Data.Str("leave_type"), ct);

        var settings = await m.SingleAsync("HRSettings", ct);
        var isDayOff = schedule is null && await IsDayOffWithoutScheduleAsync(m, emp, settings, date, ct);
        var dayOffOrHoliday = schedule?.Data.Str("schedule_type") is "REST" or "HOLIDAY" || schedule?.Data.Bool("is_public_holiday") == true || isDayOff;
        var overtime = dayOffOrHoliday && (inAt is not null || outAt is not null)
            && (await m.QueryAsync("OvertimeRequest", [new FilterClause("employee", "=", emp.Name), new FilterClause("overtime_date", "=", D.Iso(date))], ct, take: 5))
                .Any(o => o.WorkflowState == Approval.Approved);

        var now = TimeZoneInfo.ConvertTime((clock ?? TimeProvider.System).GetUtcNow(), Framework.Metadata.FieldTypeSettings.DefaultTimeZone).DateTime;
        var result = AttendanceClassifier.Classify(new AttendanceDayInput
        {
            ScheduleType = schedule?.Data.Str("schedule_type") ?? (isDayOff ? "REST" : null),
            IsPublicHoliday = schedule?.Data.Bool("is_public_holiday") == true,
            HasShift = shift is not null,
            ScheduledStart = LocalTime(d.GetValueOrDefault("scheduled_start_datetime")),
            ScheduledEnd = LocalTime(d.GetValueOrDefault("scheduled_end_datetime")),
            CheckIn = inAt,
            CheckOut = outAt,
            LateGraceMinutes = (int)(shift?.Data.Dec("late_grace_minutes") ?? 0),
            EarlyOutGraceMinutes = (int)(shift?.Data.Dec("early_out_grace_minutes") ?? 0),
            FlaggedPunches = d.Str("ess_flags"),
            LeaveIsPaid = leave is null ? null : leaveType?.Data.Bool("is_paid") ?? true,
            LeaveIsHalfDay = leave?.Data.Bool("half_day") == true,
            LeaveType = leave?.Data.Str("leave_type"),
            HasApprovedOvertime = overtime,
            CurrentStatus = d.Str("status"),
            Now = now,
            FinalizeAfterHours = settings.ContainsKey("attendance_finalize_hours") ? (int)settings.Dec("attendance_finalize_hours") : 4,
            WindowHours = settings.ContainsKey("attendance_window_hours") ? (int)settings.Dec("attendance_window_hours") : 4,
        });
        d["late_minutes"] = (long)result.LateMinutes;
        d["early_out_minutes"] = (long)result.EarlyOutMinutes;
        if (d.Bool("code_manual"))
        {
            // HR decided this day: keep their code and status; the minutes above stay informative.
            d["code_final"] = true;
            d["code_reason"] = "Set by HR";
            return;
        }
        d["attendance_code"] = result.Code;
        d["code_final"] = result.Final;
        d["code_reason"] = result.Reason;
        if (result.Code is { } code) d["status"] = AttendanceCodes.StatusFor(code, d.Str("status"));
    }

    /// <summary>Without a daily schedule: weekly off of the employee's shift or holiday list, or a date in that holiday list.</summary>
    private static async Task<bool> IsDayOffWithoutScheduleAsync(Masters m, Document emp, IDictionary<string, object?> settings, DateOnly date, CancellationToken ct)
    {
        var hl = await m.GetAsync("HolidayList", emp.Data.Str("holiday_list") ?? settings.Str("default_holiday_list"), ct);
        if (hl?.Data.Rows("holidays").Any(h => h.Date("holiday_date") == date) == true) return true;
        return (await LeaveMath.WeeklyOffAsync(m, emp, hl, ct)).Contains(date.DayOfWeek);
    }

    internal static DateTime? LocalTime(object? stored) =>
        Framework.Metadata.DatetimeFieldType.ToZone(stored)?.DateTime
        ?? (stored is not null && DateTime.TryParse(Convert.ToString(stored, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null);

    public async Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.Date("attendance_date") is { } date)
            await Overtime.CheckNotPaidAsync(new Masters(store, c.Doc.TenantId), c.Doc.Data.Str("employee"), $"{date:yyyy-MM}", "attendance_date", ct);
    }
}

/// <summary>Overtime request: paid hours computed using PP 35/2021 multipliers; resolves day type &amp; work week from EmployeeDailySchedule when available.</summary>
public sealed class OvertimeRequestHook(IDocumentStore store, TimeProvider clock) : IBeforeValidate<OvertimeRequest>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        var date = d.Date("overtime_date") ?? throw Errors.One("overtime_date", "The date is required");
        if (c.Doc.DocStatus == DocStatus.Draft)
        {
            // Approver availability is checked for today (when the request is approved), not for the overtime date.
            var resolved = await Hr.ResolveApproversAsync(m, emp, d.Str("project"), DateOnly.FromDateTime(clock.GetLocalNow().Date), ct, "leave_approver");
            d["approver"] = resolved.LineApprover;
            d["project_approver"] = resolved.ProjectApprover;
        }
        var period = $"{date:yyyy-MM}";
        d["period"] = period;
        if (c.Doc.DocStatus == DocStatus.Draft) await Overtime.CheckNotPaidAsync(m, emp.Name, period, "overtime_date", ct);

        var schedule = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name), new FilterClause("work_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();

        var week = await Overtime.WorkWeekAsync(m, emp, ct, schedule);
        d["work_week_days"] = (long)week;

        if (c.Previous is null && schedule is not null
            && (schedule.Data.Str("schedule_type") is "REST" or "HOLIDAY" || schedule.Data.Bool("is_public_holiday"))
            && d.Str("day_type") is null or Overtime.WorkingDay)
        {
            d["day_type"] = Overtime.RestDay;
        }

        var dayType = d.Str("day_type") ?? Overtime.WorkingDay;
        if (dayType == Overtime.ShortestDay && week != 6)
            throw Errors.One("day_type", "'Public holiday on the shortest working day' only applies to a 6-day work week");
        var hours = d.Dec("hours");
        var (weighted, max) = Overtime.Weigh(dayType, week, hours);
        if (hours > max) throw Errors.One("hours", $"At most {max:0.#} overtime hours on a {dayType.ToLowerInvariant()} ({week}-day work week)");
        d["weighted_hours"] = weighted;

        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var others = (await m.QueryAsync("OvertimeRequest", [new FilterClause("employee", "=", emp.Name)], ct, take: 1000))
            .Where(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled && o.WorkflowState is not ("Rejected" or "Cancelled")
                        && o.Data.Date("overtime_date") is { } od && od >= monday && od <= monday.AddDays(6))
            .Sum(o => o.Data.Dec("hours"));
        if (others + hours > Overtime.MaxPerWeek)
            throw Errors.One("hours", $"Overtime would total {others + hours:0.#} hours in the week of {D.Iso(monday)}; the limit is {Overtime.MaxPerWeek:0} hours");
    }
}
