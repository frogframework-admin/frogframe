using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Apps.Erp.Hooks;

internal static class Hr
{
    public const string Left = "Left";

    public static readonly (string Name, string Code, long Order, string? ParentType, string Description)[] DefaultUnitTypes =
    [
        ("Board of Directors", "BOD", 1, null, "Executive board level"),
        ("Directorate", "DIR", 2, "Board of Directors", "Directorate level"),
        ("Division", "DIV", 3, "Directorate", "Division level"),
        ("Department", "DEPT", 4, null, "Department level"),
        ("Sub-department", "SUBDEPT", 5, "Department", "Sub-department level"),
        ("Section", "SEC", 6, "Department", "Section level"),
        ("Team", "TEAM", 7, null, "Operational team or squad"),
        ("Branch", "BRANCH", 3, null, "Regional branch or office"),
    ];

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    /// <summary>Ensures the 8 default system OrganizationUnitType master documents exist in the tenant store.</summary>
    public static async Task EnsureDefaultUnitTypesAsync(IDocumentStore store, string tenant, CancellationToken ct)
    {
        var uow = new UnitOfWork();
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, code, order, parentType, desc) in DefaultUnitTypes)
        {
            if (await store.GetAsync(tenant, "OrganizationUnitType", name, ct) is not null) continue;
            uow.Upserts.Add((new Document
            {
                TenantId = tenant,
                DocType = "OrganizationUnitType",
                Name = name,
                Owner = "system",
                ModifiedBy = "system",
                Created = now,
                Modified = now,
                Version = 1,
                SchemaVersion = 1,
                DocStatus = DocStatus.Draft,
                Data = new Dictionary<string, object?>
                {
                    ["type_code"] = code,
                    ["type_name"] = name,
                    ["hierarchy_order"] = order,
                    ["parent_type"] = parentType,
                    ["is_active"] = true,
                    ["is_system"] = true,
                    ["description"] = desc,
                },
            }, null));
        }
        if (uow.Upserts.Count > 0) await store.CommitAsync(uow, ct);
    }

    /// <summary>Checks whether an OrganizationUnit version row is effective on the given date.</summary>
    public static bool IsUnitValidOn(Document unit, DateOnly date)
    {
        var status = unit.Data.Str("status") ?? (unit.Data.Bool("is_active") ? "Active" : "Closed");
        if (status is "Draft" or "Planned") return false;
        var from = unit.Data.Date("effective_from");
        var to = unit.Data.Date("effective_to");
        if (status == "Closed" && to is null) return false;
        if (from is not null && date < from.Value) return false;
        if (to is not null && date > to.Value) return false;
        return true;
    }

    /// <summary>Resolves the OrganizationUnit version row valid on <paramref name="asOfDate"/> (by direct name or canonical base_unit_code).</summary>
    public static async Task<Document?> GetOrgUnitAsOfAsync(Masters m, string? unitNameOrCode, DateOnly? asOfDate, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(unitNameOrCode)) return null;
        var direct = await m.GetAsync("OrganizationUnit", unitNameOrCode, ct);
        if (asOfDate is not { } date)
            return direct;

        var baseCode = direct?.Data.Str("base_unit_code") ?? direct?.Data.Str("org_unit_code") ?? unitNameOrCode;
        var versions = await m.QueryAsync("OrganizationUnit", [new FilterClause("base_unit_code", "=", baseCode)], ct, "effective_from", "desc", 50);
        var match = versions.FirstOrDefault(u => IsUnitValidOn(u, date));
        if (match is not null) return match;
        if (direct is not null && IsUnitValidOn(direct, date)) return direct;
        return versions.Count == 0 ? direct : null;
    }

    /// <summary>Returns all descendant OrganizationUnit rows (including the parent unit itself) valid on <paramref name="asOfDate"/>.</summary>
    public static async Task<IReadOnlyList<Document>> DescendantUnitsAsOfAsync(Masters m, string? parentUnitNameOrCode, DateOnly asOfDate, CancellationToken ct)
    {
        var root = await GetOrgUnitAsOfAsync(m, parentUnitNameOrCode, asOfDate, ct);
        if (root is null) return [];
        var rootPath = root.Data.Str("materialized_path") ?? $"/{root.Data.Str("org_unit_code") ?? root.Name}/";
        var candidates = await m.QueryAsync("OrganizationUnit", [new FilterClause("materialized_path", "startswith", rootPath)], ct, take: 5000);
        return candidates
            .Where(u => (u.Data.Str("materialized_path") ?? "").StartsWith(rootPath, StringComparison.Ordinal) && IsUnitValidOn(u, asOfDate))
            .ToList();
    }

    /// <summary>Returns all active employees belonging to <paramref name="parentUnitNameOrCode"/> or any of its valid descendant units on <paramref name="asOfDate"/>.</summary>
    public static async Task<IReadOnlyList<Document>> EmployeesInSubtreeAsOfAsync(Masters m, string? parentUnitNameOrCode, DateOnly asOfDate, CancellationToken ct)
    {
        var units = await DescendantUnitsAsOfAsync(m, parentUnitNameOrCode, asOfDate, ct);
        if (units.Count == 0) return [];
        var validNames = units.Select(u => u.Name)
            .Concat(units.Select(u => u.Data.Str("base_unit_code")).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        var allEmps = await m.QueryAsync("Employee", [], ct, take: 10_000);
        var result = new List<Document>();
        foreach (var emp in allEmps.Where(e => e.Data.Str("status") != Left))
        {
            if (emp.Data.Date("hire_date") is { } hire && hire > asOfDate) continue;
            if (emp.Data.Date("separation_date") is { } sep && sep < asOfDate) continue;
            var empUnit = emp.Data.Str("organization_unit");
            var resolved = await GetOrgUnitAsOfAsync(m, empUnit, asOfDate, ct);
            if ((resolved is not null && validNames.Contains(resolved.Name)) || (empUnit is not null && validNames.Contains(empUnit)))
                result.Add(emp);
        }
        return result;
    }

    /// <summary>Active holder of a position (primary source: Employee.position; fallback: active Acting/Primary EmployeeAssignment).</summary>
    public static async Task<Document?> HolderAsync(Masters m, string? position, string? except, CancellationToken ct, DateOnly? asOfDate = null)
    {
        if (position is null) return null;
        var primary = (await m.QueryAsync("Employee", [new FilterClause("position", "=", position)], ct, take: 20))
            .FirstOrDefault(e => e.Name != except && e.Data.Str("status") != Left);
        if (primary is not null) return primary;

        var assignments = (await m.QueryAsync("EmployeeAssignment", [new FilterClause("position", "=", position), new FilterClause("status", "=", "Active")], ct, take: 20))
            .Where(a => a.Data.Str("employee") != except && a.Data.Str("assignment_type") is "Acting" or "Primary")
            .Where(a => asOfDate is not { } d || ((a.Data.Date("start_date") is not { } s || s <= d) && (a.Data.Date("end_date") is not { } e || d <= e)))
            .OrderByDescending(a => a.Data.Date("start_date"))
            .ToList();
        foreach (var a in assignments)
        {
            if (await m.GetAsync("Employee", a.Data.Str("employee"), ct) is { } emp && emp.Data.Str("status") != Left)
                return emp;
        }
        return null;
    }

    /// <summary>Supervisor resolution: holder of "reports to" position; if empty, head of the organization unit valid on <paramref name="asOfDate"/>, walking up parent units.</summary>
    public static async Task<string?> ManagerForAsync(Masters m, string self, string? position, string? orgUnit, CancellationToken ct, DateOnly? asOfDate = null)
    {
        var pos = await m.GetAsync("Position", position, ct);
        if (await HolderAsync(m, pos?.Data.Str("reports_to_position"), self, ct, asOfDate) is { } boss) return boss.Name;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var unit = await GetOrgUnitAsOfAsync(m, orgUnit, asOfDate, ct);
             unit is not null && visited.Add(unit.Name);
             unit = await GetOrgUnitAsOfAsync(m, unit.Data.Str("parent_org_unit"), asOfDate, ct))
        {
            if (await HolderAsync(m, unit.Data.Str("manager_position"), self, ct, asOfDate) is { } head) return head.Name;
        }
        return null;
    }

    public sealed record ResolvedApprovers(
        string? LineApprover,
        string? ProjectApprover,
        bool LineDelegated,
        string? OriginalLineApprover);

    /// <summary>Returns true if <paramref name="employeeId"/> has an Approved LeaveApplication covering <paramref name="asOfDate"/>.</summary>
    public static async Task<bool> IsOnApprovedLeaveAsync(Masters m, string employeeId, DateOnly asOfDate, CancellationToken ct)
    {
        // Only approved leave: a request still "Pending Approval" is also submitted (docstatus 1) but the person is not away yet.
        var leaves = await m.QueryAsync("LeaveApplication",
            [new FilterClause("employee", "=", employeeId), new FilterClause("workflow_state", "=", Approval.Approved)], ct, take: 200);
        return leaves.Any(l => Overlaps(asOfDate, asOfDate, l.Data.Date("from_date"), l.Data.Date("to_date")));
    }

    /// <summary>Finds an active Acting (Plt.) employee substituting for <paramref name="managerId"/> (or their position) on <paramref name="asOfDate"/>.</summary>
    public static async Task<string?> FindActingDelegateAsync(Masters m, string managerId, string? managerPosition, string exceptEmployee, DateOnly asOfDate, CancellationToken ct)
    {
        var activeActing = (await m.QueryAsync("EmployeeAssignment",
            [new FilterClause("assignment_type", "=", "Acting"), new FilterClause("status", "=", "Active")], ct, take: 200))
            .Where(a => a.Data.Str("employee") is { } holder && holder != exceptEmployee && holder != managerId)
            .Where(a => !a.Data.ContainsKey("has_approval_authority") || a.Data.Bool("has_approval_authority"))
            .Where(a => (a.Data.Date("start_date") is not { } s || s <= asOfDate) && (a.Data.Date("end_date") is not { } e || asOfDate <= e))
            .OrderByDescending(a => a.Data.Str("acting_for") == managerId ? 1 : 0)
            .ThenByDescending(a => a.Data.Date("start_date"))
            .ToList();

        foreach (var a in activeActing)
        {
            var matchesManager = a.Data.Str("acting_for") == managerId;
            var matchesPosition = managerPosition is not null && a.Data.Str("position") == managerPosition;
            if (!matchesManager && !matchesPosition) continue;
            var holderId = a.Data.Str("employee")!;
            if (await m.GetAsync("Employee", holderId, ct) is { } emp && emp.Data.Str("status") != Left)
                return holderId;
        }
        return null;
    }

    /// <summary>
    /// Resolves solid-line approver (with Acting/Plt. substitution if the manager is on leave or vacant)
    /// and dotted-line / project approver from EmployeeAssignment and Project. Enforces no self-approval.
    /// </summary>
    public static async Task<ResolvedApprovers> ResolveApproversAsync(
        Masters m, Document employee, string? project, DateOnly asOfDate, CancellationToken ct, string? preferredLineField = null)
    {
        var self = employee.Name;
        var origLine = (preferredLineField is not null ? employee.Data.Str(preferredLineField) : null)
            ?? employee.Data.Str("reports_to")
            ?? await ManagerForAsync(m, self, employee.Data.Str("position"), employee.Data.Str("organization_unit"), ct, asOfDate);

        if (origLine == self) origLine = null;

        string? resolvedLine = origLine;
        bool delegated = false;

        if (origLine is not null)
        {
            var bossDoc = await m.GetAsync("Employee", origLine, ct);
            var bossUnavailable = bossDoc is null || bossDoc.Data.Str("status") == Left
                || await IsOnApprovedLeaveAsync(m, origLine, asOfDate, ct);
            if (bossUnavailable)
            {
                var acting = await FindActingDelegateAsync(m, origLine, bossDoc?.Data.Str("position"), self, asOfDate, ct);
                if (acting is not null)
                {
                    resolvedLine = acting;
                    delegated = true;
                }
            }
        }
        else if (employee.Data.Str("position") is { } myPos)
        {
            var posDoc = await m.GetAsync("Position", myPos, ct);
            if (posDoc?.Data.Str("reports_to_position") is { } bossPos)
            {
                var acting = await FindActingDelegateAsync(m, "", bossPos, self, asOfDate, ct);
                if (acting is not null) { resolvedLine = acting; delegated = true; }
            }
        }

        string? projectApprover = null;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var dotted = (await m.QueryAsync("EmployeeAssignment",
                [new FilterClause("employee", "=", self), new FilterClause("status", "=", "Active")], ct, take: 100))
                .Where(a => a.Data.Str("reporting_line") == "Dotted" && a.Data.Str("project") == project)
                .Where(a => (a.Data.Date("start_date") is not { } s || s <= asOfDate) && (a.Data.Date("end_date") is not { } e || asOfDate <= e))
                .OrderByDescending(a => a.Data.Date("start_date"))
                .FirstOrDefault();

            projectApprover = dotted?.Data.Str("dotted_line_manager");
            var projDoc = await m.GetAsync("Project", project, ct);
            projectApprover ??= projDoc?.Data.Str("project_manager");

            if (projectApprover == self)
            {
                var sponsor = projDoc?.Data.Str("sponsor");
                projectApprover = sponsor != self ? sponsor : null;
            }

            if (projectApprover is not null)
            {
                var pmDoc = await m.GetAsync("Employee", projectApprover, ct);
                if (pmDoc is null || pmDoc.Data.Str("status") == Left || await IsOnApprovedLeaveAsync(m, projectApprover, asOfDate, ct))
                {
                    var actingPm = await FindActingDelegateAsync(m, projectApprover, pmDoc?.Data.Str("position"), self, asOfDate, ct);
                    if (actingPm is not null) projectApprover = actingPm;
                }
            }
        }

        // The same person on both stages would approve twice: the line approval covers it.
        if (projectApprover is not null && projectApprover == resolvedLine) projectApprover = null;

        return new ResolvedApprovers(resolvedLine, projectApprover, delegated, origLine);
    }

    public static bool Overlaps(DateOnly? aFrom, DateOnly? aTo, DateOnly? bFrom, DateOnly? bTo) =>
        (aFrom ?? DateOnly.MinValue) <= (bTo ?? DateOnly.MaxValue) && (bFrom ?? DateOnly.MinValue) <= (aTo ?? DateOnly.MaxValue);

    /// <summary>Activates a Planned OrganizationUnit on <paramref name="effectiveDate"/>, closing its predecessor version and re-pointing active Positions.</summary>
    public static async Task<int> ActivatePlannedUnitAsync(SystemDocs sys, Masters m, Document planned, DateOnly effectiveDate, CancellationToken ct)
    {
        var baseCode = planned.Data.Str("base_unit_code") ?? planned.Data.Str("org_unit_code") ?? planned.Name;
        var prevName = planned.Data.Str("previous_version");
        var candidates = await m.QueryAsync("OrganizationUnit", [new FilterClause("base_unit_code", "=", baseCode)], ct, take: 100);
        var closedUnits = new HashSet<string>(StringComparer.Ordinal);

        foreach (var old in candidates.Where(u => u.Name != planned.Name && u.Data.Str("status") == "Active"))
        {
            if (Overlaps(effectiveDate, planned.Data.Date("effective_to"), old.Data.Date("effective_from"), old.Data.Date("effective_to")))
            {
                var closeDate = effectiveDate.AddDays(-1);
                var oldFrom = old.Data.Date("effective_from");
                if (oldFrom is not null && closeDate < oldFrom.Value) closeDate = oldFrom.Value;
                await sys.UpdateAsync("OrganizationUnit", old.Name, new Dictionary<string, object?>
                {
                    ["effective_to"] = D.Iso(closeDate),
                }, ct);
                closedUnits.Add(old.Name);
            }
        }
        if (prevName is not null && prevName != planned.Name && !closedUnits.Contains(prevName))
            closedUnits.Add(prevName);

        await sys.UpdateAsync("OrganizationUnit", planned.Name, new Dictionary<string, object?>
        {
            ["status"] = "Active",
            ["is_active"] = true,
            ["effective_from"] = D.Iso(effectiveDate),
        }, ct);

        int movedPositions = 0;
        foreach (var oldUnitName in closedUnits)
        {
            var positions = await m.QueryAsync("Position", [new FilterClause("organization_unit", "=", oldUnitName)], ct, take: 1000);
            foreach (var pos in positions.Where(p => p.Data.Str("status") != "Closed"))
            {
                await sys.UpdateAsync("Position", pos.Name, new Dictionary<string, object?>
                {
                    ["organization_unit"] = planned.Name,
                }, ct);
                movedPositions++;
                if (pos.Data.Str("current_employee") is { } holder)
                    await sys.TouchAsync("Employee", holder, ct);
            }
        }
        return movedPositions;
    }
}

