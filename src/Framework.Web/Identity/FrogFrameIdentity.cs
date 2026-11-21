using System.Security.Claims;
using Framework.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framework.Web.Identity;

/// <summary>Nama klaim FrogFrame. Role memakai ClaimTypes.Role standar.</summary>
public static class FrogFrameClaims
{
    public const string Tenant = "frogframe:tenant";
    public const string FullName = "frogframe:full_name";
    public const string Department = "frogframe:department";
    public const string Employee = "frogframe:employee";
    public const string Avatar = "frogframe:avatar";
    public const string Email = "frogframe:email";

    /// <summary>Atribut UserContext ↔ klaim. Tambah baris di sini untuk atribut baru (mis. cost center).</summary>
    public static readonly IReadOnlyDictionary<string, string> AttributeClaims = new Dictionary<string, string>
    {
        ["department"] = Department,
        ["employee"] = Employee,
        ["avatar"] = Avatar,
        ["email"] = Email,
    };
}

/// <summary>
/// Satu-satunya tempat pemetaan akun → klaim → UserContext. Request (cookie/bearer) dan proses latar belakang
/// (notifikasi) memakai jalur yang sama sehingga hasilnya identik.
/// </summary>
public static class ClaimsUserMapper
{
    public static IEnumerable<Claim> ToClaims(UserAccount a)
    {
        yield return new Claim(FrogFrameClaims.Tenant, a.TenantId);
        yield return new Claim(FrogFrameClaims.FullName, a.FullName);
        foreach (var (attr, value) in a.Attributes())
            if (FrogFrameClaims.AttributeClaims.TryGetValue(attr, out var type)) yield return new Claim(type, value);
        // Effective roles: role groups expanded both ways (see RoleGroups), so every check sees the same set.
        foreach (var r in RoleGroups.Expand(a.Roles)) yield return new Claim(ClaimTypes.Role, r);
    }

    /// <summary>Null bila tidak terautentikasi atau klaim wajib (id, tenant) tidak ada — gagal tertutup.</summary>
    public static UserContext? ToUserContext(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenant = principal.FindFirstValue(FrogFrameClaims.Tenant);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(tenant)) return null;
        var attributes = FrogFrameClaims.AttributeClaims
            .Select(kv => (kv.Key, Value: principal.FindFirstValue(kv.Value)))
            .Where(x => !string.IsNullOrEmpty(x.Value))
            .ToDictionary(x => x.Key, x => x.Value!);
        return new UserContext(
            id,
            principal.FindFirstValue(FrogFrameClaims.FullName) ?? principal.Identity!.Name ?? id,
            tenant,
            principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(),
            attributes);
    }

    public static UserContext FromAccount(UserAccount a)
    {
        var identity = new ClaimsIdentity(ToClaims(a).Append(new Claim(ClaimTypes.NameIdentifier, a.Id)), "frogframe");
        return ToUserContext(new ClaimsPrincipal(identity))!;
    }
}

public sealed class FrogFrameClaimsFactory(IOptions<IdentityOptions> options) : IUserClaimsPrincipalFactory<UserAccount>
{
    public Task<ClaimsPrincipal> CreateAsync(UserAccount user)
    {
        var o = options.Value.ClaimsIdentity;
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme, o.UserNameClaimType, o.RoleClaimType);
        identity.AddClaim(new Claim(o.UserIdClaimType, user.Id));
        identity.AddClaim(new Claim(o.UserNameClaimType, user.UserName));
        // Security stamp di cookie: dicek ulang berkala; berubah saat password/role/status diubah → sesi lama tidak berlaku.
        identity.AddClaim(new Claim(o.SecurityStampClaimType, user.SecurityStamp));
        identity.AddClaims(ClaimsUserMapper.ToClaims(user));
        // Enrolment state for Security:TwoFactor:RequiredForRoles. Changing it changes the security stamp, so the claim stays current.
        if (user.TwoFactorEnabled) identity.AddClaim(new Claim(TwoFactorSetup.EnabledClaim, "1"));
        return Task.FromResult(new ClaimsPrincipal(identity));
    }
}

