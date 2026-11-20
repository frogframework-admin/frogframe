using System.Globalization;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

internal static class WsmTime
{
    public static (string? StartUtc, string? EndUtc, bool CrossesMidnight) ComputeWindow(DateOnly workDate, Document? shift)
    {
        if (shift is null) return (null, null, false);
        var stStr = shift.Data.Str("start_time");
        var etStr = shift.Data.Str("end_time");
        if (stStr is null || etStr is null) return (null, null, false);
        if (!TimeOnly.TryParse(stStr, CultureInfo.InvariantCulture, out var start)
            || !TimeOnly.TryParse(etStr, CultureInfo.InvariantCulture, out var end))
            return (null, null, false);

        var crosses = shift.Data.Bool("is_night_shift") || end <= start;
        var localStart = workDate.ToDateTime(start, DateTimeKind.Unspecified);
        var localEnd = (crosses ? workDate.AddDays(1) : workDate).ToDateTime(end, DateTimeKind.Unspecified);
        var zone = FieldTypeSettings.DefaultTimeZone;
        var startUtc = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
        var endUtc = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
        return (startUtc, endUtc, crosses);
    }

    public static bool IsMorningShift(Document? shift) =>
        shift?.Data.Str("start_time") is { } st
        && TimeOnly.TryParse(st, CultureInfo.InvariantCulture, out var start)
        && start >= new TimeOnly(4, 0) && start < new TimeOnly(12, 0)
        && !shift.Data.Bool("is_night_shift");

    public static bool IsNightShift(Document? shift)
    {
        if (shift is null) return false;
        if (shift.Data.Bool("is_night_shift")) return true;
        return shift.Data.Str("start_time") is { } st && shift.Data.Str("end_time") is { } et
            && TimeOnly.TryParse(st, CultureInfo.InvariantCulture, out var s)
            && TimeOnly.TryParse(et, CultureInfo.InvariantCulture, out var e)
            && e <= s;
    }
}

/// <summary>Shift Group: operational work group/squad with effective-dated member assignments.</summary>
public sealed class ShiftGroupHook(IDocumentStore store) : IBeforeValidate<ShiftGroup>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var rows = d.Rows("members");
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var emp = r.Str("employee");
            if (emp is not null) await m.RequireAsync("Employee", emp, $"members[{i}].employee", "Employee", ct);
            var from = r.Date("effective_from");
            var to = r.Date("effective_to");
            if (from is not null && to is not null && to < from)
                errors.Add($"members[{i}].effective_to", "The effective end date must be on or after the effective start date");

            if (!r.Bool("is_active") || emp is null) continue;
            for (var j = 0; j < i; j++)
            {
                var prev = rows[j];
                if (!prev.Bool("is_active") || prev.Str("employee") != emp) continue;
                if (Hr.Overlaps(from, to, prev.Date("effective_from"), prev.Date("effective_to")))
                {
                    errors.Add($"members[{i}].employee", $"Employee {emp} has overlapping active membership in this group");
                    break;
                }
            }
        }
        errors.ThrowIfAny();
    }
}

/// <summary>Shift Pattern: versioned N-day rotational work cycle template; validates sequence completeness and version date overlaps.</summary>
public sealed class ShiftPatternHook(IDocumentStore store) : IBeforeValidate<ShiftPattern>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var code = d.Str("pattern_code") ?? c.Doc.Name;
        d.SetIfEmpty("base_pattern_code", code);
        var baseCode = d.Str("base_pattern_code") ?? code;

        var status = d.Str("status") ?? "Active";
        d["is_active"] = status == "Active";

        var cycleDays = (int)d.Dec("cycle_days");
        var items = d.Rows("items");
        if (items.Count != cycleDays)
            errors.Add("items", $"The cycle sequence has {items.Count} row(s), which must equal the cycle length of {cycleDays} day(s)");

        var seenDays = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var r = items[i];
            var dayNum = (int)r.Dec("day_number");
            if (dayNum <= 0)
            {
                dayNum = i + 1;
                r["day_number"] = (long)dayNum;
            }
            if (dayNum < 1 || dayNum > cycleDays)
                errors.Add($"items[{i}].day_number", $"Day in cycle must be between 1 and {cycleDays}");
            else if (!seenDays.Add(dayNum))
                errors.Add($"items[{i}].day_number", $"Day {dayNum} is defined more than once in the cycle");

            var st = r.Str("schedule_type") ?? "WORK";
            if (st == "REST")
            {
                r["shift_type"] = null;
            }
            else if (r.Str("shift_type") is null)
            {
                errors.Add($"items[{i}].shift_type", "Shift type is required for a WORK day");
            }
        }

        if (status == "Active")
        {
            var from = d.Date("effective_from");
            var to = d.Date("effective_to");
            var others = await m.QueryAsync("ShiftPattern", [new FilterClause("base_pattern_code", "=", baseCode)], ct, take: 100);
            foreach (var other in others.Where(p => p.Name != code && p.Name != c.Doc.Name && p.Data.Str("status") == "Active"))
            {
                if (Hr.Overlaps(from, to, other.Data.Date("effective_from"), other.Data.Date("effective_to")))
                {
                    errors.Add("effective_from", $"Active pattern version overlaps with {other.Name} for canonical pattern {baseCode}");
                    break;
                }
            }
        }

        errors.ThrowIfAny();
    }
}

/// <summary>Shift Assignment: links a Shift Pattern to a Shift Group, Employee, or Organization Unit with effective dates and cycle offset.</summary>
public sealed class ShiftAssignmentHook(IDocumentStore store) : IBeforeValidate<ShiftAssignment>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var target = d.Str("assignment_target") ?? "Shift Group";
        var pattern = await m.RequireAsync("ShiftPattern", d.Str("shift_pattern"), "shift_pattern", "Shift pattern", ct);

        if (target == "Shift Group")
        {
            d["employee"] = null;
            d["organization_unit"] = null;
        }
        else if (target == "Employee")
        {
            d["shift_group"] = null;
            d["organization_unit"] = null;
        }
        else if (target == "Organization Unit")
        {
            d["shift_group"] = null;
            d["employee"] = null;
        }

        var cycleDays = (int)pattern.Data.Dec("cycle_days");
        var offset = (int)d.Dec("day_offset");
        if (cycleDays > 0 && offset >= cycleDays)
            d["day_offset"] = (long)(offset % cycleDays);

        if (d.Bool("is_active"))
        {
            var start = d.Date("start_date");
            var end = d.Date("end_date");
            var targetField = target switch
            {
                "Employee" => "employee",
                "Organization Unit" => "organization_unit",
                _ => "shift_group",
            };
            var targetVal = d.Str(targetField);
            if (targetVal is not null)
            {
                var existing = await m.QueryAsync("ShiftAssignment",
                    [new FilterClause("assignment_target", "=", target), new FilterClause(targetField, "=", targetVal), new FilterClause("is_active", "=", true)], ct, take: 100);
                foreach (var other in existing.Where(x => x.Name != c.Doc.Name))
                {
                    if (Hr.Overlaps(start, end, other.Data.Date("start_date"), other.Data.Date("end_date")))
                    {
                        errors.Add("start_date", $"Overlaps with active shift assignment {other.Name} for {targetVal}");
                        break;
                    }
                }
            }
        }

        errors.ThrowIfAny();
    }
}

