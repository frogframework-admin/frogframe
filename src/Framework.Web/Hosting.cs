using Framework.Documents;
using Framework.Email;
using Framework.Hooks;
using Framework.Expressions;
using Framework.Files;
using Framework.Dashboards;
using Framework.Reports;
using Framework.Api;
using Framework.Imports;
using Framework.Navigation;
using Framework.Observability;
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
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Configuration;

namespace Framework.Web;

/// <summary>Pengguna aktif per tenant, untuk proses tanpa request (mis. menentukan penerima notifikasi).</summary>
public interface IUserDirectory
{
    Task<UserContext?> FindAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserContext>> ListAsync(string tenantId, CancellationToken ct = default);
}

internal sealed class DocumentHookLogger(ILogger<DocumentHookLogger> logger) : IDocHookLogger
{
    public void LogHookFailure(Exception exception, Type hookType, string docType, DocHookStage stage) =>
        logger.LogError(exception, "Document hook {HookName} failed for DocType {DocType} at stage {Stage}", hookType.FullName, docType, stage);
}

internal sealed class WebSchedulerLogger(ILogger<WebSchedulerLogger> logger) : ISchedulerLogger
{
    public void LogJobStarting(string jobName, string tenantId, DateTimeOffset scheduledTime) =>
        logger.LogInformation("Executing scheduled job {JobName} for tenant {TenantId} at {ScheduledTime}", jobName, tenantId, scheduledTime);

    public void LogJobCompleted(string jobName, string tenantId) =>
        logger.LogInformation("Completed scheduled job {JobName} for tenant {TenantId}", jobName, tenantId);

    public void LogJobFailed(Exception exception, string jobName, string tenantId) =>
        logger.LogError(exception, "Scheduled job {JobName} failed for tenant {TenantId}", jobName, tenantId);

    public void LogLockFailed(Exception exception, string jobName, string tenantId) =>
        logger.LogWarning(exception, "Failed to acquire lock for scheduled job {JobName} on tenant {TenantId}", jobName, tenantId);
}

/// <summary>User aktif pada scope (request API atau sirkuit Blazor).</summary>
public sealed class CurrentUser
{
    // Diisi dari klaim login (AuthenticationState) oleh komponen Routes; lihat ClaimsUserMapper.
    public UserContext? User { get; private set; }
    public string? ActiveModuleId { get; private set; }
    public event Action? Changed;
    public event Action? ModuleChanged;
    public UserContext Required => User ?? throw new PermissionDeniedException("Not authenticated.");
    public void Set(UserContext? user) { User = user; Changed?.Invoke(); }
    public void SetActiveModule(string? moduleId)
    {
        if (ActiveModuleId == moduleId) return;
        ActiveModuleId = moduleId;
        ModuleChanged?.Invoke();
    }
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
        services.TryAddSingleton<IDatabaseHealthCheck>(sp => sp.GetRequiredService<InMemoryDocumentStore>());
        services.TryAddSingleton<INotificationStore, InMemoryNotificationStore>();
        services.TryAddSingleton<IFileStore, InMemoryFileStore>();
        services.TryAddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        services.AddSingleton<AttachmentService>();

        // Email outgoing: SMTP jika dikonfigurasi, InMemory untuk dev/test
        services.TryAddSingleton<IEmailSender>(sp =>
        {
            var cfg = sp.GetService<IConfiguration>();
            var smtpHost = cfg?["Email:SmtpHost"];
            if (!string.IsNullOrWhiteSpace(smtpHost))
            {
                var opts = new SmtpEmailOptions
                {
                    Host = smtpHost,
                    Port = cfg.GetValue("Email:SmtpPort", 25),
                    EnableSsl = cfg.GetValue("Email:EnableSsl", false),
                    UserName = cfg["Email:SmtpUser"],
                    Password = cfg["Email:SmtpPassword"],
                    FromAddress = cfg["Email:FromAddress"] ?? "noreply@docframe.local",
                    FromDisplayName = cfg["Email:FromDisplayName"] ?? "DocFrame"
                };
                return new SmtpEmailSender(opts);
            }
            return new InMemoryEmailSender();
        });
        services.TryAddSingleton<IEmailQueue, OutboxEmailQueue>();
        services.AddSingleton<IEventHandler, OutboxEmailHandler>();
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
        services.TryAddSingleton<IDocHookLogger, DocumentHookLogger>();
        services.AddSingleton<HookRunner>();
        services.AddSingleton<DocumentService>();
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<ReportService>();
        services.AddHostedService<OutboxWorker>();

