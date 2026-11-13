using Framework.Accounting;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class SalaryComponent { }
public sealed class SalaryAssignment { }
public sealed class PayrollSettings { }
public sealed class Payslip { }
public sealed class PayrollRun { }

/// <summary>
/// Tabel ketentuan yang diisikan ke Payroll Settings saat tabelnya masih kosong. Setelah itu yang dipakai adalah isi Payroll Settings
/// (bisa diubah pengguna), bukan konstanta ini. Sumber (diperiksa November 2026):
/// - PTKP: PMK 101/PMK.010/2016. Kategori TER: PP 58/2023 Pasal 2 (A: TK/0, TK/1, K/0; B: TK/2, TK/3, K/1, K/2; C: K/3).
/// - TER bulanan: Lampiran PP 58/2023, sebagaimana dimuat di buku DJP "PPh Pasal 21/26 Cermat Pemotongan" (Januari 2024), tabel 6.2–6.4.
/// - Tarif Pasal 17 ayat (1) huruf a UU PPh sebagaimana diubah UU HPP: 5% s.d. 60 jt, 15% s.d. 250 jt, 25% s.d. 500 jt, 30% s.d. 5 M, 35%.
/// </summary>
internal static class PayrollDefaults
{
    public static readonly (string Status, decimal Amount, string Category)[] Ptkp =
    [
        ("TK/0", 54_000_000, "A"), ("TK/1", 58_500_000, "A"), ("TK/2", 63_000_000, "B"), ("TK/3", 67_500_000, "B"),
        ("K/0", 58_500_000, "A"), ("K/1", 63_000_000, "B"), ("K/2", 67_500_000, "B"), ("K/3", 72_000_000, "C"),
    ];

    public static readonly (decimal UpTo, decimal Rate)[] Article17 =
        [(60_000_000, 5), (250_000_000, 15), (500_000_000, 25), (5_000_000_000, 30), (0, 35)];

    // (batas atas penghasilan bruto bulanan, tarif %); batas 0 = baris terakhir tanpa batas atas.
    public static readonly (decimal UpTo, decimal Rate)[] TerA =
    [
        (5_400_000, 0), (5_650_000, 0.25m), (5_950_000, 0.5m), (6_300_000, 0.75m), (6_750_000, 1), (7_500_000, 1.25m), (8_550_000, 1.5m),
        (9_650_000, 1.75m), (10_050_000, 2), (10_350_000, 2.25m), (10_700_000, 2.5m), (11_050_000, 3), (11_600_000, 3.5m), (12_500_000, 4),
        (13_750_000, 5), (15_100_000, 6), (16_950_000, 7), (19_750_000, 8), (24_150_000, 9), (26_450_000, 10), (28_000_000, 11),
        (30_050_000, 12), (32_400_000, 13), (35_400_000, 14), (39_100_000, 15), (43_850_000, 16), (47_800_000, 17), (51_400_000, 18),
        (56_300_000, 19), (62_200_000, 20), (68_600_000, 21), (77_500_000, 22), (89_000_000, 23), (103_000_000, 24), (125_000_000, 25),
        (157_000_000, 26), (206_000_000, 27), (337_000_000, 28), (454_000_000, 29), (550_000_000, 30), (695_000_000, 31), (910_000_000, 32),
        (1_400_000_000, 33), (0, 34),
    ];

    public static readonly (decimal UpTo, decimal Rate)[] TerB =
    [
        (6_200_000, 0), (6_500_000, 0.25m), (6_850_000, 0.5m), (7_300_000, 0.75m), (9_200_000, 1), (10_750_000, 1.5m), (11_250_000, 2),
        (11_600_000, 2.5m), (12_600_000, 3), (13_600_000, 4), (14_950_000, 5), (16_400_000, 6), (18_450_000, 7), (21_850_000, 8),
        (26_000_000, 9), (27_700_000, 10), (29_350_000, 11), (31_450_000, 12), (33_950_000, 13), (37_100_000, 14), (41_100_000, 15),
        (45_800_000, 16), (49_500_000, 17), (53_800_000, 18), (58_500_000, 19), (64_000_000, 20), (71_000_000, 21), (80_000_000, 22),
        (93_000_000, 23), (109_000_000, 24), (129_000_000, 25), (163_000_000, 26), (211_000_000, 27), (374_000_000, 28), (459_000_000, 29),
        (555_000_000, 30), (704_000_000, 31), (957_000_000, 32), (1_405_000_000, 33), (0, 34),
    ];

