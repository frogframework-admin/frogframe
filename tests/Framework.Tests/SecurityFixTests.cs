using System.Net;
using Framework.Security;
using Framework.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Regression tests for security fixes: password reset links, forwarded headers, and API key revocation.</summary>
sealed partial class Tests
{
    private static HttpRequest RequestWithHost(string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString(host);
        return ctx.Request;
    }

    [Test]
    public Task PublicUrl_link_reset_memakai_konfigurasi_bukan_header_Host()
    {
        var resolver = new PublicUrlResolver("https://erp.contoh.co.id/", allowRequestFallback: false);
        Assert.Equal("https://erp.contoh.co.id", resolver.GetBaseUrl(RequestWithHost("attacker.example")), "Spoofed Host is ignored");

        var withPath = new PublicUrlResolver("https://contoh.co.id/erp/", allowRequestFallback: true);
        Assert.Equal("https://contoh.co.id/erp", withPath.GetBaseUrl(RequestWithHost("attacker.example")), "Configured URL wins even when fallback is allowed");

        var link = PasswordResetLink.Build("https://erp.contoh.co.id", "dewi", "a+b/c=");
        Assert.Equal("https://erp.contoh.co.id/reset-password?userId=dewi&token=a%2Bb%2Fc%3D", link, "Token is escaped");
        return Task.CompletedTask;
    }

    [Test]
    public Task PublicUrl_tanpa_konfigurasi_hanya_boleh_di_Development()
    {
        var dev = new PublicUrlResolver(null, allowRequestFallback: true);
        Assert.Equal("https://localhost:5001", dev.GetBaseUrl(RequestWithHost("localhost:5001")), "Development uses request host");

        var prod = new PublicUrlResolver("", allowRequestFallback: false);
        var threw = false;
        try { prod.GetBaseUrl(RequestWithHost("attacker.example")); } catch (InvalidOperationException) { threw = true; }
        Assert.True(threw, "Outside Development without App:PublicBaseUrl must fail instead of using Host header");

        Assert.True(!PublicUrlResolver.TryNormalize("ftp://contoh.co.id", out _), "Non-http/https schemes rejected");
        Assert.True(!PublicUrlResolver.TryNormalize("https://contoh.co.id/?x=1", out _), "Query string rejected");
        Assert.True(!PublicUrlResolver.TryNormalize("/relative", out _), "Relative URLs rejected");
        Assert.True(!PublicUrlResolver.TryNormalize("https://user:pass@contoh.co.id", out _), "User info rejected");
        var invalidThrew = false;
        try { _ = new PublicUrlResolver("not-a-url", allowRequestFallback: true); } catch (InvalidOperationException) { invalidThrew = true; }
        Assert.True(invalidThrew, "Invalid configuration values fail immediately");
        return Task.CompletedTask;
    }

    [Test]
    public Task PasswordReset_kunci_idempotensi_stabil_per_token()
    {
        var a1 = PasswordResetLink.IdempotencyKey("dewi", "token-one");
        var a2 = PasswordResetLink.IdempotencyKey("dewi", "token-one");
        var b = PasswordResetLink.IdempotencyKey("dewi", "token-two");
        Assert.Equal(a1, a2, "Same token produces identical idempotency key");
        Assert.True(a1 != b, "Different tokens produce different keys");
        Assert.True(!a1.Contains("token-one"), "Raw token is not leaked in idempotency key");
        return Task.CompletedTask;
    }

    private static ForwardedHeadersOptions ForwardedFrom(Dictionary<string, string?> values)
    {
        var options = new ForwardedHeadersOptions();
        HttpHardeningExtensions.ConfigureTrustedProxies(options, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return options;
    }

    [Test]
    public Task ForwardedHeaders_default_hanya_loopback_dan_proxy_dari_konfigurasi()
    {
        var defaults = ForwardedFrom(new());
        Assert.True(defaults.KnownIPNetworks.Count + defaults.KnownProxies.Count > 0, "ASP.NET Core defaults (loopback) not cleared");
        Assert.True(!defaults.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("203.0.113.9"))), "Arbitrary public IPs not trusted");

        var configured = ForwardedFrom(new()
        {
            ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
            ["ForwardedHeaders:KnownNetworks:0"] = "172.18.0.0/16",
            ["ForwardedHeaders:ForwardLimit"] = "2",
        });
        Assert.True(configured.KnownProxies.Contains(IPAddress.Parse("10.0.0.5")), "Configured proxy is trusted");
        Assert.True(configured.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("172.18.3.4"))), "Configured proxy network is trusted");
        Assert.Equal<int?>(2, configured.ForwardLimit, "ForwardLimit");

        var all = ForwardedFrom(new() { ["ForwardedHeaders:TrustAllProxies"] = "true" });
        Assert.True(all.KnownIPNetworks.Count == 0 && all.KnownProxies.Count == 0, "TrustAllProxies only active when explicitly requested");

        var blank = ForwardedFrom(new() { ["ForwardedHeaders:KnownNetworks:0"] = "", ["ForwardedHeaders:KnownProxies:0"] = " " });
        Assert.True(!blank.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("203.0.113.9"))), "Blank entries are skipped without error");

        var threw = false;
        try { ForwardedFrom(new() { ["ForwardedHeaders:KnownNetworks:0"] = "invalid-cidr" }); } catch (InvalidOperationException) { threw = true; }
        Assert.True(threw, "Invalid CIDR rejected on startup");
        return Task.CompletedTask;
    }

    [Test]
    public Task AllowedHosts_juga_membatasi_X_Forwarded_Host()
    {
        Assert.True(HttpHardeningExtensions.ParseAllowedHosts(" erp.example.com ; localhost;; ").SequenceEqual(["erp.example.com", "localhost"]), "Parse host list");
        Assert.True(HttpHardeningExtensions.ParseAllowedHosts(null).Length == 0, "Empty value yields no hosts");

        var restricted = ForwardedFrom(new() { ["AllowedHosts"] = "erp.example.com;localhost" });
        Assert.True(restricted.AllowedHosts.Contains("erp.example.com") && restricted.AllowedHosts.Contains("localhost"), "X-Forwarded-Host restricted to AllowedHosts");

        var wildcard = ForwardedFrom(new() { ["AllowedHosts"] = "*" });
        Assert.True(wildcard.AllowedHosts.Count == 0, "AllowedHosts \"*\" adds no restriction (Development only)");
        return Task.CompletedTask;
    }

    [Test]
    public async Task ApiKey_pencabutan_tidak_tertimpa_salinan_lama_atau_catatan_pemakaian()
    {
        var admin = U("admin");
        var store = sp.GetRequiredService<IApiKeyStore>();
        var (key, token) = await ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("Race", "dewi", [], [], null));

        // Stale copy read prior to revocation (simulates an in-flight VerifyAsync).
        var stale = (await store.GetAsync(key.Id))!;
        await ApiKeys.RevokeAsync(admin, key.Id);

        stale.LastUsedAt = DateTimeOffset.UtcNow;
        await store.SaveAsync(stale);
        Assert.True((await store.GetAsync(key.Id))!.RevokedAt is not null, "SaveAsync with stale copy does not undo revocation");

        await store.RecordUsageAsync(key.Id, DateTimeOffset.UtcNow, "10.0.0.1");
        var after = (await store.GetAsync(key.Id))!;
        Assert.True(after.RevokedAt is not null && after.LastUsedIp == "10.0.0.1", "RecordUsageAsync only updates usage columns");
        Assert.True(await ApiKeys.VerifyAsync(token, IPAddress.Loopback) is null, "Key remains rejected");
    }
}
