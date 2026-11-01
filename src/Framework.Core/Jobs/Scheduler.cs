using System.Collections.Concurrent;
using Framework.Documents;
using Framework.Security;

namespace Framework.Jobs;

/// <summary>
/// Distributed lock interface for cross-instance coordination (PostgreSQL advisory lock or in-memory).
/// </summary>
public interface IDistributedLock
{
    Task<IDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout = default, CancellationToken ct = default);
}

/// <summary>
/// In-memory distributed lock provider using SemaphoreSlim per key.
/// </summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<IDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout = default, CancellationToken ct = default)
    {
        var sem = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        var acquired = await sem.WaitAsync(timeout, ct).ConfigureAwait(false);
        if (!acquired) return null;
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                sem.Release();
            }
        }
    }
}

/// <summary>
/// Resolves active tenants for multi-tenant background scheduling.
/// </summary>
public interface ITenantProvider
{
    Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct = default);
}

public sealed class DefaultTenantProvider : ITenantProvider
{
    private readonly IReadOnlyList<string> _tenants;

    public DefaultTenantProvider(IEnumerable<string>? tenants = null)
    {
        _tenants = tenants?.ToList() is { Count: > 0 } list ? list : ["acme"];
    }

    public Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct = default) =>
        Task.FromResult(_tenants);
}

/// <summary>
/// Logger abstraction for scheduled job lifecycle events without direct Microsoft.Extensions.Logging dependency in Core.
/// </summary>
public interface ISchedulerLogger
{
    void LogJobStarting(string jobName, string tenantId, DateTimeOffset scheduledTime);
    void LogJobCompleted(string jobName, string tenantId);
    void LogJobFailed(Exception exception, string jobName, string tenantId);
    void LogLockFailed(Exception exception, string jobName, string tenantId);
}

public sealed class NullSchedulerLogger : ISchedulerLogger
{
    public static readonly NullSchedulerLogger Instance = new();
    public void LogJobStarting(string jobName, string tenantId, DateTimeOffset scheduledTime) { }
    public void LogJobCompleted(string jobName, string tenantId) { }
    public void LogJobFailed(Exception exception, string jobName, string tenantId) { }
    public void LogLockFailed(Exception exception, string jobName, string tenantId) { }
}

/// <summary>
/// Scheduled job definition (inspired by Frappe scheduler_events, strictly typed and audited).
/// </summary>
public interface IScheduledJob
{
    string Name { get; }
    /// <summary>
    /// Standard 5-field cron expression or shorthand (@daily, @hourly, @every_5m, @every_15m, @every_30m).
    /// </summary>
    string Schedule { get; }
    Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct);
}

public sealed class ScheduledJobContext
{
    public required string TenantId { get; init; }
    public required UserContext SystemUser { get; init; }
    public required DocumentService Docs { get; init; }
    public required IServiceProvider Services { get; init; }
    public required DateTimeOffset ScheduledTime { get; init; }
    public ISchedulerLogger Logger { get; init; } = NullSchedulerLogger.Instance;
}

public sealed record ScheduledJobRunResult(int Evaluated, int Executed, int SkippedLocked, int Failed);