/// <summary>Employee Daily Schedule: materialized daily schedule slot per employee and work date; computes cross-midnight datetime windows.</summary>
public sealed class EmployeeDailyScheduleHook(IDocumentStore store)
    : IBeforeValidate<EmployeeDailySchedule>, IBeforeDelete<EmployeeDailySchedule>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        var date = d.Date("work_date") ?? throw Errors.One("work_date", "The work date is required");
        await Overtime.CheckNotPaidAsync(m, emp.Name, $"{date:yyyy-MM}", "work_date", ct);

        var dup = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name), new FilterClause("work_date", "=", D.Iso(date))], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null)
            throw Errors.One("work_date", $"{emp.Name} already has daily schedule {dup.Name} on {D.Iso(date)}");

        d.SetIfEmpty("company", emp.Data.Str("company"));
        if (d.Str("organization_unit") is null)
        {
            var unit = await Hr.GetOrgUnitAsOfAsync(m, emp.Data.Str("organization_unit"), date, ct);
            d["organization_unit"] = unit?.Name ?? emp.Data.Str("organization_unit");
        }

        var scheduleType = d.Str("schedule_type") ?? "WORK";
        if (scheduleType == "WORK")
        {
            d.SetIfEmpty("day_category", "Scheduled Workday");
            var shift = await m.RequireAsync("ShiftType", d.Str("shift_type"), "shift_type", "Shift type", ct);
            var (startUtc, endUtc, crosses) = WsmTime.ComputeWindow(date, shift);
            d["scheduled_start_datetime"] = startUtc;
            d["scheduled_end_datetime"] = endUtc;
            d["crosses_midnight"] = crosses;
        }
        else
        {
            d["shift_type"] = null;
            d["scheduled_start_datetime"] = null;
            d["scheduled_end_datetime"] = null;
            d["crosses_midnight"] = false;
            if (scheduleType == "REST" && d.Str("day_category") is null or "Scheduled Workday")
                d["day_category"] = "Scheduled Rest Day";
            else if (scheduleType == "HOLIDAY" && d.Str("day_category") is null or "Scheduled Workday" or "Scheduled Rest Day")
                d["day_category"] = "Public Holiday";
            else if (scheduleType == "LEAVE" && d.Str("day_category") is null or "Scheduled Workday" or "Scheduled Rest Day")
                d["day_category"] = "Annual Leave";
        }
    }

    public async Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.Date("work_date") is { } date)
            await Overtime.CheckNotPaidAsync(new Masters(store, c.Doc.TenantId), c.Doc.Data.Str("employee"), $"{date:yyyy-MM}", "work_date", ct);
    }
}

/// <summary>Workforce Constraint Engine: evaluates WorkScheduleRule rules (consecutive work days, rest hours between shifts, night-to-morning).</summary>
internal static class WsmConstraintEngine
{
    public sealed record Violation(string RuleCode, string EnforcementMode, DateOnly Date, string Message)
    {
        public bool IsStrict => EnforcementMode == "Strict";
    }

    public sealed record ProposedSlot(DateOnly WorkDate, string ScheduleType, string? ShiftType);

    public static async Task<IReadOnlyList<Document>> RulesForEmployeeAsync(Masters m, Document emp, DateOnly asOfDate, CancellationToken ct)
    {
        var allRules = (await m.QueryAsync("WorkScheduleRule", [new FilterClause("is_active", "=", true)], ct, take: 200)).ToList();
        if (allRules.Count == 0) return [];
        var empCompany = emp.Data.Str("company");
        var empUnit = (await Hr.GetOrgUnitAsOfAsync(m, emp.Data.Str("organization_unit"), asOfDate, ct))?.Name ?? emp.Data.Str("organization_unit");
        var empUnitDoc = await m.GetAsync("OrganizationUnit", empUnit, ct);
        var empPath = empUnitDoc?.Data.Str("materialized_path") ?? $"/{empUnit}/";

        return allRules.Where(r =>
        {
            var rc = r.Data.Str("company");
            if (rc is not null && rc != empCompany) return false;
            var ru = r.Data.Str("organization_unit");
            if (ru is null) return true;
            return ru == empUnit || empPath.Contains($"/{ru}/", StringComparison.Ordinal);
        }).ToList();
    }

    public static async Task<IReadOnlyList<Violation>> EvaluateAsync(
        Masters m,
        Document emp,
        IReadOnlyList<ProposedSlot> proposed,
        CancellationToken ct)
    {
        if (proposed.Count == 0) return [];
        var minDate = proposed.Min(p => p.WorkDate);
        var maxDate = proposed.Max(p => p.WorkDate);
        var rules = await RulesForEmployeeAsync(m, emp, minDate, ct);
        var maxConsecutiveLimit = rules.Count > 0 ? rules.Min(r => (int)r.Data.Dec("max_consecutive_work_days")) : 6;
        var lookback = Math.Max(maxConsecutiveLimit + 2, 10);
        var windowFrom = minDate.AddDays(-lookback);
        var windowTo = maxDate.AddDays(lookback);

        var existing = await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name),
             new FilterClause("work_date", ">=", D.Iso(windowFrom)),
             new FilterClause("work_date", "<=", D.Iso(windowTo))], ct, "work_date", "asc", 500);

        var slots = existing
            .Where(s => s.Data.Date("work_date") is not null)
            .ToDictionary(
                s => s.Data.Date("work_date")!.Value,
                s => new ProposedSlot(s.Data.Date("work_date")!.Value, s.Data.Str("schedule_type") ?? "WORK", s.Data.Str("shift_type")));

        foreach (var p in proposed)
            slots[p.WorkDate] = p;

        if (rules.Count == 0)
        {
            // Default built-in statutory/operational rule when no WorkScheduleRule document exists yet
            rules =
            [
                new Document
                {
                    Name = "WorkScheduleRule",
                    DocType = "WorkScheduleRule",
                    TenantId = m.Tenant,
                    Owner = "system",
                    Data = new Dictionary<string, object?>
                    {
                        ["rule_code"] = "WorkScheduleRule",
                        ["max_consecutive_work_days"] = 6L,
                        ["min_rest_hours_between_shifts"] = 11m,
                        ["forbid_night_to_morning"] = true,
                        ["enforcement_mode"] = "Strict",
                    }
                }
            ];
        }

        var violations = new List<Violation>();
        var orderedDates = slots.Keys.OrderBy(d => d).ToList();

        foreach (var rule in rules)
        {
            var code = rule.Data.Str("rule_code") ?? rule.Name;
            var mode = rule.Data.Str("enforcement_mode") ?? "Strict";
            var maxDays = (int)rule.Data.Dec("max_consecutive_work_days");
            var minRest = rule.Data.Dec("min_rest_hours_between_shifts");
            var forbidNightToMorning = rule.Data.Bool("forbid_night_to_morning");

            // 1. Consecutive working days check
            int streak = 0;
            DateOnly? prevDay = null;
            foreach (var date in orderedDates)
            {
                var slot = slots[date];
                if (slot.ScheduleType == "WORK")
                {
                    streak = (prevDay is not null && date == prevDay.Value.AddDays(1)) ? streak + 1 : 1;
                    prevDay = date;
                    if (maxDays > 0 && streak > maxDays && proposed.Any(p => Math.Abs(p.WorkDate.DayNumber - date.DayNumber) <= maxDays))
                    {
                        violations.Add(new Violation(code, mode, date,
                            $"Employee {emp.Name} would work {streak} consecutive days on {D.Iso(date)}, exceeding the limit of {maxDays} consecutive working days ({code})"));
                    }
                }
                else
                {
                    streak = 0;
                    prevDay = null;
                }
            }

            // 2. Minimum rest hours between consecutive days & Night-to-Morning check
            for (var i = 1; i < orderedDates.Count; i++)
            {
                var d1 = orderedDates[i - 1];
                var d2 = orderedDates[i];
                if (d2 != d1.AddDays(1)) continue;
                var s1 = slots[d1];
                var s2 = slots[d2];
                if (s1.ScheduleType != "WORK" || s2.ScheduleType != "WORK") continue;
                if (!proposed.Any(p => p.WorkDate == d1 || p.WorkDate == d2)) continue;

                var shift1 = await m.GetAsync("ShiftType", s1.ShiftType, ct);
                var shift2 = await m.GetAsync("ShiftType", s2.ShiftType, ct);
                if (shift1 is null || shift2 is null) continue;

                if (forbidNightToMorning && WsmTime.IsNightShift(shift1) && WsmTime.IsMorningShift(shift2))
                {
                    violations.Add(new Violation(code, mode, d2,
                        $"Employee {emp.Name} cannot be scheduled for morning shift '{shift2.Name}' on {D.Iso(d2)} immediately after night shift '{shift1.Name}' on {D.Iso(d1)} ({code})"));
                }

                if (minRest > 0)
                {
                    var (_, end1Utc, _) = WsmTime.ComputeWindow(d1, shift1);
                    var (start2Utc, _, _) = WsmTime.ComputeWindow(d2, shift2);
                    if (AttendanceHook.LocalTime(end1Utc) is { } end1 && AttendanceHook.LocalTime(start2Utc) is { } start2)
                    {
                        var restHours = (decimal)(start2 - end1).TotalHours;
                        if (restHours < minRest)
                        {
                            violations.Add(new Violation(code, mode, d2,
                                $"Employee {emp.Name} has only {restHours:0.#} hours of rest between '{shift1.Name}' ({D.Iso(d1)}) and '{shift2.Name}' ({D.Iso(d2)}), below the minimum of {minRest:0.#} hours ({code})"));
                        }
                    }
                }
            }
        }

        return violations;
    }
}

