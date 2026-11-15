using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Saldo stok per item & gudang per tanggal (qty, rata-rata, nilai), tanda di bawah titik pesan ulang, dan cek ke saldo akun persediaan.</summary>
public sealed class StockBalanceReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-stock-balance";
    public string Title => "Stock Balance";
    public string Description => "Quantity, moving-average rate, and value per item and warehouse as of a date, with items below their reorder level, and a check against the inventory accounts in the ledger.";
    public string DocType => "StockEntry";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
        [new("as_of", "As of date", "Date"), new("show", "Show", "Select", ["Items in stock", "Below reorder level", "All movements (incl. zero)"])];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var show = Rpt.Opt(f, "show") ?? "Items in stock";
        var m = new Masters(store, user.TenantId);
        var bins = Stock.Bins(await Stock.AllAsync(m, null, null, ct), asOf);
        var items = (await m.QueryAsync("Item", [new FilterClause("is_stock_item", "=", true)], ct, take: 50_000)).ToDictionary(i => i.Name, StringComparer.Ordinal);
        var totalsPerItem = bins.GroupBy(kv => kv.Key.Item).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.Qty));
        var rows = new List<Dictionary<string, object?>>();
        foreach (var ((item, wh), b) in bins.OrderBy(kv => kv.Key.Item, StringComparer.Ordinal).ThenBy(kv => kv.Key.Wh, StringComparer.Ordinal))
        {
            var it = items.GetValueOrDefault(item);
            var reorder = it?.Data.Dec("reorder_level") ?? 0m;
            var below = reorder > 0 && totalsPerItem[item] < reorder;
            if (show == "Items in stock" && b.Qty == 0 && b.Value == 0) continue;
            if (show == "Below reorder level" && !below) continue;
            rows.Add(Rpt.Row(("item", item), ("item_name", it?.Data.Str("item_name")), ("warehouse", wh), ("qty", b.Qty), ("rate", Math.Round(b.Rate, 2)),
                ("value", D.R2(b.Value)), ("reorder", below ? $"Below {reorder:0.##} (reorder {it?.Data.Dec("reorder_qty"):0.##})" : null)));
        }
        var stockValue = bins.Values.Sum(b => b.Value);
        rows.Add(Rpt.Row(("item", "Total"), ("value", D.R2(stockValue))));
        // Cek ke buku besar: semua akun bertipe Inventory.
        var invAccounts = (await m.QueryAsync("Account", [new FilterClause("account_type", "=", "Inventory")], ct, take: 1000)).Select(a => a.Name).ToHashSet();
        var gl = (await ledger.GetBalancesAsync(user.TenantId, null, asOf, ct)).Where(x => invAccounts.Contains(x.Account)).Sum(x => x.Debit - x.Credit);
        rows.Add(Rpt.Row(("item", "Inventory accounts (ledger)"), ("value", gl)));
        rows.Add(Rpt.Row(("item", gl == D.R2(stockValue) ? "Check: stock = ledger" : "Check: DIFFERENCE (manual journals to inventory accounts?)"), ("value", gl - D.R2(stockValue))));
        return new ReportResult(
        [
            new("item", "Item"), new("item_name", "Item name"), new("warehouse", "Warehouse"), new("qty", "Quantity", "Float"),
            new("rate", "Avg. rate", "Currency"), new("value", "Value", "Currency"), new("reorder", "Reorder"),
        ], rows);
    }
}
