using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Laba rugi periode: pendapatan − beban, per akun atau per cost center; jurnal tutup buku dikeluarkan.</summary>
public sealed class ProfitAndLossReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-profit-and-loss";
    public string Title => "Profit and Loss";
    public string Description => "Income minus expense for the period, per account or per cost center. Year-end closing entries are excluded.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From date", "Date"), new("to", "To date", "Date"),
        new("group_by", "Group by", "Select", ["Account", "Cost center"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, 1, 1);
        var accounts = await Rpt.AccountsAsync(m, ct);
        bool IsPl(string code) => accounts.TryGetValue(code, out var a) && a.Data.Str("report_type") == "Profit and Loss";
        string Root(string code) => accounts.TryGetValue(code, out var a) ? a.Data.Str("root_type") ?? "" : "";
        var rows = new List<Dictionary<string, object?>>();

        if (Rpt.Opt(f, "group_by") == "Cost center")
        {
            // Per cost center (LedgerLine.Department), jurnal tutup buku dikeluarkan per (akun, cost center).
            var net = new Dictionary<(string Account, string Cc), decimal>();
            foreach (var x in await ledger.GetBalancesAsync(user.TenantId, from, to, ct))
                net[(x.Account, x.Department ?? "(none)")] = net.GetValueOrDefault((x.Account, x.Department ?? "(none)")) + x.Debit - x.Credit;
            foreach (var pcv in await m.QueryAsync("PeriodClosingVoucher", [], ct, take: 1000))
                if (pcv.DocStatus != DocStatus.Draft)
                    foreach (var e in await ledger.GetVoucherAsync(user.TenantId, "PeriodClosingVoucher", pcv.Name, ct))
                        if (e.PostingDate >= from && e.PostingDate <= to)
                            net[(e.Account, e.Department ?? "(none)")] = net.GetValueOrDefault((e.Account, e.Department ?? "(none)")) - (e.Debit - e.Credit);
            decimal ti = 0, te = 0;
            foreach (var g in net.Where(kv => IsPl(kv.Key.Account)).GroupBy(kv => kv.Key.Cc).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var income = -g.Where(kv => Root(kv.Key.Account) == "Income").Sum(kv => kv.Value);
                var expense = g.Where(kv => Root(kv.Key.Account) == "Expense").Sum(kv => kv.Value);
                if (income == 0 && expense == 0) continue;
                ti += income; te += expense;
                rows.Add(Rpt.Row(("label", g.Key), ("income", income), ("expense", expense), ("net", income - expense)));
            }
            rows.Add(Rpt.Row(("label", "Net profit (loss)"), ("income", ti), ("expense", te), ("net", ti - te)));
            return new ReportResult([new("label", "Cost center"), new("income", "Income", "Currency"), new("expense", "Expense", "Currency"), new("net", "Net", "Currency")], rows);
        }

        var bal = await Rpt.BalancesAsync(ledger, m, user.TenantId, from, to, true, ct);
        decimal totalIncome = 0, totalExpense = 0;
        foreach (var root in new[] { "Income", "Expense" })
        {
            rows.Add(Rpt.Row(("label", root.ToUpperInvariant()), ("amount", null)));
            decimal sub = 0;
            foreach (var code in bal.Keys.Where(c => IsPl(c) && Root(c) == root).OrderBy(c => c, StringComparer.Ordinal))
            {
                var (dr, cr) = bal[code];
                var amount = root == "Income" ? cr - dr : dr - cr;
                if (amount == 0) continue;
                sub += amount;
                rows.Add(Rpt.Row(("label", $"    {Rpt.AccountLabel(accounts, code)}"), ("amount", amount)));
            }
            rows.Add(Rpt.Row(("label", $"Total {root.ToLowerInvariant()}"), ("amount", sub)));
            if (root == "Income") totalIncome = sub; else totalExpense = sub;
        }
        rows.Add(Rpt.Row(("label", "Net profit (loss)"), ("amount", totalIncome - totalExpense)));
        return new ReportResult([new("label", "Account"), new("amount", "Amount", "Currency")], rows);
    }
}
