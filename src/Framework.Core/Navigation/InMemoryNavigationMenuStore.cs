using System.Collections.Concurrent;

namespace Framework.Navigation;

public sealed class InMemoryNavigationMenuStore(TimeProvider clock) : INavigationMenuStore
{
    private readonly ConcurrentDictionary<string, NavigationModule> modules = new();
    private readonly ConcurrentDictionary<string, NavigationMenuGroup> groups = new();
    private readonly ConcurrentDictionary<string, NavigationMenuItem> items = new();

    public event Action? Changed;

    public Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var modified = false;

        foreach (var defMod in NavigationMenuDefaults.CreateDefaultModules(tenantId, now))
        {
            if (!modules.Values.Any(x => x.TenantId == tenantId && (x.Id == defMod.Id || string.Equals(x.Name, defMod.Name, StringComparison.OrdinalIgnoreCase))))
            {
                modules[defMod.Id] = defMod;
                modified = true;
            }
        }

        foreach (var defGrp in NavigationMenuDefaults.CreateDefaultGroups(tenantId, now))
        {
            if (!groups.Values.Any(x => x.TenantId == tenantId && (x.Id == defGrp.Id || string.Equals(x.Name, defGrp.Name, StringComparison.OrdinalIgnoreCase))))
            {
                groups[defGrp.Id] = defGrp;
                modified = true;
            }
        }

        var groupByName = groups.Values
            .Where(g => g.TenantId == tenantId)
            .ToDictionary(g => g.Name, g => g, StringComparer.OrdinalIgnoreCase);

        foreach (var defItem in NavigationMenuDefaults.CreateDefaultItems(tenantId, now))
        {
            if (!items.Values.Any(x => x.TenantId == tenantId && string.Equals(x.Href, defItem.Href, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(defItem.GroupId) && groupByName.TryGetValue(defItem.GroupName, out var grp))
                {
                    defItem.GroupId = grp.Id;
                }
                items[defItem.Id] = defItem;
                modified = true;
            }
        }

        // Backfill GroupId on any existing menu items that only have GroupName set.
        foreach (var existing in items.Values.Where(x => x.TenantId == tenantId && string.IsNullOrWhiteSpace(x.GroupId)))
        {
            if (groupByName.TryGetValue(existing.GroupName, out var grp))
            {
                existing.GroupId = grp.Id;
                modified = true;
            }
        }

        if (modified) Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NavigationModule>> ListModulesAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureDefaultMenusAsync(tenantId, ct);
        return modules.Values
            .Where(x => x.TenantId == tenantId && (!onlyActive || x.IsActive))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public Task<NavigationModule?> GetModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(modules.TryGetValue(id, out var mod) && mod.TenantId == tenantId ? mod : null);
    }

    public Task SaveModuleAsync(NavigationModule module, CancellationToken ct = default)
    {
        module.UpdatedAt = clock.GetUtcNow();
        if (module.CreatedAt == default) module.CreatedAt = module.UpdatedAt;
        if (string.IsNullOrWhiteSpace(module.Id)) module.Id = Guid.NewGuid().ToString("N");

        modules[module.Id] = module;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (modules.TryGetValue(id, out var mod) && mod.TenantId == tenantId)
        {
            if (mod.IsSystem) throw new InvalidOperationException("Modul sistem tidak dapat dihapus.");
            if (groups.Values.Any(g => g.TenantId == tenantId && g.ModuleId == id))
                throw new InvalidOperationException("Modul masih memiliki group menu. Pindahkan atau hapus group menu terlebih dahulu.");
            modules.TryRemove(id, out _);
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NavigationMenuGroup>> ListGroupsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureDefaultMenusAsync(tenantId, ct);
        return groups.Values
            .Where(x => x.TenantId == tenantId && (!onlyActive || x.IsActive))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public Task<NavigationMenuGroup?> GetGroupAsync(string id, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(groups.TryGetValue(id, out var grp) && grp.TenantId == tenantId ? grp : null);
    }

    public Task SaveGroupAsync(NavigationMenuGroup group, CancellationToken ct = default)
    {
        group.UpdatedAt = clock.GetUtcNow();
        if (group.CreatedAt == default) group.CreatedAt = group.UpdatedAt;
        if (string.IsNullOrWhiteSpace(group.Id)) group.Id = Guid.NewGuid().ToString("N");

        groups[group.Id] = group;

        // Keep GroupName synchronized on child menu items referencing this GroupId.
        foreach (var item in items.Values.Where(x => x.TenantId == group.TenantId && x.GroupId == group.Id))
        {
            item.GroupName = group.Name;
        }

        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (groups.TryGetValue(id, out var grp) && grp.TenantId == tenantId)
        {
            if (grp.IsSystem) throw new InvalidOperationException("Group menu sistem tidak dapat dihapus.");
            if (items.Values.Any(m => m.TenantId == tenantId && (m.GroupId == id || string.Equals(m.GroupName, grp.Name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Group menu masih memiliki item menu. Pindahkan atau hapus item menu terlebih dahulu.");
            groups.TryRemove(id, out _);
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureDefaultMenusAsync(tenantId, ct);
        return items.Values
            .Where(x => x.TenantId == tenantId && (!onlyActive || x.IsActive))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Title)
            .ToList();
    }

    public Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(items.TryGetValue(id, out var item) && item.TenantId == tenantId ? item : null);
    }

    public Task SaveAsync(NavigationMenuItem item, CancellationToken ct = default)
    {
        item.UpdatedAt = clock.GetUtcNow();
        if (item.CreatedAt == default) item.CreatedAt = item.UpdatedAt;
        if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(item.GroupId) && groups.TryGetValue(item.GroupId, out var grp) && grp.TenantId == item.TenantId)
        {
            item.GroupName = grp.Name;
        }

        items[item.Id] = item;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (items.TryGetValue(id, out var item) && item.TenantId == tenantId)
        {
            if (item.IsSystem) throw new InvalidOperationException("Menu sistem tidak dapat dihapus.");
            items.TryRemove(id, out _);
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }
}
