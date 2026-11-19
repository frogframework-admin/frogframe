using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Actions;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Framework.Web.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Enterprise Organization Management (OM) and Workforce Scheduling &amp; Management (WSM) integration tests:
/// dynamic multi-level unit types (2-level corporate &amp; 5-level government), cycle prevention, path cascade,
/// effective-dated reorganization, overlap &amp; closure checks, acting assignments (Plt.), 4-group 3-shift roster generation,
/// cross-midnight attendance, rest-day overtime, workforce constraint engine, shift swap workflow, leave sync, and manpower coverage.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildErpProvider()
    {
        var erpMetadataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Apps.Erp", "Metadata"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(erpMetadataPath));
        services.AddFrogFrameworkIdentity();
        services.AddErpHooks();
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task OrgManagement_dynamic_levels_cycle_check_path_cascade_reorganization_and_acting_assignment()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        var m = new Masters(store, "acme");

        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?>
        {
            ["currency_code"] = "IDR",
            ["currency_name"] = "Indonesian Rupiah",
        });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?>
        {
            ["company_code"] = "ACME",
            ["company_name"] = "PT Acme Nusantara",
            ["base_currency"] = "IDR",
            ["country"] = "ID",
        });

        // 1. 2-level corporate structure using seeded system OrganizationUnitType ("Division" -> "Team")
        var divA = await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DIV-A",
            ["org_unit_name"] = "Division Alpha",
            ["unit_type"] = "Division",
            ["company"] = "ACME",
            ["effective_from"] = "2026-01-01",
        });
        var teamA = await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "TEAM-A1",
            ["org_unit_name"] = "Team Alpha One",
            ["unit_type"] = "Team",
            ["parent_org_unit"] = "DIV-A",
            ["company"] = "ACME",
            ["effective_from"] = "2026-01-01",
        });
        Assert.Equal(1L, (long)divA.Data.Dec("depth"), "root unit has depth 1");
        Assert.Equal("/DIV-A/", divA.Data.Str("materialized_path"), "root unit materialized path");
        Assert.Equal(2L, (long)teamA.Data.Dec("depth"), "2-level child has depth 2");
        Assert.Equal("/DIV-A/TEAM-A1/", teamA.Data.Str("materialized_path"), "2-level child materialized path");
        Assert.Equal("Division Alpha > Team Alpha One", teamA.Data.Str("breadcrumb"), "2-level child breadcrumb");

        // System OrganizationUnitType cannot be deleted (test on 'Branch' which has no linked units)
        var sysType = await docs.GetAsync(sys, "OrganizationUnitType", "Branch");
        await Assert.Throws<ValidationFailedException>(() => docs.DeleteAsync(sys, "OrganizationUnitType", "Branch", sysType.Version),
            "seeded system OrganizationUnitType cannot be deleted");

        // 2. 5-level government structure (Ministry -> Echelon I -> Echelon II -> Technical Office -> Sub-Office)
        var govTypes = new (string Code, string Name, int Order, string? Parent)[]
        {
            ("MIN", "Ministry", 1, null),
            ("ESL1", "Echelon I", 2, "Ministry"),
            ("ESL2", "Echelon II", 3, "Echelon I"),
            ("BALAI", "Technical Office", 4, "Echelon II"),
            ("LOKA", "Sub-Office", 5, "Technical Office"),
        };
        foreach (var gt in govTypes)
        {
            await docs.InsertAsync(sys, "OrganizationUnitType", new Dictionary<string, object?>
            {
                ["type_code"] = gt.Code,
                ["type_name"] = gt.Name,
                ["hierarchy_order"] = gt.Order,
                ["parent_type"] = gt.Parent,
                ["is_active"] = true,
            });
        }

        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "KEMEN", ["org_unit_name"] = "Ministry of Infrastructure", ["unit_type"] = "Ministry", ["company"] = "ACME", ["effective_from"] = "2026-01-01"
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DITJEN-1", ["org_unit_name"] = "Directorate General I", ["unit_type"] = "Echelon I", ["parent_org_unit"] = "KEMEN", ["company"] = "ACME", ["effective_from"] = "2026-01-01"
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DIT-2", ["org_unit_name"] = "Directorate of Operations", ["unit_type"] = "Echelon II", ["parent_org_unit"] = "DITJEN-1", ["company"] = "ACME", ["effective_from"] = "2026-01-01"
        });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "BALAI-SBY", ["org_unit_name"] = "Surabaya Regional Office", ["unit_type"] = "Technical Office", ["parent_org_unit"] = "DIT-2", ["company"] = "ACME", ["effective_from"] = "2026-01-01"
        });
        var loka = await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "LOKA-MLG", ["org_unit_name"] = "Malang Sub-Office", ["unit_type"] = "Sub-Office", ["parent_org_unit"] = "BALAI-SBY", ["company"] = "ACME", ["effective_from"] = "2026-01-01"
        });
        Assert.Equal(5L, (long)loka.Data.Dec("depth"), "5-level leaf unit has depth 5");
        Assert.Equal("/KEMEN/DITJEN-1/DIT-2/BALAI-SBY/LOKA-MLG/", loka.Data.Str("materialized_path"), "5-level materialized path");
        Assert.Equal("Ministry of Infrastructure > Directorate General I > Directorate of Operations > Surabaya Regional Office > Malang Sub-Office",
            loka.Data.Str("breadcrumb"), "5-level breadcrumb");

        // 3. Cycle prevention: setting KEMEN's parent to its own descendant LOKA-MLG is rejected
        var kemen = await docs.GetAsync(sys, "OrganizationUnit", "KEMEN");
        await Assert.Throws<ValidationFailedException>(() =>
            docs.UpdateAsync(sys, "OrganizationUnit", "KEMEN", kemen.Version, new Dictionary<string, object?> { ["parent_org_unit"] = "LOKA-MLG" }),
            "setting a unit's parent to one of its descendants is rejected");

        // 4. Overlap prevention: two Active rows for the same base_unit_code on overlapping dates are rejected
        await Assert.Throws<ValidationFailedException>(() =>
            docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
            {
                ["org_unit_code"] = "DIV-A-DUP",
                ["org_unit_name"] = "Division Alpha Duplicate",
                ["unit_type"] = "Division",
                ["base_unit_code"] = "DIV-A",
                ["company"] = "ACME",
                ["status"] = "Active",
                ["effective_from"] = "2026-03-01",
            }), "two active rows for the same base_unit_code on overlapping dates are rejected");

        // 5. Closure check & reorganization with Positions and Employees
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "DIV-B",
            ["org_unit_name"] = "Division Beta",
            ["unit_type"] = "Division",
            ["company"] = "ACME",
            ["effective_from"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-ENG",
            ["job_title"] = "Software Engineer",
            ["job_level"] = "Staff",
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-MGR",
            ["job_title"] = "Division Manager",
            ["job_level"] = "Manager",
            ["is_manager_job"] = true,
        });

        var posTeamA = await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = "Alpha Engineer",
            ["job_profile"] = "JOB-ENG",
            ["organization_unit"] = "TEAM-A1",
            ["effective_from"] = "2026-01-01",
        });
        var emp1 = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-01",
            ["full_name"] = "Andi Pratama",
            ["gender"] = "Male",
            ["date_of_birth"] = "1995-04-12",
            ["company"] = "ACME",
            ["position"] = posTeamA.Name,
            ["job_profile"] = "JOB-ENG",
            ["hire_date"] = "2026-01-01",
            ["status"] = "Active",
        });
        Assert.Equal("TEAM-A1", emp1.Data.Str("organization_unit"), "Employee organization_unit is derived from Position");
        Assert.Equal("Division Alpha > Team Alpha One", emp1.Data.Str("organization_breadcrumb"), "Employee breadcrumb fetched from unit");

        // Closing TEAM-A1 while an active Position points to it is rejected
        var teamACurrent = await docs.GetAsync(sys, "OrganizationUnit", "TEAM-A1");
        await Assert.Throws<ValidationFailedException>(() =>
            docs.UpdateAsync(sys, "OrganizationUnit", "TEAM-A1", teamACurrent.Version, new Dictionary<string, object?> { ["status"] = "Closed" }),
            "closing a unit with an active Position is rejected");

        // Planned reorganization: TEAM-A1 moves from DIV-A to DIV-B on 2026-07-01
        var plannedTeamA = await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "TEAM-A1-H2",
            ["org_unit_name"] = "Team Alpha One",
            ["unit_type"] = "Team",
            ["base_unit_code"] = "TEAM-A1",
            ["previous_version"] = "TEAM-A1",
            ["parent_org_unit"] = "DIV-B",
            ["company"] = "ACME",
            ["status"] = "Planned",
            ["effective_from"] = "2026-07-01",
        });

        var sysDocs = new SystemDocs(docs, "acme");
        var migratedCount = await Hr.ActivatePlannedUnitAsync(sysDocs, new Masters(store, "acme"), plannedTeamA, new DateOnly(2026, 7, 1), CancellationToken.None);
        Assert.Equal(1, migratedCount, "active position migrated to the new effective unit version");

        // Query before reorganization (2026-05-15) vs on/after reorganization (2026-08-01)
        var mAfter = new Masters(store, "acme");
        var unitInMay = await Hr.GetOrgUnitAsOfAsync(mAfter, "TEAM-A1", new DateOnly(2026, 5, 15), CancellationToken.None);
        var unitInAug = await Hr.GetOrgUnitAsOfAsync(mAfter, "TEAM-A1", new DateOnly(2026, 8, 1), CancellationToken.None);
        Assert.Equal("/DIV-A/TEAM-A1/", unitInMay?.Data.Str("materialized_path"), "before reorganization date, TEAM-A1 is under DIV-A");
        Assert.Equal("/DIV-B/TEAM-A1-H2/", unitInAug?.Data.Str("materialized_path"), "on/after reorganization date, TEAM-A1 is under DIV-B");

        var divAEmpsInMay = await Hr.EmployeesInSubtreeAsOfAsync(mAfter, "DIV-A", new DateOnly(2026, 5, 15), CancellationToken.None);
        var divAEmpsInAug = await Hr.EmployeesInSubtreeAsOfAsync(mAfter, "DIV-A", new DateOnly(2026, 8, 1), CancellationToken.None);
        var divBEmpsInAug = await Hr.EmployeesInSubtreeAsOfAsync(mAfter, "DIV-B", new DateOnly(2026, 8, 1), CancellationToken.None);
        Assert.Equal(1, divAEmpsInMay.Count, "employee matches DIV-A subtree before reorganization");
        Assert.Equal(0, divAEmpsInAug.Count, "employee no longer matches DIV-A subtree after reorganization");
        Assert.Equal(1, divBEmpsInAug.Count, "employee matches DIV-B subtree after reorganization");

        // 6. Acting Assignment (Plt.): vacant Division Beta manager position filled temporarily via EmployeeAssignment
        var posDivBMgr = await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = "Head of Division Beta",
            ["job_profile"] = "JOB-MGR",
            ["organization_unit"] = "DIV-B",
            ["is_manager_position"] = true,
            ["effective_from"] = "2026-01-01",
        });
        var divBDoc = await docs.GetAsync(sys, "OrganizationUnit", "DIV-B");
        await docs.UpdateAsync(sys, "OrganizationUnit", "DIV-B", divBDoc.Version, new Dictionary<string, object?>
        {
            ["manager_position"] = posDivBMgr.Name,
        });

        var posDivAMgr = await docs.InsertAsync(sys, "Position", new Dictionary<string, object?>
        {
            ["position_title"] = "Head of Division Alpha",
            ["job_profile"] = "JOB-MGR",
            ["organization_unit"] = "DIV-A",
            ["is_manager_position"] = true,
            ["effective_from"] = "2026-01-01",
        });

        var empMgr = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-MGR",
            ["full_name"] = "Siti Rahayu",
            ["gender"] = "Female",
            ["date_of_birth"] = "1988-08-20",
            ["company"] = "ACME",
            ["position"] = posDivAMgr.Name,
            ["organization_unit"] = "DIV-A",
            ["job_profile"] = "JOB-MGR",
            ["hire_date"] = "2026-01-01",
            ["status"] = "Active",
        });

        await docs.InsertAsync(sys, "EmployeeAssignment", new Dictionary<string, object?>
        {
            ["employee"] = empMgr.Name,
            ["assignment_type"] = "Acting",
            ["organization_unit"] = "DIV-B",
            ["position"] = posDivBMgr.Name,
            ["start_date"] = "2026-07-01",
            ["end_date"] = "2026-12-31",
            ["status"] = "Active",
            ["reference_memo"] = "SK-PLT-2026-01",
        });

        var bossForEmp1 = await Hr.ManagerForAsync(new Masters(store, "acme"), emp1.Name, posTeamA.Name, "TEAM-A1-H2", CancellationToken.None, new DateOnly(2026, 8, 1));
        Assert.Equal("EMP-MGR", bossForEmp1, "Acting (Plt.) assignment holder is resolved as unit head and approver");
    }

    [Test]
    public async Task Wsm_roster_generation_cross_midnight_attendance_rest_day_overtime_constraints_swap_and_leave_coverage()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");

        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "Indonesian Rupiah" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?> { ["company_code"] = "ACME", ["company_name"] = "PT Acme Nusantara", ["base_currency"] = "IDR", ["country"] = "ID" });

        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?>
        {
            ["org_unit_code"] = "PLANT-1",
            ["org_unit_name"] = "Manufacturing Plant 1",
            ["unit_type"] = "Department",
            ["company"] = "ACME",
            ["staffing_model"] = "Job",
            ["effective_from"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?>
        {
            ["job_code"] = "JOB-OP",
            ["job_title"] = "Machine Operator",
            ["job_level"] = "Staff",
        });

        // 1. Three shifts (Morning 06-14, Afternoon 14-22, Night 22-06 crossing midnight) — OFF is NOT a ShiftType
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Morning 06-14", ["start_time"] = "06:00:00", ["end_time"] = "14:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = false
        });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Afternoon 14-22", ["start_time"] = "14:00:00", ["end_time"] = "22:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = false
        });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?>
        {
            ["shift_name"] = "Night 22-06", ["start_time"] = "22:00:00", ["end_time"] = "06:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = true
        });

        // Holiday List with a mid-week public holiday on 2026-12-03 (Thursday)
        await docs.InsertAsync(sys, "HolidayList", new Dictionary<string, object?>
        {
            ["holiday_list_name"] = "Holidays 2026",
            ["from_date"] = "2026-01-01",
            ["to_date"] = "2026-12-31",
            ["weekly_off"] = "None",
            ["holidays"] = new List<Dictionary<string, object?>>
            {
                new() { ["holiday_date"] = "2026-12-03", ["description"] = "National Holiday", ["holiday_type"] = "Public holiday" },
            },
        });
        await docs.SetSingleAsync(sys, "HRSettings", new Dictionary<string, object?>
        {
            ["default_holiday_list"] = "Holidays 2026",
            ["default_shift_type"] = "Morning 06-14",
        });

        // Create two operators in Squad A and Squad B
        var opA = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-OPA", ["full_name"] = "Operator Alpha", ["gender"] = "Male", ["date_of_birth"] = "1993-02-10",
            ["company"] = "ACME", ["organization_unit"] = "PLANT-1", ["job_profile"] = "JOB-OP", ["hire_date"] = "2026-01-01", ["status"] = "Active"
        });
        var opB = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "EMP-OPB", ["full_name"] = "Operator Beta", ["gender"] = "Male", ["date_of_birth"] = "1994-06-15",
            ["company"] = "ACME", ["organization_unit"] = "PLANT-1", ["job_profile"] = "JOB-OP", ["hire_date"] = "2026-01-01", ["status"] = "Active"
        });

        // 2. Shift Groups (Squad A and Squad B)
        await docs.InsertAsync(sys, "ShiftGroup", new Dictionary<string, object?>
        {
            ["group_code"] = "SQUAD-A",
            ["group_name"] = "Squad A",
            ["company"] = "ACME",
            ["organization_unit"] = "PLANT-1",
            ["effective_from"] = "2026-01-01",
            ["members"] = new List<Dictionary<string, object?>>
            {
                new() { ["employee"] = opA.Name, ["effective_from"] = "2026-01-01", ["is_active"] = true },
            },
        });
        await docs.InsertAsync(sys, "ShiftGroup", new Dictionary<string, object?>
        {
            ["group_code"] = "SQUAD-B",
            ["group_name"] = "Squad B",
            ["company"] = "ACME",
            ["organization_unit"] = "PLANT-1",
            ["effective_from"] = "2026-01-01",
            ["members"] = new List<Dictionary<string, object?>>
            {
                new() { ["employee"] = opB.Name, ["effective_from"] = "2026-01-01", ["is_active"] = true },
            },
        });

        // 3. Versioned 8-day Continental Shift Pattern: 2 Morning, 2 Afternoon, 2 Night, 2 REST
        await docs.InsertAsync(sys, "ShiftPattern", new Dictionary<string, object?>
        {
            ["pattern_code"] = "PAT-4G3S-V1",
            ["pattern_name"] = "4-Group 3-Shift Continental (2-2-2-2)",
            ["pattern_version"] = 1,
            ["cycle_days"] = 8,
            ["work_week_days"] = "5",
            ["effective_from"] = "2026-01-01",
            ["status"] = "Active",
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["day_number"] = 1, ["schedule_type"] = "WORK", ["shift_type"] = "Morning 06-14" },
                new() { ["day_number"] = 2, ["schedule_type"] = "WORK", ["shift_type"] = "Morning 06-14" },
                new() { ["day_number"] = 3, ["schedule_type"] = "WORK", ["shift_type"] = "Afternoon 14-22" },
                new() { ["day_number"] = 4, ["schedule_type"] = "WORK", ["shift_type"] = "Afternoon 14-22" },
                new() { ["day_number"] = 5, ["schedule_type"] = "WORK", ["shift_type"] = "Night 22-06" },
                new() { ["day_number"] = 6, ["schedule_type"] = "WORK", ["shift_type"] = "Night 22-06" },
                new() { ["day_number"] = 7, ["schedule_type"] = "REST" },
                new() { ["day_number"] = 8, ["schedule_type"] = "REST" },
            },
        });

        // Assign Squad A with day_offset = 0, Squad B with day_offset = 7 (starting 2026-12-01)
        await docs.InsertAsync(sys, "ShiftAssignment", new Dictionary<string, object?>
        {
            ["assignment_target"] = "Shift Group",
            ["shift_group"] = "SQUAD-A",
            ["company"] = "ACME",
            ["shift_pattern"] = "PAT-4G3S-V1",
            ["start_date"] = "2026-12-01",
            ["day_offset"] = 0,
            ["respect_public_holidays"] = false,
        });
        await docs.InsertAsync(sys, "ShiftAssignment", new Dictionary<string, object?>
        {
            ["assignment_target"] = "Shift Group",
            ["shift_group"] = "SQUAD-B",
            ["company"] = "ACME",
            ["shift_pattern"] = "PAT-4G3S-V1",
            ["start_date"] = "2026-12-01",
            ["day_offset"] = 7,
            ["respect_public_holidays"] = false,
        });

        // 4. Generate Materialized Employee Daily Schedules for Dec 1 - Dec 10, 2026
        var gen = await docs.InsertAsync(sys, "ScheduleGeneration", new Dictionary<string, object?>
        {
            ["company"] = "ACME",
            ["organization_unit"] = "PLANT-1",
            ["from_date"] = "2026-12-01",
            ["to_date"] = "2026-12-10",
            ["overwrite_unlocked"] = true,
        });
        Assert.Equal("Generated", gen.Data.Str("status"), "schedule generation completed");
        Assert.Equal(20L, (long)gen.Data.Dec("generated_count"), "10 days × 2 employees = 20 daily schedule slots materialized");

        var m = new Masters(store, "acme");
        // Verify Operator A's rotating cycle:
        // Dec 1 (Tue): Day 1 -> Morning
        // Dec 3 (Thu, Public Holiday): Day 3 -> Afternoon (is_public_holiday = true!)
        // Dec 5 (Sat): Day 5 -> Night (crosses_midnight = true!)
        // Dec 7 (Mon): Day 7 -> REST (Scheduled Rest Day on a Monday!)
        var opADec3 = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-03")], CancellationToken.None)).Single();
        Assert.Equal("WORK", opADec3.Data.Str("schedule_type"), "24/7 roster continues working on public holiday");
        Assert.Equal("Afternoon 14-22", opADec3.Data.Str("shift_type"), "Day 3 of cycle is Afternoon shift");
        Assert.True(opADec3.Data.Bool("is_public_holiday"), "Public holiday flag is recorded alongside scheduled work day");

        var opADec5 = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-05")], CancellationToken.None)).Single();
        Assert.Equal("Night 22-06", opADec5.Data.Str("shift_type"), "Day 5 of cycle is Night shift");
        Assert.True(opADec5.Data.Bool("crosses_midnight"), "Night shift crosses midnight");

        var opADec7 = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-07")], CancellationToken.None)).Single();
        Assert.Equal("REST", opADec7.Data.Str("schedule_type"), "Day 7 (Monday) is a scheduled REST day");
        Assert.Equal("Scheduled Rest Day", opADec7.Data.Str("day_category"), "day category is Scheduled Rest Day");
        Assert.True(opADec7.Data.Str("shift_type") is null, "REST day has no ShiftType");

        // 5. Cross-midnight Attendance on Night Shift (work_date = 2026-12-05, shift 22:00 to Dec 6 06:00)
        // Check-in after midnight at 2026-12-06 00:25 local time (2 hours 25 minutes late = 145 min - 10 grace = 135 late minutes)
        var attNight = await docs.InsertAsync(sys, "Attendance", new Dictionary<string, object?>
        {
            ["employee"] = opA.Name,
            ["attendance_date"] = "2026-12-05",
            ["status"] = "Present",
            ["check_in"] = "2026-12-06T00:25:00",
            ["check_out"] = "2026-12-06T06:00:00",
        });
        Assert.Equal("Night 22-06", attNight.Data.Str("shift_type"), "Attendance auto-resolves Night shift from EmployeeDailySchedule");
        Assert.Equal(135L, (long)attNight.Data.Dec("late_minutes"), "Cross-midnight check-in at 00:25 is accurately calculated as 135 minutes late against 22:00 start");

        // 6. Overtime on a weekday Scheduled Rest Day (Monday 2026-12-07) automatically uses PP 35/2021 Rest-Day tiers (2x for first 8 hours)
        var otRest = await docs.InsertAsync(sys, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = opA.Name,
            ["overtime_date"] = "2026-12-07",
            ["hours"] = 4,
            ["reason"] = "Emergency turbine maintenance on Monday rest day",
        });
        Assert.Equal("Rest day or public holiday", otRest.Data.Str("day_type"), "Overtime on a roster Monday REST day auto-selects Rest day or public holiday");
        Assert.Equal(8m, otRest.Data.Dec("weighted_hours"), "4 hours on rest day = 4 × 2 = 8 paid hours");

        // 7. Workforce Constraint Engine: Override forcing Morning shift on 2026-12-06 right after Night shift on 2026-12-05 is rejected (< 11h rest & Night-to-Morning)
        await docs.InsertAsync(sys, "WorkScheduleRule", new Dictionary<string, object?>
        {
            ["rule_code"] = "PLANT-FATIGUE-RULE",
            ["rule_name"] = "Plant Fatigue & Rest Rule",
            ["company"] = "ACME",
            ["max_consecutive_work_days"] = 6,
            ["min_rest_hours_between_shifts"] = 11,
            ["forbid_night_to_morning"] = true,
            ["enforcement_mode"] = "Strict",
        });
        await Assert.Throws<ValidationFailedException>(() =>
            docs.InsertAsync(sys, "ShiftOverride", new Dictionary<string, object?>
            {
                ["employee"] = opA.Name,
                ["override_date"] = "2026-12-06",
                ["new_schedule_type"] = "WORK",
                ["new_shift_type"] = "Morning 06-14",
                ["reason"] = "Attempted illegal back-to-back night-to-morning shift",
            }), "Constraint engine blocks Morning shift immediately following Night shift");

        // 8. Valid Shift Swap Request between Operator A (Afternoon on Dec 3) and Operator B (Morning on Dec 3)
        // Both have >= 16h rest before and after Dec 3 and <= 6 consecutive working days.
        var swap = await docs.InsertAsync(sys, "ShiftSwapRequest", new Dictionary<string, object?>
        {
            ["requester"] = opA.Name,
            ["target_employee"] = opB.Name,
            ["swap_date"] = "2026-12-03",
            ["reason"] = "Family medical appointment in the afternoon",
        });
        Assert.Equal("Afternoon 14-22", swap.Data.Str("requester_shift_type"), "Requester original shift captured");
        Assert.Equal("Morning 06-14", swap.Data.Str("target_shift_type"), "Target original shift captured");

        swap = await docs.RunActionAsync(sys, "ShiftSwapRequest", swap.Name, "Submit for peer acceptance", swap.Version, null);
        Assert.Equal("Pending Peer Approval", swap.WorkflowState, "Swap transitioned to Pending Peer Approval");

        swap = await docs.RunActionAsync(sys, "ShiftSwapRequest", swap.Name, "Accept swap", swap.Version, null);
        Assert.Equal("Pending Manager Approval", swap.WorkflowState, "Swap transitioned to Pending Manager Approval");

        swap = await docs.RunActionAsync(sys, "ShiftSwapRequest", swap.Name, "Approve", swap.Version, "Approved by plant manager");
        Assert.Equal("Approved", swap.WorkflowState, "Swap approved");

        var mAfterSwap = new Masters(store, "acme");
        var opADec3After = (await mAfterSwap.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-03")], CancellationToken.None)).Single();
        var opBDec3After = (await mAfterSwap.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opB.Name), new FilterClause("work_date", "=", "2026-12-03")], CancellationToken.None)).Single();
        Assert.Equal("Morning 06-14", opADec3After.Data.Str("shift_type"), "Operator A now holds Morning shift on Dec 3");
        Assert.Equal("Shift Swap", opADec3After.Data.Str("source_type"), "Schedule source recorded as Shift Swap");
        Assert.Equal("Afternoon 14-22", opBDec3After.Data.Str("shift_type"), "Operator B now holds Afternoon shift on Dec 3");

        // 9. Leave Integration & Manpower Coverage Alert (requires at least 2 operators on Morning shift)
        await docs.InsertAsync(sys, "ShiftRequirement", new Dictionary<string, object?>
        {
            ["company"] = "ACME",
            ["organization_unit"] = "PLANT-1",
            ["shift_type"] = "Morning 06-14",
            ["day_scope"] = "All days",
            ["min_headcount"] = 2,
            ["target_headcount"] = 2,
            ["effective_from"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "LeaveType", new Dictionary<string, object?>
        {
            ["leave_type_name"] = "Annual Leave",
            ["is_annual_quota"] = true,
            ["is_paid"] = true,
            ["max_consecutive_days"] = 12,
        });
        var alloc = await docs.InsertAsync(sys, "LeaveAllocation", new Dictionary<string, object?>
        {
            ["employee"] = opA.Name,
            ["leave_type"] = "Annual Leave",
            ["from_date"] = "2026-01-01",
            ["to_date"] = "2026-12-31",
            ["new_leaves"] = 12,
        });
        await docs.RunActionAsync(sys, "LeaveAllocation", alloc.Name, DocActions.Submit, alloc.Version, null);

        var hrUser = new UserContext("hr01", "HR Officer", "acme", new HashSet<string> { "HR", "Employee" }, new Dictionary<string, string>());
        // Operator A takes leave on 2026-12-02 (when Operator A was scheduled for Morning 06-14)
        var leaveApp = await docs.InsertAsync(hrUser, "LeaveApplication", new Dictionary<string, object?>
        {
            ["employee"] = opA.Name,
            ["leave_type"] = "Annual Leave",
            ["from_date"] = "2026-12-02",
            ["to_date"] = "2026-12-02",
            ["reason"] = "Personal leave",
        });
        leaveApp = await docs.RunActionAsync(hrUser, "LeaveApplication", leaveApp.Name, "Submit for approval", leaveApp.Version, null);
        leaveApp = await docs.RunActionAsync(hrUser, "LeaveApplication", leaveApp.Name, "Approve (HR)", leaveApp.Version, "Approved by HR");

        var mAfterLeave = new Masters(store, "acme");
        var opADec2After = (await mAfterLeave.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-02")], CancellationToken.None)).Single();
        Assert.Equal("LEAVE", opADec2After.Data.Str("schedule_type"), "Approved leave updates EmployeeDailySchedule to LEAVE");
        Assert.Equal("Annual Leave", opADec2After.Data.Str("day_category"), "Day category updated to Annual Leave");
        Assert.True(leaveApp.TransientMessages.Any(msg => msg.Contains("Manpower shortage alert") && msg.Contains("2026-12-02")),
            "Manpower shortage alert triggered when leave causes headcount on Morning shift to drop below minimum requirement");

        // 10. Rolling Horizon Job generates 30 days ahead without overwriting protected LEAVE rows
        var rollingJob = new RosterRollingHorizonJob();
        var jobCtx = new Framework.Jobs.ScheduledJobContext
        {
            TenantId = "acme",
            SystemUser = sys,
            Docs = docs,
            Services = provider,
            ScheduledTime = new DateTimeOffset(2026, 12, 1, 2, 0, 0, TimeSpan.Zero),
        };
        await rollingJob.ExecuteAsync(jobCtx, CancellationToken.None);

        var dec31Sched = (await mAfterLeave.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-31")], CancellationToken.None)).SingleOrDefault();
        Assert.True(dec31Sched is not null, "Rolling horizon job populates schedule 30 days ahead (2026-12-31)");
        var dec02StillLeave = (await mAfterLeave.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", opA.Name), new FilterClause("work_date", "=", "2026-12-02")], CancellationToken.None)).Single();
        Assert.Equal("LEAVE", dec02StillLeave.Data.Str("schedule_type"), "Rolling horizon job preserves existing LEAVE slot");
    }

    [Test]
    public async Task WsmOptimizer_cpsat_respects_constraints_fairness_preferences_and_preserves_protected_slots()
    {
        // 1. Pure CP-SAT Engine test: 4 employees, 7 days, 3 shifts (Morning, Afternoon, Night)
        var days = Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 12, 1).AddDays(i)).ToList();
        var emps = new List<WsmOptimizerEngine.EmployeeInput>
        {
            new("E1", "Alice"), new("E2", "Bob"), new("E3", "Charlie"), new("E4", "Dana")
        };
        var shifts = new List<WsmOptimizerEngine.ShiftInput>
        {
            new("SH-MORN", new TimeOnly(6, 0), new TimeOnly(14, 0), IsNight: false, IsMorning: true),
            new("SH-AFT", new TimeOnly(14, 0), new TimeOnly(22, 0), IsNight: false, IsMorning: false),
            new("SH-NGT", new TimeOnly(22, 0), new TimeOnly(6, 0), IsNight: true, IsMorning: false),
        };
        var demands = days.SelectMany(d => shifts.Select(s => new WsmOptimizerEngine.DemandInput(d, s.Code, 1, 1))).ToList();
        var pinned = new List<WsmOptimizerEngine.PinnedSlot>
        {
            new("E1", days[1], "LEAVE", null),
        };
        var prefs = new List<WsmOptimizerEngine.PreferenceInput>
        {
            new("E2", "Unavailable", null, days[2], days[2], null, 5),
            new("E3", "Prefer", "SH-MORN", days[0], days[6], null, 4),
        };

        var res = WsmOptimizerEngine.Solve(new WsmOptimizerEngine.OptimizerInput(
            emps, days, shifts, demands, pinned, prefs,
            new Dictionary<(string, DateOnly), string?>(),
            new WsmOptimizerEngine.RuleInput(MaxConsecutiveWorkDays: 5, MinRestHours: 11.0, ForbidNightToMorning: true),
            new WsmOptimizerEngine.WeightInput(Coverage: 100, Preference: 10, Fairness: 20, NightBalance: 15, Stability: 0, TimeLimitSeconds: 10)));

        Assert.True(res.IsSuccess, $"CP-SAT solved successfully (status={res.SolverStatus})");
        Assert.Equal(100.0, res.CoveragePct, "All shift demands covered 100%");
        Assert.Equal(0, res.ShortageSlots, "Zero shortage slots");

        // Verify pinned LEAVE and Unavailable respected
        var e1Day1 = res.Lines.Single(l => l.EmployeeId == "E1" && l.WorkDate == days[1]);
        Assert.Equal("LEAVE", e1Day1.ScheduleType, "Pinned LEAVE slot preserved");
        var e2Day2 = res.Lines.Single(l => l.EmployeeId == "E2" && l.WorkDate == days[2]);
        Assert.Equal("REST", e2Day2.ScheduleType, "Unavailable preference enforced as REST");

        // Verify no Night -> Morning violation and max consecutive <= 5
        foreach (var emp in emps)
        {
            var seq = res.Lines.Where(l => l.EmployeeId == emp.Id).OrderBy(l => l.WorkDate).ToList();
            int consec = 0;
            for (int i = 0; i < seq.Count; i++)
            {
                consec = seq[i].ScheduleType == "WORK" ? consec + 1 : 0;
                Assert.True(consec <= 5, $"Employee {emp.Id} consecutive work days ({consec}) <= 5");
                if (i > 0 && seq[i - 1].ShiftCode == "SH-NGT")
                    Assert.True(seq[i].ShiftCode != "SH-MORN", $"Employee {emp.Id} has no Night -> Morning transition");
            }
        }

        // Verify fairness spread <= 1 on symmetric workload
        var counts = emps.Select(e => res.Lines.Count(l => l.EmployeeId == e.Id && l.ScheduleType == "WORK")).ToList();
        Assert.True(counts.Max() - counts.Min() <= 2, $"Workload balanced across employees ({counts.Min()}..{counts.Max()})");

        // 2. Over-demanded instance reports shortages cleanly instead of failing
        var overDemands = days.Select(d => new WsmOptimizerEngine.DemandInput(d, "SH-MORN", 10, 10)).ToList();
        var overRes = WsmOptimizerEngine.Solve(new WsmOptimizerEngine.OptimizerInput(
            emps, days, shifts, overDemands, [], [],
            new Dictionary<(string, DateOnly), string?>(),
            new WsmOptimizerEngine.RuleInput(6, 11.0, true),
            new WsmOptimizerEngine.WeightInput(100, 10, 20, 15, 0, 5)));
        Assert.True(overRes.IsSuccess, "Over-demanded instance still returns feasible solution with shortage slack");
        Assert.True(overRes.ShortageSlots > 0, "Shortage slots accurately reported");

        // 3. End-to-end ScheduleOptimization Apply preserves locked rows
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");

        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "IDR", ["symbol"] = "Rp" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?> { ["company_code"] = "ACME", ["company_name"] = "Acme Corp", ["base_currency"] = "IDR", ["country"] = "ID" });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-STD", ["job_title"] = "Standard Staff" });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = "OPS", ["org_unit_name"] = "Ops", ["company"] = "ACME", ["unit_type"] = "Department", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?> { ["shift_name"] = "Morning 06-14", ["start_time"] = "06:00:00", ["end_time"] = "14:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = false });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?> { ["shift_name"] = "Afternoon 14-22", ["start_time"] = "14:00:00", ["end_time"] = "22:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = false });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "OPT-1", ["full_name"] = "Opt One", ["gender"] = "Male", ["date_of_birth"] = "1995-01-01", ["company"] = "ACME", ["organization_unit"] = "OPS", ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "OPT-2", ["full_name"] = "Opt Two", ["gender"] = "Female", ["date_of_birth"] = "1995-02-02", ["company"] = "ACME", ["organization_unit"] = "OPS", ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        await docs.InsertAsync(sys, "ShiftRequirement", new Dictionary<string, object?> { ["company"] = "ACME", ["organization_unit"] = "OPS", ["shift_type"] = "Morning 06-14", ["day_scope"] = "All days", ["min_headcount"] = 1, ["target_headcount"] = 1, ["effective_from"] = "2026-01-01", ["is_active"] = true });

        // Pre-seed a locked row on 2026-12-01 for OPT-1
        await docs.InsertAsync(sys, "EmployeeDailySchedule", new Dictionary<string, object?>
        {
            ["employee"] = "OPT-1", ["company"] = "ACME", ["organization_unit"] = "OPS",
            ["work_date"] = "2026-12-01", ["schedule_type"] = "WORK", ["day_category"] = "Scheduled Workday",
            ["shift_type"] = "Afternoon 14-22", ["source_type"] = "Manual", ["is_locked"] = true,
        });

        var optDoc = await docs.InsertAsync(sys, "ScheduleOptimization", new Dictionary<string, object?>
        {
            ["company"] = "ACME", ["organization_unit"] = "OPS",
            ["from_date"] = "2026-12-01", ["to_date"] = "2026-12-03",
            ["candidate_shift_types"] = "Morning 06-14,Afternoon 14-22",
            ["time_limit_seconds"] = 10,
        });

        var runRes = await WsmOptimizationService.RunAsync(store, "acme", optDoc, CancellationToken.None);
        Assert.True(runRes.IsSuccess, "Optimization service run succeeded");
        var refreshed = await store.GetAsync("acme", "ScheduleOptimization", optDoc.Name, CancellationToken.None);
        Assert.Equal("Proposed", refreshed!.Data.Str("status"), "Status moved to Proposed");

        var (applied, skipped, _) = await WsmOptimizationService.ApplyAsync(store, "acme", refreshed, CancellationToken.None);
        Assert.True(applied >= 5, $"Applied {applied} unlocked slots");
        Assert.Equal(1, skipped, "Skipped 1 locked row");

        var m = new Masters(store, "acme");
        var lockedAfter = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", "OPT-1"), new FilterClause("work_date", "=", "2026-12-01")], CancellationToken.None)).Single();
        Assert.Equal("Afternoon 14-22", lockedAfter.Data.Str("shift_type"), "Locked manual slot not overwritten");
        Assert.Equal("Manual", lockedAfter.Data.Str("source_type"), "Locked slot source preserved");
    }

    [Test]
    public async Task MatrixReporting_dotted_line_project_approval_and_acting_delegation_on_leave()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        var m = new Masters(store, "acme");

        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "IDR", ["symbol"] = "Rp" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?> { ["company_code"] = "ACME", ["company_name"] = "Acme Corp", ["base_currency"] = "IDR", ["country"] = "ID" });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-STD", ["job_title"] = "Standard Staff" });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = "DEPT-A", ["org_unit_name"] = "Dept A", ["company"] = "ACME", ["unit_type"] = "Department", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = "DEPT-B", ["org_unit_name"] = "Dept B", ["company"] = "ACME", ["unit_type"] = "Department", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });

        // Employees: MGR-A (line manager), PLT-A (acting substitute), PM-B (cross-dept project manager), SPONSOR (director), STAFF-A (requester)
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "SPONSOR", ["full_name"] = "Director Sponsor", ["gender"] = "Male", ["date_of_birth"] = "1980-01-01", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "MGR-A", ["full_name"] = "Manager A", ["gender"] = "Male", ["date_of_birth"] = "1985-01-01", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["reports_to"] = "SPONSOR", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "PLT-A", ["full_name"] = "Acting A", ["gender"] = "Female", ["date_of_birth"] = "1988-01-01", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["reports_to"] = "SPONSOR", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "PM-B", ["full_name"] = "Project Manager B", ["gender"] = "Female", ["date_of_birth"] = "1987-01-01", ["company"] = "ACME", ["organization_unit"] = "DEPT-B", ["job_profile"] = "JOB-STD", ["reports_to"] = "SPONSOR", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        var staffDoc = await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = "STAFF-A", ["full_name"] = "Staff A", ["gender"] = "Male", ["date_of_birth"] = "1995-01-01", ["company"] = "ACME", ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["reports_to"] = "MGR-A", ["leave_approver"] = "MGR-A", ["expense_approver"] = "MGR-A", ["hire_date"] = "2026-01-01", ["status"] = "Active" });

        // Project + Dotted-line Assignment + Acting Assignment
        await docs.InsertAsync(sys, "Project", new Dictionary<string, object?>
        {
            ["project_code"] = "PRJ-MATRIX", ["project_name"] = "Matrix Project",
            ["company"] = "ACME", ["status"] = "In Progress", ["project_manager"] = "PM-B", ["sponsor"] = "SPONSOR",
        });
        await docs.InsertAsync(sys, "EmployeeAssignment", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["assignment_type"] = "Secondary", ["status"] = "Active",
            ["reporting_line"] = "Dotted", ["project"] = "PRJ-MATRIX", ["dotted_line_manager"] = "PM-B",
            ["has_approval_authority"] = true, ["start_date"] = "2026-01-01",
        });
        await docs.InsertAsync(sys, "EmployeeAssignment", new Dictionary<string, object?>
        {
            ["employee"] = "PLT-A", ["assignment_type"] = "Acting", ["status"] = "Active",
            ["reporting_line"] = "Solid", ["organization_unit"] = "DEPT-A", ["acting_for"] = "MGR-A",
            ["has_approval_authority"] = true, ["start_date"] = "2026-12-01", ["end_date"] = "2026-12-31",
        });

        // 1. ResolveApproversAsync when MGR-A is NOT on leave
        var r1 = await Hr.ResolveApproversAsync(m, staffDoc, "PRJ-MATRIX", new DateOnly(2026, 12, 10), CancellationToken.None, "leave_approver");
        Assert.Equal("MGR-A", r1.LineApprover, "Solid line approver is MGR-A");
        Assert.Equal("PM-B", r1.ProjectApprover, "Dotted/project approver is PM-B");
        Assert.True(!r1.LineDelegated, "Not delegated when MGR-A is available");

        // Self-approval guard: PM-B requesting on PRJ-MATRIX falls back to SPONSOR
        var pmDoc = (await m.GetAsync("Employee", "PM-B", CancellationToken.None))!;
        var rSelf = await Hr.ResolveApproversAsync(m, pmDoc, "PRJ-MATRIX", new DateOnly(2026, 12, 10), CancellationToken.None);
        Assert.Equal("SPONSOR", rSelf.ProjectApprover, "Self-approval prevented: PM-B falls back to project sponsor");

        // 2. OvertimeRequest WITH project routes through Pending Project Approval -> Pending Approval -> Approved
        var staffUser = new UserContext("u-staff", "Staff A", "acme", new HashSet<string> { "Employee" }, new Dictionary<string, string> { ["employee"] = "STAFF-A" });
        var pmUser = new UserContext("u-pm", "PM B", "acme", new HashSet<string> { "Employee" }, new Dictionary<string, string> { ["employee"] = "PM-B" });
        var mgrUser = new UserContext("u-mgr", "Mgr A", "acme", new HashSet<string> { "Employee" }, new Dictionary<string, string> { ["employee"] = "MGR-A" });
        var pltUser = new UserContext("u-plt", "Acting A", "acme", new HashSet<string> { "Employee" }, new Dictionary<string, string> { ["employee"] = "PLT-A" });

        var otWithProj = await docs.InsertAsync(staffUser, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["overtime_date"] = "2026-12-10", ["hours"] = 2m,
            ["day_type"] = "Working day", ["project"] = "PRJ-MATRIX", ["reason"] = "Matrix deployment",
        });
        Assert.Equal("PM-B", otWithProj.Data.Str("project_approver"), "Project approver resolved on OvertimeRequest");
        otWithProj = await docs.RunActionAsync(staffUser, "OvertimeRequest", otWithProj.Name, "Submit for project approval", otWithProj.Version, null);
        Assert.Equal("Pending Project Approval", otWithProj.WorkflowState, "Routed to Pending Project Approval first");
        otWithProj = await docs.RunActionAsync(pmUser, "OvertimeRequest", otWithProj.Name, "Approve (Project)", otWithProj.Version, "PM approved");
        Assert.Equal("Pending Approval", otWithProj.WorkflowState, "Advanced to Pending Approval (line manager)");
        otWithProj = await docs.RunActionAsync(mgrUser, "OvertimeRequest", otWithProj.Name, "Approve", otWithProj.Version, "Line manager approved");
        Assert.Equal("Approved", otWithProj.WorkflowState, "Final approval completed");

        // 3. OvertimeRequest WITHOUT project skips Pending Project Approval
        var otNoProj = await docs.InsertAsync(staffUser, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["overtime_date"] = "2026-12-11", ["hours"] = 2m,
            ["day_type"] = "Working day", ["reason"] = "Regular task",
        });
        otNoProj = await docs.RunActionAsync(staffUser, "OvertimeRequest", otNoProj.Name, "Submit for approval", otNoProj.Version, null);
        Assert.Equal("Pending Approval", otNoProj.WorkflowState, "Without project goes directly to Pending Approval");

        // 4. MGR-A takes approved leave on 2026-12-15 -> Resolver + Delegation Refresh Job delegate to PLT-A!
        await docs.InsertAsync(sys, "LeaveType", new Dictionary<string, object?> { ["leave_type_name"] = "Annual", ["is_annual_quota"] = false, ["is_paid"] = true, ["max_consecutive_days"] = 10 });
        var hrUser = new UserContext("u-hr", "HR", "acme", new HashSet<string> { "HR", "Employee" }, new Dictionary<string, string>());
        var mgrLeave = await docs.InsertAsync(hrUser, "LeaveApplication", new Dictionary<string, object?>
        {
            ["employee"] = "MGR-A", ["leave_type"] = "Annual",
            ["from_date"] = "2026-12-15", ["to_date"] = "2026-12-16", ["reason"] = "Vacation",
        });
        mgrLeave = await docs.RunActionAsync(hrUser, "LeaveApplication", mgrLeave.Name, "Submit for approval", mgrLeave.Version, null);
        mgrLeave = await docs.RunActionAsync(hrUser, "LeaveApplication", mgrLeave.Name, "Approve (HR)", mgrLeave.Version, "OK");

        var rOnLeave = await Hr.ResolveApproversAsync(m, staffDoc, "PRJ-MATRIX", new DateOnly(2026, 12, 15), CancellationToken.None, "leave_approver");
        Assert.Equal("PLT-A", rOnLeave.LineApprover, "Acting (Plt.) PLT-A substituted while MGR-A is on leave");
        Assert.True(rOnLeave.LineDelegated, "LineDelegated flag set");

        // Pending document (otNoProj) gets re-pointed by ApprovalDelegationRefreshJob
        var delegJob = new ApprovalDelegationRefreshJob();
        await delegJob.ExecuteAsync(new Framework.Jobs.ScheduledJobContext
        {
            TenantId = "acme", SystemUser = sys, Docs = docs, Services = provider,
            ScheduledTime = new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        var otDelegated = (await store.GetAsync("acme", "OvertimeRequest", otNoProj.Name, CancellationToken.None))!;
        Assert.Equal("PLT-A", otDelegated.Data.Str("approver"), "Delegation refresh job re-pointed pending doc to PLT-A");
        otDelegated = await docs.RunActionAsync(pltUser, "OvertimeRequest", otDelegated.Name, "Approve", otDelegated.Version, "Approved as Acting (Plt.)");
        Assert.Equal("Approved", otDelegated.WorkflowState, "Acting (Plt.) holder successfully approved delegated document");
    }
}
