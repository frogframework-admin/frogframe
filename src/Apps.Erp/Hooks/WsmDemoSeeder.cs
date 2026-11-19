using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

/// <summary>
/// Seeds realistic Enterprise Organization Management (OM) and Workforce Scheduling (WSM) sample data
/// for demo/development testing when Demo:SeedData is enabled.
/// </summary>
public static class WsmDemoSeeder
{
    public static async Task EnsureSampleDataAsync(IServiceProvider sp, string tenant, CancellationToken ct = default)
    {
        var store = sp.GetRequiredService<IDocumentStore>();
        var docs = sp.GetRequiredService<DocumentService>();
        var m = new Masters(store, tenant);

        // Only seed if ACME company and sample employees exist
        if (await m.GetAsync("Company", "ACME", ct) is null) return;
        if (await m.GetAsync("Employee", "EMP-0001", ct) is null) return;

        var admin = UserContext.System(tenant);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var nextMonthEnd = monthStart.AddMonths(2).AddDays(-1);
        var monthStartStr = D.Iso(monthStart);

        if (await m.GetAsync("ShiftPattern", "PAT-3SHIFT-ROT", ct) is null)
        {

        // 1. Ensure EmployeeAssignment sample (Acting role)
        if ((await m.QueryAsync("EmployeeAssignment", [new FilterClause("employee", "=", "EMP-0002")], ct, take: 1)).Count == 0
            && await m.GetAsync("OrganizationUnit", "DEPT-IT", ct) is not null)
        {
            await docs.InsertAsync(admin, "EmployeeAssignment", new Dictionary<string, object?>
            {
                ["employee"] = "EMP-0002",
                ["assignment_type"] = "Acting",
                ["is_primary"] = false,
                ["status"] = "Active",
                ["organization_unit"] = "DEPT-IT",
                ["start_date"] = monthStartStr,
                ["end_date"] = D.Iso(monthStart.AddMonths(3).AddDays(-1)),
                ["reference_memo"] = "SK-PLT-2026-01",
                ["remarks"] = "Sample acting assignment for IT Department coordination.",
            }, "Seeded demo data", ct);
        }

        // 2. WorkScheduleRule
        if (await m.GetAsync("WorkScheduleRule", "WSR-ACME-STD", ct) is null)
        {
            await docs.InsertAsync(admin, "WorkScheduleRule", new Dictionary<string, object?>
            {
                ["rule_code"] = "WSR-ACME-STD",
                ["rule_name"] = "ACME Standard Labor & Rest Rule (PP 35/2021)",
                ["company"] = "ACME",
                ["max_consecutive_work_days"] = 6,
                ["min_rest_hours_between_shifts"] = 11m,
                ["forbid_night_to_morning"] = true,
                ["enforcement_mode"] = "Strict",
                ["is_active"] = true,
                ["description"] = "Maximum 6 consecutive work days, minimum 11 hours rest between shifts, and no night-to-morning turnaround.",
            }, "Seeded demo data", ct);
        }

        // 3. ShiftPatterns
        await docs.InsertAsync(admin, "ShiftPattern", new Dictionary<string, object?>
        {
            ["pattern_code"] = "PAT-3SHIFT-ROT",
            ["pattern_name"] = "3-Shift Continuous Rotation (6-2)",
            ["base_pattern_code"] = "PAT-3SHIFT-ROT",
            ["pattern_version"] = 1,
            ["cycle_days"] = 8,
            ["work_week_days"] = "6",
            ["effective_from"] = monthStartStr,
            ["status"] = "Active",
            ["is_active"] = true,
            ["description"] = "2 days Morning (06-14), 2 days Afternoon (14-22), 2 days Night (22-06), 2 days Rest.",
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["day_number"] = 1, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Pagi 06-14" },
                new() { ["day_number"] = 2, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Pagi 06-14" },
                new() { ["day_number"] = 3, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Siang 14-22" },
                new() { ["day_number"] = 4, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Siang 14-22" },
                new() { ["day_number"] = 5, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Malam 22-06" },
                new() { ["day_number"] = 6, ["schedule_type"] = "WORK", ["shift_type"] = "Shift Malam 22-06" },
                new() { ["day_number"] = 7, ["schedule_type"] = "REST" },
                new() { ["day_number"] = 8, ["schedule_type"] = "REST" },
            },
        }, "Seeded demo data", ct);

        await docs.InsertAsync(admin, "ShiftPattern", new Dictionary<string, object?>
        {
            ["pattern_code"] = "PAT-OFFICE-5D",
            ["pattern_name"] = "Office Regular 5-Day (Mon-Fri)",
            ["base_pattern_code"] = "PAT-OFFICE-5D",
            ["pattern_version"] = 1,
            ["cycle_days"] = 7,
            ["work_week_days"] = "5",
            ["effective_from"] = monthStartStr,
            ["status"] = "Active",
            ["is_active"] = true,
            ["description"] = "5 days Office 08-17, 2 days Rest.",
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["day_number"] = 1, ["schedule_type"] = "WORK", ["shift_type"] = "Office 08-17" },
                new() { ["day_number"] = 2, ["schedule_type"] = "WORK", ["shift_type"] = "Office 08-17" },
                new() { ["day_number"] = 3, ["schedule_type"] = "WORK", ["shift_type"] = "Office 08-17" },
                new() { ["day_number"] = 4, ["schedule_type"] = "WORK", ["shift_type"] = "Office 08-17" },
                new() { ["day_number"] = 5, ["schedule_type"] = "WORK", ["shift_type"] = "Office 08-17" },
                new() { ["day_number"] = 6, ["schedule_type"] = "REST" },
                new() { ["day_number"] = 7, ["schedule_type"] = "REST" },
            },
        }, "Seeded demo data", ct);

        // 4. ShiftGroups
        var whUnit = await m.GetAsync("OrganizationUnit", "DEPT-WH", ct) is not null ? "DEPT-WH" : "BOD";
        var itUnit = await m.GetAsync("OrganizationUnit", "DEPT-IT", ct) is not null ? "DEPT-IT" : "BOD";

        await EnsureShiftGroupAsync(docs, m, admin, "REGU-A", "Regu Operasional A", "ACME", whUnit, "EMP-0005", monthStartStr, ["EMP-0005", "EMP-0006", "EMP-0008"], ct);
        await EnsureShiftGroupAsync(docs, m, admin, "REGU-B", "Regu Operasional B", "ACME", whUnit, "EMP-0009", monthStartStr, ["EMP-0009", "EMP-0010"], ct);
        await EnsureShiftGroupAsync(docs, m, admin, "REGU-OFFICE", "Regu Kantor & IT", "ACME", itUnit, "EMP-0001", monthStartStr, ["EMP-0001", "EMP-0002", "EMP-0003", "EMP-0004"], ct);

        // 5. ShiftAssignments
        await docs.InsertAsync(admin, "ShiftAssignment", new Dictionary<string, object?>
        {
            ["assignment_target"] = "Shift Group",
            ["shift_group"] = "REGU-A",
            ["company"] = "ACME",
            ["shift_pattern"] = "PAT-3SHIFT-ROT",
            ["start_date"] = monthStartStr,
            ["day_offset"] = 0,
            ["respect_public_holidays"] = true,
            ["is_active"] = true,
            ["remarks"] = "Regu A starts at cycle day 1.",
        }, "Seeded demo data", ct);

        await docs.InsertAsync(admin, "ShiftAssignment", new Dictionary<string, object?>
        {
            ["assignment_target"] = "Shift Group",
            ["shift_group"] = "REGU-B",
            ["company"] = "ACME",
            ["shift_pattern"] = "PAT-3SHIFT-ROT",
            ["start_date"] = monthStartStr,
            ["day_offset"] = 2,
            ["respect_public_holidays"] = true,
            ["is_active"] = true,
            ["remarks"] = "Regu B staggered by 2 days for continuous shift coverage.",
        }, "Seeded demo data", ct);

        await docs.InsertAsync(admin, "ShiftAssignment", new Dictionary<string, object?>
        {
            ["assignment_target"] = "Shift Group",
            ["shift_group"] = "REGU-OFFICE",
            ["company"] = "ACME",
            ["shift_pattern"] = "PAT-OFFICE-5D",
            ["start_date"] = monthStartStr,
            ["day_offset"] = 0,
            ["respect_public_holidays"] = true,
            ["is_active"] = true,
            ["remarks"] = "Office regular 5-day work week.",
        }, "Seeded demo data", ct);

        // 6. ShiftRequirements
        foreach (var (shift, min, target) in new[] { ("Shift Pagi 06-14", 1, 2), ("Shift Siang 14-22", 1, 2), ("Shift Malam 22-06", 1, 1) })
        {
            await docs.InsertAsync(admin, "ShiftRequirement", new Dictionary<string, object?>
            {
                ["company"] = "ACME",
                ["organization_unit"] = whUnit,
                ["shift_type"] = shift,
                ["day_scope"] = "All days",
                ["min_headcount"] = min,
                ["target_headcount"] = target,
                ["effective_from"] = monthStartStr,
                ["is_active"] = true,
            }, "Seeded demo data", ct);
        }

        // 7. Generate Materialized Schedules (current month + next month)
        await docs.InsertAsync(admin, "ScheduleGeneration", new Dictionary<string, object?>
        {
            ["company"] = "ACME",
            ["from_date"] = monthStartStr,
            ["to_date"] = D.Iso(nextMonthEnd),
            ["overwrite_unlocked"] = false,
        }, "Seeded demo data", ct);
        }

        // 8. Sample ShiftOverride (EMP-0006 on day 10 of current month: Morning 06-14 -> Afternoon 14-22)
        if ((await m.QueryAsync("ShiftOverride", [new FilterClause("employee", "=", "EMP-0006")], ct, take: 1)).Count == 0)
        {
            var overrideDate = D.Iso(monthStart.AddDays(9));
            await docs.InsertAsync(admin, "ShiftOverride", new Dictionary<string, object?>
            {
                ["employee"] = "EMP-0006",
                ["override_date"] = overrideDate,
                ["new_schedule_type"] = "WORK",
                ["new_shift_type"] = "Shift Siang 14-22",
                ["reason"] = "Sample operational coverage backup",
            }, "Seeded demo data", ct);
        }

        // 9. Sample ShiftSwapRequest (EMP-0005 <-> EMP-0002 on first Monday of month where both work daytime shifts)
        if ((await m.QueryAsync("ShiftSwapRequest", [new FilterClause("requester", "=", "EMP-0005")], ct, take: 1)).Count == 0)
        {
            var firstMonday = monthStart;
            while (firstMonday.DayOfWeek != DayOfWeek.Monday) firstMonday = firstMonday.AddDays(1);
            var swapDate = D.Iso(firstMonday);
            try
            {
                var swapDoc = await docs.InsertAsync(admin, "ShiftSwapRequest", new Dictionary<string, object?>
                {
                    ["requester"] = "EMP-0005",
                    ["target_employee"] = "EMP-0002",
                    ["swap_date"] = swapDate,
                    ["target_swap_date"] = swapDate,
                    ["reason"] = "Sample shift swap request for family appointment",
                }, "Seeded demo data", ct);
                await docs.RunActionAsync(admin, "ShiftSwapRequest", swapDoc.Name, "Request Swap", swapDoc.Version, null, ct);
            }
            catch (ValidationFailedException)
            {
                // Ignore if a public holiday happens to fall on the first Monday
            }
        }

        // 10. Priority 3 Demo: Shift Preferences (Prefer, Avoid, Unavailable)
        var nextMonthStart = monthStart.AddMonths(1);
        var nextMonth14 = nextMonthStart.AddDays(13);
        if ((await m.QueryAsync("ShiftPreference", [], ct, take: 1)).Count == 0)
        {
            var samplePrefs = new (string Emp, string Type, string? Shift, string? Days, string? From, string? To, int Weight, string Reason)[]
            {
                ("EMP-0006", "Prefer", "Shift Pagi 06-14", "Mon,Tue,Wed", null, null, 4, "Prefers morning shifts early in the week"),
                ("EMP-0006", "Avoid", "Shift Malam 22-06", "Fri,Sat", null, null, 3, "Avoids weekend night shifts"),
                ("EMP-0007", "Prefer", "Shift Siang 14-22", "Mon,Tue,Wed,Thu,Fri", null, null, 4, "Prefers afternoon shifts"),
                ("EMP-0008", "Avoid", "Shift Pagi 06-14", "Sat,Sun", null, null, 3, "Avoids weekend morning shifts"),
                ("EMP-0009", "Prefer", "Shift Malam 22-06", null, D.Iso(nextMonthStart), D.Iso(nextMonth14), 5, "Volunteers for night shift allowance"),
                ("EMP-0007", "Unavailable", null, null, D.Iso(nextMonthStart.AddDays(4)), D.Iso(nextMonthStart.AddDays(4)), 5, "Medical appointment — unavailable"),
            };
            foreach (var p in samplePrefs)
            {
                if (await m.GetAsync("Employee", p.Emp, ct) is null) continue;
                await docs.InsertAsync(admin, "ShiftPreference", new Dictionary<string, object?>
                {
                    ["employee"] = p.Emp,
                    ["preference_type"] = p.Type,
                    ["shift_type"] = p.Shift,
                    ["days_of_week"] = p.Days,
                    ["from_date"] = p.From,
                    ["to_date"] = p.To,
                    ["weight"] = p.Weight,
                    ["status"] = "Active",
                    ["reason"] = p.Reason,
                }, "Seeded demo data", ct);
            }
        }

        // 11. Priority 3 Demo: ScheduleOptimization in Proposed state (14-day window next month)
        if ((await m.QueryAsync("ScheduleOptimization", [], ct, take: 1)).Count == 0)
        {
            var optDoc = await docs.InsertAsync(admin, "ScheduleOptimization", new Dictionary<string, object?>
            {
                ["company"] = "ACME",
                ["from_date"] = D.Iso(nextMonthStart),
                ["to_date"] = D.Iso(nextMonth14),
                ["candidate_shift_types"] = "Shift Pagi 06-14,Shift Siang 14-22,Shift Malam 22-06",
                ["time_limit_seconds"] = 10,
                ["w_coverage"] = 100,
                ["w_preference"] = 10,
                ["w_fairness"] = 20,
                ["w_night_balance"] = 15,
                ["w_stability"] = 3,
            }, "Seeded demo data", ct);
            await WsmOptimizationService.RunAsync(store, tenant, optDoc, ct);
        }

        // 12. Priority 3 Demo: Matrix Project (PRJ-ERP-ROLLOUT) + Dotted-line & Acting (Plt.) Assignments
        if (await m.GetAsync("Project", "PRJ-ERP-ROLLOUT", ct) is null
            && await m.GetAsync("Employee", "EMP-0003", ct) is not null)
        {
            await docs.InsertAsync(admin, "Project", new Dictionary<string, object?>
            {
                ["project_code"] = "PRJ-ERP-ROLLOUT",
                ["project_name"] = "ERP Cross-Department Rollout",
                ["company"] = "ACME",
                ["status"] = "In Progress",
                ["start_date"] = monthStartStr,
                ["project_manager"] = "EMP-0003",
                ["sponsor"] = await m.GetAsync("Employee", "EMP-0001", ct) is not null ? "EMP-0001" : null,
            }, "Seeded demo data", ct);
        }

        var allAsgs = await m.QueryAsync("EmployeeAssignment", [], ct, take: 100);
        if (!allAsgs.Any(a => a.Data.Str("project") == "PRJ-ERP-ROLLOUT")
            && await m.GetAsync("Employee", "EMP-0006", ct) is not null
            && await m.GetAsync("Project", "PRJ-ERP-ROLLOUT", ct) is not null)
        {
            await docs.InsertAsync(admin, "EmployeeAssignment", new Dictionary<string, object?>
            {
                ["employee"] = "EMP-0006",
                ["assignment_type"] = "Secondary",
                ["status"] = "Active",
                ["reporting_line"] = "Dotted",
                ["project"] = "PRJ-ERP-ROLLOUT",
                ["dotted_line_manager"] = "EMP-0003",
                ["has_approval_authority"] = true,
                ["start_date"] = monthStartStr,
                ["remarks"] = "Cross-department ERP rollout implementation team member (dotted-line to Project Manager).",
            }, "Seeded demo data", ct);
        }

        if (!allAsgs.Any(a => a.Data.Str("acting_for") == "EMP-0002")
            && await m.GetAsync("Employee", "EMP-0005", ct) is not null
            && await m.GetAsync("Employee", "EMP-0002", ct) is not null)
        {
            var emp2 = await m.GetAsync("Employee", "EMP-0002", ct);
            await docs.InsertAsync(admin, "EmployeeAssignment", new Dictionary<string, object?>
            {
                ["employee"] = "EMP-0005",
                ["assignment_type"] = "Acting",
                ["status"] = "Active",
                ["reporting_line"] = "Solid",
                ["organization_unit"] = emp2?.Data.Str("organization_unit"),
                ["acting_for"] = "EMP-0002",
                ["has_approval_authority"] = true,
                ["start_date"] = monthStartStr,
                ["end_date"] = D.Iso(nextMonthEnd),
                ["reference_memo"] = "SK-PLT-2026-009",
                ["remarks"] = "Acting (Plt.) substitute for EMP-0002 during scheduled leave.",
            }, "Seeded demo data", ct);
        }

        // 13. Priority 3 Demo: Sample ExpenseClaim with Project in Pending Project Approval
        if ((await m.QueryAsync("ExpenseClaim", [new FilterClause("project", "=", "PRJ-ERP-ROLLOUT")], ct, take: 1)).Count == 0
            && await m.GetAsync("Employee", "EMP-0006", ct) is not null
            && await m.GetAsync("Project", "PRJ-ERP-ROLLOUT", ct) is not null)
        {
            var expType = (await m.QueryAsync("ExpenseClaimType", [], ct, take: 10))
                .FirstOrDefault(t => !t.Data.Bool("requires_receipt"))
                ?? (await m.QueryAsync("ExpenseClaimType", [], ct, take: 1)).FirstOrDefault();
            if (expType is not null && !expType.Data.Bool("requires_receipt"))
            {
                try
                {
                    var emp6User = new UserContext("demo-emp6", "EMP-0006", tenant, new HashSet<string> { "Employee" },
                        new Dictionary<string, string> { ["employee"] = "EMP-0006" });
                    var claim = await docs.InsertAsync(emp6User, "ExpenseClaim", new Dictionary<string, object?>
                    {
                        ["employee"] = "EMP-0006",
                        ["posting_date"] = D.Iso(today),
                        ["purpose"] = "ERP Rollout workshop transport & meals",
                        ["project"] = "PRJ-ERP-ROLLOUT",
                        ["items"] = new List<Dictionary<string, object?>>
                        {
                            new()
                            {
                                ["expense_date"] = D.Iso(today),
                                ["expense_type"] = expType.Name,
                                ["description"] = "Cross-department ERP rollout workshop",
                                ["amount"] = 150_000m,
                            }
                        }
                    }, "Seeded demo data", ct);
                    await docs.RunActionAsync(emp6User, "ExpenseClaim", claim.Name, "Submit for project approval", claim.Version, null, ct);
                }
                catch
                {
                    // Skip gracefully if accounting period or expense type requires extra setup
                }
            }
        }
    }

    private static async Task EnsureShiftGroupAsync(
        DocumentService docs, Masters m, UserContext admin, string code, string name,
        string company, string unit, string supervisor, string effectiveFrom, IReadOnlyList<string> empIds, CancellationToken ct)
    {
        if (await m.GetAsync("ShiftGroup", code, ct) is not null) return;
        var members = new List<Dictionary<string, object?>>();
        foreach (var empId in empIds)
        {
            if (await m.GetAsync("Employee", empId, ct) is null) continue;
            members.Add(new()
            {
                ["employee"] = empId,
                ["effective_from"] = effectiveFrom,
                ["is_active"] = true,
            });
        }
        await docs.InsertAsync(admin, "ShiftGroup", new Dictionary<string, object?>
        {
            ["group_code"] = code,
            ["group_name"] = name,
            ["company"] = company,
            ["organization_unit"] = unit,
            ["supervisor"] = await m.GetAsync("Employee", supervisor, ct) is not null ? supervisor : null,
            ["effective_from"] = effectiveFrom,
            ["is_active"] = true,
            ["members"] = members,
        }, "Seeded demo data", ct);
    }
}
