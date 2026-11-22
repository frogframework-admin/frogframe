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
    public const string DefaultPassword = Framework.Hosting.DemoMode.PublicPassword;

    public static readonly IReadOnlyList<DemoPersona> All =
    [
        new("admin", "admin", "Admin Sistem", "acme", "IT", null, [UserContext.Administrator, UserContext.SystemManager], "System Administrator"),
        new("it01", "it01", "IT Staff", "acme", "IT", "EMP-0001", ["Employee", "User"], "IT Staff"),
        new("it02", "it02", "IT Manager", "acme", "IT", "EMP-0003", ["Employee", "Manager"], "IT Manager"),
        new("hr01", "hr01", "HR Staff", "acme", "HR", "EMP-0009", ["Employee", "HR-Recruitment", "HR-Employee-Management", "HR-Time-Attendance"], "HR Staff"),
        new("hr02", "hr02", "HR Manager", "acme", "HR", "EMP-0008", ["Employee", "Manager", "HR-Admin", "HR-Recruitment", "HR-Employee-Management", "HR-Time-Attendance", "HR-Talent", "HR-Payroll"], "HR Manager"),
        new("marketing01", "marketing01", "Marketing Staff", "acme", "MARKETING", "EMP-0002", ["Employee", "CRM-Lead-Management", "CRM-Marketing"], "Marketing Staff"),
        new("marketing02", "marketing02", "Marketing Manager", "acme", "MARKETING", "EMP-0004", ["Employee", "Manager", "CRM-Admin", "CRM-Lead-Management", "CRM-Sales"], "Marketing Manager"),
        new("purchasing01", "purchasing01", "Purchasing Staff", "acme", "PURCHASING", "EMP-0006", ["Employee", "Procurement-Admin", "Procurement-Buyer"], "Purchasing & Procurement Specialist"),
        new("warehouse01", "warehouse01", "Warehouse", "acme", "WAREHOUSE", "EMP-0010", ["Employee", "Stock-Admin", "Stock-Keeper", "Procurement-Receiving"], "Warehouse Officer"),
        new("finance01", "finance01", "Finance Staff", "acme", "FINANCE", "EMP-0005", ["Employee", "Finance-Receivable", "Finance-Payable", "Finance-Tax", "Accounting-General-Ledger", "Accounting-Fixed-Assets", "Accounting-Budget-Costing"], "Finance & Accounting Specialist"),
        new("finance02", "finance02", "Finance Treasury", "acme", "FINANCE", null, ["Employee", "Finance-Admin", "Finance-Treasury", "Finance-Cashier", "Accounting-Admin", "Accounting-Closing"], "Treasury & Finance Controller"),
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
        foreach (var tenant in All.Select(p => p.TenantId).Distinct())
            await Framework.Web.Tenancy.TenantRegistration.EnsureAsync(scope.ServiceProvider, tenant, tenant == "acme" ? "ACME (demo)" : null, ct);

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
