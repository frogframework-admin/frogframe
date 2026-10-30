using Framework.Documents;
using Framework.Expressions;
using Framework.Files;
using Framework.Dashboards;
using Framework.Reports;
using Framework.Api;
using Framework.Imports;
using Framework.Navigation;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;
using Framework.Web.Printing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Framework.Web;

/// <summary>Pengguna aktif per tenant, untuk proses tanpa request (mis. menentukan penerima notifikasi).</summary>
public interface IUserDirectory
{
    Task<UserContext?> FindAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default);
}

/// <summary>User aktif pada scope (request API atau sirkuit Blazor).</summary>
public sealed class CurrentUser
{
    // Diisi dari klaim login (AuthenticationState) oleh komponen Routes; lihat ClaimsUserMapper.
    public UserContext? User { get; private set; }
    public event Action? Changed;
    public UserContext Required => User ?? throw new PermissionDeniedException("Belum masuk");
    public void Set(UserContext? user) { User = user; Changed?.Invoke(); }
}


/// <summary>Item menu tambahan di sidebar (mis. halaman Struktur Organisasi di grup HR).</summary>
/// <param name="Group">Nama grup menu; sama dengan Module DocType agar tampil di grup yang sama.</param>
/// <param name="RequiresReadOf">Opsional: hanya tampil bila pengguna boleh membaca DocType ini.</param>
public sealed record DeskMenuItem(string Group, string Title, string Href, string Icon = "file-text", string? RequiresReadOf = null, int Order = 100);

public sealed class DocFrameworkOptions
{
    public List<DocTypeDef> DocTypes { get; } = [];
    public List<WorkflowDef> Workflows { get; } = [];

