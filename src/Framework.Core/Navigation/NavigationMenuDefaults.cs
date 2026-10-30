using Framework.Security;

namespace Framework.Navigation;

public static class NavigationMenuDefaults
{
    public const string BadgeInboxUnread = "inbox_unread";

    public static IReadOnlyList<NavigationMenuItem> CreateDefaultItems(string tenantId, DateTimeOffset now) =>
    [
        // Menu Universal Karyawan
        new()
        {
            Id = $"menu_{tenantId}_beranda",
            TenantId = tenantId,
            Title = "Beranda",
            Href = "",
            Icon = "house",
            GroupName = "Utama",
            SortOrder = 10,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_dashboard",
            TenantId = tenantId,
            Title = "Dashboard",
            Href = "dashboard",
            Icon = "layout-dashboard",
            GroupName = "Utama",
            SortOrder = 20,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_inbox",
            TenantId = tenantId,
            Title = "Kotak masuk",
            Href = "inbox",
            Icon = "inbox",
            GroupName = "Utama",
            SortOrder = 30,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            BadgeKey = BadgeInboxUnread,
            CreatedAt = now,
            UpdatedAt = now
        },

        // Menu Wajib Sistem (Auto-Seeded jika belum ada)
        new()
        {
            Id = $"menu_{tenantId}_metadata",
            TenantId = tenantId,
            Title = "Metadata DocType",
            Href = "admin/metadata",
            Icon = "settings-2",
            GroupName = "Sistem",
            SortOrder = 100,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_query_apis",
            TenantId = tenantId,
            Title = "API Query",
            Href = "query-apis",
            Icon = "braces",
            GroupName = "Sistem",
            SortOrder = 110,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_api_keys",
            TenantId = tenantId,
            Title = "Kunci API",
            Href = "admin/api-keys",
            Icon = "key-round",
            GroupName = "Sistem",
            SortOrder = 120,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_users",
            TenantId = tenantId,
            Title = "Pengguna",
            Href = "admin/users",
            Icon = "users",
            GroupName = "Sistem",
            SortOrder = 130,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_reports",
            TenantId = tenantId,
            Title = "Laporan",
            Href = "reports",
            Icon = "chart-column",
            GroupName = "Laporan",
            SortOrder = 140,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_report_templates",
            TenantId = tenantId,
            Title = "Laporan Excel/Word",
            Href = "report-templates",
            Icon = "file-spreadsheet",
            GroupName = "Laporan",
            SortOrder = 145,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_menu_admin",
            TenantId = tenantId,
            Title = "Kelola Menu",
            Href = "admin/menus",
            Icon = "panel-left",
            GroupName = "Sistem",
            SortOrder = 150,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];
}

