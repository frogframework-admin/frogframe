using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>
/// 360 feedback per cycle: progress (requested / submitted) of every review and, once closed, the self, manager and
/// others averages with the gap. Shows only aggregated numbers (never single responses). HR-Talent, HR-Admin and
/// administrators see every review; a manager sees the reviews of their team (the "report" right comes from the
/// manager rule only) and never their own review here.
/// </summary>
public sealed class FeedbackSummaryReport(IDocumentStore store) : IReportProvider
{
    public string Name => "erp-360-feedback";
    public string Title => "360 Feedback Summary";
    public string Description => "Progress and results of 360 feedback per appraisal cycle and unit.";
    public string DocType => "MultiRaterReview";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("appraisal_cycle", "Appraisal cycle", "Data"),
        new("organization_unit", "Organization unit (with units below)", "Data"),
        new("status", "Status", "Data"),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        string? Filter(string key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var cycle = Filter("appraisal_cycle");
        var unit = Filter("organization_unit");
        var status = Filter("status");

        var me = user.Attr("employee");
        var everyone = user.IsAdmin || user.HasRole(ErpRoles.HrTalent) || user.HasRole(ErpRoles.HrAdmin);
        var reviews = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(r => everyone || (me is not null && r.Data.Str("manager") == me && r.Data.Str("employee") != me))
            .Where(r => cycle is null || r.Data.Str("appraisal_cycle") == cycle)
            .Where(r => status is null || string.Equals(r.Data.Str("status"), status, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (unit is not null)
        {
            var m = new Masters(store, user.TenantId);
            var kept = new List<Document>();
            foreach (var r in reviews)
                if ((await Announcements.UnitChainAsync(m, r.Data.Str("organization_unit"), ct)).Contains(unit)) kept.Add(r);
            reviews = kept;
        }

        static object? Num(Document r, string field) => r.Data.GetValueOrDefault(field) is null ? null : r.Data.Dec(field);
        var rows = reviews
            .OrderBy(r => r.Data.Str("organization_unit"), StringComparer.CurrentCulture)
            .ThenBy(r => r.Data.Str("employee_name"), StringComparer.CurrentCulture)
            .Select(r =>
            {
                var raters = r.Data.Rows("reviewers");
                var requested = raters.Count(x => x.Str("status") is MultiRater.RowRequested or MultiRater.RowSubmitted);
                var submitted = raters.Count(x => x.Str("status") == MultiRater.RowSubmitted);
                var others = Num(r, "overall_others");
                var self = Num(r, "overall_self");
                return Rpt.Row(
                    ("review", r.Name), ("employee", r.Data.Str("employee")), ("employee_name", r.Data.Str("employee_name")),
                    ("organization_unit", r.Data.Str("organization_unit")), ("status", r.Data.Str("status")),
                    ("due_date", r.Data.Str("due_date")), ("raters", raters.Count), ("requested", requested), ("submitted", submitted),
                    ("response_rate", requested > 0 ? Math.Round(100m * submitted / requested, 0) : 0m),
                    ("self", self), ("manager", Num(r, "overall_manager")), ("others", others),
                    ("gap", others is decimal o && self is decimal s ? o - s : null));
            }).ToList();

        return new ReportResult(
        [
            new("review", "360 Feedback"), new("employee", "Employee"), new("employee_name", "Name"), new("organization_unit", "Unit"),
            new("status", "Status"), new("due_date", "Due", "Date"), new("raters", "Raters", "Int"), new("requested", "Requested", "Int"),
            new("submitted", "Submitted", "Int"), new("response_rate", "Response rate (%)", "Float"),
            new("self", "Self", "Float"), new("manager", "Manager", "Float"), new("others", "All others", "Float"), new("gap", "Others − self", "Float"),
        ], rows);
    }
}
