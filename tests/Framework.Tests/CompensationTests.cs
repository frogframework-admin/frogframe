using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Actions;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Compensation planning: guideline matrix, worksheet rules and budget, approval, and salary changes applied.</summary>
sealed partial class Tests
{
    private static List<Dictionary<string, object?>> MeritMatrix() =>
    [
        new() { ["rating"] = "Outstanding", ["compa_band"] = "0.90–1.10", ["min_percent"] = 7m, ["target_percent"] = 9m, ["max_percent"] = 11m },
        new() { ["rating"] = "Outstanding", ["compa_band"] = "Any", ["min_percent"] = 6m, ["target_percent"] = 8m, ["max_percent"] = 10m },
        new() { ["rating"] = "Exceeds expectations", ["compa_band"] = "Any", ["min_percent"] = 3m, ["target_percent"] = 5m, ["max_percent"] = 8m },
        new() { ["rating"] = "Meets expectations", ["compa_band"] = "Any", ["min_percent"] = 3m, ["target_percent"] = 5m, ["max_percent"] = 8m },
        new() { ["rating"] = "Needs improvement", ["compa_band"] = "Any", ["min_percent"] = 0m, ["target_percent"] = 1m, ["max_percent"] = 2m },
        new() { ["rating"] = "Unsatisfactory", ["compa_band"] = "Any", ["min_percent"] = 0m, ["target_percent"] = 0m, ["max_percent"] = 0m },
        new() { ["rating"] = "Not rated", ["compa_band"] = "Any", ["min_percent"] = 0m, ["target_percent"] = 2m, ["max_percent"] = 3m },
    ];

    [Test]
    public Task Compensation_guideline_and_line_math()
    {
        Assert.Equal("Below 0.90", Comp.Band(0.85m), "below the range midpoint band");
        Assert.Equal("0.90–1.10", Comp.Band(0.90m), "0.90 is inside");
        Assert.Equal("0.90–1.10", Comp.Band(1.10m), "1.10 is inside");
        Assert.Equal("Above 1.10", Comp.Band(1.11m), "above");
        Assert.True(Comp.Band(null) is null, "no range, no band");

        var rules = MeritMatrix().Cast<IDictionary<string, object?>>().ToList();
        Assert.Equal(9m, Comp.MeritGuideline(rules, "Outstanding", "0.90–1.10").Target, "band-specific row wins");
        Assert.Equal(8m, Comp.MeritGuideline(rules, "Outstanding", "Above 1.10").Target, "falls back to the Any row");
        Assert.Equal(0m, Comp.MeritGuideline([], "Outstanding", null).Max, "no row: 0–0–0");

        IReadOnlyList<IDictionary<string, object?>> bonus = [new Dictionary<string, object?> { ["rating"] = "Outstanding", ["target_percent"] = 10m, ["max_percent"] = 15m }];
        var line = new Dictionary<string, object?>
        {
            ["rating"] = "Outstanding", ["current_base"] = 10_000_000m, ["range_mid"] = 10_000_000m,
        };
        Comp.RecalcLine(line, rules, bonus);
        Assert.Equal(1.00m, line["compa_ratio"], "compa-ratio = base / mid");
        Assert.Equal(9m, line.Dec("merit_percent"), "starts at the guideline target");
        Assert.Equal(900_000m, line.Dec("merit_amount"), "9% of 10 million");
        Assert.Equal(10_900_000m, line.Dec("new_base"), "new base");
        Assert.Equal(12_000_000m, line.Dec("bonus_amount"), "bonus target 10% of annual base");
        Assert.True(!line.Bool("outside_guideline"), "target is inside the guideline");

        line["merit_percent"] = 12m;
        Comp.RecalcLine(line, rules, bonus);
        Assert.True(line.Bool("outside_guideline"), "12% is above the 11% maximum");

        var sheet = new Dictionary<string, object?> { ["lines"] = new List<Dictionary<string, object?>> { line } };
        Comp.Totals(sheet, 4m, 10m);
        Assert.Equal(400_000m, sheet.Dec("merit_budget"), "4% of 10 million");
        Assert.Equal(-800_000m, sheet.Dec("merit_remaining"), "1.2 million planned");
        Assert.True(Comp.OverBudget(sheet), "over budget");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Compensation_cycle_from_worksheets_to_salary_assignments()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, m) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var comben = MatrixUser("HR-COMBEN", ErpRoles.HrComben, "Employee");
        var talent = MatrixUser("HR-TALENT", ErpRoles.HrTalent, "Employee");
        var mgrA = MatrixUser("MGR-A");
        var pmB = MatrixUser("PM-B");
        var registry = sp.GetRequiredService<MetadataRegistry>();

