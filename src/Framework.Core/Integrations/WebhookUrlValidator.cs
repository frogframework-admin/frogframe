using System.Net;
using System.Net.Sockets;

namespace Framework.Integrations;

/// <summary>
/// Validates destination webhook URLs against SSRF (Server-Side Request Forgery) attacks.
/// Rejects loopback (127.0.0.0/8, ::1), private subnets (RFC 1918), link-local/cloud metadata (169.254.0.0/16),
/// and IPv6 equivalents unless explicitly exempted in configuration.
/// </summary>
public static class WebhookUrlValidator
{
    private static readonly IPNetwork[] DisallowedNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fc00::/7")
    ];

    public static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        foreach (var net in DisallowedNetworks)
        {
            try
            {
                if (net.Contains(ip)) return true;
            }
            catch
            {
                // Ignore address family mismatch during check
            }
        }
        return false;
    }

    public static void ValidateUrl(string rawUrl, bool allowHttp = false, IEnumerable<string>? allowedPrivateHosts = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) throw new InvalidOperationException("Webhook URL is required.");
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid webhook URL: '{rawUrl}'. Must be an absolute URL.");

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException($"Webhook URL scheme '{uri.Scheme}' is not supported. Use https (or http if allowed).");

        if (uri.Scheme == Uri.UriSchemeHttp && !allowHttp)
            throw new InvalidOperationException("Insecure HTTP webhook URLs are disallowed. Please use HTTPS.");

        var allowed = new HashSet<string>(allowedPrivateHosts ?? [], StringComparer.OrdinalIgnoreCase);
        if (allowed.Contains(uri.DnsSafeHost)) return;

        // If host is an IP literal
        if (IPAddress.TryParse(uri.DnsSafeHost, out var directIp))
        {
            if (IsPrivateOrLoopback(directIp))
                throw new InvalidOperationException($"Webhook URL '{rawUrl}' points to a disallowed private, loopback, or metadata address.");
            return;
        }

        // Hostname DNS resolution
        try
        {
            var addresses = Dns.GetHostAddresses(uri.DnsSafeHost);
            if (addresses.Length == 0)
                throw new InvalidOperationException($"Could not resolve host '{uri.DnsSafeHost}'.");

            foreach (var ip in addresses)
            {
                if (IsPrivateOrLoopback(ip))
                    throw new InvalidOperationException($"Webhook URL '{rawUrl}' resolves to a disallowed private or loopback IP address ({ip}).");
            }
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"DNS resolution failed for host '{uri.DnsSafeHost}': {ex.Message}", ex);
        }
    }
}

