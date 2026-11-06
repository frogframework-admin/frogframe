using System.Collections.Concurrent;
using Framework.Metadata;
using Framework.Navigation;

namespace Framework.Security;

/// <summary>
/// Satu sumber kebenaran untuk nama role: master role per tenant (IRoleStore). Permission DocType, transisi workflow,
/// dan role di akun pengguna harus merujuk ke nama yang ada di sini. Metadata yang memakai role tak dikenal ditolak
/// saat publish (lewat MetadataCompiler); role yang masih dipakai tidak bisa di-rename atau dihapus.
/// </summary>
public sealed class RoleCatalog(IRoleStore roles, IUserAccountRepository accounts, INavigationMenuStore navigation)
{
    private readonly ConcurrentDictionary<string, (IReadOnlySet<string> Data, DateTimeOffset Exp)> _moduleCache = new();
    private readonly ConcurrentDictionary<string, (IReadOnlySet<string> Data, DateTimeOffset Exp)> _roleCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public void Invalidate(string tenantId)
    {
        _moduleCache.TryRemove(tenantId, out _);
        _roleCache.TryRemove(tenantId, out _);
    }

    /// <summary>Role dan kode modul master tenant: daftar yang diizinkan untuk diacu metadata saat publish.</summary>
    public MetadataScope Scope(string tenantId) => new(Names(tenantId), ModuleCodes(tenantId));

    /// <summary>Kode semua modul aktif tenant (sinkron; daftar modul kecil).</summary>
    public IReadOnlySet<string> ModuleCodes(string tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_moduleCache.TryGetValue(tenantId, out var cached) && cached.Exp > now)
            return cached.Data;

        var list = Task.Run(async () => await navigation.ListModulesAsync(tenantId).ConfigureAwait(false))
            .ConfigureAwait(false).GetAwaiter().GetResult()
            .Select(m => m.Code).Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.Ordinal);

        _moduleCache[tenantId] = (list, now.Add(CacheTtl));
        return list;
    }

    /// <summary>Nama semua role master tenant. Dipakai editor (sinkron; query-nya kecil).</summary>
    public IReadOnlySet<string> Names(string tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_roleCache.TryGetValue(tenantId, out var cached) && cached.Exp > now)
            return cached.Data;

        var set = Task.Run(async () => await NamesAsync(tenantId).ConfigureAwait(false))
            .ConfigureAwait(false).GetAwaiter().GetResult();

        _roleCache[tenantId] = (set, now.Add(CacheTtl));
        return set;
    }

    public async Task<IReadOnlySet<string>> NamesAsync(string tenantId, CancellationToken ct = default) =>
        (await roles.ListAsync(tenantId, ct).ConfigureAwait(false)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Role di akun yang tidak ada di master tenant. Kosong = valid.</summary>
    public async Task<IReadOnlyList<string>> UnknownAccountRolesAsync(string tenantId, IEnumerable<string> accountRoles, CancellationToken ct = default)
    {
        var known = await NamesAsync(tenantId, ct).ConfigureAwait(false);
        return accountRoles.Where(r => !known.Contains(r)).Distinct().ToList();
    }

    /// <summary>
    /// Alasan role tidak boleh di-rename atau dihapus, atau null bila aman. Role dianggap dipakai bila tercantum di
    /// permission DocType, transisi workflow, atau akun pengguna dalam tenant yang sama.
    /// </summary>
    public async Task<string?> BlockingUseAsync(string tenantId, string roleName, MetadataSnapshot snapshot, CancellationToken ct = default)
    {
        var docTypes = snapshot.DocTypes.Values
            .Where(d => d.Def.Permissions.Any(p => p.Role == roleName))
            .Select(d => d.Name).Order().ToList();
        if (docTypes.Count > 0)
            return $"Role '{roleName}' masih dipakai izin DocType: {string.Join(", ", docTypes)}.";

        var workflows = snapshot.Workflows.Values
            .Where(w => w.Def.Transitions.Any(t => t.AllowedRoles.Contains(roleName)))
            .Select(w => w.Def.Name).Order().ToList();
        if (workflows.Count > 0)
            return $"Role '{roleName}' masih dipakai transisi workflow: {string.Join(", ", workflows)}.";

        var users = (await accounts.ListAsync(tenantId, ct).ConfigureAwait(false)).Where(a => a.Roles.Contains(roleName)).Select(a => a.UserName).Order().ToList();
        if (users.Count > 0)
            return $"Role '{roleName}' masih dimiliki pengguna: {string.Join(", ", users.Take(10))}{(users.Count > 10 ? " …" : "")}.";

        return null;
    }
}
