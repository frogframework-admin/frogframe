using System.Text;
using Framework.Files;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Web;
using Framework.Web.Services;
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

    [Test]
    public async Task StartupGate_shows_waiting_messages_for_database_and_seaweedfs()
    {
        var startup = new StartupReadiness();
        startup.BeginDatabaseStartup();
        Assert.True(!startup.IsDatabaseReady, "database should initially be waiting");

        var services = new ServiceCollection();
        services.AddSingleton(startup);
        services.AddSingleton<IDatabaseHealthCheck>(new InMemoryDocumentStore());
        services.AddSingleton<IObjectStorageHealthCheck>(new StubObjectStorageHealthCheck(false, "connection refused"));
        services.AddSingleton<SystemConnectivityService>();
        var provider = services.BuildServiceProvider();

        var connectivity = provider.GetRequiredService<SystemConnectivityService>();
        Assert.Equal(DependencyState.StartingUp, connectivity.Current.DatabaseState, "DB starts in StartingUp");
        Assert.Equal(DependencyState.StartingUp, connectivity.Current.ObjectStorageState, "SeaweedFS starts in StartingUp");
        Assert.True(connectivity.Current.ObjectStorageMessage?.Contains("cannot upload or download documents", StringComparison.OrdinalIgnoreCase) == true,
            "SeaweedFS message explains user can continue without upload/download");

        var nextCalled = false;
        var middleware = new FrogFrameStartupGateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Browser request during DB startup returns 503 HTML waiting page
        var htmlCtx = new DefaultHttpContext { RequestServices = provider };
        htmlCtx.Request.Method = HttpMethods.Get;
        htmlCtx.Request.Path = "/";
        htmlCtx.Request.Headers.Accept = "text/html";
        htmlCtx.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(htmlCtx);
        Assert.True(!nextCalled, "normal route should be gated while DB is starting");
        Assert.Equal(503, htmlCtx.Response.StatusCode, "503 status during DB startup");
        htmlCtx.Response.Body.Position = 0;
        var html = Encoding.UTF8.GetString(((MemoryStream)htmlCtx.Response.Body).ToArray());
        Assert.True(html.Contains("Waiting for the database to be ready", StringComparison.Ordinal), "HTML page states waiting for database");
        Assert.True(html.Contains("Waiting for SeaweedFS", StringComparison.Ordinal), "HTML page notes SeaweedFS status");

        // API request during DB startup returns 503 problem+json
        var apiCtx = new DefaultHttpContext { RequestServices = provider };
        apiCtx.Request.Method = HttpMethods.Get;
        apiCtx.Request.Path = "/api/data/Employee";
        apiCtx.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(apiCtx);
        Assert.Equal(503, apiCtx.Response.StatusCode, "API returns 503 during DB startup");
        apiCtx.Response.Body.Position = 0;
        var json = Encoding.UTF8.GetString(((MemoryStream)apiCtx.Response.Body).ToArray());
        Assert.True(json.Contains("Waiting for the database to be ready", StringComparison.Ordinal), "API returns English waiting message");

        // Health endpoint passes through even while DB is starting
        var healthCtx = new DefaultHttpContext { RequestServices = provider };
        healthCtx.Request.Method = HttpMethods.Get;
        healthCtx.Request.Path = "/health/ready";
        await middleware.InvokeAsync(healthCtx);
        Assert.True(nextCalled, "/health/ready bypasses startup gate");

        // Once DB is ready, app continues even if SeaweedFS is still not ready
        nextCalled = false;
        startup.ReportDatabaseReady();
        Assert.True(await startup.WaitForDatabaseReadyAsync(), "WaitForDatabaseReadyAsync completes with true");
        var snap = await connectivity.CheckNowAsync();
        Assert.True(snap.IsDatabaseHealthy, "database is now healthy");
        Assert.True(!snap.IsObjectStorageHealthy, "SeaweedFS is still not ready");
        Assert.Equal(DependencyState.StartingUp, snap.ObjectStorageState, "SeaweedFS remains in StartingUp");
        Assert.True(snap.ObjectStorageMessage?.Contains("You can continue, but you cannot upload or download documents", StringComparison.Ordinal) == true,
            "SeaweedFS message lets user continue without upload/download");

        var readyCtx = new DefaultHttpContext { RequestServices = provider };
        readyCtx.Request.Method = HttpMethods.Get;
        readyCtx.Request.Path = "/account/login";
        await middleware.InvokeAsync(readyCtx);
        Assert.True(nextCalled, "app routes proceed once DB is ready even if SeaweedFS is not ready");
    }

    private sealed class StubObjectStorageHealthCheck(bool healthy, string? err = null) : IObjectStorageHealthCheck
    {
        public string StorageName => "s3://frogframe";
        public string? Endpoint => "http://seaweedfs:8333";
        public Task<(bool IsHealthy, string? ErrorMessage)> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult((healthy, healthy ? null : err));
    }
}
