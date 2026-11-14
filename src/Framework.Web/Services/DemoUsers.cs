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
        new("it01", "it01", "IT Staff", "acme", "IT", "EMP-0001", ["Employee", "User"], "IT Staff"),
        new("it02", "it02", "IT Manager", "acme", "IT", "EMP-0003", ["Employee", "Manager"], "IT Manager"),
        new("hr01", "hr01", "HR Staff", "acme", "HR", "EMP-0009", ["Employee", "HR"], "HR Staff"),
        new("hr02", "hr02", "HR Manager", "acme", "HR", "EMP-0008", ["Employee", "HR", "Manager", "Payroll"], "HR Manager"),
        new("marketing01", "marketing01", "Marketing Staff", "acme", "MARKETING", "EMP-0002", ["Employee", "Sales"], "Marketing Staff"),
        new("marketing02", "marketing02", "Marketing Manager", "acme", "MARKETING", "EMP-0004", ["Employee", "Manager", "Sales"], "Marketing Manager"),
        new("purchasing01", "purchasing01", "Purchasing Staff", "acme", "PURCHASING", "EMP-0006", ["Employee", "Purchasing", "Procurement"], "Purchasing & Procurement Specialist"),
        new("warehouse01", "warehouse01", "Warehouse", "acme", "WAREHOUSE", "EMP-0010", ["Employee", "Stock"], "Warehouse Officer"),
        new("finance01", "finance01", "Finance Staff", "acme", "FINANCE", "EMP-0005", ["Employee", "Finance"], "Finance & Accounting Specialist"),
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

        // Clean up legacy demo accounts that have been replaced or dropped
        string[] legacyDemoIds = ["andi", "budi", "citra", "joko", "maya", "rina", "sari", "dewi", "tono"];
        foreach (var leg in legacyDemoIds)
        {
            var oldUser = await accounts.FindByIdAsync(leg, ct);
            if (oldUser is not null)
            {
                await accounts.DeleteAsync(leg, ct);
            }
        }

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
                if (existing.FullName != p.FullName)
                {
                    existing.FullName = p.FullName;
                    updated = true;
                }
                if (existing.Department != p.Department)
                {
                    existing.Department = p.Department;
                    updated = true;
                }
                if (existing.EmployeeId != p.EmployeeId)
                {
                    existing.EmployeeId = p.EmployeeId;
                    updated = true;
                }
                if (!existing.Roles.SequenceEqual(p.Roles, StringComparer.OrdinalIgnoreCase))
                {
                    existing.Roles = p.Roles.ToList();
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
