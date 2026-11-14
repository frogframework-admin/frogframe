using Framework.Security;

namespace Framework.Navigation;

public static class NavigationMenuDefaults
{
    public const string BadgeInboxUnread = "inbox_unread";

    // Modules
    public static string ModuleCoreId(string tenantId) => $"mod_{tenantId}_core";
    public static string ModuleHrId(string tenantId) => $"mod_{tenantId}_hr";
    public static string ModuleProcurementId(string tenantId) => $"mod_{tenantId}_procurement";
    public static string ModuleAccountingId(string tenantId) => $"mod_{tenantId}_accounting";
    public static string ModuleFinanceId(string tenantId) => $"mod_{tenantId}_finance";
    public static string ModuleCrmId(string tenantId) => $"mod_{tenantId}_crm";
    public static string ModuleStockId(string tenantId) => $"mod_{tenantId}_stock";
    public static string ModuleAnalyticsId(string tenantId) => $"mod_{tenantId}_analytics";
    public static string ModuleAdminId(string tenantId) => $"mod_{tenantId}_admin";

    // Legacy / Canonical Group IDs
    public static string GroupHrId(string tenantId) => $"grp_{tenantId}_hr";
    public static string GroupPengadaanId(string tenantId) => $"grp_{tenantId}_pengadaan";
    public static string GroupMasterId(string tenantId) => $"grp_{tenantId}_master";
    public static string GroupUtamaId(string tenantId) => $"grp_{tenantId}_utama";
    public static string GroupLaporanId(string tenantId) => $"grp_{tenantId}_laporan";
    public static string GroupSistemId(string tenantId) => $"grp_{tenantId}_sistem";

    // Modern Group IDs
    public static string GroupCorePartiesId(string tenantId) => $"grp_{tenantId}_core_parties";
    public static string GroupCoreItemsId(string tenantId) => $"grp_{tenantId}_core_items";
    public static string GroupCoreSettingsId(string tenantId) => $"grp_{tenantId}_core_settings";

    public static string GroupHrEmployeesId(string tenantId) => $"grp_{tenantId}_hr_employees";
    public static string GroupHrAttendanceId(string tenantId) => $"grp_{tenantId}_hr_attendance";
    public static string GroupHrPayrollId(string tenantId) => $"grp_{tenantId}_hr_payroll";
    public static string GroupHrRecruitmentId(string tenantId) => $"grp_{tenantId}_hr_recruitment";

    public static string GroupProcOrdersId(string tenantId) => $"grp_{tenantId}_proc_orders";
    public static string GroupProcReceiptsId(string tenantId) => $"grp_{tenantId}_proc_receipts";

    public static string GroupAccGlId(string tenantId) => $"grp_{tenantId}_acc_gl";
    public static string GroupAccClosingId(string tenantId) => $"grp_{tenantId}_acc_closing";

    public static string GroupFinInvoicesId(string tenantId) => $"grp_{tenantId}_fin_invoices";
    public static string GroupFinBankingId(string tenantId) => $"grp_{tenantId}_fin_banking";
    public static string GroupFinTaxId(string tenantId) => $"grp_{tenantId}_fin_tax";
    public static string GroupFinAssetsId(string tenantId) => $"grp_{tenantId}_fin_assets";
    public static string GroupFinEmployeeId(string tenantId) => $"grp_{tenantId}_fin_employee";

    public static string GroupCrmPipelineId(string tenantId) => $"grp_{tenantId}_crm_pipeline";
    public static string GroupCrmCustomersId(string tenantId) => $"grp_{tenantId}_crm_customers";
    public static string GroupCrmSettingsId(string tenantId) => $"grp_{tenantId}_crm_settings";

    public static string GroupStockOpsId(string tenantId) => $"grp_{tenantId}_stock_ops";
    public static string GroupStockMasterId(string tenantId) => $"grp_{tenantId}_stock_master";

    public static string GroupAdminSystemId(string tenantId) => $"grp_{tenantId}_admin_system";
    public static string GroupAdminMetadataId(string tenantId) => $"grp_{tenantId}_admin_metadata";
    public static string GroupAdminIntegrationsId(string tenantId) => $"grp_{tenantId}_admin_integrations";

