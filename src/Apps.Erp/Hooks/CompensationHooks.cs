using System.Globalization;
using Framework.Actions;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

public sealed class CompensationCycle { }
public sealed class CompensationWorksheet { }

/// <summary>
/// Compensation planning (merit and bonus cycle). Amounts are monthly base salary unless named "annual" or "bonus".
/// - Compa-ratio = current base ÷ midpoint of the grade pay range (company row valid on the effective date).
/// - Merit guideline: the matrix row for (rating, compa band); without one the (rating, Any) row; without that 0–0–0.
/// - Bonus guideline: % of annual base (12 × monthly base) per rating.
/// - Budget: merit = total monthly base × merit %; bonus = total annual base × bonus %. A unit budget row applies to
///   worksheets whose unit is that unit or below it (nearest unit wins); otherwise the cycle defaults.
/// </summary>
public static class Comp
{
    public const string Draft = "Draft";
    public const string Planning = "Planning";
    public const string Applied = "Applied";
    public const string Cancelled = "Cancelled";
    public const string NotRated = "Not rated";
    public const string WsDraft = "Draft";
    public const string WsSubmitted = "Submitted";
    public const string WsApproved = "Approved";
    public const string BandAny = "Any";
    public const string BandBelow = "Below 0.90";
    public const string BandWithin = "0.90–1.10";
    public const string BandAbove = "Above 1.10";

    /// <summary>Budget tolerance for rounding (whole currency units).</summary>
    public const decimal Tolerance = 1m;

    internal static bool IsSystem(UserContext u) => u.Id == "system";
    public static bool IsComben(UserContext u) => u.IsAdmin || u.HasRole(ErpRoles.HrComben);

    public static decimal? CompaRatio(decimal basePay, decimal mid) => mid > 0m ? Math.Round(basePay / mid, 2) : null;

    public static string? Band(decimal? compa) => compa switch
    {
        null => null,
        < 0.90m => BandBelow,
        > 1.10m => BandAbove,
        _ => BandWithin,
    };

    public sealed record Guideline(decimal Min, decimal Target, decimal Max);

    public static Guideline MeritGuideline(IReadOnlyList<IDictionary<string, object?>> rules, string rating, string? band)
    {
        var row = rules.FirstOrDefault(r => r.Str("rating") == rating && band is not null && r.Str("compa_band") == band)
                  ?? rules.FirstOrDefault(r => r.Str("rating") == rating && (r.Str("compa_band") ?? BandAny) == BandAny);
        return row is null ? new(0m, 0m, 0m) : new(row.Dec("min_percent"), row.Dec("target_percent"), row.Dec("max_percent"));
    }

    public static (decimal Target, decimal Max) BonusGuideline(IReadOnlyList<IDictionary<string, object?>> rules, string rating) =>
        rules.FirstOrDefault(r => r.Str("rating") == rating) is { } row ? (row.Dec("target_percent"), row.Dec("max_percent")) : (0m, 0m);

    private static decimal Money(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);

    /// <summary>Recomputes one worksheet line from its snapshot (base, range, rating) and the manager's inputs.</summary>
    public static void RecalcLine(IDictionary<string, object?> line, IReadOnlyList<IDictionary<string, object?>> meritRules,
        IReadOnlyList<IDictionary<string, object?>> bonusRules)
    {
        var basePay = line.Dec("current_base");
        var mid = line.Dec("range_mid");
        var rating = line.Str("rating") ?? NotRated;
        var compa = CompaRatio(basePay, mid);
        var g = MeritGuideline(meritRules, rating, Band(compa));
        var (bonusTarget, bonusMax) = BonusGuideline(bonusRules, rating);

        line["compa_ratio"] = compa;
        line["guideline_min"] = g.Min;
        line["guideline_target"] = g.Target;
        line["guideline_max"] = g.Max;
        var pct = line.TryGetValue("merit_percent", out var raw) && raw is not null ? line.Dec("merit_percent") : g.Target;
        line["merit_percent"] = pct;
        var amount = Money(basePay * pct / 100m);
        line["merit_amount"] = amount;
        line["new_base"] = basePay + amount;
        line["new_compa_ratio"] = CompaRatio(basePay + amount, mid);
        line["bonus_target"] = Money(basePay * 12m * bonusTarget / 100m);
        line["bonus_max"] = Money(basePay * 12m * bonusMax / 100m);
        if (!line.TryGetValue("bonus_amount", out var b) || b is null) line["bonus_amount"] = line["bonus_target"];
        line["outside_guideline"] = pct < g.Min || pct > g.Max || line.Dec("bonus_amount") > line.Dec("bonus_max");
    }

