using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Neraca saldo: saldo awal, mutasi periode, dan saldo akhir per akun (sisi debit/kredit).</summary>
public sealed class TrialBalanceReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-trial-balance";
    public string Title => "Trial Balance";
    public string Description => "Opening balance, period movements, and closing balance per account. Opening includes all postings before the start date.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From date", "Date"), new("to", "To date", "Date"),
        new("show", "Accounts", "Select", ["With balance or movement", "All active accounts"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        var all = Rpt.Opt(f, "show") == "All active accounts";
        var accounts = await Rpt.AccountsAsync(m, ct);
        var opening = await Rpt.BalancesAsync(ledger, m, user.TenantId, null, from.AddDays(-1), false, ct);
        var period = await Rpt.BalancesAsync(ledger, m, user.TenantId, from, to, false, ct);

        var codes = accounts.Values.Where(a => !a.Data.Bool("is_group") && all && a.Data.Bool("is_active")).Select(a => a.Name)
            .Concat(opening.Keys).Concat(period.Keys).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        var rows = new List<Dictionary<string, object?>>();
        decimal tod = 0, toc = 0, tpd = 0, tpc = 0, tcd = 0, tcc = 0;
        foreach (var code in codes)
        {
            var o = opening.GetValueOrDefault(code); var p = period.GetValueOrDefault(code);
            var openNet = o.Dr - o.Cr; var closeNet = openNet + p.Dr - p.Cr;
            if (!all && openNet == 0 && p.Dr == 0 && p.Cr == 0) continue;
            var acc = accounts.GetValueOrDefault(code);
            decimal od = Math.Max(openNet, 0), oc = Math.Max(-openNet, 0), cd = Math.Max(closeNet, 0), cc = Math.Max(-closeNet, 0);
            tod += od; toc += oc; tpd += p.Dr; tpc += p.Cr; tcd += cd; tcc += cc;
            rows.Add(Rpt.Row(("account", code), ("account_name", acc?.Data.Str("account_name")), ("root_type", acc?.Data.Str("root_type")),
                ("opening_debit", od), ("opening_credit", oc), ("debit", p.Dr), ("credit", p.Cr), ("closing_debit", cd), ("closing_credit", cc)));
        }
        rows.Add(Rpt.Row(("account", "Total"), ("account_name", tcd == tcc ? "Balanced" : $"Out of balance {tcd - tcc:N2}"), ("root_type", null),
            ("opening_debit", tod), ("opening_credit", toc), ("debit", tpd), ("credit", tpc), ("closing_debit", tcd), ("closing_credit", tcc)));
        return new ReportResult(
        [
            new("account", "Account"), new("account_name", "Account name"), new("root_type", "Root type"),
            new("opening_debit", "Opening debit", "Currency"), new("opening_credit", "Opening credit", "Currency"),
            new("debit", "Debit", "Currency"), new("credit", "Credit", "Currency"),
            new("closing_debit", "Closing debit", "Currency"), new("closing_credit", "Closing credit", "Currency"),
        ], rows);
    }
}