    public static readonly (decimal UpTo, decimal Rate)[] TerC =
    [
        (6_600_000, 0), (6_950_000, 0.25m), (7_350_000, 0.5m), (7_800_000, 0.75m), (8_850_000, 1), (9_800_000, 1.25m), (10_950_000, 1.5m),
        (11_200_000, 1.75m), (12_050_000, 2), (12_950_000, 3), (14_150_000, 4), (15_550_000, 5), (17_050_000, 6), (19_500_000, 7),
        (22_700_000, 8), (26_600_000, 9), (28_100_000, 10), (30_100_000, 11), (32_600_000, 12), (35_400_000, 13), (38_900_000, 14),
        (43_000_000, 15), (47_400_000, 16), (51_200_000, 17), (55_800_000, 18), (60_400_000, 19), (66_700_000, 20), (74_500_000, 21),
        (83_200_000, 22), (95_600_000, 23), (110_000_000, 24), (134_000_000, 25), (169_000_000, 26), (221_000_000, 27), (390_000_000, 28),
        (463_000_000, 29), (561_000_000, 30), (709_000_000, 31), (965_000_000, 32), (1_419_000_000, 33), (0, 34),
    ];
}

/// <summary>Mesin hitung payroll (murni, tanpa I/O) supaya bisa diuji terpisah.</summary>
internal static class PayrollCalc
{
    public static decimal Rupiah(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);

    /// <summary>Tarif dari tabel bertingkat (batas atas naik; batas 0/kosong = tanpa batas).</summary>
    public static decimal TerRate(IEnumerable<(decimal UpTo, decimal Rate)> table, decimal gross)
    {
        foreach (var (upTo, rate) in table.OrderBy(t => t.UpTo == 0 ? decimal.MaxValue : t.UpTo))
            if (upTo == 0 || gross <= upTo) return rate;
        return 0m;
    }

    /// <summary>PPh setahun dengan tarif progresif Pasal 17.</summary>
    public static decimal Article17(IEnumerable<(decimal UpTo, decimal Rate)> brackets, decimal pkp)
    {
        decimal tax = 0, lower = 0;
        foreach (var (upTo, rate) in brackets.OrderBy(t => t.UpTo == 0 ? decimal.MaxValue : t.UpTo))
        {
            if (pkp <= lower) break;
            var top = upTo == 0 ? pkp : Math.Min(pkp, upTo);
            tax += (top - lower) * rate / 100m;
            lower = upTo == 0 ? pkp : upTo;
        }
        return Math.Floor(tax);
    }

    /// <summary>PKP dibulatkan ke bawah ke ribuan penuh, minimal 0.</summary>
    public static decimal Pkp(decimal netto, decimal ptkp) => Math.Max(0m, Math.Floor((netto - ptkp) / 1000m) * 1000m);
}

/// <summary>Payroll Settings: tabel PTKP, Pasal 17, dan TER diisi otomatis bila kosong; tiap kategori TER wajib punya baris tanpa batas.</summary>
public sealed class PayrollSettingsHook : IBeforeValidate<PayrollSettings>
{
    public Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        if (d.Rows("ptkp_rates").Count == 0)
            d["ptkp_rates"] = PayrollDefaults.Ptkp.Select(p => new Dictionary<string, object?> { ["ptkp_status"] = p.Status, ["amount"] = p.Amount, ["ter_category"] = p.Category }).ToList();
        if (d.Rows("tax_brackets").Count == 0)
            d["tax_brackets"] = PayrollDefaults.Article17.Select(b => new Dictionary<string, object?> { ["up_to"] = b.UpTo == 0 ? (object?)null : b.UpTo, ["rate"] = b.Rate }).ToList();
        if (d.Rows("ter_rates").Count == 0)
            d["ter_rates"] = new[] { ("A", PayrollDefaults.TerA), ("B", PayrollDefaults.TerB), ("C", PayrollDefaults.TerC) }
                .SelectMany(t => t.Item2.Select(r => new Dictionary<string, object?> { ["ter_category"] = t.Item1, ["up_to"] = r.UpTo == 0 ? (object?)null : r.UpTo, ["rate"] = r.Rate }))
                .ToList();

        var errors = new Errors();
        foreach (var cat in new[] { "A", "B", "C" })
            if (!d.Rows("ter_rates").Any(r => r.Str("ter_category") == cat && r.Dec("up_to") == 0))
                errors.Add("ter_rates", $"TER category {cat} needs a last row without an upper limit");
        if (!d.Rows("tax_brackets").Any(r => r.Dec("up_to") == 0)) errors.Add("tax_brackets", "Article 17 rates need a last row without an upper limit");
        foreach (var s in d.Rows("ptkp_rates").GroupBy(r => r.Str("ptkp_status")).Where(g => g.Count() > 1))
            errors.Add("ptkp_rates", $"PTKP status {s.Key} appears more than once");
        errors.ThrowIfAny();
        return Task.CompletedTask;
    }
}

