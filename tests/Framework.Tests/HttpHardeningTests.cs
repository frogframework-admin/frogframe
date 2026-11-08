using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public async Task HttpHardening_SecurityHeadersMiddleware_menambahkan_semua_header_keamanan()
    {
        var context = new DefaultHttpContext();
        var middleware = new FrogFrameSecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString(), "X-Content-Type-Options");
        Assert.Equal("SAMEORIGIN", context.Response.Headers["X-Frame-Options"].ToString(), "X-Frame-Options");
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"].ToString(), "Referrer-Policy");
        Assert.Equal("camera=(), microphone=(), geolocation=()", context.Response.Headers["Permissions-Policy"].ToString(), "Permissions-Policy");
        Assert.True(context.Response.Headers.ContainsKey("Content-Security-Policy"), "CSP header harus ada");
        Assert.True(context.Response.Headers["Content-Security-Policy"].ToString().Contains("default-src 'self'"), "CSP default-src");
        Assert.True(context.Response.Headers["Content-Security-Policy"].ToString().Contains("frame-ancestors 'self'"), "CSP frame-ancestors");
    }

    [Test]
    public async Task HttpHardening_InMemoryDocumentStore_mengimplementasikan_health_check()
    {
        var store = new InMemoryDocumentStore();
        Assert.Equal("in-memory", store.ProviderName, "ProviderName in-memory");
        var healthy = await store.CheckHealthAsync();
        Assert.True(healthy, "InMemory store selalu sehat");
    }

    [Test, PgTest]
    public async Task Pg_PostgresDocumentStore_mengimplementasikan_health_check()
    {
        var store = sp.GetRequiredService<PostgresDocumentStore>();
        Assert.Equal("postgresql", store.ProviderName, "ProviderName postgresql");
        var healthy = await store.CheckHealthAsync();
        Assert.True(healthy, "PostgresDocumentStore harus sehat ketika terkoneksi");
    }
}
