using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Framework.Expressions;

namespace Apps.Erp.Hooks;

/// <summary>Salary component calculated by formula (Salary Component with calculation type "Formula").</summary>
internal sealed record FormulaComponent(string Name, string Variable, string Type, string Formula, string? Condition, string? Company = null);

/// <summary>One row of a Salary Rate Table. Empty key = any key; range is From ≤ x &lt; To (empty = no bound).</summary>
internal sealed record RateRow(string? Key, string? Key2, decimal? From, decimal? To, decimal Value, DateOnly? ValidFrom = null);

internal sealed record RateTable(string Code, string? Company, IReadOnlyList<RateRow> Rows);

/// <summary>Result of one formula component; Note records the formula and the values it used (audit trail on the payslip).</summary>
internal sealed record FormulaResult(string Component, string Type, decimal Amount, string Note);

/// <summary>Values a formula can read: other components by variable name, emp.*, att.*, period.*, and rate tables.</summary>
internal sealed class FormulaInputs
{
    /// <summary>Variable name → amount. Formula results are written back so later formulas can use them.</summary>
    public required Dictionary<string, decimal> Components { get; init; }
    public IReadOnlyDictionary<string, object?> Emp { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> Att { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, object?> Period { get; init; } = new Dictionary<string, object?>();
    public required DateOnly PeriodEnd { get; init; }
    public Func<string, RateTable?> Tables { get; init; } = _ => null;
}

/// <summary>
/// Payroll formulas (docs/guides/payroll-formulas.md). Uses the framework's restricted expression language (no eval, no reflection)
/// with payroll functions: if, min, max, floor, ceil, div, lookup, lookup_range. Pure: no I/O, so it can be tested on its own.
/// Note: if() evaluates both branches, so a division that may be by zero should use div(a, b[, fallback]).
/// </summary>
internal static class PayrollFormulas
{
    public static readonly ExpressionLimits Limits = new(MaxLength: 2000, MaxDepth: 32, MaxNodes: 500, MaxSteps: 20_000);
    public const string EmpScope = "emp", AttScope = "att", PeriodScope = "period";

    private static readonly Regex VariablePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,29}$", RegexOptions.CultureInvariant);

    /// <summary>Names that cannot be used as a component variable (scopes, literals, keywords, and function names).</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        EmpScope, AttScope, PeriodScope, "true", "false", "null", "in", "and", "or", "not",
        "if", "min", "max", "floor", "ceil", "div", "round", "abs", "lookup", "lookup_range", "coalesce", "sum", "len", "empty", "today", "has_role",
    };

