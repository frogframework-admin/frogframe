using Framework.Metadata;
using Framework.Security;

namespace Framework.Web.Admin;

public static class MetadataRoles
{
    /// <summary>Role yang dikenal: dari permission semua DocType, role di semua workflow, dan System Manager.</summary>
    public static IReadOnlyList<string> Known(MetadataSnapshot snapshot, IEnumerable<string>? extra = null) =>
        snapshot.DocTypes.Values.SelectMany(d => d.Def.Permissions.Select(p => p.Role))
            .Concat(snapshot.Workflows.Values.SelectMany(w => w.Def.Transitions.SelectMany(t => t.AllowedRoles)))
            .Append("Employee").Append(UserContext.SystemManager)
            .Concat(extra ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Order().ToList();
}