/// <summary>
/// Evaluates and parses cron expressions and standard shorthands.
/// </summary>
public static class CronSchedule
{
    public static string Normalize(string cronExpression) =>
        cronExpression.Trim().ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            "@every_minute" => "* * * * *",
            "@every_5m" => "*/5 * * * *",
            "@every_10m" => "*/10 * * * *",
            "@every_15m" => "*/15 * * * *",
            "@every_30m" => "*/30 * * * *",
            _ => cronExpression.Trim()
        };

    public static bool IsDue(string cronExpression, DateTimeOffset now, DateTimeOffset? lastRun = null)
    {
        if (lastRun.HasValue &&
            lastRun.Value.Year == now.Year &&
            lastRun.Value.Month == now.Month &&
            lastRun.Value.Day == now.Day &&
            lastRun.Value.Hour == now.Hour &&
            lastRun.Value.Minute == now.Minute)
        {
            return false;
        }

        var normalized = Normalize(cronExpression);
        var parts = normalized.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        // parts: 0:minute, 1:hour, 2:dayOfMonth, 3:month, 4:dayOfWeek
        if (!MatchesPart(parts[0], now.Minute, 0, 59)) return false;
        if (!MatchesPart(parts[1], now.Hour, 0, 23)) return false;
        if (!MatchesPart(parts[2], now.Day, 1, 31)) return false;
        if (!MatchesPart(parts[3], now.Month, 1, 12)) return false;

        var dow = (int)now.DayOfWeek; // 0 = Sunday
        if (dow == 0)
        {
            if (!MatchesPart(parts[4], 0, 0, 7) && !MatchesPart(parts[4], 7, 0, 7)) return false;
        }
        else
        {
            if (!MatchesPart(parts[4], dow, 0, 7)) return false;
        }

        return true;
    }

    public static bool MatchesPart(string part, int val, int min, int max)
    {
        if (part == "*") return true;

        if (part.Contains(','))
        {
            return part.Split(',').Any(sub => MatchesPart(sub.Trim(), val, min, max));
        }

        if (part.Contains('/'))
        {
            var slash = part.Split('/');
            if (slash.Length == 2 && int.TryParse(slash[1], out var step) && step > 0)
            {
                var basePart = slash[0].Trim();
                if (basePart == "*")
                {
                    return (val - min) % step == 0;
                }
                if (basePart.Contains('-'))
                {
                    var range = basePart.Split('-');
                    if (range.Length == 2 && int.TryParse(range[0], out var rMin) && int.TryParse(range[1], out var rMax))
                    {
                        return val >= rMin && val <= rMax && (val - rMin) % step == 0;
                    }
                }
            }
            return false;
        }

        if (part.Contains('-'))
        {
            var range = part.Split('-');
            if (range.Length == 2 && int.TryParse(range[0], out var rMin) && int.TryParse(range[1], out var rMax))
            {
                return val >= rMin && val <= rMax;
            }
            return false;
        }

        return int.TryParse(part, out var exact) && exact == val;
    }
}

/// <summary>
/// Executes due scheduled jobs across active tenants with distributed locking.
/// </summary>
public sealed class ScheduledJobRunner(
    IEnumerable<IScheduledJob> jobs,
    ITenantProvider tenantProvider,
    IDistributedLock distributedLock,
    DocumentService documentService,
    IServiceProvider serviceProvider,
    ISchedulerLogger? logger = null)
{
    private readonly ISchedulerLogger _logger = logger ?? NullSchedulerLogger.Instance;
    private readonly ConcurrentDictionary<(string JobName, string TenantId), DateTimeOffset> _lastRuns = new();

    public DateTimeOffset? GetLastRun(string jobName, string tenantId) =>
        _lastRuns.TryGetValue((jobName, tenantId), out var dt) ? dt : null;

    public void RecordRun(string jobName, string tenantId, DateTimeOffset time) =>
        _lastRuns[(jobName, tenantId)] = time;

    public async Task<ScheduledJobRunResult> RunDueJobsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var tenants = await tenantProvider.GetActiveTenantsAsync(ct).ConfigureAwait(false);
        int evaluated = 0, executed = 0, skippedLocked = 0, failed = 0;

        foreach (var job in jobs)
        {
            foreach (var tenant in tenants)
            {
                evaluated++;
                var lastRun = GetLastRun(job.Name, tenant);
                if (!CronSchedule.IsDue(job.Schedule, now, lastRun))
                    continue;

                var lockKey = $"docframe:scheduler:{job.Name}:{tenant}";
                IDisposable? lockHandle = null;
                try
                {
                    lockHandle = await distributedLock.TryAcquireAsync(lockKey, TimeSpan.Zero, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogLockFailed(ex, job.Name, tenant);
                    continue;
                }

                if (lockHandle == null)
                {
                    skippedLocked++;
                    continue;
                }

                using (lockHandle)
                {
                    RecordRun(job.Name, tenant, now);
                    var context = new ScheduledJobContext
                    {
                        TenantId = tenant,
                        SystemUser = UserContext.System(tenant),
                        Docs = documentService,
                        Services = serviceProvider,
                        ScheduledTime = now,
                        Logger = _logger
                    };

                    try
                    {
                        _logger.LogJobStarting(job.Name, tenant, now);
                        await job.ExecuteAsync(context, ct).ConfigureAwait(false);
                        executed++;
                        _logger.LogJobCompleted(job.Name, tenant);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        _logger.LogJobFailed(ex, job.Name, tenant);
                    }
                }
            }
        }

        return new ScheduledJobRunResult(evaluated, executed, skippedLocked, failed);
    }
}