/// <summary>Materialized schedule generator: resolves ShiftAssignment, ShiftPattern version, HolidayList, and LeaveApplication to persist EmployeeDailySchedule rows.</summary>
internal static class WsmScheduleGenerator
{
    public sealed record GenerationResult(int GeneratedCount, int SkippedLockedCount, int ViolationCount, string Log);

    public static async Task<GenerationResult> GenerateAsync(
        IDocumentStore store,
        string tenant,
        string company,
        string? organizationUnit,
        string? shiftGroup,
        string? employee,
        DateOnly fromDate,
        DateOnly toDate,
        bool overwriteUnlocked,
        string sourceReference,
        CancellationToken ct)
    {
        var m = new Masters(store, tenant);
        var hs = await m.SingleAsync("HRSettings", ct);
        var companyDoc = await m.GetAsync("Company", company, ct);

        // 1. Resolve target employees
        List<Document> targetEmployees;
        if (!string.IsNullOrEmpty(employee))
        {
            var e = await m.GetAsync("Employee", employee, ct);
            targetEmployees = e is not null && e.Data.Str("status") != Hr.Left ? [e] : [];
        }
        else if (!string.IsNullOrEmpty(organizationUnit))
        {
            targetEmployees = (await Hr.EmployeesInSubtreeAsOfAsync(m, organizationUnit, fromDate, ct))
                .Where(e => e.Data.Str("company") == company).ToList();
        }
        else
        {
            targetEmployees = (await m.QueryAsync("Employee", [new FilterClause("company", "=", company)], ct, take: 10_000))
                .Where(e => e.Data.Str("status") != Hr.Left).ToList();
        }

        var allGroups = (await m.QueryAsync("ShiftGroup", [new FilterClause("company", "=", company), new FilterClause("is_active", "=", true)], ct, take: 500)).ToList();
        if (!string.IsNullOrEmpty(shiftGroup))
        {
            var grp = allGroups.FirstOrDefault(g => g.Name == shiftGroup);
            var memberIds = grp?.Data.Rows("members").Where(r => r.Bool("is_active")).Select(r => r.Str("employee")).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            targetEmployees = targetEmployees.Where(e => memberIds.Contains(e.Name)).ToList();
        }

        var assignments = (await m.QueryAsync("ShiftAssignment", [new FilterClause("company", "=", company), new FilterClause("is_active", "=", true)], ct, take: 2000)).ToList();
        var patterns = (await m.QueryAsync("ShiftPattern", [], ct, take: 500)).ToList();
        var shifts = (await m.QueryAsync("ShiftType", [], ct, take: 500)).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var approvedLeaves = (await m.QueryAsync("LeaveApplication", [], ct, take: 5000))
            .Where(l => l.WorkflowState == Approval.Approved && Hr.Overlaps(fromDate, toDate, l.Data.Date("from_date"), l.Data.Date("to_date")))
            .GroupBy(l => l.Data.Str("employee") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        int generated = 0, skipped = 0;
        var allViolations = new List<string>();
        var uow = new UnitOfWork();
        var now = DateTimeOffset.UtcNow;

        foreach (var emp in targetEmployees)
        {
            var empHire = emp.Data.Date("hire_date");
            var empSep = emp.Data.Date("separation_date");
            var hlName = emp.Data.Str("holiday_list") ?? hs.Str("default_holiday_list") ?? companyDoc?.Data.Str("default_holiday_list");
            var hl = await m.GetAsync("HolidayList", hlName, ct);
            var holidaysByDate = hl?.Data.Rows("holidays")
                .Where(h => h.Date("holiday_date") is not null)
                .GroupBy(h => h.Date("holiday_date")!.Value)
                .ToDictionary(g => g.Key, g => g.First().Str("holiday_type") ?? "Public holiday")
                ?? new Dictionary<DateOnly, string>();
            var weeklyOff = await LeaveMath.WeeklyOffAsync(m, emp, hl, ct);

            var existingRows = (await m.QueryAsync("EmployeeDailySchedule",
                [new FilterClause("employee", "=", emp.Name),
                 new FilterClause("work_date", ">=", D.Iso(fromDate)),
                 new FilterClause("work_date", "<=", D.Iso(toDate))], ct, take: 500))
                .Where(s => s.Data.Date("work_date") is not null)
                .ToDictionary(s => s.Data.Date("work_date")!.Value, s => s);

            var empProposals = new List<WsmConstraintEngine.ProposedSlot>();

            for (var day = fromDate; day <= toDate; day = day.AddDays(1))
            {
                if (empHire is not null && day < empHire.Value) continue;
                if (empSep is not null && day > empSep.Value) continue;

                if (existingRows.TryGetValue(day, out var existing))
                {
                    var srcType = existing.Data.Str("source_type");
                    if (existing.Data.Bool("is_locked") || !overwriteUnlocked || srcType is "Override" or "Shift Swap" or "Leave")
                    {
                        skipped++;
                        continue;
                    }
                }

                // Resolve employee's active shift group on this day
                string? activeGroup = null;
                foreach (var g in allGroups)
                {
                    if (!Hr.Overlaps(day, day, g.Data.Date("effective_from"), g.Data.Date("effective_to"))) continue;
                    var isMember = g.Data.Rows("members").Any(r =>
                        r.Bool("is_active") && r.Str("employee") == emp.Name
                        && Hr.Overlaps(day, day, r.Date("effective_from"), r.Date("effective_to")));
                    if (isMember) { activeGroup = g.Name; break; }
                }

                var empUnitOnDay = (await Hr.GetOrgUnitAsOfAsync(m, emp.Data.Str("organization_unit"), day, ct))?.Name ?? emp.Data.Str("organization_unit");

                // Resolve active ShiftAssignment (priority: 1. Employee, 2. Shift Group, 3. Organization Unit)
                var activeAssignment = assignments
                    .Where(a => Hr.Overlaps(day, day, a.Data.Date("start_date"), a.Data.Date("end_date")))
                    .OrderBy(a => a.Data.Str("assignment_target") switch
                    {
                        "Employee" => 1,
                        "Shift Group" => 2,
                        _ => 3,
                    })
                    .FirstOrDefault(a => a.Data.Str("assignment_target") switch
                    {
                        "Employee" => a.Data.Str("employee") == emp.Name,
                        "Shift Group" => activeGroup is not null && a.Data.Str("shift_group") == activeGroup,
                        "Organization Unit" => empUnitOnDay is not null && a.Data.Str("organization_unit") == empUnitOnDay,
                        _ => false,
                    });

                var isPublicHoliday = holidaysByDate.TryGetValue(day, out var holidayKind) && holidayKind != "Weekend";
                var empLeave = approvedLeaves.TryGetValue(emp.Name, out var empLeaveList)
                    ? empLeaveList.FirstOrDefault(l => Hr.Overlaps(day, day, l.Data.Date("from_date"), l.Data.Date("to_date")))
                    : null;

                string scheduleType;
                string dayCategory;
                string? shiftType = null;
                string? patternName = null;
                string rowSourceType = "Generator";
                string rowSourceRef = sourceReference;

                if (empLeave is not null)
                {
                    var lt = await m.GetAsync("LeaveType", empLeave.Data.Str("leave_type"), ct);
                    scheduleType = "LEAVE";
                    dayCategory = lt?.Data.Bool("is_annual_quota") == false ? "Special Leave" : "Annual Leave";
                    rowSourceType = "Leave";
                    rowSourceRef = empLeave.Name;
                }
                else if (activeAssignment is not null)
                {
                    var reqPatCode = activeAssignment.Data.Str("shift_pattern");
                    var directPat = patterns.FirstOrDefault(p => p.Name == reqPatCode);
                    var basePatCode = directPat?.Data.Str("base_pattern_code") ?? reqPatCode;
                    var validPat = patterns
                        .Where(p => (p.Data.Str("base_pattern_code") ?? p.Name) == basePatCode && p.Data.Str("status") is null or "Active")
                        .Where(p => Hr.Overlaps(day, day, p.Data.Date("effective_from"), p.Data.Date("effective_to")))
                        .OrderByDescending(p => p.Data.Date("effective_from"))
                        .FirstOrDefault() ?? directPat;

                    patternName = validPat?.Name;
                    var cycleDays = Math.Max(1, (int)(validPat?.Data.Dec("cycle_days") ?? 1));
                    var startDate = activeAssignment.Data.Date("start_date") ?? day;
                    var offset = (int)activeAssignment.Data.Dec("day_offset");
                    var dayIndex = (((day.DayNumber - startDate.DayNumber) + offset) % cycleDays + cycleDays) % cycleDays + 1;
                    var item = validPat?.Data.Rows("items").FirstOrDefault(r => (int)r.Dec("day_number") == dayIndex);

                    if (isPublicHoliday && activeAssignment.Data.Bool("respect_public_holidays"))
                    {
                        scheduleType = "HOLIDAY";
                        dayCategory = holidayKind == "Company holiday" ? "Company Holiday" : "Public Holiday";
                    }
                    else if (item is null || item.Str("schedule_type") == "REST")
                    {
                        scheduleType = "REST";
                        dayCategory = "Scheduled Rest Day";
                    }
                    else
                    {
                        scheduleType = "WORK";
                        dayCategory = "Scheduled Workday";
                        shiftType = item.Str("shift_type");
                    }
                }
                else
                {
                    if (isPublicHoliday)
                    {
                        scheduleType = "HOLIDAY";
                        dayCategory = holidayKind == "Company holiday" ? "Company Holiday" : "Public Holiday";
                    }
                    else if (weeklyOff.Contains(day.DayOfWeek))
                    {
                        scheduleType = "REST";
                        dayCategory = "Scheduled Rest Day";
                    }
                    else
                    {
                        scheduleType = "WORK";
                        dayCategory = "Scheduled Workday";
                        shiftType = emp.Data.Str("shift_type") ?? hs.Str("default_shift_type");
                    }
                }

                shifts.TryGetValue(shiftType ?? "", out var shiftDoc);
                var (startUtc, endUtc, crosses) = scheduleType == "WORK" ? WsmTime.ComputeWindow(day, shiftDoc) : (null, null, false);

                var data = new Dictionary<string, object?>
                {
                    ["employee"] = emp.Name,
                    ["employee_name"] = emp.Data.Str("full_name"),
                    ["company"] = company,
                    ["organization_unit"] = empUnitOnDay,
                    ["shift_group"] = activeGroup,
                    ["work_date"] = D.Iso(day),
                    ["schedule_type"] = scheduleType,
                    ["day_category"] = dayCategory,
                    ["shift_type"] = shiftType,
                    ["is_public_holiday"] = isPublicHoliday,
                    ["scheduled_start_datetime"] = startUtc,
                    ["scheduled_end_datetime"] = endUtc,
                    ["crosses_midnight"] = crosses,
                    ["source_type"] = rowSourceType,
                    ["source_reference"] = rowSourceRef,
                    ["shift_pattern"] = patternName,
                    ["is_locked"] = false,
                };

                if (existingRows.TryGetValue(day, out var existingDoc))
                {
                    var clone = existingDoc.Clone();
                    foreach (var (k, v) in data) clone.Data[k] = v;
                    clone.Version = existingDoc.Version + 1;
                    clone.Modified = now;
                    uow.Upserts.Add((clone, existingDoc.Version));
                }
                else
                {
                    var docName = await store.NextNameAsync(tenant, "EmployeeDailySchedule", "EDS-{YYYY}-{#######}", now, ct);
                    uow.Upserts.Add((new Document
                    {
                        TenantId = tenant,
                        DocType = "EmployeeDailySchedule",
                        Name = docName,
                        Owner = "system",
                        ModifiedBy = "system",
                        Created = now,
                        Modified = now,
                        Version = 1,
                        SchemaVersion = 1,
                        DocStatus = DocStatus.Draft,
                        Data = data,
                    }, null));
                }
                generated++;
                empProposals.Add(new WsmConstraintEngine.ProposedSlot(day, scheduleType, shiftType));
            }

            if (empProposals.Count > 0)
            {
                var empViolations = await WsmConstraintEngine.EvaluateAsync(m, emp, empProposals, ct);
                foreach (var v in empViolations)
                    allViolations.Add(v.Message);
            }
        }

        if (uow.Upserts.Count > 0) await store.CommitAsync(uow, ct);
        var distinctViolations = allViolations.Distinct(StringComparer.Ordinal).ToList();
        var summary = $"Generated {generated} schedule slot(s) across {targetEmployees.Count} employee(s); {skipped} protected slot(s) skipped.";
        if (distinctViolations.Count > 0)
            summary += "\nConstraint warnings:\n- " + string.Join("\n- ", distinctViolations.Take(25));
        return new GenerationResult(generated, skipped, distinctViolations.Count, summary);
    }
}

/// <summary>Schedule Generation: validates date window and materializes daily schedules on save or via the Generate form action.</summary>
public sealed class ScheduleGenerationHook(IDocumentStore store) : IBeforeValidate<ScheduleGeneration>
{
    public const int MaxBatchDays = 120;

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var from = d.Date("from_date");
        var to = d.Date("to_date");
        if (from is null || to is null) return;
        if (to < from)
            throw Errors.One("to_date", "The end date must be on or after the start date");
        var span = to.Value.DayNumber - from.Value.DayNumber + 1;
        if (span > MaxBatchDays)
            throw Errors.One("to_date", $"A single schedule generation batch cannot exceed {MaxBatchDays} days (requested {span} days)");

