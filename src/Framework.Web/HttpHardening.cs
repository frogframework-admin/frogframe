using Framework.Observability;
using Framework.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framework.Web;

/// <summary>Parsed AllowedHosts value, used for startup validation.</summary>
public sealed class AllowedHostsSettings
{
    public string[] Hosts { get; set; } = [];
}

public static class HttpHardeningExtensions
{
    /// <summary>
    /// Configures production HTTP security hardening services: ForwardedHeaders, HSTS, and security options.
    /// </summary>
    public static IServiceCollection AddFrogFrameHttpHardening(this IServiceCollection services, IConfiguration? config = null)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            ConfigureTrustedProxies(options, config);
        });

        // Fail on startup if App:PublicBaseUrl is empty or invalid outside Development (used for password reset links).
        services.AddOptions<PublicUrlOptions>()
            .Configure<IConfiguration>((o, cfg) => o.PublicBaseUrl = cfg[PublicUrlOptions.ConfigKey])
            .Validate<IHostEnvironment>(
                (o, env) => string.IsNullOrWhiteSpace(o.PublicBaseUrl) ? env.IsDevelopment() : PublicUrlResolver.TryNormalize(o.PublicBaseUrl, out _),
                $"{PublicUrlOptions.ConfigKey} must be configured with an absolute http/https URL (e.g. https://erp.example.com) outside Development environments. " +
                "This value is used for password reset links and must not be derived from the Host header.")
            .ValidateOnStart();

        // Fail on startup if AllowedHosts is "*" (or empty) outside Development. The same list is used by HostFiltering
        // for the Host header and for X-Forwarded-Host (see ConfigureTrustedProxies).
        services.AddOptions<AllowedHostsSettings>()
            .Configure<IConfiguration>((o, cfg) => o.Hosts = ParseAllowedHosts(cfg["AllowedHosts"]))
            .Validate<IHostEnvironment>(
                (o, env) => env.IsDevelopment() || (o.Hosts.Length > 0 && !o.Hosts.Contains("*")),
                "AllowedHosts must list the application's host names (separated by ';', e.g. \"erp.example.com;localhost\") " +
                "outside Development environments. The value \"*\" accepts any Host header.")
            .ValidateOnStart();

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
            options.Preload = true;
        });

        return services;
    }

    /// <summary>
    /// Configures trusted proxies allowed to send X-Forwarded-For/Proto/Host headers. Headers from other sources are ignored,
    /// preventing clients from spoofing IP addresses (API key IP allowlists, per-IP rate limits) or Host headers.
    /// ASP.NET Core default: loopback only. Add reverse proxy IPs/CIDRs via configuration:
    /// <code>
    /// "ForwardedHeaders": {
    ///   "KnownProxies":  [ "10.0.0.5" ],
    ///   "KnownNetworks": [ "172.18.0.0/16" ],
    ///   "ForwardLimit": 1,
    ///   "TrustAllProxies": false
    /// }
    /// </code>
    /// <c>TrustAllProxies = true</c> is only intended for deployments where the application port CANNOT be reached directly except through the proxy.
    /// </summary>
    public static void ConfigureTrustedProxies(ForwardedHeadersOptions options, IConfiguration? config)
    {
        if (config is null) return;

        // Only accept X-Forwarded-Host values that match AllowedHosts (unless AllowedHosts is "*").
        var hosts = ParseAllowedHosts(config["AllowedHosts"]);
        if (!hosts.Contains("*"))
            foreach (var host in hosts) options.AllowedHosts.Add(host);

        var section = config.GetSection("ForwardedHeaders");

        if (section.GetValue<bool>("TrustAllProxies"))
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }

        // Blank entries are skipped (e.g. an environment variable intentionally left empty in docker-compose).
        foreach (var raw in section.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!System.Net.IPAddress.TryParse(raw.Trim(), out var ip))
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains an invalid IP address: '{raw}'");
            options.KnownProxies.Add(ip);
        }

        foreach (var raw in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!System.Net.IPNetwork.TryParse(raw.Trim(), out var network))
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains an invalid CIDR notation: '{raw}'");
            options.KnownIPNetworks.Add(network);
        }

        if (section.GetValue<int?>("ForwardLimit") is { } limit) options.ForwardLimit = limit;
    }

    /// <summary>Splits an AllowedHosts value ("a.com;b.com") into a list of host names.</summary>
    public static string[] ParseAllowedHosts(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Adds global security headers: X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy, and CSP.
    /// </summary>
    public static IApplicationBuilder UseFrogFrameSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<FrogFrameSecurityHeadersMiddleware>();

    /// <summary>
    /// Standardized production exception handler: RFC 7807 problem+json for /api/*, user-friendly HTML for browser requests.
    /// </summary>
    public static IApplicationBuilder UseFrogFrameExceptionHandler(this IApplicationBuilder app)
    {
        return app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var feature = context.Features.Get<IExceptionHandlerPathFeature>();
                var ex = feature?.Error;
                var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("FrogFrame.Http");
                logger?.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

                FrogFrameTelemetry.HttpErrors.Add(1);

                if (ex is not null)
                {
                    try
                    {
                        var errorStore = context.RequestServices.GetService<IErrorLogStore>();
                        if (errorStore is not null)
                        {
                            var user = context.RequestServices.GetService<CurrentUser>()?.User;
                            var tenantId = user?.TenantId ?? context.Request.Headers["X-Tenant-ID"].FirstOrDefault() ?? "unknown";
                            var userId = user?.Id ?? context.User.Identity?.Name;

                            await errorStore.LogAsync(new ErrorLogEntry
                            {
                                TenantId = tenantId,
                                Timestamp = DateTimeOffset.UtcNow,
                                Method = context.Request.Method,
                                Path = context.Request.Path.Value ?? "",
                                UserId = userId,
                                ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
                                Message = ex.Message,
                                StackTrace = ex.StackTrace,
                                Seen = false
                            });
                        }
                    }
                    catch
                    {
                        // Ensure exception logging failure never masks original error
                    }
                }

                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/problem+json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        type = "https://httpstatuses.io/500",
                        title = "Internal Server Error",
                        status = 500,
                        detail = "An unexpected error occurred on the server. Please contact the administrator.",
                        instance = context.Request.Path.Value
                    });
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(
                        "<!DOCTYPE html><html lang='en'><head><title>Internal Server Error</title><meta charset='utf-8'/>" +
                        "<meta name='viewport' content='width=device-width, initial-scale=1.0'/>" +
                        "<style>body{font-family:system-ui,-apple-system,sans-serif;background:#fafafa;color:#222;display:flex;" +
                        "align-items:center;justify-content:center;height:100vh;margin:0;padding:1.5rem;box-sizing:border-box}" +
                        ".card{background:#fff;border:1px solid #e5e7eb;border-radius:12px;padding:2.5rem;max-width:480px;text-align:center;box-shadow:0 4px 6px -1px rgba(0,0,0,0.05)}" +
                        "h1{font-size:1.5rem;margin:0 0 0.75rem;color:#111}p{color:#6b7280;line-height:1.6;margin:0 0 1.5rem}" +
                        "a{display:inline-block;background:#111;color:#fff;text-decoration:none;padding:0.6rem 1.25rem;border-radius:8px;font-weight:500;font-size:0.875rem}</style></head>" +
                        "<body><div class='card'><h1>Server Error</h1>" +
                        "<p>Your request could not be processed at this time. Please reload the page or contact the administrator if the issue persists.</p>" +
                        "<a href='/'>Return to Home</a></div></body></html>");
                }
            });
        });
    }

    /// <summary>
    /// Maps sanitized, production-ready health check endpoints without leaking internal database error messages.
    /// /health/live: Liveness check
    /// /health/ready: Readiness check validating database connectivity
    /// /health: Backward-compatible alias for readiness
    /// </summary>
    public static IEndpointRouteBuilder MapFrogFrameHealthChecks(this IEndpointRouteBuilder app)
    {
        // Liveness probe: process is running
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

        // Readiness probe: checks dependencies without leaking internal connection details
        async Task<IResult> CheckReadinessAsync(IServiceProvider sp, CancellationToken ct)
        {
            var checker = sp.GetService<IDatabaseHealthCheck>();
            if (checker is null)
                return Results.Ok(new { status = "ready", storage = "in-memory" });

            try
            {
                var ok = await checker.CheckHealthAsync(ct);
                if (ok)
                    return Results.Ok(new { status = "ready", storage = checker.ProviderName });

                return Results.Json(new { status = "unready", storage = checker.ProviderName }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("FrogFrame.Health");
                logger?.LogWarning(ex, "Readiness check failed for provider {Provider}", checker.ProviderName);
                return Results.Json(new { status = "unready", storage = checker.ProviderName }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        app.MapGet("/health/ready", (IServiceProvider sp, CancellationToken ct) => CheckReadinessAsync(sp, ct));

        // Backward compatibility
        app.MapGet("/health", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var r = await CheckReadinessAsync(sp, ct);
            return r;
        });

        return app;
    }
}

public sealed class FrogFrameSecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Prevent MIME sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // Prevent clickjacking
        headers["X-Frame-Options"] = "SAMEORIGIN";

        // Strict referrer
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Feature policies
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        // Content Security Policy for FrogFrame (allows Blazor Server WebSocket + Google fonts)
        if (!headers.ContainsKey("Content-Security-Policy"))
        {
            headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "font-src 'self' https://fonts.gstatic.com data:; " +
                "img-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; " +
                "frame-ancestors 'self';";
        }

        await next(context);
    }
}