        // Pay data: base component, grade range for STAFF-A, salaries for STAFF-A and STAFF-B only.
        await docs.InsertAsync(sys, "SalaryComponent", new Dictionary<string, object?> { ["component_name"] = "Basic", ["component_type"] = "Earning", ["is_active"] = true, ["is_regular"] = true });
        await docs.SetSingleAsync(sys, "PayrollSettings", new Dictionary<string, object?> { ["base_salary_component"] = "Basic" });
        await docs.InsertAsync(sys, "Grade", new Dictionary<string, object?>
        {
            ["grade_code"] = "G5", ["grade_name"] = "Grade 5", ["grade_level"] = 5L, ["is_active"] = true,
            ["pay_ranges"] = new List<Dictionary<string, object?>>
            {
                new() { ["company"] = "ACME", ["currency"] = "IDR", ["min_salary"] = 8_000_000m, ["mid_salary"] = 10_000_000m, ["max_salary"] = 12_000_000m, ["valid_from"] = "2026-01-01" },
            },
        });
        var staffA = await docs.GetAsync(sys, "Employee", "STAFF-A");
        await docs.UpdateAsync(sys, "Employee", "STAFF-A", staffA.Version, new Dictionary<string, object?> { ["grade"] = "G5" });
        foreach (var (emp, pay) in new[] { ("STAFF-A", 10_000_000m), ("STAFF-B", 8_000_000m) })
            await docs.InsertAsync(sys, "SalaryAssignment", new Dictionary<string, object?>
            {
                ["employee"] = emp, ["company"] = "ACME", ["from_date"] = "2026-01-01", ["is_active"] = true,
                ["earnings"] = new List<Dictionary<string, object?>> { new() { ["salary_component"] = "Basic", ["amount"] = pay } },
            });