        var company = d.Str("company");
        if (company is null) return;

        var res = await WsmScheduleGenerator.GenerateAsync(
            store,
            c.Doc.TenantId,
            company,
            d.Str("organization_unit"),
            d.Str("shift_group"),
            d.Str("employee"),
            from.Value,
            to.Value,
            d.Bool("overwrite_unlocked"),
            string.IsNullOrEmpty(c.Doc.Name) || c.Doc.Name == "(baru)" ? "ScheduleGeneration" : c.Doc.Name,
            ct);

        d["status"] = "Generated";
        d["generated_count"] = (long)res.GeneratedCount;
        d["skipped_locked_count"] = (long)res.SkippedLockedCount;
        d["violation_count"] = (long)res.ViolationCount;
        d["summary_log"] = res.Log;
    }
}

/// <summary>Form action on ScheduleGeneration to re-run daily schedule materialization for its configured scope and period.</summary>
public sealed class ScheduleGenerationRunAction(IDocumentStore store) : IFormAction<ScheduleGeneration>
{
    public string ActionId => "erp.wsm.generate_schedules";
    public string Label => "Generate daily schedules";
    public string Icon => "calendar-plus";
    public string Tone => "primary";

    public bool IsVisible(UserContext user, Document doc) =>
        user.HasRole("HR") || user.IsAdmin;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var sys = new SystemDocs(ctx.Docs, ctx.Doc.TenantId);
        await sys.TouchAsync("ScheduleGeneration", ctx.Doc.Name, ct);
        var updated = await store.GetAsync(ctx.Doc.TenantId, "ScheduleGeneration", ctx.Doc.Name, ct);
        return FormActionResult.Ok(updated?.Data.Str("summary_log") ?? "Schedules generated.");
    }
}

