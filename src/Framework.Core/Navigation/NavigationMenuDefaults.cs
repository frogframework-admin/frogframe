using Framework.Security;

namespace Framework.Navigation;

public static class NavigationMenuDefaults
{
    public const string BadgeInboxUnread = "inbox_unread";

    public static string ModuleHrId(string tenantId) => $"mod_{tenantId}_hr";
    public static string ModuleProcurementId(string tenantId) => $"mod_{tenantId}_procurement";
    public static string ModuleAnalyticsId(string tenantId) => $"mod_{tenantId}_analytics";
    public static string ModuleAdminId(string tenantId) => $"mod_{tenantId}_admin";
    public static string ModuleAccountingId(string tenantId) => $"mod_{tenantId}_accounting";

    public static string GroupHrId(string tenantId) => $"grp_{tenantId}_hr";
    public static string GroupPengadaanId(string tenantId) => $"grp_{tenantId}_pengadaan";
    public static string GroupMasterId(string tenantId) => $"grp_{tenantId}_master";
    public static string GroupUtamaId(string tenantId) => $"grp_{tenantId}_utama";
    public static string GroupLaporanId(string tenantId) => $"grp_{tenantId}_laporan";
    public static string GroupSistemId(string tenantId) => $"grp_{tenantId}_sistem";

    /// <summary>Mencari modul untuk DocType.module. Hanya kode yang dikenal (tidak peka huruf besar).</summary>
    public static NavigationModule? ResolveModule(IEnumerable<NavigationModule> modules, string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : modules.FirstOrDefault(m => string.Equals(m.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<NavigationModule> CreateDefaultModules(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = ModuleHrId(tenantId),
            TenantId = tenantId,
            Code = "HR",
            Name = "Human Resources",
            Description = "Employee records, departments, and organization structure",
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
            Code = "PROCUREMENT",
            Name = "Procurement",
            Description = "Purchase requests, product catalog, and vendor management",
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
            Id = ModuleAccountingId(tenantId),
            TenantId = tenantId,
            Code = "ACCOUNTING",
            Name = "Accounting",
            Description = "Chart of accounts, accounting periods, journal entries, and general ledger",
            Icon = "calculator",
            SortOrder = 25,
            AllowedRoles = ["Finance"],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleAnalyticsId(tenantId),
            TenantId = tenantId,
            Code = "ANALYTICS",
            Name = "Analytics",
            Description = "Interactive dashboards, analytical reports, and Excel/Word exports",
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
            Code = "ADMINISTRATION",
            Name = "Administration",
            Description = "Manage users, roles, modules, menus, DocType metadata, and API integrations",
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
            Name = "Procurement",
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
            Name = "Master Data",
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
            Name = "Main",
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
            Name = "Reports",
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
            Name = "System",
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
            Title = "Home",
            Href = "",
            Icon = "house",
            GroupId = GroupUtamaId(tenantId),
            GroupName = "Main",
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
            GroupName = "Main",
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
            Title = "Inbox",
            Href = "inbox",
            Icon = "inbox",
            GroupId = GroupUtamaId(tenantId),
            GroupName = "Main",
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
            Title = "DocType Metadata",
            Href = "admin/metadata",
            Icon = "settings-2",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 100,
            AllowedRoles = [UserContext.SystemManager],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_workflows",
            TenantId = tenantId,
            Title = "Workflows",
            Href = "admin/workflows",
            Icon = "git-branch",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 105,
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
            Title = "Query APIs",
            Href = "query-apis",
            Icon = "braces",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
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
            Title = "API Keys",
            Href = "admin/api-keys",
            Icon = "key-round",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
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
            Title = "Users",
            Href = "admin/users",
            Icon = "users",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 130,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_notifications",
            TenantId = tenantId,
            Title = "Notification Rules",
            Href = "admin/notifications",
            Icon = "bell",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 138,
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
            Title = "User Roles",
            Href = "admin/roles",
            Icon = "shield-check",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
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
            Title = "Reports",
            Href = "reports",
            Icon = "chart-column",
            GroupId = GroupLaporanId(tenantId),
            GroupName = "Reports",
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
            Title = "Excel/Word Reports",
            Href = "admin/report-templates",
            Icon = "file-spreadsheet",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 115,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_widgets",
            TenantId = tenantId,
            Title = "Widgets",
            Href = "admin/widgets",
            Icon = "layout-grid",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 118,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_module_admin",
            TenantId = tenantId,
            Title = "Master Modules",
            Href = "admin/modules",
            Icon = "boxes",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
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
            Title = "Menu Groups",
            Href = "admin/menu-groups",
            Icon = "folders",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
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
            Title = "Manage Menus",
            Href = "admin/menus",
            Icon = "panel-left",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 150,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_scheduled_jobs",
            TenantId = tenantId,
            Title = "Scheduled Jobs",
            Href = "admin/jobs",
            Icon = "clock",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 160,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"menu_{tenantId}_error_logs",
            TenantId = tenantId,
            Title = "Error Log",
            Href = "admin/error-logs",
            Icon = "triangle-alert",
            GroupId = GroupSistemId(tenantId),
            GroupName = "System",
            SortOrder = 170,
            AllowedRoles = [UserContext.SystemManager, UserContext.Administrator],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];
}