    /// <summary>Budget and totals of a worksheet from its lines.</summary>
    public static void Totals(IDictionary<string, object?> d, decimal meritPct, decimal bonusPct)
    {
        var lines = d.Rows("lines");
        var total = lines.Sum(l => l.Dec("current_base"));
        d["total_base"] = total;
        d["merit_budget_percent"] = meritPct;
        d["bonus_budget_percent"] = bonusPct;
        d["merit_budget"] = Money(total * meritPct / 100m);
        d["merit_planned"] = lines.Sum(l => l.Dec("merit_amount"));
        d["merit_remaining"] = d.Dec("merit_budget") - d.Dec("merit_planned");
        d["bonus_budget"] = Money(total * 12m * bonusPct / 100m);
        d["bonus_planned"] = lines.Sum(l => l.Dec("bonus_amount"));
        d["bonus_remaining"] = d.Dec("bonus_budget") - d.Dec("bonus_planned");
    }

    public static bool OverBudget(IDictionary<string, object?> d) =>
        d.Dec("merit_planned") > d.Dec("merit_budget") + Tolerance || d.Dec("bonus_planned") > d.Dec("bonus_budget") + Tolerance;

    /// <summary>Merit and bonus budget % for a unit: nearest unit row in the cycle, else the cycle defaults.</summary>
    internal static async Task<(decimal Merit, decimal Bonus)> BudgetPercentAsync(Masters m, IDictionary<string, object?> cycle, string? unit, CancellationToken ct)
    {
        var rows = cycle.Rows("unit_budgets");
        if (rows.Count > 0)
            foreach (var u in await Announcements.UnitChainAsync(m, unit, ct))
                if (rows.FirstOrDefault(r => r.Str("organization_unit") == u) is { } row)
                    return (row.Dec("merit_budget_percent"), row.Dec("bonus_budget_percent"));
        return (cycle.Dec("merit_budget_percent"), cycle.Dec("bonus_budget_percent"));
    }

    /// <summary>Rating of the employee's completed appraisal in the cycle, or "Not rated".</summary>
    internal static async Task<string> RatingAsync(Masters m, string? appraisalCycle, string employee, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(appraisalCycle)) return NotRated;
        var done = (await m.QueryAsync("Appraisal", [new FilterClause("employee", "=", employee), new FilterClause("appraisal_cycle", "=", appraisalCycle)], ct, take: 20))
            .Where(a => a.DocStatus == DocStatus.Submitted && a.Data.Str("rating") is not null)
            .OrderByDescending(a => a.Modified)
            .FirstOrDefault();
        return done?.Data.Str("rating") ?? NotRated;
    }

    /// <summary>Pay range (min, mid, max) of a grade for the company on a date; zeros when none is set.</summary>
    internal static async Task<(decimal Min, decimal Mid, decimal Max)> RangeAsync(Masters m, string? grade, string? company, DateOnly on, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(grade) || await m.GetAsync("Grade", grade, ct) is not { } g) return (0m, 0m, 0m);
        var row = g.Data.Rows("pay_ranges")
            .Where(r => (r.Str("company") is null || r.Str("company") == company) && (r.Date("valid_from") is not { } f || f <= on))
            .OrderByDescending(r => r.Str("company") is not null)
            .ThenByDescending(r => r.Date("valid_from") ?? DateOnly.MinValue)
            .FirstOrDefault();
        return row is null ? (0m, 0m, 0m) : (row.Dec("min_salary"), row.Dec("mid_salary"), row.Dec("max_salary"));
    }

    /// <summary>Monthly base salary: the base component in the salary assignment in force the day before the effective date.</summary>
    internal static async Task<(Document? Assignment, decimal Base)> BaseAsync(Masters m, string baseComponent, string employee, DateOnly effective, CancellationToken ct)
    {
        var a = await Payroll.AssignmentAsync(m, employee, effective.AddDays(-1), ct);
        var amount = a?.Data.Rows("earnings").Where(r => r.Str("salary_component") == baseComponent).Sum(r => r.Dec("amount")) ?? 0m;
        return (a, amount);
    }

    internal static async Task<Dictionary<string, object?>> NewLineAsync(Masters m, IDictionary<string, object?> cycle, Document emp, decimal basePay, CancellationToken ct)
    {
        var eff = cycle.Date("effective_date") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var (min, mid, max) = await RangeAsync(m, emp.Data.Str("grade"), cycle.Str("company"), eff, ct);
        var line = new Dictionary<string, object?>
        {
            ["employee"] = emp.Name,
            ["employee_name"] = emp.Data.Str("full_name") ?? emp.Name,
            ["grade"] = emp.Data.Str("grade"),
            ["rating"] = await RatingAsync(m, cycle.Str("appraisal_cycle"), emp.Name, ct),
            ["current_base"] = basePay,
            ["range_min"] = min,
            ["range_mid"] = mid,
            ["range_max"] = max,
        };
        RecalcLine(line, cycle.Rows("merit_rules").Cast<IDictionary<string, object?>>().ToList(),
            cycle.Rows("bonus_rules").Cast<IDictionary<string, object?>>().ToList());
        return line;
    }

    internal static IReadOnlyList<IDictionary<string, object?>> Rules(IDictionary<string, object?> cycle, string key) =>
        cycle.Rows(key).Cast<IDictionary<string, object?>>().ToList();

    /// <summary>
    /// Sets the cycle status (a read-only field, so it is written at store level like other system fields), then saves
    /// the cycle once as the system user so the progress totals are recomputed.
    /// </summary>
    internal static async Task SetStatusAsync(IDocumentStore store, DocumentService docs, string tenant, string cycle, string status,
        DateTimeOffset now, CancellationToken ct, bool stampApplied = false)
    {
        await MultiRater.PatchAsync(store, tenant, "CompensationCycle", cycle, doc =>
        {
            doc.Data["status"] = status;
            if (stampApplied) doc.Data["applied_at"] = now.ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
            return true;
        }, $"Status: {status}", now, ct);
        await new SystemDocs(docs, tenant).TouchAsync("CompensationCycle", cycle, ct);
    }
}

