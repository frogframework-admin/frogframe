using Apps.Erp.Hooks;
using Framework.Expressions;

/// <summary>
/// Payroll formulas (docs/guides/payroll-formulas.md): components calculated from other components, rate tables (grade, level,
/// length of service), attendance, eligibility conditions, calculation order, and the tax allowance gross-up.
/// </summary>
sealed partial class Tests
{
    private static readonly DateOnly PayrollNov2026 = new(2026, 11, 30);

    private static Dictionary<string, RateTable> SampleRateTables() => new(StringComparer.Ordinal)
    {
        ["GAPOK_GRADE"] = new("GAPOK_GRADE", null, [new("G3", null, null, null, 6_000_000m), new("G4", null, null, null, 8_000_000m)]),
        ["BENSIN"] = new("BENSIN", null, [new("STAFF", null, null, null, 20_000m), new("MANAGER", null, null, null, 40_000m)]),
        ["MASA_KERJA"] = new("MASA_KERJA", null,
            [new(null, null, 0m, 3m, 0m), new(null, null, 3m, 5m, 150_000m), new(null, null, 5m, null, 300_000m)]),
    };

    private static List<FormulaComponent> SampleFormulaComponents() =>
    [
        // Listed before GAPOK on purpose: the order comes from the references, not from the list.
        new("Tunjangan Istri", "TUNJ_ISTRI", "Earning", "GAPOK * 10 / 100", "emp.marital_status == 'Married'"),
        new("Gaji Pokok", "GAPOK", "Earning", "lookup('GAPOK_GRADE', emp.grade)", null),
        new("Tunjangan Bensin", "BENSIN", "Earning", "att.present_days * lookup('BENSIN', emp.job_level)", null),
        new("Tunjangan Pulsa", "PULSA", "Earning", "150000", "emp.job_profile in ['SALES', 'SALES-SPV']"),
        new("Tunjangan Masa Kerja", "TMK", "Earning", "lookup_range('MASA_KERJA', emp.years_of_service)", null),
        new("Potongan Absen", "POT_ABSEN", "Deduction", "GAPOK / 21 * att.absent_days", null),
        new("Uang Makan", "MAKAN", "Earning", "div(GAPOK, att.working_days) * 0 + att.present_days * 25000", null),
    ];

    private static Dictionary<string, decimal> RunSampleFormulas(Dictionary<string, object?> emp, Dictionary<string, object?> att)
    {
        var tables = SampleRateTables();
        var results = PayrollFormulas.Evaluate(PayrollFormulas.Order(SampleFormulaComponents()), new FormulaInputs
        {
            Components = new Dictionary<string, decimal>(StringComparer.Ordinal),
            Emp = emp,
            Att = att,
            PeriodEnd = PayrollNov2026,
            Tables = code => tables.GetValueOrDefault(code),
        });
        Assert.True(results.All(r => r.Note.Contains(" = ")), "every row records its formula and inputs");
        return results.ToDictionary(r => r.Component, r => r.Amount, StringComparer.Ordinal);
    }