/// <summary>Organization unit type: dynamic hierarchy level definition; system-seeded types cannot be deleted.</summary>
public sealed class OrganizationUnitTypeHook(IDocumentStore store)
    : IBeforeValidate<OrganizationUnitType>, IBeforeDelete<OrganizationUnitType>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var code = d.Str("type_code")?.Trim().ToUpperInvariant();
        if (code is not null) d["type_code"] = code;
        var name = d.Str("type_name") ?? c.Doc.Name;

        if (d.Str("parent_type") == name)
            errors.Add("parent_type", "An organization unit type cannot be its own parent type");

        var existing = await m.QueryAsync("OrganizationUnitType", [new FilterClause("type_code", "=", code)], ct, take: 10);
        if (existing.FirstOrDefault(x => x.Name != name && x.Name != c.Doc.Name) is { } dup)
            errors.Add("type_code", $"Type code {code} is already used by {dup.Name}");

        errors.ThrowIfAny();
    }

    public Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.Bool("is_system"))
            throw Errors.One("name", "System organization unit types cannot be deleted");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Employee. Position model: unit, job profile, grade, location, and cost center are derived from the position; one active holder per position.
/// Supervisor is derived from position/unit hierarchy; default leave &amp; expense approvers = supervisor.
/// </summary>
public sealed class EmployeeHook(IDocumentStore store) : IBeforeValidate<Employee>, IAfterSave<Employee>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var me = d.Str("employee_number") ?? c.Doc.Name;
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var prev = c.Previous?.Data;

        if (d.Str("status") == Hr.Left)
        {
            if (d.Str("separation_date") is null)
            {
                var sep = (await m.QueryAsync("EmployeeSeparation", [new FilterClause("employee", "=", me)], ct, take: 10))
                    .FirstOrDefault(s => s.DocStatus == DocStatus.Submitted);
                if (sep is null && !c.User.IsAdmin) errors.Add("status", "The Left status is applied through Employee Separation");
                d["separation_date"] = sep?.Data.Str("last_working_date");
            }
            d["position"] = null;
        }
        else if (prev?.Str("status") == Hr.Left) d["separation_date"] = null;

        var positionChanged = prev?.Str("position") != d.Str("position");
        if (d.Str("position") is { } positionName)
        {
            var pos = await m.RequireAsync("Position", positionName, "position", "Position", ct);
            if (pos.Data.Str("status") is "Closed" or "Frozen") errors.Add("position", $"Position {pos.Name} is {pos.Data.Str("status")}");
            if (await Hr.HolderAsync(m, pos.Name, me, ct) is { } holder && holder.Data.Str("position") == pos.Name)
                errors.Add("position", $"Position {pos.Name} is already filled by {holder.Name}");
            d["organization_unit"] = pos.Data.Str("organization_unit");
            d["job_profile"] = pos.Data.Str("job_profile");
            if (positionChanged) { d["grade"] = pos.Data.Str("grade"); d["cost_center"] = pos.Data.Str("cost_center"); d["location"] = pos.Data.Str("location"); }
        }
        d.SetIfEmpty("grade", await m.StrAsync("JobProfile", d.Str("job_profile"), "default_grade", ct));
        var asOf = d.Date("last_job_change_date") ?? d.Date("hire_date");
        var unitDoc = await Hr.GetOrgUnitAsOfAsync(m, d.Str("organization_unit"), asOf, ct);
        d.SetIfEmpty("cost_center", unitDoc?.Data.Str("cost_center") ?? await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "cost_center", ct) ?? company.Default("default_cost_center"));
        d.SetIfEmpty("location", unitDoc?.Data.Str("location") ?? await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "location", ct));

        var oldBoss = prev?.Str("reports_to");
        if (d.Str("reports_to") is null || (positionChanged && d.Str("reports_to") == oldBoss))
            d["reports_to"] = await Hr.ManagerForAsync(m, me, d.Str("position"), d.Str("organization_unit"), ct, asOf);
        foreach (var approver in new[] { "leave_approver", "expense_approver" })
            if (d.Str(approver) is null || (oldBoss is not null && d.Str(approver) == oldBoss && d.Str("reports_to") != oldBoss))
                d[approver] = d.Str("reports_to");

        var hs = await m.SingleAsync("HRSettings", ct);
        d.SetIfEmpty("holiday_list", hs.Str("default_holiday_list") ?? company.Default("default_holiday_list"));
        d.SetIfEmpty("shift_type", hs.Str("default_shift_type"));
        d.SetIfEmpty("leave_policy", hs.Str("default_leave_policy"));
        if (d.Date("probation_end_date") is { } pe && d.Date("hire_date") is { } hd && pe < hd)
            errors.Add("probation_end_date", "The probation end date is before the date of joining");
        if (d.Str("reports_to") == me) errors.Add("reports_to", "An employee cannot report to themselves");
        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var oldPos = c.Previous?.Data.Str("position"); var newPos = c.Doc.Data.Str("position");
        if (oldPos != newPos || c.Previous?.Data.Str("status") != c.Doc.Data.Str("status"))
            ErpEvents.Emit(c, ErpEvents.PositionSync, new() { ["old_position"] = oldPos, ["new_position"] = newPos });
        if (c.Previous is null && c.Doc.Data.Str("user_id") is null && c.Doc.Data.Str("work_email") is not null)
            ErpEvents.Emit(c, ErpEvents.EmployeeUser, new());
        return Task.CompletedTask;
    }
}

