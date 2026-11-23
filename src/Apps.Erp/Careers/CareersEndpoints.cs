using System.Text.Json;
using System.Threading.RateLimiting;
using Framework.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Careers;

/// <summary>
/// Public careers portal API, version 1: <c>/api/careers/v1/{tenant}</c>. No sign-in; the tenant code is in the path and
/// the tenant must turn the portal on in HR Settings. Documentation: docs/guides/careers-portal-api.md.
/// Browsers may call it only from the tenant's careers portal address (CORS) or <c>Careers:AllowedOrigins</c>.
/// Limited per client IP: <c>Careers:ReadPerMinute</c> (default 120) and <c>Careers:SubmitPerMinute</c> (default 10).
/// </summary>
public static class CareersEndpoints
{
    public const string Prefix = "/api/careers/v1";
    public const string RateLimitPolicy = "erp-careers";
    public const long MaxApplyBytes = 11 * 1024 * 1024;
    private static readonly string[] CaptchaFields = ["captchaToken", "cf-turnstile-response", "h-captcha-response", "g-recaptcha-response"];

    public static IEndpointRouteBuilder MapCareersApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup(Prefix + "/{tenant}").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        g.AddEndpointFilter(CorsFilter);

        // CORS preflight; the filter adds the headers when the origin is allowed.
        g.MapMethods("/{**path}", [HttpMethods.Options], () => Results.NoContent());

        g.MapGet("/jobs", (HttpContext http, string tenant) => Run(http, async (svc, ct) =>
        {
            var jobs = await svc.ListJobsAsync(tenant, ct);
            http.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok(new { jobs, captchaRequired = http.RequestServices.GetRequiredService<ICareersHumanCheck>().Enabled });
        }));

        g.MapGet("/jobs/{id}", (HttpContext http, string tenant, string id) => Run(http, async (svc, ct) =>
        {
            var d = await svc.GetJobAsync(tenant, id, ct);
            http.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok(new
            {
                d.Job.Id, d.Job.Title, d.Job.Company, d.Job.EmploymentType, d.Job.Location, d.Job.PostedOn, d.Job.ClosesOn,
                descriptionHtml = d.DescriptionHtml, privacyNotice = d.PrivacyNotice, privacyNoticeVersion = d.PrivacyNoticeVersion,
                captchaRequired = http.RequestServices.GetRequiredService<ICareersHumanCheck>().Enabled,
            });
        }));

        g.MapPost("/jobs/{id}/apply", (HttpContext http, string tenant, string id) => Run(http, async (svc, ct) =>
        {
            if (http.Request.ContentLength is > MaxApplyBytes)
                throw new CareersException(413, "Too large", "The CV may be at most 10 MB");
            if (!http.Request.HasFormContentType)
                throw new CareersException(400, "Invalid request", "Send multipart/form-data with the CV in the 'resume' field");
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("resume");
            await using var stream = file?.OpenReadStream();
            await svc.ApplyAsync(tenant, id, new CareersApplyInput
            {
                FullName = form["fullName"], Email = form["email"], Mobile = form["mobile"],
                Consent = form["consent"].ToString().Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes",
                PrivacyNoticeVersion = form["privacyNoticeVersion"],
                CaptchaToken = CaptchaFields.Select(f => form[f].ToString()).FirstOrDefault(v => v.Length > 0),
                Honeypot = form["website"], RemoteIp = http.Connection.RemoteIpAddress?.ToString(),
                ResumeFileName = file?.FileName, Resume = stream,
            }, ct);
            return Results.Json(new { message = "Check your e-mail and confirm your application with the link we sent." }, statusCode: StatusCodes.Status202Accepted);
        })).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxApplyBytes));

        g.MapPost("/applications/confirm", (HttpContext http, string tenant, CareersTokenBody body) => Run(http, async (svc, ct) =>
        {
            var c = await svc.ConfirmAsync(tenant, body.Token, ct);
            return Results.Json(new { application = c.Application, jobTitle = c.JobTitle, alreadyApplied = c.AlreadyApplied, statusToken = c.StatusToken },
                statusCode: c.AlreadyApplied ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }));

        g.MapPost("/applications/status-link", (HttpContext http, string tenant, CareersStatusLinkBody body) => Run(http, async (svc, ct) =>
        {
            await svc.RequestStatusLinkAsync(tenant, body.Email, body.CaptchaToken, http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Json(new { message = "If you have applied with this address, we have sent you a link to your applications." },
                statusCode: StatusCodes.Status202Accepted);
        }));

        g.MapPost("/applications/lookup", (HttpContext http, string tenant, CareersTokenBody body) => Run(http, async (svc, ct) =>
            Results.Ok(new { applications = await svc.ApplicationsAsync(tenant, body.Token, ct) })));

        g.MapPost("/applications/{application}/withdraw", (HttpContext http, string tenant, string application, CareersTokenBody body) => Run(http, async (svc, ct) =>
            Results.Ok(new { application = await svc.WithdrawAsync(tenant, body.Token, application, ct) })));

        return app;
    }

    /// <summary>Per-IP fixed windows: reads and form posts have separate budgets.</summary>
    public static RateLimitPartition<string> Partition(HttpContext http)
    {
        var config = http.RequestServices.GetService<IConfiguration>();
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var submit = HttpMethods.IsPost(http.Request.Method);
        var limit = submit ? config?.GetValue("Careers:SubmitPerMinute", 10) ?? 10 : config?.GetValue("Careers:ReadPerMinute", 120) ?? 120;
        return RateLimitPartition.GetFixedWindowLimiter((submit ? "careers-submit:" : "careers-read:") + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        });
    }

    private static async ValueTask<object?> CorsFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && http.Request.RouteValues["tenant"] is string tenant && await AllowedOriginAsync(http, tenant, origin))
        {
            var h = http.Response.Headers;
            h.AccessControlAllowOrigin = origin;
            h.AccessControlAllowMethods = "GET, POST, OPTIONS";
            h.AccessControlAllowHeaders = "Content-Type";
            h.AccessControlMaxAge = "600";
            h.Vary = "Origin";
        }
        return await next(ctx);
    }

    private static async Task<bool> AllowedOriginAsync(HttpContext http, string tenant, string origin)
    {
        var extra = http.RequestServices.GetService<IConfiguration>()?.GetSection("Careers:AllowedOrigins").Get<string[]>() ?? [];
        if (extra.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            var s = await http.RequestServices.GetRequiredService<CareersService>().SettingsAsync(tenant, http.RequestAborted);
            return string.Equals(CareersService.PortalOrigin(s), origin, StringComparison.OrdinalIgnoreCase);
        }
        catch (CareersException) { return false; }
    }

    private static async Task<IResult> Run(HttpContext http, Func<CareersService, CancellationToken, Task<IResult>> action)
    {
        try { return await action(http.RequestServices.GetRequiredService<CareersService>(), http.RequestAborted); }
        catch (CareersException ex) { return DocApi.ProblemResult(ex.Status, ex.Title, ex.Message, ex.Errors.Count > 0 ? ex.Errors : null); }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidDataException)
        {
            return DocApi.ProblemResult(400, "Invalid request", "The request could not be read");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never show internal details to the public; the log has them.
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Apps.Erp.Careers")
                .LogError(ex, "Careers API {Method} {Path} failed", http.Request.Method, http.Request.Path);
            return DocApi.ProblemResult(500, "Something went wrong", "Please try again later");
        }
    }
}

public sealed record CareersTokenBody(string? Token);

public sealed record CareersStatusLinkBody(string? Email, string? CaptchaToken);
