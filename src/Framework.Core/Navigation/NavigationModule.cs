using Framework.Security;

namespace Framework.Navigation;

/// <summary>
/// Top-level application module containing one or more navigation menu groups.
/// Visibility is controlled by role-based access control (<see cref="AllowedRoles"/>).
/// </summary>
public sealed class NavigationModule
{
    private static readonly System.Text.RegularExpressions.Regex CodeRx = new("^[A-Z][A-Z0-9_]{1,31}$");

    /// <summary>Menormalkan dan memvalidasi kode modul: huruf besar, 2–32 karakter, huruf/angka/garis bawah.</summary>
    public static string NormalizeCode(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (!CodeRx.IsMatch(c))
            throw new ArgumentException("Kode modul harus 2–32 karakter: huruf, angka, atau garis bawah, diawali huruf (mis. PROCUREMENT).");
        return c;
    }

    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    /// <summary>Kode unik per tenant (huruf besar), mis. HR, PROCUREMENT. Dipakai DocType.module sebagai referensi.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Icon { get; set; } = "layout-grid";
    public int SortOrder { get; set; } = 100;
    public List<string> AllowedRoles { get; set; } = [];
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Determines whether this module is accessible by the specified user based on active status and roles.
    /// </summary>
    public bool IsVisibleFor(UserContext? user)
    {
        if (!IsActive || user is null) return false;
        if (user.IsAdmin) return true;
        if (AllowedRoles.Count == 0) return true;
        return user.Roles.Any(r => AllowedRoles.Any(ar => string.Equals(ar, r, StringComparison.OrdinalIgnoreCase)));
    }
}

