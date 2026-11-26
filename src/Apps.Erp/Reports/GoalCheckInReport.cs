using Apps.Erp.Hooks;
using Apps.Erp.Home;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>
/// Per employee: active goals and their average progress, date of the last completed 1:1, and open and overdue action
/// items. HR-Talent, HR-Admin and administrators see everyone; anyone else sees themselves and their direct reports
/// (goals are readable company-wide, so the report narrows them itself).
/// </summary>
public sealed class GoalCheckInReport(TimeProvider? clock = null) : IReportProvider
{
    public string Name => "erp-goal-checkin";
    public string Title => "Goal & 1:1 Coverage";
    public string Description => "Per employee: active goals and progress, last completed 1:1, open and overdue action items.";
    public string DocType => "OkrObjective";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("period", "Period (e.g. 2026-Q1)", "Data"),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var period = Rpt.Opt(f, "period");
        var today = CheckInRules.Today(clock ?? TimeProvider.System);

        var me = user.Attr("employee");
        var everyone = user.IsAdmin || user.HasRole(ErpRoles.HrTalent) || user.HasRole(ErpRoles.HrAdmin);
        bool Mine(Document x, string bossField) => everyone || (me is not null && (x.Data.Str("employee") == me || x.Data.Str(bossField) == me));

        var goals = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(g => period is null || g.Data.Str("period") == period)
            .Where(g => Mine(g, "reports_to"))
            .ToList();
        var checkIns = (await documents.QueryForReportAsync(user, "OneOnOne", [], ct: ct)).Where(c => Mine(c, "manager")).ToList();

        var employees = goals.Select(g => g.Data.Str("employee"))
            .Concat(checkIns.Select(c => c.Data.Str("employee")))
            .OfType<string>()
            .Distinct()
            .OrderBy(e => e, StringComparer.CurrentCulture)
            .ToList();

        var rows = employees.Select(emp =>
        {
            var mine = goals.Where(g => g.Data.Str("employee") == emp).ToList();
            var active = mine.Where(g => g.Data.Str("status") == "Active").ToList();
            var meetings = checkIns.Where(c => c.Data.Str("employee") == emp).ToList();

            var (last, _, overdue) = CheckInRules.Summarize(meetings, today);
            var actions = meetings.SelectMany(c => c.Data.Rows("action_items")).Where(a => a.Str("status") != "Done").ToList();

            return Rpt.Row(
                ("employee", emp),
                ("employee_name", mine.FirstOrDefault()?.Data.Str("employee_name") ?? meetings.FirstOrDefault()?.Data.Str("employee_name") ?? emp),
                ("active_goals", active.Count),
                ("avg_progress", active.Count > 0 ? Math.Round(active.Average(g => g.Data.Dec("progress_percent")), 1) : (decimal?)null),
                ("last_checkin", last is { } l ? D.Iso(l) : null),
                ("days_since_checkin", last is { } l2 ? today.DayNumber - l2.DayNumber : null),
                ("open_actions", actions.Count),
                ("overdue_actions", overdue));
        }).ToList();

        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("active_goals", "Active goals", "Int"),
            new("avg_progress", "Avg progress (%)", "Float"), new("last_checkin", "Last 1:1", "Date"),
            new("days_since_checkin", "Days since 1:1", "Int"), new("open_actions", "Open actions", "Int"),
            new("overdue_actions", "Overdue actions", "Int"),
        ], rows);
    }
}
