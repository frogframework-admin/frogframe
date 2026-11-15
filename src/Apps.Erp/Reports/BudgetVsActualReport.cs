using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Anggaran vs realisasi per akun (dan cost center) untuk tahun buku yang memuat tanggal.</summary>
public sealed class BudgetVsActualReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-budget-vs-actual";
    public string Title => "Budget vs Actual";
    public string Description => "Per submitted budget line: annual budget, year-to-date budget (equal months), actual from the ledger up to the date, remaining, and % used.";
    public string DocType => "Budget";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var m = new Masters(store, user.TenantId);
        var fy = (await m.QueryAsync("FiscalYear", [], ct, take: 200)).FirstOrDefault(y => y.Data.Date("start_date") <= asOf && y.Data.Date("end_date") >= asOf);
        var rows = new List<Dictionary<string, object?>>();
        var cols = new List<ReportColumn>
        {
            new("budget", "Budget"), new("cost_center", "Cost center"), new("account", "Account"), new("annual", "Annual budget", "Currency"),
            new("ytd_budget", "Budget to date", "Currency"), new("actual", "Actual to date", "Currency"), new("remaining", "Remaining (annual)", "Currency"),
            new("used", "% of annual used", "Float"),
        };
        if (fy is null) return new ReportResult(cols, rows);
        var start = fy.Data.Date("start_date")!.Value; var end = fy.Data.Date("end_date")!.Value;
        var totalMonths = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month + 1);
        var elapsed = Math.Min(totalMonths, (asOf.Year - start.Year) * 12 + asOf.Month - start.Month + 1);
        var balances = await ledger.GetBalancesAsync(user.TenantId, start, asOf, ct);
        var accounts = await Rpt.AccountsAsync(m, ct);
        foreach (var b in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(b => b.DocStatus == DocStatus.Submitted && b.Data.Str("fiscal_year") == fy.Name).OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            var cc = b.Data.Str("cost_center");
            foreach (var r in b.Data.Rows("accounts"))
            {
                var acc = r.Str("account")!;
                var annual = r.Dec("annual_amount");
                var actual = balances.Where(x => x.Account == acc && (cc is null || x.Department == cc)).Sum(x => x.Debit - x.Credit);
                if (accounts.TryGetValue(acc, out var a) && a.Data.Str("root_type") == "Income") actual = -actual;
                rows.Add(Rpt.Row(("budget", b.Name), ("cost_center", cc ?? "(all)"), ("account", Rpt.AccountLabel(accounts, acc)), ("annual", annual),
                    ("ytd_budget", D.R2(annual * elapsed / totalMonths)), ("actual", actual), ("remaining", annual - actual),
                    ("used", annual == 0 ? (object?)null : Math.Round(actual / annual * 100m, 1))));
            }
        }
        var ta = rows.Sum(r => (decimal)r["annual"]!); var tac = rows.Sum(r => (decimal)r["actual"]!);
        rows.Add(Rpt.Row(("budget", "Total"), ("annual", ta), ("ytd_budget", rows.Sum(r => (decimal)r["ytd_budget"]!)), ("actual", tac),
            ("remaining", ta - tac), ("used", ta == 0 ? (object?)null : Math.Round(tac / ta * 100m, 1))));
        return new ReportResult(cols, rows);
    }
}
