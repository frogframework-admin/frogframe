using Apps.Erp;
using Apps.Erp.Components;
using BlazorBlueprint.Components;
using Framework.Localization;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Security;
using Framework.Web;
using Framework.Web.Layout;
using Framework.Web.Identity;
using Framework.Persistence.PostgreSql;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
// Aset statis Razor Class Library (BlazorBlueprint, Framework.Web) juga tersaji saat dijalankan dari source di luar Development.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// UI: BlazorBlueprint (komponen shadcn/ui untuk Blazor) dengan teks bawaan berbahasa Indonesia.
builder.Services.AddBlazorBlueprintComponents(configureLocalizer: BbIndonesian.Apply);

// 1) Framework: metadata dimuat dari folder, dikompilasi & divalidasi saat startup (gagal cepat bila tidak valid).
builder.Services.AddFrogFramework(o => o.LoadFrom(Path.Combine(builder.Environment.ContentRootPath, "Metadata")));
builder.Services.AddFrogFrameOpenTelemetry(builder.Configuration);

// Penyimpanan: "InMemory" (default, data hilang saat restart) atau "PostgreSql" (permanen, siap pilot).
var usePostgres = string.Equals(builder.Configuration["Persistence:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase);
if (usePostgres)
    // FrogFrame = peran app (operasi data). FrogFrameSchema (opsional) = pemilik skema untuk migrasi & indeks;
    // dengan pemisahan ini peran app tidak bisa mengubah/menghapus buku besar (lihat deploy/sql/gl-roles.sql).
    builder.Services.AddFrogFrameworkPostgreSql(builder.Configuration.GetConnectionString("FrogFrame")
        ?? throw new InvalidOperationException("ConnectionStrings:FrogFrame wajib diisi bila Persistence:Provider = PostgreSql"),
        builder.Configuration.GetConnectionString("FrogFrameSchema"));

// Isi file & lampiran: "Database" (default, kolom bytea) atau "S3" (SeaweedFS/MinIO/Ceph/AWS S3, lihat docs/guides/object-storage.md).
// Dipanggil SESUDAH provider persistence karena membungkus file store yang terdaftar.
Framework.Storage.ObjectStorageSetup.AddFrogFrameObjectStorage(builder.Services, builder.Configuration);

// Login: ASP.NET Core Identity (cookie untuk browser, bearer token untuk API). Klaim → UserContext.
builder.Services.AddFrogFrameworkIdentity();

// Dashboard SQL: butuh PostgreSQL + user database read-only (deploy/sql/reporting-readonly-user.sql).
var readOnlyConnection = builder.Configuration.GetConnectionString("FrogFrameReadOnly");
var useReporting = usePostgres && !string.IsNullOrWhiteSpace(readOnlyConnection);
if (useReporting)
    builder.Services.AddFrogFrameworkReporting(readOnlyConnection!, o =>
    {
        o.ReadOnlyRole = builder.Configuration["Dashboard:ReadOnlyRole"] ?? "frogframe_ro";
        o.StatementTimeoutSeconds = builder.Configuration.GetValue("Dashboard:StatementTimeoutSeconds", 5);
        // Instance analitik terpisah (replika logis). Kosong = database aplikasi.
        o.AnalyticsConnectionString = builder.Configuration.GetConnectionString("FrogFrameAnalytics");
    });

// 2) Extension point eksplisit (RFC §2.5): handler, validator, event handler, report — semuanya C# terdaftar via DI.
// ERP: Core, Accounting, Finance, HR, Recruitment, Payroll, CRM, Persediaan, Laporan, Job harian (Apps.Erp)
builder.Services.AddErpHooks();

builder.Services.AddScheduledJob<NotificationRuleDateJob>();
builder.Services.AddScheduledJob<Framework.Integrations.AutoRepeatJob>();
builder.Services.AddScheduledJob<Framework.Scripting.ServerScriptScheduledJob>();
builder.Services.AddScheduledJob<Framework.Communication.EmailAccountPollJob>();
builder.Services.AddScheduledJob<Framework.Assignments.AssignmentReminderJob>();

// ESS mobile: refresh token bearer berlaku lebih lama (default 30 hari) supaya karyawan tidak login ulang setiap hari.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.BearerToken.BearerTokenOptions>(Microsoft.AspNetCore.Identity.IdentityConstants.BearerScheme,
    o => o.RefreshTokenExpiration = TimeSpan.FromDays(Math.Clamp(builder.Configuration.GetValue("Ess:RefreshTokenDays", 30), 1, 180)));

// Hardening HTTP production: ForwardedHeaders & HSTS
builder.Services.AddFrogFrameHttpHardening(builder.Configuration);

var app = builder.Build();

// 1. Reverse proxy forwarded headers (Docker, Traefik, Nginx, ALB)
app.UseForwardedHeaders();

// 2. Exception handling & HSTS
if (!app.Environment.IsDevelopment())
{
    app.UseFrogFrameExceptionHandler();
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// 404 & HTTP status code handling: re-execute to Blazor error pages
app.UseStatusCodePagesWithReExecute("/error/{0}");

// 3. Global security headers (nosniff, SAMEORIGIN, CSP, Referrer, Permissions)
app.UseFrogFrameSecurityHeaders();
// 4. Startup gate: serves a clear English status page while PostgreSQL migrations/initialization are waiting on startup.
app.UseFrogFrameStartupGate();

app.UseAuthentication();
// Bahasa UI per pengguna (Indonesia/Inggris: cookie → preferensi tersimpan → Localization:DefaultLanguage).
// Format angka & tanggal tetap Indonesia; penyimpanan dan API tetap memakai format invarian (yyyy-MM-dd, titik desimal).
app.UseFrogFrameLocalization();
app.UseAuthorization();
app.UseRateLimiter(); // batas permintaan /api per kunci API / pengguna (Api:RateLimitPerMinute)
app.UseAntiforgery();
app.MapStaticAssets();

// Health checks liveness & readiness
app.MapFrogFrameHealthChecks();
app.MapFrogFrameworkApi();
Apps.Erp.Ess.EssEndpoints.MapEssApi(app); // /api/ess/v1 (aplikasi mobile ESS)
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(DeskLayout).Assembly);

async Task InitializeDatabaseAndSeedAsync(CancellationToken ct)
{
    if (usePostgres)
    {
        await app.Services.MigrateFrogFrameworkDatabaseAsync(ct);
        // Indeks dari metadata dibangun di latar belakang (CONCURRENTLY): startup tidak menunggu pembangunan indeks
        // pada tabel besar, dan tabel tetap bisa ditulis selama proses. Diperbarui otomatis setiap metadata dipublikasikan.
        _ = Task.Run(async () =>
        {
            try { await app.Services.SyncDocumentIndexesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { app.Logger.LogError(ex, "Sinkronisasi indeks dokumen gagal"); }
        }, ct);
    }
    // View pelaporan dibuat dari metadata aktif (dan diperbarui otomatis setiap publish).
    if (useReporting)
    {
        await app.Services.SyncReportingSchemaAsync(ct);
        // Peringatan di log bila user read-only belum siap. Aplikasi tetap berjalan; dashboard menampilkan pesannya.
        _ = await app.Services.CheckReportingConnectionAsync(ct);
    }

    // Pastikan menu default dan role default ter-seed untuk tenant bootstrap dan audit tenants
    var auditTenants = app.Configuration.GetSection("Metadata:AuditTenants").Get<string[]>() ?? [];
    var tenantToSeed = app.Configuration["Bootstrap:TenantId"] ?? "acme";
    var allTenants = new HashSet<string>(auditTenants, StringComparer.OrdinalIgnoreCase) { tenantToSeed };
    try
    {
        var navStore = app.Services.GetRequiredService<Framework.Navigation.INavigationMenuStore>();
        var roleStore = app.Services.GetRequiredService<Framework.Security.IRoleStore>();
        foreach (var t in allTenants)
        {
            await navStore.EnsureDefaultMenusAsync(t, ct);
            await roleStore.EnsureDefaultRolesAsync(t, ct);
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        app.Logger.LogWarning(ex, "Gagal seed awal navigasi menu/role");
    }

    // Audit metadata terhadap master role & modul tiap tenant yang dipantau (Metadata:AuditTenants).
    // Hanya melaporkan ke log; tidak mengubah metadata. Mencegah masalah baru ketahuan saat publish ulang.
    {
        var registry = app.Services.GetRequiredService<MetadataRegistry>();
        var catalog = app.Services.GetRequiredService<RoleCatalog>();
        foreach (var tenant in auditTenants)
        {
            foreach (var issue in registry.AuditAgainst(catalog.Scope(tenant)))
                app.Logger.LogWarning("Metadata tidak cocok dengan master (tenant {Tenant}): {Source} › {Path}: {Message}",
                    tenant, issue.Source, issue.Path, issue.Message);
        }
    }

    // Akun awal dari konfigurasi "Bootstrap" (pilot / production).
    await BootstrapAdmin.EnsureAsync(app.Services, app.Configuration);

    // Pastikan sample demo users ter-seed / tersinkronisasi bila Demo:SeedData aktif atau di Development
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Demo:SeedData", false))
    {
        try
        {
            await Framework.Web.Services.DemoUsers.EnsureDemoUsersAsync(app.Services, app.Configuration["Demo:Password"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogWarning(ex, "Gagal seed sample demo users");
        }
    }
}

var startupReadiness = app.Services.GetRequiredService<Framework.Persistence.StartupReadiness>();
var connectivity = app.Services.GetRequiredService<Framework.Web.Services.SystemConnectivityService>();

if (usePostgres)
{
    startupReadiness.BeginDatabaseStartup("Waiting for the database to be ready...");
    connectivity.ReportDatabaseStartingUp();
}

await app.StartAsync();

if (usePostgres)
{
    _ = Task.Run(async () =>
    {
        var ct = app.Lifetime.ApplicationStopping;
        try
        {
            await InitializeDatabaseAndSeedAsync(ct);
            startupReadiness.ReportDatabaseReady();
            connectivity.ReportDatabaseHealthy();
            app.Logger.LogInformation("Database initialization complete; application is ready.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            startupReadiness.ReportDatabaseFailed(ex.Message);
            connectivity.ReportException(ex);
            app.Logger.LogCritical(ex, "Database initialization failed during startup.");
        }
    });
}
else
{
    await InitializeDatabaseAndSeedAsync(app.Lifetime.ApplicationStopping);
    startupReadiness.ReportDatabaseReady();
    connectivity.ReportDatabaseHealthy();
}

await app.WaitForShutdownAsync();

public partial class Program;