    /// <summary>Memuat *.doctype.json dan *.workflow.json dari folder.</summary>
    public DocFrameworkOptions LoadFrom(string directory)
    {
        foreach (var f in Directory.GetFiles(directory, "*.doctype.json").Order())
            DocTypes.Add(MetadataJson.ParseDocType(File.ReadAllText(f)));
        foreach (var f in Directory.GetFiles(directory, "*.workflow.json").Order())
            Workflows.Add(MetadataJson.ParseWorkflow(File.ReadAllText(f)));
        return this;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocFramework(this IServiceCollection services, Action<DocFrameworkOptions> configure)
    {
        var options = new DocFrameworkOptions();
        configure(options);

        services.TryAddSingleton(TimeProvider.System);
        foreach (var t in FieldTypeRegistry.BuiltIns()) services.AddSingleton(t);
        services.AddSingleton<IFieldTypeRegistry>(sp => new FieldTypeRegistry(sp.GetServices<IFieldType>()));
        services.AddSingleton(_ => FunctionRegistry.CreateDefault());
        services.AddSingleton<ExpressionEvaluator>();
        services.AddSingleton<MetadataCompiler>();
        services.AddSingleton(sp => new MetadataRegistry(sp.GetRequiredService<MetadataCompiler>(), options.DocTypes, options.Workflows,
            sp.GetRequiredService<TimeProvider>(), sp.GetService<IMetadataVersionStore>()));
        services.AddSingleton<IMetadataProvider>(sp => sp.GetRequiredService<MetadataRegistry>());

        // Penyimpanan default in-memory. TryAdd → provider lain (mis. AddDocFrameworkPostgreSql) bisa menggantinya
        // tanpa bergantung urutan pemanggilan.
        services.TryAddSingleton<InMemoryDocumentStore>();
        services.TryAddSingleton<IDocumentStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.TryAddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.TryAddSingleton<INotificationStore, InMemoryNotificationStore>();
        services.TryAddSingleton<IFileStore, InMemoryFileStore>();
        // Dashboard SQL: default tidak tersedia (butuh PostgreSQL + koneksi read-only; lihat AddDocFrameworkReporting).
        services.TryAddSingleton<IWidgetStore, InMemoryWidgetStore>();
        services.TryAddSingleton<IReportingQueryExecutor, UnavailableReportingExecutor>();
        // Template laporan Excel/Word: memakai eksekutor read-only yang sama dengan dashboard.
        services.TryAddSingleton<IReportTemplateStore, InMemoryReportTemplateStore>();
        // API integrasi: kunci API + method kustom + batas laju per kunci/pengguna.
        services.TryAddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.AddSingleton<ApiKeyService>();
        services.AddSingleton<ApiMethodRegistry>();
        services.TryAddSingleton<IQueryApiStore, InMemoryQueryApiStore>();
        services.AddSingleton<QueryApiService>();
        // Impor Excel/CSV ke DocType (lewat DocumentService yang sama dengan form).
        services.TryAddSingleton<IImportLogStore, InMemoryImportLogStore>();
        // Navigasi menu sidebar dinamis.
        services.TryAddSingleton<INavigationMenuStore, InMemoryNavigationMenuStore>();
        // Master roles pengguna.
        services.TryAddSingleton<IRoleStore, InMemoryRoleStore>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            return new ImportLimits
            {
                MaxRows = cfg?.GetValue("Import:MaxRows", 10_000) ?? 10_000,
                MaxFileBytes = (cfg?.GetValue("Import:MaxFileMb", 10) ?? 10) * 1024L * 1024L,
            };
        });
        services.AddSingleton<DataImportService>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                await ctx.HttpContext.Response.WriteAsJsonAsync(new { title = "Terlalu banyak permintaan", detail = "Batas permintaan per menit terlampaui. Coba lagi sebentar lagi.", status = 429 }, ct);
            };
            o.AddPolicy(ApiRateLimit, http =>
            {
                var who = http.User.FindFirst(Framework.Web.Identity.ApiKeyDefaults.KeyIdClaim)?.Value is { } k ? "k:" + k
                        : http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } u ? "u:" + u
                        : "ip:" + http.Connection.RemoteIpAddress;
                var limit = http.RequestServices.GetService<IConfiguration>()?.GetValue("Api:RateLimitPerMinute", 300) ?? 300;
                return RateLimitPartition.GetFixedWindowLimiter(who, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
        services.AddSingleton<ReportDownloads>();
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            return new ReportTemplateService(sp.GetRequiredService<IReportTemplateStore>(), sp.GetRequiredService<IReportingQueryExecutor>(), sp.GetRequiredService<TimeProvider>(),
                new ReportLimits
                {
                    MaxRowsExcel = cfg?.GetValue("Reports:MaxRowsExcel", 50_000) ?? 50_000,
                    MaxRowsWord = cfg?.GetValue("Reports:MaxRowsWord", 5_000) ?? 5_000,
                    StatementTimeout = TimeSpan.FromSeconds(cfg?.GetValue("Reports:StatementTimeoutSeconds", 30) ?? 30),
                });
        });
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            return new DashboardService(sp.GetRequiredService<IWidgetStore>(), sp.GetRequiredService<IReportingQueryExecutor>(),
                sp.GetRequiredService<TimeProvider>(), cfg?.GetValue("Dashboard:PreviewLimit", 100) ?? 100, cfg?.GetValue("Dashboard:WidgetRowLimit", 1000) ?? 1000);
        });
        services.AddSingleton<FileService>();

        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<DocumentEvaluator>();
        services.AddSingleton<IValidatorRegistry>(sp => new ValidatorRegistry(sp.GetServices<IFieldValidator>()));
        services.AddSingleton<ValidationEngine>();
        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton<IDocumentHandlerRegistry>(sp => new DocumentHandlerRegistry(sp.GetServices<IDocumentHandler>()));
        services.AddSingleton<DocumentService>();
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<ReportService>();
        services.AddHostedService<OutboxWorker>();
        services.AddScoped<CurrentUser>();

        // Cetak & PDF
        services.TryAddSingleton<ILetterheadProvider, ConfigLetterheadProvider>();
        services.TryAddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        services.AddSingleton<PrintService>();
        services.AddSingleton<PrintHtmlRenderer>();
        return services;
    }

    public static IServiceCollection AddDocHandler<T>(this IServiceCollection s) where T : class, IDocumentHandler => s.AddSingleton<IDocumentHandler, T>();
    public static IServiceCollection AddFieldValidator<T>(this IServiceCollection s) where T : class, IFieldValidator => s.AddSingleton<IFieldValidator, T>();
    public static IServiceCollection AddEventHandler<T>(this IServiceCollection s) where T : class, IEventHandler => s.AddSingleton<IEventHandler, T>();
    public static IServiceCollection AddReport<T>(this IServiceCollection s) where T : class, IReportProvider => s.AddSingleton<IReportProvider, T>();
    public static IServiceCollection AddFieldType<T>(this IServiceCollection s) where T : class, IFieldType => s.AddSingleton<IFieldType, T>();
    public static IServiceCollection AddDeskMenuItem(this IServiceCollection s, DeskMenuItem item) => s.AddSingleton(item);
    /// <summary>Membuka method C# sebagai API: POST /api/method/{nama}.</summary>
    public static IServiceCollection AddApiMethod<T>(this IServiceCollection s) where T : class, IApiMethod => s.AddSingleton<IApiMethod, T>();
    public const string ApiRateLimit = "docframe-api";
}

public sealed class OutboxWorker(OutboxProcessor processor, ILogger<OutboxWorker> log, IConfiguration? config = null) : BackgroundService
{
    /// <summary>Masa simpan pesan yang sudah diproses (Outbox:RetentionDays, default 7). Dead-letter tidak dihapus.</summary>
    private TimeSpan Retention => TimeSpan.FromDays(Math.Max(1, config?.GetValue("Outbox:RetentionDays", 7) ?? 7));
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (DateTimeOffset.UtcNow >= _nextPrune)
            {
                _nextPrune = DateTimeOffset.UtcNow.AddHours(1);
                try
                {
                    var pruned = await processor.PruneAsync(Retention, stoppingToken);
                    if (pruned > 0) log.LogInformation("Outbox: {Count} pesan lama dihapus (retensi {Days} hari)", pruned, Retention.TotalDays);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Retensi outbox gagal"); }
            }
            try
            {
                var r = await processor.ProcessDueAsync(stoppingToken);
                if (r.Failed + r.DeadLettered > 0)
                    log.LogWarning("Outbox: {Processed} diproses, {Failed} dijadwalkan ulang, {Dead} dead-letter", r.Processed, r.Failed, r.DeadLettered);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Outbox worker gagal");
            }
        }
    }
}
