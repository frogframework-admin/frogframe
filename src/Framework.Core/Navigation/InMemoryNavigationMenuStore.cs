using System.Collections.Concurrent;

namespace Framework.Navigation;

public sealed class InMemoryNavigationMenuStore(TimeProvider clock) : INavigationMenuStore
{
    private readonly ConcurrentDictionary<string, NavigationMenuItem> items = new();

    public event Action? Changed;

    public Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default)
    {
        var defaults = NavigationMenuDefaults.CreateDefaultItems(tenantId, clock.GetUtcNow());
        var modified = false;

        foreach (var def in defaults)
        {
            if (!items.Values.Any(x => x.TenantId == tenantId && string.Equals(x.Href, def.Href, StringComparison.OrdinalIgnoreCase)))
            {
                items[def.Id] = def;
                modified = true;
            }
        }

        if (modified) Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default)
    {
        await EnsureDefaultMenusAsync(tenantId, ct);
        return items.Values
            .Where(x => x.TenantId == tenantId && (!onlyActive || x.IsActive))
            .OrderBy(x => x.SortOrder)
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