        // Background Scheduler
        services.TryAddSingleton<ISchedulerLogger, WebSchedulerLogger>();
        services.TryAddSingleton<IDistributedLock, InMemoryDistributedLock>();
        services.TryAddSingleton<ITenantProvider, DefaultTenantProvider>();
        services.TryAddSingleton<IScheduledJobLogStore, InMemoryScheduledJobLogStore>();
        services.TryAddSingleton<IErrorLogStore, InMemoryErrorLogStore>();
        services.AddSingleton<ScheduledJobRunner>();
        services.AddHostedService<SchedulerWorker>();

        services.AddScoped<CurrentUser>();

        // Cetak & PDF
        services.TryAddSingleton<ILetterheadProvider, ConfigLetterheadProvider>();
        services.TryAddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        services.AddSingleton<PrintService>();
        services.AddSingleton<PrintHtmlRenderer>();
        return services;
    }

    public static IServiceCollection AddDocHandler<T>(this IServiceCollection s) where T : class, IDocumentHandler => s.AddSingleton<IDocumentHandler, T>();
    public static IServiceCollection AddDocHook<TDoc, THook>(this IServiceCollection s) where THook : class =>
        s.AddSingleton<THook>().AddSingleton<IDocHookRegistration>(sp => new DocHookRegistration<TDoc, THook>(sp.GetRequiredService<THook>()));
    public static IServiceCollection AddFormAction<TDoc, TAction>(this IServiceCollection s) where TAction : class, Framework.Actions.IFormAction<TDoc> =>
        s.AddSingleton<Framework.Actions.IFormAction, TAction>();
    public static IServiceCollection AddFieldValidator<T>(this IServiceCollection s) where T : class, IFieldValidator => s.AddSingleton<IFieldValidator, T>();
    public static IServiceCollection AddEventHandler<T>(this IServiceCollection s) where T : class, IEventHandler => s.AddSingleton<IEventHandler, T>();
    public static IServiceCollection AddReport<T>(this IServiceCollection s) where T : class, IReportProvider => s.AddSingleton<IReportProvider, T>();
    public static IServiceCollection AddFieldType<T>(this IServiceCollection s) where T : class, IFieldType => s.AddSingleton<IFieldType, T>();
    public static IServiceCollection AddDeskMenuItem(this IServiceCollection s, DeskMenuItem item) => s.AddSingleton(item);
    /// <summary>Membuka method C# sebagai API: POST /api/method/{nama}.</summary>
    public static IServiceCollection AddApiMethod<T>(this IServiceCollection s) where T : class, IApiMethod => s.AddSingleton<IApiMethod, T>();
    public static IServiceCollection AddScheduledJob<TJob>(this IServiceCollection s) where TJob : class, IScheduledJob
    {
        s.TryAddSingleton<IDistributedLock, InMemoryDistributedLock>();
        s.TryAddSingleton<ITenantProvider, DefaultTenantProvider>();
        s.TryAddSingleton<IScheduledJobLogStore, InMemoryScheduledJobLogStore>();
        s.AddSingleton<IScheduledJob, TJob>();
        return s;
    }

    /// <summary>
    /// Configures OpenTelemetry metrics and activity tracing for DocFrame.
    /// If OpenTelemetry:OtlpEndpoint or OTEL_EXPORTER_OTLP_ENDPOINT is configured, exports to that collector.
    /// </summary>
    public static IServiceCollection AddDocFrameOpenTelemetry(this IServiceCollection services, IConfiguration? config = null)
    {
        var otlpEndpoint = config?["OpenTelemetry:OtlpEndpoint"] 
            ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        var otel = services.AddOpenTelemetry();

        otel.WithTracing(tracing =>
        {
            tracing.AddSource(DocFrameTelemetry.SourceName);
            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                tracing.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter(DocFrameTelemetry.SourceName);
            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                metrics.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
            }
        });

        return services;
    }
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

public sealed class SchedulerWorker(
    ScheduledJobRunner runner,
    TimeProvider clock,
    ILogger<SchedulerWorker> log,
    IConfiguration? config = null) : BackgroundService
{
    private TimeSpan Interval => TimeSpan.FromSeconds(Math.Max(5, config?.GetValue("Scheduler:PollIntervalSeconds", 15) ?? 15));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = clock.GetUtcNow();
                var result = await runner.RunDueJobsAsync(now, stoppingToken);
                if (result.Executed > 0 || result.Failed > 0)
                {
                    log.LogInformation("Scheduler: {Executed} executed, {Failed} failed, {Skipped} locked",
                        result.Executed, result.Failed, result.SkippedLocked);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Scheduler worker encountered an error");
            }
        }
    }
}
