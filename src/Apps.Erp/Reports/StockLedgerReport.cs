using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Kartu stok: mutasi per item-gudang dalam rentang tanggal dengan saldo berjalan.</summary>
public sealed class StockLedgerReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-stock-ledger";
    public string Title => "Stock Ledger";
    public string Description => "Stock card: every movement per item and warehouse in the period with running quantity and value (opening balance first).";
    public string DocType => "StockEntry";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From date", "Date"), new("to", "To date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        var moves = await Stock.AllAsync(new Masters(store, user.TenantId), null, null, ct);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in moves.Where(x => x.Date <= to).GroupBy(x => (x.Item, x.Warehouse)).OrderBy(g => g.Key.Item, StringComparer.Ordinal).ThenBy(g => g.Key.Warehouse))
        {
            decimal qty = g.Where(x => x.Date < from).Sum(x => x.Qty), value = g.Where(x => x.Date < from).Sum(x => x.Value);
            var inPeriod = g.Where(x => x.Date >= from).ToList();
            if (inPeriod.Count == 0) continue;
            rows.Add(Rpt.Row(("date", D.Iso(from)), ("item", g.Key.Item), ("warehouse", g.Key.Warehouse), ("voucher", "Opening"),
                ("qty_in", null), ("qty_out", null), ("value_change", null), ("balance_qty", qty), ("balance_value", D.R2(value))));
            foreach (var x in inPeriod)
            {
                qty += x.Qty; value += x.Value;
                rows.Add(Rpt.Row(("date", D.Iso(x.Date)), ("item", x.Item), ("warehouse", x.Warehouse), ("voucher", $"{x.DocType} {x.DocName}"),
                    ("qty_in", x.Qty > 0 ? x.Qty : (object?)null), ("qty_out", x.Qty < 0 ? -x.Qty : (object?)null), ("value_change", x.Value),
                    ("balance_qty", qty), ("balance_value", D.R2(value))));
            }
        }
        return new ReportResult(
        [
            new("date", "Date"), new("item", "Item"), new("warehouse", "Warehouse"), new("voucher", "Document"),
            new("qty_in", "In", "Float"), new("qty_out", "Out", "Float"), new("value_change", "Value change", "Currency"),
            new("balance_qty", "Balance qty", "Float"), new("balance_value", "Balance value", "Currency"),
        ], rows);
    }
}
