using Framework.Metadata;
using Framework.Security;

namespace Framework.Web.Admin;

public static class MetadataRoles
{
    /// <summary>Role yang dikenal: dari permission semua DocType, role di semua workflow, dan System Manager.</summary>
    /// <param name="master">Master role tenant. Bila diisi, pilihan HANYA role master; nilai lama yang tidak ada di master
    /// tetap dimasukkan lewat <paramref name="extra"/> agar terlihat dan bisa diperbaiki (publish akan menolaknya).</param>
    public static IReadOnlyList<string> Known(MetadataSnapshot snapshot, IEnumerable<string>? extra = null, IEnumerable<string>? master = null)
    {
        if (master is not null)
            return master.Concat(extra ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Order().ToList();
        return KnownFromMetadata(snapshot, extra);
    }

    private static IReadOnlyList<string> KnownFromMetadata(MetadataSnapshot snapshot, IEnumerable<string>? extra) =>
        snapshot.DocTypes.Values.SelectMany(d => d.Def.Permissions.Select(p => p.Role))
            .Concat(snapshot.Workflows.Values.SelectMany(w => w.Def.Transitions.SelectMany(t => t.AllowedRoles)))
            .Append("Employee").Append(UserContext.SystemManager)
            .Concat(extra ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Order().ToList();
}
