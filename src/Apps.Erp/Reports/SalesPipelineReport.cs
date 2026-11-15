using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Pipeline penjualan: jumlah, nilai, dan nilai tertimbang opportunity menurut tahap, kategori forecast, sales, atau bulan closing.</summary>
public sealed class SalesPipelineReport : IReportProvider
{
    public string Name => "erp-sales-pipeline";
    public string Title => "Sales Pipeline";
    public string Description => "Opportunity count, amount, and weighted amount (amount × probability) by stage, forecast category, sales rep, or expected close month. Row scope applies: sales reps see their own opportunities.";
    public string DocType => "Opportunity";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "Expected close from", "Date"), new("to", "Expected close to", "Date"),
        new("group_by", "Group by", "Select", ["Stage", "Forecast category", "Sales rep", "Close month"]),
        new("status", "Opportunities", "Select", ["Open only", "Won only", "Lost only"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var filters = new List<FilterClause>();
        if (Rpt.DateOf(f, "from") is { } from) filters.Add(new("expected_close_date", ">=", D.Iso(from)));
        if (Rpt.DateOf(f, "to") is { } to) filters.Add(new("expected_close_date", "<=", D.Iso(to)));
        var status = Rpt.Opt(f, "status");
        var opps = (await documents.QueryForReportAsync(user, DocType, filters, ct: ct)).Where(o => status switch
        {
            "Open only" => o.Data.Str("stage_type") is not ("Won" or "Lost"),
            "Won only" => o.Data.Str("stage_type") == "Won",
            "Lost only" => o.Data.Str("stage_type") == "Lost",
            _ => true,
        }).ToList();

        var groupBy = Rpt.Opt(f, "group_by") ?? "Stage";
        string Key(Document o) => groupBy switch
        {
            "Forecast category" => o.Data.Str("forecast_category") ?? "(none)",
            "Sales rep" => o.Data.Str("sales_rep") ?? "(unassigned)",
            "Close month" => o.Data.Str("expected_close_date") is { Length: >= 7 } d ? d[..7] : "(no date)",
            _ => o.Data.Str("sales_stage") ?? "(none)",
        };
        var rows = opps.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Rpt.Row(
            ("group", g.Key), ("count", (decimal)g.Count()), ("amount", g.Sum(o => o.Data.Dec("amount"))),
            ("weighted", g.Sum(o => o.Data.Dec("weighted_amount"))),
            ("avg_probability", g.Any() ? Math.Round((decimal)g.Average(o => o.Data.Dec("probability")), 1) : 0m))).ToList();
        rows.Add(Rpt.Row(("group", (object?)"Total"), ("count", (decimal)opps.Count), ("amount", opps.Sum(o => o.Data.Dec("amount"))),
            ("weighted", opps.Sum(o => o.Data.Dec("weighted_amount"))), ("avg_probability", (object?)null)));
        return new ReportResult(
        [
            new("group", groupBy), new("count", "Opportunities", "Int"), new("amount", "Amount", "Currency"),
            new("weighted", "Weighted amount", "Currency"), new("avg_probability", "Avg. probability (%)", "Float"),
        ], rows);
    }
}