/// <summary>Shift Override: single-day schedule override validated against Workforce Schedule Rules and materialized into EmployeeDailySchedule.</summary>
public sealed class ShiftOverrideHook(IDocumentStore store)
    : IBeforeValidate<ShiftOverride>, IAfterSave<ShiftOverride>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var date = d.Date("override_date") ?? throw Errors.One("override_date", "The override date is required");
        await Overtime.CheckNotPaidAsync(m, emp.Name, $"{date:yyyy-MM}", "override_date", ct);

        var newType = d.Str("new_schedule_type") ?? "WORK";
        if (newType != "WORK") d["new_shift_type"] = null;
        var newShift = d.Str("new_shift_type");
        if (newType == "WORK" && newShift is null)
            throw Errors.One("new_shift_type", "New shift type is required when schedule type is WORK");

        var existing = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name), new FilterClause("work_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();
        if (c.Previous is null && existing is not null)
        {
            d["previous_schedule_type"] = existing.Data.Str("schedule_type");
            d["previous_shift_type"] = existing.Data.Str("shift_type");
        }

        var violations = await WsmConstraintEngine.EvaluateAsync(m, emp,
            [new WsmConstraintEngine.ProposedSlot(date, newType, newShift)], ct);
        var strict = violations.FirstOrDefault(v => v.IsStrict);
        if (strict is not null)
            throw Errors.One("new_shift_type", strict.Message);
        foreach (var adv in violations.Where(v => !v.IsStrict))
            c.Messages.Add(adv.Message);
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var date = d.Date("override_date");
        var empName = d.Str("employee");
        if (date is null || empName is null) return;
        await WsmLeaveSync.UpsertDailySlotAsync(
            store, c.Doc.TenantId, empName, date.Value,
            d.Str("new_schedule_type") ?? "WORK",
            d.Str("new_shift_type"),
            "Override",
            c.Doc.Name,
            ct);
    }
}