/// <summary>
/// Compensation Cycle: guideline checks, status and progress are system fields (changed by the cycle actions), and the
/// cycle is locked once Applied or Cancelled. Progress totals are recomputed from the worksheets on every save.
/// </summary>
public sealed class CompensationCycleHook(IDocumentStore store) : IBeforeValidate<CompensationCycle>
{
    private static readonly string[] SystemFields =
        ["status", "applied_at", "worksheets", "worksheets_approved", "eligible_employees", "total_base", "merit_budget", "merit_planned", "bonus_budget", "bonus_planned"];

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var prev = c.Previous?.Data;
        var system = Comp.IsSystem(c.User);

        if (prev?.Str("status") is Comp.Applied or Comp.Cancelled && !system)
            throw Errors.One("status", $"The cycle is {prev.Str("status")} and can no longer be changed");
        if (!system)
            foreach (var f in SystemFields) d[f] = prev?.GetValueOrDefault(f);
        d.SetIfEmpty("status", Comp.Draft);

        var errors = new Errors();
        var merit = d.Rows("merit_rules");
        if (merit.Count == 0) errors.Add("merit_rules", "Add at least one merit guideline row");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < merit.Count; i++)
        {
            var r = merit[i];
            r.SetIfEmpty("compa_band", Comp.BandAny);
            if (!seen.Add($"{r.Str("rating")}|{r.Str("compa_band")}"))
                errors.Add($"merit_rules[{i}].rating", $"{r.Str("rating")} / {r.Str("compa_band")} appears more than once");
            if (r.Dec("min_percent") > r.Dec("target_percent") || r.Dec("target_percent") > r.Dec("max_percent"))
                errors.Add($"merit_rules[{i}].target_percent", "Use min ≤ target ≤ max");
        }
        var bonus = d.Rows("bonus_rules");
        if (bonus.Select(r => r.Str("rating")).Distinct().Count() != bonus.Count) errors.Add("bonus_rules", "Each rating may appear once in the bonus guideline");
        if (bonus.Any(r => r.Dec("target_percent") > r.Dec("max_percent"))) errors.Add("bonus_rules", "Bonus target cannot be above the maximum");
        var units = d.Rows("unit_budgets");
        if (units.Select(r => r.Str("organization_unit")).Distinct().Count() != units.Count) errors.Add("unit_budgets", "Each unit may appear once");
        if (d.Date("eligibility_hire_date") is { } hire && d.Date("effective_date") is { } eff && hire > eff)
            errors.Add("eligibility_hire_date", "The eligibility date must be on or before the effective date");
        errors.ThrowIfAny();

        if (c.Previous is null) return;
        var sheets = await new Masters(store, c.Doc.TenantId).QueryAsync("CompensationWorksheet", [new FilterClause("compensation_cycle", "=", c.Doc.Name)], ct, take: 5000);
        d["worksheets"] = sheets.Count;
        d["worksheets_approved"] = sheets.Count(s => s.WorkflowState == Comp.WsApproved);
        d["eligible_employees"] = sheets.Sum(s => s.Data.Rows("lines").Count);
        d["total_base"] = sheets.Sum(s => s.Data.Dec("total_base"));
        d["merit_budget"] = sheets.Sum(s => s.Data.Dec("merit_budget"));
        d["merit_planned"] = sheets.Sum(s => s.Data.Dec("merit_planned"));
        d["bonus_budget"] = sheets.Sum(s => s.Data.Dec("bonus_budget"));
        d["bonus_planned"] = sheets.Sum(s => s.Data.Dec("bonus_planned"));
    }
}