/// <summary>Penugasan gaji: komponen di tabel pendapatan harus Earning, di tabel potongan harus Deduction; satu per (karyawan, tanggal berlaku).</summary>
public sealed class SalaryAssignmentHook(IDocumentStore store) : IBeforeValidate<SalaryAssignment>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        foreach (var (table, type) in new[] { ("earnings", "Earning"), ("deductions", "Deduction") })
        {
            var rows = d.Rows(table);
            for (var i = 0; i < rows.Count; i++)
                if (await m.StrAsync("SalaryComponent", rows[i].Str("salary_component"), "component_type", ct) is { } t && t != type)
                    errors.Add($"{table}[{i}].salary_component", $"{rows[i].Str("salary_component")} is a {t.ToLowerInvariant()}, not a {type.ToLowerInvariant()}");
        }
        var dup = (await m.QueryAsync("SalaryAssignment", [new FilterClause("employee", "=", d.Str("employee")), new FilterClause("from_date", "=", d.Str("from_date"))], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) errors.Add("from_date", $"{dup.Name} already starts on this date for this employee");
        errors.ThrowIfAny();
    }
}

internal static class Payroll
{
    public sealed record Settings(IDictionary<string, object?> Data)
    {
        public decimal Rate(string field) => Data.Dec(field) / 100m;
        public IEnumerable<(decimal UpTo, decimal Rate)> Ter(string category) =>
            Data.Rows("ter_rates").Where(r => r.Str("ter_category") == category).Select(r => (r.Dec("up_to"), r.Dec("rate")));
        public IEnumerable<(decimal UpTo, decimal Rate)> Brackets => Data.Rows("tax_brackets").Select(r => (r.Dec("up_to"), r.Dec("rate")));
        public (decimal Amount, string Category)? Ptkp(string? status) =>
            Data.Rows("ptkp_rates").FirstOrDefault(r => r.Str("ptkp_status") == status) is { } r ? (r.Dec("amount"), r.Str("ter_category") ?? "A") : null;
    }

    public static async Task<Settings> SettingsAsync(Masters m, CancellationToken ct)
    {
        var s = await m.SingleAsync("PayrollSettings", ct);
        if (s.Rows("ter_rates").Count == 0 || s.Rows("ptkp_rates").Count == 0 || s.Rows("tax_brackets").Count == 0)
            throw Errors.One("name", "Open Payroll Settings and save it once to load the PTKP, TER, and Article 17 tables");
        return new Settings(s);
    }

    public static DateOnly PeriodEnd(string period)
    {
        var start = DateOnly.ParseExact(period + "-01", "yyyy-MM-dd");
        return start.AddMonths(1).AddDays(-1);
    }

    /// <summary>Penugasan gaji aktif dengan tanggal berlaku terakhir ≤ akhir periode.</summary>
    public static async Task<Document?> AssignmentAsync(Masters m, string employee, DateOnly periodEnd, CancellationToken ct) =>
        (await m.QueryAsync("SalaryAssignment", [new FilterClause("employee", "=", employee)], ct, take: 200))
            .Where(a => a.Data.Bool("is_active") && a.Data.Date("from_date") <= periodEnd)
            .OrderByDescending(a => a.Data.Date("from_date")).FirstOrDefault();

    public static List<Dictionary<string, object?>> CopyRows(IEnumerable<Dictionary<string, object?>> rows) =>
        rows.Select(r => new Dictionary<string, object?> { ["salary_component"] = r.Str("salary_component"), ["amount"] = r.Dec("amount") }).ToList();
}

