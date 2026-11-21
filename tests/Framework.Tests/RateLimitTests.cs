using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Framework.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tiered rate limits (sign-in / password reset per IP; API read, write and heavy per user or key).</summary>
sealed partial class Tests
{
    private static DefaultHttpContext RateLimitRequest(IServiceProvider sp, string method, string path, string ip = "10.0.0.1", string? user = null, string? apiKey = null)
    {
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Method = method;
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        var claims = new List<Claim>();
        if (user is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, user));
        if (apiKey is not null) claims.Add(new Claim(Framework.Web.Identity.ApiKeyDefaults.KeyIdClaim, apiKey));
        if (claims.Count > 0) http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return http;
    }

    private static ServiceProvider RateLimitServices(params (string Key, string Value)[] settings) =>
        new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build()).BuildServiceProvider();

    [Test]
    public Task Rate_limit_tiers_classify_requests()
    {
        using var sp = RateLimitServices(("Cluster:Nodes", "2"));
        var cfg = sp.GetRequiredService<IConfiguration>();
        (RateLimitTier Tier, string Key, int Limit) C(string method, string path, string? user = null, string? key = null)
        {
            var d = RateLimitTiers.Classify(RateLimitRequest(sp, method, path, user: user, apiKey: key), cfg);
            return (d.Tier, d.Key, d.PermitPerMinute);
        }

        Assert.Equal((RateLimitTier.Auth, "auth:ip:10.0.0.1", 600), C("POST", "/api/auth/login"), "API login per IP, divided by 2 nodes");
        Assert.Equal(RateLimitTier.Auth, C("POST", "/api/auth/refresh").Tier, "refresh");
        Assert.Equal(RateLimitTier.Auth, C("POST", "/account/login").Tier, "browser login form post");
        Assert.Equal(RateLimitTier.Auth, C("POST", "/account/login-2fa").Tier, "2FA form post");
        Assert.Equal(RateLimitTier.Auth, C("GET", "/account/external/google").Tier, "SSO start");
        Assert.Equal(RateLimitTier.None, C("GET", "/account/login").Tier, "showing the login page is not limited");
        Assert.Equal((RateLimitTier.PasswordReset, "passwordreset:ip:10.0.0.1", 10), C("POST", "/api/auth/forgot-password"), "forgot password API");
        Assert.Equal(RateLimitTier.PasswordReset, C("POST", "/account/forgot-password").Tier, "forgot password form");
        Assert.Equal(RateLimitTier.PasswordReset, C("POST", "/account/reset-password").Tier, "reset password form");
        Assert.Equal((RateLimitTier.Read, "read:u:rina", 150), C("GET", "/api/data/Customer", "rina"), "read per user");
        Assert.Equal((RateLimitTier.Write, "write:u:rina", 60), C("POST", "/api/data/Customer", "rina"), "write per user");
        Assert.Equal(RateLimitTier.Write, C("DELETE", "/api/data/Customer/C-1", "rina").Tier, "delete is a write");
        Assert.Equal((RateLimitTier.Heavy, "heavy:k:key-1", 30), C("GET", "/api/reports/sales", "rina", "key-1"), "report per API key (key wins over user)");
        Assert.Equal(RateLimitTier.Heavy, C("GET", "/api/query/top-customers", "rina").Tier, "saved query");
        Assert.Equal(RateLimitTier.Heavy, C("GET", "/api/print/SalesInvoice/SI-1", "rina").Tier, "print/PDF");
        Assert.Equal(RateLimitTier.Heavy, C("POST", "/api/report-templates/t1/render", "rina").Tier, "report template render");
        Assert.Equal("read:ip:10.0.0.1", C("GET", "/api/meta/Customer").Key, "anonymous API calls keyed by IP");
        Assert.Equal(RateLimitTier.None, C("GET", "/app/Customer").Tier, "pages not limited");
        Assert.Equal(RateLimitTier.None, C("POST", "/_blazor/negotiate").Tier, "Blazor not limited");
        Assert.Equal(RateLimitTier.None, C("GET", "/apiary").Tier, "path segments, not prefixes");
        return Task.CompletedTask;
    }

    [Test]
    public Task Rate_limit_tiers_have_separate_budgets()
    {
        using var sp = RateLimitServices((RateLimitTiers.PasswordResetSetting, "3"), (RateLimitTiers.WriteSetting, "2"), (RateLimitTiers.ReadSetting, "4"), (RateLimitTiers.HeavySetting, "1"));
        using var global = RateLimitTiers.CreateGlobalLimiter();
        using var api = PartitionedRateLimiter.Create<HttpContext, string>(RateLimitTiers.ApiPolicy);
        bool Global(string method, string path, string ip = "10.0.0.1") => global.AttemptAcquire(RateLimitRequest(sp, method, path, ip)).IsAcquired;
        bool Api(string method, string path, string user) => api.AttemptAcquire(RateLimitRequest(sp, method, path, user: user)).IsAcquired;

        var resets = Enumerable.Range(0, 4).Select(_ => Global("POST", "/api/auth/forgot-password")).ToList();
        Assert.Equal("True,True,True,False", string.Join(",", resets), "password reset: 3 per minute per IP");
        Assert.True(Global("POST", "/account/forgot-password", "10.0.0.2"), "another IP has its own budget");
        Assert.True(Global("POST", "/api/auth/login"), "sign-in budget is separate from password reset");
        Assert.True(Enumerable.Range(0, 50).All(_ => Global("GET", "/api/data/Customer")), "API reads are not limited by the global limiter");

        Assert.Equal("True,True,False", string.Join(",", Enumerable.Range(0, 3).Select(_ => Api("POST", "/api/data/Customer", "rina"))), "writes: 2 per minute");
        Assert.Equal("True,True,True,True,False", string.Join(",", Enumerable.Range(0, 5).Select(_ => Api("GET", "/api/data/Customer", "rina"))), "reads still have their own 4");
        Assert.Equal("True,False", string.Join(",", Enumerable.Range(0, 2).Select(_ => Api("GET", "/api/reports/sales", "rina"))), "heavy: 1");
        Assert.True(Api("POST", "/api/data/Customer", "budi"), "another user has their own budget");
        Assert.True(Enumerable.Range(0, 20).All(_ => Api("POST", "/api/auth/login", "x")), "sign-in is not counted twice by the API policy");
        return Task.CompletedTask;
    }
}