/// <summary>Position: holder &amp; status computed from Employee/EmployeeAssignment; default grade/cost center from job profile/unit.</summary>
public sealed class PositionHook(IDocumentStore store) : IBeforeValidate<Position>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var job = await m.RequireAsync("JobProfile", d.Str("job_profile"), "job_profile", "Job profile", ct);
        d.SetIfEmpty("grade", job.Data.Str("default_grade"));
        d.SetIfEmpty("cost_center", await m.StrAsync("OrganizationUnit", d.Str("organization_unit"), "cost_center", ct));
        if (c.Previous is null && job.Data.Bool("is_manager_job")) d["is_manager_position"] = true;

        var holder = await Hr.HolderAsync(m, c.Doc.Name, null, ct);
        d["current_employee"] = holder?.Name;
        var status = d.Str("status");
        if (holder is not null && status is "Closed" or "Frozen")
            throw Errors.One("status", $"The position is still filled by {holder.Name}; move the employee first");
        if (holder is not null) d["status"] = "Filled";
        else if (status is null or "Filled") d["status"] = "Vacant";
    }
}

/// <summary>
/// Organization unit: dynamic unit types, cycle prevention, effective-dated overlap validation, closure checks,
/// materialized path &amp; breadcrumb computation, and automatic path cascade to descendant units.
/// </summary>
public sealed class OrganizationUnitHook(IDocumentStore store)
    : IBeforeValidate<OrganizationUnit>, IAfterSave<OrganizationUnit>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var errors = new Errors();
        var code = d.Str("org_unit_code") ?? c.Doc.Name;

        if (d.Str("unit_type") is { } ut && Hr.DefaultUnitTypes.Any(x => x.Name == ut))
            await Hr.EnsureDefaultUnitTypesAsync(store, c.Doc.TenantId, ct);

        var m = new Masters(store, c.Doc.TenantId);

        if (d.Str("previous_version") is { } prevVer && await m.GetAsync("OrganizationUnit", prevVer, ct) is { } prevUnit)
            d.SetIfEmpty("base_unit_code", prevUnit.Data.Str("base_unit_code") ?? prevUnit.Data.Str("org_unit_code") ?? prevUnit.Name);
        d.SetIfEmpty("base_unit_code", code);
        var baseCode = d.Str("base_unit_code") ?? code;

        if (c.Previous is not null && c.Previous.Data.Bool("is_active") && !d.Bool("is_active") && d.Str("status") == "Active")
            d["status"] = "Closed";
        d.SetIfEmpty("status", d.Bool("is_active") ? "Active" : "Closed");
        var status = d.Str("status") ?? "Active";
        d["is_active"] = status == "Active";

        var from = d.Date("effective_from");
        var to = d.Date("effective_to");
        if (from is not null && to is not null && to < from)
            errors.Add("effective_to", "The effective end date must be on or after the effective start date");

        // Advisory parent type check
        var unitTypeDoc = await m.GetAsync("OrganizationUnitType", d.Str("unit_type"), ct);
        var parentName = d.Str("parent_org_unit");
        var parentDoc = await m.GetAsync("OrganizationUnit", parentName, ct);
        if (unitTypeDoc?.Data.Str("parent_type") is { } expectedParent && parentDoc is not null)
        {
            var actualParentType = parentDoc.Data.Str("unit_type");
            if (actualParentType is not null && actualParentType != expectedParent)
                c.Messages.Add($"Advisory: unit type '{unitTypeDoc.Name}' recommends parent type '{expectedParent}', but parent '{parentDoc.Name}' has type '{actualParentType}'");
        }

        // Cycle prevention (including logical base_unit_code cycles across versions) & path calculation
        var ancestorCodes = new List<string>();
        var ancestorNames = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { code };
        var cur = parentDoc;
        while (cur is not null)
        {
            var curCode = cur.Data.Str("org_unit_code") ?? cur.Name;
            var curBase = cur.Data.Str("base_unit_code") ?? curCode;
            var sameBase = curBase == baseCode && Hr.Overlaps(from, to, cur.Data.Date("effective_from"), cur.Data.Date("effective_to"));
            if (curCode == code || sameBase)
            {
                errors.Add("parent_org_unit", $"Circular organization hierarchy detected involving unit '{code}'");
                break;
            }
            if (!visited.Add(curCode)) break;
            ancestorCodes.Add(curCode);
            ancestorNames.Add(cur.Data.Str("org_unit_name") ?? curCode);
            cur = await m.GetAsync("OrganizationUnit", cur.Data.Str("parent_org_unit"), ct);
        }
        ancestorCodes.Reverse();
        ancestorNames.Reverse();
        ancestorCodes.Add(code);
        ancestorNames.Add(d.Str("org_unit_name") ?? code);
        d["depth"] = (long)ancestorCodes.Count;
        d["materialized_path"] = "/" + string.Join("/", ancestorCodes) + "/";
        d["breadcrumb"] = string.Join(" > ", ancestorNames);

        // Overlap check: only one Active row per base_unit_code on any date
        if (status == "Active")
        {
            var sameUnits = await m.QueryAsync("OrganizationUnit", [new FilterClause("base_unit_code", "=", baseCode)], ct, take: 100);
            foreach (var other in sameUnits.Where(u => u.Name != code && u.Name != c.Doc.Name && (u.Data.Str("status") ?? "Active") == "Active"))
            {
                if (Hr.Overlaps(from, to, other.Data.Date("effective_from"), other.Data.Date("effective_to")))
                {
                    errors.Add("effective_from", $"Active validity period overlaps with version {other.Name} for unit {baseCode}");
                    break;
                }
            }
        }

        // Closure check: closing a unit or ending its validity requires no active Position to be stranded
        if (status == "Closed" || to is not null)
        {
            var activePositions = (await m.QueryAsync("Position", [new FilterClause("organization_unit", "=", code)], ct, take: 100))
                .Where(p => p.Data.Str("status") is not "Closed").ToList();
            if (activePositions.Count > 0)
            {
                var sameUnits = await m.QueryAsync("OrganizationUnit", [new FilterClause("base_unit_code", "=", baseCode)], ct, take: 100);
                var hasSuccessor = to is not null && sameUnits.Any(u =>
                    u.Name != code && u.Name != c.Doc.Name
                    && (u.Data.Str("status") ?? "Active") is "Active" or "Planned"
                    && u.Data.Date("effective_from") is { } sf && sf <= to.Value.AddDays(1)
                    && (u.Data.Date("effective_to") is null || u.Data.Date("effective_to") > to.Value));
                if (!hasSuccessor)
                    errors.Add(status == "Closed" ? "status" : "effective_to",
                        $"Cannot close unit {code} while active position {activePositions[0].Name} is still assigned to it");
            }
        }

        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var oldPos = c.Previous?.Data.Str("manager_position"); var newPos = c.Doc.Data.Str("manager_position");
        if (c.Previous is not null && oldPos != newPos)
            ErpEvents.Emit(c, ErpEvents.OrgManagerChanged, new() { ["old_position"] = oldPos, ["new_position"] = newPos });

        if (c.Previous is null) return;
        var oldPath = c.Previous.Data.Str("materialized_path");
        var newPath = c.Doc.Data.Str("materialized_path");
        var oldCrumb = c.Previous.Data.Str("breadcrumb");
        var newCrumb = c.Doc.Data.Str("breadcrumb");
        if (oldPath == newPath && oldCrumb == newCrumb) return;

        // Cascade recalculated materialized_path, depth, and breadcrumb to all descendant units and linked Positions/Employees
        var m = new Masters(store, c.Doc.TenantId);
        var allUnits = await m.QueryAsync("OrganizationUnit", [], ct, take: 10_000);
        var childrenByParent = allUnits
            .Where(u => u.Name != c.Doc.Name && u.Data.Str("parent_org_unit") is not null)
            .GroupBy(u => u.Data.Str("parent_org_unit")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var uow = new UnitOfWork();
        var updatedUnits = new Dictionary<string, (string Path, string Crumb)>(StringComparer.Ordinal)
        {
            [c.Doc.Name] = (newPath ?? $"/{c.Doc.Name}/", newCrumb ?? c.Doc.Name)
        };
        var queue = new Queue<(string Name, string Path, long Depth, string Crumb)>();
        queue.Enqueue((c.Doc.Name, newPath ?? $"/{c.Doc.Name}/", (long)c.Doc.Data.Dec("depth"), newCrumb ?? c.Doc.Name));
        var seen = new HashSet<string>(StringComparer.Ordinal) { c.Doc.Name };

        while (queue.Count > 0)
        {
            var (parentName, parentPath, parentDepth, parentCrumb) = queue.Dequeue();
            if (!childrenByParent.TryGetValue(parentName, out var children)) continue;
            foreach (var child in children)
            {
                if (!seen.Add(child.Name)) continue;
                var childCode = child.Data.Str("org_unit_code") ?? child.Name;
                var childTitle = child.Data.Str("org_unit_name") ?? childCode;
                var cPath = $"{parentPath}{childCode}/";
                var cDepth = parentDepth + 1;
                var cCrumb = $"{parentCrumb} > {childTitle}";
                var clone = child.Clone();
                clone.Data["materialized_path"] = cPath;
                clone.Data["depth"] = cDepth;
                clone.Data["breadcrumb"] = cCrumb;
                clone.Version = child.Version + 1;
                clone.Modified = c.Doc.Modified;
                uow.Upserts.Add((clone, child.Version));
                updatedUnits[child.Name] = (cPath, cCrumb);
                queue.Enqueue((child.Name, cPath, cDepth, cCrumb));
            }
        }

        // Refresh cached breadcrumb and path on Positions and Employees in the updated subtree
        foreach (var dt in new[] { "Position", "Employee" })
        {
            var docs = await m.QueryAsync(dt, [], ct, take: 10_000);
            foreach (var doc in docs)
            {
                if (doc.Data.Str("organization_unit") is { } uName && updatedUnits.TryGetValue(uName, out var info))
                {
                    if (doc.Data.Str("organization_path") == info.Path && doc.Data.Str("organization_breadcrumb") == info.Crumb)
                        continue;
                    var clone = doc.Clone();
                    clone.Data["organization_path"] = info.Path;
                    clone.Data["organization_breadcrumb"] = info.Crumb;
                    clone.Version = doc.Version + 1;
                    clone.Modified = c.Doc.Modified;
                    uow.Upserts.Add((clone, doc.Version));
                }
            }
        }

        if (uow.Upserts.Count > 0) await store.CommitAsync(uow, ct);
    }
}