/// <summary>
/// Payslip: semua angka dihitung hook setiap simpan.
/// - BPJS: dasar upah = komponen bertanda "BPJS base"; JP dibatasi batas upah JP, Kesehatan dibatasi plafon (dan minimal UMK bila diisi).
/// - PPh 21 masa Januari–November: bruto (pendapatan kena pajak + JKK + JKM + BPJS Kesehatan pemberi kerja) × TER kategori PTKP.
/// - Masa terakhir (Desember, atau bulan karyawan keluar): bruto setahun − biaya jabatan − JHT/JP pegawai − PTKP → PKP dibulatkan ke
///   bawah ribuan → tarif Pasal 17 → dikurangi PPh 21 yang sudah dipotong (termasuk bagian DTP). Hasil negatif dikembalikan ke pegawai.
///   Pegawai yang mulai bekerja di tengah tahun tidak disetahunkan (penghasilan riil setahun dari payslip yang ada).
/// - DTP (PMK 105/2025): bila diaktifkan dan karyawan memenuhi syarat, PPh 21 masa itu dibayarkan tunai ke karyawan (tidak dipotong).
/// </summary>
public sealed class PayslipHook(IDocumentStore store) : IBeforeValidate<Payslip>, IOnCancel<Payslip>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var run = await m.RequireAsync("PayrollRun", d.Str("payroll_run"), "payroll_run", "Payroll run", ct);
        if (run.DocStatus != DocStatus.Draft && c.Doc.DocStatus == DocStatus.Draft && c.Previous is null)
            throw Errors.One("payroll_run", $"{run.Name} is already submitted; create a new payroll run");
        var period = run.Data.Str("period")!;
        var periodEnd = Payroll.PeriodEnd(period);
        d["period"] = period;
        d["company"] = run.Data.Str("company");

        var emp = await m.RequireAsync("Employee", d.Str("employee"), "employee", "Employee", ct);
        if (emp.Data.Str("company") != run.Data.Str("company")) errors.Add("employee", $"{emp.Name} belongs to another company");
        d.SetIfEmpty("cost_center", emp.Data.Str("cost_center"));

        var others = (await m.QueryAsync("Payslip", [new FilterClause("employee", "=", emp.Name), new FilterClause("period", "=", period)], ct, take: 20))
            .Where(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled).ToList();
        if (others.Count > 0) errors.Add("employee", $"{emp.Name} already has payslip {others[0].Name} for {period}");

        var assignment = await Payroll.AssignmentAsync(m, emp.Name, periodEnd, ct);
        if (assignment is null) { errors.Add("employee", $"{emp.Name} has no active salary assignment valid on {D.Iso(periodEnd)}"); errors.ThrowIfAny(); return; }
        d["salary_assignment"] = assignment.Name;
        var fresh = c.Previous is null && d.Rows("earnings").Count == 0;
        if (fresh)
        {
            d["earnings"] = Payroll.CopyRows(assignment.Data.Rows("earnings"));
            d["deductions"] = Payroll.CopyRows(assignment.Data.Rows("deductions"));
        }

        var s = await Payroll.SettingsAsync(m, ct);
        if (fresh) await ProrateAsync(m, s, emp, d, period, ct);
        // Lembur disetujui pada bulan ini ditambahkan sebagai satu baris pendapatan saat payslip dibuat (PP 35/2021).
        if (fresh && s.Data.Str("overtime_component") is { } otComponent
            && await Overtime.PayAsync(m, emp.Name, period, assignment, ct) is { Amount: > 0 } ot)
        {
            var earnings = d.Rows("earnings");
            earnings.Add(new Dictionary<string, object?> { ["salary_component"] = otComponent, ["amount"] = ot.Amount });
            d["earnings"] = earnings;
            c.Messages.Add($"Overtime: {ot.WeightedHours:0.##} paid hours × {ot.HourlyWage:N0} = {ot.Amount:N0}");
        }
        // Komponen: tanda pajak/regular/BPJS dan akun dari master.
        decimal gross = 0, taxable = 0, regular = 0, bpjsBase = 0, other = 0;
        foreach (var (table, type) in new[] { ("earnings", "Earning"), ("deductions", "Deduction") })
        {
            var rows = d.Rows(table);
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var comp = await m.GetAsync("SalaryComponent", r.Str("salary_component"), ct);
                if (comp is null) continue;
                if (comp.Data.Str("component_type") != type) { errors.Add($"{table}[{i}].salary_component", $"{comp.Name} is not a {type.ToLowerInvariant()}"); continue; }
                var amount = PayrollCalc.Rupiah(r.Dec("amount"));
                r["amount"] = amount;
                r["account"] = comp.Data.Str("account");
                if (type == "Earning")
                {
                    r["is_taxable"] = comp.Data.Bool("is_taxable"); r["is_regular"] = comp.Data.Bool("is_regular"); r["in_bpjs_base"] = comp.Data.Bool("in_bpjs_base");
                    gross += amount;
                    if (comp.Data.Bool("is_taxable")) taxable += amount;
                    if (comp.Data.Bool("is_regular")) regular += amount;
                    if (comp.Data.Bool("in_bpjs_base")) bpjsBase += amount;
                }
                else
                {
                    if (comp.Data.Str("account") is null) errors.Add($"{table}[{i}].salary_component", $"Set the GL account of deduction component {comp.Name}");
                    other += amount;
                }
            }
        }
        errors.ThrowIfAny();

        // BPJS
        var a = assignment.Data;
        bool tk = a.Bool("bpjs_employment"), jp = tk && a.Bool("pension_member"), kes = a.Bool("bpjs_health");
        var jpBase = jp ? Math.Min(bpjsBase, s.Data.Dec("jp_wage_cap") is > 0m and var cap ? cap : bpjsBase) : 0m;
        var kesBase = 0m;
        if (kes)
        {
            kesBase = Math.Max(bpjsBase, s.Data.Dec("kes_wage_floor"));
            if (s.Data.Dec("kes_wage_cap") is > 0m and var kcap) kesBase = Math.Min(kesBase, kcap);
        }
        decimal R(decimal v) => PayrollCalc.Rupiah(v);
        var jhtEe = tk ? R(bpjsBase * s.Rate("jht_employee_rate")) : 0m;
        var jhtEr = tk ? R(bpjsBase * s.Rate("jht_employer_rate")) : 0m;
        var jkk = tk ? R(bpjsBase * s.Rate("jkk_rate")) : 0m;
        var jkm = tk ? R(bpjsBase * s.Rate("jkm_rate")) : 0m;
        var jpEe = R(jpBase * s.Rate("jp_employee_rate"));
        var jpEr = R(jpBase * s.Rate("jp_employer_rate"));
        var kesEe = R(kesBase * s.Rate("kes_employee_rate"));
        var kesEr = R(kesBase * s.Rate("kes_employer_rate"));
        d["bpjs_base"] = bpjsBase; d["jp_base"] = jpBase; d["kes_base"] = kesBase;
        d["jht_employee"] = jhtEe; d["jp_employee"] = jpEe; d["kes_employee"] = kesEe;
        d["jht_employer"] = jhtEr; d["jp_employer"] = jpEr; d["jkk"] = jkk; d["jkm"] = jkm; d["kes_employer"] = kesEr;
        d["employer_contributions"] = jhtEr + jpEr + jkk + jkm + kesEr;

        // PPh 21
        var status = emp.Data.Str("ptkp_status");
        var ptkp = s.Ptkp(status) ?? throw Errors.One("employee", $"Set the PTKP status of {emp.Name} (Employee, Identity & Tax)");
        var bruto = taxable + jkk + jkm + kesEr;
        d["ptkp_status"] = status; d["ter_category"] = ptkp.Category; d["taxable_gross"] = bruto;
        var sep = emp.Data.Date("separation_date");
        var periodStart = new DateOnly(periodEnd.Year, periodEnd.Month, 1);
        var isLast = periodEnd.Month == 12 || (sep is { } x && x >= periodStart && x <= periodEnd);
        d["is_last_period"] = isLast;

        decimal pph;
        if (!isLast)
        {
            var rate = PayrollCalc.TerRate(s.Ter(ptkp.Category), bruto);
            d["ter_rate"] = rate;
            pph = Math.Floor(bruto * rate / 100m);
            foreach (var f in new[] { "ytd_taxable_gross", "position_cost", "pension_contributions", "ptkp_amount", "pkp", "pph21_annual", "pph21_before" }) d[f] = null;
        }
        else
        {
            d["ter_rate"] = null;
            var year = periodEnd.Year.ToString("0000");
            var earlier = (await m.QueryAsync("Payslip", [new FilterClause("employee", "=", emp.Name)], ct, take: 500))
                .Where(p => p.Name != c.Doc.Name && p.DocStatus == DocStatus.Submitted && p.Data.Str("company") == d.Str("company")
                            && p.Data.Str("period") is { } pp && pp.StartsWith(year, StringComparison.Ordinal) && string.CompareOrdinal(pp, period) < 0)
                .ToList();
            var months = earlier.Count + 1;
            var yearGross = earlier.Sum(p => p.Data.Dec("taxable_gross")) + bruto;
            var positionCost = Math.Min(yearGross * s.Rate("position_cost_rate"), s.Data.Dec("position_cost_monthly_cap") * months);
            var pension = earlier.Sum(p => p.Data.Dec("jht_employee") + p.Data.Dec("jp_employee")) + jhtEe + jpEe;
            var pkp = PayrollCalc.Pkp(yearGross - positionCost - pension, ptkp.Amount);
            var annual = PayrollCalc.Article17(s.Brackets, pkp);
            var before = earlier.Sum(p => p.Data.Dec("pph21"));
            pph = annual - before;
            d["ytd_taxable_gross"] = yearGross; d["position_cost"] = R(positionCost); d["pension_contributions"] = pension;
            d["ptkp_amount"] = ptkp.Amount; d["pkp"] = pkp; d["pph21_annual"] = annual; d["pph21_before"] = before;
        }
        d["pph21"] = pph;

        // DTP: diusulkan saat payslip dibuat; setelah itu mengikuti centang pengguna.
        var dtpOn = s.Data.Bool("dtp_enabled") && (s.Data.Date("dtp_valid_until") is not { } until || periodEnd <= until);
        if (c.Previous is null) d["dtp_eligible"] = dtpOn && regular <= s.Data.Dec("dtp_gross_limit") && (emp.Data.Str("npwp") ?? emp.Data.Str("nik")) is not null;
        var dtp = dtpOn && d.Bool("dtp_eligible") ? Math.Max(pph, 0m) : 0m;
        d["pph21_dtp"] = dtp;

        var employeeContrib = jhtEe + jpEe + kesEe;
        d["gross_earnings"] = gross;
        d["employee_contributions"] = employeeContrib;
        d["pph21_withheld"] = pph - dtp;
        d["other_deductions"] = other;
        d["net_pay"] = gross - employeeContrib - (pph - dtp) - other;
    }

    /// <summary>
    /// Proporsi gaji saat payslip dibuat: hari kerja periode (kalender libur & libur mingguan karyawan), dibatasi tanggal masuk/keluar,
    /// dikurangi cuti tidak dibayar yang disetujui dan (opsional) absensi Absent. Pendapatan regular dikali hari dibayar ÷ hari kerja.
    /// Setelah dibuat, nominal bisa diubah manual; hook tidak memproporsikan ulang.
    /// </summary>
    private static async Task ProrateAsync(Masters m, Payroll.Settings s, Document emp, IDictionary<string, object?> d, string period, CancellationToken ct)
    {
        var end = Payroll.PeriodEnd(period); var start = new DateOnly(end.Year, end.Month, 1);
        var working = await LeaveMath.WorkingDaysAsync(m, emp, start, end, false, ct);
        if (working <= 0) return;
        var from = emp.Data.Date("hire_date") is { } h && h > start ? h : start;
        var to = emp.Data.Date("separation_date") is { } x && x < end ? x : end;
        var employed = to < from ? 0m : from == start && to == end ? working : await LeaveMath.WorkingDaysAsync(m, emp, from, to, false, ct);

        decimal unpaid = 0;
        if (s.Data.Bool("deduct_unpaid_leave"))
            foreach (var l in (await m.QueryAsync("LeaveApplication", [new FilterClause("employee", "=", emp.Name)], ct, take: 500))
                     .Where(l => l.WorkflowState == Approval.Approved && l.Data.Date("from_date") <= to && l.Data.Date("to_date") >= from))
            {
                if ((await m.GetAsync("LeaveType", l.Data.Str("leave_type"), ct))?.Data.Bool("is_paid") != false) continue;
                var a = l.Data.Date("from_date")!.Value; var b = l.Data.Date("to_date")!.Value;
                unpaid += l.Data.Bool("half_day") ? 0.5m : await LeaveMath.WorkingDaysAsync(m, emp, a > from ? a : from, b < to ? b : to, false, ct);
            }
        decimal absent = 0;
        if (s.Data.Bool("deduct_absence"))
            absent = (await m.QueryAsync("Attendance", [new FilterClause("employee", "=", emp.Name)], ct, take: 5000))
                .Count(x => x.Data.Str("status") == "Absent" && x.Data.Date("attendance_date") is { } ad && ad >= from && ad <= to);

        var payable = Math.Max(0m, employed - unpaid - absent);
        d["working_days"] = working; d["unpaid_leave_days"] = unpaid; d["absent_days"] = absent; d["payable_days"] = payable;
        if (payable >= working) return;
        var factor = payable / working;
        foreach (var r in d.Rows("earnings"))
            if ((await m.GetAsync("SalaryComponent", r.Str("salary_component"), ct))?.Data.Bool("is_regular") == true)
                r["amount"] = PayrollCalc.Rupiah(r.Dec("amount") * factor);
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var run = await new Masters(store, c.Doc.TenantId).GetAsync("PayrollRun", c.Doc.Data.Str("payroll_run"), ct);
        if (run is not null && run.DocStatus == DocStatus.Submitted)
            throw Errors.One("name", $"{run.Name} is submitted; cancel the payroll run first");
    }
}

