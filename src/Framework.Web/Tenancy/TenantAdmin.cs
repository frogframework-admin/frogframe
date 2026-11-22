using System.Security.Claims;
using Framework.Security;
using Framework.Tenancy;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Tenancy;

/// <summary>
/// Who may manage tenants: user ids listed in <c>Platform:Administrators</c> (comma separated or an array), for
/// example <c>FROGFRAME_PLATFORM_ADMINS=admin</c> in Docker Compose. Deliberately configuration and not a role: a System
/// Manager of one tenant must not be able to grant themselves power over the other tenants.
/// </summary>
public sealed class PlatformAdmins(IConfiguration? config)
{
    public const string Section = "Platform:Administrators";

    public IReadOnlySet<string> Ids
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config is null) return set;
            var section = config.GetSection(Section);
            foreach (var v in section.GetChildren().Select(c => c.Value).Append(section.Value))
                foreach (var id in (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    set.Add(id);
            return set;
        }
    }

    public bool IsPlatformAdmin(UserContext? user) => user is not null && Ids.Contains(user.Id);
}

public sealed record NewTenantRequest(string Id, string Name, string AdminUserName, string AdminFullName, string? AdminEmail, string AdminPassword, TenantSettings Settings);

public sealed class TenantAdminException(string message) : Exception(message);