/// <summary>Form action on a Planned OrganizationUnit to activate it on its effective date, close its predecessor version, and migrate active Positions.</summary>
public sealed class OrganizationUnitActivateAction(IDocumentStore store, TimeProvider clock) : IFormAction<OrganizationUnit>
{
    public string ActionId => "erp.org.activate_planned";
    public string Label => "Activate planned structure";
    public string Icon => "check-circle";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) =>
        doc.Data.Str("status") == "Planned" && (user.HasRole("HR") || user.IsAdmin);

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        var m = new Masters(store, ctx.Doc.TenantId);
        var sys = new SystemDocs(ctx.Docs, ctx.Doc.TenantId);
        var effDate = ctx.Doc.Data.Date("effective_from") ?? Hr.Today(clock);
        var moved = await Hr.ActivatePlannedUnitAsync(sys, m, ctx.Doc, effDate, ct);
        return FormActionResult.Ok($"Organization unit {ctx.Doc.Name} activated effective {D.Iso(effDate)} ({moved} position(s) migrated).");
    }
}

/// <summary>Employee Assignment: secondary placements and acting appointments (Plt./Pj.) with effective dates.</summary>
public sealed class EmployeeAssignmentHook(IDocumentStore store)
    : IBeforeValidate<EmployeeAssignment>, IAfterSave<EmployeeAssignment>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        d["is_primary"] = d.Str("assignment_type") == "Primary";

        if (d.Str("position") is { } posName)
        {
            var pos = await m.RequireAsync("Position", posName, "position", "Position", ct);
            if (pos.Data.Str("status") is "Closed" or "Frozen")
                errors.Add("position", $"Position {pos.Name} is {pos.Data.Str("status")}");
            d.SetIfEmpty("organization_unit", pos.Data.Str("organization_unit"));
            d.SetIfEmpty("job_profile", pos.Data.Str("job_profile"));
        }

        var start = d.Date("start_date");
        var end = d.Date("end_date");
        if (start is not null && end is not null && end < start)
            errors.Add("end_date", "The end date must be on or after the start date");

        var asgType = d.Str("assignment_type");
        var repLine = d.Str("reporting_line") ?? "Solid";
        if (asgType == "Acting" && d.Str("acting_for") is null && d.Str("position") is null)
            errors.Add("acting_for", "An Acting (Plt.) assignment must specify either 'Acting for' or a 'Position'");
        if (d.Str("acting_for") == emp.Name)
            errors.Add("acting_for", "An employee cannot be Acting for themselves");
        if (repLine == "Dotted" && d.Str("dotted_line_manager") is null && d.Str("project") is null)
            errors.Add("dotted_line_manager", "A Dotted-line assignment must specify either a 'Dotted-line manager' or a 'Project'");
        if (d.Str("dotted_line_manager") == emp.Name)
            errors.Add("dotted_line_manager", "An employee cannot be their own dotted-line manager");
        if (d.Str("project") is { } prjCode && d.Str("dotted_line_manager") is null)
        {
            var prj = await m.GetAsync("Project", prjCode, ct);
            if (prj?.Data.Str("project_manager") is { } pm && pm != emp.Name)
                d["dotted_line_manager"] = pm;
        }

        if (d.Str("status") == "Active")
        {
            var existing = await m.QueryAsync("EmployeeAssignment", [new FilterClause("employee", "=", emp.Name), new FilterClause("status", "=", "Active")], ct, take: 100);
            foreach (var other in existing.Where(x => x.Name != c.Doc.Name))
            {
                var sameSlot = other.Data.Str("assignment_type") == d.Str("assignment_type")
                    && other.Data.Str("organization_unit") == d.Str("organization_unit")
                    && other.Data.Str("position") == d.Str("position");
                if (sameSlot && Hr.Overlaps(start, end, other.Data.Date("start_date"), other.Data.Date("end_date")))
                {
                    errors.Add("start_date", $"Overlaps with active assignment {other.Name}");
                    break;
                }
            }
        }

        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        var oldPos = c.Previous?.Data.Str("position"); var newPos = c.Doc.Data.Str("position");
        if (oldPos is not null || newPos is not null)
            ErpEvents.Emit(c, ErpEvents.PositionSync, new() { ["old_position"] = oldPos, ["new_position"] = newPos });
        return Task.CompletedTask;
    }
}

