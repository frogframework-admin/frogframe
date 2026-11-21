using Framework.Security;

namespace Framework.Navigation;

/// <summary>
/// Top-level application category (e.g., HRM, Finance, Accounting, IT).
/// Visibility can be controlled by role-based access control (<see cref="AllowedRoles"/>).
/// </summary>
public sealed class NavigationApplication
{
    /// <summary>Normalizes and validates an application code (uppercase, 2–32 chars, letters/digits/underscore).</summary>
    public static string NormalizeCode(string? code) => NavigationModule.NormalizeCode(code);

    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "app-window";
    public int SortOrder { get; set; } = 100;
    public List<string> AllowedRoles { get; set; } = [];
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Determines whether this application is accessible by the specified user based on active status and roles.
    /// </summary>
    public bool IsVisibleFor(UserContext? user)
    {
        if (!IsActive || user is null) return false;
        if (user.IsAdmin) return true;
        if (AllowedRoles.Count == 0) return true;
        return user.Roles.Any(r => AllowedRoles.Any(ar => string.Equals(ar, r, StringComparison.OrdinalIgnoreCase)));
    }
}
