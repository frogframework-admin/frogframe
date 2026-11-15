using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Neraca per tanggal: aset = kewajiban + ekuitas + laba (rugi) yang belum ditutup.</summary>
public sealed class BalanceSheetReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-balance-sheet";
    public string Title => "Balance Sheet";
    public string Description => "Assets, liabilities, and equity as of a date. Profit not yet closed to retained earnings is shown as current year earnings.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var accounts = await Rpt.AccountsAsync(m, ct);
        var bal = await Rpt.BalancesAsync(ledger, m, user.TenantId, null, asOf, false, ct);
        string Root(string code) => accounts.TryGetValue(code, out var a) ? a.Data.Str("root_type") ?? "" : "";
        var rows = new List<Dictionary<string, object?>>();
        var totals = new Dictionary<string, decimal>();
        foreach (var root in new[] { "Asset", "Liability", "Equity" })
        {
            rows.Add(Rpt.Row(("label", root.ToUpperInvariant()), ("amount", null)));
            decimal sub = 0;
            foreach (var code in bal.Keys.Where(c => Root(c) == root).OrderBy(c => c, StringComparer.Ordinal))
            {
                var (dr, cr) = bal[code];
                var amount = root == "Asset" ? dr - cr : cr - dr;
                if (amount == 0) continue;
                sub += amount;
                rows.Add(Rpt.Row(("label", $"    {Rpt.AccountLabel(accounts, code)}"), ("amount", amount)));
            }
            if (root == "Equity")
            {
                // Laba (rugi) akun laba rugi yang belum ditutup lewat Tutup Buku Tahunan.
                var unclosed = bal.Where(kv => Root(kv.Key) is "Income" or "Expense").Sum(kv => kv.Value.Cr - kv.Value.Dr);
                if (unclosed != 0) { sub += unclosed; rows.Add(Rpt.Row(("label", "    Current year earnings (not yet closed)"), ("amount", unclosed))); }
            }
            totals[root] = sub;
            rows.Add(Rpt.Row(("label", $"Total {root.ToLowerInvariant()}"), ("amount", sub)));
        }
        var diff = totals["Asset"] - totals["Liability"] - totals["Equity"];
        rows.Add(Rpt.Row(("label", "Total liabilities and equity"), ("amount", totals["Liability"] + totals["Equity"])));
        rows.Add(Rpt.Row(("label", diff == 0 ? "Check: balanced" : "Check: OUT OF BALANCE (accounts without a root type?)"), ("amount", diff)));
        return new ReportResult([new("label", "Account"), new("amount", "Amount", "Currency")], rows);
    }
}