/// <summary>Adapter ASP.NET Core Identity → IUserAccountRepository (in-memory atau PostgreSQL).</summary>
public sealed class FrogFrameUserStore(IUserAccountRepository repo, TimeProvider clock, IDataProtectionProvider dataProtection) :
    IUserPasswordStore<UserAccount>, IUserSecurityStampStore<UserAccount>, IUserLockoutStore<UserAccount>, IUserEmailStore<UserAccount>,
    IUserTwoFactorStore<UserAccount>, IUserAuthenticatorKeyStore<UserAccount>, IUserTwoFactorRecoveryCodeStore<UserAccount>
{
    /// <summary>Purpose string of the Data Protection key that encrypts authenticator secrets at rest.</summary>
    public const string AuthenticatorKeyPurpose = "FrogFrame.Identity.AuthenticatorKey.v1";
    private readonly IDataProtector _keyProtector = dataProtection.CreateProtector(AuthenticatorKeyPurpose);

    public Task<string> GetUserIdAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(UserAccount user, CancellationToken ct) => Task.FromResult<string?>(user.UserName);
    public Task SetUserNameAsync(UserAccount user, string? userName, CancellationToken ct) { user.UserName = userName ?? ""; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(UserAccount user, CancellationToken ct) => Task.FromResult<string?>(user.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(UserAccount user, string? name, CancellationToken ct) { user.NormalizedUserName = name ?? ""; return Task.CompletedTask; }

    public async Task<IdentityResult> CreateAsync(UserAccount user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.Id)) user.Id = user.UserName.Trim().ToLowerInvariant();
        if (user.CreatedAt == default) user.CreatedAt = clock.GetUtcNow();
        try { await repo.CreateAsync(user, ct); return IdentityResult.Success; }
        catch (DuplicateAccountException ex) { return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = ex.Message }); }
    }

    public async Task<IdentityResult> UpdateAsync(UserAccount user, CancellationToken ct)
    {
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        try { await repo.UpdateAsync(user, ct); return IdentityResult.Success; }
        catch (DuplicateAccountException ex) { return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = ex.Message }); }
    }

    public async Task<IdentityResult> DeleteAsync(UserAccount user, CancellationToken ct) { await repo.DeleteAsync(user.Id, ct); return IdentityResult.Success; }
    public Task<UserAccount?> FindByIdAsync(string userId, CancellationToken ct) => repo.FindByIdAsync(userId, ct);
    public Task<UserAccount?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => repo.FindByNormalizedUserNameAsync(normalizedUserName, ct);

    // Password
    public Task SetPasswordHashAsync(UserAccount user, string? passwordHash, CancellationToken ct) { user.PasswordHash = passwordHash; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.PasswordHash is not null);

    // Security stamp
    public Task SetSecurityStampAsync(UserAccount user, string stamp, CancellationToken ct) { user.SecurityStamp = stamp; return Task.CompletedTask; }
    public Task<string?> GetSecurityStampAsync(UserAccount user, CancellationToken ct) => Task.FromResult<string?>(user.SecurityStamp);

    // Lockout
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.LockoutEnd);
    public Task SetLockoutEndDateAsync(UserAccount user, DateTimeOffset? lockoutEnd, CancellationToken ct) { user.LockoutEnd = lockoutEnd; return Task.CompletedTask; }
    public Task<int> IncrementAccessFailedCountAsync(UserAccount user, CancellationToken ct) => Task.FromResult(++user.AccessFailedCount);
    public Task ResetAccessFailedCountAsync(UserAccount user, CancellationToken ct) { user.AccessFailedCount = 0; return Task.CompletedTask; }
    public Task<int> GetAccessFailedCountAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.LockoutEnabled);
    public Task SetLockoutEnabledAsync(UserAccount user, bool enabled, CancellationToken ct) { user.LockoutEnabled = enabled; return Task.CompletedTask; }

    // Email
    public Task SetEmailAsync(UserAccount user, string? email, CancellationToken ct) { user.Email = email; return Task.CompletedTask; }
    public Task<string?> GetEmailAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.Email);
    public Task<bool> GetEmailConfirmedAsync(UserAccount user, CancellationToken ct) => Task.FromResult(true);
    public Task SetEmailConfirmedAsync(UserAccount user, bool confirmed, CancellationToken ct) => Task.CompletedTask;
    public Task<UserAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) => repo.FindByNormalizedEmailAsync(normalizedEmail, ct);
    public Task<string?> GetNormalizedEmailAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);
    public Task SetNormalizedEmailAsync(UserAccount user, string? normalizedEmail, CancellationToken ct) { user.NormalizedEmail = normalizedEmail; return Task.CompletedTask; }

    // Two-factor
    public Task SetTwoFactorEnabledAsync(UserAccount user, bool enabled, CancellationToken ct) { user.TwoFactorEnabled = enabled; return Task.CompletedTask; }
    public Task<bool> GetTwoFactorEnabledAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.TwoFactorEnabled);

    // Authenticator key: encrypted at rest. Identity reads it on every code check, so it is decrypted here only.
    public Task SetAuthenticatorKeyAsync(UserAccount user, string key, CancellationToken ct)
    {
        user.AuthenticatorKey = string.IsNullOrEmpty(key) ? null : _keyProtector.Protect(key);
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(UserAccount user, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.AuthenticatorKey)) return Task.FromResult<string?>(null);
        try { return Task.FromResult<string?>(_keyProtector.Unprotect(user.AuthenticatorKey)); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Data Protection keys lost: the secret cannot be read. Fail closed: for an enrolled user return a random key
            // nobody has, so a second factor is still demanded but no app code matches (recovery codes still work, or an
            // administrator resets two-factor). Returning null would make Identity treat the account as having no 2FA.
            return Task.FromResult<string?>(user.TwoFactorEnabled
                ? Base32Random()
                : null);
        }
    }

    private static string Base32Random()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        return string.Concat(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32).Select(b => alphabet[b & 31]));
    }

    // Recovery codes: stored as SHA-256 hashes; redeeming removes the code.
    public Task ReplaceCodesAsync(UserAccount user, IEnumerable<string> recoveryCodes, CancellationToken ct)
    {
        user.RecoveryCodes = recoveryCodes.Select(HashRecoveryCode).Distinct().ToList();
        return Task.CompletedTask;
    }

    public Task<bool> RedeemCodeAsync(UserAccount user, string code, CancellationToken ct)
    {
        var hash = HashRecoveryCode(code);
        var index = user.RecoveryCodes.FindIndex(h => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(h), Convert.FromHexString(hash)));
        if (index < 0) return Task.FromResult(false);
        user.RecoveryCodes.RemoveAt(index);
        return Task.FromResult(true);
    }

    public Task<int> CountCodesAsync(UserAccount user, CancellationToken ct) => Task.FromResult(user.RecoveryCodes.Count);

    /// <summary>Codes are compared without case, spaces or dashes, so "ab12-cd34" and "AB12CD34" are the same code.</summary>
    public static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    public void Dispose() { }
}

