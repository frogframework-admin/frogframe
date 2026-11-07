using Apps.Sample.Components;
using Apps.Sample.Domain;
using Apps.Sample.Hooks;
using BlazorBlueprint.Components;
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
builder.Services.AddDocFramework(o => o.LoadFrom(Path.Combine(builder.Environment.ContentRootPath, "Metadata")));
builder.Services.AddDocFrameOpenTelemetry(builder.Configuration);

// Penyimpanan: "InMemory" (default, data hilang saat restart) atau "PostgreSql" (permanen, siap pilot).
var usePostgres = string.Equals(builder.Configuration["Persistence:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase);
if (usePostgres)
    // DocFrame = peran app (operasi data). DocFrameSchema (opsional) = pemilik skema untuk migrasi & indeks;
    // dengan pemisahan ini peran app tidak bisa mengubah/menghapus buku besar (lihat deploy/sql/gl-roles.sql).
    builder.Services.AddDocFrameworkPostgreSql(builder.Configuration.GetConnectionString("DocFrame")
        ?? throw new InvalidOperationException("ConnectionStrings:DocFrame wajib diisi bila Persistence:Provider = PostgreSql"),
        builder.Configuration.GetConnectionString("DocFrameSchema"));

// Login: ASP.NET Core Identity (cookie untuk browser, bearer token untuk API). Klaim → UserContext.
builder.Services.AddDocFrameworkIdentity();

// Dashboard SQL: butuh PostgreSQL + user database read-only (deploy/sql/reporting-readonly-user.sql).
var readOnlyConnection = builder.Configuration.GetConnectionString("DocFrameReadOnly");
var useReporting = usePostgres && !string.IsNullOrWhiteSpace(readOnlyConnection);
if (useReporting)
    builder.Services.AddDocFrameworkReporting(readOnlyConnection!, o =>
    {
        o.ReadOnlyRole = builder.Configuration["Dashboard:ReadOnlyRole"] ?? "docframe_ro";
        o.StatementTimeoutSeconds = builder.Configuration.GetValue("Dashboard:StatementTimeoutSeconds", 5);
        // Instance analitik terpisah (replika logis). Kosong = database aplikasi.
        o.AnalyticsConnectionString = builder.Configuration.GetConnectionString("DocFrameAnalytics");
    });

// 2) Extension point eksplisit (RFC §2.5): handler, validator, event handler, report — semuanya C# terdaftar via DI.
builder.Services.AddDocHandler<PurchaseRequestHandler>();
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestBeforeValidateHook>();
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestBeforeInsertHook>();
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestAfterSaveHook>();
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestOnLoadHook>();
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestBeforeTransitionHook>();
// Akuntansi: aturan akun & periode, posting jurnal ke buku besar (dalam transaksi yang sama dengan dokumen)
builder.Services.AddDocHook<Account, AccountRulesHook>();
builder.Services.AddDocHook<AccountingPeriod, AccountingPeriodRulesHook>();
builder.Services.AddDocHook<JournalEntry, JournalEntryPostingHook>();
builder.Services.AddDocHook<JournalEntry, JournalEntryRulesHook>();
builder.Services.AddDocHook<AccountingSettings, AccountingSettingsRulesHook>();
builder.Services.AddDocHook<SalesInvoice, SalesInvoiceHook>();
builder.Services.AddDocHook<PurchaseInvoice, PurchaseInvoiceHook>();
builder.Services.AddDocHook<PaymentEntry, PaymentEntryHook>();

// HR hooks: Employee, Department
builder.Services.AddDocHook<Employee, EmployeeOnLoadHook>();
builder.Services.AddDocHook<Employee, EmployeeAfterSaveHook>();
builder.Services.AddDocHook<Department, DepartmentBeforeValidateHook>();
builder.Services.AddDocHook<Department, DepartmentBeforeInsertHook>();
builder.Services.AddDocHook<Department, DepartmentBeforeUpdateHook>();
builder.Services.AddDocHook<Department, DepartmentRefreshHierarchyHook>();

// Procurement hooks: Vendor
builder.Services.AddDocHook<Vendor, VendorBeforeValidateHook>();
builder.Services.AddDocHook<Vendor, VendorBeforeInsertHook>();
builder.Services.AddDocHook<Vendor, VendorBeforeUpdateHook>();
builder.Services.AddDocHook<Vendor, VendorOnLoadHook>();
builder.Services.AddDocHook<Vendor, VendorAfterSaveHook>();

builder.Services.AddFieldValidator<NpwpValidator>();
builder.Services.AddEventHandler<WorkflowNotificationHandler>();
builder.Services.AddReport<SpendByDepartmentReport>();
// Method kustom yang dibuka sebagai API: POST /api/method/{nama}
builder.Services.AddApiMethod<SisaAnggaranMethod>();
builder.Services.AddApiMethod<AjukanMassalMethod>();
// Menu tambahan di grup HR (tampil bagi yang boleh membaca data Karyawan).
builder.Services.AddDeskMenuItem(new DeskMenuItem("HR", "Struktur Organisasi", "/hr/struktur-organisasi", "network", RequiresReadOf: "Employee"));
builder.Services.AddScheduledJob<NotificationRuleDateJob>();

// Hardening HTTP production: ForwardedHeaders & HSTS
builder.Services.AddDocFrameHttpHardening(builder.Configuration);

var app = builder.Build();

// 1. Reverse proxy forwarded headers (Docker, Traefik, Nginx, ALB)
app.UseForwardedHeaders();

// 2. Exception handling & HSTS
if (!app.Environment.IsDevelopment())
{
    app.UseDocFrameExceptionHandler();
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// 404 & HTTP status code handling: re-execute to Blazor error pages
app.UseStatusCodePagesWithReExecute("/error/{0}");

// 3. Global security headers (nosniff, SAMEORIGIN, CSP, Referrer, Permissions)
app.UseDocFrameSecurityHeaders();

// Bahasa & format Indonesia untuk UI (nama bulan di date picker, dsb.).
// Penyimpanan dan API tetap memakai format invarian (yyyy-MM-dd, titik desimal).
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("id-ID").AddSupportedCultures("id-ID").AddSupportedUICultures("id-ID"));

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // batas permintaan /api per kunci API / pengguna (Api:RateLimitPerMinute)
app.UseAntiforgery();
app.MapStaticAssets();

// Health checks liveness & readiness
app.MapDocFrameHealthChecks();
app.MapDocFrameworkApi();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(DeskLayout).Assembly);

if (usePostgres)
{
    await app.Services.MigrateDocFrameworkDatabaseAsync();
    // Indeks dari metadata dibangun di latar belakang (CONCURRENTLY): startup tidak menunggu pembangunan indeks
    // pada tabel besar, dan tabel tetap bisa ditulis selama proses. Diperbarui otomatis setiap metadata dipublikasikan.
    _ = Task.Run(async () =>
    {
        try { await app.Services.SyncDocumentIndexesAsync(); }
        catch (Exception ex) { app.Logger.LogError(ex, "Sinkronisasi indeks dokumen gagal"); }
    });
}
// View pelaporan dibuat dari metadata aktif (dan diperbarui otomatis setiap publish).
string? reportingProblem = null;
if (useReporting)
{
    await app.Services.SyncReportingSchemaAsync();
    // Peringatan di log bila user read-only belum siap. Aplikasi tetap berjalan; dashboard menampilkan pesannya.
    reportingProblem = await app.Services.CheckReportingConnectionAsync();
}

// Audit metadata terhadap master role & modul tiap tenant yang dipantau (Metadata:AuditTenants).
// Hanya melaporkan ke log; tidak mengubah metadata. Mencegah masalah baru ketahuan saat publish ulang.
{
    var auditTenants = app.Configuration.GetSection("Metadata:AuditTenants").Get<string[]>() ?? [];
    var registry = app.Services.GetRequiredService<MetadataRegistry>();
    var catalog = app.Services.GetRequiredService<RoleCatalog>();
    foreach (var tenant in auditTenants)
    {
        foreach (var issue in registry.AuditAgainst(catalog.Scope(tenant)))
            app.Logger.LogWarning("Metadata tidak cocok dengan master (tenant {Tenant}): {Source} › {Path}: {Message}",
                tenant, issue.Source, issue.Path, issue.Message);
    }
}

// Akun: data demo (pengembangan) atau satu admin awal dari konfigurasi "Bootstrap" (pilot).
if (app.Configuration.GetValue("Demo:SeedData", false))
{
    await DemoAccounts.SeedAsync(app.Services, app.Configuration["Demo:Password"] ?? throw new InvalidOperationException("Demo:Password wajib diisi"));
    await Seeder.RunAsync(app.Services);
    // Widget contoh butuh koneksi read-only yang berfungsi. Bila belum, dilewati — dibuat otomatis
    // pada startup berikutnya setelah koneksi diperbaiki (selama tenant belum punya widget).
    if (useReporting && reportingProblem is null) await DemoDashboard.SeedAsync(app.Services);
}
await BootstrapAdmin.EnsureAsync(app.Services, app.Configuration);

// Pastikan menu default dan role default ter-seed untuk tenant acme/bootstrap
var tenantToSeed = app.Configuration["Bootstrap:TenantId"] ?? "acme";
try
{
    await app.Services.GetRequiredService<Framework.Navigation.INavigationMenuStore>().EnsureDefaultMenusAsync(tenantToSeed);
    await app.Services.GetRequiredService<Framework.Security.IRoleStore>().EnsureDefaultRolesAsync(tenantToSeed);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Gagal seed awal navigasi menu/role");
}

app.Run();

public partial class Program;
