using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Imports;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.Logging;

namespace Framework.Tasks;

/// <summary>Wakes the background worker when a task is queued (instead of waiting for its next poll).</summary>
public sealed class BackgroundTaskSignal
{
    private readonly SemaphoreSlim signal = new(0, int.MaxValue);
    public void Wake() => signal.Release();
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await signal.WaitAsync(timeout, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

/// <summary>
/// Queue long-running operations for the current user ("enqueue") and follow them: list, progress, cancel, output,
/// run again. Users see their own tasks; administrators can see all tasks of the tenant.
/// </summary>
public sealed class BackgroundTaskService(
    IBackgroundTaskStore store,
    IEnumerable<IBackgroundTaskHandler> handlers,
    TimeProvider clock,
    BackgroundTaskSignal? signal = null)
{
    private readonly HashSet<string> kinds = handlers.Select(h => h.Kind).ToHashSet(StringComparer.Ordinal);

    public async Task<BackgroundTask> EnqueueAsync(UserContext user, string kind, string title, string payload = "{}", byte[]? input = null, CancellationToken ct = default)
    {
        if (!kinds.Contains(kind)) throw new InvalidPayloadException($"Unknown background task '{kind}'.");
        if (input is { Length: > BackgroundTaskLimits.MaxInputBytes })
            throw new InvalidPayloadException($"The input is larger than {BackgroundTaskLimits.MaxInputBytes / 1024 / 1024} MB.");
        if (await store.CountActiveAsync(user.TenantId, user.Id, ct) >= BackgroundTaskLimits.MaxActivePerUser)
            throw new InvalidPayloadException($"You already have {BackgroundTaskLimits.MaxActivePerUser} background tasks queued or running. Wait for one to finish.");
        var task = new BackgroundTask
        {
            TenantId = user.TenantId, UserId = user.Id, Kind = kind, Title = title.Length > 200 ? title[..200] : title,
            Payload = payload, Input = input, CreatedAt = clock.GetUtcNow(),
        };
        await store.AddAsync(task, ct);
        signal?.Wake();
        return task with { Input = null };
    }

    /// <summary>The user's tasks, newest first; all tasks of the tenant for an administrator who asks for them.</summary>
    public Task<IReadOnlyList<BackgroundTask>> ListAsync(UserContext user, bool allUsers = false, int take = 100, CancellationToken ct = default) =>
        store.ListAsync(user.TenantId, allUsers && user.IsAdmin ? null : user.Id, Math.Clamp(take, 1, 500), ct);

    public async Task<BackgroundTask> GetAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        var task = await store.GetAsync(user.TenantId, id, ct);
        if (task is null || (!user.IsAdmin && !string.Equals(task.UserId, user.Id, StringComparison.OrdinalIgnoreCase)))
            throw new DocumentNotFoundException("Background task", id.ToString());
        return task;
    }

    public async Task<(byte[] Content, string Name)> OutputAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        await GetAsync(user, id, ct);
        return await store.OutputAsync(user.TenantId, id, ct) ?? throw new DocumentNotFoundException("Output of background task", id.ToString());
    }

    /// <summary>Cancels a queued task at once; asks a running one to stop at its next progress report.</summary>
    public async Task<bool> CancelAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        await GetAsync(user, id, ct);
        return await store.RequestCancelAsync(user.TenantId, id, clock.GetUtcNow(), ct);
    }

    /// <summary>Queues a finished task again with the same parameters and input, as the current user.</summary>
    public async Task<BackgroundTask> RunAgainAsync(UserContext user, Guid id, CancellationToken ct = default)
    {
        var task = await GetAsync(user, id, ct);
        if (!BackgroundTaskStatus.IsFinished(task.Status)) throw new InvalidStateException("The task has not finished yet.");
        var full = await store.GetWithInputAsync(id, ct) ?? throw new DocumentNotFoundException("Background task", id.ToString());
        return await EnqueueAsync(user, full.Kind, full.Title, full.Payload, full.Input, ct);
    }
}

