using System.Security.Claims;
using System.Text;
using Framework.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Framework.Web.Identity;

/// <summary>Configuration section "Security:TwoFactor". See docs/guides/two-factor-and-sso.md.</summary>
public sealed class TwoFactorOptions
{
    public const string Section = "Security:TwoFactor";

    /// <summary>Name shown in the authenticator app next to the account (otpauth issuer).</summary>
    public string Issuer { get; set; } = "FrogFrame";

    /// <summary>
    /// Users with any of these roles must enrol before they can use the application (empty = optional for everyone).
    /// An entry may hold several roles separated by commas, e.g. "System Manager, Accounts Manager" from one environment variable.
    /// </summary>
    public string[] RequiredForRoles { get; set; } = [];

    /// <summary><see cref="RequiredForRoles"/> split and trimmed, blanks removed.</summary>
    public IReadOnlyList<string> EffectiveRequiredRoles => RequiredForRoles
        .SelectMany(r => (r ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// When true (default), signing in with single sign-on satisfies <see cref="RequiredForRoles"/>: multi-factor
    /// authentication is then the identity provider's job (Google / Entra conditional access).
    /// </summary>
    public bool TrustSingleSignOn { get; set; } = true;

    /// <summary>Number of recovery codes generated at enrolment and on regeneration.</summary>
    public int RecoveryCodeCount { get; set; } = 10;
}

/// <summary>Helpers for authenticator-app enrolment.</summary>
public static class TwoFactorSetup
{
    /// <summary>The claim added to the session cookie when the account has two-factor authentication enabled.</summary>
    public const string EnabledClaim = "frogframe:2fa";

    /// <summary>Authentication method ("amr") recorded for single sign-on sessions, e.g. "sso:google".</summary>
    public const string SsoMethodPrefix = "sso:";

    /// <summary>The key in groups of four ("abcd efgh …") so it can be typed into an app by hand.</summary>
    public static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>otpauth:// URI understood by Google Authenticator, Microsoft Authenticator, 1Password, Authy, etc.</summary>
    public static string AuthenticatorUri(string issuer, string account, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

    /// <summary>
    /// Verifies a code from the authenticator app (spaces and dashes ignored). Used at enrolment, before two-factor
    /// authentication is switched on, so a wrong scan never locks the user out.
    /// </summary>
    public static Task<bool> VerifyAuthenticatorCodeAsync(UserManager<UserAccount> users, UserAccount user, string code) =>
        users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));

    public static string Normalize(string? code) => new((code ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A 6-digit value is an authenticator code; anything else is treated as a recovery code.</summary>
    public static bool LooksLikeAuthenticatorCode(string? code)
    {
        var n = Normalize(code);
        return n.Length == 6 && n.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Turns two-factor authentication off and forgets the authenticator secret and recovery codes (used by the user
    /// and by an administrator for a lost phone). The security stamp changes, so other sessions sign out.
    /// </summary>
    public static async Task<IdentityResult> DisableAsync(UserManager<UserAccount> users, UserAccount user)
    {
        user.TwoFactorEnabled = false;
        user.AuthenticatorKey = null;
        user.RecoveryCodes.Clear();
        return await users.UpdateSecurityStampAsync(user);
    }

    /// <summary>
    /// Re-checks the current password before a sensitive change (new recovery codes, turning 2FA off). Wrong passwords
    /// count towards the normal lockout (5 attempts, 15 minutes), so a borrowed session cannot be used to guess it.
    /// Returns null when the password is right, otherwise the message to show.
    /// </summary>
    public static async Task<string?> ConfirmPasswordAsync(SignInManager<UserAccount> signIn, UserAccount user, string? password)
    {
        var result = await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true);
        return result.Succeeded ? null
             : result.IsLockedOut ? "Too many failed attempts. The account is locked for 15 minutes."
             : result.IsNotAllowed ? "This account is inactive."
             : "Current password is incorrect.";
    }

    /// <summary>True when the signed-in principal satisfies the "two-factor required" rule for its roles.</summary>
    public static bool Satisfies(ClaimsPrincipal user, TwoFactorOptions options)
    {
        var roles = options.EffectiveRequiredRoles;
        if (roles.Count == 0 || !roles.Any(user.IsInRole)) return true;
        return HasSecondFactor(user, options.TrustSingleSignOn);
    }

    /// <summary>
    /// True when this session has a second factor: an authenticator app is enrolled, or (when trusted) the user signed
    /// in with single sign-on. The home page uses it to suggest enrolment to password-only accounts.
    /// </summary>
    public static bool HasSecondFactor(ClaimsPrincipal user, bool trustSingleSignOn)
    {
        if (user.HasClaim(EnabledClaim, "1")) return true;
        return trustSingleSignOn && user.FindAll("amr")
            .Any(c => c.Value.StartsWith(SsoMethodPrefix, StringComparison.Ordinal));
    }
}

/// <summary>
/// Enforces <see cref="TwoFactorOptions.RequiredForRoles"/>: a signed-in user who must use two-factor authentication
/// but has not enrolled is sent to the enrolment page (browser) or gets 403 (API). Account pages, static files and
/// health checks stay reachable. API keys (service accounts) are not affected.
/// </summary>
public sealed class TwoFactorRequirementMiddleware(RequestDelegate next, IOptionsMonitor<TwoFactorOptions> options)
{
    public const string EnrolPath = "/account/two-factor";

    public Task InvokeAsync(HttpContext context)
    {
        var o = options.CurrentValue;
        var user = context.User;
        if (o.EffectiveRequiredRoles.Count == 0 || user.Identity?.IsAuthenticated != true
            || user.HasClaim(c => c.Type == ApiKeyDefaults.KeyIdClaim) || TwoFactorSetup.Satisfies(user, o))
            return next(context);

        var path = context.Request.Path;
        if (path.StartsWithSegments("/account") || path.StartsWithSegments("/api/auth") || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content") || path.StartsWithSegments("/error")
            || Path.HasExtension(path.Value))
            return next(context);

        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/_blazor"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            return context.Response.WriteAsync("{\"title\":\"Two-factor authentication required\",\"status\":403,\"detail\":\"Your role requires two-factor authentication. Enable it at /account/two-factor, then sign in again.\"}");
        }
        context.Response.Redirect(EnrolPath + "?required=1");
        return Task.CompletedTask;
    }
}

public static class TwoFactorServiceCollectionExtensions
{
    /// <summary>Binds "Security:TwoFactor" (issuer, roles that must enrol). Called by AddFrogFrameworkIdentity.</summary>
    public static IServiceCollection AddFrogFrameworkTwoFactor(this IServiceCollection services)
    {
        // Bound when IConfiguration is registered (always in the app; optional in tests and tools).
        services.AddSingleton<IConfigureOptions<TwoFactorOptions>>(sp => new ConfigureNamedOptions<TwoFactorOptions>(
            Microsoft.Extensions.Options.Options.DefaultName, o => sp.GetService<IConfiguration>()?.GetSection(TwoFactorOptions.Section).Bind(o)));
        // Only the authenticator app (and recovery codes) count as a second factor. The default e-mail and SMS providers
        // would otherwise be offered as 2FA methods (and the SMS one needs a phone-number store FrogFrame does not have).
        services.Configure<IdentityOptions>(o =>
        {
            o.Tokens.ProviderMap.Remove(TokenOptions.DefaultEmailProvider);
            o.Tokens.ProviderMap.Remove(TokenOptions.DefaultPhoneProvider);
        });
        return services;
    }

    /// <summary>Place after UseAuthentication/UseAuthorization.</summary>
    public static IApplicationBuilder UseFrogFrameTwoFactorRequirement(this IApplicationBuilder app) =>
        app.UseMiddleware<TwoFactorRequirementMiddleware>();
}
