using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Approver routing and schedule optimizer, end to end through DocumentService: the approver chosen by the hook
/// survives the save pipeline, pending leave does not count as absence, the same person is not asked twice,
/// and the optimizer run/apply respects unit scope, earlier days, unchanged rows, paid months and late leave.
/// </summary>
sealed partial class Tests
{
    private static readonly DateOnly MatrixToday = DateOnly.FromDateTime(DateTime.Now.Date);

    private static async Task<(DocumentService Docs, IDocumentStore Store, Masters M)> MatrixSetupAsync(ServiceProvider provider)
    {
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "IDR", ["symbol"] = "Rp" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?> { ["company_code"] = "ACME", ["company_name"] = "Acme Corp", ["base_currency"] = "IDR", ["country"] = "ID" });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-STD", ["job_title"] = "Standard Staff" });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = "DEPT-A", ["org_unit_name"] = "Dept A", ["company"] = "ACME", ["unit_type"] = "Department", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });
        foreach (var (id, boss) in new[] { ("SPONSOR", (string?)null), ("MGR-A", "SPONSOR"), ("PLT-A", "SPONSOR"), ("PM-B", "SPONSOR"), ("STAFF-A", "MGR-A"), ("STAFF-B", "PM-B") })
        {
            var data = new Dictionary<string, object?>
            {
                ["employee_number"] = id, ["full_name"] = id, ["gender"] = "Male", ["date_of_birth"] = "1990-01-01", ["company"] = "ACME",
                ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active",
            };
            if (boss is not null) { data["reports_to"] = boss; data["leave_approver"] = boss; data["expense_approver"] = boss; }
            await docs.InsertAsync(sys, "Employee", data);
        }
        await docs.InsertAsync(sys, "Project", new Dictionary<string, object?>
        {
            ["project_code"] = "PRJ-X", ["project_name"] = "Project X", ["company"] = "ACME", ["status"] = "In Progress",
            ["project_manager"] = "PM-B", ["sponsor"] = "SPONSOR",
        });
        // PLT-A acts for MGR-A for the whole year.
        await docs.InsertAsync(sys, "EmployeeAssignment", new Dictionary<string, object?>
        {
            ["employee"] = "PLT-A", ["assignment_type"] = "Acting", ["status"] = "Active", ["reporting_line"] = "Solid",
            ["organization_unit"] = "DEPT-A", ["acting_for"] = "MGR-A", ["has_approval_authority"] = true,
            ["start_date"] = $"{MatrixToday.Year - 1}-01-01", ["end_date"] = $"{MatrixToday.Year + 1}-12-31",
        });
        await docs.InsertAsync(sys, "LeaveType", new Dictionary<string, object?> { ["leave_type_name"] = "Annual", ["is_annual_quota"] = false, ["is_paid"] = true, ["max_consecutive_days"] = 30 });
        return (docs, store, new Masters(store, "acme"));
    }

    private static UserContext MatrixUser(string employee, params string[] roles) =>
        new($"u-{employee}", employee, "acme", RoleGroups.Expand(roles.Length > 0 ? roles : ["Employee"]), new Dictionary<string, string> { ["employee"] = employee });

    private static async Task<Document> LeaveForAsync(DocumentService docs, string employee, DateOnly from, DateOnly to, bool approve)
    {
        var hr = MatrixUser("HR-USER", "HR", "Employee");
        var leave = await docs.InsertAsync(hr, "LeaveApplication", new Dictionary<string, object?>
        {
            ["employee"] = employee, ["leave_type"] = "Annual", ["from_date"] = D.Iso(from), ["to_date"] = D.Iso(to), ["reason"] = "Away",
        });
        leave = await docs.RunActionAsync(hr, "LeaveApplication", leave.Name, "Submit for approval", leave.Version, null);
        if (approve) leave = await docs.RunActionAsync(hr, "LeaveApplication", leave.Name, "Approve (HR)", leave.Version, "OK");
        return leave;
    }

    [Test]
    public async Task Approval_acting_substitute_survives_save_and_submit()
    {
        await using var provider = BuildErpProvider();
        var (docs, store, _) = await MatrixSetupAsync(provider);
        await LeaveForAsync(docs, "MGR-A", MatrixToday.AddDays(-1), MatrixToday.AddDays(5), approve: true);

        var staff = MatrixUser("STAFF-A");
        // The requester tries to pick their own approver: the hook decides, not the payload.
        var ot = await docs.InsertAsync(staff, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["overtime_date"] = D.Iso(MatrixToday), ["hours"] = 2m, ["day_type"] = "Working day",
            ["reason"] = "Release", ["approver"] = "STAFF-A",
        });
        Assert.Equal("PLT-A", ot.Data.Str("approver"), "MGR-A is on approved leave today: Acting (Plt.) PLT-A is the approver when the draft is saved");

        ot = await docs.RunActionAsync(staff, "OvertimeRequest", ot.Name, "Submit for approval", ot.Version, null);
        Assert.Equal("PLT-A", ot.Data.Str("approver"), "still PLT-A after submit (the save pipeline no longer copies the employee record over it)");
        ot = await docs.RunActionAsync(MatrixUser("PLT-A"), "OvertimeRequest", ot.Name, "Approve", ot.Version, "Approved as Plt.");
        Assert.Equal("Approved", ot.WorkflowState, "the Acting holder can approve right away, without waiting for the daily job");

        var pr = await docs.InsertAsync(staff, "PurchaseRequest", new Dictionary<string, object?>
        {
            ["requested_by"] = "STAFF-A", ["required_by"] = D.Iso(MatrixToday.AddDays(30)),
            ["company"] = "ACME", ["purpose"] = "Laptops", ["items"] = new List<Dictionary<string, object?>> { new() { ["item_name"] = "Laptop", ["qty"] = 1m, ["estimated_rate"] = 1_000_000m } },
        });
        Assert.Equal("PLT-A", pr.Data.Str("approver"), "PurchaseRequest: availability is checked for today, not for the required-by date");
    }

    [Test]
    public async Task Approval_pending_leave_does_not_hand_over_approvals()
    {
        await using var provider = BuildErpProvider();
        var (docs, store, m) = await MatrixSetupAsync(provider);
        await LeaveForAsync(docs, "MGR-A", MatrixToday, MatrixToday.AddDays(3), approve: false); // still Pending Approval

        Assert.True(!await Hr.IsOnApprovedLeaveAsync(m, "MGR-A", MatrixToday, CancellationToken.None), "a leave request waiting for approval is not leave yet");
        var staffDoc = (await m.GetAsync("Employee", "STAFF-A", CancellationToken.None))!;
        var r = await Hr.ResolveApproversAsync(m, staffDoc, null, MatrixToday, CancellationToken.None, "leave_approver");
        Assert.Equal("MGR-A", r.LineApprover, "MGR-A keeps approving while the leave is only requested");
        Assert.True(!r.LineDelegated, "not delegated");

        var staff = MatrixUser("STAFF-A");
        var ot = await docs.InsertAsync(staff, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-A", ["overtime_date"] = D.Iso(MatrixToday), ["hours"] = 1m, ["day_type"] = "Working day", ["reason"] = "Fix",
        });
        ot = await docs.RunActionAsync(staff, "OvertimeRequest", ot.Name, "Submit for approval", ot.Version, null);
        await new ApprovalDelegationRefreshJob().ExecuteAsync(new Framework.Jobs.ScheduledJobContext
        {
            TenantId = "acme", SystemUser = UserContext.System("acme"), Docs = docs, Services = provider,
            ScheduledTime = new DateTimeOffset(MatrixToday.ToDateTime(new TimeOnly(3, 0)), TimeSpan.Zero),
        }, CancellationToken.None);
        Assert.Equal("MGR-A", (await store.GetAsync("acme", "OvertimeRequest", ot.Name, CancellationToken.None))!.Data.Str("approver"),
            "the daily job leaves documents with MGR-A");
    }

    [Test]
    public async Task Approval_project_stage_uses_sponsor_and_skips_duplicate_approver()
    {
        await using var provider = BuildErpProvider();
        var (docs, _, m) = await MatrixSetupAsync(provider);

        // STAFF-B reports to PM-B, who also manages PRJ-X: one approval is enough.
        var staffB = MatrixUser("STAFF-B");
        var ot = await docs.InsertAsync(staffB, "OvertimeRequest", new Dictionary<string, object?>
        {
            ["employee"] = "STAFF-B", ["overtime_date"] = D.Iso(MatrixToday), ["hours"] = 2m, ["day_type"] = "Working day",
            ["project"] = "PRJ-X", ["reason"] = "Go-live",
        });
        Assert.Equal("PM-B", ot.Data.Str("approver"), "line approver PM-B");
        Assert.Equal(null, ot.Data.Str("project_approver"), "no separate project stage for the same person");
        ot = await docs.RunActionAsync(staffB, "OvertimeRequest", ot.Name, "Submit for approval", ot.Version, null);
        Assert.Equal("Pending Approval", ot.WorkflowState, "goes straight to the line approver");

        // MGR-A works on PRJ-X but is managed by SPONSOR: the PM approves the project stage.
        var mgr = (await m.GetAsync("Employee", "MGR-A", CancellationToken.None))!;
        var r = await Hr.ResolveApproversAsync(m, mgr, "PRJ-X", MatrixToday, CancellationToken.None, "leave_approver");
        Assert.Equal("SPONSOR", r.LineApprover, "line approver of MGR-A");
        Assert.Equal("PM-B", r.ProjectApprover, "project manager approves the project stage");

        // PM-B on their own project: not themselves; the sponsor is next but is also their line manager.
        var pm = (await m.GetAsync("Employee", "PM-B", CancellationToken.None))!;
        var rPm = await Hr.ResolveApproversAsync(m, pm, "PRJ-X", MatrixToday, CancellationToken.None, "leave_approver");
        Assert.Equal(null, rPm.ProjectApprover, "sponsor = line manager: no duplicate stage");

        // PM-C manages PRJ-Y and reports to MGR-A: on their own project the sponsor (a different person) takes the stage.
        var sys = UserContext.System("acme");
        await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?>
        {
            ["employee_number"] = "PM-C", ["full_name"] = "PM-C", ["gender"] = "Male", ["date_of_birth"] = "1990-01-01", ["company"] = "ACME",
            ["organization_unit"] = "DEPT-A", ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active",
            ["reports_to"] = "MGR-A", ["leave_approver"] = "MGR-A", ["expense_approver"] = "MGR-A",
        });
        await docs.InsertAsync(sys, "Project", new Dictionary<string, object?>
        {
            ["project_code"] = "PRJ-Y", ["project_name"] = "Project Y", ["company"] = "ACME", ["status"] = "In Progress",
            ["project_manager"] = "PM-C", ["sponsor"] = "SPONSOR",
        });
        var pmC = (await m.GetAsync("Employee", "PM-C", CancellationToken.None))!;
        var rPmC = await Hr.ResolveApproversAsync(m, pmC, "PRJ-Y", MatrixToday, CancellationToken.None, "leave_approver");
        Assert.Equal("MGR-A", rPmC.LineApprover, "line approver MGR-A");
        Assert.Equal("SPONSOR", rPmC.ProjectApprover, "PM-C cannot approve their own project request: the sponsor does");
    }

    [Test]
    public async Task Approval_delegation_job_moves_pending_documents_once_leave_is_approved()
    {
        await using var provider = BuildErpProvider();
        var (docs, store, _) = await MatrixSetupAsync(provider);
        var staff = MatrixUser("STAFF-A");
        var pending = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var ot = await docs.InsertAsync(staff, "OvertimeRequest", new Dictionary<string, object?>
            {
                ["employee"] = "STAFF-A", ["overtime_date"] = D.Iso(MatrixToday.AddDays(-i)), ["hours"] = 1m, ["day_type"] = "Working day", ["reason"] = $"Task {i}",
            });
            ot = await docs.RunActionAsync(staff, "OvertimeRequest", ot.Name, "Submit for approval", ot.Version, null);
            pending.Add(ot.Name);
        }
        Assert.True(pending.All(n => store.GetAsync("acme", "OvertimeRequest", n, CancellationToken.None).Result!.Data.Str("approver") == "MGR-A"), "submitted to MGR-A");

        await LeaveForAsync(docs, "MGR-A", MatrixToday, MatrixToday.AddDays(2), approve: true);
        await new ApprovalDelegationRefreshJob().ExecuteAsync(new Framework.Jobs.ScheduledJobContext
        {
            TenantId = "acme", SystemUser = UserContext.System("acme"), Docs = docs, Services = provider,
            ScheduledTime = new DateTimeOffset(MatrixToday.ToDateTime(new TimeOnly(3, 0)), TimeSpan.Zero),
        }, CancellationToken.None);

        foreach (var n in pending)
            Assert.Equal("PLT-A", (await store.GetAsync("acme", "OvertimeRequest", n, CancellationToken.None))!.Data.Str("approver"), $"{n} moved to PLT-A");
    }

    [Test]
    public async Task WsmOptimizer_run_and_apply_respect_scope_history_unchanged_rows_paid_months_and_late_leave()
    {
        await using var provider = BuildErpProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var store = provider.GetRequiredService<IDocumentStore>();
        var sys = UserContext.System("acme");
        var m = new Masters(store, "acme");

        await docs.InsertAsync(sys, "Currency", new Dictionary<string, object?> { ["currency_code"] = "IDR", ["currency_name"] = "IDR", ["symbol"] = "Rp" });
        await docs.InsertAsync(sys, "Company", new Dictionary<string, object?> { ["company_code"] = "ACME", ["company_name"] = "Acme Corp", ["base_currency"] = "IDR", ["country"] = "ID" });
        await docs.InsertAsync(sys, "JobProfile", new Dictionary<string, object?> { ["job_code"] = "JOB-STD", ["job_title"] = "Standard Staff" });
        await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = "OPS", ["org_unit_name"] = "Ops", ["company"] = "ACME", ["unit_type"] = "Department", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });
        foreach (var u in new[] { "OPS-A", "OPS-B" })
            await docs.InsertAsync(sys, "OrganizationUnit", new Dictionary<string, object?> { ["org_unit_code"] = u, ["org_unit_name"] = u, ["company"] = "ACME", ["unit_type"] = "Team", ["parent_org_unit"] = "OPS", ["staffing_model"] = "Job", ["status"] = "Active", ["effective_from"] = "2026-01-01" });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?> { ["shift_name"] = "Morning 06-14", ["start_time"] = "06:00:00", ["end_time"] = "14:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 7, ["working_days_per_week"] = "6", ["late_grace_minutes"] = 10, ["is_night_shift"] = false });
        await docs.InsertAsync(sys, "ShiftType", new Dictionary<string, object?> { ["shift_name"] = "Office 08-17", ["start_time"] = "08:00:00", ["end_time"] = "17:00:00", ["break_minutes"] = 60, ["working_hours_per_day"] = 8, ["working_days_per_week"] = "5", ["late_grace_minutes"] = 10, ["is_night_shift"] = false });
        foreach (var (id, unit) in new[] { ("A1", "OPS-A"), ("A2", "OPS-A"), ("A3", "OPS-A"), ("B1", "OPS-B"), ("B2", "OPS-B") })
            await docs.InsertAsync(sys, "Employee", new Dictionary<string, object?> { ["employee_number"] = id, ["full_name"] = id, ["gender"] = "Male", ["date_of_birth"] = "1990-01-01", ["company"] = "ACME", ["organization_unit"] = unit, ["job_profile"] = "JOB-STD", ["hire_date"] = "2026-01-01", ["status"] = "Active" });
        // OPS-A needs 1 + 1 (two requirements, same scope), OPS-B needs 1 of its own.
        foreach (var unit in new[] { "OPS-A", "OPS-A", "OPS-B" })
            await docs.InsertAsync(sys, "ShiftRequirement", new Dictionary<string, object?> { ["company"] = "ACME", ["organization_unit"] = unit, ["shift_type"] = "Morning 06-14", ["day_scope"] = "All days", ["min_headcount"] = 1, ["target_headcount"] = 1, ["effective_from"] = "2026-01-01", ["is_active"] = true });

        var from = new DateOnly(2027, 3, 1);
        // A1 worked the 6 days before the period.
        for (int i = 1; i <= 6; i++)
            await docs.InsertAsync(sys, "EmployeeDailySchedule", new Dictionary<string, object?>
            {
                ["employee"] = "A1", ["company"] = "ACME", ["organization_unit"] = "OPS-A", ["work_date"] = D.Iso(from.AddDays(-i)),
                ["schedule_type"] = "WORK", ["day_category"] = "Scheduled Workday", ["shift_type"] = "Morning 06-14", ["source_type"] = "Manual",
            });
        // B2 has a locked office day on day 2 (not a candidate shift).
        await docs.InsertAsync(sys, "EmployeeDailySchedule", new Dictionary<string, object?>
        {
            ["employee"] = "B2", ["company"] = "ACME", ["organization_unit"] = "OPS-B", ["work_date"] = D.Iso(from.AddDays(1)),
            ["schedule_type"] = "WORK", ["day_category"] = "Scheduled Workday", ["shift_type"] = "Office 08-17", ["source_type"] = "Manual", ["is_locked"] = true,
        });

        var opt = await docs.InsertAsync(sys, "ScheduleOptimization", new Dictionary<string, object?>
        {
            ["company"] = "ACME", ["organization_unit"] = "OPS", ["from_date"] = D.Iso(from), ["to_date"] = D.Iso(from.AddDays(3)),
            ["candidate_shift_types"] = "Morning 06-14", ["time_limit_seconds"] = 10,
        });
        var res = await WsmOptimizationService.RunAsync(store, "acme", opt, CancellationToken.None);
        Assert.True(res.IsSuccess, $"solved: {res.Log}");
        Assert.Equal(0, res.ShortageSlots, "both teams covered");
        Assert.Equal(0, res.OverstaffSlots, "nobody above target");

        bool Works(string e, int day) => res.Lines.Single(l => l.EmployeeId == e && l.WorkDate == from.AddDays(day)).ScheduleType == "WORK";
        for (int d = 0; d < 4; d++)
        {
            Assert.Equal(2, new[] { "A1", "A2", "A3" }.Count(e => Works(e, d)), $"OPS-A staffed 1 + 1 on day {d}");
            Assert.Equal(1, new[] { "B1", "B2" }.Count(e => Works(e, d) && res.Lines.Single(l => l.EmployeeId == e && l.WorkDate == from.AddDays(d)).ShiftCode == "Morning 06-14"),
                $"OPS-B staffed by its own people on day {d}");
        }
        Assert.True(!Works("A1", 0), "A1 rests on the first day after 6 days in a row before the period");
        var locked = res.Lines.Single(l => l.EmployeeId == "B2" && l.WorkDate == from.AddDays(1));
        Assert.True(locked is { ScheduleType: "WORK", ShiftCode: "Office 08-17", IsPinned: true }, "locked office day kept as work in the proposal");

        // Apply once, then simulate events after the proposal: a payslip for March is submitted for A2,
        // and leave is approved for A3 on day 3. Re-run and apply again.
        var applied = (await store.GetAsync("acme", "ScheduleOptimization", opt.Name, CancellationToken.None))!;
        var (n1, _, _) = await WsmOptimizationService.ApplyAsync(store, "acme", applied, CancellationToken.None);
        Assert.True(n1 > 0, "first apply writes rows");
        var firstRows = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("source_type", "=", "Optimizer")], CancellationToken.None, take: 100))
            .ToDictionary(r => r.Name, r => r.Version);

        var now = DateTimeOffset.UtcNow;
        var uow = new UnitOfWork();
        uow.Upserts.Add((new Document { TenantId = "acme", DocType = "Payslip", Name = "PS-TEST-A2", Owner = "system", ModifiedBy = "system", Created = now, Modified = now, Version = 1, SchemaVersion = 1, DocStatus = DocStatus.Submitted,
            Data = new() { ["employee"] = "A2", ["period"] = $"{from:yyyy-MM}", ["company"] = "ACME" } }, null));
        uow.Upserts.Add((new Document { TenantId = "acme", DocType = "LeaveApplication", Name = "LA-TEST-A3", Owner = "system", ModifiedBy = "system", Created = now, Modified = now, Version = 1, SchemaVersion = 1, DocStatus = DocStatus.Submitted, WorkflowState = "Approved",
            Data = new() { ["employee"] = "A3", ["leave_type"] = "Annual", ["from_date"] = D.Iso(from.AddDays(3)), ["to_date"] = D.Iso(from.AddDays(3)) } }, null));
        await store.CommitAsync(uow, CancellationToken.None);

        var opt2 = await docs.InsertAsync(sys, "ScheduleOptimization", new Dictionary<string, object?>
        {
            ["company"] = "ACME", ["organization_unit"] = "OPS", ["from_date"] = D.Iso(from), ["to_date"] = D.Iso(from.AddDays(3)),
            ["candidate_shift_types"] = "Morning 06-14", ["time_limit_seconds"] = 10, ["w_stability"] = 50,
        });
        await WsmOptimizationService.RunAsync(store, "acme", opt2, CancellationToken.None);
        opt2 = (await store.GetAsync("acme", "ScheduleOptimization", opt2.Name, CancellationToken.None))!;
        // Make the proposal stale, as if it had been made before A3's leave was approved: the line for that day
        // asks for a change (work if the day is free today, rest if it is a working day).
        var a3Before = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", "A3"), new FilterClause("work_date", "=", D.Iso(from.AddDays(3)))], CancellationToken.None)).SingleOrDefault();
        var lines = opt2.Data.Rows("lines").Select(r => new Dictionary<string, object?>(r)).ToList();
        var a3Line = lines.Single(r => r.Str("employee") == "A3" && r.Str("work_date") == D.Iso(from.AddDays(3)));
        var a3WasWork = a3Before?.Data.Str("schedule_type") == "WORK";
        a3Line["schedule_type"] = a3WasWork ? "REST" : "WORK"; a3Line["shift_type"] = a3WasWork ? null : "Morning 06-14"; a3Line["is_pinned"] = false;
        var stale = opt2.Clone(); stale.Data["lines"] = lines;

        var before = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", "A2")], CancellationToken.None, take: 100))
            .ToDictionary(r => r.Data.Str("work_date")!, r => (r.Version, r.Data.Str("shift_type")));
        var (n2, skipped2, _) = await WsmOptimizationService.ApplyAsync(store, "acme", stale, CancellationToken.None);

        var after = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", "A2")], CancellationToken.None, take: 100))
            .ToDictionary(r => r.Data.Str("work_date")!, r => (r.Version, r.Data.Str("shift_type")));
        Assert.True(before.All(kv => after[kv.Key] == kv.Value), "A2's March rows are untouched: the month has a submitted payslip");
        var a3After = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("employee", "=", "A3"), new FilterClause("work_date", "=", D.Iso(from.AddDays(3)))], CancellationToken.None)).SingleOrDefault();
        Assert.True(a3After?.Version == a3Before?.Version && a3After?.Data.Str("schedule_type") == a3Before?.Data.Str("schedule_type"),
            "A3's day is not rewritten: leave was approved for it after the proposal");
        var unchangedRows = (await m.QueryAsync("EmployeeDailySchedule", [new FilterClause("source_reference", "=", opt.Name)], CancellationToken.None, take: 100))
            .Where(r => r.Data.Str("employee") != "A2").ToList();
        Assert.True(unchangedRows.Count > 0 && unchangedRows.All(r => firstRows.TryGetValue(r.Name, out var v) && v == r.Version),
            "rows that were already as proposed are not rewritten (version and source of the first run kept)");
        Assert.True(skipped2 >= 4 + 1 + 1, $"protected slots skipped: 4 paid days of A2, A3's leave day, B2's locked day (got {skipped2})");
    }
}
