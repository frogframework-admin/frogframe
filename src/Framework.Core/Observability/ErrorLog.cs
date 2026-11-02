using System.Collections.Concurrent;

namespace Framework.Observability;

public sealed record ErrorLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TenantId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string? UserId { get; init; }
    public string ErrorType { get; init; } = "";
    public string Message { get; init; } = "";
    public string? StackTrace { get; init; }
    public bool Seen { get; init; }
}

public interface IErrorLogStore
{
    Task LogAsync(ErrorLogEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<ErrorLogEntry>> GetRecentAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default);
    Task MarkSeenAsync(Guid id, CancellationToken ct = default);
    Task ClearAsync(string? tenantId = null, CancellationToken ct = default);
}

public sealed class InMemoryErrorLogStore : IErrorLogStore
{
    private readonly ConcurrentDictionary<Guid, ErrorLogEntry> _items = new();

    public Task LogAsync(ErrorLogEntry entry, CancellationToken ct = default)
    {
        _items.TryAdd(entry.Id, entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ErrorLogEntry>> GetRecentAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default)
    {
        var result = _items.Values
            .Where(e => tenantId is null || e.TenantId == tenantId)
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<ErrorLogEntry>>(result);
    }

    public Task MarkSeenAsync(Guid id, CancellationToken ct = default)
    {
        if (_items.TryGetValue(id, out var entry))
        {
            _items[id] = entry with { Seen = true };
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? tenantId = null, CancellationToken ct = default)
    {
        var keysToRemove = _items.Values
            .Where(e => tenantId is null || e.TenantId == tenantId)
            .Select(e => e.Id)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _items.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}

