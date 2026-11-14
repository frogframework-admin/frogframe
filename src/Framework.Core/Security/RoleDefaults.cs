namespace Framework.Security;

public static class RoleDefaults
{
    public static IReadOnlyList<RoleDef> CreateDefaultRoles(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = $"role_{tenantId}_administrator",
            TenantId = tenantId,
            Name = "Administrator",
            Description = "Full access to all system configurations, data, and user management.",
            IsSystem = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_system_manager",
            TenantId = tenantId,
            Name = "System Manager",
            Description = "Technical system manager (DocTypes, workflows, API integrations).",
            IsSystem = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_user",
            TenantId = tenantId,
            Name = "User",
            Description = "Standard application user.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_employee",
            TenantId = tenantId,
            Name = "Employee",
            Description = "Internal company employee.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_hr",
            TenantId = tenantId,
            Name = "HR",
            Description = "Human Resources division (Employees, Departments, Organizational Structure).",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_payroll",
            TenantId = tenantId,
            Name = "Payroll",
            Description = "Payroll administration, salary calculation, and compensation management.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_purchasing",
            TenantId = tenantId,
            Name = "Purchasing",
            Description = "Procurement of Goods & Services division (Purchase Requests, Vendors, Products).",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_manager",
            TenantId = tenantId,
            Name = "Manager",
            Description = "Division manager / document approval supervisor.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_finance",
            TenantId = tenantId,
            Name = "Finance",
            Description = "Finance division and budget verification.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_procurement",
            TenantId = tenantId,
            Name = "Procurement",
            Description = "Procurement and order fulfillment officer.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_sales",
            TenantId = tenantId,
            Name = "Sales",
            Description = "Sales division and CRM operations.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_stock",
            TenantId = tenantId,
            Name = "Stock",
            Description = "Warehouse and inventory operations.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];
}

