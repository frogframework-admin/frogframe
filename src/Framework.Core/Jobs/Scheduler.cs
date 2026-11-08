using System.Collections.Concurrent;
using Framework.Documents;
using Framework.Observability;
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

public sealed record ScheduledJobRunEntry
{
    public required string JobName { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? DurationMs { get; init; }
    public required string Status { get; init; } // "Success", "Failed", "SkippedLocked"
    public string? Error { get; init; }
}

/// <summary>
/// Persistence contract for scheduled job execution history and audit log.
/// </summary>
public interface IScheduledJobLogStore
{
    Task<DateTimeOffset?> GetLastRunAsync(string jobName, string tenantId, CancellationToken ct = default);
    Task RecordRunAsync(ScheduledJobRunEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJobRunEntry>> GetRecentRunsAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default);
}

/// <summary>
/// In-memory scheduled job log store for development and testing.
/// </summary>
public sealed class InMemoryScheduledJobLogStore : IScheduledJobLogStore
{
    private readonly ConcurrentDictionary<(string JobName, string TenantId), DateTimeOffset> _lastRuns = new();
    private readonly ConcurrentBag<ScheduledJobRunEntry> _entries = new();

    public Task<DateTimeOffset?> GetLastRunAsync(string jobName, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(_lastRuns.TryGetValue((jobName, tenantId), out var dt) ? (DateTimeOffset?)dt : null);
    }

    public Task RecordRunAsync(ScheduledJobRunEntry entry, CancellationToken ct = default)
    {
        _lastRuns[(entry.JobName, entry.TenantId)] = entry.StartedAt;
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScheduledJobRunEntry>> GetRecentRunsAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default)
    {
        var runs = _entries
            .Where(e => tenantId is null || e.TenantId == tenantId)
            .OrderByDescending(e => e.StartedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ScheduledJobRunEntry>>(runs);
    }

    public IReadOnlyCollection<ScheduledJobRunEntry> Entries => _entries;
}

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
    IScheduledJobLogStore? logStore = null,
    ISchedulerLogger? logger = null)
{
    private readonly ISchedulerLogger _logger = logger ?? NullSchedulerLogger.Instance;
    private readonly IScheduledJobLogStore _logStore = logStore ?? new InMemoryScheduledJobLogStore();
    private readonly ConcurrentDictionary<(string JobName, string TenantId), DateTimeOffset> _lastRuns = new();

    public IReadOnlyList<IScheduledJob> RegisteredJobs { get; } = jobs.ToList();

    public async Task<ScheduledJobRunEntry> RunJobNowAsync(string jobName, string tenantId, CancellationToken ct = default)
    {
        var job = RegisteredJobs.FirstOrDefault(j => j.Name == jobName)
            ?? throw new KeyNotFoundException($"Job '{jobName}' not found");

        using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.scheduler.job");
        activity?.SetTag("frogframe.job_name", job.Name);
        activity?.SetTag("frogframe.tenant", tenantId);
        activity?.SetTag("frogframe.trigger", "manual");

        var now = DateTimeOffset.UtcNow;
        var context = new ScheduledJobContext
        {
            TenantId = tenantId,
            SystemUser = UserContext.System(tenantId),
            Docs = documentService,
            Services = serviceProvider,
            ScheduledTime = now,
            Logger = _logger
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string status = "Success";
        string? error = null;

        try
        {
            _logger.LogJobStarting(job.Name, tenantId, now);
            await job.ExecuteAsync(context, ct).ConfigureAwait(false);
            _logger.LogJobCompleted(job.Name, tenantId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = "Failed";
            error = ex.Message;
            _logger.LogJobFailed(ex, job.Name, tenantId);
        }
        finally
        {
            sw.Stop();
        }

        FrogFrameTelemetry.ScheduledJobRuns.Add(1, new KeyValuePair<string, object?>("job", job.Name), new KeyValuePair<string, object?>("status", status));

        var entry = new ScheduledJobRunEntry
        {
            JobName = job.Name,
            TenantId = tenantId,
            StartedAt = now,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = (int)sw.ElapsedMilliseconds,
            Status = status,
            Error = error
        };

        try
        {
            await _logStore.RecordRunAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            _logger.LogJobFailed(logEx, job.Name, tenantId);
        }

        return entry;
    }

    public async Task<DateTimeOffset?> GetLastRunAsync(string jobName, string tenantId, CancellationToken ct = default)
    {
        if (_lastRuns.TryGetValue((jobName, tenantId), out var dt))
            return dt;

        var stored = await _logStore.GetLastRunAsync(jobName, tenantId, ct).ConfigureAwait(false);
        if (stored.HasValue)
            _lastRuns[(jobName, tenantId)] = stored.Value;

        return stored;
    }

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
                var lastRun = await GetLastRunAsync(job.Name, tenant, ct).ConfigureAwait(false);
                if (!CronSchedule.IsDue(job.Schedule, now, lastRun))
                    continue;

                var lockKey = $"frogframe:scheduler:{job.Name}:{tenant}";
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
                    using var activity = FrogFrameTelemetry.ActivitySource.StartActivity("frogframe.scheduler.job");
                    activity?.SetTag("frogframe.job_name", job.Name);
                    activity?.SetTag("frogframe.tenant", tenant);
                    activity?.SetTag("frogframe.trigger", "scheduled");

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

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string status = "Success";
                    string? error = null;

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
                        status = "Failed";
                        error = ex.Message;
                        _logger.LogJobFailed(ex, job.Name, tenant);
                    }
                    finally
                    {
                        sw.Stop();
                        FrogFrameTelemetry.ScheduledJobRuns.Add(1, new KeyValuePair<string, object?>("job", job.Name), new KeyValuePair<string, object?>("status", status));
                        var entry = new ScheduledJobRunEntry
                        {
                            JobName = job.Name,
                            TenantId = tenant,
                            StartedAt = now,
                            CompletedAt = DateTimeOffset.UtcNow,
                            DurationMs = (int)sw.ElapsedMilliseconds,
                            Status = status,
                            Error = error
                        };

                        try
                        {
                            await _logStore.RecordRunAsync(entry, ct).ConfigureAwait(false);
                        }
                        catch (Exception logEx) when (logEx is not OperationCanceledException)
                        {
                            _logger.LogLockFailed(logEx, job.Name, tenant);
                        }
                    }
                }
            }
        }

        return new ScheduledJobRunResult(evaluated, executed, skippedLocked, failed);
    }
}
