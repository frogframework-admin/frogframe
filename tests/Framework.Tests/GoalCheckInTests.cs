using System.Globalization;
using Apps.Erp;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

/// <summary>Goal (OKR) progress math, who may change a goal, and 1:1 check-ins (notes per side, locks, goal snapshot).</summary>
sealed partial class Tests
{
    [Test]
    public Task Goal_progress_per_measure_and_weighted_average()
    {
        Assert.Equal(50m, GoalMath.KeyResultProgress(GoalMath.Increase, 0m, 100m, 50m, false), "increase halfway to target");
        Assert.Equal(75m, GoalMath.KeyResultProgress(GoalMath.Decrease, 100m, 0m, 25m, false), "decrease from 100 to 0, now at 25");
        Assert.Equal(100m, GoalMath.KeyResultProgress(GoalMath.Increase, 0m, 100m, 150m, false), "overshoot capped at 100");
        Assert.Equal(0m, GoalMath.KeyResultProgress(GoalMath.Increase, 10m, 20m, 5m, false), "below the start is 0");
        Assert.Equal(100m, GoalMath.KeyResultProgress(GoalMath.Milestone, 0m, 0m, 0m, true), "milestone done");
        Assert.Equal(0m, GoalMath.KeyResultProgress(GoalMath.Milestone, 0m, 0m, 0m, false), "milestone open");
        Assert.Equal(70m, GoalMath.Weighted([(60m, 50m), (40m, 100m)]), "60% at 50 and 40% at 100 gives 70");
        Assert.Equal(0m, GoalMath.Weighted([(0m, 80m)]), "no weight, no progress");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Goal_activation_needs_weights_of_100_and_closed_goals_are_locked()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var other = MatrixUser("STAFF-B");

        var draft = await docs.InsertAsync(staff, "OkrObjective", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["period"] = "2026-Q1", ["title"] = "Ship onboarding", ["start_date"] = "2026-01-01", ["end_date"] = "2026-03-31",
            ["key_results"] = new List<Dictionary<string, object?>>
            {
                new() { ["description"] = "Forms signed", ["direction"] = "Increase", ["start_value"] = 0m, ["target_value"] = 100m, ["current_value"] = 50m, ["weight"] = 60m },
                new() { ["description"] = "Handbook published", ["direction"] = "Milestone", ["is_done"] = true, ["weight"] = 30m },
            },
        });
        Assert.Equal("MGR-A", draft.Data.Str("reports_to"), "reports_to comes from the employee record");
        Assert.Equal(66.7m, draft.Data.Dec("progress_percent"), "draft with weights 60 + 30: (60×50 + 30×100) / 90");

        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(staff, "OkrObjective", draft.Name, draft.Version,
            new Dictionary<string, object?> { ["status"] = "Active" }), "weights 90 cannot be Active");

        var rows = draft.Data.Rows("key_results").Select(r => new Dictionary<string, object?>(r)).ToList();
        rows[1]["weight"] = 40m;
        var active = await docs.UpdateAsync(staff, "OkrObjective", draft.Name, draft.Version,
            new Dictionary<string, object?> { ["status"] = "Active", ["key_results"] = rows });
        Assert.Equal(70m, active.Data.Dec("progress_percent"), "60% at 50 and 40% at 100 gives 70");

        await Assert.Throws<PermissionDeniedException>(() => docs.UpdateAsync(other, "OkrObjective", active.Name, active.Version,
            new Dictionary<string, object?> { ["title"] = "Taken over" }), "a peer cannot edit this goal");

        var byManager = await docs.UpdateAsync(manager, "OkrObjective", active.Name, active.Version,
            new Dictionary<string, object?> { ["description"] = "Agreed in Q1 planning" });
        Assert.Equal("Agreed in Q1 planning", byManager.Data.Str("description"), "the manager may set the goal with the employee");

        var achieved = await docs.UpdateAsync(staff, "OkrObjective", byManager.Name, byManager.Version,
            new Dictionary<string, object?> { ["status"] = "Achieved" });
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(staff, "OkrObjective", achieved.Name, achieved.Version,
            new Dictionary<string, object?> { ["title"] = "Edited after close" }), "closed goals are locked");

        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(staff, "OkrObjective", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["period"] = "2026-Q1-extra", ["title"] = "Bad period", ["start_date"] = "2026-01-01", ["end_date"] = "2026-03-31",
            ["key_results"] = new List<Dictionary<string, object?>> { new() { ["description"] = "x", ["weight"] = 100m } },
        }), "period must look like 2026-Q1");
    }

    [Test]
    public async Task One_on_one_notes_are_written_by_their_owner_and_locked_when_completed()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var other = MatrixUser("STAFF-B");
        var hr = MatrixUser("HR-USER", ErpRoles.HrTalent, "Employee");
        var when = new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero).ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);

        var goal = await docs.InsertAsync(staff, "OkrObjective", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["period"] = "2026-Q1", ["title"] = "Ship onboarding", ["start_date"] = "2026-01-01", ["end_date"] = "2026-03-31",
            ["status"] = "Active",
            ["key_results"] = new List<Dictionary<string, object?>>
            {
                new() { ["description"] = "Forms signed", ["direction"] = "Increase", ["start_value"] = 0m, ["target_value"] = 100m, ["current_value"] = 40m, ["weight"] = 100m },
            },
        });

        var meeting = await docs.InsertAsync(staff, "OneOnOne", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["meeting_at"] = when, ["agenda"] = "Sprint review", ["employee_notes"] = "Feeling stretched",
        });
        Assert.Equal("MGR-A", meeting.Data.Str("manager"), "manager comes from the employee record");

        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(manager, "OneOnOne", meeting.Name, meeting.Version,
            new Dictionary<string, object?> { ["employee_notes"] = "Edited by my boss" }), "the manager cannot write the employee notes");
        var withNotes = await docs.UpdateAsync(manager, "OneOnOne", meeting.Name, meeting.Version,
            new Dictionary<string, object?>
            {
                ["manager_notes"] = "Rebalance workload",
                ["objectives"] = new List<Dictionary<string, object?>> { new() { ["objective"] = goal.Name } },
            });
        Assert.Equal(40m, withNotes.Data.Rows("objectives").Single().Dec("progress_snapshot"), "goal progress copied to the check-in");

        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(staff, "OneOnOne", withNotes.Name, withNotes.Version,
            new Dictionary<string, object?> { ["manager_notes"] = "Written by staff" }), "the employee cannot write the manager notes");

        var done = await docs.UpdateAsync(staff, "OneOnOne", withNotes.Name, withNotes.Version,
            new Dictionary<string, object?>
            {
                ["status"] = "Completed",
                ["action_items"] = new List<Dictionary<string, object?>> { new() { ["action"] = "Share the onboarding checklist", ["due_date"] = "2026-02-20" } },
            });
        Assert.True(done.Data.Str("completed_at") is not null, "completed_at is stamped");
        Assert.Equal("STAFF-A", done.Data.Rows("action_items").Single().Str("assignee"), "assignee defaults to the employee");
        Assert.Equal("Open", done.Data.Rows("action_items").Single().Str("status"), "action items start as Open");

        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(manager, "OneOnOne", done.Name, done.Version,
            new Dictionary<string, object?> { ["agenda"] = "Changed later" }), "completed check-ins are locked");
        await Assert.Throws<DocumentNotFoundException>(() => docs.UpdateAsync(other, "OneOnOne", done.Name, done.Version,
            new Dictionary<string, object?> { ["agenda"] = "Peer edit" }), "a peer cannot change the check-in");

        var corrected = await docs.UpdateAsync(hr, "OneOnOne", done.Name, done.Version,
            new Dictionary<string, object?> { ["agenda"] = "Corrected by HR" });
        Assert.Equal("Corrected by HR", corrected.Data.Str("agenda"), "HR-Talent may correct a locked check-in");
    }

    [Test]
    public async Task Goals_are_readable_for_alignment_but_company_goals_and_loops_are_blocked()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var staff = MatrixUser("STAFF-A");
        var manager = MatrixUser("MGR-A");
        var hr = MatrixUser("HR-USER", ErpRoles.HrTalent, "Employee");
        Dictionary<string, object?> Goal(string employee, string title, string level, string? parent = null) => new()
        {
            ["employee"] = employee, ["period"] = "2026-Q1", ["title"] = title, ["level"] = level, ["parent_objective"] = parent,
            ["start_date"] = "2026-01-01", ["end_date"] = "2026-03-31",
            ["key_results"] = new List<Dictionary<string, object?>> { new() { ["description"] = "Done", ["direction"] = "Milestone", ["weight"] = 100m } },
        };

        await Assert.Throws<ValidationFailedException>(() => docs.InsertAsync(manager, "OkrObjective", Goal("MGR-A", "Grow revenue", "Company")),
            "only HR-Talent sets company-level goals");
        var company = await docs.InsertAsync(hr, "OkrObjective", Goal("MGR-A", "Grow revenue", "Company"));
        var dept = await docs.InsertAsync(manager, "OkrObjective", Goal("MGR-A", "Ship v2", "Department", company.Name));

        var options = await docs.SearchLinkAsync(staff, "OkrObjective", null);
        Assert.True(options.Any(o => o.Name == dept.Name), "staff can find the manager's goal to align to");
        var mine = await docs.InsertAsync(staff, "OkrObjective", Goal("STAFF-A", "Build the checkout", "Individual", dept.Name));
        Assert.Equal(dept.Name, mine.Data.Str("parent_objective"), "aligned to the manager's goal");
        await Assert.Throws<PermissionDeniedException>(() => docs.UpdateAsync(staff, "OkrObjective", dept.Name, dept.Version,
            new Dictionary<string, object?> { ["title"] = "Edited by staff" }), "reading a goal does not allow editing it");

        var fresh = await docs.GetAsync(hr, "OkrObjective", company.Name);
        await Assert.Throws<ValidationFailedException>(() => docs.UpdateAsync(hr, "OkrObjective", fresh.Name, fresh.Version,
            new Dictionary<string, object?> { ["parent_objective"] = mine.Name }), "company → staff → department → company is a loop");

        var report = await new Apps.Erp.Reports.GoalCheckInReport().RunAsync(manager, new Dictionary<string, string?>(), docs, default);
        Assert.True(report.Rows.All(r => (string?)r["employee"] is "MGR-A" or "STAFF-A"), "a manager's report shows only themselves and their team");
    }

    [Test]
    public async Task One_on_one_keeps_its_manager_after_a_reorganisation()
    {
        await using var sp = BuildEssRequestProvider();
        var (docs, _, _) = await MatrixSetupAsync(sp);
        var sys = UserContext.System("acme");
        var hr = MatrixUser("HR-USER", ErpRoles.HrTalent, "Employee");
        var when = new DateTimeOffset(2026, 2, 10, 2, 0, 0, TimeSpan.Zero).ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);

        var meeting = await docs.InsertAsync(MatrixUser("STAFF-A"), "OneOnOne", new Dictionary<string, object?> { ["employee"] = "STAFF-A", ["meeting_at"] = when });
        Assert.Equal("MGR-A", meeting.Data.Str("manager"), "manager at the time");

        var emp = await docs.GetAsync(sys, "Employee", "STAFF-A");
        await docs.UpdateAsync(sys, "Employee", emp.Name, emp.Version, new Dictionary<string, object?> { ["reports_to"] = "PM-B" });

        var corrected = await docs.UpdateAsync(hr, "OneOnOne", meeting.Name, meeting.Version, new Dictionary<string, object?> { ["agenda"] = "Fixed" });
        Assert.Equal("MGR-A", corrected.Data.Str("manager"), "saving later does not move the check-in to the new manager");
    }
}
