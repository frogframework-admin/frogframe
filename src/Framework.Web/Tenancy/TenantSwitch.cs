using System.Security.Claims;
using System.Security.Cryptography;
using Framework.Security;
using Framework.Tenancy;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Tenancy;

/// <summary>
/// Lets a platform (group) administrator work in another tenant of the installation without a second account.
/// The chosen tenant lives in a separate, encrypted cookie (<see cref="CookieName"/>); the sign-in cookie is not touched.
/// On every browser request <see cref="ActingTenantClaimsTransformation"/> turns the principal into one for that tenant:
/// the tenant claim becomes the chosen tenant, roles become System Manager, employee and department are removed (they
/// belong to the home tenant) and <see cref="FrogFrameClaims.HomeTenant"/> keeps the original tenant.
/// Rules: only user ids in <c>Platform:Administrators</c>; only active tenants; API bearer tokens and API keys are not
/// affected (they carry no cookie); the cookie ends with the browser session and is ignored as soon as the user is no
/// longer a platform administrator or the tenant is suspended.
/// </summary>
public sealed class TenantSwitch(IDataProtectionProvider dataProtection, PlatformAdmins admins, TenantDirectory tenants, ILogger<TenantSwitch> log)
{
    public const string CookieName = "frogframe.acting_tenant";
    private const string Purpose = "FrogFrame.Tenancy.ActingTenant.v1";
    private IDataProtector Protector => dataProtection.CreateProtector(Purpose);

    /// <summary>The tenant the request should act in, or null (own tenant). The cookie is bound to the user id.</summary>
    public string? Read(HttpContext http, string userId)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var raw) || string.IsNullOrEmpty(raw)) return null;
        try
        {
            var value = Protector.Unprotect(raw);
            var sep = value.IndexOf('|');
            if (sep <= 0 || !string.Equals(value[..sep], userId, StringComparison.Ordinal)) return null;
            var tenant = value[(sep + 1)..];
            return TenantIds.IsValid(tenant) ? tenant : null;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Switches the browser to <paramref name="tenantId"/>; null or the home tenant switches back.</summary>
    public async Task SwitchAsync(HttpContext http, UserContext user, string? tenantId, CancellationToken ct = default)
    {
        if (!admins.IsPlatformAdmin(user)) throw new PermissionDeniedException("Only platform administrators can switch tenants.");
        var home = HomeTenant(user);
        if (string.IsNullOrEmpty(tenantId) || tenantId == home)
        {
            http.Response.Cookies.Delete(CookieName, Options(http));
            log.LogInformation("Platform administrator {User} returned to home tenant {Tenant}", user.Id, home);
            return;
        }
        if (!TenantIds.IsValid(tenantId) || await tenants.GetAsync(tenantId, ct) is not { IsActive: true })
            throw new PermissionDeniedException($"Tenant '{tenantId}' does not exist or is suspended.");
        http.Response.Cookies.Append(CookieName, Protector.Protect($"{user.Id}|{tenantId}"), Options(http));
        log.LogWarning("Platform administrator {User} (home tenant {Home}) switched to tenant {Tenant}", user.Id, home, tenantId);
    }

    /// <summary>The user's own tenant, also while acting in another one.</summary>
    public static string HomeTenant(UserContext user) => user.Attr("home_tenant") is { Length: > 0 } h ? h : user.TenantId;

    /// <summary>True while a platform administrator works in a tenant that is not their own.</summary>
    public static bool IsActing(UserContext? user) => user?.Attr("home_tenant") is { Length: > 0 } h && h != user.TenantId;

    public bool IsPlatformAdmin(UserContext? user) => admins.IsPlatformAdmin(user);

    // Session cookie (no Expires): closing the browser ends the switch.
    private static CookieOptions Options(HttpContext http) => new()
    {
        HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true,
    };

    /// <summary>
    /// The principal for <paramref name="tenantId"/>: tenant claim replaced, roles = System Manager (expanded through
    /// role groups), home-tenant attributes removed, home tenant kept in <see cref="FrogFrameClaims.HomeTenant"/>.
    /// The security stamp and every other claim stay, so revalidation keeps working.
    /// </summary>
    public static ClaimsPrincipal ActAs(ClaimsPrincipal principal, string tenantId)
    {
        var source = (ClaimsIdentity)principal.Identity!;
        var home = source.FindFirst(FrogFrameClaims.Tenant)?.Value ?? "";
        var identity = new ClaimsIdentity(source.AuthenticationType, source.NameClaimType, source.RoleClaimType);
        foreach (var c in source.Claims)
        {
            if (c.Type is FrogFrameClaims.Tenant or FrogFrameClaims.Employee or FrogFrameClaims.Department or FrogFrameClaims.HomeTenant) continue;
            if (c.Type == source.RoleClaimType || c.Type == ClaimTypes.Role) continue;
            identity.AddClaim(new Claim(c.Type, c.Value, c.ValueType, c.Issuer));
        }
        identity.AddClaim(new Claim(FrogFrameClaims.Tenant, tenantId));
        identity.AddClaim(new Claim(FrogFrameClaims.HomeTenant, home));
        foreach (var r in RoleGroups.Expand([UserContext.SystemManager])) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>Applies <see cref="TenantSwitch"/> to signed-in browser requests (cookie scheme only).</summary>
public sealed class ActingTenantClaimsTransformation(IHttpContextAccessor http, TenantSwitch switcher, PlatformAdmins admins, TenantDirectory tenants)
    : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) return principal;
        // Browser sign-in cookie only: bearer tokens and API keys always act in their own tenant.
        if (identity.AuthenticationType != IdentityConstants.ApplicationScheme) return principal;
        if (identity.HasClaim(c => c.Type == FrogFrameClaims.HomeTenant)) return principal; // already transformed
        if (http.HttpContext is not { } ctx || !ctx.Request.Cookies.ContainsKey(TenantSwitch.CookieName)) return principal;
        if (ClaimsUserMapper.ToUserContext(principal) is not { } user || !admins.IsPlatformAdmin(user)) return principal;
        if (switcher.Read(ctx, user.Id) is not { } tenant || tenant == user.TenantId) return principal;
        if (!await tenants.IsActiveAsync(tenant, ctx.RequestAborted)) return principal;
        return TenantSwitch.ActAs(principal, tenant);
    }
}
