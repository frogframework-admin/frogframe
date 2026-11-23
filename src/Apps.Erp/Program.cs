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
using Framework.Web.Tenancy;
using Framework.Persistence.PostgreSql;
using Npgsql;

// The migration switches (see below) are not configuration: keep them away from the command-line configuration source,
// which would otherwise read the next argument as their value.
var builder = WebApplication.CreateBuilder(args.Where(a => !MigrationCommand.Requested([a])).ToArray());
// Secrets as files (Docker/Kubernetes secrets, Key Vault or Vault CSI mounts): one file per setting, named like the
// environment variable (ConnectionStrings__FrogFrame, Bootstrap__AdminPassword, ...). Values override other sources.
// Folder: Secrets:Directory, default /run/secrets; a missing folder is ignored. See docs/guides/secrets.md.
builder.Configuration.AddKeyPerFile(
    builder.Configuration["Secrets:Directory"] is { Length: > 0 } secretsDir ? secretsDir : "/run/secrets", optional: true);
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
// Optional single sign-on (Google / Microsoft Entra ID), enabled per provider by Authentication:* settings.
// Two-factor authentication options: Security:TwoFactor. See docs/guides/two-factor-and-sso.md.
builder.Services.AddFrogFrameworkSso(builder.Configuration);

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

// Refuse to start with an unsafe configuration (e.g. demo accounts with a known password outside Development),
// before anything is migrated or seeded. See Framework.Hosting.DeploymentChecks.
Framework.Hosting.DeploymentChecks.ThrowIfInvalid(app.Environment.IsDevelopment(), k => app.Configuration[k]);
var demoMode = Framework.Hosting.DemoMode.Resolve(app.Environment.IsDevelopment(), k => app.Configuration[k]);

// Migration step, separate from serving requests: "--migrate-only" applies pending migrations and exits,
// "--migration-status" lists them (exit code 2 when some are pending). The web server is not started.
// See docs/guides/database-migrations.md.
if (MigrationCommand.Requested(args))
{
    if (!usePostgres)
    {
        Console.Error.WriteLine("Migrations need Persistence:Provider=PostgreSql.");
        Environment.ExitCode = 1;
        return;
    }
    try
    {
        Environment.ExitCode = await MigrationCommand.RunAsync(app.Services.GetRequiredService<SchemaMigrator>(), args, Console.Out);
        // Database:AppRole: create/update the restricted runtime role and grant it access to the (new) tables.
        if (Environment.ExitCode == 0 && args.Contains(MigrationCommand.MigrateOnly))
            await app.Services.EnsureFrogFrameworkAppRoleAsync(app.Configuration["Database:AppRole"], app.Configuration["Database:AppRolePassword"]);
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Database migration failed.");
        Environment.ExitCode = 1;
    }
    return;
}
// Database:MigrateOnStartup=false: a separate migrate step applies migrations; this instance waits for it.
var migrateOnStartup = app.Configuration.GetValue("Database:MigrateOnStartup", true);

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
app.UseFrogFrameTenantStatus(); // suspended tenants: sign out / 403 (see /platform/tenants)
// Bahasa UI per pengguna (Indonesia/Inggris: cookie → preferensi tersimpan → Localization:DefaultLanguage).
// Format angka & tanggal tetap Indonesia; penyimpanan dan API tetap memakai format invarian (yyyy-MM-dd, titik desimal).
app.UseFrogFrameLocalization();
app.UseAuthorization();
app.UseFrogFrameTwoFactorRequirement(); // Security:TwoFactor:RequiredForRoles (no-op when empty)
app.UseRateLimiter(); // tiered limits: sign-in and password reset per IP; API read/write/heavy per user or key (docs/guides/rate-limiting.md)
app.UseAntiforgery();
app.MapStaticAssets();

// Health checks liveness & readiness
app.MapFrogFrameHealthChecks();
app.MapFrogFrameworkApi();
app.MapFrogFrameworkSso(); // /account/external/{provider} + callback (only providers that are configured)
Apps.Erp.Ess.EssEndpoints.MapEssApi(app); // /api/ess/v1 (aplikasi mobile ESS)
Apps.Erp.Careers.CareersEndpoints.MapCareersApi(app); // /api/careers/v1/{tenant} (portal karier publik, tanpa login)
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(DeskLayout).Assembly);

async Task InitializeDatabaseAndSeedAsync(CancellationToken ct)
{
    if (usePostgres)
    {
        if (migrateOnStartup)
        {
            await app.Services.MigrateFrogFrameworkDatabaseAsync(ct);
            await app.Services.EnsureFrogFrameworkAppRoleAsync(app.Configuration["Database:AppRole"], app.Configuration["Database:AppRolePassword"], ct);
        }
        else await app.Services.WaitForFrogFrameworkMigrationsAsync(
            TimeSpan.FromSeconds(app.Configuration.GetValue("Database:WaitForMigrationsSeconds", 600)), ct);
        // Database role segregation (docs/guides/database-roles.md): warn when requests run as a superuser or the table owner.
        try
        {
            var runtimeRole = await app.Services.InspectFrogFrameworkRuntimeRoleAsync(ct);
            if (!runtimeRole.IsSegregated)
                app.Logger.Log(app.Environment.IsDevelopment() ? LogLevel.Information : LogLevel.Warning,
                    "The application connects to PostgreSQL as {Role} (superuser: {Super}, owns tables: {Owner}, can create objects: {Create}). " +
                    "Use a separate data-only role in production: see docs/guides/database-roles.md.",
                    runtimeRole.Role, runtimeRole.IsSuperuser, runtimeRole.OwnsSchemaTables, runtimeRole.CanCreateInSchema);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { app.Logger.LogWarning(ex, "Could not check the database role of the application"); }
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
        // Tenants created at /platform/tenants (docs/guides/tenants.md).
        foreach (var t in await app.Services.GetRequiredService<Framework.Tenancy.ITenantStore>().ListAsync(ct))
            if (t.IsActive) allTenants.Add(t.Id);

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

    // Role split (docs/guides/roles.md): active metadata that still names a broad role (group) gives all of its members
    // the group's full rights; typically a DocType or workflow an administrator published before the split.
    foreach (var use in ErpRoles.FindGroupUses(app.Services.GetRequiredService<Framework.Metadata.IMetadataProvider>().Current))
        app.Logger.LogWarning("Permissions still name a role group, which gives every member its full rights: {Use}. Change them to granular roles (docs/guides/roles.md).", use);

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

    // Demo users and sample data only in demo mode (always in Development; elsewhere only when explicitly allowed).
    if (demoMode.SeedData)
    {
        try
        {
            await Framework.Web.Services.DemoUsers.EnsureDemoUsersAsync(app.Services, demoMode.Password);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Logger.LogWarning(ex, "Gagal seed sample demo users");
        }
        foreach (var t in allTenants)
        {
            try
            {
                await Apps.Erp.Hooks.WsmDemoSeeder.EnsureSampleDataAsync(app.Services, t, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                app.Logger.LogWarning(ex, "Failed to seed WSM/OM sample data for tenant {Tenant}", t);
            }
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