/// <summary>
/// Compensation Worksheet.
/// - Created only by the cycle (Generate worksheets). Header, employees and every snapshot value (base, range, rating)
///   are kept as generated; the manager changes only merit %, bonus and justification per line, plus their comments.
/// - Editable only while the cycle is Planning, and by the manager only in Draft (Submitted = with HR).
/// - Guidelines and budgets are recomputed on every save from the cycle, so a change to the matrix reaches open worksheets.
/// - Submit: within budget and a justification on every line outside the guideline. Approve over budget: HR comment required.
/// </summary>
public sealed class CompensationWorksheetHook(IDocumentStore store) : IBeforeValidate<CompensationWorksheet>, IBeforeTransition<CompensationWorksheet>
{
    private static readonly string[] Header = ["compensation_cycle", "manager", "organization_unit", "effective_date"];
    private static readonly string[] Inputs = ["merit_percent", "bonus_amount", "justification"];

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var prev = c.Previous;
        var system = Comp.IsSystem(c.User);
        var comben = Comp.IsComben(c.User);

        if (prev is null && !system)
            throw Errors.One("compensation_cycle", "Worksheets are created by the Compensation Cycle (Generate worksheets)");
        var cycle = await m.RequireAsync("CompensationCycle", d.Str("compensation_cycle"), "compensation_cycle", "Compensation cycle", ct);
        if (!system && cycle.Data.Str("status") != Comp.Planning)
            throw Errors.One("compensation_cycle", $"The compensation cycle is {cycle.Data.Str("status")}; worksheets can be changed only while it is Planning");
        if (!system && !comben && prev?.WorkflowState == Comp.WsSubmitted)
            throw Errors.One("lines", "This worksheet is with HR. Ask HR to return it if something must change");

        if (prev is not null && !system)
        {
            foreach (var f in Header) d[f] = prev.Data.GetValueOrDefault(f);
            var before = prev.Data.Rows("lines");
            var incoming = d.Rows("lines").Where(l => l.Str("employee") is not null).ToDictionary(l => l.Str("employee")!, StringComparer.Ordinal);
            var known = before.Select(l => l.Str("employee")).ToHashSet(StringComparer.Ordinal);
            if (incoming.Keys.Any(k => !known.Contains(k)) || d.Rows("lines").Any(l => l.Str("employee") is null))
                throw Errors.One("lines", "Employees come from the compensation cycle; they cannot be added here");
            if (!comben && incoming.Count != before.Count)
                throw Errors.One("lines", "Only HR can remove an employee from a worksheet");
            var merged = new List<Dictionary<string, object?>>();
            foreach (var old in before)
            {
                if (!incoming.TryGetValue(old.Str("employee")!, out var now)) continue;
                var row = new Dictionary<string, object?>(old);
                foreach (var f in Inputs) row[f] = now.GetValueOrDefault(f);
                merged.Add(row);
            }
            d["lines"] = merged;
        }