/// <summary>
/// Payroll Run: total dari payslip yang sudah submit; satu run aktif per (perusahaan, periode, unit).
/// Submit: semua payslip run harus sudah submit, lalu satu jurnal: Dr beban gaji per komponen & cost center, Dr beban BPJS pemberi kerja;
/// Cr utang gaji per karyawan (against = run, dibayar lewat Payment Entry), Cr utang PPh 21 (setelah DTP), Cr utang BPJS TK & Kesehatan,
/// Cr akun komponen potongan (pihak = karyawan).
/// </summary>
public sealed class PayrollRunHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PayrollRun>, IAfterSave<PayrollRun>, IOnCancel<PayrollRun>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var dup = (await m.QueryAsync("PayrollRun", [new FilterClause("company", "=", d.Str("company")), new FilterClause("period", "=", d.Str("period"))], ct, take: 50))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus != DocStatus.Cancelled && x.Data.Str("organization_unit") == d.Str("organization_unit"));
        if (dup is not null) throw Errors.One("period", $"{dup.Name} already covers this company, period, and unit");
        if (d.Str("period") is { Length: 7 } p && d.Date("posting_date") is null) d["posting_date"] = D.Iso(Payroll.PeriodEnd(p));

        var slips = (await m.QueryAsync("Payslip", [new FilterClause("payroll_run", "=", c.Doc.Name)], ct, take: 10_000))
            .Where(s => s.DocStatus == DocStatus.Submitted).ToList();
        d["employee_count"] = (long)slips.Count;
        d["total_gross"] = slips.Sum(s => s.Data.Dec("gross_earnings"));
        d["total_employer_contributions"] = slips.Sum(s => s.Data.Dec("employer_contributions"));
        d["total_pph21"] = slips.Sum(s => s.Data.Dec("pph21_withheld"));
        d["total_pph21_dtp"] = slips.Sum(s => s.Data.Dec("pph21_dtp"));
        d["total_net_pay"] = slips.Sum(s => s.Data.Dec("net_pay"));
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var all = (await m.QueryAsync("Payslip", [new FilterClause("payroll_run", "=", c.Doc.Name)], ct, take: 10_000)).Where(s => s.DocStatus != DocStatus.Cancelled).ToList();
        if (all.Any(s => s.DocStatus == DocStatus.Draft))
            throw Errors.One("name", $"{all.Count(s => s.DocStatus == DocStatus.Draft)} payslips are still draft; submit or delete them first");
        if (all.Count == 0) throw Errors.One("name", "There are no submitted payslips in this payroll run");

        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        await Periods.CheckAsync(m, c.User, date, Periods.LockPayroll, "posting_date", ct);
        var s = await m.SingleAsync("PayrollSettings", ct);
        string Acc(string field, string label) => s.Str(field) ?? throw Errors.One("name", $"Set '{label}' in Payroll Settings");
        var salaryExpense = Acc("salary_expense_account", "Salary expense");
        var employerExpense = Acc("employer_contribution_account", "Employer BPJS expense");
        var payable = Acc("salary_payable_account", "Salary payable");

        var posting = new Posting(date, d.Str("remarks") ?? $"Payroll {d.Str("period")} {c.Doc.Name}");
        decimal pph = 0, tk = 0, kes = 0;
        foreach (var slip in all)
        {
            var p = slip.Data; var emp = p.Str("employee"); var cc = p.Str("cost_center");
            foreach (var e in p.Rows("earnings")) posting.Dr(e.Str("account") ?? salaryExpense, e.Dec("amount"), costCenter: cc);
            posting.Dr(employerExpense, p.Dec("employer_contributions"), costCenter: cc);
            foreach (var x in p.Rows("deductions")) posting.Cr(x.Str("account")!, x.Dec("amount"), "Employee", emp);
            posting.Cr(payable, p.Dec("net_pay"), "Employee", emp, "PayrollRun", c.Doc.Name);
            pph += p.Dec("pph21_withheld");
            tk += p.Dec("jht_employee") + p.Dec("jht_employer") + p.Dec("jp_employee") + p.Dec("jp_employer") + p.Dec("jkk") + p.Dec("jkm");
            kes += p.Dec("kes_employee") + p.Dec("kes_employer");
        }
        if (pph != 0) posting.Cr(Acc("pph21_payable_account", "PPh 21 payable"), pph); // negatif (lebih bayar Desember) = debit
        if (tk != 0) posting.Cr(Acc("bpjs_employment_payable_account", "BPJS Employment payable"), tk);
        if (kes != 0) posting.Cr(Acc("bpjs_health_payable_account", "BPJS Health payable"), kes);
        if (posting.Difference != 0) throw Errors.One("name", $"Payroll entry is out of balance by {posting.Difference:N2}");
        var lines = posting.Build();
        await BudgetControl.CheckAsync(c, m, ledger, lines, d.Str("company"), ct);
        c.AddLedgers(lines);
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
}