/// <summary>
/// Shift Swap Request: two-party shift exchange with peer acceptance, manager approval, and Workforce Constraint Engine validation
/// for both parties. Updates both employees' EmployeeDailySchedule records when approved.
/// </summary>
public sealed class ShiftSwapRequestHook(IDocumentStore store, IUserDirectory? users = null, INotificationStore? inbox = null)
    : IBeforeValidate<ShiftSwapRequest>, IBeforeTransition<ShiftSwapRequest>, IAfterSave<ShiftSwapRequest>, IOnCancel<ShiftSwapRequest>
{
    private async Task ValidateSwapAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var reqEmp = await Approval.ActiveEmployeeAsync(m, d.Str("requester"), ct);
        var tgtEmp = await Approval.ActiveEmployeeAsync(m, d.Str("target_employee"), ct);
        if (reqEmp.Name == tgtEmp.Name)
            errors.Add("target_employee", "An employee cannot swap shifts with themselves");

        var swapDate = d.Date("swap_date");
        if (swapDate is null) { errors.ThrowIfAny(); return; }
        d.SetIfEmpty("target_swap_date", D.Iso(swapDate.Value));
        var targetDate = d.Date("target_swap_date") ?? swapDate.Value;

        await Overtime.CheckNotPaidAsync(m, reqEmp.Name, $"{swapDate.Value:yyyy-MM}", "swap_date", ct);
        await Overtime.CheckNotPaidAsync(m, tgtEmp.Name, $"{targetDate:yyyy-MM}", "target_swap_date", ct);

        d.SetIfEmpty("approver", reqEmp.Data.Str("leave_approver") ?? reqEmp.Data.Str("reports_to")
            ?? await Hr.ManagerForAsync(m, reqEmp.Name, reqEmp.Data.Str("position"), reqEmp.Data.Str("organization_unit"), ct, swapDate));

        var reqSched = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", reqEmp.Name), new FilterClause("work_date", "=", D.Iso(swapDate.Value))], ct, take: 1)).FirstOrDefault();
        var tgtSched = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", tgtEmp.Name), new FilterClause("work_date", "=", D.Iso(targetDate))], ct, take: 1)).FirstOrDefault();

        if (reqSched is null)
            errors.Add("swap_date", $"No materialized daily schedule exists for {reqEmp.Name} on {D.Iso(swapDate.Value)}");
        if (tgtSched is null)
            errors.Add("target_swap_date", $"No materialized daily schedule exists for {tgtEmp.Name} on {D.Iso(targetDate)}");
        errors.ThrowIfAny();

        if (c.Doc.WorkflowState is null or "Draft" or "Pending Peer Approval")
        {
            d["requester_schedule_type"] = reqSched!.Data.Str("schedule_type");
            d["requester_shift_type"] = reqSched.Data.Str("shift_type");
            d["target_schedule_type"] = tgtSched!.Data.Str("schedule_type");
            d["target_shift_type"] = tgtSched.Data.Str("shift_type");
        }

        var reqType = d.Str("requester_schedule_type") ?? reqSched!.Data.Str("schedule_type") ?? "WORK";
        var reqShift = d.Str("requester_shift_type") ?? reqSched!.Data.Str("shift_type");
        var tgtType = d.Str("target_schedule_type") ?? tgtSched!.Data.Str("schedule_type") ?? "WORK";
        var tgtShift = d.Str("target_shift_type") ?? tgtSched!.Data.Str("shift_type");

        if (reqType == "LEAVE" || tgtType == "LEAVE")
            errors.Add("swap_date", "Cannot swap a day that is marked as approved leave");
        errors.ThrowIfAny();

        // Build proposed post-swap slots for requester and target employee
        var reqProposals = new List<WsmConstraintEngine.ProposedSlot>();
        var tgtProposals = new List<WsmConstraintEngine.ProposedSlot>();
        if (swapDate.Value == targetDate)
        {
            reqProposals.Add(new(swapDate.Value, tgtType, tgtShift));
            tgtProposals.Add(new(targetDate, reqType, reqShift));
        }
        else
        {
            var reqOnTargetDate = (await m.QueryAsync("EmployeeDailySchedule",
                [new FilterClause("employee", "=", reqEmp.Name), new FilterClause("work_date", "=", D.Iso(targetDate))], ct, take: 1)).FirstOrDefault();
            var tgtOnSwapDate = (await m.QueryAsync("EmployeeDailySchedule",
                [new FilterClause("employee", "=", tgtEmp.Name), new FilterClause("work_date", "=", D.Iso(swapDate.Value))], ct, take: 1)).FirstOrDefault();

            reqProposals.Add(new(swapDate.Value, tgtOnSwapDate?.Data.Str("schedule_type") ?? "REST", tgtOnSwapDate?.Data.Str("shift_type")));
            reqProposals.Add(new(targetDate, tgtType, tgtShift));

            tgtProposals.Add(new(swapDate.Value, reqType, reqShift));
            tgtProposals.Add(new(targetDate, reqOnTargetDate?.Data.Str("schedule_type") ?? "REST", reqOnTargetDate?.Data.Str("shift_type")));
        }

        var reqViolations = await WsmConstraintEngine.EvaluateAsync(m, reqEmp, reqProposals, ct);
        var tgtViolations = await WsmConstraintEngine.EvaluateAsync(m, tgtEmp, tgtProposals, ct);
        var allViolations = reqViolations.Concat(tgtViolations).ToList();

        d["constraint_notes"] = allViolations.Count == 0
            ? "All workforce schedule constraints satisfied for both employees."
            : string.Join("\n", allViolations.Select(v => $"[{v.EnforcementMode}] {v.Message}"));

        var strictViolation = allViolations.FirstOrDefault(v => v.IsStrict);
        if (strictViolation is not null)
            errors.Add("swap_date", strictViolation.Message);

        foreach (var adv in allViolations.Where(v => !v.IsStrict))
            c.Messages.Add(adv.Message);

        errors.ThrowIfAny();
    }

    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => ValidateSwapAsync(c, ct);

    public Task BeforeTransitionAsync(HookContext c, CancellationToken ct) =>
        c.ToState is "Pending Peer Approval" or "Pending Manager Approval" or "Approved" ? ValidateSwapAsync(c, ct) : Task.CompletedTask;

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var swapDate = d.Date("swap_date");
        var targetDate = d.Date("target_swap_date") ?? swapDate;
        var reqEmp = d.Str("requester");
        var tgtEmp = d.Str("target_employee");
        if (swapDate is null || targetDate is null || reqEmp is null || tgtEmp is null) return;

        if (c.Doc.WorkflowState == Approval.Approved && c.Previous?.WorkflowState != Approval.Approved)
        {
            var reqOrigType = d.Str("requester_schedule_type") ?? "WORK";
            var reqOrigShift = d.Str("requester_shift_type");
            var tgtOrigType = d.Str("target_schedule_type") ?? "WORK";
            var tgtOrigShift = d.Str("target_shift_type");

            await WsmLeaveSync.UpsertDailySlotAsync(store, c.Doc.TenantId, reqEmp, swapDate.Value, tgtOrigType, tgtOrigShift, "Shift Swap", c.Doc.Name, ct);
            await WsmLeaveSync.UpsertDailySlotAsync(store, c.Doc.TenantId, tgtEmp, targetDate.Value, reqOrigType, reqOrigShift, "Shift Swap", c.Doc.Name, ct);
        }
        else if (c.Doc.WorkflowState == "Cancelled" && c.Previous?.WorkflowState == Approval.Approved)
        {
            await RevertSwapAsync(c, ct);
        }

        if (users is not null && inbox is not null && c.Doc.WorkflowState != c.Previous?.WorkflowState)
        {
            var reqName = d.Str("requester_name") ?? reqEmp;
            var tgtName = d.Str("target_employee_name") ?? tgtEmp;
            switch (c.Doc.WorkflowState)
            {
                case "Pending Peer Approval":
                    await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [tgtEmp],
                        $"Shift swap request from {reqName}",
                        $"{reqName} requested to swap shift on {D.Iso(swapDate.Value)} with your shift on {D.Iso(targetDate.Value)}.",
                        "ShiftSwapRequest", c.Doc.Name, $"swap-peer|{c.Doc.Name}|{c.Doc.Version}", ct);
                    break;
                case "Pending Manager Approval":
                    await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [d.Str("approver")],
                        $"Shift swap awaiting approval: {reqName} ↔ {tgtName}",
                        $"Both {reqName} and {tgtName} agreed to swap shifts ({D.Iso(swapDate.Value)} ↔ {D.Iso(targetDate.Value)}).",
                        "ShiftSwapRequest", c.Doc.Name, $"swap-mgr|{c.Doc.Name}|{c.Doc.Version}", ct);
                    break;
                case Approval.Approved:
                    await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [reqEmp, tgtEmp],
                        $"Shift swap {c.Doc.Name} approved",
                        $"Your shift swap ({D.Iso(swapDate.Value)} ↔ {D.Iso(targetDate.Value)}) has been approved and applied to your schedule.",
                        "ShiftSwapRequest", c.Doc.Name, $"swap-ok|{c.Doc.Name}|{c.Doc.Version}", ct);
                    break;
                case "Rejected":
                    await Notify.ToEmployeesAsync(users, inbox, c.Doc.TenantId, [reqEmp, tgtEmp],
                        $"Shift swap {c.Doc.Name} rejected",
                        $"Shift swap request {c.Doc.Name} ({D.Iso(swapDate.Value)}) was rejected.",
                        "ShiftSwapRequest", c.Doc.Name, $"swap-rej|{c.Doc.Name}|{c.Doc.Version}", ct);
                    break;
            }
        }
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => RevertSwapAsync(c, ct);

    private async Task RevertSwapAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var swapDate = d.Date("swap_date");
        var targetDate = d.Date("target_swap_date") ?? swapDate;
        var reqEmp = d.Str("requester");
        var tgtEmp = d.Str("target_employee");
        if (swapDate is null || targetDate is null || reqEmp is null || tgtEmp is null) return;

        await WsmLeaveSync.UpsertDailySlotAsync(
            store, c.Doc.TenantId, reqEmp, swapDate.Value,
            d.Str("requester_schedule_type") ?? "WORK", d.Str("requester_shift_type"),
            "Generator", c.Doc.Name, ct);
        await WsmLeaveSync.UpsertDailySlotAsync(
            store, c.Doc.TenantId, tgtEmp, targetDate.Value,
            d.Str("target_schedule_type") ?? "WORK", d.Str("target_shift_type"),
            "Generator", c.Doc.Name, ct);
    }
}

