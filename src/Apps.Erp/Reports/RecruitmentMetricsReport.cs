using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Recruitment funnel metrics by source: total applicants, hired count, conversion rate, and average days to hire.</summary>
public sealed class RecruitmentMetricsReport : IReportProvider
{
    public string Name => "erp-recruitment-metrics";
    public string Title => "Recruitment Metrics";
    public string Description => "Applicants by source, hired count, conversion rate, and average days from application to hire.";
    public string DocType => "JobApplicant";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var apps = await documents.QueryForReportAsync(user, DocType, [], ct: ct);
        var groups = apps.GroupBy(a => a.Data.Str("source") ?? "Unspecified", StringComparer.OrdinalIgnoreCase);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in groups.OrderByDescending(x => x.Count()))
        {
            var total = g.Count();
            var hired = g.Where(a => a.Data.Str("status") == "Hired").ToList();
            var hiredCount = hired.Count;
            var conv = total > 0 ? Math.Round(100m * hiredCount / total, 1) : 0m;
            var avgDays = hiredCount > 0
                ? Math.Round((decimal)hired.Average(a => Math.Max(0, (a.Modified - a.Created).TotalDays)), 1)
                : 0m;
            rows.Add(Rpt.Row(
                ("source", g.Key),
                ("applicants", total),
                ("hired", hiredCount),
                ("conversion_rate", conv),
                ("avg_days_to_hire", avgDays)));
        }

        return new ReportResult(
        [
            new("source", "Source"), new("applicants", "Applicants", "Int"),
            new("hired", "Hired", "Int"), new("conversion_rate", "Conversion (%)", "Float"),
            new("avg_days_to_hire", "Avg days to hire", "Float"),
        ], rows);
    }
}
