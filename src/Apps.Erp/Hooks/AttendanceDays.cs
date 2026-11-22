using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Hooks;

/// <summary>Monthly attendance recap of one employee (docs/guides/attendance-codes.md).</summary>
public sealed record AttendanceRecapRow(
    string Employee,
    string EmployeeName,
    string? OrganizationUnit,
    IReadOnlyDictionary<string, int> Codes,
    int LateDays,
    int LateMinutes,
    int EarlyOutDays,
    int EarlyOutMinutes,
    int UnclassifiedDays)
{
    public int Count(string code) => Codes.GetValueOrDefault(code);
}

/// <summary>
/// One Attendance record per employee per day, also for days without punches (ABS, DO, time off), and the monthly
/// recap of attendance codes for payroll.
/// </summary>
public static class AttendanceDays
{
    public sealed record EnsureResult(int Created, int Recalculated, int Failed);

    /// <summary>
    /// For every employee employed on a date in [from, to]: creates the missing Attendance records, and re-saves
    /// records whose code may change (not final and past the deadline, or every record without "Code set by HR" when
    /// <paramref name="recalculateAll"/>, e.g. after leave was approved for past days). Days of a month whose payslip
    /// is submitted are left alone (the Attendance hook refuses them).
    /// </summary>
    internal static async Task<EnsureResult> EnsureAsync(DocumentService docs, IDocumentStore store, string tenant, DateOnly from, DateOnly to,
        bool recalculateAll, TimeProvider clock, ILogger? log, CancellationToken ct, string? employee = null)
    {
        var m = new Masters(store, tenant);
        var sys = new SystemDocs(docs, tenant);
        var employees = (await m.QueryAsync("Employee", employee is null ? [] : [new FilterClause(StandardFields.Name, "=", employee)], ct, take: 20000))
            // Active employees, and those who left during the period (until their separation date). Employees on unpaid
            // leave or suspension get no automatic records (HR enters them if needed).
            .Where(e => e.Data.Str("status") is null or "Active" || (e.Data.Str("status") == "Left" && e.Data.Date("separation_date") is { } sep && sep >= from))
            .ToList();
        var existing = (await m.QueryAsync("Attendance",
                [new FilterClause("attendance_date", ">=", D.Iso(from)), new FilterClause("attendance_date", "<=", D.Iso(to))], ct, take: 200000))
            .Where(a => employee is null || a.Data.Str("employee") == employee)
            .GroupBy(a => (a.Data.Str("employee") ?? "", a.Data.Date("attendance_date") ?? default))
            .ToDictionary(g => g.Key, g => g.First());
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), FieldTypeSettings.DefaultTimeZone).DateTime;
        var settings = await m.SingleAsync("HRSettings", ct);
        var finalizeHours = settings.ContainsKey("attendance_finalize_hours") ? (int)settings.Dec("attendance_finalize_hours") : 4;

        int created = 0, recalculated = 0, failed = 0;
        foreach (var emp in employees)
        {
            var hired = emp.Data.Date("hire_date") ?? DateOnly.MinValue;
            var left = emp.Data.Date("separation_date") ?? DateOnly.MaxValue;
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                if (day < hired || day > left) continue;
                try
                {
                    if (!existing.TryGetValue((emp.Name, day), out var att))
                    {
                        await sys.InsertAsync("Attendance", new Dictionary<string, object?>
                        {
                            ["employee"] = emp.Name, ["attendance_date"] = D.Iso(day), ["source"] = "System", ["status"] = "Present",
                        }, "Daily attendance from schedule", ct);
                        created++;
                        continue;
                    }
                    if (att.Data.Bool("code_manual")) continue;
                    var due = recalculateAll || (!att.Data.Bool("code_final") && IsPastDeadline(att, day, now, finalizeHours));
                    if (!due) continue;
                    await sys.TouchAsync("Attendance", att.Name, ct);
                    recalculated++;
                }
                catch (Exception ex) when (ex is ValidationFailedException or ConcurrencyException)
                {
                    failed++; // payroll month locked, or edited concurrently: retried on the next run
                    log?.LogDebug("Attendance {Employee} {Date} skipped: {Error}", emp.Name, day, ex.Message);
                }
            }
        }
        return new EnsureResult(created, recalculated, failed);
    }

    private static bool IsPastDeadline(Document att, DateOnly day, DateTime now, int finalizeHours)
    {
        var end = AttendanceHook.LocalTime(att.Data.GetValueOrDefault("scheduled_end_datetime")) ?? day.ToDateTime(new TimeOnly(23, 59));
        return now >= end.AddHours(finalizeHours);
    }

    /// <summary>
    /// Counts per employee for [from, to]: days per code (LI/LO counts as late and as early out), late and early-out
    /// minutes, and days still without a final code (no record, or record waiting for punches). Only reads.
    /// </summary>
    internal static async Task<IReadOnlyList<AttendanceRecapRow>> RecapAsync(IDocumentStore store, string tenant, DateOnly from, DateOnly to,
        CancellationToken ct, string? employee = null, string? organizationUnit = null, DateOnly? today = null)
    {
        var m = new Masters(store, tenant);
        var filters = new List<FilterClause>();
        if (employee is not null) filters.Add(new FilterClause(StandardFields.Name, "=", employee));
        if (organizationUnit is not null) filters.Add(new FilterClause("organization_unit", "=", organizationUnit));
        var employees = await m.QueryAsync("Employee", filters, ct, "full_name", "asc", 20000);
        var attFilters = new List<FilterClause> { new("attendance_date", ">=", D.Iso(from)), new("attendance_date", "<=", D.Iso(to)) };
        if (employee is not null) attFilters.Add(new FilterClause("employee", "=", employee));
        var byEmployee = (await m.QueryAsync("Attendance", attFilters, ct, take: 400000))
            .GroupBy(a => a.Data.Str("employee") ?? "").ToDictionary(g => g.Key, g => g.ToList());
        var last = today is { } t && t < to ? t : to;

        var rows = new List<AttendanceRecapRow>();
        foreach (var emp in employees)
        {
            var hired = emp.Data.Date("hire_date") ?? DateOnly.MinValue;
            var left = emp.Data.Date("separation_date") ?? DateOnly.MaxValue;
            var records = byEmployee.GetValueOrDefault(emp.Name) ?? [];
            if (records.Count == 0 && (hired > to || left < from)) continue;

            var codes = AttendanceCodes.All.ToDictionary(c => c, _ => 0);
            int lateDays = 0, lateMin = 0, earlyDays = 0, earlyMin = 0, unclassified = 0;
            var seen = new HashSet<DateOnly>();
            foreach (var a in records)
            {
                if (a.Data.Date("attendance_date") is { } ad) seen.Add(ad);
                var code = a.Data.Str("attendance_code");
                if (code is null || (!a.Data.Bool("code_final") && !a.Data.Bool("code_manual"))) { unclassified++; continue; }
                if (codes.ContainsKey(code)) codes[code]++;
                if (AttendanceCodes.IsLate(code)) { lateDays++; lateMin += (int)a.Data.Dec("late_minutes"); }
                if (AttendanceCodes.IsEarlyOut(code)) { earlyDays++; earlyMin += (int)a.Data.Dec("early_out_minutes"); }
            }
            for (var day = from > hired ? from : hired; day <= last && day <= left; day = day.AddDays(1))
                if (!seen.Contains(day)) unclassified++;
            rows.Add(new AttendanceRecapRow(emp.Name, emp.Data.Str("full_name") ?? emp.Name, emp.Data.Str("organization_unit"),
                codes, lateDays, lateMin, earlyDays, earlyMin, unclassified));
        }
        return rows;
    }
}

