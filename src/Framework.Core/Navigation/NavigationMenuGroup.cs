namespace Framework.Navigation;

/// <summary>
/// Navigation menu group belonging to a parent <see cref="NavigationModule"/> and containing menu items.
/// </summary>
public sealed class NavigationMenuGroup
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "folder";
    public int SortOrder { get; set; } = 100;
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