/// <summary>Job Change (Workday Change Job): applied to Employee on the effective date (immediately if today or past, otherwise by the daily job).</summary>
public sealed class JobChangeHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<JobChange>, IAfterSave<JobChange>, IOnCancel<JobChange>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Date("effective_date") is { } eff && emp.Data.Date("hire_date") is { } hire && eff < hire)
            errors.Add("effective_date", "The effective date is before the employee's date of joining");
        if (d.Str("new_position") is { } np)
        {
            var pos = await m.RequireAsync("Position", np, "new_position", "Position", ct);
            if (pos.Data.Str("status") is "Closed" or "Frozen") errors.Add("new_position", $"Position {np} is {pos.Data.Str("status")}");
            if (await Hr.HolderAsync(m, np, emp.Name, ct) is { } holder && holder.Data.Str("position") == np)
                errors.Add("new_position", $"Position {np} is still filled by {holder.Name}");
            d.SetIfEmpty("new_organization_unit", pos.Data.Str("organization_unit"));
            d.SetIfEmpty("new_job_profile", pos.Data.Str("job_profile"));
            d.SetIfEmpty("new_grade", pos.Data.Str("grade"));
        }
        errors.ThrowIfAny();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft
            && c.Doc.Data.Date("effective_date") <= Hr.Today(clock))
            ErpEvents.Emit(c, ErpEvents.JobChangeApply, new());
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var emp = await new Masters(store, c.Doc.TenantId).GetAsync("Employee", c.Doc.Data.Str("employee"), ct);
        if (emp?.Data.Str("last_job_change") == c.Doc.Name)
            throw Errors.One("name", "This change has already been applied to the employee; create a new Job Change to reverse it");
    }
}

