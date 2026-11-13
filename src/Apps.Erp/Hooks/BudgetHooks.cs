using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

public sealed class Budget { }

/// <summary>
/// Kontrol anggaran saat posting beban: untuk tiap (akun, cost center) yang bertambah di sisi debit, realisasi tahun buku (dari buku besar)
/// + posting ini dibandingkan dengan anggaran yang di-submit (seluruh tahun, atau year-to-date = tahunan × bulan berjalan / 12).
/// Warn = pesan, Stop = ditolak. Dipanggil dari posting faktur pembelian/penjualan, jurnal, klaim biaya, dan payroll.
/// </summary>
internal static class BudgetControl
{
    public static async Task CheckAsync(HookContext c, Masters m, ILedgerReader ledger, IReadOnlyList<LedgerLine> lines, string? company, CancellationToken ct)
    {
        if (company is null || lines.Count == 0) return;
        var increases = lines.GroupBy(l => (l.Account, l.Department)).Select(g => (g.Key.Account, g.Key.Department, Amount: g.Sum(l => l.Debit - l.Credit)))
            .Where(x => x.Amount > 0).ToList();
        if (increases.Count == 0) return;
        var date = lines[0].PostingDate;
        var fy = (await m.QueryAsync("FiscalYear", [], ct, take: 200))
            .FirstOrDefault(y => y.Data.Date("start_date") <= date && y.Data.Date("end_date") >= date);
        if (fy is null) return;
        var budgets = (await m.QueryAsync("Budget", [new FilterClause("fiscal_year", "=", fy.Name)], ct, take: 500))
            .Where(b => b.DocStatus == DocStatus.Submitted && b.Data.Str("company") == company && b.Data.Str("action_if_exceeded") != "Ignore").ToList();
        if (budgets.Count == 0) return;

        var start = fy.Data.Date("start_date")!.Value; var end = fy.Data.Date("end_date")!.Value;
        IReadOnlyList<LedgerBalance>? yearBalances = null, ytdBalances = null;
        foreach (var (account, dept, amount) in increases)
            foreach (var b in budgets)
            {
                var row = b.Data.Rows("accounts").FirstOrDefault(r => r.Str("account") == account);
                if (row is null) continue;
                var cc = b.Data.Str("cost_center");
                if (cc is not null && cc != dept) continue;
                var ytd = b.Data.Str("control_basis") == "Year-to-date (equal months)";
                var annual = row.Dec("annual_amount");
                var totalMonths = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month + 1);
                var elapsed = Math.Min(totalMonths, (date.Year - start.Year) * 12 + date.Month - start.Month + 1);
                var limit = ytd ? D.R2(annual * elapsed / totalMonths) : annual;
                var balances = ytd ? ytdBalances ??= await ledger.GetBalancesAsync(c.Doc.TenantId, start, date, ct)
                                   : yearBalances ??= await ledger.GetBalancesAsync(c.Doc.TenantId, start, end, ct);
                var actual = balances.Where(x => x.Account == account && (cc is null || x.Department == cc)).Sum(x => x.Debit - x.Credit);
                if (actual + amount <= limit) continue;
                var msg = $"Budget {b.Name} exceeded for {account}{(cc is null ? "" : $" / {cc}")}: actual {actual:N0} + this {amount:N0} = {actual + amount:N0} > {(ytd ? "year-to-date budget" : "budget")} {limit:N0}";
                if (b.Data.Str("action_if_exceeded") == "Stop") throw Errors.One("name", msg);
                c.Messages.Add(msg);
            }
    }
}

/// <summary>Anggaran: satu anggaran aktif per (perusahaan, tahun buku, cost center); akun tidak boleh dobel.</summary>
public sealed class BudgetHook(IDocumentStore store) : IBeforeValidate<Budget>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        foreach (var g in d.Rows("accounts").GroupBy(r => r.Str("account")).Where(g => g.Key is not null && g.Count() > 1))
            errors.Add("accounts", $"{g.Key} appears more than once");
        var dup = (await m.QueryAsync("Budget", [new FilterClause("fiscal_year", "=", d.Str("fiscal_year")), new FilterClause("company", "=", d.Str("company"))], ct, take: 200))
            .FirstOrDefault(b => b.Name != c.Doc.Name && b.DocStatus == DocStatus.Submitted && b.Data.Str("cost_center") == d.Str("cost_center")
                                 && b.Name != d.Str("amended_from"));
        if (dup is not null) errors.Add("cost_center", $"{dup.Name} is already the active budget for this fiscal year and cost center; amend it instead");
        errors.ThrowIfAny();
    }
}
