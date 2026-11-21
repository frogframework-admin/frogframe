using System.Collections.Concurrent;

namespace Framework.Navigation;

public sealed class InMemoryNavigationMenuStore(TimeProvider clock) : INavigationMenuStore
{
    private readonly ConcurrentDictionary<string, NavigationApplication> applications = new();
    private readonly ConcurrentDictionary<string, NavigationModule> modules = new();
    private readonly ConcurrentDictionary<string, NavigationMenuGroup> groups = new();
    private readonly ConcurrentDictionary<string, NavigationMenuItem> items = new();

    public event Action? Changed;

    public Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var modified = false;

        foreach (var defApp in NavigationMenuDefaults.CreateDefaultApplications(tenantId, now))
        {
            if (!applications.Values.Any(x => x.TenantId == tenantId && (x.Id == defApp.Id || string.Equals(x.Code, defApp.Code, StringComparison.OrdinalIgnoreCase))))
            {
                applications[defApp.Id] = defApp;
                modified = true;
            }
        }

        // Remove deprecated system modules (such as ANALYTICS) and their groups
        foreach (var mod in modules.Values.Where(m => m.TenantId == tenantId && m.IsSystem && (m.Code == "ANALYTICS" || m.Id.EndsWith("_analytics"))).ToList())
        {
            modules.TryRemove(mod.Id, out _);
            modified = true;
        }
        foreach (var grp in groups.Values.Where(g => g.TenantId == tenantId && g.IsSystem && (g.ModuleId.EndsWith("_analytics") || g.Id.EndsWith("_utama") || g.Id.EndsWith("_laporan"))).ToList())
        {
            groups.TryRemove(grp.Id, out _);
            modified = true;
        }

        foreach (var defMod in NavigationMenuDefaults.CreateDefaultModules(tenantId, now))
        {
            var existingMod = modules.Values.FirstOrDefault(x => x.TenantId == tenantId && (x.Id == defMod.Id || string.Equals(x.Name, defMod.Name, StringComparison.OrdinalIgnoreCase)));
            if (existingMod is null)
            {
                modules[defMod.Id] = defMod;
                modified = true;
            }
            else if (string.IsNullOrWhiteSpace(existingMod.ApplicationId) && !string.IsNullOrWhiteSpace(defMod.ApplicationId))
            {
                existingMod.ApplicationId = defMod.ApplicationId;
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
            var existing = items.Values.FirstOrDefault(x => x.TenantId == tenantId && (x.Id == defItem.Id || string.Equals(x.Href, defItem.Href, StringComparison.OrdinalIgnoreCase) || (defItem.Id.EndsWith("_report_templates") && string.Equals(x.Href, "report-templates", StringComparison.OrdinalIgnoreCase)) || (defItem.Id.EndsWith("_widgets") && string.Equals(x.Href, "dashboard/widgets", StringComparison.OrdinalIgnoreCase))));
            if (existing is null)
            {
                if (string.IsNullOrWhiteSpace(defItem.GroupId) && !string.IsNullOrWhiteSpace(defItem.GroupName) && groupByName.TryGetValue(defItem.GroupName, out var grp))
                {
                    defItem.GroupId = grp.Id;
                }
                items[defItem.Id] = defItem;
                modified = true;
            }
            else if (existing.IsSystem && (existing.GroupId != defItem.GroupId || existing.GroupName != defItem.GroupName || existing.Href != defItem.Href || existing.SortOrder != defItem.SortOrder || !existing.AllowedRoles.SequenceEqual(defItem.AllowedRoles)))
            {
                existing.GroupId = defItem.GroupId;
                existing.GroupName = defItem.GroupName;
                existing.Href = defItem.Href;
                existing.AllowedRoles = defItem.AllowedRoles;
                existing.SortOrder = defItem.SortOrder;
                modified = true;
            }
        }

        // Backfill GroupId on any existing menu items that only have GroupName set.
        foreach (var existing in items.Values.Where(x => x.TenantId == tenantId && string.IsNullOrWhiteSpace(x.GroupId) && !string.IsNullOrWhiteSpace(x.GroupName)))
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

    public async Task<IReadOnlyList<NavigationApplication>> ListApplicationsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureDefaultMenusAsync(tenantId, ct);
        return applications.Values
            .Where(x => x.TenantId == tenantId && (!onlyActive || x.IsActive))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToList();
    }

    public Task<NavigationApplication?> GetApplicationAsync(string id, string tenantId, CancellationToken ct = default) =>
        Task.FromResult(applications.TryGetValue(id, out var app) && app.TenantId == tenantId ? app : null);

    public Task SaveApplicationAsync(NavigationApplication application, CancellationToken ct = default)
    {
        application.UpdatedAt = clock.GetUtcNow();
        if (application.CreatedAt == default) application.CreatedAt = application.UpdatedAt;
        if (string.IsNullOrWhiteSpace(application.Id)) application.Id = Guid.NewGuid().ToString("N");
        application.Code = NavigationApplication.NormalizeCode(application.Code);
        if (applications.Values.Any(a => a.TenantId == application.TenantId && a.Id != application.Id && string.Equals(a.Code, application.Code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Application code '{application.Code}' is already in use.");
        if (applications.TryGetValue(application.Id, out var existing) && !string.IsNullOrEmpty(existing.Code) && existing.Code != application.Code)
            throw new InvalidOperationException($"Application code '{existing.Code}' cannot be changed once created.");

        applications[application.Id] = application;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteApplicationAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (applications.TryGetValue(id, out var app) && app.TenantId == tenantId)
        {
            if (app.IsSystem) throw new InvalidOperationException("System applications cannot be deleted.");
            applications.TryRemove(id, out _);
            Changed?.Invoke();
        }
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
        module.Code = NavigationModule.NormalizeCode(module.Code);
        if (modules.Values.Any(m => m.TenantId == module.TenantId && m.Id != module.Id && m.Code == module.Code))
            throw new InvalidOperationException($"Kode modul '{module.Code}' sudah dipakai modul lain.");
        // Kode adalah kunci referensi DocType: setelah tersimpan tidak boleh berubah.
        if (modules.TryGetValue(module.Id, out var existing) && !string.IsNullOrEmpty(existing.Code) && existing.Code != module.Code)
            throw new InvalidOperationException($"Kode modul '{existing.Code}' tidak bisa diubah karena dipakai DocType.");

        modules[module.Id] = module;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task DeleteModuleAsync(string id, string tenantId, CancellationToken ct = default)
    {
        if (modules.TryGetValue(id, out var mod) && mod.TenantId == tenantId)
        {
            if (mod.IsSystem) throw new InvalidOperationException("System modules cannot be deleted.");
            if (groups.Values.Any(g => g.TenantId == tenantId && g.ModuleId == id))
                throw new InvalidOperationException("Module still contains menu groups. Move or delete the menu groups first.");
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
            if (grp.IsSystem) throw new InvalidOperationException("System menu groups cannot be deleted.");
            if (items.Values.Any(m => m.TenantId == tenantId && (m.GroupId == id || string.Equals(m.GroupName, grp.Name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Menu group still contains menu items. Move or delete the menu items first.");
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
            if (item.IsSystem) throw new InvalidOperationException("System menus cannot be deleted.");
            items.TryRemove(id, out _);
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }
}