/// <summary>Tenant management for platform (group) administrators: list with usage, create with a first administrator, suspend, settings.</summary>
public sealed class TenantAdminService(
    ITenantStore store,
    TenantDirectory directory,
    IUserAccountRepository accounts,
    PlatformAdmins admins,
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<TenantAdminService> log)
{
    public static readonly TimeSpan UsageCacheDuration = TimeSpan.FromMinutes(10);
    private const string UsageKey = "tenants:usage";

    private void Demand(UserContext user)
    {
        if (!admins.IsPlatformAdmin(user)) throw new PermissionDeniedException("Only platform administrators can manage tenants.");
    }

    /// <summary>Registered tenants plus tenant ids that have users or documents but no registry row (shown as "not registered").</summary>
    public async Task<IReadOnlyList<(Tenant Tenant, bool Registered, TenantUsage? Usage)>> ListAsync(UserContext user, bool refreshUsage = false, CancellationToken ct = default)
    {
        Demand(user);
        var tenants = await store.ListAsync(ct);
        var usage = await UsageAsync(refreshUsage, ct);
        var byId = usage.ToDictionary(u => u.TenantId, StringComparer.Ordinal);
        var list = tenants.Select(t => (t, true, byId.GetValueOrDefault(t.Id))).ToList();
        foreach (var u in usage.Where(u => tenants.All(t => t.Id != u.TenantId)))
            list.Add((new Tenant { Id = u.TenantId, Name = u.TenantId }, false, u));
        return list.OrderBy(x => x.Item1.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => (x.Item1, x.Item2, x.Item3)).ToList();
    }

    public async Task<IReadOnlyList<TenantUsage>> UsageAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && cache.TryGetValue(UsageKey, out IReadOnlyList<TenantUsage>? cached) && cached is not null) return cached;
        var usage = await store.UsageAsync(ct);
        cache.Set(UsageKey, usage, UsageCacheDuration);
        return usage;
    }

    /// <summary>Creates the tenant and its first System Manager. Nothing is created when the user name is taken or the password is rejected.</summary>
    public async Task<Tenant> CreateAsync(UserContext user, NewTenantRequest req, CancellationToken ct = default)
    {
        Demand(user);
        var id = req.Id.Trim().ToLowerInvariant();
        if (!TenantIds.IsValid(id)) throw new TenantAdminException("Tenant code: 2–32 characters, lower-case letters, digits and hyphens.");
        if (string.IsNullOrWhiteSpace(req.Name)) throw new TenantAdminException("Name is required.");
        if (await store.GetAsync(id, ct) is not null || (await accounts.ListAsync(id, ct)).Count > 0)
            throw new TenantAdminException($"Tenant code '{id}' is already in use.");
        if (string.IsNullOrWhiteSpace(req.AdminUserName) || string.IsNullOrWhiteSpace(req.AdminFullName))
            throw new TenantAdminException("The administrator's user name and full name are required.");
        Validate(req.Settings);

        var now = clock.GetUtcNow();
        var tenant = new Tenant { Id = id, Name = req.Name.Trim(), Settings = req.Settings, CreatedAt = now, UpdatedAt = now, UpdatedBy = user.Id };

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var account = new UserAccount
        {
            UserName = req.AdminUserName.Trim(), FullName = req.AdminFullName.Trim(), Email = string.IsNullOrWhiteSpace(req.AdminEmail) ? null : req.AdminEmail.Trim(),
            TenantId = id, Roles = [UserContext.SystemManager], IsActive = true,
        };
        // Password rules first, so a rejected password leaves nothing behind.
        foreach (var v in users.PasswordValidators)
        {
            var check = await v.ValidateAsync(users, account, req.AdminPassword);
            if (!check.Succeeded) throw new TenantAdminException(string.Join(" ", check.Errors.Select(e => e.Description)));
        }
        if (await users.FindByNameAsync(account.UserName) is not null)
            throw new TenantAdminException($"User name '{account.UserName}' is already used (user names are unique across all tenants).");

        await directory.SaveAsync(tenant, ct);
        var created = await users.CreateAsync(account, req.AdminPassword);
        if (!created.Succeeded)
        {
            // Keep the registry consistent with the accounts: a tenant without its administrator is of no use.
            tenant.Status = TenantStatus.Suspended;
            tenant.SuspendedReason = "Creating the first administrator failed: " + string.Join(" ", created.Errors.Select(e => e.Description));
            tenant.SuspendedAt = now;
            await directory.SaveAsync(tenant, ct);
            throw new TenantAdminException(tenant.SuspendedReason);
        }
        // Default sidebar menus and roles, as the startup seeding does for the bootstrap tenant.
        try
        {
            if (scope.ServiceProvider.GetService<Framework.Navigation.INavigationMenuStore>() is { } menus) await menus.EnsureDefaultMenusAsync(id, ct);
            if (scope.ServiceProvider.GetService<IRoleStore>() is { } roles) await roles.EnsureDefaultRolesAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Default menus/roles for new tenant {Tenant} could not be created; they are created on first use", id);
        }
        cache.Remove(UsageKey);
        log.LogInformation("Tenant {Tenant} created by {User} with administrator {Admin}", id, user.Id, account.UserName);
        return tenant;
    }

    public async Task<Tenant> UpdateAsync(UserContext user, string id, string name, TenantSettings settings, CancellationToken ct = default)
    {
        Demand(user);
        if (string.IsNullOrWhiteSpace(name)) throw new TenantAdminException("Name is required.");
        Validate(settings);
        var tenant = await store.GetAsync(id, ct) ?? new Tenant { Id = id, Name = id, CreatedAt = clock.GetUtcNow() }; // registers an older tenant
        tenant.Name = name.Trim();
        tenant.Settings = settings;
        tenant.UpdatedAt = clock.GetUtcNow();
        tenant.UpdatedBy = user.Id;
        await directory.SaveAsync(tenant, ct);
        log.LogInformation("Tenant {Tenant} settings changed by {User}", id, user.Id);
        return tenant;
    }

    /// <summary>
    /// Suspends or reactivates a tenant. A platform administrator cannot suspend their own tenant (they would lock
    /// themselves out of this page).
    /// </summary>
    public async Task<Tenant> SetStatusAsync(UserContext user, string id, bool active, string? reason, CancellationToken ct = default)
    {
        Demand(user);
        if (!active && string.Equals(user.TenantId, id, StringComparison.Ordinal))
            throw new TenantAdminException("You cannot suspend your own tenant.");
        if (!active && string.IsNullOrWhiteSpace(reason)) throw new TenantAdminException("A reason is required to suspend a tenant.");
        var tenant = await store.GetAsync(id, ct) ?? new Tenant { Id = id, Name = id, CreatedAt = clock.GetUtcNow() };
        tenant.Status = active ? TenantStatus.Active : TenantStatus.Suspended;
        tenant.SuspendedReason = active ? null : reason!.Trim();
        tenant.SuspendedAt = active ? null : clock.GetUtcNow();
        tenant.UpdatedAt = clock.GetUtcNow();
        tenant.UpdatedBy = user.Id;
        await directory.SaveAsync(tenant, ct);
        log.LogWarning("Tenant {Tenant} {Action} by {User}: {Reason}", id, active ? "reactivated" : "suspended", user.Id, reason);
        return tenant;
    }

    /// <summary>14 characters with upper case, lower case and digits (meets the password policy); shown once to the platform administrator.</summary>
    public static string TemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz", digits = "23456789";
        const string all = upper + lower + digits;
        var chars = new List<char>
        {
            upper[System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper.Length)],
            lower[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lower.Length)],
            digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)],
        };
        while (chars.Count < 14) chars.Add(all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)]);
        return new string(chars.OrderBy(_ => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000)).ToArray());
    }

    private static void Validate(TenantSettings s)
    {
        if (s.MaxUsers is < 1) throw new TenantAdminException("Maximum users must be at least 1, or empty for no limit.");
        if (s.DefaultLanguage is { Length: > 0 } lang && Framework.Localization.Languages.Normalize(lang) is null)
            throw new TenantAdminException("Unsupported language.");
        s.DefaultLanguage = s.DefaultLanguage is { Length: > 0 } l ? Framework.Localization.Languages.Normalize(l) : null;
        s.TwoFactorRequiredRoles = s.TwoFactorRequiredRoles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// Refuses requests of signed-in users whose tenant is suspended: API and Blazor calls get 403, pages sign the user
/// out and show the reason on the login page. Account pages and static files stay reachable.
/// </summary>
public sealed class TenantStatusMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantDirectory tenants)
    {
        var tenantId = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue(FrogFrameClaims.Tenant) : null;
        if (tenantId is null || await tenants.IsActiveAsync(tenantId, context.RequestAborted))
        {
            await next(context);
            return;
        }
        var path = context.Request.Path;
        if (path.StartsWithSegments("/account") || path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content")
            || path.StartsWithSegments("/health") || path.StartsWithSegments("/error") || Path.HasExtension(path.Value))
        {
            await next(context);
            return;
        }
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync("{\"title\":\"Tenant suspended\",\"status\":403,\"detail\":\"Access for this organization is suspended. Contact the group administrator.\"}");
            return;
        }
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        context.Response.Redirect("/account/login?suspended=1");
    }
}

