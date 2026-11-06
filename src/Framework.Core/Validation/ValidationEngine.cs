using System.Globalization;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Expressions;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Validation;

public readonly record struct FieldState(bool Visible, bool Required, bool ReadOnly);

/// <summary>
/// Evaluasi ekspresi metadata terhadap data dokumen. Dipakai bersama oleh server (otoritatif)
/// dan renderer Blazor (umpan balik instan) sehingga semantiknya identik.
/// </summary>
public sealed class DocumentEvaluator(ExpressionEvaluator evaluator, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public static Dictionary<string, object?> UserScope(UserContext user)
    {
        var u = new Dictionary<string, object?> { ["id"] = user.Id, ["name"] = user.FullName, ["roles"] = user.Roles.Cast<object?>().ToList() };
        foreach (var (k, v) in user.Attributes) u[k] = v;
        return u;
    }

    public EvalContext Context(IReadOnlyDictionary<string, object?> data, UserContext user, IReadOnlyDictionary<string, object?>? parent = null)
    {
        var extra = new Dictionary<string, object?> { ["user"] = UserScope(user) };
        if (parent is not null) extra["parent"] = parent;
        return new EvalContext
        {
            Scope = new DictionaryScope(data, new DictionaryScope(extra)),
            Roles = user.Roles,
            Today = Today,
        };
    }

    public bool Bool(ParsedExpression? expr, bool fallback, IReadOnlyDictionary<string, object?> data, UserContext user,
        IReadOnlyDictionary<string, object?>? parent = null)
    {
        if (expr is null) return fallback;
        try { return evaluator.EvaluateBool(expr, Context(data, user, parent)); }
        catch (ExpressionException) { return fallback; }
    }

    public object? Value(ParsedExpression expr, IReadOnlyDictionary<string, object?> data, UserContext user,
        IReadOnlyDictionary<string, object?>? parent = null) => evaluator.Evaluate(expr, Context(data, user, parent));

    public FieldState StateOf(CompiledField f, IReadOnlyDictionary<string, object?> data, UserContext user,
        IReadOnlyDictionary<string, object?>? parent = null)
    {
        var visible = Bool(f.VisibleWhen, true, data, user, parent);
        var required = visible && Bool(f.RequiredWhen, false, data, user, parent);
        var readOnly = f.IsDerived || Bool(f.ReadOnlyWhen, false, data, user, parent);
        return new FieldState(visible, required, readOnly);
    }

    public object? DefaultFor(CompiledField f, UserContext user) => f.DynamicDefault switch
    {
        "$today" => Today.ToString("yyyy-MM-dd"),
        "$user" => user.Id,
        "$employee" => user.Attr("employee"),
        _ => f.StaticDefault
    };

    public void ApplyDefaults(CompiledDocType meta, Dictionary<string, object?> data, UserContext user)
    {
        foreach (var f in meta.Fields)
        {
            if (f.Def.Type == "Table") { if (!data.ContainsKey(f.Name)) data[f.Name] = new List<Dictionary<string, object?>>(); continue; }
            if (!data.TryGetValue(f.Name, out var v) || Values.IsEmpty(v))
            {
                var d = DefaultFor(f, user);
                if (d is not null || !data.ContainsKey(f.Name)) data[f.Name] = d;
            }
        }
    }

    /// <summary>Menghitung ulang field compute: baris child dulu, lalu dokumen induk.</summary>
    public List<FieldError> ApplyCompute(CompiledDocType meta, Dictionary<string, object?> data, UserContext user,
        Func<string, CompiledDocType> resolve)
    {
        var errors = new List<FieldError>();
        foreach (var table in meta.TableFields)
        {
            var childMeta = resolve(table.Def.ChildDocType!);
            if (data.GetValueOrDefault(table.Name) is not List<Dictionary<string, object?>> rows) continue;
            for (var i = 0; i < rows.Count; i++)
                foreach (var f in childMeta.ComputeOrder)
                    errors.AddRange(ComputeOne(f, rows[i], user, data, $"{table.Name}[{i}].{f.Name}"));
        }
        foreach (var f in meta.ComputeOrder)
            errors.AddRange(ComputeOne(f, data, user, null, f.Name));
        return errors;
    }

    private IEnumerable<FieldError> ComputeOne(CompiledField f, Dictionary<string, object?> data, UserContext user,
        IReadOnlyDictionary<string, object?>? parent, string path)
    {
        try
        {
            var raw = evaluator.Evaluate(f.Compute!, Context(data, user, parent));
            var n = f.Type.Normalize(raw);
            if (!n.Ok) return [new FieldError(path, $"Hasil compute {f.Label} {n.Error}")];
            data[f.Name] = n.Value;
            return [];
        }
        catch (ExpressionException ex)
        {
            data[f.Name] = null;
            return [new FieldError(path, $"{f.Label} tidak dapat dihitung: {ex.Message}")];
        }
    }
}

