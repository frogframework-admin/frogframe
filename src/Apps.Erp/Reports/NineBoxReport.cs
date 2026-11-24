using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>
/// 9-box grid of Talent Reviews: one row per box (1A … 3C, top-right to bottom-left as usually drawn) with the number of
/// employees, their share and their names. Filters: appraisal cycle, unit (with the units below it), calibration session,
/// and whether to include reviews that are not yet submitted. Uses the reader's permissions on Talent Review.
/// </summary>
public sealed class NineBoxReport(IDocumentStore store) : IReportProvider
{
    public static readonly string[] Boxes =
    [
        "1A - Star / Future Leader", "1B - High Potential", "1C - Rough Diamond",
        "2A - High Performer", "2B - Core Player", "2C - Inconsistent",
        "3A - Trusted Professional", "3B - Effective", "3C - Underperformer",
    ];

    public string Name => "erp-nine-box";
    public string Title => "9-Box Talent Grid";
    public string Description => "Talent Reviews per 9-box position for a cycle, unit or calibration session.";
    public string DocType => "TalentReview";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("appraisal_cycle", "Appraisal cycle", "Data"),
        new("organization_unit", "Organization unit (with units below)", "Data"),
        new("calibration_session", "Calibration session", "Data"),
        new("include_drafts", "Include draft reviews (yes/no)", "Data"),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        string? Filter(string key) => f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var cycle = Filter("appraisal_cycle");
        var unit = Filter("organization_unit");
        var session = Filter("calibration_session");
        var drafts = Filter("include_drafts")?.ToLowerInvariant() is "yes" or "y" or "true" or "1" or "ya";

        var reviews = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(r => r.DocStatus == DocStatus.Submitted || (drafts && r.DocStatus == DocStatus.Draft))
            .Where(r => cycle is null || r.Data.Str("appraisal_cycle") == cycle)
            .Where(r => session is null || r.Data.Str("calibration_session") == session)
            .ToList();

        if (unit is not null)
        {
            // Unit filter includes sub-units: keep reviews whose unit chain reaches the filter unit.
            var m = new Masters(store, user.TenantId);
            var kept = new List<Document>();
            foreach (var r in reviews)
                if ((await Announcements.UnitChainAsync(m, r.Data.Str("organization_unit"), ct)).Contains(unit)) kept.Add(r);
            reviews = kept;
        }

        var total = reviews.Count;
        var rows = Boxes.Select(box =>
        {
            var inBox = reviews.Where(r => r.Data.Str("nine_box_position") == box).ToList();
            return Rpt.Row(
                ("nine_box_position", box),
                ("employees", inBox.Count),
                ("share", total > 0 ? Math.Round(100m * inBox.Count / total, 1) : 0m),
                ("names", string.Join(", ", inBox.Select(r => r.Data.Str("employee_name") ?? r.Data.Str("employee")).OrderBy(x => x, StringComparer.CurrentCulture))));
        }).ToList();

        return new ReportResult(
        [
            new("nine_box_position", "9-box position"), new("employees", "Employees", "Int"),
            new("share", "Share (%)", "Float"), new("names", "Names"),
        ], rows);
    }
}