/// <summary>Employee Separation (Workday Terminate): default checklist; applied on the last working day (Employee → Left, user deactivated).</summary>
public sealed class EmployeeSeparationHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<EmployeeSeparation>, IAfterSave<EmployeeSeparation>, IOnCancel<EmployeeSeparation>
{
    private static readonly (string Task, string Who)[] DefaultChecklist =
    [
        ("Work handover", "Manager"), ("Return company assets (laptop, access card, vehicle)", "General Affairs"),
        ("Revoke system & email access", "IT"), ("Compute final pay & deductions, settle advances", "Finance"),
        ("Exit interview & employment certificate", "HR"), ("Report to BPJS Employment & Health", "HR"),
    ];

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        if (d.Date("last_working_date") is { } last && emp.Data.Date("hire_date") is { } hire && last < hire)
            throw Errors.One("last_working_date", "The last working date is before the date of joining");
        var other = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 10))
            .FirstOrDefault(s => s.Name != c.Doc.Name && s.DocStatus == DocStatus.Submitted);
        if (other is not null) throw Errors.One("employee", $"Separation {other.Name} already exists");
        if (d.Rows("checklist").Count == 0)
            d["checklist"] = DefaultChecklist.Select(x => new Dictionary<string, object?> { ["task"] = x.Task, ["responsible"] = x.Who, ["is_done"] = false }).ToList();
    }

    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus == DocStatus.Submitted && c.Previous?.DocStatus == DocStatus.Draft
            && c.Doc.Data.Date("last_working_date") <= Hr.Today(clock))
            ErpEvents.Emit(c, ErpEvents.SeparationApply, new());
        return Task.CompletedTask;
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        var emp = await new Masters(store, c.Doc.TenantId).GetAsync("Employee", c.Doc.Data.Str("employee"), ct);
        if (emp?.Data.Str("status") == Hr.Left)
            throw Errors.One("name", "The separation has already been applied; reactivate the employee through HR (status) before cancelling");
    }
}