        var meritRules = Comp.Rules(cycle.Data, "merit_rules");
        var bonusRules = Comp.Rules(cycle.Data, "bonus_rules");
        var lines = d.Rows("lines");
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Dec("bonus_amount") < 0m) throw Errors.One($"lines[{i}].bonus_amount", "Bonus cannot be negative");
            Comp.RecalcLine(lines[i], meritRules, bonusRules);
        }
        var (meritPct, bonusPct) = await Comp.BudgetPercentAsync(m, cycle.Data, d.Str("organization_unit"), ct);
        Comp.Totals(d, meritPct, bonusPct);
    }

    public async Task BeforeTransitionAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var cycle = await new Masters(store, c.Doc.TenantId).RequireAsync("CompensationCycle", d.Str("compensation_cycle"), "compensation_cycle", "Compensation cycle", ct);
        if (cycle.Data.Str("status") != Comp.Planning)
            throw Errors.One("compensation_cycle", $"The compensation cycle is {cycle.Data.Str("status")}");

        if (c.ToState == Comp.WsSubmitted)
        {
            var missing = d.Rows("lines").Where(l => l.Bool("outside_guideline") && l.Str("justification") is null).Select(l => l.Str("employee_name")).ToList();
            if (missing.Count > 0)
                throw Errors.One("lines", $"Add a justification for proposals outside the guideline: {string.Join(", ", missing)}");
            if (Comp.OverBudget(d))
                throw Errors.One("merit_planned",
                    $"Over budget: merit {d.Dec("merit_planned"):N0} of {d.Dec("merit_budget"):N0}, bonus {d.Dec("bonus_planned"):N0} of {d.Dec("bonus_budget"):N0}");
        }
        if (c.ToState == Comp.WsApproved && Comp.OverBudget(d) && d.Str("hr_comments") is null)
            throw Errors.One("hr_comments", "This worksheet is over budget; explain the approval in HR comments");
    }
}

/// <summary>
/// Creates one worksheet per manager (direct reports = reports_to) for active employees of the cycle's company who
/// were hired on or before the eligibility date and have a base salary. Run again to add employees who became
/// eligible: they are added to the manager's worksheet while it is in Draft. The cycle then moves to Planning.
/// </summary>
public sealed class CompensationGenerateAction(IDocumentStore store, TimeProvider? clock = null) : IFormAction<CompensationCycle>
{
    public string ActionId => "erp.comp.generate";
    public string Label => "Generate worksheets";
    public string Icon => "table";

    public bool IsVisible(UserContext user, Document doc) =>
        Comp.IsComben(user) && doc.Data.Str("status") is Comp.Draft or Comp.Planning;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var tenant = ctx.Doc.TenantId;
        var m = new Masters(store, tenant);
        var sys = new SystemDocs(ctx.Docs, tenant);
        var cycle = ctx.Doc.Data;
        if (cycle.Date("effective_date") is not { } eff) return FormActionResult.Fail("Set the effective date first.");
        var baseComponent = (await m.SingleAsync("PayrollSettings", ct)).Str("base_salary_component");
        if (baseComponent is null) return FormActionResult.Fail("Set 'Base salary component' in Payroll Settings first.");
        var hiredBy = cycle.Date("eligibility_hire_date");

        var existing = await m.QueryAsync("CompensationWorksheet", [new FilterClause("compensation_cycle", "=", ctx.Doc.Name)], ct, take: 5000);
        var covered = existing.SelectMany(w => w.Data.Rows("lines")).Select(l => l.Str("employee")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var employees = await m.QueryAsync("Employee", [new FilterClause("company", "=", cycle.Str("company"))], ct, "full_name", "asc", 20000);

        int noManager = 0, noSalary = 0, locked = 0;
        var byManager = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var e in employees)
        {
            if (e.Data.Str("status") != "Active" || covered.Contains(e.Name)) continue;
            if (hiredBy is { } h && e.Data.Date("hire_date") is { } hd && hd > h) continue;
            var (_, basePay) = await Comp.BaseAsync(m, baseComponent, e.Name, eff, ct);
            if (basePay <= 0m) { noSalary++; continue; }
            if (e.Data.Str("reports_to") is not { } boss) { noManager++; continue; }
            if (!byManager.TryGetValue(boss, out var list)) byManager[boss] = list = [];
            list.Add(await Comp.NewLineAsync(m, cycle, e, basePay, ct));
        }

