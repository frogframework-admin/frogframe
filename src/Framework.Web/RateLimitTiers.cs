using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Framework.Web;

/// <summary>Request classes with their own per-minute budget. See docs/guides/rate-limiting.md.</summary>
public enum RateLimitTier
{
    /// <summary>Not limited (pages, static files, Blazor).</summary>
    None,
    /// <summary>Sign-in: API login/refresh, browser login and 2FA form posts, SSO start. Per client IP.</summary>
    Auth,
    /// <summary>Forgot/reset password (sends e-mail). Per client IP, strict.</summary>
    PasswordReset,
    /// <summary>Reports, report templates, saved queries, printing/PDF. Per user or API key.</summary>
    Heavy,
    /// <summary>POST/PUT/PATCH/DELETE on the API. Per user or API key.</summary>
    Write,
    /// <summary>GET/HEAD on the API. Per user or API key.</summary>
    Read,
}

/// <summary>The tier, partition key and per-minute limit of one request.</summary>
public readonly record struct RateLimitDecision(RateLimitTier Tier, string Key, int PermitPerMinute);

/// <summary>
/// Tiered rate limits. Sign-in and password-reset requests are limited per client IP for the whole application
/// (including the browser login form, which is not under /api); API calls are limited per user or API key with
/// separate budgets for reads, writes and heavy queries, so a script that writes a lot cannot use up the budget for
/// reading, and an expensive report cannot be called as often as a lookup.
/// </summary>
public static class RateLimitTiers
{
    // Settings (per minute) and their defaults. Api:RateLimitPerMinute and Api:AuthRateLimitPerMinute keep their meaning.
    public const string ReadSetting = "Api:RateLimitPerMinute";
    public const string WriteSetting = "Api:WriteRateLimitPerMinute";
    public const string HeavySetting = "Api:HeavyRateLimitPerMinute";
    public const string AuthSetting = "Api:AuthRateLimitPerMinute";
    public const string PasswordResetSetting = "Api:PasswordResetRateLimitPerMinute";
    public const int DefaultRead = 300, DefaultWrite = 120, DefaultHeavy = 60, DefaultAuth = 1200, DefaultPasswordReset = 20;

    private static readonly string[] AuthApiPaths = ["/api/auth/login", "/api/auth/refresh"];
    private static readonly string[] PasswordResetApiPaths = ["/api/auth/forgot-password", "/api/auth/reset-password"];
    private static readonly string[] AuthFormPaths = ["/account/login", "/account/login-2fa"];
    private static readonly string[] PasswordResetFormPaths = ["/account/forgot-password", "/account/reset-password"];
    private static readonly string[] HeavyApiPrefixes = ["/api/reports", "/api/report-templates", "/api/query", "/api/print"];

    /// <summary>Classifies a request. <see cref="RateLimitTier.None"/> means no limit applies.</summary>
    public static RateLimitDecision Classify(HttpContext http, IConfiguration? config)
    {
        var path = http.Request.Path;
        var method = http.Request.Method;
        var nodes = Math.Max(1, config?.GetValue("Cluster:Nodes", 1) ?? 1);
        RateLimitDecision Make(RateLimitTier tier, string who, string setting, int fallback) =>
            // The counter lives in each node's memory. With N nodes behind a load balancer that spreads calls evenly,
            // Cluster:Nodes = N keeps the total close to the configured limit.
            new(tier, tier.ToString().ToLowerInvariant() + ":" + who, Math.Max(1, (config?.GetValue(setting, fallback) ?? fallback) / nodes));

        var ip = "ip:" + http.Connection.RemoteIpAddress;
        var isPost = HttpMethods.IsPost(method);

        if (PasswordResetApiPaths.Any(p => path.StartsWithSegments(p)) || (isPost && PasswordResetFormPaths.Any(p => path.StartsWithSegments(p))))
            return Make(RateLimitTier.PasswordReset, ip, PasswordResetSetting, DefaultPasswordReset);
        // Sign-in is keyed by IP: many employees behind one office NAT log in at shift start, so the default is high;
        // password guessing on one account is stopped by the account lockout (5 failures / 15 minutes) instead.
        if (AuthApiPaths.Any(p => path.StartsWithSegments(p)) || (isPost && AuthFormPaths.Any(p => path.StartsWithSegments(p)))
            || path.StartsWithSegments("/account/external"))
            return Make(RateLimitTier.Auth, ip, AuthSetting, DefaultAuth);

        if (!path.StartsWithSegments("/api")) return new(RateLimitTier.None, "", 0);

        var who = http.User.FindFirst(Identity.ApiKeyDefaults.KeyIdClaim)?.Value is { } k ? "k:" + k
                : http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } u ? "u:" + u
                : ip;
        if (HeavyApiPrefixes.Any(p => path.StartsWithSegments(p)))
            return Make(RateLimitTier.Heavy, who, HeavySetting, DefaultHeavy);
        return HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            ? Make(RateLimitTier.Read, who, ReadSetting, DefaultRead)
            : Make(RateLimitTier.Write, who, WriteSetting, DefaultWrite);
    }

    /// <summary>Fixed one-minute window for a decision, or no limiter for <see cref="RateLimitTier.None"/>.</summary>
    public static RateLimitPartition<string> Partition(RateLimitDecision d) => d.Tier == RateLimitTier.None
        ? RateLimitPartition.GetNoLimiter("none")
        : RateLimitPartition.GetFixedWindowLimiter(d.Key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = d.PermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

    /// <summary>Applied to every request (UseRateLimiter): the per-IP sign-in and password-reset tiers.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            var d = Classify(http, http.RequestServices?.GetService(typeof(IConfiguration)) as IConfiguration);
            return d.Tier is RateLimitTier.Auth or RateLimitTier.PasswordReset ? Partition(d) : RateLimitPartition.GetNoLimiter("none");
        });

    /// <summary>The "frogframe-api" endpoint policy: read, write and heavy tiers (sign-in is already limited globally).</summary>
    public static RateLimitPartition<string> ApiPolicy(HttpContext http)
    {
        var d = Classify(http, http.RequestServices?.GetService(typeof(IConfiguration)) as IConfiguration);
        return d.Tier is RateLimitTier.Read or RateLimitTier.Write or RateLimitTier.Heavy ? Partition(d) : RateLimitPartition.GetNoLimiter("none");
    }
}
