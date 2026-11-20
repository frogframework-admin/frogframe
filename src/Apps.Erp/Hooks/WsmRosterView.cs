using System.Globalization;
using System.Text;
using Framework.Imports;
using Framework.Reports;

namespace Apps.Erp.Hooks;

/// <summary>
/// Roster calendar (/hr/roster-calendar): manpower coverage per day and shift, and the team schedule export.
/// Pure and I/O free so it can be tested without a database: the page passes the documents it already loaded.
/// Coverage follows the same rules as the schedule optimizer: a requirement counts the employees of its unit
/// and every sub-unit (and of its job profile, when set); requirements with the same day, shift, unit and job
/// profile add up.
/// </summary>
public static class WsmRosterView
{
    /// <summary>Organization unit with its materialized path (e.g. "/HOLD/DIV-IT/"), used for sub-unit checks.</summary>
    public sealed record UnitNode(string Name, string? Path);

    /// <summary>All shifts of one day: required vs scheduled headcount summed over the requirements in scope.</summary>
    public sealed record SlotCoverage(DateOnly Date, string ShiftType, int Required, int Target, int Actual, IReadOnlyList<string> ShortScopes)
    {
        /// <summary>"Understaffed" when any requirement of the slot is short, else "Overstaffed" above the target, else "Sufficient".</summary>
        public string Status => ShortScopes.Count > 0 ? "Understaffed" : Actual > Target ? "Overstaffed" : "Sufficient";
    }

    /// <summary><paramref name="root"/> and every unit below it. A unit without a path only matches itself.</summary>
    public static IReadOnlySet<string> Subtree(IReadOnlyList<UnitNode> units, string root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { root };
        var rootNode = units.FirstOrDefault(u => u.Name == root);
        if (rootNode?.Path is not { Length: > 0 } prefix) return set;
        foreach (var u in units)
            if (u.Path is { } p && p.StartsWith(prefix, StringComparison.Ordinal)) set.Add(u.Name);
        return set;
    }