/// <summary>
/// Runs queued tasks (used by the hosted worker). Claims a task, runs its handler as the user who queued it (with that
/// user's current roles), saves progress at most about once a second, renews the lease every 20 seconds, stops on
/// cancel, and notifies the user when the task ends. Safe with several application instances (claim with
/// FOR UPDATE SKIP LOCKED in PostgreSQL).
/// </summary>
public sealed class BackgroundTaskRunner(
    IBackgroundTaskStore store,
    IEnumerable<IBackgroundTaskHandler> handlers,
    IUserDirectory users,
    INotificationStore inbox,
    TimeProvider clock,
    ILogger<BackgroundTaskRunner>? log = null,
    INotificationLiveFeed? liveFeed = null)
{
    private readonly Dictionary<string, IBackgroundTaskHandler> byKind = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);
    /// <summary>Identifies this worker in the task's lease (machine name and a random part).</summary>
    public string WorkerId { get; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    /// <summary>Runs the next queued task; false when there was none.</summary>
    public async Task<bool> RunNextAsync(CancellationToken stopping = default)
    {
        var task = await store.ClaimAsync(WorkerId, clock.GetUtcNow(), stopping);
        if (task is null) return false;
        await RunAsync(task, stopping);
        return true;
    }

    private async Task RunAsync(BackgroundTask task, CancellationToken stopping)
    {
        string status, message;
        string? result = null, error = null;
        byte[]? output = null; string? outputName = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var cancelRequested = false;
        var lastProgress = 0; string? lastMessage = null;
        var lastSaved = DateTimeOffset.MinValue;
        var gate = new SemaphoreSlim(1, 1);

        async Task Save(bool force, CancellationToken ct)
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                var now = clock.GetUtcNow();
                if (!force && now - lastSaved < TimeSpan.FromSeconds(1)) return;
                lastSaved = now;
                if (await store.ProgressAsync(task.Id, WorkerId, lastProgress, lastMessage, now + BackgroundTaskLimits.Lease, CancellationToken.None))
                {
                    cancelRequested = true;
                    cts.Cancel();
                }
            }
            finally { gate.Release(); }
        }

        async Task Report(int percent, string? msg, CancellationToken ct)
        {
            lastProgress = percent; lastMessage = msg;
            await Save(force: false, ct);
            cts.Token.ThrowIfCancellationRequested();
        }

        // Lease and cancel check even when the handler reports rarely.
        var heartbeat = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(Heartbeat, cts.Token); } catch (OperationCanceledException) { return; }
                try { await Save(force: true, CancellationToken.None); } catch (Exception ex) { log?.LogWarning(ex, "Heartbeat of background task {Id} failed", task.Id); }
            }
        });

        try
        {
            if (!byKind.TryGetValue(task.Kind, out var handler))
                throw new InvalidStateException($"No handler is registered for background task '{task.Kind}'.");
            var user = await users.FindAsync(task.UserId, stopping);
            if (user is null || user.TenantId != task.TenantId)
                throw new PermissionDeniedException("The user who queued this task no longer exists.");
            var full = await store.GetWithInputAsync(task.Id, stopping) ?? task;
            var outcome = await handler.RunAsync(new BackgroundTaskContext(full, user, Report), cts.Token);
            (status, result, output, outputName) = (BackgroundTaskStatus.Succeeded, outcome.Summary, outcome.Output, outcome.OutputName);
            message = outcome.Summary;
        }
        catch (OperationCanceledException) when (cancelRequested)
        {
            (status, error) = (BackgroundTaskStatus.Cancelled, $"Cancelled at {lastProgress}%.");
            message = error;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            (status, error) = (BackgroundTaskStatus.Interrupted, "The server stopped while this task was running.");
            message = error;
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            (status, error) = (BackgroundTaskStatus.Failed, ex.Message);
            message = ex.Message;
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Background task {Id} ({Kind}) failed", task.Id, task.Kind);
            (status, error) = (BackgroundTaskStatus.Failed, "An unexpected error occurred. The administrator can find the details in the error log.");
            message = error;
        }
        finally
        {
            await cts.CancelAsync();
            await heartbeat;
        }

        await store.FinishAsync(task.Id, status, result, error, output, outputName, clock.GetUtcNow(), CancellationToken.None);
        await NotifyAsync(task, status, message);
    }

    /// <summary>Marks tasks of stopped workers as Interrupted (and tells their users), and deletes old finished tasks.</summary>
    public async Task MaintainAsync(CancellationToken ct = default)
    {
        foreach (var t in await store.InterruptExpiredAsync(clock.GetUtcNow(), ct))
            await NotifyAsync(t, BackgroundTaskStatus.Interrupted, "The server stopped while this task was running. Check the results before running it again.");
        await store.PruneAsync(clock.GetUtcNow() - BackgroundTaskLimits.Retention, ct);
    }

    private async Task NotifyAsync(BackgroundTask task, string status, string message)
    {
        var verb = status switch
        {
            BackgroundTaskStatus.Succeeded => "finished",
            BackgroundTaskStatus.Cancelled => "was cancelled",
            BackgroundTaskStatus.Interrupted => "was interrupted",
            _ => "failed",
        };
        var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{task.TenantId}|task|{task.Id}|{status}")));
        var n = new Notification(id, task.TenantId, task.UserId, $"{task.Title} {verb}", message, null, null, clock.GetUtcNow());
        inbox.Add(n);
        if (liveFeed is not null)
        {
            try { await liveFeed.PublishAsync(n, CancellationToken.None); } catch (Exception ex) { log?.LogWarning(ex, "Live notification failed"); }
        }
    }

    private static bool IsUserError(Exception ex) => ex is PermissionDeniedException or InvalidPayloadException or InvalidStateException
        or ValidationFailedException or DocumentNotFoundException or Framework.Reports.TemplateException;
}