        int created = 0, extended = 0;
        var notify = new List<string>();
        foreach (var (boss, lines) in byManager)
        {
            var sheet = existing.FirstOrDefault(w => w.Data.Str("manager") == boss);
            if (sheet is null)
            {
                var unit = await m.StrAsync("Employee", boss, "organization_unit", ct);
                await sys.InsertAsync("CompensationWorksheet", new Dictionary<string, object?>
                {
                    ["compensation_cycle"] = ctx.Doc.Name, ["manager"] = boss, ["organization_unit"] = unit,
                    ["effective_date"] = D.Iso(eff), ["lines"] = lines,
                }, $"Generated from {ctx.Doc.Name}", ct);
                created++;
                notify.Add(boss);
            }
            else if (sheet.WorkflowState is null or Comp.WsDraft)
            {
                var rows = sheet.Data.Rows("lines").Select(r => new Dictionary<string, object?>(r)).Concat(lines).ToList();
                await sys.UpdateAsync("CompensationWorksheet", sheet.Name, new Dictionary<string, object?> { ["lines"] = rows }, ct);
                extended++;
            }
            else locked += lines.Count;
        }

        await Comp.SetStatusAsync(store, ctx.Docs, tenant, ctx.Doc.Name, Comp.Planning, (clock ?? TimeProvider.System).GetUtcNow(), ct);
        var users = ctx.Services?.GetService(typeof(IUserDirectory)) as IUserDirectory;
        var inbox = ctx.Services?.GetService(typeof(INotificationStore)) as INotificationStore;
        if (users is not null && inbox is not null && notify.Count > 0)
            await Notify.ToEmployeesAsync(users, inbox, tenant, notify, $"Compensation worksheet ready: {ctx.Doc.Name}",
                "Propose merit increases and bonuses for your team within the budget, then press 'Submit to HR'.",
                "CompensationWorksheet", null, $"comp:ready:{ctx.Doc.Name}", ct);

        var notes = new List<string> { $"{created} worksheets created", $"{extended} extended" };
        if (noSalary > 0) notes.Add($"{noSalary} employees skipped without a base salary");
        if (noManager > 0) notes.Add($"{noManager} skipped without a manager (reports_to)");
        if (locked > 0) notes.Add($"{locked} not added because their manager's worksheet is already submitted");
        return FormActionResult.Ok(string.Join("; ", notes) + ".");
    }
}

/// <summary>
/// Applies an approved cycle: for every line with an increase, a new Salary Assignment from the effective date that
/// copies the assignment in force and replaces the base component amount. Lines already applied are skipped, so the
/// action can be run again after fixing a problem. The cycle becomes Applied when every line went through. Bonuses
/// are not posted to payroll; they are listed in the Compensation Awards report.
/// </summary>
public sealed class CompensationApplyAction(IDocumentStore store, TimeProvider? clock = null) : IFormAction<CompensationCycle>
{
    public string ActionId => "erp.comp.apply";
    public string Label => "Apply salary changes";
    public string Icon => "check-check";
    public string Tone => "positive";

    public bool IsVisible(UserContext user, Document doc) => Comp.IsComben(user) && doc.Data.Str("status") == Comp.Planning;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        var tenant = ctx.Doc.TenantId;
        var m = new Masters(store, tenant);
        var sys = new SystemDocs(ctx.Docs, tenant);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var eff = ctx.Doc.Data.Date("effective_date");
        if (eff is null) return FormActionResult.Fail("Set the effective date first.");
        var baseComponent = (await m.SingleAsync("PayrollSettings", ct)).Str("base_salary_component");
        if (baseComponent is null) return FormActionResult.Fail("Set 'Base salary component' in Payroll Settings first.");

        var sheets = await m.QueryAsync("CompensationWorksheet", [new FilterClause("compensation_cycle", "=", ctx.Doc.Name)], ct, take: 5000);
        if (sheets.Count == 0) return FormActionResult.Fail("There are no worksheets. Generate them first.");
        var open = sheets.Where(s => s.WorkflowState != Comp.WsApproved).ToList();
        if (open.Count > 0)
            return FormActionResult.Fail($"{open.Count} worksheets are not approved yet: {string.Join(", ", open.Take(10).Select(s => s.Data.Str("manager_name") ?? s.Name))}.");

