namespace Framework.Security;

public static class RoleDefaults
{
    private static readonly object Gate = new();
    private static (string Name, string Description)[] _appRoles = [];

    /// <summary>
    /// Roles an application adds to every tenant's role list (created if missing, see EnsureDefaultRolesAsync).
    /// Idempotent: registering the same name again replaces its description.
    /// </summary>
    public static void RegisterAppRoles(IEnumerable<(string Name, string Description)> roles)
    {
        lock (Gate)
        {
            var map = _appRoles.ToDictionary(r => r.Name, r => r.Description, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, description) in roles) map[name] = description;
            _appRoles = map.Select(kv => (kv.Key, kv.Value)).ToArray();
        }
    }

    /// <summary>Roles registered by the application (in registration order is not guaranteed).</summary>
    public static IReadOnlyList<(string Name, string Description)> AppRoles => _appRoles;

    public static IReadOnlyList<RoleDef> CreateDefaultRoles(string tenantId, DateTimeOffset now)
    {
        var builtIn = BuiltIn(tenantId, now);
        var names = builtIn.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. builtIn, .. _appRoles.Where(r => !names.Contains(r.Name)).Select(r => new RoleDef
        {
            Id = $"role_{tenantId}_{r.Name.ToLowerInvariant().Replace(' ', '_').Replace('-', '_')}",
            TenantId = tenantId,
            Name = r.Name,
            Description = r.Description,
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now,
        })];
    }

    private static List<RoleDef> BuiltIn(string tenantId, DateTimeOffset now) =>
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

