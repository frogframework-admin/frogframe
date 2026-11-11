using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Framework.Web;

/// <summary>Options for application public URL. Configured via <c>App:PublicBaseUrl</c>.</summary>
public sealed class PublicUrlOptions
{
    public const string ConfigKey = "App:PublicBaseUrl";

    /// <summary>Base URL viewed by end users, e.g. <c>https://erp.example.com</c> or <c>https://example.com/erp</c>.</summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>
/// Source of base URL for absolute links sent outside the application (e.g., password reset email links).
/// This URL is intentionally NOT derived from the Host / X-Forwarded-Host header, as those headers are
/// controlled by the client: an attacker could request a reset with a spoofed Host so the token link
/// in the victim's email points to an attacker-controlled domain (password reset poisoning).
/// </summary>
public sealed class PublicUrlResolver
{
    private readonly string? _configured;
    private readonly bool _allowRequestFallback;

    /// <param name="configuredBaseUrl">Value of <c>App:PublicBaseUrl</c>; empty if unset.</param>
    /// <param name="allowRequestFallback">
    /// True only in Development environments: falls back to scheme + host from HTTP request when unset.
    /// </param>
    public PublicUrlResolver(string? configuredBaseUrl, bool allowRequestFallback)
    {
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            if (!TryNormalize(configuredBaseUrl, out var normalized))
                throw new InvalidOperationException(
                    $"{PublicUrlOptions.ConfigKey} must be an absolute http/https URL without query, fragment, or user info, e.g. https://erp.example.com");
            _configured = normalized;
        }
        _allowRequestFallback = allowRequestFallback;
    }

    /// <summary>Base URL without trailing slash.</summary>
    public string GetBaseUrl(HttpRequest request)
    {
        if (_configured is not null) return _configured;
        if (_allowRequestFallback) return $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
        throw new InvalidOperationException(
            $"{PublicUrlOptions.ConfigKey} is not configured. This setting is required outside Development environments (used for password reset links).");
    }

    /// <summary>Validates and normalizes base URL: absolute, http/https, without query/fragment/user info, no trailing slash.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return false;
        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}

/// <summary>Constructs password reset links and deterministic idempotency keys for email queues.</summary>
public static class PasswordResetLink
{
    public static string Build(string baseUrl, string userId, string token) =>
        $"{baseUrl}/reset-password?userId={Uri.EscapeDataString(userId)}&token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// Stable idempotency key for the same token. Avoids string.GetHashCode(),
    /// which in .NET Core/.NET 5+ is randomized per process and unstable across restarts/instances.
    /// </summary>
    public static string IdempotencyKey(string userId, string token) =>
        $"pwd-reset-{userId}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32]}";
}
