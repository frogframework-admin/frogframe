using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>
/// Ringkasan PPh 21 tahunan per pegawai tetap, mengikuti baris bukti potong 1721-A1 (dasar pengisian e-Bupot 21). Diambil dari payslip
/// yang sudah submit pada tahun itu; baris 9–20 dari payslip masa terakhir (Desember / bulan keluar). Pegawai yang belum punya payslip
/// masa terakhir ditandai "In progress".
/// </summary>
public sealed class Pph21AnnualReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-pph21-annual";
    public string Title => "PPh 21 Annual Summary (1721-A1)";
    public string Description => "Per employee for the year of the chosen date: salary, other allowances and overtime, employer-paid insurance, bonus/THR, gross, position cost, pension contributions, PTKP, PKP, PPh 21 due and withheld — the lines of the 1721-A1 withholding slip.";
    public string DocType => "Payslip";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("year_of", "Any date in the year", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var year = (Rpt.DateOf(f, "year_of") ?? Rpt.Today(clock)).Year.ToString("0000");
        var m = new Masters(store, user.TenantId);
        var comps = (await m.QueryAsync("SalaryComponent", [], ct, take: 2000)).ToDictionary(x => x.Name, StringComparer.Ordinal);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(p => p.DocStatus == DocStatus.Submitted && p.Data.Str("period") is { } per && per.StartsWith(year, StringComparison.Ordinal))
                 .GroupBy(p => p.Data.Str("employee") ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            decimal salary = 0, allowances = 0, bonus = 0, premiums = 0;
            foreach (var p in g)
            {
                premiums += p.Data.Dec("jkk") + p.Data.Dec("jkm") + p.Data.Dec("kes_employer");
                foreach (var e in p.Data.Rows("earnings"))
                {
                    var c = comps.GetValueOrDefault(e.Str("salary_component") ?? "");
                    if (c is null || !c.Data.Bool("is_taxable")) continue;
                    if (!c.Data.Bool("is_regular")) bonus += e.Dec("amount");
                    else if (c.Data.Bool("in_bpjs_base")) salary += e.Dec("amount");
                    else allowances += e.Dec("amount");
                }
            }
            var last = g.FirstOrDefault(p => p.Data.Bool("is_last_period"));
            var periods = g.Select(p => p.Data.Str("period")!).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var gross = salary + allowances + premiums + bonus;
            var row = Rpt.Row(("employee", g.Key), ("employee_name", g.First().Data.Str("employee_name")), ("ptkp_status", (last ?? g.Last()).Data.Str("ptkp_status")),
                ("months", $"{periods.First()[5..]}–{periods.Last()[5..]}"), ("status", last is null ? "In progress" : "Final"),
                ("l1_salary", salary), ("l3_allowances", allowances), ("l5_insurance", premiums), ("l7_bonus", bonus), ("l8_gross", gross),
                ("l20_withheld", g.Sum(p => p.Data.Dec("pph21"))), ("dtp", g.Sum(p => p.Data.Dec("pph21_dtp"))));
            if (last is not null)
            {
                var ld = last.Data;
                row["l9_position_cost"] = ld.Dec("position_cost"); row["l10_pension"] = ld.Dec("pension_contributions");
                row["l14_net"] = ld.Dec("ytd_taxable_gross") - ld.Dec("position_cost") - ld.Dec("pension_contributions");
                row["l15_ptkp"] = ld.Dec("ptkp_amount"); row["l16_pkp"] = ld.Dec("pkp"); row["l17_tax_due"] = ld.Dec("pph21_annual");
            }
            rows.Add(row);
        }
        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("ptkp_status", "PTKP"), new("months", "Months"), new("status", "Status"),
            new("l1_salary", "1 Salary", "Currency"), new("l3_allowances", "3 Other allowances & overtime", "Currency"),
            new("l5_insurance", "5 Insurance paid by employer", "Currency"), new("l7_bonus", "7 Bonus/THR", "Currency"), new("l8_gross", "8 Gross", "Currency"),
            new("l9_position_cost", "9 Position cost", "Currency"), new("l10_pension", "10 Pension/JHT contributions", "Currency"),
            new("l14_net", "14 Net income", "Currency"), new("l15_ptkp", "15 PTKP", "Currency"), new("l16_pkp", "16 PKP", "Currency"),
            new("l17_tax_due", "17 PPh 21 due", "Currency"), new("l20_withheld", "20 PPh 21 withheld", "Currency"), new("dtp", "of which DTP", "Currency"),
        ], rows);
    }
}