/// <summary>Aksi "Create payslips": draft payslip untuk setiap karyawan aktif perusahaan (dan unit) yang punya penugasan gaji. Idempoten.</summary>
public sealed class PayrollRunCreatePayslipsAction(IDocumentStore store) : IFormAction<PayrollRun>
{
    public string ActionId => "erp.payroll.create_payslips";
    public string Label => "Create payslips";
    public string Icon => "users";
    public string Tone => "primary";

    public bool IsVisible(UserContext user, Document doc) => doc.DocStatus == DocStatus.Draft && (user.HasRole("HR") || user.HasRole("Finance") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var run = ctx.Doc.Data;
        var period = run.Str("period");
        if (period is null) return FormActionResult.Fail("Set the period first");
        var periodEnd = Payroll.PeriodEnd(period);
        var periodStart = new DateOnly(periodEnd.Year, periodEnd.Month, 1);
        var unit = run.Str("organization_unit");
        var existing = (await m.QueryAsync("Payslip", [new FilterClause("period", "=", period)], ct, take: 10_000))
            .Where(s => s.DocStatus != DocStatus.Cancelled).Select(s => s.Data.Str("employee")).ToHashSet();
        var employees = (await m.QueryAsync("Employee", [new FilterClause("company", "=", run.Str("company"))], ct, take: 10_000))
            .Where(e => unit is null || e.Data.Str("organization_unit") == unit)
            .Where(e => e.Data.Date("hire_date") is not { } j || j <= periodEnd)
            .Where(e => e.Data.Str("status") != Hr.Left || (e.Data.Date("separation_date") is { } sep && sep >= periodStart))
            .ToList();

        int created = 0, skipped = 0; var failed = new List<string>();
        foreach (var emp in employees)
        {
            if (existing.Contains(emp.Name)) { skipped++; continue; }
            if (await Payroll.AssignmentAsync(m, emp.Name, periodEnd, ct) is null) { skipped++; continue; }
            try
            {
                await ctx.Docs.InsertAsync(ctx.User, "Payslip", new Dictionary<string, object?> { ["payroll_run"] = ctx.Doc.Name, ["employee"] = emp.Name },
                    $"Created by {ctx.Doc.Name}", ct);
                created++;
            }
            catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException)
            {
                failed.Add($"{emp.Name}: {ex.Message}");
            }
        }
        var msg = $"{created} payslips created, {skipped} skipped (already exists or no salary assignment).";
        return failed.Count == 0 ? FormActionResult.Ok(msg) : FormActionResult.Fail($"{msg} Failed: {string.Join("; ", failed.Take(10))}");
    }
}

