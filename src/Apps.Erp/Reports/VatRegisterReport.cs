using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Register PPN per masa: faktur pajak keluaran & masukan (retur bernilai negatif), dan PPN kurang/lebih bayar.</summary>
public sealed class VatRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-vat-register";
    public string Title => "VAT Register";
    public string Description => "Output and input tax invoices per tax period (returns negative), with VAT payable (output − input). Cancelled and replaced tax invoices are excluded.";
    public string DocType => "TaxInvoice";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From (tax period)", "Date"), new("to", "To (tax period)", "Date"),
        new("direction", "Direction", "Select", ["Output", "Input"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var direction = Rpt.Opt(f, "direction");
        var docs = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(d => d.Data.Str("status") is not ("Cancelled" or "Replaced"))
            .Where(d => d.Data.Str("tax_period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .Where(d => direction is null || d.Data.Str("direction") == direction)
            .OrderBy(d => d.Data.Str("direction")).ThenBy(d => d.Data.Str("tax_period")).ThenBy(d => d.Data.Str("tax_invoice_date")).ThenBy(d => d.Name);

        var rows = new List<Dictionary<string, object?>>();
        var sums = new Dictionary<string, (decimal Dpp, decimal Vat)>();
        foreach (var d in docs)
        {
            var sign = d.Data.Str("reference_doctype") is "SalesCreditNote" or "PurchaseDebitNote" ? -1m : 1m;
            var dir = d.Data.Str("direction") ?? "";
            var dpp = sign * d.Data.Dec("tax_base"); var vat = sign * d.Data.Dec("vat_amount");
            var s = sums.GetValueOrDefault(dir); sums[dir] = (s.Dpp + dpp, s.Vat + vat);
            rows.Add(Rpt.Row(("direction", dir), ("tax_period", d.Data.Str("tax_period")), ("tax_invoice_no", d.Data.Str("tax_invoice_no")),
                ("tax_invoice_date", d.Data.Str("tax_invoice_date")), ("party", d.Data.Str("party")), ("reference", $"{d.Data.Str("reference_doctype")} {d.Data.Str("reference_name")}"),
                ("status", d.Data.Str("status")), ("tax_base", dpp), ("other_tax_base", sign * d.Data.Dec("other_tax_base")), ("vat_amount", vat)));
        }
        foreach (var (dir, s) in sums.OrderBy(x => x.Key))
            rows.Add(Rpt.Row(("direction", $"Total {dir.ToLowerInvariant()}"), ("tax_base", s.Dpp), ("vat_amount", s.Vat)));
        if (direction is null)
        {
            var net = sums.GetValueOrDefault("Output").Vat - sums.GetValueOrDefault("Input").Vat;
            rows.Add(Rpt.Row(("direction", net >= 0 ? "VAT payable (output − input)" : "VAT overpaid (output − input)"), ("vat_amount", net)));
        }
        return new ReportResult(
        [
            new("direction", "Direction"), new("tax_period", "Tax period"), new("tax_invoice_no", "Tax invoice no."), new("tax_invoice_date", "Date"),
            new("party", "Party"), new("reference", "Source document"), new("status", "Coretax status"),
            new("tax_base", "DPP", "Currency"), new("other_tax_base", "Other tax base", "Currency"), new("vat_amount", "VAT", "Currency"),
        ], rows);
    }
}
