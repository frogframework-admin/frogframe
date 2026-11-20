using System.Text;
using Apps.Erp.Hooks;
using Framework.Imports;

/// <summary>
/// Roster calendar quick actions (P1-4): coverage per shift slot with sub-unit and job-profile scoping,
/// day scopes, summed requirements, the per-slot shortage indicator and the team schedule export.
/// </summary>
sealed partial class Tests
{
    private static readonly WsmRosterView.UnitNode[] RosterUnits =
    [
        new("PLANT", "/PLANT/"),
        new("LINE-A", "/PLANT/LINE-A/"),
        new("LINE-B", "/PLANT/LINE-B/"),
        new("OFFICE", "/OFFICE/"),
    ];

    private static Dictionary<string, object?> Req(string unit, string shift, int min, int target, string? job = null, string scope = "All days",
        string? from = null, string? to = null, bool active = true) => new()
    {
        ["organization_unit"] = unit, ["shift_type"] = shift, ["min_headcount"] = (long)min, ["target_headcount"] = (long)target,
        ["job_profile"] = job, ["day_scope"] = scope, ["effective_from"] = from, ["effective_to"] = to, ["is_active"] = active,
    };

    private static Dictionary<string, object?> Sched(string emp, string date, string unit, string? shift, string type = "WORK", bool holiday = false) => new()
    {
        ["employee"] = emp, ["work_date"] = date, ["organization_unit"] = unit, ["shift_type"] = shift, ["schedule_type"] = type, ["is_public_holiday"] = holiday,
    };

