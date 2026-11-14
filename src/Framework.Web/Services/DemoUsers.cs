using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web.Services;

public sealed record DemoPersona(
    string Id,
    string UserName,
    string FullName,
    string TenantId,
    string Department,
    string? EmployeeId,
    IReadOnlyList<string> Roles,
    string Designation
);

public static class DemoUsers
{
    public const string DefaultPassword = "FrogFrame#2026";

    public static readonly IReadOnlyList<DemoPersona> All =
    [
        new("admin", "admin", "Admin Sistem", "acme", "IT", null, [UserContext.Administrator, UserContext.SystemManager], "System Administrator"),
        new("budi", "budi", "Budi Santoso", "acme", "IT", "EMP-0003", ["Employee", "Manager"], "IT & Operations Manager"),
        new("dewi", "dewi", "Dewi Anggraini", "acme", "FINANCE", "EMP-0005", ["Employee", "Finance"], "Finance & Accounting Head"),
        new("rina", "rina", "Rina Kusuma", "acme", "PURCHASING", "EMP-0006", ["Employee", "Purchasing", "Procurement"], "Purchasing & Procurement Specialist"),
        new("citra", "citra", "Citra Handayani", "acme", "HR", "EMP-0009", ["Employee", "HR"], "Human Resources Specialist"),
        new("sari", "sari", "Sari Wulandari", "acme", "MARKETING", "EMP-0002", ["Employee", "Sales"], "Sales & Marketing Officer"),
        new("maya", "maya", "Maya Lestari", "acme", "MARKETING", "EMP-0004", ["Employee", "Manager", "Sales"], "Commercial & Sales Manager"),
        new("joko", "joko", "Joko Prasetyo", "acme", "WAREHOUSE", "EMP-0010", ["Employee", "Stock"], "Warehouse & Stock Officer"),
        new("andi", "andi", "Andi Pratama", "acme", "IT", "EMP-0001", ["Employee", "User"], "Software Developer / General Staff"),
        new("tono", "tono", "Tono Wijaya", "globex", "IT", "EMP-0001", ["Employee", "Manager"], "Branch Manager (Globex Tenant)"),
    ];

    /// <summary>
    /// Memastikan seluruh sample persona demo terdaftar di repositori user dan memiliki role yang sesuai.
    /// </summary>
    public static async Task EnsureDemoUsersAsync(IServiceProvider sp, string? password = null, CancellationToken ct = default)
    {
        await using var scope = sp.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var pwd = string.IsNullOrWhiteSpace(password) ? DefaultPassword : password;

        foreach (var p in All)
        {
            var existing = await accounts.FindByIdAsync(p.Id, ct);
            if (existing is null)
            {
                var user = new UserAccount
                {
                    Id = p.Id,
                    UserName = p.UserName,
                    FullName = p.FullName,
                    Email = $"{p.UserName}@{p.TenantId}.co.id",
                    TenantId = p.TenantId,
                    Department = p.Department,
                    EmployeeId = p.EmployeeId,
                    Roles = p.Roles.ToList(),
                    IsActive = true
                };
                await userManager.CreateAsync(user, pwd);
            }
            else
            {
                var updated = false;
                foreach (var r in p.Roles)
                {
                    if (!existing.Roles.Contains(r, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Roles.Add(r);
                        updated = true;
                    }
                }
                if (existing.Department != p.Department)
                {
                    existing.Department = p.Department;
                    updated = true;
                }
                if (updated)
                {
                    await accounts.UpdateAsync(existing, ct);
                }
            }
        }
    }
}
