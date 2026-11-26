using System.Globalization;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class OkrObjective { }
public sealed class OneOnOne { }

/// <summary>
/// Progress rules for goals (OKR). Each key result is 0–100 %: Increase/Decrease move from start to target (capped at 0
/// and 100), Milestone is 100 when done. An objective's progress is the weighted average of its key results.
/// </summary>
public static class GoalMath
{
    public const string Increase = "Increase";
    public const string Decrease = "Decrease";
    public const string Milestone = "Milestone";
    public static readonly string[] Directions = [Increase, Decrease, Milestone];

    public static decimal KeyResultProgress(string direction, decimal start, decimal target, decimal current, bool done)
    {
        if (direction == Milestone) return done ? 100m : 0m;
        decimal pct;
        if (direction == Decrease)
            pct = start == target ? (current <= target ? 100m : 0m) : (start - current) / (start - target) * 100m;
        else
            pct = target == start ? (current >= target ? 100m : 0m) : (current - start) / (target - start) * 100m;
        return Clamp(pct);
    }

    public static decimal Weighted(IEnumerable<(decimal Weight, decimal Progress)> items)
    {
        var list = items.ToList();
        var total = list.Sum(x => x.Weight);
        return total <= 0m ? 0m : Clamp(list.Sum(x => x.Weight * x.Progress) / total);
    }

    private static decimal Clamp(decimal pct) => Math.Round(Math.Min(100m, Math.Max(0m, pct)), 1, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Goal (OKR): period format, company-level goals by HR-Talent only, optional alignment to a parent goal in the same
/// period without loops, key result checks, computed
/// progress, weights that add up to 100 before the goal is Active, and a lock once the goal is Achieved, Missed or
/// Cancelled (only HR-Talent may change it afterwards). <c>reports_to</c> comes from the employee record.
/// </summary>
public sealed class OkrObjectiveHook(IDocumentStore store) : IBeforeValidate<OkrObjective>
{
    private static readonly Regex PeriodRx = new(@"^\d{4}-(Q[1-4]|H[12]|FY)$", RegexOptions.CultureInvariant);

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var prev = c.Previous?.Data;
        var isHr = c.User.IsAdmin || c.User.HasRole(ErpRoles.HrTalent);

        var emp = d.Str("employee");
        d["reports_to"] = string.IsNullOrEmpty(emp) ? null : await m.StrAsync("Employee", emp, "reports_to", ct);

        var status = d.Str("status") ?? "Draft";
        var prevStatus = prev?.Str("status");
        if (prevStatus is "Achieved" or "Missed" or "Cancelled" && !isHr)
            throw Errors.One("status", "Closed goals are locked; only HR-Talent can change them");

        var period = d.Str("period") ?? "";
        if (!PeriodRx.IsMatch(period))
            throw Errors.One("period", "Use the form 2026-Q1, 2026-H1 or 2026-FY");

        if (d.Str("level") == "Company" && !isHr && prev?.Str("level") != "Company")
            throw Errors.One("level", "Only HR-Talent can create or set company-level goals");

        if (d.Str("parent_objective") is { } parentName)
        {
            var parent = await m.GetAsync("OkrObjective", parentName, ct)
                ?? throw Errors.One("parent_objective", "Parent goal not found");
            if (parent.Data.Str("period") != period)
                throw Errors.One("parent_objective", "The parent goal must be in the same period");
            // No loops: walk up the chain (A → B → A, or a goal aligned to itself).
            var seen = new HashSet<string>(StringComparer.Ordinal) { c.Doc.Name };
            for (var p = parent; p is not null; p = p.Data.Str("parent_objective") is { } up ? await m.GetAsync("OkrObjective", up, ct) : null)
                if (!seen.Add(p.Name) || seen.Count > 50)
                    throw Errors.One("parent_objective", "This alignment would create a loop");
        }

        var krs = d.Rows("key_results");
        if (krs.Count == 0)
            throw Errors.One("key_results", "Add at least one key result");

        for (var i = 0; i < krs.Count; i++)
        {
            var kr = krs[i];
            var label = $"Key result {i + 1}";
            var direction = kr.Str("direction") ?? GoalMath.Increase;
            if (!GoalMath.Directions.Contains(direction))
                throw Errors.One("key_results", $"{label}: measure must be Increase, Decrease or Milestone");
            if (kr.Str("description") is null)
                throw Errors.One("key_results", $"{label}: the key result text is required");
            if (kr.Dec("weight") <= 0m)
                throw Errors.One("key_results", $"{label}: weight must be above 0");

            kr["progress_percent"] = GoalMath.KeyResultProgress(direction,
                kr.Dec("start_value"), kr.Dec("target_value"), kr.Dec("current_value"), kr.Bool("is_done"));
        }

        var total = krs.Sum(k => k.Dec("weight"));
        if (status is "Active" or "Achieved" or "Missed" && Math.Abs(total - 100m) > 0.01m)
            throw Errors.One("key_results", $"Key result weights must add up to 100 before the goal is {status} (now {total})");

        d["progress_percent"] = GoalMath.Weighted(krs.Select(k => (k.Dec("weight"), k.Dec("progress_percent"))));
    }
}

/// <summary>
/// 1:1 check-in. Manager is copied from the employee record when the check-in is created and kept afterwards. Only the employee and the manager may change the record
/// (HR-Talent may correct it). Each side writes only its own notes. Completed and cancelled check-ins are locked.
/// Goal progress is copied to the check-in when it is saved, and action items get an owner and status by default.
/// </summary>
public sealed class OneOnOneHook(IDocumentStore store, TimeProvider? clock = null) : IBeforeValidate<OneOnOne>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var prev = c.Previous?.Data;
        var me = c.User.Attr("employee");
        var isHr = c.User.IsAdmin || c.User.HasRole(ErpRoles.HrTalent);