    /// <summary>att.* names: the payslip's attendance summary and payable-day counts.</summary>
    public static readonly IReadOnlySet<string> AttFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "on_time_days", "late_days", "late_minutes", "early_out_days", "early_out_minutes", "no_clock_in_days", "no_clock_out_days",
        "absent_days", "invalid_days", "time_off_paid_days", "time_off_deduction_days", "day_off_days", "unclassified_days", "present_days",
        "working_days", "payable_days", "unpaid_leave_days",
    };

    /// <summary>period.* names.</summary>
    public static readonly IReadOnlySet<string> PeriodFields = new HashSet<string>(StringComparer.Ordinal) { "year", "month", "days", "start", "end" };

    /// <summary>emp.* names added to the Employee fields.</summary>
    public static readonly IReadOnlySet<string> EmpDerivedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "grade_level", "job_level", "is_manager", "years_of_service", "months_of_service", "age", "children", "tax_dependents",
    };

    /// <summary>Paths in an expression that do not name a known variable, emp.*, att.* or period.* field.</summary>
    public static IReadOnlyList<string> UnknownNames(ParsedExpression e, IReadOnlySet<string> variables, IReadOnlySet<string> empFields) =>
        e.Paths.Where(p =>
        {
            var s = p.Split('.');
            return s[0] switch
            {
                EmpScope => s.Length != 2 || !(empFields.Contains(s[1]) || EmpDerivedFields.Contains(s[1])),
                AttScope => s.Length != 2 || !AttFields.Contains(s[1]),
                PeriodScope => s.Length != 2 || !PeriodFields.Contains(s[1]),
                _ => s.Length != 1 || !variables.Contains(s[0]),
            };
        }).OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static readonly FunctionRegistry ParseFunctions = CreateFunctions(_ => null, DateOnly.MinValue);
    private static readonly ConcurrentDictionary<string, ParsedExpression> Cache = new(StringComparer.Ordinal);

    public static bool IsValidVariable(string? name) => name is not null && VariablePattern.IsMatch(name) && !Reserved.Contains(name);

    /// <summary>Parses (and caches) a formula or condition. Throws <see cref="ExpressionException"/> on syntax errors.</summary>
    public static ParsedExpression Parse(string source)
    {
        if (Cache.TryGetValue(source, out var hit)) return hit;
        var parsed = new ExpressionParser(ParseFunctions, Limits).Parse(source);
        if (Cache.Count > 2000) Cache.Clear();
        Cache[source] = parsed;
        return parsed;
    }

    /// <summary>Root names a component's formula and condition refer to (component variables or emp/att/period).</summary>
    public static IReadOnlySet<string> References(FormulaComponent c)
    {
        var refs = new HashSet<string>(Parse(c.Formula).RootNames, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(c.Condition)) refs.UnionWith(Parse(c.Condition).RootNames);
        return refs;
    }

    /// <summary>Calculation order: a component comes after every formula component it refers to; ties by name. Loops are rejected.</summary>
    public static IReadOnlyList<FormulaComponent> Order(IReadOnlyList<FormulaComponent> components)
    {
        if (components.GroupBy(c => c.Variable, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } dup)
            throw new ExpressionException($"Variable {dup.Key} is used by more than one component: {string.Join(", ", dup.Select(c => c.Name))}");
        var byVariable = components.ToDictionary(c => c.Variable, c => c.Name, StringComparer.Ordinal);
        var deps = components.ToDictionary(c => c.Name,
            c => References(c).Where(byVariable.ContainsKey).Select(v => byVariable[v]).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        var ordered = new List<FormulaComponent>(components.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var pending = components.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(c => deps[c.Name].All(placed.Contains))
                ?? throw new ExpressionException($"Formulas refer to each other in a loop: {string.Join(", ", pending.Select(c => c.Name))}");
            ordered.Add(ready);
            placed.Add(ready.Name);
            pending.Remove(ready);
        }
        return ordered;
    }

    /// <summary>
    /// Evaluates the components in the given order. A component whose condition is false, or whose result is 0 or negative,
    /// gives no payslip row (its variable is 0). Errors are prefixed with the component name.
    /// </summary>
    public static List<FormulaResult> Evaluate(IReadOnlyList<FormulaComponent> ordered, FormulaInputs input)
    {
        var evaluator = new ExpressionEvaluator(CreateFunctions(input.Tables, input.PeriodEnd));
        var scope = new LiveScope(input);
        var results = new List<FormulaResult>();
        foreach (var c in ordered)
        {
            object? Run(string source)
            {
                try { return evaluator.Evaluate(Parse(source), new EvalContext { Scope = scope, Limits = Limits, Today = input.PeriodEnd }); }
                catch (ExpressionException ex) { throw new ExpressionException($"{c.Name}: {ex.Message}"); }
                catch (OverflowException) { throw new ExpressionException($"{c.Name}: a number in the calculation is too large"); }
            }

            if (!string.IsNullOrWhiteSpace(c.Condition) && !Values.Truthy(Run(c.Condition)))
            {
                input.Components[c.Variable] = 0m;
                continue;
            }
            var raw = Run(c.Formula);
            var value = raw is bool ? null : Values.ToDecimal(raw);
            if (value is null) throw new ExpressionException($"{c.Name}: the formula gave '{Show(raw)}', not a number");
            var amount = Math.Max(0m, PayrollCalc.Rupiah(value.Value));
            input.Components[c.Variable] = amount;
            if (amount > 0) results.Add(new FormulaResult(c.Name, c.Type, amount, Note(c, amount, evaluator, scope, input.PeriodEnd)));
        }
        return results;
    }

    /// <summary>Whole months from <paramref name="from"/> to <paramref name="to"/>; a month ending on the last day of a shorter month counts as complete.</summary>
    public static int CompletedMonths(DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day && to.Day != DateTime.DaysInMonth(to.Year, to.Month)) months--;
        return Math.Max(0, months);
    }

    /// <summary>
    /// Tax allowance (gross-up): the allowance T that equals the tax due when T itself is part of the taxable gross.
    /// taxFor is non-decreasing in T with a marginal rate below 100%, so iterating from 0 converges in a few steps.
    /// </summary>
    public static decimal SolveGrossUp(Func<decimal, decimal> taxFor, int maxIterations = 100)
    {
        var t = 0m;
        for (var i = 0; i < maxIterations; i++)
        {
            var next = Math.Max(0m, taxFor(t));
            if (next == t) return t;
            t = next;
        }
        throw new ExpressionException($"The tax allowance did not settle after {maxIterations} rounds");
    }

    /// <summary>
    /// Value of a rate table. Only rows of the latest version (valid from ≤ period end) are used. Empty key/key2 on a row
    /// matches any key; the most specific matching row wins. With a number, the row's range From ≤ number &lt; To must hold.
    /// </summary>
    public static decimal Lookup(RateTable table, DateOnly periodEnd, decimal? number, string? key, string? key2)
    {
        var dated = table.Rows.Where(r => r.ValidFrom is null || r.ValidFrom <= periodEnd).ToList();
        if (dated.Count == 0) throw new ExpressionException($"Salary rate table {table.Code} has no rows valid on {D.Iso(periodEnd)}");
        var version = dated.Max(r => r.ValidFrom ?? DateOnly.MinValue);
        var matches = dated
            .Where(r => (r.ValidFrom ?? DateOnly.MinValue) == version)
            .Where(r => KeyMatches(r.Key, key) && KeyMatches(r.Key2, key2))
            .Where(r => number is null || ((r.From is null || number >= r.From) && (r.To is null || number < r.To)))
            .OrderByDescending(Specificity)
            .ToList();
        var what = string.Join(", ", new[] { key is null ? null : $"key '{key}'", key2 is null ? null : $"key 2 '{key2}'", number is null ? null : $"value {number:0.##}" }
            .OfType<string>());
        if (matches.Count == 0)
            throw new ExpressionException($"Salary rate table {table.Code} has no row for {what} (add a row with an empty key as the default)");
        if (matches.Count > 1 && Specificity(matches[0]) == Specificity(matches[1]))
            throw new ExpressionException($"Salary rate table {table.Code} has more than one row for {what}");
        return matches[0].Value;
    }

    /// <summary>Normalized key text: numbers without trailing zeros (3.0 → "3"), strings trimmed.</summary>
    public static string? KeyText(object? v) => v switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        bool b => b ? "true" : "false",
        _ when Values.ToDecimal(v) is { } d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    public static FunctionRegistry CreateFunctions(Func<string, RateTable?> tables, DateOnly periodEnd)
    {
        var r = FunctionRegistry.CreateDefault();
        r.Register(new("if", 3, 3, (a, _) => Values.Truthy(a[0]) ? a[1] : a[2]));
        r.Register(new("min", 1, 10, (a, _) => a.Select(Num).Min()));
        r.Register(new("max", 1, 10, (a, _) => a.Select(Num).Max()));
        r.Register(new("floor", 1, 2, (a, _) => ToMultiple(a, Math.Floor)));
        r.Register(new("ceil", 1, 2, (a, _) => ToMultiple(a, Math.Ceiling)));
        // div(a, b[, fallback]): a / b, or fallback (default 0) when b is 0. if() evaluates both branches, so it cannot guard a division.
        r.Register(new("div", 2, 3, (a, _) =>
        {
            var divisor = Num(a[1]);
            return divisor == 0 ? (a.Count > 2 ? Num(a[2]) : 0m) : Num(a[0]) / divisor;
        }));
        r.Register(new("lookup", 2, 3, (a, _) =>
            Lookup(Table(tables, a[0]), periodEnd, null, KeyText(a[1]), a.Count > 2 ? KeyText(a[2]) : null)));
        r.Register(new("lookup_range", 2, 4, (a, _) =>
            Lookup(Table(tables, a[0]), periodEnd, Num(a[1]), a.Count > 2 ? KeyText(a[2]) : null, a.Count > 3 ? KeyText(a[3]) : null)));
        return r;
    }

    private static RateTable Table(Func<string, RateTable?> tables, object? name) =>
        (name is string s ? tables(s) : null)
        ?? throw new ExpressionException($"Salary rate table '{Show(name)}' not found (or inactive, or set up for another company)");

    private static decimal Num(object? v) =>
        v is null ? 0m : Values.ToDecimal(v) ?? throw new ExpressionException($"'{Show(v)}' is not a number");

    /// <summary>floor(x) / ceil(x), or to a multiple: floor(x, 1000) rounds down to whole thousands.</summary>
    private static decimal ToMultiple(IReadOnlyList<object?> a, Func<decimal, decimal> round)
    {
        var x = Num(a[0]);
        var multiple = a.Count > 1 ? Num(a[1]) : 1m;
        if (multiple <= 0) throw new ExpressionException("The multiple must be greater than 0");
        return round(x / multiple) * multiple;
    }

    private static bool KeyMatches(string? rowKey, string? key) =>
        string.IsNullOrWhiteSpace(rowKey) || (key is not null && string.Equals(rowKey.Trim(), key, StringComparison.OrdinalIgnoreCase));

    private static int Specificity(RateRow r) => (string.IsNullOrWhiteSpace(r.Key) ? 0 : 1) + (string.IsNullOrWhiteSpace(r.Key2) ? 0 : 1);

    private static string Note(FormulaComponent c, decimal amount, ExpressionEvaluator evaluator, IEvalScope scope, DateOnly periodEnd)
    {
        var paths = new SortedSet<string>(Parse(c.Formula).Paths, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(c.Condition)) paths.UnionWith(Parse(c.Condition).Paths);
        var inputs = paths.Select(p =>
        {
            try { return $"{p}={Show(evaluator.Evaluate(Parse(p), new EvalContext { Scope = scope, Limits = Limits, Today = periodEnd }))}"; }
            catch (ExpressionException) { return $"{p}=?"; }
        });
        var note = $"{c.Formula} = {amount.ToString("#,##0", CultureInfo.InvariantCulture)}";
        if (paths.Count > 0) note += $" [{string.Join("; ", inputs)}]";
        return note.Length <= 1000 ? note : note[..997] + "...";
    }

    private static string Show(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(Show)) + "]",
        _ when Values.ToDecimal(v) is { } d => d.ToString("#,##0.##", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Reads component amounts as they are updated, so a formula sees the results of the formulas before it.</summary>
    private sealed class LiveScope(FormulaInputs input) : IEvalScope
    {
        public bool TryResolve(string name, out object? value)
        {
            if (input.Components.TryGetValue(name, out var amount)) { value = amount; return true; }
            value = name switch
            {
                EmpScope => input.Emp,
                AttScope => input.Att,
                PeriodScope => input.Period,
                _ => null,
            };
            return value is not null;
        }
    }
}