/// <summary>Akun nonaktif tidak bisa login, walau password benar.</summary>
public sealed class ActiveUserConfirmation : IUserConfirmation<UserAccount>
{
    public Task<bool> IsConfirmedAsync(UserManager<UserAccount> manager, UserAccount user) => Task.FromResult(user.IsActive);
}

/// <summary>
/// Sirkuit Blazor Server hidup lama; status login dicek ulang berkala. Bila akun dinonaktifkan, password/role diubah
/// (security stamp berubah), sesi yang sedang terbuka otomatis keluar.
/// </summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider(ILoggerFactory loggers, IServiceScopeFactory scopes, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null || !user.IsActive) return false;
        var stamp = state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return stamp == user.SecurityStamp;
    }
}

/// <summary>Daftar pengguna aktif per tenant (untuk proses tanpa request, mis. penerima notifikasi).</summary>
public sealed class AccountUserDirectory(IUserAccountRepository repo) : IUserDirectory
{
    public async Task<UserContext?> FindAsync(string userId, CancellationToken ct = default) =>
        await repo.FindByIdAsync(userId, ct) is { IsActive: true } a ? ClaimsUserMapper.FromAccount(a) : null;

    public async Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default) =>
        (await repo.ListAsync(tenantId, ct)).Where(a => a.IsActive).Select(ClaimsUserMapper.FromAccount).ToList();
}

public static class IdentityServiceCollectionExtensions
{
    public const string AuthScheme = "FrogFrame";