        int created = 0;
        var problems = new List<string>();
        foreach (var sheet in sheets)
        {
            var lines = sheet.Data.Rows("lines");
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var emp = line.Str("employee")!;
                if (line.Dec("merit_amount") <= 0m || line.Str("salary_assignment") is not null) continue;
                var (current, basePay) = await Comp.BaseAsync(m, baseComponent, emp, eff.Value, ct);
                if (current is null) { problems.Add($"{emp}: no salary assignment in force"); continue; }
                if (basePay != line.Dec("current_base"))
                {
                    problems.Add($"{emp}: base salary changed since the worksheet ({basePay:N0} now, {line.Dec("current_base"):N0} planned); adjust manually");
                    continue;
                }
                var earnings = Payroll.CopyRows(current.Data.Rows("earnings"));
                foreach (var r in earnings.Where(r => r.Str("salary_component") == baseComponent)) r["amount"] = 0m;
                var baseRow = earnings.FirstOrDefault(r => r.Str("salary_component") == baseComponent);
                if (baseRow is null) earnings.Insert(0, baseRow = new() { ["salary_component"] = baseComponent });
                baseRow["amount"] = line.Dec("new_base");
                earnings.RemoveAll(r => r.Str("salary_component") == baseComponent && !ReferenceEquals(r, baseRow));

                try
                {
                    var sa = await sys.InsertAsync("SalaryAssignment", new Dictionary<string, object?>
                    {
                        ["employee"] = emp, ["company"] = current.Data.Str("company"), ["from_date"] = D.Iso(eff.Value), ["is_active"] = true,
                        ["earnings"] = earnings, ["deductions"] = Payroll.CopyRows(current.Data.Rows("deductions")),
                        ["bpjs_employment"] = current.Data.Bool("bpjs_employment"), ["pension_member"] = current.Data.Bool("pension_member"),
                        ["bpjs_health"] = current.Data.Bool("bpjs_health"),
                        ["remarks"] = $"Merit {line.Dec("merit_percent"):0.##}% from {ctx.Doc.Name} ({sheet.Name})",
                    }, $"Created from Compensation Cycle {ctx.Doc.Name}", ct);
                    var index = i;
                    await MultiRater.PatchAsync(store, tenant, "CompensationWorksheet", sheet.Name, doc =>
                    {
                        var rows = doc.Data.Rows("lines").Select(r => new Dictionary<string, object?>(r)).ToList();
                        if (index >= rows.Count || rows[index].Str("employee") != emp) return false;
                        rows[index]["salary_assignment"] = sa.Name;
                        doc.Data["lines"] = rows;
                        return true;
                    }, $"Salary assignment {sa.Name} created", now, ct);
                    created++;
                }
                catch (Exception ex) when (ex is ValidationFailedException or InvalidPayloadException or PermissionDeniedException)
                {
                    problems.Add($"{emp}: {ex.Message}");
                }
            }
        }

        if (problems.Count == 0) await Comp.SetStatusAsync(store, ctx.Docs, tenant, ctx.Doc.Name, Comp.Applied, now, ct, stampApplied: true);
        else await sys.TouchAsync("CompensationCycle", ctx.Doc.Name, ct);

        return problems.Count == 0
            ? FormActionResult.Ok($"{created} salary assignments created. The cycle is Applied. Bonuses are listed in the Compensation Awards report for payroll.")
            : FormActionResult.Fail($"{created} salary assignments created; {problems.Count} need attention: {string.Join("; ", problems.Take(10))}. Fix them and run again.");
    }
}

/// <summary>Cancels a cycle that has not been applied. Worksheets stay for reference and can no longer be changed.</summary>
public sealed class CompensationCancelAction(IDocumentStore store, TimeProvider? clock = null) : IFormAction<CompensationCycle>
{
    public string ActionId => "erp.comp.cancel";
    public string Label => "Cancel cycle";
    public string Icon => "x";
    public string Tone => "negative";

    public bool IsVisible(UserContext user, Document doc) => Comp.IsComben(user) && doc.Data.Str("status") is Comp.Draft or Comp.Planning;

    public async Task<FormActionResult> ExecuteAsync(FormActionContext ctx, CancellationToken ct)
    {
        if (!IsVisible(ctx.User, ctx.Doc)) return FormActionResult.Fail("Not available.");
        await Comp.SetStatusAsync(store, ctx.Docs, ctx.Doc.TenantId, ctx.Doc.Name, Comp.Cancelled, (clock ?? TimeProvider.System).GetUtcNow(), ct);
        return FormActionResult.Ok("The cycle is cancelled.");
    }
}
