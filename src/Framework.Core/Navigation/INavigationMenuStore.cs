namespace Framework.Navigation;

public interface INavigationMenuStore
{
    /// <summary>Triggered whenever modules, menu groups, or menu items change so the UI can refresh immediately.</summary>
    event Action? Changed;

    /// <summary>Lists all navigation modules for the given tenant, ordered by sort_order.</summary>
    Task<IReadOnlyList<NavigationModule>> ListModulesAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default);

    /// <summary>Gets a single navigation module by id and tenant.</summary>
    Task<NavigationModule?> GetModuleAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Saves a new or existing navigation module.</summary>
    Task SaveModuleAsync(NavigationModule module, CancellationToken ct = default);

    /// <summary>Deletes a custom navigation module (system modules cannot be deleted).</summary>
    Task DeleteModuleAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Lists all navigation menu groups for the given tenant, ordered by sort_order.</summary>
    Task<IReadOnlyList<NavigationMenuGroup>> ListGroupsAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default);

    /// <summary>Gets a single navigation menu group by id and tenant.</summary>
    Task<NavigationMenuGroup?> GetGroupAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Saves a new or existing navigation menu group.</summary>
    Task SaveGroupAsync(NavigationMenuGroup group, CancellationToken ct = default);

    /// <summary>Deletes a custom navigation menu group (system groups cannot be deleted).</summary>
    Task DeleteGroupAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Lists all navigation menu items for the given tenant, ordered by sort_order.</summary>
    Task<IReadOnlyList<NavigationMenuItem>> ListAsync(string tenantId, bool onlyActive = false, CancellationToken ct = default);

    /// <summary>Gets a single navigation menu item by id and tenant.</summary>
    Task<NavigationMenuItem?> GetAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Saves a new or existing navigation menu item.</summary>
    Task SaveAsync(NavigationMenuItem item, CancellationToken ct = default);

    /// <summary>Deletes a custom navigation menu item (system menus cannot be deleted).</summary>
    Task DeleteAsync(string id, string tenantId, CancellationToken ct = default);

    /// <summary>Ensures default modules, menu groups, and menu items are seeded for the tenant.</summary>
    Task EnsureDefaultMenusAsync(string tenantId, CancellationToken ct = default);
}
