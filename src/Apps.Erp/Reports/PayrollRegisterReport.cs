using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Security;

namespace Apps.Erp.Reports;

/// <summary>Register payroll per masa: dasar SPT Masa PPh 21 (e-Bupot 21) dan iuran BPJS. Hanya payslip yang sudah submit.</summary>
public sealed class PayrollRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-payroll-register";
    public string Title => "Payroll Register";
    public string Description => "Submitted payslips per period: gross income (bruto), PPh 21 (TER or annual), DTP, BPJS employee and employer contributions, and net pay. Basis for the monthly PPh 21 return and BPJS payments.";
    public string DocType => "Payslip";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From (period)", "Date"), new("to", "To (period)", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var slips = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(s => s.DocStatus == DocStatus.Submitted && s.Data.Str("period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .OrderBy(s => s.Data.Str("period")).ThenBy(s => s.Data.Str("employee"), StringComparer.Ordinal).ToList();
        string[] money = ["taxable_gross", "pph21", "pph21_dtp", "pph21_withheld", "employee_contributions", "employer_contributions", "gross_earnings", "net_pay"];
        var rows = slips.Select(s =>
        {
            var r = Rpt.Row(("period", s.Data.Str("period")), ("employee", s.Data.Str("employee")), ("employee_name", s.Data.Str("employee_name")),
                ("ptkp_status", s.Data.Str("ptkp_status")), ("method", s.Data.Bool("is_last_period") ? "Annual (Art. 17)" : $"TER {s.Data.Str("ter_category")} {s.Data.Dec("ter_rate"):0.##}%"));
            foreach (var k in money) r[k] = s.Data.Dec(k);
            return r;
        }).ToList();
        var total = Rpt.Row(("period", "Total"), ("employee", $"{slips.Count} payslips"), ("employee_name", null), ("ptkp_status", null), ("method", null));
        foreach (var k in money) total[k] = slips.Sum(s => s.Data.Dec(k));
        rows.Add(total);
        return new ReportResult(
        [
            new("period", "Period"), new("employee", "Employee"), new("employee_name", "Name"), new("ptkp_status", "PTKP"), new("method", "Method"),
            new("gross_earnings", "Gross earnings", "Currency"), new("taxable_gross", "Bruto (PPh 21)", "Currency"), new("pph21", "PPh 21", "Currency"),
            new("pph21_dtp", "DTP", "Currency"), new("pph21_withheld", "PPh 21 withheld", "Currency"),
            new("employee_contributions", "BPJS employee", "Currency"), new("employer_contributions", "BPJS employer", "Currency"), new("net_pay", "Net pay", "Currency"),
        ], rows);
    }
}