    [Test]
    public Task RosterCalendar_coverage_counts_sub_units_job_profiles_day_scopes_and_sums_requirements()
    {
        // 2026-11-06 is a Friday, 2026-11-07 a Saturday.
        DateOnly fri = new(2026, 11, 6), sat = new(2026, 11, 7);
        var days = new[] { fri, sat };
        var reqs = new[]
        {
            Req("PLANT", "Morning", 3, 4),                          // counts LINE-A and LINE-B staff too
            Req("LINE-A", "Morning", 1, 2, job: "Technician"),      // only technicians of LINE-A
            Req("LINE-A", "Morning", 1, 1, job: "Technician"),      // same scope: adds up to 2/3
            Req("OFFICE", "Morning", 1, 1, scope: "Weekdays"),
            Req("OFFICE", "Night", 1, 1, scope: "Weekends"),
            Req("OFFICE", "Evening", 1, 1, scope: "Public holidays"),
            Req("PLANT", "Night", 1, 1, active: false),             // inactive: ignored
            Req("PLANT", "Evening", 1, 1, from: "2026-11-07"),      // not effective on Friday
        };
        var scheds = new[]
        {
            Sched("E1", "2026-11-06", "LINE-A", "Morning"),
            Sched("E2", "2026-11-06", "LINE-A", "Morning"),
            Sched("E3", "2026-11-06", "LINE-B", "Morning"),
            Sched("E4", "2026-11-06", "PLANT", "Morning"),
            Sched("E5", "2026-11-06", "OFFICE", "Morning", type: "LEAVE"),  // not WORK: not counted
            Sched("E6", "2026-11-07", "OFFICE", null, type: "HOLIDAY", holiday: true),
        };
        var jobs = new Dictionary<string, string?> { ["E1"] = "Technician", ["E2"] = "Operator", ["E3"] = "Technician", ["E4"] = null };

        var rows = WsmRosterView.CoverageRows(days, reqs, scheds, jobs, RosterUnits, null);
        WsmCoverage.CoverageRow Row(DateOnly d, string unit, string shift, string? job = null) =>
            rows.Single(r => r.WorkDate == d && r.OrganizationUnit == unit && r.ShiftType == shift && r.JobProfile == job);

        var plant = Row(fri, "PLANT", "Morning");
        Assert.Equal(4, plant.ActualHeadcount, "PLANT morning counts the unit and its sub-units");
        Assert.Equal("Sufficient", plant.Status, "PLANT morning status");
        var tech = Row(fri, "LINE-A", "Morning", "Technician");
        Assert.Equal(2, tech.MinHeadcount, "two requirements with the same scope add up (min)");
        Assert.Equal(3, tech.TargetHeadcount, "two requirements with the same scope add up (target)");
        Assert.Equal(1, tech.ActualHeadcount, "only LINE-A technicians count (E3 is in LINE-B, E2 is an operator)");
        Assert.Equal("Understaffed", tech.Status, "LINE-A technicians short");
        Assert.Equal(0, Row(fri, "OFFICE", "Morning").ActualHeadcount, "a LEAVE row is not counted as working");
        Assert.True(!rows.Any(r => r.WorkDate == sat && r.ShiftType == "Morning" && r.OrganizationUnit == "OFFICE"), "weekday requirement skipped on Saturday");
        Assert.True(rows.Any(r => r.WorkDate == sat && r.OrganizationUnit == "OFFICE" && r.ShiftType == "Night"), "weekend requirement applies on Saturday");
        Assert.True(!rows.Any(r => r.WorkDate == fri && r.OrganizationUnit == "OFFICE" && r.ShiftType == "Night"), "weekend requirement skipped on Friday");
        Assert.True(rows.Any(r => r.WorkDate == sat && r.ShiftType == "Evening" && r.OrganizationUnit == "OFFICE"), "public-holiday requirement applies on a flagged day");
        Assert.True(!rows.Any(r => r.WorkDate == fri && r.ShiftType == "Evening" && r.OrganizationUnit == "OFFICE"), "public-holiday requirement skipped on a normal day");
        Assert.True(!rows.Any(r => r.ShiftType == "Night" && r.OrganizationUnit == "PLANT"), "inactive requirement ignored");
        Assert.True(!rows.Any(r => r.WorkDate == fri && r.ShiftType == "Evening" && r.OrganizationUnit == "PLANT"), "requirement not yet effective");
        Assert.True(rows.Any(r => r.WorkDate == sat && r.ShiftType == "Evening" && r.OrganizationUnit == "PLANT"), "requirement effective from Saturday");

        // Unit filter: LINE-A keeps only requirements on LINE-A or below (not the parent PLANT, not OFFICE).
        var lineA = WsmRosterView.CoverageRows(days, reqs, scheds, jobs, RosterUnits, "LINE-A");
        Assert.True(lineA.Count > 0 && lineA.All(r => r.OrganizationUnit == "LINE-A"), "unit filter keeps the selected subtree: " + string.Join(",", lineA.Select(r => r.OrganizationUnit)));
        var plantView = WsmRosterView.CoverageRows(days, reqs, scheds, jobs, RosterUnits, "PLANT");
        Assert.True(plantView.Any(r => r.OrganizationUnit == "LINE-A") && plantView.All(r => r.OrganizationUnit != "OFFICE"), "parent filter includes sub-unit requirements only");

        // Slot indicator: Friday morning sums every scope and lists the short ones.
        var slots = WsmRosterView.SlotSummary(rows);
        var slot = slots[(fri, "Morning")];
        Assert.Equal(3 + 2 + 1, slot.Required, "slot required = sum of minimums");
        Assert.Equal(4 + 1 + 0, slot.Actual, "slot actual = sum over scopes");
        Assert.Equal("Understaffed", slot.Status, "slot is short when any scope is short");
        Assert.True(slot.ShortScopes.Contains("LINE-A / Technician: 1/2") && slot.ShortScopes.Contains("OFFICE: 0/1"), "short scopes listed: " + string.Join("; ", slot.ShortScopes));
        Assert.Equal("Understaffed", slots[(sat, "Evening")].Status, "Saturday evening: nobody scheduled");
        return Task.CompletedTask;
    }