    [Test]
    public Task Payroll_formulas_grade_percentage_attendance_eligibility_service_and_absence()
    {
        var order = PayrollFormulas.Order(SampleFormulaComponents()).Select(c => c.Variable).ToList();
        Assert.True(order.IndexOf("GAPOK") < order.IndexOf("TUNJ_ISTRI") && order.IndexOf("GAPOK") < order.IndexOf("POT_ABSEN"),
            "components that use GAPOK are calculated after it");

        // Married staff in grade G3, 4 years of service, 18 days present, 2 days absent, not in sales.
        var staff = RunSampleFormulas(
            new() { ["grade"] = "G3", ["job_level"] = "STAFF", ["marital_status"] = "Married", ["job_profile"] = "ADMIN", ["years_of_service"] = 4m },
            new() { ["present_days"] = 18m, ["absent_days"] = 2m, ["working_days"] = 21m });
        Assert.Equal(6_000_000m, staff["Gaji Pokok"], "basic salary from the grade table");
        Assert.Equal(600_000m, staff["Tunjangan Istri"], "10% of basic salary when married");
        Assert.Equal(360_000m, staff["Tunjangan Bensin"], "18 days × staff rate 20,000");
        Assert.True(!staff.ContainsKey("Tunjangan Pulsa"), "phone allowance only for sales job profiles");
        Assert.Equal(150_000m, staff["Tunjangan Masa Kerja"], "3 ≤ 4 years < 5");
        Assert.Equal(571_429m, staff["Potongan Absen"], "6,000,000 / 21 × 2, rounded to whole rupiah");
        Assert.Equal(450_000m, staff["Uang Makan"], "18 days × 25,000");

        // Single sales manager in grade G4, new (0 years), 20 days present, no working days recorded (div by zero guarded).
        var sales = RunSampleFormulas(
            new() { ["grade"] = "G4", ["job_level"] = "MANAGER", ["marital_status"] = "Single", ["job_profile"] = "SALES", ["years_of_service"] = 0m },
            new() { ["present_days"] = 20m, ["absent_days"] = 0m, ["working_days"] = 0m });
        Assert.Equal(8_000_000m, sales["Gaji Pokok"], "grade G4");
        Assert.True(!sales.ContainsKey("Tunjangan Istri") && !sales.ContainsKey("Potongan Absen") && !sales.ContainsKey("Tunjangan Masa Kerja"),
            "no row when the condition is false or the result is 0");
        Assert.Equal(800_000m, sales["Tunjangan Bensin"], "20 days × manager rate 40,000");
        Assert.Equal(150_000m, sales["Tunjangan Pulsa"], "sales job profile");
        return Task.CompletedTask;
    }

