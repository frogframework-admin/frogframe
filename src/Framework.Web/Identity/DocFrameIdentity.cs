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

/// <summary>Nama klaim DocFrame. Role memakai ClaimTypes.Role standar.</summary>
public static class DocFrameClaims
{
    public const string Tenant = "docframe:tenant";
    public const string FullName = "docframe:full_name";
    public const string Department = "docframe:department";
    public const string Employee = "docframe:employee";
    public const string Avatar = "docframe:avatar";
    public const string Email = "docframe:email";

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
        yield return new Claim(DocFrameClaims.Tenant, a.TenantId);
        yield return new Claim(DocFrameClaims.FullName, a.FullName);
        foreach (var (attr, value) in a.Attributes())
            if (DocFrameClaims.AttributeClaims.TryGetValue(attr, out var type)) yield return new Claim(type, value);
        foreach (var r in a.Roles.Distinct()) yield return new Claim(ClaimTypes.Role, r);
    }

    /// <summary>Null bila tidak terautentikasi atau klaim wajib (id, tenant) tidak ada — gagal tertutup.</summary>
    public static UserContext? ToUserContext(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenant = principal.FindFirstValue(DocFrameClaims.Tenant);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(tenant)) return null;
        var attributes = DocFrameClaims.AttributeClaims
            .Select(kv => (kv.Key, Value: principal.FindFirstValue(kv.Value)))
            .Where(x => !string.IsNullOrEmpty(x.Value))
            .ToDictionary(x => x.Key, x => x.Value!);
        return new UserContext(
            id,
            principal.FindFirstValue(DocFrameClaims.FullName) ?? principal.Identity!.Name ?? id,
            tenant,
            principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(),
            attributes);
    }

    public static UserContext FromAccount(UserAccount a)
    {
        var identity = new ClaimsIdentity(ToClaims(a).Append(new Claim(ClaimTypes.NameIdentifier, a.Id)), "docframe");
        return ToUserContext(new ClaimsPrincipal(identity))!;
    }
}

public sealed class DocFrameClaimsFactory(IOptions<IdentityOptions> options) : IUserClaimsPrincipalFactory<UserAccount>
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
        return Task.FromResult(new ClaimsPrincipal(identity));
    }
}

/// <summary>Adapter ASP.NET Core Identity → IUserAccountRepository (in-memory atau PostgreSQL).</summary>
public sealed class DocFrameUserStore(IUserAccountRepository repo, TimeProvider clock) :
    IUserPasswordStore<UserAccount>, IUserSecurityStampStore<UserAccount>, IUserLockoutStore<UserAccount>, IUserEmailStore<UserAccount>
{
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
    public const string AuthScheme = "DocFrame";

    /// <summary>
    /// ASP.NET Core Identity dengan penyimpanan akun DocFrame (tanpa EF Core): cookie untuk browser,
    /// bearer token untuk API. Repository akun default in-memory; AddDocFrameworkPostgreSql menggantinya.
    /// </summary>
    public static IServiceCollection AddDocFrameworkIdentity(this IServiceCollection services, Action<IdentityOptions>? configure = null)
    {
        services.TryAddSingleton<IUserAccountRepository, InMemoryUserAccountRepository>();
        services.AddScoped<IUserStore<UserAccount>, DocFrameUserStore>();
        services.AddScoped<IUserClaimsPrincipalFactory<UserAccount>, DocFrameClaimsFactory>();
        services.AddScoped<IUserConfirmation<UserAccount>, ActiveUserConfirmation>();
        services.AddSingleton<IUserDirectory, AccountUserDirectory>();

        services.AddDataProtection()
            .SetApplicationName("DocFrame");

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
                o.AccessDeniedPath = "/account/login";
                o.Cookie.Name = "docframe.auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
            }));

        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5));
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        return services;
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