    /// <summary>
    /// ASP.NET Core Identity dengan penyimpanan akun FrogFrame (tanpa EF Core): cookie untuk browser,
    /// bearer token untuk API. Repository akun default in-memory; AddFrogFrameworkPostgreSql menggantinya.
    /// </summary>
    public static IServiceCollection AddFrogFrameworkIdentity(this IServiceCollection services, Action<IdentityOptions>? configure = null)
    {
        services.TryAddSingleton<IUserAccountRepository, InMemoryUserAccountRepository>();
        services.AddScoped<IUserStore<UserAccount>, FrogFrameUserStore>();
        services.AddScoped<IUserClaimsPrincipalFactory<UserAccount>, FrogFrameClaimsFactory>();
        services.AddScoped<IUserConfirmation<UserAccount>, ActiveUserConfirmation>();
        services.AddSingleton<IUserDirectory, AccountUserDirectory>();

        services.AddDataProtection()
            .SetApplicationName("FrogFrame");

        services.AddIdentityCore<UserAccount>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.SignIn.RequireConfirmedAccount = true; // = akun aktif (ActiveUserConfirmation)
                o.User.RequireUniqueEmail = false;
                configure?.Invoke(o);
            })
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddFrogFrameworkTwoFactor(); // after AddDefaultTokenProviders: limits 2FA to the authenticator app

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = AuthScheme;
                o.DefaultChallengeScheme = AuthScheme;
            })
            // Browser memakai cookie; klien API mengirim "Authorization: Bearer <token>".
            .AddPolicyScheme(AuthScheme, "Cookie, bearer, atau kunci API", o => o.ForwardDefaultSelector = ctx =>
            {
                var h = ctx.Request.Headers.Authorization.ToString();
                return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? IdentityConstants.BearerScheme
                     : h.StartsWith(ApiKeyDefaults.Scheme + " ", StringComparison.OrdinalIgnoreCase) ? ApiKeyDefaults.Scheme
                     : IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null)
            .AddBearerToken(IdentityConstants.BearerScheme, o => o.BearerTokenExpiration = TimeSpan.FromHours(1))
            .AddIdentityCookies(c => c.ApplicationCookie?.Configure(o =>
            {
                o.LoginPath = "/account/login";
                o.LogoutPath = "/account/logout";
                o.AccessDeniedPath = "/error/403";
                o.Cookie.Name = "frogframe.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
            }));

        services.Configure<SecurityStampValidatorOptions>(o =>
        {
            o.ValidationInterval = TimeSpan.FromMinutes(5);
            o.OnRefreshingPrincipal = PreserveAuthenticationMethod;
        });
        services.TryAddSingleton(new SsoProviders([]));
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        return services;
    }

    /// <summary>
    /// The periodic security-stamp check rebuilds the cookie principal from the account; keep how the user signed in
    /// ("amr": "mfa" after a two-factor code, "sso:google" after single sign-on) so that information is not lost.
    /// </summary>
    public static Task PreserveAuthenticationMethod(SecurityStampRefreshingPrincipalContext context)
    {
        if (context.NewPrincipal?.Identity is not ClaimsIdentity target || context.CurrentPrincipal is null) return Task.CompletedTask;
        foreach (var claim in context.CurrentPrincipal.FindAll(c => c.Type is "amr" || c.Type == ClaimTypes.AuthenticationMethod))
            if (!target.HasClaim(claim.Type, claim.Value)) target.AddClaim(new Claim(claim.Type, claim.Value));
        return Task.CompletedTask;
    }
}

public static class BootstrapAdmin
{
    /// <summary>
    /// Membuat satu akun System Manager dari konfigurasi "Bootstrap" bila BELUM ADA akun sama sekali (pilot tanpa data demo).
    /// Setelah login pertama, ganti password dan kosongkan Bootstrap:AdminPassword dari konfigurasi.
    /// </summary>
    public static async Task EnsureAsync(IServiceProvider sp, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var s = config.GetSection("Bootstrap");
        if (string.IsNullOrWhiteSpace(s["AdminUserName"]) || string.IsNullOrWhiteSpace(s["AdminPassword"])) return;
        await using var scope = sp.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<IUserAccountRepository>().AnyAsync()) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var result = await users.CreateAsync(new UserAccount
        {
            UserName = s["AdminUserName"]!, FullName = s["AdminFullName"] ?? "Administrator",
            TenantId = s["TenantId"] ?? "default", Roles = [UserContext.SystemManager],
        }, s["AdminPassword"]!);
        if (!result.Succeeded)
            throw new InvalidOperationException("Failed to create initial administrator: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