    /// <summary>Mencari modul untuk DocType.module. Hanya kode yang dikenal (tidak peka huruf besar).</summary>
    public static NavigationModule? ResolveModule(IEnumerable<NavigationModule> modules, string? code) =>
        string.IsNullOrWhiteSpace(code) ? null
        : modules.FirstOrDefault(m => string.Equals(m.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<NavigationModule> CreateDefaultModules(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = ModuleCoreId(tenantId),
            TenantId = tenantId,
            Code = "CORE",
            Name = "Core",
            Description = "Master data, companies, parties, and global settings",
            Icon = "database",
            SortOrder = 5,
            AllowedRoles = [],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleHrId(tenantId),
            TenantId = tenantId,
            Code = "HR",
            Name = "Human Resources",
            Description = "Employee records, attendance, payroll, and recruitment",
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
            Description = "Purchase requests, orders, and supplier receipts",
            Icon = "shopping-cart",
            SortOrder = 20,
            AllowedRoles = ["Purchasing", "Stock", "Manager"],
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
            Description = "Chart of accounts, periods, closing, and general ledger",
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
            Id = ModuleFinanceId(tenantId),
            TenantId = tenantId,
            Code = "FINANCE",
            Name = "Finance",
            Description = "Invoicing, payments, banking, tax, and fixed assets",
            Icon = "landmark",
            SortOrder = 26,
            AllowedRoles = ["Finance"],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleCrmId(tenantId),
            TenantId = tenantId,
            Code = "CRM",
            Name = "CRM",
            Description = "Leads, opportunities, quotations, sales orders, and customer relations",
            Icon = "briefcase",
            SortOrder = 27,
            AllowedRoles = ["Sales", "Manager"],
            IsSystem = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = ModuleStockId(tenantId),
            TenantId = tenantId,
            Code = "STOCK",
            Name = "Stock",
            Description = "Warehouses, stock movements, and inventory reconciliation",
            Icon = "boxes",
            SortOrder = 28,
            AllowedRoles = ["Stock", "Purchasing"],
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
            Description = "Interactive dashboards and operational reports",
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
        // CORE
        new() { Id = GroupCorePartiesId(tenantId), TenantId = tenantId, ModuleId = ModuleCoreId(tenantId), Name = "Company & Parties", Icon = "building", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupCoreItemsId(tenantId), TenantId = tenantId, ModuleId = ModuleCoreId(tenantId), Name = "Product Catalog", Icon = "tag", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupCoreSettingsId(tenantId), TenantId = tenantId, ModuleId = ModuleCoreId(tenantId), Name = "Settings & Regional", Icon = "sliders", SortOrder = 30, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // HR
        new() { Id = GroupHrEmployeesId(tenantId), TenantId = tenantId, ModuleId = ModuleHrId(tenantId), Name = "Employees & Structure", Icon = "users", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupHrAttendanceId(tenantId), TenantId = tenantId, ModuleId = ModuleHrId(tenantId), Name = "Time & Attendance", Icon = "calendar-clock", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupHrPayrollId(tenantId), TenantId = tenantId, ModuleId = ModuleHrId(tenantId), Name = "Payroll", Icon = "wallet", SortOrder = 30, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupHrRecruitmentId(tenantId), TenantId = tenantId, ModuleId = ModuleHrId(tenantId), Name = "Talent & Recruitment", Icon = "user-check", SortOrder = 40, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // PROCUREMENT
        new() { Id = GroupProcOrdersId(tenantId), TenantId = tenantId, ModuleId = ModuleProcurementId(tenantId), Name = "Purchasing", Icon = "shopping-cart", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupProcReceiptsId(tenantId), TenantId = tenantId, ModuleId = ModuleProcurementId(tenantId), Name = "Receiving & Invoicing", Icon = "receipt", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // ACCOUNTING
        new() { Id = GroupAccGlId(tenantId), TenantId = tenantId, ModuleId = ModuleAccountingId(tenantId), Name = "General Ledger", Icon = "book-open", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupAccClosingId(tenantId), TenantId = tenantId, ModuleId = ModuleAccountingId(tenantId), Name = "Periods & Closing", Icon = "calendar-check", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // FINANCE
        new() { Id = GroupFinInvoicesId(tenantId), TenantId = tenantId, ModuleId = ModuleFinanceId(tenantId), Name = "Invoices & Payments", Icon = "file-spreadsheet", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupFinBankingId(tenantId), TenantId = tenantId, ModuleId = ModuleFinanceId(tenantId), Name = "Treasury & Banking", Icon = "landmark", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupFinTaxId(tenantId), TenantId = tenantId, ModuleId = ModuleFinanceId(tenantId), Name = "Taxation", Icon = "receipt", SortOrder = 30, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupFinAssetsId(tenantId), TenantId = tenantId, ModuleId = ModuleFinanceId(tenantId), Name = "Fixed Assets", Icon = "layers", SortOrder = 40, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupFinEmployeeId(tenantId), TenantId = tenantId, ModuleId = ModuleFinanceId(tenantId), Name = "Employee Claims", Icon = "receipt-text", SortOrder = 50, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // CRM
        new() { Id = GroupCrmPipelineId(tenantId), TenantId = tenantId, ModuleId = ModuleCrmId(tenantId), Name = "Sales Pipeline", Icon = "trending-up", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupCrmCustomersId(tenantId), TenantId = tenantId, ModuleId = ModuleCrmId(tenantId), Name = "Customers & Contracts", Icon = "handshake", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupCrmSettingsId(tenantId), TenantId = tenantId, ModuleId = ModuleCrmId(tenantId), Name = "Commissions & Settings", Icon = "settings-2", SortOrder = 30, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // STOCK
        new() { Id = GroupStockOpsId(tenantId), TenantId = tenantId, ModuleId = ModuleStockId(tenantId), Name = "Inventory Operations", Icon = "boxes", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupStockMasterId(tenantId), TenantId = tenantId, ModuleId = ModuleStockId(tenantId), Name = "Warehouses & Catalog", Icon = "warehouse", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // ANALYTICS
        new() { Id = GroupUtamaId(tenantId), TenantId = tenantId, ModuleId = ModuleAnalyticsId(tenantId), Name = "Main", Icon = "layout-dashboard", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupLaporanId(tenantId), TenantId = tenantId, ModuleId = ModuleAnalyticsId(tenantId), Name = "Reports", Icon = "chart-column", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

        // ADMINISTRATION
        new() { Id = GroupAdminSystemId(tenantId), TenantId = tenantId, ModuleId = ModuleAdminId(tenantId), Name = "System & Users", Icon = "shield-check", SortOrder = 10, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupAdminMetadataId(tenantId), TenantId = tenantId, ModuleId = ModuleAdminId(tenantId), Name = "Metadata & Customization", Icon = "code-xml", SortOrder = 20, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
        new() { Id = GroupAdminIntegrationsId(tenantId), TenantId = tenantId, ModuleId = ModuleAdminId(tenantId), Name = "Integration & Settings", Icon = "webhook", SortOrder = 30, IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now }
    ];

    public static IReadOnlyList<NavigationMenuItem> CreateDefaultItems(string tenantId, DateTimeOffset now)
    {
        var items = new List<NavigationMenuItem>
        {
            // ==================== ANALYTICS (Universal Employee & Dashboards) ====================
            new() { Id = $"menu_{tenantId}_beranda", TenantId = tenantId, Title = "Home", Href = "", Icon = "house", GroupId = GroupUtamaId(tenantId), GroupName = "Main", SortOrder = 10, AllowedRoles = [], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_dashboard", TenantId = tenantId, Title = "Dashboard", Href = "dashboard", Icon = "layout-dashboard", GroupId = GroupUtamaId(tenantId), GroupName = "Main", SortOrder = 20, AllowedRoles = [], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_inbox", TenantId = tenantId, Title = "Inbox", Href = "inbox", Icon = "inbox", GroupId = GroupUtamaId(tenantId), GroupName = "Main", SortOrder = 30, AllowedRoles = [], IsSystem = true, IsActive = true, BadgeKey = BadgeInboxUnread, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_reports", TenantId = tenantId, Title = "Reports", Href = "reports", Icon = "chart-column", GroupId = GroupLaporanId(tenantId), GroupName = "Reports", SortOrder = 10, AllowedRoles = [UserContext.SystemManager], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== CORE ====================
            // Company & Parties
            new() { Id = $"menu_{tenantId}_company", TenantId = tenantId, Title = "Company", Href = "app/Company", Icon = "building", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 10, AllowedRoles = ["Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_customer", TenantId = tenantId, Title = "Customer", Href = "app/Customer", Icon = "users", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 20, AllowedRoles = ["Sales", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_customer_group", TenantId = tenantId, Title = "Customer Group", Href = "app/CustomerGroup", Icon = "folder-tree", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 25, AllowedRoles = ["Sales", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_vendor", TenantId = tenantId, Title = "Supplier", Href = "app/Vendor", Icon = "truck", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 30, AllowedRoles = ["Purchasing", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_vendor_group", TenantId = tenantId, Title = "Supplier Group", Href = "app/VendorGroup", Icon = "folder-tree", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 35, AllowedRoles = ["Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_contact", TenantId = tenantId, Title = "Contact", Href = "app/Contact", Icon = "contact", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 40, AllowedRoles = ["Sales", "Purchasing", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_address", TenantId = tenantId, Title = "Address", Href = "app/Address", Icon = "map-pin", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 50, AllowedRoles = ["Sales", "Purchasing", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_bank", TenantId = tenantId, Title = "Bank", Href = "app/Bank", Icon = "landmark", GroupId = GroupCorePartiesId(tenantId), GroupName = "Company & Parties", SortOrder = 60, AllowedRoles = ["Finance", "HR", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Product Catalog
            new() { Id = $"menu_{tenantId}_item", TenantId = tenantId, Title = "Item", Href = "app/Item", Icon = "package", GroupId = GroupCoreItemsId(tenantId), GroupName = "Product Catalog", SortOrder = 10, AllowedRoles = ["Purchasing", "Sales", "Finance", "Stock", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_item_group", TenantId = tenantId, Title = "Item Group", Href = "app/ItemGroup", Icon = "folder-tree", GroupId = GroupCoreItemsId(tenantId), GroupName = "Product Catalog", SortOrder = 20, AllowedRoles = ["Purchasing", "Sales", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_price_list", TenantId = tenantId, Title = "Price List", Href = "app/PriceList", Icon = "list-ordered", GroupId = GroupCoreItemsId(tenantId), GroupName = "Product Catalog", SortOrder = 30, AllowedRoles = ["Sales", "Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_item_price", TenantId = tenantId, Title = "Item Price", Href = "app/ItemPrice", Icon = "badge-dollar-sign", GroupId = GroupCoreItemsId(tenantId), GroupName = "Product Catalog", SortOrder = 40, AllowedRoles = ["Sales", "Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_uom", TenantId = tenantId, Title = "UOM", Href = "app/UOM", Icon = "ruler", GroupId = GroupCoreItemsId(tenantId), GroupName = "Product Catalog", SortOrder = 50, AllowedRoles = ["Purchasing", "Sales", "Finance", "Stock", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Settings & Regional
            new() { Id = $"menu_{tenantId}_currency", TenantId = tenantId, Title = "Currency", Href = "app/Currency", Icon = "coins", GroupId = GroupCoreSettingsId(tenantId), GroupName = "Settings & Regional", SortOrder = 10, AllowedRoles = ["Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_exchange_rate", TenantId = tenantId, Title = "Exchange Rate", Href = "app/CurrencyExchangeRate", Icon = "arrow-left-right", GroupId = GroupCoreSettingsId(tenantId), GroupName = "Settings & Regional", SortOrder = 20, AllowedRoles = ["Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payment_term", TenantId = tenantId, Title = "Payment Terms", Href = "app/PaymentTerm", Icon = "calendar", GroupId = GroupCoreSettingsId(tenantId), GroupName = "Settings & Regional", SortOrder = 30, AllowedRoles = ["Sales", "Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_location", TenantId = tenantId, Title = "Location", Href = "app/Location", Icon = "map-pinned", GroupId = GroupCoreSettingsId(tenantId), GroupName = "Settings & Regional", SortOrder = 40, AllowedRoles = ["HR", "Stock", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_global_defaults", TenantId = tenantId, Title = "General Settings", Href = "app/GlobalDefaults", Icon = "settings", GroupId = GroupCoreSettingsId(tenantId), GroupName = "Settings & Regional", SortOrder = 50, AllowedRoles = ["System Manager", "Finance"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== HUMAN RESOURCES ====================
            // Employees & Structure
            new() { Id = $"menu_{tenantId}_employee", TenantId = tenantId, Title = "Employee", Href = "app/Employee", Icon = "user", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 10, AllowedRoles = ["HR", "Manager", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_org_unit", TenantId = tenantId, Title = "Organization Unit", Href = "app/OrganizationUnit", Icon = "layers", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 20, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_position", TenantId = tenantId, Title = "Position", Href = "app/Position", Icon = "briefcase", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 30, AllowedRoles = ["HR", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_contract", TenantId = tenantId, Title = "Employment Contract", Href = "app/EmploymentContract", Icon = "file-text", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 50, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_grade", TenantId = tenantId, Title = "Grade", Href = "app/Grade", Icon = "award", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 60, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_separation", TenantId = tenantId, Title = "Employee Separation", Href = "app/EmployeeSeparation", Icon = "user-x", GroupId = GroupHrEmployeesId(tenantId), GroupName = "Employees & Structure", SortOrder = 70, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Time & Attendance
            new() { Id = $"menu_{tenantId}_attendance", TenantId = tenantId, Title = "Attendance", Href = "app/Attendance", Icon = "clock", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 10, AllowedRoles = ["HR", "Manager", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_leave_application", TenantId = tenantId, Title = "Leave Application", Href = "app/LeaveApplication", Icon = "calendar-days", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 20, AllowedRoles = ["Employee", "HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_leave_allocation", TenantId = tenantId, Title = "Leave Allocation", Href = "app/LeaveAllocation", Icon = "calendar-plus", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 30, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_leave_type", TenantId = tenantId, Title = "Leave Type", Href = "app/LeaveType", Icon = "calendar-range", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 40, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_leave_policy", TenantId = tenantId, Title = "Leave Policy", Href = "app/LeavePolicy", Icon = "book", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 45, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_overtime", TenantId = tenantId, Title = "Overtime Request", Href = "app/OvertimeRequest", Icon = "timer", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 50, AllowedRoles = ["Employee", "HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_shift_type", TenantId = tenantId, Title = "Shift Type", Href = "app/ShiftType", Icon = "sun-moon", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 60, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_holiday_list", TenantId = tenantId, Title = "Holiday List", Href = "app/HolidayList", Icon = "calendar-heart", GroupId = GroupHrAttendanceId(tenantId), GroupName = "Time & Attendance", SortOrder = 70, AllowedRoles = ["HR", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Payroll
            new() { Id = $"menu_{tenantId}_payroll_run", TenantId = tenantId, Title = "Payroll Run", Href = "app/PayrollRun", Icon = "play-circle", GroupId = GroupHrPayrollId(tenantId), GroupName = "Payroll", SortOrder = 10, AllowedRoles = ["Payroll", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payslip", TenantId = tenantId, Title = "Payslip", Href = "app/Payslip", Icon = "receipt", GroupId = GroupHrPayrollId(tenantId), GroupName = "Payroll", SortOrder = 20, AllowedRoles = ["Employee", "Payroll", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_salary_assignment", TenantId = tenantId, Title = "Salary Assignment", Href = "app/SalaryAssignment", Icon = "badge-check", GroupId = GroupHrPayrollId(tenantId), GroupName = "Payroll", SortOrder = 30, AllowedRoles = ["Payroll", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_salary_component", TenantId = tenantId, Title = "Salary Component", Href = "app/SalaryComponent", Icon = "coins", GroupId = GroupHrPayrollId(tenantId), GroupName = "Payroll", SortOrder = 40, AllowedRoles = ["Payroll", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payroll_settings", TenantId = tenantId, Title = "Payroll Settings", Href = "app/PayrollSettings", Icon = "settings", GroupId = GroupHrPayrollId(tenantId), GroupName = "Payroll", SortOrder = 50, AllowedRoles = ["Payroll", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Talent & Recruitment
            new() { Id = $"menu_{tenantId}_job_requisition", TenantId = tenantId, Title = "Job Requisition", Href = "app/JobRequisition", Icon = "file-search", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 10, AllowedRoles = ["HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_job_applicant", TenantId = tenantId, Title = "Job Applicant", Href = "app/JobApplicant", Icon = "user-plus", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 20, AllowedRoles = ["HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_job_offer", TenantId = tenantId, Title = "Job Offer", Href = "app/JobOffer", Icon = "mail-check", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 30, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_interview", TenantId = tenantId, Title = "Interview", Href = "app/Interview", Icon = "messages-square", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 40, AllowedRoles = ["HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_appraisal", TenantId = tenantId, Title = "Appraisal", Href = "app/Appraisal", Icon = "award", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 50, AllowedRoles = ["Employee", "HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_appraisal_cycle", TenantId = tenantId, Title = "Appraisal Cycle", Href = "app/AppraisalCycle", Icon = "repeat", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 60, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_training_event", TenantId = tenantId, Title = "Training Event", Href = "app/TrainingEvent", Icon = "graduation-cap", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 70, AllowedRoles = ["HR", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_hr_settings", TenantId = tenantId, Title = "HR Settings", Href = "app/HRSettings", Icon = "settings", GroupId = GroupHrRecruitmentId(tenantId), GroupName = "Talent & Recruitment", SortOrder = 80, AllowedRoles = ["HR", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== PROCUREMENT ====================
            // Purchasing
            new() { Id = $"menu_{tenantId}_purchase_request", TenantId = tenantId, Title = "Purchase Request", Href = "app/PurchaseRequest", Icon = "file-question", GroupId = GroupProcOrdersId(tenantId), GroupName = "Purchasing", SortOrder = 10, AllowedRoles = ["Purchasing", "Employee", "Manager", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_purchase_order", TenantId = tenantId, Title = "Purchase Order", Href = "app/PurchaseOrder", Icon = "file-check", GroupId = GroupProcOrdersId(tenantId), GroupName = "Purchasing", SortOrder = 20, AllowedRoles = ["Purchasing", "Manager", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_proc_vendor", TenantId = tenantId, Title = "Supplier", Href = "app/Vendor", Icon = "truck", GroupId = GroupProcOrdersId(tenantId), GroupName = "Purchasing", SortOrder = 30, AllowedRoles = ["Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Receiving & Invoicing
            new() { Id = $"menu_{tenantId}_proc_goods_receipt", TenantId = tenantId, Title = "Goods Receipt", Href = "app/GoodsReceipt", Icon = "package-check", GroupId = GroupProcReceiptsId(tenantId), GroupName = "Receiving & Invoicing", SortOrder = 10, AllowedRoles = ["Purchasing", "Stock", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_proc_purchase_invoice", TenantId = tenantId, Title = "Purchase Invoice", Href = "app/PurchaseInvoice", Icon = "receipt", GroupId = GroupProcReceiptsId(tenantId), GroupName = "Receiving & Invoicing", SortOrder = 20, AllowedRoles = ["Finance", "Purchasing", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_proc_debit_note", TenantId = tenantId, Title = "Purchase Debit Note", Href = "app/PurchaseDebitNote", Icon = "file-minus", GroupId = GroupProcReceiptsId(tenantId), GroupName = "Receiving & Invoicing", SortOrder = 30, AllowedRoles = ["Finance", "Purchasing", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== ACCOUNTING ====================
            // General Ledger
            new() { Id = $"menu_{tenantId}_account", TenantId = tenantId, Title = "Chart of Accounts", Href = "app/Account", Icon = "list-tree", GroupId = GroupAccGlId(tenantId), GroupName = "General Ledger", SortOrder = 10, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_journal_entry", TenantId = tenantId, Title = "Journal Entry", Href = "app/JournalEntry", Icon = "book-open", GroupId = GroupAccGlId(tenantId), GroupName = "General Ledger", SortOrder = 20, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_cost_center", TenantId = tenantId, Title = "Cost Center", Href = "app/CostCenter", Icon = "target", GroupId = GroupAccGlId(tenantId), GroupName = "General Ledger", SortOrder = 30, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_project", TenantId = tenantId, Title = "Project", Href = "app/Project", Icon = "folder-kanban", GroupId = GroupAccGlId(tenantId), GroupName = "General Ledger", SortOrder = 40, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_dimension", TenantId = tenantId, Title = "Accounting Dimension", Href = "app/AccountingDimension", Icon = "tag", GroupId = GroupAccGlId(tenantId), GroupName = "General Ledger", SortOrder = 50, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Periods & Closing
            new() { Id = $"menu_{tenantId}_fiscal_year", TenantId = tenantId, Title = "Fiscal Year", Href = "app/FiscalYear", Icon = "calendar", GroupId = GroupAccClosingId(tenantId), GroupName = "Periods & Closing", SortOrder = 10, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_accounting_period", TenantId = tenantId, Title = "Accounting Period", Href = "app/AccountingPeriod", Icon = "calendar-range", GroupId = GroupAccClosingId(tenantId), GroupName = "Periods & Closing", SortOrder = 20, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_period_closing", TenantId = tenantId, Title = "Period Closing Voucher", Href = "app/PeriodClosingVoucher", Icon = "lock", GroupId = GroupAccClosingId(tenantId), GroupName = "Periods & Closing", SortOrder = 30, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_budget", TenantId = tenantId, Title = "Budget", Href = "app/Budget", Icon = "wallet", GroupId = GroupAccClosingId(tenantId), GroupName = "Periods & Closing", SortOrder = 40, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_accounting_settings", TenantId = tenantId, Title = "Accounting Settings", Href = "app/AccountingSettings", Icon = "settings", GroupId = GroupAccClosingId(tenantId), GroupName = "Periods & Closing", SortOrder = 50, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== FINANCE ====================
            // Invoices & Payments
            new() { Id = $"menu_{tenantId}_sales_invoice", TenantId = tenantId, Title = "Sales Invoice", Href = "app/SalesInvoice", Icon = "file-text", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 10, AllowedRoles = ["Finance", "Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_sales_credit_note", TenantId = tenantId, Title = "Sales Credit Note", Href = "app/SalesCreditNote", Icon = "file-minus", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 20, AllowedRoles = ["Finance", "Sales", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_purchase_invoice", TenantId = tenantId, Title = "Purchase Invoice", Href = "app/PurchaseInvoice", Icon = "receipt", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 30, AllowedRoles = ["Finance", "Purchasing", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_purchase_debit_note", TenantId = tenantId, Title = "Purchase Debit Note", Href = "app/PurchaseDebitNote", Icon = "file-minus", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 40, AllowedRoles = ["Finance", "Purchasing", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payment_entry", TenantId = tenantId, Title = "Payment Entry", Href = "app/PaymentEntry", Icon = "credit-card", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 50, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payment_reconciliation", TenantId = tenantId, Title = "Payment Reconciliation", Href = "app/PaymentReconciliation", Icon = "split", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 60, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_payment_run", TenantId = tenantId, Title = "Payment Run", Href = "app/PaymentRun", Icon = "send", GroupId = GroupFinInvoicesId(tenantId), GroupName = "Invoices & Payments", SortOrder = 70, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Treasury & Banking
            new() { Id = $"menu_{tenantId}_bank_account", TenantId = tenantId, Title = "Bank Account", Href = "app/BankAccount", Icon = "landmark", GroupId = GroupFinBankingId(tenantId), GroupName = "Treasury & Banking", SortOrder = 10, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_bank_statement", TenantId = tenantId, Title = "Bank Statement", Href = "app/BankStatement", Icon = "file-spreadsheet", GroupId = GroupFinBankingId(tenantId), GroupName = "Treasury & Banking", SortOrder = 20, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_bank_reconciliation", TenantId = tenantId, Title = "Bank Reconciliation", Href = "app/BankReconciliation", Icon = "check-check", GroupId = GroupFinBankingId(tenantId), GroupName = "Treasury & Banking", SortOrder = 30, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Taxation
            new() { Id = $"menu_{tenantId}_tax_invoice", TenantId = tenantId, Title = "Tax Invoice (Faktur Pajak)", Href = "app/TaxInvoice", Icon = "receipt", GroupId = GroupFinTaxId(tenantId), GroupName = "Taxation", SortOrder = 10, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_withholding_cert", TenantId = tenantId, Title = "Withholding Certificate (Bukti Potong)", Href = "app/WithholdingCertificate", Icon = "stamp", GroupId = GroupFinTaxId(tenantId), GroupName = "Taxation", SortOrder = 20, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_tax_code", TenantId = tenantId, Title = "Tax Code", Href = "app/TaxCode", Icon = "percent", GroupId = GroupFinTaxId(tenantId), GroupName = "Taxation", SortOrder = 30, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Fixed Assets
            new() { Id = $"menu_{tenantId}_asset", TenantId = tenantId, Title = "Fixed Asset", Href = "app/Asset", Icon = "layers", GroupId = GroupFinAssetsId(tenantId), GroupName = "Fixed Assets", SortOrder = 10, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_depreciation_run", TenantId = tenantId, Title = "Depreciation Run", Href = "app/DepreciationRun", Icon = "trending-down", GroupId = GroupFinAssetsId(tenantId), GroupName = "Fixed Assets", SortOrder = 20, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_asset_disposal", TenantId = tenantId, Title = "Asset Disposal", Href = "app/AssetDisposal", Icon = "trash-2", GroupId = GroupFinAssetsId(tenantId), GroupName = "Fixed Assets", SortOrder = 30, AllowedRoles = ["Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_asset_category", TenantId = tenantId, Title = "Asset Category", Href = "app/AssetCategory", Icon = "folder-tree", GroupId = GroupFinAssetsId(tenantId), GroupName = "Fixed Assets", SortOrder = 40, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Employee Claims
            new() { Id = $"menu_{tenantId}_expense_claim", TenantId = tenantId, Title = "Expense Claim", Href = "app/ExpenseClaim", Icon = "receipt-text", GroupId = GroupFinEmployeeId(tenantId), GroupName = "Employee Claims", SortOrder = 10, AllowedRoles = ["Employee", "Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_employee_advance", TenantId = tenantId, Title = "Employee Advance", Href = "app/EmployeeAdvance", Icon = "hand-coins", GroupId = GroupFinEmployeeId(tenantId), GroupName = "Employee Claims", SortOrder = 20, AllowedRoles = ["Employee", "Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_finance_settings", TenantId = tenantId, Title = "Finance Settings", Href = "app/FinanceSettings", Icon = "settings", GroupId = GroupFinEmployeeId(tenantId), GroupName = "Employee Claims", SortOrder = 30, AllowedRoles = ["Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== CRM & SALES ====================
            // Sales Pipeline
            new() { Id = $"menu_{tenantId}_lead", TenantId = tenantId, Title = "Lead", Href = "app/Lead", Icon = "user-plus", GroupId = GroupCrmPipelineId(tenantId), GroupName = "Sales Pipeline", SortOrder = 10, AllowedRoles = ["Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_opportunity", TenantId = tenantId, Title = "Opportunity", Href = "app/Opportunity", Icon = "target", GroupId = GroupCrmPipelineId(tenantId), GroupName = "Sales Pipeline", SortOrder = 20, AllowedRoles = ["Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_quotation", TenantId = tenantId, Title = "Quotation", Href = "app/Quotation", Icon = "file-text", GroupId = GroupCrmPipelineId(tenantId), GroupName = "Sales Pipeline", SortOrder = 30, AllowedRoles = ["Sales", "Manager", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_sales_order", TenantId = tenantId, Title = "Sales Order", Href = "app/SalesOrder", Icon = "shopping-bag", GroupId = GroupCrmPipelineId(tenantId), GroupName = "Sales Pipeline", SortOrder = 40, AllowedRoles = ["Sales", "Manager", "Finance", "Stock", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Customers & Contracts
            new() { Id = $"menu_{tenantId}_crm_customer", TenantId = tenantId, Title = "Customer", Href = "app/Customer", Icon = "users", GroupId = GroupCrmCustomersId(tenantId), GroupName = "Customers & Contracts", SortOrder = 10, AllowedRoles = ["Sales", "Finance", "Employee", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_customer_contract", TenantId = tenantId, Title = "Customer Contract", Href = "app/CustomerContract", Icon = "handshake", GroupId = GroupCrmCustomersId(tenantId), GroupName = "Customers & Contracts", SortOrder = 20, AllowedRoles = ["Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_activity", TenantId = tenantId, Title = "Activity", Href = "app/Activity", Icon = "check-square", GroupId = GroupCrmCustomersId(tenantId), GroupName = "Customers & Contracts", SortOrder = 30, AllowedRoles = ["Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_campaign", TenantId = tenantId, Title = "Campaign", Href = "app/Campaign", Icon = "megaphone", GroupId = GroupCrmCustomersId(tenantId), GroupName = "Customers & Contracts", SortOrder = 40, AllowedRoles = ["Sales", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Commissions & Settings
            new() { Id = $"menu_{tenantId}_commission_plan", TenantId = tenantId, Title = "Commission Plan", Href = "app/CommissionPlan", Icon = "badge-percent", GroupId = GroupCrmSettingsId(tenantId), GroupName = "Commissions & Settings", SortOrder = 10, AllowedRoles = ["Sales", "Manager", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_commission_statement", TenantId = tenantId, Title = "Commission Statement", Href = "app/CommissionStatement", Icon = "file-spreadsheet", GroupId = GroupCrmSettingsId(tenantId), GroupName = "Commissions & Settings", SortOrder = 20, AllowedRoles = ["Sales", "Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_crm_settings", TenantId = tenantId, Title = "CRM Settings", Href = "app/CRMSettings", Icon = "settings", GroupId = GroupCrmSettingsId(tenantId), GroupName = "Commissions & Settings", SortOrder = 30, AllowedRoles = ["Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== STOCK ====================
            // Inventory Operations
            new() { Id = $"menu_{tenantId}_stock_entry", TenantId = tenantId, Title = "Stock Entry", Href = "app/StockEntry", Icon = "arrow-up-down", GroupId = GroupStockOpsId(tenantId), GroupName = "Inventory Operations", SortOrder = 10, AllowedRoles = ["Stock", "Purchasing", "Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_stock_reconciliation", TenantId = tenantId, Title = "Stock Opname", Href = "app/StockReconciliation", Icon = "clipboard-check", GroupId = GroupStockOpsId(tenantId), GroupName = "Inventory Operations", SortOrder = 20, AllowedRoles = ["Stock", "Purchasing", "Finance", "Manager", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_stock_goods_receipt", TenantId = tenantId, Title = "Goods Receipt", Href = "app/GoodsReceipt", Icon = "package-check", GroupId = GroupStockOpsId(tenantId), GroupName = "Inventory Operations", SortOrder = 30, AllowedRoles = ["Stock", "Purchasing", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Warehouses & Catalog
            new() { Id = $"menu_{tenantId}_warehouse", TenantId = tenantId, Title = "Warehouse", Href = "app/Warehouse", Icon = "warehouse", GroupId = GroupStockMasterId(tenantId), GroupName = "Warehouses & Catalog", SortOrder = 10, AllowedRoles = ["Stock", "Purchasing", "Sales", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_stock_item", TenantId = tenantId, Title = "Item Catalog", Href = "app/Item", Icon = "package", GroupId = GroupStockMasterId(tenantId), GroupName = "Warehouses & Catalog", SortOrder = 20, AllowedRoles = ["Stock", "Purchasing", "Sales", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_stock_settings", TenantId = tenantId, Title = "Stock Settings", Href = "app/StockSettings", Icon = "settings", GroupId = GroupStockMasterId(tenantId), GroupName = "Warehouses & Catalog", SortOrder = 30, AllowedRoles = ["Stock", "Finance", "System Manager"], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // ==================== ADMINISTRATION ====================
            // System & Users
            new() { Id = $"menu_{tenantId}_users", TenantId = tenantId, Title = "Users", Href = "admin/users", Icon = "users", GroupId = GroupAdminSystemId(tenantId), GroupName = "System & Users", SortOrder = 10, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_roles", TenantId = tenantId, Title = "User Roles", Href = "admin/roles", Icon = "shield-check", GroupId = GroupAdminSystemId(tenantId), GroupName = "System & Users", SortOrder = 20, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_scheduled_jobs", TenantId = tenantId, Title = "Scheduled Jobs", Href = "admin/jobs", Icon = "clock", GroupId = GroupAdminSystemId(tenantId), GroupName = "System & Users", SortOrder = 30, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_error_logs", TenantId = tenantId, Title = "Error Log", Href = "admin/error-logs", Icon = "triangle-alert", GroupId = GroupAdminSystemId(tenantId), GroupName = "System & Users", SortOrder = 40, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Metadata & Customization
            new() { Id = $"menu_{tenantId}_metadata", TenantId = tenantId, Title = "DocType Metadata", Href = "admin/metadata", Icon = "settings-2", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 10, AllowedRoles = [UserContext.SystemManager], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_workflows", TenantId = tenantId, Title = "Workflows", Href = "admin/workflows", Icon = "git-branch", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 20, AllowedRoles = [UserContext.SystemManager], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_server_scripts", TenantId = tenantId, Title = "Server Scripts", Href = "admin/server-scripts", Icon = "code-xml", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 30, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_auto_repeats", TenantId = tenantId, Title = "Auto Repeats", Href = "admin/auto-repeats", Icon = "repeat", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 40, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_report_templates", TenantId = tenantId, Title = "Excel/Word Reports", Href = "admin/report-templates", Icon = "file-spreadsheet", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 50, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_dashboards_admin", TenantId = tenantId, Title = "Dashboards", Href = "admin/dashboards", Icon = "layout-dashboard", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 60, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_widgets", TenantId = tenantId, Title = "Widgets", Href = "admin/widgets", Icon = "layout-grid", GroupId = GroupAdminMetadataId(tenantId), GroupName = "Metadata & Customization", SortOrder = 70, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },

            // Integration & Settings
            new() { Id = $"menu_{tenantId}_query_apis", TenantId = tenantId, Title = "Query APIs", Href = "query-apis", Icon = "braces", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 10, AllowedRoles = [UserContext.SystemManager], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_api_keys", TenantId = tenantId, Title = "API Keys", Href = "admin/api-keys", Icon = "key-round", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 20, AllowedRoles = [UserContext.SystemManager], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_webhooks", TenantId = tenantId, Title = "Webhooks", Href = "admin/webhooks", Icon = "webhook", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 30, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_email_accounts", TenantId = tenantId, Title = "Email Accounts", Href = "admin/email-accounts", Icon = "mail", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 40, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_notifications", TenantId = tenantId, Title = "Notification Rules", Href = "admin/notifications", Icon = "bell", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 50, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_translations", TenantId = tenantId, Title = "Translations", Href = "admin/translations", Icon = "languages", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 60, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_module_admin", TenantId = tenantId, Title = "Master Modules", Href = "admin/modules", Icon = "boxes", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 70, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_menu_group_admin", TenantId = tenantId, Title = "Menu Groups", Href = "admin/menu-groups", Icon = "folders", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 80, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new() { Id = $"menu_{tenantId}_menu_admin", TenantId = tenantId, Title = "Manage Menus", Href = "admin/menus", Icon = "panel-left", GroupId = GroupAdminIntegrationsId(tenantId), GroupName = "Integration & Settings", SortOrder = 90, AllowedRoles = [UserContext.SystemManager, UserContext.Administrator], IsSystem = true, IsActive = true, CreatedAt = now, UpdatedAt = now }
        };

        return items;
    }
}
