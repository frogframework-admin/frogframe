using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Position template per organization unit type (positions created for new units, sync after template changes) and
/// supervisor reassignment when a manager leaves.
/// </summary>
sealed partial class Tests
{
    private static async Task PositionTemplateSetupAsync(DocumentService docs, UserContext sys)
    {
        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "Indonesian Rupiah" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?>
        {
            ["company_code"] = "ACME", ["company_name"] = "PT Acme Nusantara", ["base_currency"] = "IDR", ["country"] = "ID",
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-BM", ["job_title"] = "Kepala Cabang", ["job_level"] = "Manager", ["is_manager_job"] = true,
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-SLS", ["job_title"] = "Sales", ["job_level"] = "Staff" });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-ADM", ["job_title"] = "Admin", ["job_level"] = "Staff" });
    }

    private static async Task<List<Document>> OpenPositionsAsync(IDocumentStore store, string unit) =>
        (await new Masters(store, "acme").QueryAsync("Position", [new FilterClause("organization_unit", "=", unit)], CancellationToken.None, take: 100))
            .Where(p => p.Data.Str("status") != "Closed")
            .ToList();

    [Test]
    public async Task PositionTemplate_creates_positions_for_a_new_unit_and_sync_adds_and_closes()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        await PositionTemplateSetupAsync(docs, sys);

        // Template lines are validated: one head line with quantity 1, no duplicate job profiles.
        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(sys, "OrganizationUnitType", new Dictionary<string, object?>
        {
            ["type_code"] = "BAD", ["type_name"] = "Bad type", ["hierarchy_order"] = 4,
            ["position_template"] = new List<Dictionary<string, object?>>
            {
                new() { ["job_profile"] = "JOB-SLS", ["quantity"] = 2 },
                new() { ["job_profile"] = "JOB-SLS", ["quantity"] = 1 },
            },
        }), "duplicate job profile in the template is rejected");

        var branchType = await docs.InsertAsync(sys, "OrganizationUnitType", new Dictionary<string, object?>
        {
            ["type_code"] = "CBG", ["type_name"] = "Cabang", ["hierarchy_order"] = 4,
            ["position_template"] = new List<Dictionary<string, object?>>
            {
                new() { ["job_profile"] = "JOB-BM", ["quantity"] = 1, ["is_head"] = true },
                new() { ["job_profile"] = "JOB-SLS", ["quantity"] = 3 },
            },
        });

        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "REG-JT", ["org_unit_name"] = "Regional Jawa Tengah", ["unit_type"] = "Division", ["company"] = "ACME", ["effective_from"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "CBG-SMG", ["org_unit_name"] = "Cabang Semarang", ["unit_type"] = "Cabang", ["parent_org_unit"] = "REG-JT",
            ["company"] = "ACME", ["effective_from"] = "2026-01-01",
        });
        await DrainErpOutboxAsync(provider);

        var positions = await OpenPositionsAsync(store, "CBG-SMG");
        Assert.Equal(4, positions.Count, "head + 3 sales positions created from the template");
        Assert.True(positions.All(p => p.Data.Bool("auto_created")), "positions are marked as created from the template");
        var head = positions.Single(p => p.Data.Str("job_profile") == "JOB-BM");
        Assert.True(head.Data.Bool("is_manager_position"), "the head line creates a head position");
        Assert.Equal("Kepala Cabang Cabang Semarang", head.Data.Str("position_title"), "default title pattern");
        Assert.True(positions.Where(p => p.Data.Str("job_profile") == "JOB-SLS").All(p => p.Data.Str("reports_to_position") == head.Name),
            "staff positions report to the unit's head position");
        var unit = await docs.GetAsync(sys, "OrganizationUnit", "CBG-SMG");
        Assert.Equal(head.Name, unit.Data.Str("manager_position"), "the unit's head position is set");

        // Applying again creates nothing (idempotent).
        var sysDocs = new SystemDocs(docs, "acme");
        var today = new DateOnly(2026, 11, 23);
        var again = await Hr.ApplyPositionTemplateAsync(sysDocs, new Masters(store, "acme"), unit, closeSurplus: false, today, CancellationToken.None);
        Assert.Equal(0, again.Created, "a second run creates no duplicates");

        // New template line (Admin) and fewer sales: sync adds 1 and closes 1 vacant sales position.
        await docs.UpdateAsync(sys, "OrganizationUnitType", branchType.Name, (await docs.GetAsync(sys, "OrganizationUnitType", branchType.Name)).Version,
            new Dictionary<string, object?>
            {
                ["position_template"] = new List<Dictionary<string, object?>>
                {
                    new() { ["job_profile"] = "JOB-BM", ["quantity"] = 1, ["is_head"] = true },
                    new() { ["job_profile"] = "JOB-SLS", ["quantity"] = 2 },
                    new() { ["job_profile"] = "JOB-ADM", ["quantity"] = 1 },
                },
            });
        var sync = await Hr.ApplyPositionTemplateAsync(sysDocs, new Masters(store, "acme"), await docs.GetAsync(sys, "OrganizationUnit", "CBG-SMG"),
            closeSurplus: true, today, CancellationToken.None);
        Assert.Equal(1, sync.Created, "the new Admin line adds one position");
        Assert.Equal(1, sync.Closed, "one vacant sales position is closed");
        positions = await OpenPositionsAsync(store, "CBG-SMG");
        Assert.Equal(4, positions.Count, "head + 2 sales + 1 admin open positions");
        Assert.Equal(1, positions.Count(p => p.Data.Str("job_profile") == "JOB-ADM"), "admin position exists");
    }

    [Test]
    public async Task Supervisor_reassigned_to_the_next_head_when_the_manager_leaves()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        await PositionTemplateSetupAsync(docs, sys);

        async Task<Document> AddUnit(string code, string? parent) => await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = code, ["org_unit_name"] = code, ["unit_type"] = "Department", ["parent_org_unit"] = parent,
            ["company"] = "ACME", ["effective_from"] = "2026-01-01",
        });
        async Task<Document> AddPosition(string title, string job, string unit, string? reportsTo = null) => await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = title, ["job_profile"] = job, ["organization_unit"] = unit, ["reports_to_position"] = reportsTo, ["effective_from"] = "2026-01-01",
        });
        async Task<Document> AddEmployee(string number, string position) => await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = number, ["full_name"] = number, ["gender"] = "Male", ["date_of_birth"] = "1990-01-01",
            ["company"] = "ACME", ["position"] = position, ["hire_date"] = "2026-01-01", ["status"] = "Active",
        });
        async Task SetHead(string unit, string position)
        {
            var u = await docs.GetAsync(sys, "OrganizationUnit", unit);
            await docs.UpdateAsync(sys, "OrganizationUnit", unit, u.Version, new Dictionary<string, object?> { ["manager_position"] = position });
        }

        await AddUnit("REG", null);
        var regHead = await AddPosition("Regional head", "JOB-BM", "REG");
        await SetHead("REG", regHead.Name);
        var bigBoss = await AddEmployee("EMP-RH", regHead.Name);

        await AddUnit("BR-1", "REG");
        var brHead = await AddPosition("Branch head", "JOB-BM", "BR-1", regHead.Name);
        await SetHead("BR-1", brHead.Name);
        var staffPos = await AddPosition("Sales 1", "JOB-SLS", "BR-1", brHead.Name);
        var boss = await AddEmployee("EMP-BH", brHead.Name);
        var staff = await AddEmployee("EMP-S1", staffPos.Name);
        Assert.Equal(boss.Name, staff.Data.Str("reports_to"), "staff reports to the branch head");
        Assert.Equal(boss.Name, staff.Data.Str("leave_approver"), "leave approver defaults to the supervisor");

        // The branch head leaves (status applied directly, as an administrator would).
        var current = await docs.GetAsync(sys, "Employee", boss.Name);
        await docs.UpdateAsync(sys, "Employee", boss.Name, current.Version, new Dictionary<string, object?> { ["status"] = "Left" });
        var problems = await HrApply.ReassignReportsAsync(new SystemDocs(docs, "acme"), store, "acme", boss.Name, CancellationToken.None);

        Assert.Equal(0, problems.Count, "everyone has a supervisor");
        staff = await docs.GetAsync(sys, "Employee", staff.Name);
        Assert.Equal(bigBoss.Name, staff.Data.Str("reports_to"), "staff now reports to the regional head");
        Assert.Equal(bigBoss.Name, staff.Data.Str("leave_approver"), "leave approver follows the new supervisor");
        Assert.Equal(bigBoss.Name, staff.Data.Str("expense_approver"), "expense approver follows the new supervisor");

        // Running it again changes nothing.
        Assert.Equal(0, (await HrApply.ReassignReportsAsync(new SystemDocs(docs, "acme"), store, "acme", boss.Name, CancellationToken.None)).Count, "idempotent");
    }
}
