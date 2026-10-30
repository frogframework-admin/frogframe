using Apps.Sample.Components;
using Apps.Sample.Domain;
using BlazorBlueprint.Components;
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

// Penyimpanan: "InMemory" (default, data hilang saat restart) atau "PostgreSql" (permanen, siap pilot).
var usePostgres = string.Equals(builder.Configuration["Persistence:Provider"], "PostgreSql", StringComparison.OrdinalIgnoreCase);
if (usePostgres)
    builder.Services.AddDocFrameworkPostgreSql(builder.Configuration.GetConnectionString("DocFrame")
        ?? throw new InvalidOperationException("ConnectionStrings:DocFrame wajib diisi bila Persistence:Provider = PostgreSql"));

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
builder.Services.AddFieldValidator<NpwpValidator>();
builder.Services.AddEventHandler<WorkflowNotificationHandler>();
builder.Services.AddReport<SpendByDepartmentReport>();
// Method kustom yang dibuka sebagai API: POST /api/method/{nama}
builder.Services.AddApiMethod<SisaAnggaranMethod>();
builder.Services.AddApiMethod<AjukanMassalMethod>();
// Menu tambahan di grup HR (tampil bagi yang boleh membaca data Karyawan).
builder.Services.AddDeskMenuItem(new DeskMenuItem("HR", "Struktur Organisasi", "/hr/struktur-organisasi", "network", RequiresReadOf: "Employee"));

var app = builder.Build();

// Bahasa & format Indonesia untuk UI (nama bulan di date picker, dsb.).
// Penyimpanan dan API tetap memakai format invarian (yyyy-MM-dd, titik desimal).
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("id-ID").AddSupportedCultures("id-ID").AddSupportedUICultures("id-ID"));

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // batas permintaan /api per kunci API / pengguna (Api:RateLimitPerMinute)
app.UseAntiforgery();
app.MapStaticAssets();

app.MapDocFrameworkApi();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .AddAdditionalAssemblies(typeof(DeskLayout).Assembly);

// Health check untuk load balancer / monitoring.
app.MapGet("/health", async (IServiceProvider sp, CancellationToken ct) =>
{
    if (sp.GetService<NpgsqlDataSource>() is not { } db) return Results.Ok(new { status = "ok", storage = "in-memory" });
    try
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok", storage = "postgresql" });
    }
    catch (Exception ex) { return Results.Json(new { status = "unhealthy", storage = "postgresql", error = ex.Message }, statusCode: 503); }
});

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
app.Run();

public partial class Program;
