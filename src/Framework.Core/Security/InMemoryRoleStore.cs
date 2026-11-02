using System.Collections.Concurrent;

namespace Framework.Security;

public sealed class InMemoryRoleStore(TimeProvider clock) : IRoleStore
{
    private readonly ConcurrentDictionary<string, RoleDef> items = new();

    public event Action? Changed;

    public Task EnsureDefaultRolesAsync(string tenantId, CancellationToken ct = default)
    {
        var defaults = RoleDefaults.CreateDefaultRoles(tenantId, clock.GetUtcNow());
        var modified = false;

        foreach (var def in defaults)
        {
            if (!items.Values.Any(x => x.TenantId == tenantId && string.Equals(x.Name, def.Name, StringComparison.OrdinalIgnoreCase)))
            {
                items[def.Id] = def;
                modified = true;
            }
        }

        if (modified) Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RoleDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await EnsureDefaultRolesAsync(tenantId, ct);
        return items.Values
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.IsSystem)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public Task<RoleDef?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(items.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null);
    }

    public async Task<RoleDef?> GetByNameAsync(string name, string tenantId, CancellationToken ct = default)
    {
        var list = await ListAsync(tenantId, ct);
        return list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Task SaveAsync(RoleDef role, CancellationToken ct = default)
    {
        role.UpdatedAt = clock.GetUtcNow();
        if (role.CreatedAt == default) role.CreatedAt = role.UpdatedAt;
        if (string.IsNullOrWhiteSpace(role.Id)) role.Id = Guid.NewGuid().ToString("N");

        items[role.Id] = role;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (items.TryGetValue(id, out var item) && item.TenantId == tenantId)
        {
            if (item.IsSystem || string.Equals(item.Name, "Administrator", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, "System Manager", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Role '{item.Name}' is a built-in system role and cannot be deleted.");
            }
            items.TryRemove(id, out _);
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }
}