/// <summary>Employment Contract: no overlap; total PKWT duration including renewals at most 60 months (PP 35/2021); status computed on load.</summary>
public sealed class EmploymentContractHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<EmploymentContract>, IOnLoad<EmploymentContract>
{
    public const int MaxPkwtMonths = 60;

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var start = d.Date("start_date"); var end = d.Date("end_date");
        foreach (var o in (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 100))
                 .Where(o => o.Name != c.Doc.Name && o.DocStatus == DocStatus.Submitted))
            if (Hr.Overlaps(start, end, o.Data.Date("start_date"), o.Data.Date("end_date")))
                throw Errors.One("start_date", $"Overlaps with contract {o.Name}");

        if (d.Str("contract_type") == "PKWT" && start is not null && end is not null)
        {
            var months = AssetBook.MonthsBetween(start.Value, end.Value.AddDays(1));
            for (var prev = await m.GetAsync(c.Doc.DocType, d.Str("renewal_of"), ct); prev is not null; prev = await m.GetAsync(c.Doc.DocType, prev.Data.Str("renewal_of"), ct))
                if (prev.Data.Str("contract_type") == "PKWT" && prev.Data.Date("start_date") is { } ps && prev.Data.Date("end_date") is { } pe)
                    months += AssetBook.MonthsBetween(ps, pe.AddDays(1));
            if (months > MaxPkwtMonths) throw Errors.One("end_date", $"Total fixed-term (PKWT) duration including renewals is {months} months, exceeding {MaxPkwtMonths} months");
        }
    }

    public async Task OnLoadAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted) return;
        var renewed = (await new Masters(store, c.Doc.TenantId).QueryAsync(c.Doc.DocType, [new FilterClause("renewal_of", "=", c.Doc.Name)], ct, take: 5))
            .Any(x => x.DocStatus == DocStatus.Submitted);
        c.Doc.Data["status"] = renewed ? "Renewed" : c.Doc.Data.Date("end_date") is { } e && e < Hr.Today(clock) ? "Expired" : "Active";
    }
}

public sealed class LeaveAllocationHook(IDocumentStore store) : IBeforeValidate<LeaveAllocation>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var other = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name), new FilterClause("leave_type", "=", d.Str("leave_type"))], ct, take: 100))
            .FirstOrDefault(o => o.Name != c.Doc.Name && o.DocStatus != DocStatus.Cancelled
                && Hr.Overlaps(d.Date("from_date"), d.Date("to_date"), o.Data.Date("from_date"), o.Data.Date("to_date")));
        if (other is not null) throw Errors.One("from_date", $"Overlaps with allocation {other.Name}");
    }
}

/// <summary>Leave math: working days (respecting materialized EmployeeDailySchedule when available, or weekly off &amp; holiday list), quota balance, overlap.</summary>
internal static class LeaveMath
{
    public static async Task<HashSet<DayOfWeek>> WeeklyOffAsync(Masters m, Document emp, Document? holidayList, CancellationToken ct)
    {
        var shift = await m.GetAsync("ShiftType", emp.Data.Str("shift_type"), ct);
        var rule = shift?.Data.Str("working_days_per_week") switch
        {
            "6" => "Sunday",
            "5" => "Saturday & Sunday",
            _ => holidayList?.Data.Str("weekly_off") ?? "Saturday & Sunday",
        };
        return rule switch
        {
            "Sunday" => [DayOfWeek.Sunday],
            "None" => [],
            _ => [DayOfWeek.Saturday, DayOfWeek.Sunday],
        };
    }

    public static async Task<decimal> WorkingDaysAsync(Masters m, Document emp, DateOnly from, DateOnly to, bool includeHolidays, CancellationToken ct)
    {
        var schedules = await m.QueryAsync("EmployeeDailySchedule",
            [new FilterClause("employee", "=", emp.Name), new FilterClause("work_date", ">=", D.Iso(from)), new FilterClause("work_date", "<=", D.Iso(to))], ct, take: 400);
        var byDate = schedules
            .Where(s => s.Data.Date("work_date") is not null)
            .ToDictionary(s => s.Data.Date("work_date")!.Value, s => s);

        var hl = await m.GetAsync("HolidayList", emp.Data.Str("holiday_list"), ct);
        var off = await WeeklyOffAsync(m, emp, hl, ct);
        var holidays = hl?.Data.Rows("holidays").Select(h => h.Date("holiday_date")).OfType<DateOnly>().ToHashSet() ?? [];
        decimal days = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (includeHolidays) { days++; continue; }
            if (byDate.TryGetValue(day, out var sched))
            {
                var st = sched.Data.Str("schedule_type");
                var src = sched.Data.Str("source_type");
                if (st == "WORK" || (st == "LEAVE" && src == "Leave")) days++;
            }
            else if (!off.Contains(day.DayOfWeek) && !holidays.Contains(day))
            {
                days++;
            }
        }
        return days;
    }

    public static bool Active(Document a) => a.DocStatus != DocStatus.Cancelled && a.WorkflowState is not ("Rejected" or "Cancelled");
}

