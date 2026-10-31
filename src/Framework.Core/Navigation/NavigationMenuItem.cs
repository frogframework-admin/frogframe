using Framework.Security;

namespace Framework.Navigation;

public sealed class NavigationMenuItem
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    public string Title { get; set; } = "";
    public string Href { get; set; } = "";
    public string Icon { get; set; } = "file-text";
    public string? GroupId { get; set; }
    public string GroupName { get; set; } = "Umum";
    public int SortOrder { get; set; } = 100;
    public List<string> AllowedRoles { get; set; } = [];
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BadgeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Determines whether this menu item is visible to the currently authenticated user.
    /// </summary>
    public bool IsVisibleFor(UserContext? user)
    {
        if (!IsActive || user is null) return false;
        if (user.IsAdmin) return true;
        if (AllowedRoles.Count == 0) return true;
        return user.Roles.Any(r => AllowedRoles.Any(ar => string.Equals(ar, r, StringComparison.OrdinalIgnoreCase)));
    }
}
