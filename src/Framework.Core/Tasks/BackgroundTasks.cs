using System.Collections.Concurrent;
using Framework.Security;

namespace Framework.Tasks;

public static class BackgroundTaskStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    /// <summary>The server stopped while the task ran. Not retried automatically: the work may be half done.</summary>
    public const string Interrupted = "Interrupted";

    public static bool IsFinished(string status) => status is Succeeded or Failed or Cancelled or Interrupted;
}

/// <summary>A long-running operation started by a user, run by the background worker (table frogframe_background_tasks).</summary>
public sealed record BackgroundTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    /// <summary>Handler that runs it (<see cref="IBackgroundTaskHandler.Kind"/>).</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    /// <summary>Handler-specific parameters (JSON).</summary>
    public string Payload { get; init; } = "{}";
    /// <summary>Optional binary input, e.g. an uploaded file. Kept until the task is pruned, for "Run again".</summary>
    public byte[]? Input { get; init; }
    public string Status { get; init; } = BackgroundTaskStatus.Queued;
    /// <summary>0–100.</summary>
    public int Progress { get; init; }
    public string? ProgressMessage { get; init; }
    /// <summary>Summary shown to the user when finished.</summary>
    public string? Result { get; init; }
    /// <summary>Optional file produced by the task (e.g. an import failure report).</summary>
    public byte[]? Output { get; init; }
    public string? OutputName { get; init; }
    public string? Error { get; init; }
    public bool CancelRequested { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    /// <summary>Worker lease: a Running task whose lease expired was interrupted.</summary>
    public DateTimeOffset? LockedUntil { get; init; }
    public string? LockedBy { get; init; }
}

/// <summary>Outcome of a handler: summary text and an optional output file.</summary>
public sealed record BackgroundTaskOutcome(string Summary, byte[]? Output = null, string? OutputName = null);

/// <summary>What a running handler gets: the task, the user (with their current roles) and progress reporting.</summary>
public sealed class BackgroundTaskContext(BackgroundTask task, UserContext user, Func<int, string?, CancellationToken, Task> report)
{
    public BackgroundTask Task { get; } = task;
    public UserContext User { get; } = user;

    /// <summary>
    /// Reports progress (0–100) with an optional message. Saved at most about once a second; throws
    /// <see cref="OperationCanceledException"/> when the user cancelled the task.
    /// </summary>
    public Task ReportAsync(int percent, string? message, CancellationToken ct) => report(Math.Clamp(percent, 0, 100), message, ct);
}

/// <summary>
/// Runs one kind of background task. Register with <c>services.AddSingleton&lt;IBackgroundTaskHandler, MyHandler&gt;()</c>
/// and start it with <see cref="BackgroundTaskService.EnqueueAsync"/>. Handlers must check permissions themselves (the
/// user may have lost a role since the task was queued) and should report progress regularly: cancellation and the
/// worker's lease depend on it.
/// </summary>
public interface IBackgroundTaskHandler
{
    string Kind { get; }
    Task<BackgroundTaskOutcome> RunAsync(BackgroundTaskContext context, CancellationToken ct);
}

public static class BackgroundTaskLimits
{
    /// <summary>Queued or running tasks per user.</summary>
    public const int MaxActivePerUser = 5;
    /// <summary>Input size (e.g. an import file).</summary>
    public const int MaxInputBytes = 20 * 1024 * 1024;
    /// <summary>Worker lease; renewed by progress reports.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    /// <summary>Finished tasks are deleted after this.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
}

/// <summary>Storage of background tasks (table frogframe_background_tasks in PostgreSQL).</summary>
public interface IBackgroundTaskStore
{
    Task AddAsync(BackgroundTask task, CancellationToken ct = default);
    /// <summary>The task without its input and output bytes (use <see cref="OutputAsync"/>).</summary>
    Task<BackgroundTask?> GetAsync(string tenantId, Guid id, CancellationToken ct = default);
    Task<BackgroundTask?> GetWithInputAsync(Guid id, CancellationToken ct = default);
    Task<(byte[] Content, string Name)?> OutputAsync(string tenantId, Guid id, CancellationToken ct = default);
    /// <summary>Newest first, without input/output bytes; all users of the tenant when <paramref name="userId"/> is null.</summary>
    Task<IReadOnlyList<BackgroundTask>> ListAsync(string tenantId, string? userId, int take, CancellationToken ct = default);
    Task<int> CountActiveAsync(string tenantId, string userId, CancellationToken ct = default);
    /// <summary>Claims the oldest queued task for a worker (Running, with a lease); null when none.</summary>
    Task<BackgroundTask?> ClaimAsync(string workerId, DateTimeOffset now, CancellationToken ct = default);
    /// <summary>Saves progress and renews the lease; returns whether a cancel was requested.</summary>
    Task<bool> ProgressAsync(Guid id, string workerId, int progress, string? message, DateTimeOffset leaseUntil, CancellationToken ct = default);
    Task FinishAsync(Guid id, string status, string? result, string? error, byte[]? output, string? outputName, DateTimeOffset at, CancellationToken ct = default);
    /// <summary>Queued: cancelled at once (true). Running: flagged for the worker. Finished: false.</summary>
    Task<bool> RequestCancelAsync(string tenantId, Guid id, DateTimeOffset at, CancellationToken ct = default);
    /// <summary>Running tasks whose lease expired become Interrupted; returns them (for notifications).</summary>
    Task<IReadOnlyList<BackgroundTask>> InterruptExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
    Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken ct = default);
}