/// <summary>Aksi "Submit payslips": submit semua payslip draft run ini sebelum run di-submit.</summary>
public sealed class PayrollRunSubmitPayslipsAction(IDocumentStore store) : IFormAction<PayrollRun>
{
    public string ActionId => "erp.payroll.submit_payslips";
    public string Label => "Submit payslips";
    public string Icon => "check";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) => doc.DocStatus == DocStatus.Draft && (user.HasRole("HR") || user.HasRole("Finance") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var drafts = (await new Masters(store, ctx.Doc.TenantId).QueryAsync("Payslip", [new FilterClause("payroll_run", "=", ctx.Doc.Name)], ct, take: 10_000))
            .Where(s => s.DocStatus == DocStatus.Draft).ToList();
        int done = 0; var failed = new List<string>();
        foreach (var s in drafts)
        {
            try { await ctx.Docs.RunActionAsync(ctx.User, "Payslip", s.Name, DocActions.Submit, s.Version, null, ct); done++; }
            catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidStateException or ConcurrencyException)
            { failed.Add($"{s.Name}: {ex.Message}"); }
        }
        // Run disentuh agar totalnya dihitung ulang dari payslip yang sudah submit.
        var fresh = await ctx.Docs.GetAsync(ctx.User, "PayrollRun", ctx.Doc.Name, ct);
        await ctx.Docs.UpdateAsync(ctx.User, "PayrollRun", ctx.Doc.Name, fresh.Version, new Dictionary<string, object?>(), ct);
        var msg = $"{done} payslips submitted.";
        return failed.Count == 0 ? FormActionResult.Ok(msg) : FormActionResult.Fail($"{msg} Failed: {string.Join("; ", failed.Take(10))}");
    }
}