public static class TenantRegistration
{
    /// <summary>Registers a tenant as active when it has no registry row yet (bootstrap administrator, demo data). Never changes an existing row.</summary>
    public static async Task EnsureAsync(IServiceProvider sp, string tenantId, string? name = null, CancellationToken ct = default)
    {
        if (sp.GetService<ITenantStore>() is not { } store || await store.GetAsync(tenantId, ct) is not null) return;
        var now = (sp.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        await store.SaveAsync(new Tenant { Id = tenantId, Name = string.IsNullOrWhiteSpace(name) ? tenantId : name.Trim(), CreatedAt = now, UpdatedAt = now, UpdatedBy = "system" }, ct);
    }
}

public static class TenancyServiceCollectionExtensions
{
    /// <summary>Tenant registry (in-memory by default; AddFrogFrameworkPostgreSql replaces the store), directory and admin service.</summary>
    public static IServiceCollection AddFrogFrameworkTenancy(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.TryAddSingleton<ITenantStore>(sp => new InMemoryTenantStore(sp.GetService<IUserAccountRepository>()));
        services.TryAddSingleton(sp => new TenantDirectory(sp.GetRequiredService<ITenantStore>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<Framework.Cluster.IClusterBus>()));
        services.TryAddSingleton(sp => new PlatformAdmins(sp.GetService<IConfiguration>()));
        services.TryAddSingleton<TenantAdminService>();
        return services;
    }

    /// <summary>Place after UseAuthentication.</summary>
    public static IApplicationBuilder UseFrogFrameTenantStatus(this IApplicationBuilder app) => app.UseMiddleware<TenantStatusMiddleware>();
}