        // The manager is fixed when the check-in is created (or its employee changes): after a reorganisation, past
        // check-ins stay with the manager who held them.
        var employee = d.Str("employee");
        var manager = prev is not null && prev.Str("employee") == employee
            ? prev.Str("manager")
            : string.IsNullOrEmpty(employee) ? null : await m.StrAsync("Employee", employee, "reports_to", ct);
        d["manager"] = manager;
        var isEmployee = !string.IsNullOrEmpty(me) && me == employee;
        var isManager = !string.IsNullOrEmpty(me) && me == manager;

        if (!isEmployee && !isManager && !isHr)
            throw Errors.One("employee", "Only the employee, their manager or HR-Talent can change this check-in");

        var prevStatus = prev?.Str("status");
        if (prevStatus is "Completed" or "Cancelled" && !isHr)
            throw Errors.One("status", "Completed or cancelled check-ins are locked; only HR-Talent can correct them");

        if (!isManager && !isHr && prev?.Str("manager_notes") != d.Str("manager_notes"))
            throw Errors.One("manager_notes", "Only the manager writes the manager notes");
        if (!isEmployee && !isHr && prev?.Str("employee_notes") != d.Str("employee_notes"))
            throw Errors.One("employee_notes", "Only the employee writes the employee notes");

        if (d.Str("status") == "Completed" && prevStatus != "Completed")
            d["completed_at"] = (clock ?? TimeProvider.System).GetUtcNow().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);

        foreach (var o in d.Rows("objectives"))
        {
            if (o.Str("objective") is not { } goalName) continue;
            var goal = await m.GetAsync("OkrObjective", goalName, ct);
            o["progress_snapshot"] = goal?.Data.Dec("progress_percent") ?? 0m;
        }

        foreach (var a in d.Rows("action_items"))
        {
            if (a.Str("action") is null)
                throw Errors.One("action_items", "Every action item needs a description");
            if (a.Str("assignee") is null) a["assignee"] = employee;
            if (a.Str("status") is null) a["status"] = "Open";
        }
    }
}
