using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Register PPh potong/pungut per masa dan kode pajak.</summary>
public sealed class WithholdingRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-withholding-register";
    public string Title => "Withholding Tax Register";
    public string Description => "Withholding tax certificates per tax period: those we withhold (payable) and those withheld by customers (tax credit), totalled per tax code.";
    public string DocType => "WithholdingCertificate";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From (tax period)", "Date"), new("to", "To (tax period)", "Date"),
        new("direction", "Direction", "Select", ["We withhold", "Withheld by counterparty"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var direction = Rpt.Opt(f, "direction");
        var docs = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(d => d.DocStatus != DocStatus.Cancelled)
            .Where(d => d.Data.Str("tax_period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .Where(d => direction is null || d.Data.Str("direction") == direction)
            .OrderBy(d => d.Data.Str("direction")).ThenBy(d => d.Data.Str("tax_code")).ThenBy(d => d.Data.Str("certificate_date")).ToList();
        var rows = docs.Select(d => Rpt.Row(("direction", d.Data.Str("direction")), ("tax_code", d.Data.Str("tax_code")), ("tax_period", d.Data.Str("tax_period")),
            ("certificate_no", d.Data.Str("certificate_no")), ("certificate_date", d.Data.Str("certificate_date")), ("party", d.Data.Str("party")),
            ("reference", d.Data.Str("reference_name")), ("gross_amount", d.Data.Dec("gross_amount")), ("tax_amount", d.Data.Dec("tax_amount")))).ToList();
        foreach (var g in docs.GroupBy(d => (Dir: d.Data.Str("direction"), Code: d.Data.Str("tax_code"))).OrderBy(g => g.Key.Dir).ThenBy(g => g.Key.Code))
            rows.Add(Rpt.Row(("direction", $"Total {g.Key.Dir}"), ("tax_code", g.Key.Code), ("gross_amount", g.Sum(d => d.Data.Dec("gross_amount"))),
                ("tax_amount", g.Sum(d => d.Data.Dec("tax_amount")))));
        return new ReportResult(
        [
            new("direction", "Direction"), new("tax_code", "Tax code"), new("tax_period", "Tax period"), new("certificate_no", "Certificate no."),
            new("certificate_date", "Date"), new("party", "Party"), new("reference", "Source document"),
            new("gross_amount", "Tax base", "Currency"), new("tax_amount", "Tax", "Currency"),
        ], rows);
    }
}