/// <summary>
/// Every hour: creates the daily Attendance of the last days (HR Settings › Attendance codes) for every employee,
/// including days without punches, and finalizes codes whose deadline has passed (ABS, NCIN, NCOUT).
/// </summary>
public sealed class AttendanceDailyJob : IScheduledJob
{
    public string Name => "Erp.AttendanceDaily";
    public string Schedule => "@hourly";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var clock = context.Services.GetService<TimeProvider>() ?? TimeProvider.System;
        var log = context.Services.GetService<ILoggerFactory>()?.CreateLogger<AttendanceDailyJob>();
        var m = new Masters(store, context.TenantId);
        var settings = await m.SingleAsync("HRSettings", ct);
        var days = settings.ContainsKey("attendance_backfill_days") ? Math.Clamp((int)settings.Dec("attendance_backfill_days"), 1, 62) : 7;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), FieldTypeSettings.DefaultTimeZone).DateTime);
        // Today is included so day-off and leave records exist; working days stay "waiting" until their deadline.
        var r = await AttendanceDays.EnsureAsync(context.Docs, store, context.TenantId, today.AddDays(-days), today, false, clock, log, ct);
        if (r.Created + r.Recalculated + r.Failed > 0)
            log?.LogInformation("Daily attendance: {Created} created, {Recalculated} finalized, {Failed} skipped", r.Created, r.Recalculated, r.Failed);
    }
}