    /// <summary>
    /// Coverage per requirement scope (day, unit, shift, job profile), for the requirements that apply to the
    /// selected unit subtree (all requirements when <paramref name="unitFilter"/> is empty).
    /// </summary>
    /// <param name="schedules">EmployeeDailySchedule rows of the period (any schedule type). A day is a public holiday
    /// when one of its rows has is_public_holiday set, as in <see cref="WsmCoverage"/>.</param>
    /// <param name="jobProfileByEmployee">Employee → job profile, for requirements limited to one job profile.</param>
    public static List<WsmCoverage.CoverageRow> CoverageRows(
        IReadOnlyList<DateOnly> days,
        IEnumerable<IReadOnlyDictionary<string, object?>> requirements,
        IEnumerable<IReadOnlyDictionary<string, object?>> schedules,
        IReadOnlyDictionary<string, string?> jobProfileByEmployee,
        IReadOnlyList<UnitNode> units,
        string? unitFilter)
    {
        var inFilter = string.IsNullOrEmpty(unitFilter) ? null : Subtree(units, unitFilter);
        var subtreeCache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        IReadOnlySet<string> SubtreeOf(string unit) =>
            subtreeCache.TryGetValue(unit, out var s) ? s : subtreeCache[unit] = Subtree(units, unit);

        // WORK rows by (date, shift): (employee, unit)
        var work = new Dictionary<(DateOnly, string), List<(string Employee, string? Unit)>>();
        var publicHolidays = new HashSet<DateOnly>();
        foreach (var s in schedules)
        {
            if (s.GetValueOrDefault("is_public_holiday") is true && Date(s, "work_date") is { } ph) publicHolidays.Add(ph);
            if (Str(s, "schedule_type") != "WORK" || Str(s, "shift_type") is not { } shift || Str(s, "employee") is not { } emp) continue;
            if (Date(s, "work_date") is not { } date) continue;
            if (!work.TryGetValue((date, shift), out var list)) work[(date, shift)] = list = [];
            list.Add((emp, Str(s, "organization_unit")));
        }

        var groups = new Dictionary<(DateOnly Date, string Unit, string Shift, string? Job), (int Min, int Target)>();
        foreach (var r in requirements)
        {
            if (r.GetValueOrDefault("is_active") is false) continue;
            if (Str(r, "shift_type") is not { } shift) continue;
            var unit = Str(r, "organization_unit") ?? "";
            // A requirement belongs to the view when its unit is the selected unit or one of its sub-units.
            if (inFilter is not null && !inFilter.Contains(unit)) continue;
            var min = Math.Max(0, Int(r, "min_headcount"));
            var target = Math.Max(min, Int(r, "target_headcount"));
            if (target == 0) continue;
            var job = Str(r, "job_profile");
            var dayScope = Str(r, "day_scope") ?? "All days";
            var from = Date(r, "effective_from");
            var to = Date(r, "effective_to");
            foreach (var day in days)
            {
                if ((from is { } f && day < f) || (to is { } t && day > t)) continue;
                var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                if ((dayScope == "Weekdays" && weekend) || (dayScope == "Weekends" && !weekend)) continue;
                if (dayScope == "Public holidays" && !publicHolidays.Contains(day)) continue;
                var key = (day, unit, shift, job);
                var cur = groups.GetValueOrDefault(key);
                groups[key] = (cur.Min + min, cur.Target + target);
            }
        }

        var rows = new List<WsmCoverage.CoverageRow>(groups.Count);
        foreach (var ((day, unit, shift, job), (min, target)) in groups
                     .OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Unit, StringComparer.Ordinal).ThenBy(g => g.Key.Shift, StringComparer.Ordinal).ThenBy(g => g.Key.Job, StringComparer.Ordinal))
        {
            var scope = unit.Length == 0 ? null : SubtreeOf(unit);
            var actual = work.TryGetValue((day, shift), out var staff)
                ? staff.Count(x => (scope is null || (x.Unit is { } u && scope.Contains(u)))
                                   && (job is null || jobProfileByEmployee.GetValueOrDefault(x.Employee) == job))
                : 0;
            var status = actual < min ? "Understaffed" : actual > target ? "Overstaffed" : "Sufficient";
            rows.Add(new WsmCoverage.CoverageRow(day, unit, shift, job, min, target, actual, status));
        }
        return rows;
    }

    /// <summary>Coverage rows summed per day and shift, for the indicator row under each shift in the calendar grid.</summary>
    public static Dictionary<(DateOnly Date, string Shift), SlotCoverage> SlotSummary(IEnumerable<WsmCoverage.CoverageRow> rows) =>
        rows.GroupBy(r => (r.WorkDate, r.ShiftType))
            .ToDictionary(g => (g.Key.WorkDate, g.Key.ShiftType), g => new SlotCoverage(
                g.Key.WorkDate, g.Key.ShiftType,
                g.Sum(r => r.MinHeadcount), g.Sum(r => r.TargetHeadcount), g.Sum(r => r.ActualHeadcount),
                g.Where(r => r.IsUnderstaffed)
                 .Select(r => $"{(r.OrganizationUnit.Length == 0 ? "All units" : r.OrganizationUnit)}{(r.JobProfile is null ? "" : " / " + r.JobProfile)}: {r.ActualHeadcount}/{r.MinHeadcount}")
                 .ToList()));

    /// <summary>One employee line of the export.</summary>
    public sealed record ExportEmployee(string Id, string Name, string? Unit, string? Group, IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, object?>> ByDate);

    /// <summary>
    /// Team schedule of the month: one row per employee, one column per day (shift name, OFF, LEAVE or HOLIDAY)
    /// and day totals. The .xlsx also has a Coverage sheet with the requirement checks.
    /// </summary>
    public static ReportOutput Export(string format, string month, string? unitLabel, IReadOnlyList<DateOnly> days,
        IReadOnlyList<ExportEmployee> employees, IReadOnlyList<WsmCoverage.CoverageRow> coverage)
    {
        var headers = new List<string> { "Employee", "Employee Name", "Organization Unit", "Shift Group" };
        headers.AddRange(days.Select(d => d.ToString("dd ddd", CultureInfo.InvariantCulture)));
        headers.AddRange(["Work Days", "Night Shifts", "Rest Days", "Leave Days", "Holidays"]);

        var rows = new List<object?[]>(employees.Count);
        foreach (var e in employees)
        {
            var row = new List<object?> { e.Id, e.Name, e.Unit, e.Group };
            int work = 0, night = 0, rest = 0, leave = 0, holiday = 0;
            foreach (var d in days)
            {
                if (!e.ByDate.TryGetValue(d, out var slot)) { row.Add(null); continue; }
                var type = Str(slot, "schedule_type") ?? "WORK";
                switch (type)
                {
                    case "REST": rest++; row.Add("OFF"); break;
                    case "LEAVE": leave++; row.Add("LEAVE"); break;
                    case "HOLIDAY": holiday++; row.Add("HOLIDAY"); break;
                    default:
                        work++;
                        if (slot.GetValueOrDefault("crosses_midnight") is true) night++;
                        row.Add(Str(slot, "shift_type") ?? "WORK");
                        break;
                }
            }
            row.AddRange([work, night, rest, leave, holiday]);
            rows.Add([.. row]);
        }

        var stamp = month + (string.IsNullOrEmpty(unitLabel) ? "" : "-" + Safe(unitLabel));
        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Csv)));
            foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(v => Csv(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))));
            // UTF-8 with BOM so Excel opens names with accents correctly.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return new ReportOutput(bytes, $"roster-{stamp}.csv", "text/csv", rows.Count);
        }

        var widths = new Dictionary<int, double> { [0] = 14, [1] = 26, [2] = 18, [3] = 14 };
        for (var i = 0; i < days.Count; i++) widths[4 + i] = 9;
        var sheets = new List<XlsxSheet>
        {
            new() { Name = "Roster " + month, Headers = headers, Rows = rows, Widths = widths },
            new()
            {
                Name = "Coverage",
                Headers = ["Work Date", "Organization Unit", "Shift", "Job Profile", "Min Required", "Target", "Scheduled", "Status"],
                Rows = coverage.Select(c => new object?[]
                {
                    c.WorkDate.ToDateTime(TimeOnly.MinValue), c.OrganizationUnit.Length == 0 ? "All units" : c.OrganizationUnit,
                    c.ShiftType, c.JobProfile ?? "All roles", c.MinHeadcount, c.TargetHeadcount, c.ActualHeadcount, c.Status,
                }).ToList(),
                Widths = new() { [0] = 12, [1] = 20, [2] = 18, [3] = 16, [7] = 14 },
            },
        };
        return new ReportOutput(XlsxWriter.Build(sheets), $"roster-{stamp}.xlsx", ReportFormats.ContentType(ReportFormats.Xlsx), rows.Count);
    }

    private static string? Str(IReadOnlyDictionary<string, object?> d, string f) => d.GetValueOrDefault(f) switch
    {
        string s when s.Length > 0 => s,
        _ => null,
    };

    private static int Int(IReadOnlyDictionary<string, object?> d, string f) => d.GetValueOrDefault(f) switch
    {
        decimal m => (int)m,
        long l => (int)l,
        int i => i,
        double x => (int)x,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };

    private static DateOnly? Date(IReadOnlyDictionary<string, object?> d, string f) => d.GetValueOrDefault(f) switch
    {
        DateOnly x => x,
        DateTime x => DateOnly.FromDateTime(x),
        string s when DateOnly.TryParseExact(s.Length >= 10 ? s[..10] : s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) => v,
        _ => null,
    };

    private static string Csv(string v) =>
        v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || (v.Length > 0 && v[0] is '=' or '+' or '-' or '@')
            ? "\"" + (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' ? "'" : "") + v.Replace("\"", "\"\"") + "\""
            : v;

    private static string Safe(string v) => new(v.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