/// <summary>
/// Leave application: calculated &amp; validated on save and before approval transition.
/// When approved, updates materialized EmployeeDailySchedule rows to LEAVE and checks shift manpower coverage.
/// </summary>
public sealed class LeaveApplicationHook(IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<LeaveApplication>, IBeforeTransition<LeaveApplication>, IAfterSave<LeaveApplication>, IOnCancel<LeaveApplication>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var errors = new Errors();
        var emp = await Approval.ActiveEmployeeAsync(m, d.Str("employee"), ct);
        var type = await m.RequireAsync("LeaveType", d.Str("leave_type"), "leave_type", "Leave type", ct);
        var from = d.Date("from_date"); var to = d.Date("to_date");
        if (from is null || to is null || to < from) return;
        if (c.Doc.DocStatus == DocStatus.Draft)
        {
            // Who can approve is decided by who is available now, not on the first day of the leave.
            var resolved = await Hr.ResolveApproversAsync(m, emp, null, DateOnly.FromDateTime(clock.GetLocalNow().Date), ct, "leave_approver");
            d["leave_approver"] = resolved.LineApprover;
        }

        var gender = type.Data.Str("applicable_gender");
        if (gender is "Female" or "Male" && emp.Data.Str("gender") != gender) errors.Add("leave_type", $"{type.Name} is only for {gender.ToLowerInvariant()} employees");
        var days = await LeaveMath.WorkingDaysAsync(m, emp, from.Value, to.Value, type.Data.Bool("include_holidays"), ct);
        if (d.Bool("half_day")) days = Math.Min(days, 0.5m);
        if (days <= 0) errors.Add("from_date", "There are no working days in this date range");
        var max = type.Data.Dec("max_consecutive_days");
        if (max > 0 && days > max) errors.Add("to_date", $"At most {max:0.#} consecutive days for {type.Name}");
        d["total_days"] = days;

        var mine = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("employee", "=", emp.Name)], ct, take: 1000))
            .Where(a => a.Name != c.Doc.Name && LeaveMath.Active(a)).ToList();
        if (mine.FirstOrDefault(a => Hr.Overlaps(from, to, a.Data.Date("from_date"), a.Data.Date("to_date"))) is { } clash)
            errors.Add("from_date", $"Overlaps with application {clash.Name}");

        if (type.Data.Bool("is_annual_quota"))
        {
            var alloc = (await m.QueryAsync("LeaveAllocation", [new FilterClause("employee", "=", emp.Name), new FilterClause("leave_type", "=", type.Name)], ct, take: 50))
                .FirstOrDefault(a => a.DocStatus == DocStatus.Submitted && a.Data.Date("from_date") <= from && to <= a.Data.Date("to_date"));
            if (alloc is null) errors.Add("leave_type", $"There is no {type.Name} allocation for this period");
            else
            {
                var aFrom = alloc.Data.Date("from_date"); var aTo = alloc.Data.Date("to_date");
                var used = mine.Where(a => a.DocStatus == DocStatus.Submitted && a.Data.Str("leave_type") == type.Name
                                           && Hr.Overlaps(aFrom, aTo, a.Data.Date("from_date"), a.Data.Date("to_date")))
                    .Sum(a => a.Data.Dec("total_days"));
                var balance = alloc.Data.Dec("total_leaves") - used;
                d["balance_before"] = balance;
                if (days > balance) errors.Add("to_date", $"Only {balance:0.#} days of {type.Name} remain");
            }
        }
        errors.ThrowIfAny();
    }

    public Task BeforeValidateAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);

    public Task BeforeTransitionAsync(HookContext c, CancellationToken ct) =>
        c.ToState == "Pending Approval" || c.ToState == "Approved" ? CheckAsync(c, ct) : Task.CompletedTask;

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.WorkflowState == Approval.Approved && c.Previous?.WorkflowState != Approval.Approved)
            await WsmLeaveSync.ApplyApprovedLeaveAsync(store, c, ct);
        else if (c.Doc.WorkflowState == "Cancelled" && c.Previous?.WorkflowState == Approval.Approved)
            await WsmLeaveSync.RevertCancelledLeaveAsync(store, c, ct);
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) =>
        WsmLeaveSync.RevertCancelledLeaveAsync(store, c, ct);
}

/// <summary>Application of HR events to Employee, shared by the event handler (immediate) and daily job (future effective dates).</summary>
internal static class HrApply
{
    public static async Task JobChangeAsync(SystemDocs sys, Document jc, CancellationToken ct)
    {
        var d = jc.Data;
        if (jc.DocStatus != DocStatus.Submitted || d.Str("employee") is not { } empName) return;
        var emp = await sys.TryGetAsync("Employee", empName, ct);
        if (emp is null) return;
        var eff = d.Date("effective_date");
        var lastDate = emp.Data.Date("last_job_change_date"); var lastName = emp.Data.Str("last_job_change");
        if (lastName == jc.Name || (lastDate is not null && eff is not null && (eff < lastDate || (eff == lastDate && string.CompareOrdinal(jc.Name, lastName) < 0))))
            return;
        var patch = new Dictionary<string, object?> { ["last_job_change"] = jc.Name, ["last_job_change_date"] = d.Str("effective_date") };
        foreach (var f in new[] { "organization_unit", "position", "job_profile", "grade", "location", "cost_center", "reports_to" })
            if (d.Str($"new_{f}") is { } v) patch[f] = v;
        if (d.Str("new_employment_type") is { } et) patch["employment_type"] = et;
        await sys.UpdateAsync("Employee", empName, patch, ct);
    }

    public static async Task SeparationAsync(SystemDocs sys, IUserAccountRepository? accounts, Document sep, CancellationToken ct)
    {
        if (sep.DocStatus != DocStatus.Submitted || sep.Data.Str("employee") is not { } empName) return;
        var emp = await sys.TryGetAsync("Employee", empName, ct);
        if (emp is null) return;
        if (emp.Data.Str("status") != Hr.Left)
            await sys.UpdateAsync("Employee", empName, new Dictionary<string, object?> { ["status"] = Hr.Left }, ct);
        if (accounts is not null && emp.Data.Str("user_id") is { } userId && await accounts.FindByIdAsync(userId, ct) is { IsActive: true } account)
        {
            account.IsActive = false;
            await accounts.UpdateAsync(account, ct);
        }
    }

    public static async Task<string?> EnsureUserAsync(IServiceProvider services, string tenant, Document emp, string email, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetService<IUserAccountRepository>();
        if (repo is null) return null;
        var userName = email.Trim().ToLowerInvariant();
        var normalizedEmail = userName.ToUpperInvariant();
        if (await repo.FindByNormalizedEmailAsync(normalizedEmail, ct) is { } existing)
        {
            if (existing.TenantId != tenant) return null;
            if (existing.EmployeeId is null)
            {
                existing.EmployeeId = emp.Name;
                existing.Department ??= emp.Data.Str("organization_unit");
                await repo.UpdateAsync(existing, ct);
            }
            return existing.EmployeeId == emp.Name ? existing.Id : null;
        }

        var account = new UserAccount
        {
            Id = userName, UserName = userName, Email = userName, FullName = emp.Data.Str("full_name") ?? emp.Name, TenantId = tenant,
            Department = emp.Data.Str("organization_unit"), EmployeeId = emp.Name, Roles = ["Employee"], CreatedAt = DateTimeOffset.UtcNow,
        };
        if (scope.ServiceProvider.GetService<UserManager<UserAccount>>() is { } manager)
        {
            var result = await manager.CreateAsync(account);
            if (!result.Succeeded)
                throw new InvalidOperationException($"Creating the user for {emp.Name} failed: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
        else
        {
            account.NormalizedUserName = normalizedEmail;
            account.NormalizedEmail = normalizedEmail;
            await repo.CreateAsync(account, ct);
        }
        return account.Id;
    }
}
