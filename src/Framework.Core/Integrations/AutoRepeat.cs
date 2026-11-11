using System.Collections.Concurrent;

namespace Framework.Integrations;

public enum AutoRepeatFrequency
{
    Daily,
    Weekly,
    Monthly,
    Quarterly,
    Yearly
}

public enum AutoRepeatStatus
{
    Active,
    Paused,
    Completed
}

public sealed record AutoRepeatDef
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required string ReferenceDocType { get; init; }
    public required string ReferenceDocName { get; init; }
    public required AutoRepeatFrequency Frequency { get; init; }
    public required DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public required DateOnly NextScheduleDate { get; init; }
    public required AutoRepeatStatus Status { get; init; }
    public required string RunAsUserId { get; init; }
    public bool NotifyByEmail { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public bool SubmitOnCreation { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record AutoRepeatRun
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string AutoRepeatId { get; init; }
    public required DateOnly ScheduledFor { get; init; }
    public required string GeneratedDocName { get; init; }
    public required string Status { get; init; } // "Success" or "Failed"
    public string? Error { get; init; }
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Deterministic pure schedule calculator for recurring document frequencies.
/// </summary>
public static class AutoRepeatSchedule
{
    public static DateOnly Next(DateOnly current, AutoRepeatFrequency frequency) => frequency switch
    {
        AutoRepeatFrequency.Daily => current.AddDays(1),
        AutoRepeatFrequency.Weekly => current.AddDays(7),
        AutoRepeatFrequency.Monthly => current.AddMonths(1),
        AutoRepeatFrequency.Quarterly => current.AddMonths(3),
        AutoRepeatFrequency.Yearly => current.AddYears(1),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unsupported recurrence frequency")
    };

    public static bool IsCompleted(AutoRepeatDef def, DateOnly nextDate) =>
        def.EndDate.HasValue && nextDate > def.EndDate.Value;
}

public interface IAutoRepeatStore
{
    Task<IReadOnlyList<AutoRepeatDef>> ListAsync(string tenantId, string? docType = null, AutoRepeatStatus? status = null, CancellationToken ct = default);
    Task<AutoRepeatDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<AutoRepeatDef?> GetByReferenceAsync(string tenantId, string docType, string docName, CancellationToken ct = default);
    Task SaveAsync(AutoRepeatDef def, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<AutoRepeatDef>> ListDueAsync(string tenantId, DateOnly today, CancellationToken ct = default);
    Task<bool> RecordRunAsync(AutoRepeatRun run, CancellationToken ct = default);
    Task<IReadOnlyList<AutoRepeatRun>> ListRunsAsync(string tenantId, string autoRepeatId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Claims one schedule period before its document is created. Returns true when this caller now owns the
    /// period: no run existed yet, or the previous run for that period failed. Returns false when the period is
    /// already done or being processed, so the document is never created twice.
    /// </summary>
    Task<bool> TryClaimRunAsync(string tenantId, string autoRepeatId, DateOnly scheduledFor, CancellationToken ct = default);

    /// <summary>Marks a claimed period as finished (Success or Failed).</summary>
    Task CompleteRunAsync(AutoRepeatRun run, CancellationToken ct = default);
}

/// <summary>Run status values used by the claim-first protocol.</summary>
public static class AutoRepeatRunStatus
{
    public const string Claimed = "Claimed";
    public const string Success = "Success";
    public const string Failed = "Failed";
}

public sealed class InMemoryAutoRepeatStore : IAutoRepeatStore
{
    private readonly ConcurrentDictionary<(string TenantId, string Id), AutoRepeatDef> _repeats = new();
    private readonly ConcurrentDictionary<(string TenantId, string AutoRepeatId, DateOnly ScheduledFor), AutoRepeatRun> _runs = new();

    public Task<IReadOnlyList<AutoRepeatDef>> ListAsync(string tenantId, string? docType = null, AutoRepeatStatus? status = null, CancellationToken ct = default)
    {
        var items = _repeats.Values
            .Where(r => r.TenantId == tenantId)
            .Where(r => string.IsNullOrEmpty(docType) || string.Equals(r.ReferenceDocType, docType, StringComparison.OrdinalIgnoreCase))
            .Where(r => !status.HasValue || r.Status == status.Value)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<AutoRepeatDef>>(items);
    }

    public Task<AutoRepeatDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _repeats.TryGetValue((tenantId, id), out var def);
        return Task.FromResult(def);
    }

    public Task<AutoRepeatDef?> GetByReferenceAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        var item = _repeats.Values.FirstOrDefault(r =>
            r.TenantId == tenantId &&
            string.Equals(r.ReferenceDocType, docType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.ReferenceDocName, docName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(item);
    }

    public Task SaveAsync(AutoRepeatDef def, CancellationToken ct = default)
    {
        _repeats[(def.TenantId, def.Id)] = def;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _repeats.TryRemove((tenantId, id), out _);
        var runKeys = _runs.Keys.Where(k => k.TenantId == tenantId && k.AutoRepeatId == id).ToList();
        foreach (var k in runKeys)
        {
            _runs.TryRemove(k, out _);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutoRepeatDef>> ListDueAsync(string tenantId, DateOnly today, CancellationToken ct = default)
    {
        var due = _repeats.Values
            .Where(r => r.TenantId == tenantId && r.Status == AutoRepeatStatus.Active && r.NextScheduleDate <= today)
            .OrderBy(r => r.NextScheduleDate)
            .ToList();

        return Task.FromResult<IReadOnlyList<AutoRepeatDef>>(due);
    }

    public Task<bool> RecordRunAsync(AutoRepeatRun run, CancellationToken ct = default)
    {
        var key = (run.TenantId, run.AutoRepeatId, run.ScheduledFor);
        var added = _runs.TryAdd(key, run);
        return Task.FromResult(added);
    }

    public Task<IReadOnlyList<AutoRepeatRun>> ListRunsAsync(string tenantId, string autoRepeatId, int limit = 50, CancellationToken ct = default)
    {
        var runs = _runs.Values
            .Where(r => r.TenantId == tenantId && r.AutoRepeatId == autoRepeatId)
            .OrderByDescending(r => r.ExecutedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<AutoRepeatRun>>(runs);
    }

    private readonly object _claimGate = new();

    public Task<bool> TryClaimRunAsync(string tenantId, string autoRepeatId, DateOnly scheduledFor, CancellationToken ct = default)
    {
        var key = (tenantId, autoRepeatId, scheduledFor);
        var claim = new AutoRepeatRun
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AutoRepeatId = autoRepeatId,
            ScheduledFor = scheduledFor,
            GeneratedDocName = string.Empty,
            Status = AutoRepeatRunStatus.Claimed,
            ExecutedAt = DateTimeOffset.UtcNow
        };

        lock (_claimGate)
        {
            if (!_runs.TryGetValue(key, out var existing))
            {
                _runs[key] = claim;
                return Task.FromResult(true);
            }

            if (existing.Status == AutoRepeatRunStatus.Failed)
            {
                _runs[key] = claim with { Id = existing.Id };
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task CompleteRunAsync(AutoRepeatRun run, CancellationToken ct = default)
    {
        var key = (run.TenantId, run.AutoRepeatId, run.ScheduledFor);
        lock (_claimGate)
        {
            var id = _runs.TryGetValue(key, out var existing) ? existing.Id : run.Id;
            _runs[key] = run with { Id = id };
        }
        return Task.CompletedTask;
    }
}