    [Test]
    public Task Payroll_formulas_reject_loops_unknown_names_and_bad_tables()
    {
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Order(
            [new("A", "A", "Earning", "B + 1", null), new("B", "B", "Earning", "A * 2", null)]), "loop between two formulas");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Order([new("A", "A", "Earning", "A + 1", null)]), "formula that uses itself");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Parse("GAPOK * (10 / 100"), "syntax error");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Parse("System.IO.File.Delete('x')"), "functions outside the allowlist");

        var unknown = PayrollFormulas.UnknownNames(PayrollFormulas.Parse("GAPOK + TUNJANGAN + emp.grade + emp.nonexistent + att.late_minutes + att.typo + period.month"),
            new HashSet<string> { "GAPOK" }, new HashSet<string> { "grade" });
        Assert.Equal("TUNJANGAN, att.typo, emp.nonexistent", string.Join(", ", unknown), "unknown names are reported, known ones are not");
        Assert.True(!PayrollFormulas.IsValidVariable("emp") && !PayrollFormulas.IsValidVariable("T.Makan") && !PayrollFormulas.IsValidVariable("1A")
                    && PayrollFormulas.IsValidVariable("TUNJ_ISTRI"), "variable name rules");

        var tables = SampleRateTables();
        FormulaInputs Inputs(string grade) => new()
        {
            Components = new Dictionary<string, decimal>(StringComparer.Ordinal),
            Emp = new Dictionary<string, object?> { ["grade"] = grade },
            PeriodEnd = PayrollNov2026,
            Tables = code => tables.GetValueOrDefault(code),
        };
        var basic = new FormulaComponent("Gaji Pokok", "GAPOK", "Earning", "lookup('GAPOK_GRADE', emp.grade)", null);
        var missing = Assert.Throws<ExpressionException>(() => PayrollFormulas.Evaluate([basic], Inputs("G9")), "no row for the key");
        Assert.True(missing.Message.Contains("Gaji Pokok") && missing.Message.Contains("G9"), "error names the component and the key");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Evaluate(
            [basic with { Formula = "lookup('NOPE', emp.grade)" }], Inputs("G3")), "unknown table");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Evaluate(
            [basic with { Formula = "emp.grade" }], Inputs("G3")), "formula must give a number");
        return Task.CompletedTask;
    }

    [Test]
    public Task Payroll_rate_tables_use_latest_version_most_specific_row_and_ranges()
    {
        var table = new RateTable("BENSIN", null,
        [
            new(null, null, null, null, 10_000m),                                     // default row for any level
            new("STAFF", null, null, null, 20_000m),
            new("STAFF", "JAKARTA", null, null, 25_000m),                              // more specific: level + location
            new("STAFF", null, null, null, 22_000m, new DateOnly(2026, 12, 1)),        // next version, not valid yet in November
        ]);
        Assert.Equal(20_000m, PayrollFormulas.Lookup(table, PayrollNov2026, null, "staff", null), "key match ignores case");
        Assert.Equal(25_000m, PayrollFormulas.Lookup(table, PayrollNov2026, null, "STAFF", "Jakarta"), "most specific row wins");
        Assert.Equal(20_000m, PayrollFormulas.Lookup(table, PayrollNov2026, null, "STAFF", "Bandung"), "row with empty key 2 matches any location");
        Assert.Equal(10_000m, PayrollFormulas.Lookup(table, PayrollNov2026, null, "INTERN", null), "default row");
        Assert.Equal(22_000m, PayrollFormulas.Lookup(table, new DateOnly(2026, 12, 31), null, "STAFF", null), "December uses the new version only");
        Assert.Throws<ExpressionException>(() => PayrollFormulas.Lookup(table, new DateOnly(2026, 12, 31), null, "INTERN", null),
            "a new version replaces the whole table, including the default row");
        Assert.Equal("3", PayrollFormulas.KeyText(3.0m), "numeric keys such as grade level are matched as text without decimals");

        var service = new RateTable("MASA_KERJA", null, [new(null, null, 0m, 3m, 0m), new(null, null, 3m, 5m, 150_000m), new(null, null, 5m, null, 300_000m)]);
        Assert.Equal("0,0,150000,150000,300000,300000",
            string.Join(",", new[] { 0m, 2.99m, 3m, 4m, 5m, 30m }.Select(y => PayrollFormulas.Lookup(service, PayrollNov2026, y, null, null))),
            "From ≤ value < Up to");

        Assert.Equal(35, PayrollFormulas.CompletedMonths(new DateOnly(2023, 12, 1), PayrollNov2026), "35 whole months");
        Assert.Equal(36, PayrollFormulas.CompletedMonths(new DateOnly(2023, 11, 30), PayrollNov2026), "3 years on the anniversary");
        Assert.Equal(1, PayrollFormulas.CompletedMonths(new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 29)), "month end counts as complete");
        return Task.CompletedTask;
    }

    [Test]
    public Task Payroll_tax_allowance_gross_up_equals_the_tax_it_carries()
    {
        foreach (var (category, ter) in new[] { ("A", PayrollDefaults.TerA), ("B", PayrollDefaults.TerB), ("C", PayrollDefaults.TerC) })
            foreach (var gross in new[] { 4_000_000m, 5_400_001m, 9_999_999m, 15_000_000m, 25_000_000m, 150_000_000m, 1_500_000_000m })
            {
                decimal Tax(decimal t) => Math.Floor((gross + t) * PayrollCalc.TerRate(ter, gross + t) / 100m);
                var allowance = PayrollFormulas.SolveGrossUp(Tax);
                Assert.Equal(Tax(allowance), allowance, $"TER {category}, gross {gross:N0}: allowance = PPh 21 on gross + allowance");
            }

        // Last period (Article 17, annual): the allowance settles too.
        decimal Annual(decimal t)
        {
            var yearGross = 120_000_000m + t;
            var positionCost = Math.Min(yearGross * 0.05m, 6_000_000m);
            return PayrollCalc.Article17(PayrollDefaults.Article17, PayrollCalc.Pkp(yearGross - positionCost - 2_400_000m, 54_000_000m)) - 1_000_000m;
        }
        var december = PayrollFormulas.SolveGrossUp(Annual);
        Assert.Equal(Math.Max(0m, Annual(december)), december, "annual gross-up");
        Assert.Equal(0m, PayrollFormulas.SolveGrossUp(_ => -500_000m), "overpaid tax gives no allowance");
        return Task.CompletedTask;
    }
}