        // A completed appraisal gives STAFF-A a rating; STAFF-B stays "Not rated".
        await docs.InsertAsync(sys, "AppraisalCycle", new Dictionary<string, object?>
        {
            ["cycle_name"] = "FY2026", ["company"] = "ACME", ["start_date"] = "2026-01-01", ["end_date"] = "2026-12-31", ["status"] = "In progress",
        });
        var apr = await docs.InsertAsync(talent, "Appraisal", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["appraisal_cycle"] = "FY2026",
            ["goals"] = new List<Dictionary<string, object?>> { new() { ["goal"] = "Deliver", ["weight"] = 100m, ["self_score"] = 4, ["manager_score"] = 5 } },
        });
        await docs.RunActionAsync(talent, "Appraisal", apr.Name, "Submit self review", apr.Version, null);
        var aprSelf = await docs.GetAsync(sys, "Appraisal", apr.Name);
        await docs.RunActionAsync(talent, "Appraisal", apr.Name, "Complete review", aprSelf.Version, null);
        var rating = (await docs.GetAsync(sys, "Appraisal", apr.Name)).Data.Str("rating");
        Assert.True(rating is not null, "the appraisal has a rating");

        var cycle = await docs.InsertAsync(comben, "CompensationCycle", new Dictionary<string, object?>
        {
            ["cycle_name"] = "MERIT-2027", ["company"] = "ACME", ["appraisal_cycle"] = "FY2026", ["effective_date"] = "2027-01-01",
            ["eligibility_hire_date"] = "2026-10-01", ["merit_budget_percent"] = 4m, ["bonus_budget_percent"] = 10m,
            ["merit_rules"] = MeritMatrix(),
            ["bonus_rules"] = new[] { "Outstanding", "Exceeds expectations", "Meets expectations", "Needs improvement", "Unsatisfactory", "Not rated" }
                .Select(r => new Dictionary<string, object?> { ["rating"] = r, ["target_percent"] = 8m, ["max_percent"] = 12m }).ToList(),
        });
        Assert.Equal("Draft", cycle.Data.Str("status"), "a new cycle is Draft");
        await Assert.Throws<PermissionDeniedException>(() => docs.UpdateAsync(comben, "CompensationCycle", cycle.Name, cycle.Version,
            new Dictionary<string, object?> { ["status"] = "Applied" }), "status changes only through the cycle actions");

        async Task<FormActionResult> Run(IFormAction action, Document doc) => await action.ExecuteAsync(new FormActionContext
        {
            User = comben, Meta = registry.Current.Get("CompensationCycle"), Doc = doc, Docs = docs, Services = sp,
        }, default);
        var store = sp.GetRequiredService<IDocumentStore>();

        var generated = await Run(new CompensationGenerateAction(store), cycle);
        Assert.True(generated.Success && generated.Message!.Contains("2 worksheets created"), $"two managers with salaried reports: {generated.Message}");
        cycle = await docs.GetAsync(comben, "CompensationCycle", cycle.Name);
        Assert.Equal("Planning", cycle.Data.Str("status"), "the cycle is Planning");

        var sheets = await m.QueryAsync("CompensationWorksheet", [new FilterClause("compensation_cycle", "=", cycle.Name)], default);
        var wsA = await docs.GetAsync(mgrA, "CompensationWorksheet", sheets.Single(s => s.Data.Str("manager") == "MGR-A").Name);
        var lineA = wsA.Data.Rows("lines").Single();
        Assert.Equal(rating, lineA.Str("rating"), "rating from the completed appraisal");
        Assert.Equal(1.00m, lineA.Dec("compa_ratio"), "10 million on a 10 million midpoint");
        Assert.Equal(400_000m, wsA.Data.Dec("merit_budget"), "4% of the team's base");

        // The manager can change only the inputs; a changed base is ignored.
        var rows = wsA.Data.Rows("lines").Select(r => new Dictionary<string, object?>(r)).ToList();
        rows[0]["current_base"] = 50_000_000m;
        rows[0]["merit_percent"] = 2m;
        wsA = await docs.UpdateAsync(mgrA, "CompensationWorksheet", wsA.Name, wsA.Version, new Dictionary<string, object?> { ["lines"] = rows });
        lineA = wsA.Data.Rows("lines").Single();
        Assert.Equal(10_000_000m, lineA.Dec("current_base"), "the base stays as generated");
        Assert.Equal(200_000m, lineA.Dec("merit_amount"), "2% of 10 million");
        Assert.True(lineA.Bool("outside_guideline"), "2% is below every guideline minimum for a rated employee");

        await Assert.Throws<ValidationFailedException>(() => docs.RunActionAsync(mgrA, "CompensationWorksheet", wsA.Name, "Submit to HR", wsA.Version, null),
            "outside the guideline needs a justification");
        rows = wsA.Data.Rows("lines").Select(r => new Dictionary<string, object?>(r)).ToList();
        rows[0]["justification"] = "Promoted in October; increase given then";
        wsA = await docs.UpdateAsync(mgrA, "CompensationWorksheet", wsA.Name, wsA.Version, new Dictionary<string, object?> { ["lines"] = rows });
        await docs.RunActionAsync(mgrA, "CompensationWorksheet", wsA.Name, "Submit to HR", wsA.Version, null);
        wsA = await docs.GetAsync(mgrA, "CompensationWorksheet", wsA.Name);
        Assert.Equal("Submitted", wsA.WorkflowState, "with HR");
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(mgrA, "CompensationWorksheet", wsA.Name, wsA.Version,
            new Dictionary<string, object?> { ["manager_comments"] = "One more thing" }), "the manager cannot change a submitted worksheet");

        var early = await Run(new CompensationApplyAction(store), cycle);
        Assert.True(!early.Success, "cannot apply before every worksheet is approved");

        await docs.RunActionAsync(comben, "CompensationWorksheet", wsA.Name, "Approve", wsA.Version, null);
        var wsB = await docs.GetAsync(pmB, "CompensationWorksheet", sheets.Single(s => s.Data.Str("manager") == "PM-B").Name);
        Assert.Equal("Not rated", wsB.Data.Rows("lines").Single().Str("rating"), "no appraisal → Not rated");
        await docs.RunActionAsync(pmB, "CompensationWorksheet", wsB.Name, "Submit to HR", wsB.Version, null);
        wsB = await docs.GetAsync(comben, "CompensationWorksheet", wsB.Name);
        await docs.RunActionAsync(comben, "CompensationWorksheet", wsB.Name, "Approve", wsB.Version, null);

        cycle = await docs.GetAsync(comben, "CompensationCycle", cycle.Name);
        var applied = await Run(new CompensationApplyAction(store), cycle);
        Assert.True(applied.Success, $"applied: {applied.Message}");
        var newA = (await m.QueryAsync("SalaryAssignment", [new FilterClause("employee", "=", "STAFF-A"), new FilterClause("from_date", "=", "2027-01-01")], default)).Single();
        Assert.Equal(10_200_000m, newA.Data.Rows("earnings").Single().Dec("amount"), "new base from the effective date");
        var newB = (await m.QueryAsync("SalaryAssignment", [new FilterClause("employee", "=", "STAFF-B"), new FilterClause("from_date", "=", "2027-01-01")], default)).Single();
        Assert.Equal(8_160_000m, newB.Data.Rows("earnings").Single().Dec("amount"), "Not rated target 2%");
        wsA = await docs.GetAsync(comben, "CompensationWorksheet", wsA.Name);
        Assert.Equal(newA.Name, wsA.Data.Rows("lines").Single().Str("salary_assignment"), "the line points to the new assignment");

        cycle = await docs.GetAsync(comben, "CompensationCycle", cycle.Name);
        Assert.Equal("Applied", cycle.Data.Str("status"), "the cycle is Applied");
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(comben, "CompensationCycle", cycle.Name, cycle.Version,
            new Dictionary<string, object?> { ["notes"] = "late edit" }), "an applied cycle is locked");
    }
}
