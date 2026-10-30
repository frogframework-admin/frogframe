using Framework.Security;

namespace Framework.Navigation;

public sealed class NavigationMenuItem
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    public string Title { get; set; } = "";
    public string Href { get; set; } = "";
    public string Icon { get; set; } = "file-text";
    public string GroupName { get; set; } = "Umum";
    public int SortOrder { get; set; } = 100;
    public List<string> AllowedRoles { get; set; } = [];
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public string? BadgeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Menentukan apakah item menu ini dapat dilihat oleh pengguna yang sedang login.
    /// - Nonaktif -> tidak terlihat oleh siapapun.
    /// - System Manager -> selalu dapat melihat semua menu aktif.
    /// - Tanpa AllowedRoles -> universal (terlihat oleh semua pengguna yang login).
    /// - Memiliki AllowedRoles -> hanya terlihat jika pengguna memiliki minimal satu dari role tersebut.
    /// </summary>
    public bool IsVisibleFor(UserContext? user)
    {
        if (!IsActive || user is null) return false;
        if (user.Roles.Contains(UserContext.SystemManager)) return true;
        if (AllowedRoles.Count == 0) return true;
        return user.Roles.Any(AllowedRoles.Contains);
    }
}