    [Test]
    public Task RosterCalendar_export_builds_team_schedule_as_xlsx_and_csv()
    {
        DateOnly d1 = new(2026, 11, 1), d2 = new(2026, 11, 2), d3 = new(2026, 11, 3), d4 = new(2026, 11, 4);
        var days = new[] { d1, d2, d3, d4 };
        IReadOnlyDictionary<string, object?> S(string type, string? shift = null, bool night = false) =>
            new Dictionary<string, object?> { ["schedule_type"] = type, ["shift_type"] = shift, ["crosses_midnight"] = night };
        var employees = new[]
        {
            new WsmRosterView.ExportEmployee("EMP-001", "Siti Nurhaliza", "LINE-A", "Group A", new Dictionary<DateOnly, IReadOnlyDictionary<string, object?>>
            {
                [d1] = S("WORK", "Morning"), [d2] = S("WORK", "Night", night: true), [d3] = S("REST"), [d4] = S("LEAVE"),
            }),
            new WsmRosterView.ExportEmployee("EMP-002", "=HYPERLINK(\"x\")", null, null, new Dictionary<DateOnly, IReadOnlyDictionary<string, object?>>
            {
                [d1] = S("HOLIDAY"),
            }),
        };
        var coverage = new List<WsmCoverage.CoverageRow> { new(d1, "LINE-A", "Morning", null, 2, 3, 1, "Understaffed") };

        var xlsx = WsmRosterView.Export("xlsx", "2026-11", "LINE A", days, employees, coverage);
        Assert.Equal("roster-2026-11-LINE-A.xlsx", xlsx.FileName, "xlsx file name");
        Assert.Equal(2, xlsx.Rows, "row count");
        var sheets = SpreadsheetReader.Read(xlsx.Content, xlsx.FileName, 10_000_000);
        Assert.True(sheets.Select(s => s.Name).SequenceEqual(["Roster 2026-11", "Coverage"]), "sheets: " + string.Join(",", sheets.Select(s => s.Name)));
        var head = sheets[0].Rows[0].Select(v => v?.ToString()).ToList();
        Assert.True(head.Take(5).SequenceEqual(["Employee", "Employee Name", "Organization Unit", "Shift Group", "01 Sun"]), "headers: " + string.Join("|", head));
        Assert.True(head.TakeLast(5).SequenceEqual(["Work Days", "Night Shifts", "Rest Days", "Leave Days", "Holidays"]), "total headers");
        var r1 = sheets[0].Rows[1].Select(v => v?.ToString()).ToList();
        Assert.True(r1.Skip(4).Take(4).SequenceEqual(["Morning", "Night", "OFF", "LEAVE"]), "day cells: " + string.Join("|", r1));
        Assert.True(r1.TakeLast(5).SequenceEqual(["2", "1", "1", "1", "0"]), "totals: " + string.Join("|", r1.TakeLast(5)));
        var cov = sheets[1].Rows[1].Select(v => v?.ToString()).ToList();
        Assert.True(cov.Skip(1).SequenceEqual(["LINE-A", "Morning", "All roles", "2", "3", "1", "Understaffed"]), "coverage sheet: " + string.Join("|", cov));

        var csv = WsmRosterView.Export("csv", "2026-11", null, days, employees, coverage);
        Assert.Equal("roster-2026-11.csv", csv.FileName, "csv file name");
        Assert.True(csv.Content.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()), "csv starts with a UTF-8 BOM");
        var lines = Encoding.UTF8.GetString(csv.Content, 3, csv.Content.Length - 3).Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length, "csv lines");
        Assert.True(lines[1].StartsWith("EMP-001,Siti Nurhaliza,LINE-A,Group A,Morning,Night,OFF,LEAVE,2,1,1,1,0"), "csv row: " + lines[1]);
        Assert.True(lines[2].StartsWith("EMP-002,\"'=HYPERLINK(\"\"x\"\")\",,,HOLIDAY,,,,0,0,0,0,1"), "formula injection guarded: " + lines[2]);
        return Task.CompletedTask;
    }
}