public sealed class ValidationEngine(DocumentEvaluator evaluator, IValidatorRegistry validators)
{
    public async Task<List<FieldError>> ValidateAsync(CompiledDocType meta, Dictionary<string, object?> data, UserContext user,
        Func<string, CompiledDocType> resolve, IDocumentLookup lookup, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        await ValidateFieldsAsync(meta, data, null, user, "", resolve, lookup, errors, ct);
        return errors;
    }

    private async Task ValidateFieldsAsync(CompiledDocType meta, Dictionary<string, object?> data,
        IReadOnlyDictionary<string, object?>? parent, UserContext user, string prefix,
        Func<string, CompiledDocType> resolve, IDocumentLookup lookup, List<FieldError> errors, CancellationToken ct)
    {
        foreach (var f in meta.Fields)
        {
            var path = prefix + f.Name;
            var value = data.GetValueOrDefault(f.Name);
            var state = evaluator.StateOf(f, data, user, parent);

            if (f.Def.Type == "Table")
            {
                var rows = value as List<Dictionary<string, object?>> ?? [];
                if (state.Required && rows.Count == 0) errors.Add(new(path, $"{f.Label} minimal berisi satu baris"));
                var childMeta = resolve(f.Def.ChildDocType!);
                for (var i = 0; i < rows.Count; i++)
                    await ValidateFieldsAsync(childMeta, rows[i], data, user, $"{path}[{i}].", resolve, lookup, errors, ct);
                continue;
            }

            if (Values.IsEmpty(value))
            {
                if (state.Required) errors.Add(new(path, $"{f.Label} wajib diisi"));
                continue;
            }

            if (f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal))
            {
                var d = Values.ToDecimal(value);
                if (f.Def.Min is { } min && d < min) errors.Add(new(path, $"{f.Label} minimal {min.ToString(CultureInfo.InvariantCulture)}"));
                if (f.Def.Max is { } max && d > max) errors.Add(new(path, $"{f.Label} maksimal {max.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (value is string s)
            {
                if (f.Def.MaxLength is { } ml && s.Length > ml) errors.Add(new(path, $"{f.Label} maksimal {ml} karakter"));
                if (f.Pattern is not null)
                {
                    bool ok;
                    try { ok = f.Pattern.IsMatch(s); } catch (RegexMatchTimeoutException) { ok = false; }
                    if (!ok) errors.Add(new(path, f.Def.PatternMessage ?? $"Format {f.Label} tidak valid"));
                }
                if (f.Def.Type == "Select" && f.Def.Options is { } opts && !opts.Contains(s))
                    errors.Add(new(path, $"{f.Label} harus salah satu dari: {string.Join(", ", opts)}"));
                if (f.Def.Type == "Link")
                {
                    var linked = await lookup.GetAsync(f.Def.LinkTo!, s, ct);
                    if (linked is null)
                        errors.Add(new(path, $"{f.Label}: {f.Def.LinkTo} '{s}' tidak ditemukan"));
                    else if (f.Def.LinkFilters.FirstOrDefault(lf => !LinkFilters.Matches(linked, lf)) is { } failed)
                        errors.Add(new(path, $"{f.Label}: {f.Def.LinkTo} '{s}' tidak memenuhi syarat ({LinkFilters.Describe(failed)})"));
                }
            }

            foreach (var vName in f.Def.Validators ?? [])
            {
                if (!validators.TryGet(vName, out var validator)) { errors.Add(new(path, $"Validator '{vName}' tidak terdaftar")); continue; }
                if (validator.Validate(value, f) is { } msg) errors.Add(new(path, msg));
            }
        }

        foreach (var rule in meta.Rules)
        {
            bool ok;
            try { ok = Values.Truthy(evaluator.Value(rule.Expr, data, user, parent)); }
            catch (ExpressionException ex) { errors.Add(new(prefix + (rule.Def.Field ?? "_doc"), $"Aturan '{rule.Def.Name}' gagal dievaluasi: {ex.Message}")); continue; }
            if (!ok) errors.Add(new(prefix + (rule.Def.Field ?? "_doc"), rule.Def.Message));
        }
    }
}
