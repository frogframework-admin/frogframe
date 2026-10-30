using Framework.Metadata;

namespace Framework.Security;

/// <summary>
/// Identitas tervalidasi. TenantId dan role selalu berasal dari identitas (token/sesi), tidak pernah dari payload client.
/// </summary>
public sealed record UserContext(
    string Id,
    string FullName,
    string TenantId,
    IReadOnlySet<string> Roles,
    IReadOnlyDictionary<string, string> Attributes)
{
    public const string SystemManager = "System Manager";
    public bool HasRole(string role) => Roles.Contains(role) || Roles.Contains(SystemManager) && role != "*";
    public string? Attr(string key) => Attributes.TryGetValue(key, out var v) ? v : null;
}

public sealed class PermissionDeniedException(string message) : Exception(message);

/// <summary>Cakupan baris yang boleh diakses. Diterjemahkan oleh store menjadi filter query.</summary>
public abstract record RowScope
{
    public static readonly RowScope All = new AllRows();
    public static readonly RowScope None = new NoRows();
    public sealed record AllRows : RowScope;
    public sealed record NoRows : RowScope;
    /// <summary>Gabungan OR dari beberapa kondisi kepemilikan / kecocokan field.</summary>
    public sealed record AnyOf(IReadOnlyList<ScopeCondition> Conditions) : RowScope;
}

public sealed record ScopeCondition(string Field, string Value);

public interface IPermissionService
{
    bool Can(UserContext user, CompiledDocType meta, string action);
    void Demand(UserContext user, CompiledDocType meta, string action);
    RowScope ScopeFor(UserContext user, CompiledDocType meta, string action);
    bool InScope(UserContext user, CompiledDocType meta, string action, string owner, IReadOnlyDictionary<string, object?> data);
    bool CanReadField(UserContext user, CompiledDocType meta, CompiledField field);
    bool CanWriteField(UserContext user, CompiledDocType meta, CompiledField field);
}

public sealed class PermissionService : IPermissionService
{
    private static IEnumerable<PermissionDef> Rules(UserContext user, CompiledDocType meta, string action, int level = 0) =>
        meta.Def.Permissions.Where(p => p.PermLevel == level && p.Actions.Contains(action) &&
            (user.Roles.Contains(p.Role) || user.Roles.Contains(UserContext.SystemManager)));

    public bool Can(UserContext user, CompiledDocType meta, string action) =>
        user.Roles.Contains(UserContext.SystemManager) || Rules(user, meta, action).Any();

    public void Demand(UserContext user, CompiledDocType meta, string action)
    {
        if (!Can(user, meta, action))
            throw new PermissionDeniedException($"Anda tidak memiliki izin '{action}' untuk {meta.Title}");
    }

    public RowScope ScopeFor(UserContext user, CompiledDocType meta, string action)
    {
        if (user.Roles.Contains(UserContext.SystemManager)) return RowScope.All;
        var rules = Rules(user, meta, action).ToList();
        if (rules.Count == 0) return RowScope.None;
        if (rules.Any(r => r.Scope == "all")) return RowScope.All;

        var conditions = new List<ScopeCondition>();
        foreach (var r in rules)
        {
            if (r.Scope == "owner") conditions.Add(new(StandardFields.Owner, user.Id));
            else if (r.Scope.StartsWith("match:"))
            {
                var (field, attr) = (r.Scope[6..].Split('=')[0], r.Scope[6..].Split('=')[1]);
                // Atribut user yang tidak ada berarti tidak ada baris yang cocok (fail closed).
                if (user.Attr(attr) is { } v) conditions.Add(new(field, v));
            }
        }
        return conditions.Count == 0 ? RowScope.None : new RowScope.AnyOf(conditions);
    }

    public bool InScope(UserContext user, CompiledDocType meta, string action, string owner, IReadOnlyDictionary<string, object?> data) =>
        ScopeFor(user, meta, action) switch
        {
            RowScope.AllRows => true,
            RowScope.AnyOf any => any.Conditions.Any(c => c.Field == StandardFields.Owner
                ? c.Value == owner
                : data.TryGetValue(c.Field, out var v) && Convert.ToString(v) == c.Value),
            _ => false
        };

    public bool CanReadField(UserContext user, CompiledDocType meta, CompiledField field) =>
        field.Def.PermLevel == 0 || user.Roles.Contains(UserContext.SystemManager) ||
        Rules(user, meta, DocActions.Read, field.Def.PermLevel).Any();

    public bool CanWriteField(UserContext user, CompiledDocType meta, CompiledField field) =>
        field.Def.PermLevel == 0 || user.Roles.Contains(UserContext.SystemManager) ||
        Rules(user, meta, DocActions.Write, field.Def.PermLevel).Any();
}
