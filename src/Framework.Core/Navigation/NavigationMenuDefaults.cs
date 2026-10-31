using Framework.Security;

namespace Framework.Navigation;

public static class NavigationMenuDefaults
{
    public const string BadgeInboxUnread = "inbox_unread";

    public static string ModuleHrId(string tenantId) => $"mod_{tenantId}_hr";
    public static string ModuleProcurementId(string tenantId) => $"mod_{tenantId}_procurement";
    public static string ModuleAnalyticsId(string tenantId) => $"mod_{tenantId}_analytics";
    public static string ModuleAdminId(string tenantId) => $"mod_{tenantId}_admin";

    public static string GroupHrId(string tenantId) => $"grp_{tenantId}_hr";
    public static string GroupPengadaanId(string tenantId) => $"grp_{tenantId}_pengadaan";
    public static string GroupMasterId(string tenantId) => $"grp_{tenantId}_master";
    public static string GroupUtamaId(string tenantId) => $"grp_{tenantId}_utama";
    public static string GroupLaporanId(string tenantId) => $"grp_{tenantId}_laporan";
    public static string GroupSistemId(string tenantId) => $"grp_{tenantId}_sistem";

    public static IReadOnlyList<NavigationModule> CreateDefaultModules(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = ModuleHrId(tenantId),
            TenantId = tenantId,
            Name = "Human Resources",
            Description = "Data karyawan, departemen, dan struktur organisasi",
            Icon = "users",
            SortOrder = 10,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleProcurementId(tenantId),
            TenantId = tenantId,
            Name = "Procurement",
            Description = "Pengajuan pembelian, katalog produk, dan manajemen vendor",
            Icon = "shopping-cart",
            SortOrder = 20,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleAnalyticsId(tenantId),
            TenantId = tenantId,
            Name = "Analytics",
            Description = "Dashboard interaktif, laporan analitik, dan cetakan Excel/Word",
            Icon = "chart-column",
            SortOrder = 30,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleAdminId(tenantId),
            TenantId = tenantId,
            Name = "Administration",
            Description = "Pengaturan pengguna, role, modul, menu, metadata DocType, dan integrasi API",
            Icon = "settings",
            SortOrder = 40,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];

    public static IReadOnlyList<NavigationMenuGroup> CreateDefaultGroups(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = GroupHrId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleHrId(tenantId),
            Name = "HR",
            Icon = "users",
            SortOrder = 10,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = GroupPengadaanId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleProcurementId(tenantId),
            Name = "Pengadaan",
            Icon = "shopping-cart",
            SortOrder = 10,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = GroupMasterId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleProcurementId(tenantId),
            Name = "Master",
            Icon = "database",
            SortOrder = 20,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = GroupUtamaId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleAnalyticsId(tenantId),
            Name = "Utama",
            Icon = "layout-dashboard",
            SortOrder = 10,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = GroupLaporanId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleAnalyticsId(tenantId),
            Name = "Laporan",
            Icon = "file-spreadsheet",
            SortOrder = 20,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = GroupSistemId(tenantId),
            TenantId = tenantId,
            ModuleId = ModuleAdminId(tenantId),
            Name = "Sistem",
            Icon = "settings-2",
            SortOrder = 10,
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];

    public static IReadOnlyList<NavigationMenuItem> CreateDefaultItems(string tenantId, DateTimeOffset now) =>
    [
        // Universal employee menu items
        new()
        {
            Id = $"menu_{tenantId}_beranda",
            TenantId = tenantId,
            Title = "Beranda",
            Href = "",
            Icon = "house",
            GroupId = GroupUtamaId(tenantId),
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
            GroupId = GroupUtamaId(tenantId),
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
            GroupId = GroupUtamaId(tenantId),
            GroupName = "Utama",
            SortOrder = 30,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            BadgeKey = BadgeInboxUnread,
            CreatedAt = now,
            UpdatedAt = now
        },

        // Mandatory system menu items (auto-seeded when missing)
        new()
        {
            Id = $"menu_{tenantId}_metadata",
            TenantId = tenantId,
            Title = "Metadata DocType",
            Href = "admin/metadata",
            Icon = "settings-2",
            GroupId = GroupSistemId(tenantId),
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
            GroupId = GroupSistemId(tenantId),
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
            GroupId = GroupSistemId(tenantId),
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
            GroupId = GroupSistemId(tenantId),
            GroupName = "Sistem",
            SortOrder = 130,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_roles",
            TenantId = tenantId,
            Title = "Role Pengguna",
            Href = "admin/roles",
            Icon = "shield-check",
            GroupId = GroupSistemId(tenantId),
            GroupName = "Sistem",
            SortOrder = 135,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
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
            GroupId = GroupLaporanId(tenantId),
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
            GroupId = GroupLaporanId(tenantId),
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
            Id = $"menu_{tenantId}_module_admin",
            TenantId = tenantId,
            Title = "Master Modul",
            Href = "admin/modules",
            Icon = "boxes",
            GroupId = GroupSistemId(tenantId),
            GroupName = "Sistem",
            SortOrder = 148,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_menu_group_admin",
            TenantId = tenantId,
            Title = "Group Menu",
            Href = "admin/menu-groups",
            Icon = "folders",
            GroupId = GroupSistemId(tenantId),
            GroupName = "Sistem",
            SortOrder = 149,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
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
            GroupId = GroupSistemId(tenantId),
            GroupName = "Sistem",
            SortOrder = 150,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];
}