/// <summary>Synchronizes LeaveApplication and ShiftOverride/Swap updates with EmployeeDailySchedule and evaluates ShiftRequirement coverage.</summary>
internal static class WsmLeaveSync
{
    public static async Task UpsertDailySlotAsync(
        IDocumentStore store,
        string tenant,
        string employee,
        DateOnly date,
        string scheduleType,
        string? shiftType,
        string sourceType,
        string sourceReference,
        CancellationToken ct,
        string? dayCategoryOverride = null)
    {
        var m = new Masters(store, tenant);
        var emp = await m.GetAsync("Employee", employee, ct);
        if (emp is null) return;
        var shiftDoc = scheduleType == "WORK" ? await m.GetAsync("ShiftType", shiftType, ct) : null;
        var (startUtc, endUtc, crosses) = scheduleType == "WORK" ? WsmTime.ComputeWindow(date, shiftDoc) : (null, null, false);
        var dayCategory = dayCategoryOverride ?? scheduleType switch
        {
            "REST" => "Scheduled Rest Day",
            "HOLIDAY" => "Public Holiday",
            "LEAVE" => "Annual Leave",
            _ => "Scheduled Workday",
        };

        var existing = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", employee), new FilterClause("work_date", "=", D.Iso(date))], ct, take: 1)).FirstOrDefault();

        var uow = new UnitOfWork();
        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            var clone = existing.Clone();
            clone.Data["schedule_type"] = scheduleType;
            clone.Data["day_category"] = dayCategory;
            clone.Data["shift_type"] = scheduleType == "WORK" ? shiftType : null;
            clone.Data["scheduled_start_datetime"] = startUtc;
            clone.Data["scheduled_end_datetime"] = endUtc;
            clone.Data["crosses_midnight"] = crosses;
            clone.Data["source_type"] = sourceType;
            clone.Data["source_reference"] = sourceReference;
            clone.Version = existing.Version + 1;
            clone.Modified = now;
            uow.Upserts.Add((clone, existing.Version));
        }
        else
        {
            var unit = (await Hr.GetOrgUnitAsOfAsync(m, emp.Data.Str("organization_unit"), date, ct))?.Name ?? emp.Data.Str("organization_unit");
            var docName = await store.NextNameAsync(tenant, "EmployeeDailySchedule", "EDS-{YYYY}-{#######}", now, ct);
            uow.Upserts.Add((new Document
            {
                TenantId = tenant,
                DocType = "EmployeeDailySchedule",
                Name = docName,
                Owner = "system",
                ModifiedBy = "system",
                Created = now,
                Modified = now,
                Version = 1,
                SchemaVersion = 1,
                DocStatus = DocStatus.Draft,
                Data = new Dictionary<string, object?>
                {
                    ["employee"] = emp.Name,
                    ["employee_name"] = emp.Data.Str("full_name"),
                    ["company"] = emp.Data.Str("company"),
                    ["organization_unit"] = unit,
                    ["work_date"] = D.Iso(date),
                    ["schedule_type"] = scheduleType,
                    ["day_category"] = dayCategory,
                    ["shift_type"] = scheduleType == "WORK" ? shiftType : null,
                    ["is_public_holiday"] = false,
                    ["scheduled_start_datetime"] = startUtc,
                    ["scheduled_end_datetime"] = endUtc,
                    ["crosses_midnight"] = crosses,
                    ["source_type"] = sourceType,
                    ["source_reference"] = sourceReference,
                    ["is_locked"] = false,
                },
            }, null));
        }
        await store.CommitAsync(uow, ct);
    }

    public static async Task ApplyApprovedLeaveAsync(IDocumentStore store, HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var empName = d.Str("employee");
        var from = d.Date("from_date");
        var to = d.Date("to_date");
        if (empName is null || from is null || to is null) return;

        var emp = await m.GetAsync("Employee", empName, ct);
        if (emp is null) return;
        var leaveType = await m.GetAsync("LeaveType", d.Str("leave_type"), ct);
        var dayCategory = leaveType?.Data.Bool("is_annual_quota") == false ? "Special Leave" : "Annual Leave";

        var existingRows = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", empName),
             new FilterClause("work_date", ">=", D.Iso(from.Value)),
             new FilterClause("work_date", "<=", D.Iso(to.Value))], ct, take: 400))
            .Where(s => s.Data.Date("work_date") is not null)
            .ToDictionary(s => s.Data.Date("work_date")!.Value, s => s);

        for (var day = from.Value; day <= to.Value; day = day.AddDays(1))
        {
            if (existingRows.TryGetValue(day, out var sched))
            {
                if (sched.Data.Str("schedule_type") == "WORK")
                {
                    await UpsertDailySlotAsync(store, c.Doc.TenantId, empName, day, "LEAVE", null, "Leave", c.Doc.Name, ct, dayCategory);
                }
            }
        }

        // Check manpower coverage after marking leave
        var company = emp.Data.Str("company");
        var unit = emp.Data.Str("organization_unit");
        if (company is not null)
        {
            var refreshedM = new Masters(store, c.Doc.TenantId);
            var coverage = await WsmCoverage.EvaluateAsync(refreshedM, company, unit, from.Value, to.Value, ct);
            foreach (var gap in coverage.Where(g => g.IsUnderstaffed))
            {
                c.Messages.Add($"Manpower shortage alert on {D.Iso(gap.WorkDate)}: {gap.OrganizationUnit} / {gap.ShiftType} has {gap.ActualHeadcount} scheduled vs {gap.MinHeadcount} required.");
            }
        }
    }

    public static async Task RevertCancelledLeaveAsync(IDocumentStore store, HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var empName = d.Str("employee");
        var from = d.Date("from_date");
        var to = d.Date("to_date");
        if (empName is null || from is null || to is null) return;
        var emp = await m.GetAsync("Employee", empName, ct);
        var company = emp?.Data.Str("company");
        if (company is null) return;

        // Clear the "Leave" source_type on rows pointing to this leave application so the generator can restore the roster
        var leaveSlots = (await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", empName),
             new FilterClause("work_date", ">=", D.Iso(from.Value)),
             new FilterClause("work_date", "<=", D.Iso(to.Value))], ct, take: 400))
            .Where(s => s.Data.Str("source_reference") == c.Doc.Name).ToList();

        if (leaveSlots.Count > 0)
        {
            var uow = new UnitOfWork();
            foreach (var slot in leaveSlots)
            {
                var clone = slot.Clone();
                clone.Data["source_type"] = "Generator";
                clone.Version = slot.Version + 1;
                uow.Upserts.Add((clone, slot.Version));
            }
            await store.CommitAsync(uow, ct);
        }

        await WsmScheduleGenerator.GenerateAsync(store, c.Doc.TenantId, company, null, null, empName, from.Value, to.Value, true, $"Revert:{c.Doc.Name}", ct);
    }
}

/// <summary>Shift Requirement: operational manpower demand per organization unit, shift type, and optional job profile.</summary>
public sealed class ShiftRequirementHook : IBeforeValidate<ShiftRequirement>
{
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var min = d.Dec("min_headcount");
        if (d.Dec("target_headcount") < min)
            d["target_headcount"] = (long)min;
        return Task.CompletedTask;
    }
}

/// <summary>Evaluates manpower coverage (Required vs Scheduled headcount) per shift and organization unit.</summary>
public static class WsmCoverage
{
    public sealed record CoverageRow(
        DateOnly WorkDate,
        string OrganizationUnit,
        string ShiftType,
        string? JobProfile,
        int MinHeadcount,
        int TargetHeadcount,
        int ActualHeadcount,
        string Status)
    {
        public bool IsUnderstaffed => ActualHeadcount < MinHeadcount;
    }

