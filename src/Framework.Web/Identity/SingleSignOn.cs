using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Framework.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Identity;

/// <summary>A configured single sign-on provider, shown as a button on the login page.</summary>
public sealed record SsoProvider(string Scheme, string DisplayName);

/// <summary>The single sign-on providers enabled by configuration (empty when SSO is not configured).</summary>
public sealed class SsoProviders(IReadOnlyList<SsoProvider> all)
{
    public IReadOnlyList<SsoProvider> All { get; } = all;
    public IReadOnlyList<string> AllowedEmailDomains { get; init; } = [];
    public SsoProvider? Find(string scheme) => All.FirstOrDefault(p => string.Equals(p.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Single sign-on with Google and Microsoft Entra ID (OpenID Connect authorization-code flow with PKCE, user details
/// from the provider's OIDC userinfo endpoint over TLS). SSO only signs in <b>existing</b> FrogFrame accounts, matched
/// by e-mail address; it never creates accounts or changes roles. See docs/guides/two-factor-and-sso.md.
/// </summary>
public static class SingleSignOn
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string Section = "Authentication";
    private const string ProviderItem = "frogframe.sso";
    private const string ReturnUrlItem = "frogframe.returnUrl";
    private static readonly string[] MultiTenantAuthorities = ["common", "organizations", "consumers"];

    /// <summary>
    /// Enables the providers whose ClientId and ClientSecret are set:
    /// Authentication:Google:{ClientId,ClientSecret} and Authentication:Microsoft:{ClientId,ClientSecret,TenantId}.
    /// Authentication:AllowedEmailDomains (optional) restricts which e-mail domains may sign in with SSO.
    /// </summary>
    public static IServiceCollection AddFrogFrameworkSso(this IServiceCollection services, IConfiguration config)
    {
        var providers = new List<SsoProvider>();
        var auth = services.AddAuthentication();

        var google = config.GetSection($"{Section}:Google");
        if (!string.IsNullOrWhiteSpace(google["ClientId"]) && !string.IsNullOrWhiteSpace(google["ClientSecret"]))
        {
            auth.AddOAuth(Google, "Google", o =>
            {
                Configure(o, google, "/signin-google");
                o.AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
                o.TokenEndpoint = "https://oauth2.googleapis.com/token";
                o.UserInformationEndpoint = "https://openidconnect.googleapis.com/v1/userinfo";
            });
            providers.Add(new SsoProvider(Google, "Google"));
        }

        var entra = config.GetSection($"{Section}:Microsoft");
        if (!string.IsNullOrWhiteSpace(entra["ClientId"]) && !string.IsNullOrWhiteSpace(entra["ClientSecret"]))
        {
            var tenant = entra["TenantId"]?.Trim();
            // A single directory only: with "common"/"organizations" anyone with any Microsoft account could present
            // an e-mail address that matches a FrogFrame account.
            if (string.IsNullOrEmpty(tenant) || MultiTenantAuthorities.Contains(tenant, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Authentication:Microsoft:TenantId must be your Entra directory (tenant) ID or primary domain; 'common', 'organizations' and 'consumers' are not allowed.");
            auth.AddOAuth(Microsoft, "Microsoft", o =>
            {
                Configure(o, entra, "/signin-microsoft");
                o.AuthorizationEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/authorize";
                o.TokenEndpoint = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token";
                o.UserInformationEndpoint = "https://graph.microsoft.com/oidc/userinfo";
            });
            providers.Add(new SsoProvider(Microsoft, "Microsoft"));
        }

        var domains = config.GetSection($"{Section}:AllowedEmailDomains").Get<string[]>() ?? [];
        services.RemoveAll<SsoProviders>();
        services.AddSingleton(new SsoProviders(providers)
        {
            AllowedEmailDomains = domains
                .SelectMany(d => (d ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(d => d.TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToList(),
        });
        return services;
    }

    private static void Configure(OAuthOptions o, IConfigurationSection section, string callbackPath)
    {
        o.ClientId = section["ClientId"]!;
        o.ClientSecret = section["ClientSecret"]!;
        o.CallbackPath = section["CallbackPath"] ?? callbackPath;
        o.SignInScheme = IdentityConstants.ExternalScheme;
        o.UsePkce = true;
        o.SaveTokens = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "sub");
        o.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");
        o.ClaimActions.MapJsonKey(ClaimTypes.Name, "name");
        o.ClaimActions.MapJsonKey("email_verified", "email_verified");
        o.Events.OnCreatingTicket = async ctx =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
            ctx.RunClaimActions(json.RootElement);
        };
        // Cancelled consent or a provider error: back to the login page with a message instead of an exception page.
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect("/account/login?sso=failed");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    }

    /// <summary>GET /account/external/{provider} (starts sign-in) and GET /account/external/callback.</summary>
    public static IEndpointRouteBuilder MapFrogFrameworkSso(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/external/{provider}", (string provider, string? returnUrl, SsoProviders providers) =>
        {
            if (providers.Find(provider) is not { } p) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = "/account/external/callback" };
            props.Items[ProviderItem] = p.Scheme;
            props.Items[ReturnUrlItem] = SafeReturnUrl(returnUrl);
            return Results.Challenge(props, [p.Scheme]);
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/account/external/callback", async (HttpContext http, SignInManager<UserAccount> signIn, SsoProviders providers,
            TimeProvider clock, ILoggerFactory loggers) =>
        {
            var log = loggers.CreateLogger("Framework.Web.Identity.SingleSignOn");
            var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (!external.Succeeded || external.Properties?.Items.TryGetValue(ProviderItem, out var scheme) != true || providers.Find(scheme!) is not { } provider)
                return Results.Redirect("/account/login?sso=failed");

            var outcome = await ResolveAccountAsync(signIn.UserManager, providers, provider.Scheme, external.Principal!);
            if (outcome.Account is not { } account)
            {
                log.LogWarning("SSO sign-in via {Provider} refused: {Reason}", provider.Scheme, outcome.Error);
                return Results.Redirect("/account/login?sso=" + outcome.Error);
            }
            if (!await signIn.CanSignInAsync(account) || await signIn.UserManager.IsLockedOutAsync(account))
                return Results.Redirect("/account/login?sso=inactive");

            // "amr" (not ClaimTypes.AuthenticationMethod) survives security-stamp revalidation; see PreserveAuthenticationMethod.
            await signIn.SignInWithClaimsAsync(account, isPersistent: false, [new Claim("amr", TwoFactorSetup.SsoMethodPrefix + provider.Scheme)]);
            account.LastLoginAt = clock.GetUtcNow();
            await signIn.UserManager.UpdateAsync(account);
            log.LogInformation("User {User} signed in via {Provider}", account.Id, provider.Scheme);
            var returnUrl = external.Properties!.Items.TryGetValue(ReturnUrlItem, out var r) ? r : "/";
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    /// <summary>Result of matching a provider identity to an account. Error is a short code shown on the login page.</summary>
    public sealed record Resolution(UserAccount? Account, string? Error);

    /// <summary>
    /// Matches the provider identity to an existing account by e-mail. Refuses when the provider does not vouch for the
    /// address (Google: email_verified must be true), the domain is not allowed, or no account has that e-mail.
    /// </summary>
    public static async Task<Resolution> ResolveAccountAsync(UserManager<UserAccount> users, SsoProviders providers, string scheme, ClaimsPrincipal external)
    {
        var email = external.FindFirstValue(ClaimTypes.Email)?.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) return new(null, "noemail");
        if (scheme == Google && !string.Equals(external.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
            return new(null, "unverified");
        var domain = email[(email.LastIndexOf('@') + 1)..].ToLowerInvariant();
        if (providers.AllowedEmailDomains.Count > 0 && !providers.AllowedEmailDomains.Contains(domain)) return new(null, "domain");
        var account = await users.FindByEmailAsync(email);
        return account is null ? new(null, "noaccount") : new(account, null);
    }

    /// <summary>Message for the ?sso= code on the login page.</summary>
    public static string? Describe(string? code) => code switch
    {
        null or "" => null,
        "noaccount" => "No FrogFrame account uses this e-mail address. Ask your administrator to add it to your account.",
        "unverified" => "Your e-mail address is not verified by the sign-in provider.",
        "noemail" => "The sign-in provider did not share an e-mail address.",
        "domain" => "Single sign-on is not allowed for this e-mail domain.",
        "inactive" => "This account is inactive or temporarily locked. Please contact the administrator.",
        _ => "Single sign-on failed or was cancelled. Please try again.",
    };

    private static string SafeReturnUrl(string? u) =>
        u is { Length: > 0 } && u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\") && !u.StartsWith("/account/") ? u : "/";
}