public sealed class InMemoryBackgroundTaskStore : IBackgroundTaskStore
{
    private readonly ConcurrentDictionary<Guid, BackgroundTask> tasks = new();
    private readonly Lock gate = new();

    private static BackgroundTask Light(BackgroundTask t) => t with { Input = null, Output = null };

    public Task AddAsync(BackgroundTask task, CancellationToken ct = default) { tasks[task.Id] = task; return Task.CompletedTask; }

    public Task<BackgroundTask?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(tasks.TryGetValue(id, out var t) && t.TenantId == tenantId ? Light(t) : null);

    public Task<BackgroundTask?> GetWithInputAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(tasks.TryGetValue(id, out var t) ? t with { Output = null } : null);

    public Task<(byte[] Content, string Name)?> OutputAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        Task.FromResult(tasks.TryGetValue(id, out var t) && t.TenantId == tenantId && t.Output is { } o ? (o, t.OutputName ?? "output") : ((byte[], string)?)null);

    public Task<IReadOnlyList<BackgroundTask>> ListAsync(string tenantId, string? userId, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackgroundTask>>(tasks.Values
            .Where(t => t.TenantId == tenantId && (userId is null || string.Equals(t.UserId, userId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.CreatedAt).Take(take).Select(Light).ToList());

    public Task<int> CountActiveAsync(string tenantId, string userId, CancellationToken ct = default) =>
        Task.FromResult(tasks.Values.Count(t => t.TenantId == tenantId && string.Equals(t.UserId, userId, StringComparison.OrdinalIgnoreCase)
                                                && !BackgroundTaskStatus.IsFinished(t.Status)));

    public Task<BackgroundTask?> ClaimAsync(string workerId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (gate)
        {
            var next = tasks.Values.Where(t => t.Status == BackgroundTaskStatus.Queued).OrderBy(t => t.CreatedAt).FirstOrDefault();
            if (next is null) return Task.FromResult<BackgroundTask?>(null);
            var claimed = next with { Status = BackgroundTaskStatus.Running, StartedAt = now, LockedBy = workerId, LockedUntil = now + BackgroundTaskLimits.Lease };
            tasks[next.Id] = claimed;
            return Task.FromResult<BackgroundTask?>(Light(claimed));
        }
    }

    public Task<bool> ProgressAsync(Guid id, string workerId, int progress, string? message, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(id, out var t) || t.LockedBy != workerId) return Task.FromResult(false);
            tasks[id] = t with { Progress = progress, ProgressMessage = message, LockedUntil = leaseUntil };
            return Task.FromResult(t.CancelRequested);
        }
    }

    public Task FinishAsync(Guid id, string status, string? result, string? error, byte[]? output, string? outputName, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (tasks.TryGetValue(id, out var t))
                tasks[id] = t with
                {
                    Status = status, Result = result, Error = error, Output = output, OutputName = outputName, FinishedAt = at,
                    Progress = status == BackgroundTaskStatus.Succeeded ? 100 : t.Progress, LockedUntil = null,
                };
        }
        return Task.CompletedTask;
    }

    public Task<bool> RequestCancelAsync(string tenantId, Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!tasks.TryGetValue(id, out var t) || t.TenantId != tenantId || BackgroundTaskStatus.IsFinished(t.Status)) return Task.FromResult(false);
            tasks[id] = t.Status == BackgroundTaskStatus.Queued
                ? t with { Status = BackgroundTaskStatus.Cancelled, FinishedAt = at, CancelRequested = true }
                : t with { CancelRequested = true };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<BackgroundTask>> InterruptExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (gate)
        {
            var expired = tasks.Values.Where(t => t.Status == BackgroundTaskStatus.Running && t.LockedUntil < now).ToList();
            foreach (var t in expired)
                tasks[t.Id] = t with { Status = BackgroundTaskStatus.Interrupted, FinishedAt = now, LockedUntil = null,
                                       Error = "The server stopped while this task was running." };
            return Task.FromResult<IReadOnlyList<BackgroundTask>>(expired.Select(Light).ToList());
        }
    }

    public Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken ct = default)
    {
        var old = tasks.Values.Where(t => t.FinishedAt < finishedBefore).Select(t => t.Id).ToList();
        return Task.FromResult(old.Count(id => tasks.TryRemove(id, out _)));
    }
}