    internal static async Task<IReadOnlyList<CoverageRow>> EvaluateAsync(
        Masters m,
        string company,
        string? organizationUnit,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken ct)
    {
        var reqFilters = new List<FilterClause>
        {
            new("company", "=", company),
            new("is_active", "=", true),
        };
        if (!string.IsNullOrEmpty(organizationUnit))
            reqFilters.Add(new("organization_unit", "=", organizationUnit));

        var requirements = await m.QueryAsync("ShiftRequirement", reqFilters, ct, take: 500);
        if (requirements.Count == 0) return [];

        var schedules = await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("company", "=", company),
             new FilterClause("work_date", ">=", D.Iso(fromDate)),
             new FilterClause("work_date", "<=", D.Iso(toDate)),
             new FilterClause("schedule_type", "=", "WORK")], ct, take: 10_000);

        var empProfiles = (await m.QueryAsync("Employee", [new FilterClause("company", "=", company)], ct, take: 10_000))
            .ToDictionary(e => e.Name, e => e.Data.Str("job_profile"), StringComparer.Ordinal);

        var results = new List<CoverageRow>();
        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var dayIso = D.Iso(day);
            var daySchedules = schedules.Where(s => s.Data.Str("work_date") == dayIso).ToList();
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var isPublicHoliday = daySchedules.Any(s => s.Data.Bool("is_public_holiday"));

            foreach (var req in requirements)
            {
                if (!Hr.Overlaps(day, day, req.Data.Date("effective_from"), req.Data.Date("effective_to"))) continue;
                var scope = req.Data.Str("day_scope") ?? "All days";
                if (scope == "Weekdays" && isWeekend) continue;
                if (scope == "Weekends" && !isWeekend) continue;
                if (scope == "Public holidays" && !isPublicHoliday) continue;

                var reqUnit = req.Data.Str("organization_unit") ?? "";
                var reqShift = req.Data.Str("shift_type") ?? "";
                var reqJob = req.Data.Str("job_profile");
                var minH = (int)req.Data.Dec("min_headcount");
                var tgtH = Math.Max(minH, (int)req.Data.Dec("target_headcount"));

                var actual = daySchedules.Count(s =>
                    s.Data.Str("organization_unit") == reqUnit
                    && s.Data.Str("shift_type") == reqShift
                    && (reqJob is null || (s.Data.Str("employee") is { } emp && empProfiles.GetValueOrDefault(emp) == reqJob)));

                var status = actual < minH ? "Understaffed" : actual > tgtH ? "Overstaffed" : "Sufficient";
                results.Add(new CoverageRow(day, reqUnit, reqShift, reqJob, minH, tgtH, actual, status));
            }
        }
        return results;
    }
}

/// <summary>
/// Daily scheduled job: maintains a 30-day rolling horizon of materialized EmployeeDailySchedule rows
/// for all companies with active ShiftAssignments, without overwriting locked, manual, swap, or leave slots.
/// </summary>
public sealed class RosterRollingHorizonJob : IScheduledJob
{
    public string Name => "Erp.RosterRollingHorizon";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var m = new Masters(store, context.TenantId);
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);
        var horizonEnd = today.AddDays(30);

        var activeAssignments = await m.QueryAsync("ShiftAssignment", [new FilterClause("is_active", "=", true)], ct, take: 1000);
        var companies = activeAssignments.Select(a => a.Data.Str("company")).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var company in companies)
        {
            await HrEffectiveDateJob.Safe(context, $"RosterRollingHorizon:{company}", () =>
                WsmScheduleGenerator.GenerateAsync(
                    store,
                    context.TenantId,
                    company,
                    organizationUnit: null,
                    shiftGroup: null,
                    employee: null,
                    fromDate: today,
                    toDate: horizonEnd,
                    overwriteUnlocked: false,
                    sourceReference: $"AUTO-{D.Iso(today)}",
                    ct: ct));
        }
    }
}

/// <summary>
/// Daily scheduled job: checks pending approval documents whose current approver is on approved leave (or left)
/// and delegates them to an active Acting (Plt.) holder, sending in-app notifications to the substitute and requester.
/// </summary>
public sealed class ApprovalDelegationRefreshJob : IScheduledJob
{
    public string Name => "Erp.ApprovalDelegationRefresh";
    public string Schedule => "@daily";

    private static readonly (string DocType, string ApproverField, string RequesterField, string PendingState)[] Targets =
    [
        ("LeaveApplication", "leave_approver", "employee", "Pending Approval"),
        ("OvertimeRequest", "approver", "employee", "Pending Approval"),
        ("OvertimeRequest", "project_approver", "employee", "Pending Project Approval"),
        ("ExpenseClaim", "expense_approver", "employee", "Pending Manager"),
        ("ExpenseClaim", "project_approver", "employee", "Pending Project Approval"),
        ("EmployeeAdvance", "expense_approver", "employee", "Pending Manager"),
        ("PurchaseRequest", "approver", "requested_by", "Pending Approval"),
        ("PurchaseRequest", "project_approver", "requested_by", "Pending Project Approval"),
    ];

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var users = context.Services.GetService<IUserDirectory>();
        var inbox = context.Services.GetService<INotificationStore>();
        var m = new Masters(store, context.TenantId);
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);
        var now = DateTimeOffset.UtcNow;

        foreach (var (docType, approverField, requesterField, pendingState) in Targets)
        {
            // Filter on the state in the query: the pending documents are a small part of all documents.
            var docs = await m.QueryAsync(docType, [new FilterClause("workflow_state", "=", pendingState)], ct, take: 10_000);

            foreach (var doc in docs)
            {
                var curApprover = doc.Data.Str(approverField);
                var requester = doc.Data.Str(requesterField) ?? "";
                if (curApprover is null) continue;

                var approverDoc = await m.GetAsync("Employee", curApprover, ct);
                var unavailable = approverDoc is null || approverDoc.Data.Str("status") == Hr.Left
                    || await Hr.IsOnApprovedLeaveAsync(m, curApprover, today, ct);
                if (!unavailable) continue;

                var acting = await Hr.FindActingDelegateAsync(m, curApprover, approverDoc?.Data.Str("position"), requester, today, ct);
                if (acting is null || acting == curApprover) continue;

                var clone = doc.Clone();
                clone.Data[approverField] = acting;
                clone.Version = doc.Version + 1;
                clone.Modified = now;
                clone.ModifiedBy = "system";

                var uow = new UnitOfWork();
                uow.Upserts.Add((clone, doc.Version));
                uow.Audits.Add(new AuditEntry
                {
                    TenantId = doc.TenantId,
                    DocType = doc.DocType,
                    DocName = doc.Name,
                    Action = "Approver delegated",
                    Actor = "system",
                    At = now,
                    Changes = [new FieldChange(approverField, curApprover, acting)],
                    Comment = $"{curApprover} is unavailable on {D.Iso(today)}; delegated to Acting (Plt.) {acting}",
                    SchemaVersion = doc.SchemaVersion,
                });
                try { await store.CommitAsync(uow, ct); }
                catch (ConcurrencyException) { continue; } // changed meanwhile (e.g. just approved): the next run looks again

                if (users is not null && inbox is not null)
                {
                    await Notify.ToEmployeesAsync(users, inbox, context.TenantId, [acting],
                        $"Delegated approval: {docType} {doc.Name}",
                        $"You have been assigned as Acting (Plt.) approver for {docType} {doc.Name} while {curApprover} is unavailable.",
                        docType, doc.Name, $"deleg-act|{docType}|{doc.Name}|{acting}|{D.Iso(today)}", ct);
                    if (!string.IsNullOrEmpty(requester))
                    {
                        await Notify.ToEmployeesAsync(users, inbox, context.TenantId, [requester],
                            $"Approver delegated for {docType} {doc.Name}",
                            $"Pending approval for {docType} {doc.Name} has been delegated from {curApprover} to Acting approver {acting}.",
                            docType, doc.Name, $"deleg-req|{docType}|{doc.Name}|{acting}|{D.Iso(today)}", ct);
                    }
                }
            }
        }
    }
}
