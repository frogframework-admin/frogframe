using System.Collections.Concurrent;
using Framework.Scripting.Abstractions;

namespace Framework.Scripting;

public record ServerScriptDef
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public ServerScriptType ScriptType { get; init; } = ServerScriptType.DocEvent;
    public string? DocType { get; init; }
    public DocEventStage? DocEvent { get; init; }
    public string? ApiMethod { get; init; }
    public string? CronExpr { get; init; }
    public string? RunAsUserId { get; init; }
    /// <summary>
    /// Roles allowed to call an Api script. Empty = only System Manager / Administrator.
    /// Ignored for DocEvent and Scheduled scripts.
    /// </summary>
    public IReadOnlyList<string> AllowedRoles { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public int Version { get; init; } = 1;
    public string CurrentCode { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record ServerScriptVersion
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string ScriptId { get; init; }
    public int Version { get; init; } = 1;
    public required string Code { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public record ServerScriptRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string TenantId { get; init; }
    public required string ScriptId { get; init; }
    public int Version { get; init; } = 1;
    public required string TriggerType { get; init; }
    public string? DocName { get; init; }
    public required string Status { get; init; } // Success, Failed, BudgetExceeded, Timeout, SecurityViolation
    public int DurationMs { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IServerScriptStore
{
    Task<ServerScriptDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<ServerScriptDef>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ServerScriptDef>> ListDocEventScriptsAsync(string tenantId, string docType, DocEventStage docEvent, CancellationToken ct = default);
    Task<ServerScriptDef?> GetApiScriptAsync(string tenantId, string apiMethod, CancellationToken ct = default);
    Task<IReadOnlyList<ServerScriptDef>> ListScheduledScriptsAsync(string tenantId, CancellationToken ct = default);
    Task SaveAsync(ServerScriptDef def, string code, string userId, CancellationToken ct = default);
    Task SetActiveAsync(string tenantId, string id, bool active, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
    Task RecordRunAsync(ServerScriptRun run, CancellationToken ct = default);
    Task<IReadOnlyList<ServerScriptRun>> ListRunsAsync(string tenantId, string scriptId, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<ServerScriptVersion>> ListVersionsAsync(string tenantId, string scriptId, CancellationToken ct = default);
}

public sealed class InMemoryServerScriptStore : IServerScriptStore
{
    private readonly ConcurrentDictionary<string, ServerScriptDef> _scripts = new();
    private readonly List<ServerScriptVersion> _versions = [];
    private readonly List<ServerScriptRun> _runs = [];
    private readonly object _lock = new();

    public Task<ServerScriptDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _scripts.TryGetValue($"{tenantId}:{id}", out var def);
        return Task.FromResult(def);
    }

    public Task<IReadOnlyList<ServerScriptDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        var list = _scripts.Values
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.ModifiedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<ServerScriptDef>>(list);
    }

    public Task<IReadOnlyList<ServerScriptDef>> ListDocEventScriptsAsync(string tenantId, string docType, DocEventStage docEvent, CancellationToken ct = default)
    {
        var list = _scripts.Values
            .Where(s => s.TenantId == tenantId &&
                        s.IsActive &&
                        s.ScriptType == ServerScriptType.DocEvent &&
                        string.Equals(s.DocType, docType, StringComparison.OrdinalIgnoreCase) &&
                        s.DocEvent == docEvent)
            .ToList();
        return Task.FromResult<IReadOnlyList<ServerScriptDef>>(list);
    }

    public Task<ServerScriptDef?> GetApiScriptAsync(string tenantId, string apiMethod, CancellationToken ct = default)
    {
        var script = _scripts.Values
            .FirstOrDefault(s => s.TenantId == tenantId &&
                                 s.IsActive &&
                                 s.ScriptType == ServerScriptType.Api &&
                                 string.Equals(s.ApiMethod, apiMethod, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(script);
    }

    public Task<IReadOnlyList<ServerScriptDef>> ListScheduledScriptsAsync(string tenantId, CancellationToken ct = default)
    {
        var list = _scripts.Values
            .Where(s => s.TenantId == tenantId &&
                        s.IsActive &&
                        s.ScriptType == ServerScriptType.Scheduled &&
                        !string.IsNullOrWhiteSpace(s.CronExpr))
            .ToList();
        return Task.FromResult<IReadOnlyList<ServerScriptDef>>(list);
    }

    public Task SaveAsync(ServerScriptDef def, string code, string userId, CancellationToken ct = default)
    {
        var key = $"{def.TenantId}:{def.Id}";
        lock (_lock)
        {
            var version = def.Version;
            if (_scripts.TryGetValue(key, out var existing))
            {
                if (existing.CurrentCode != code)
                {
                    version = existing.Version + 1;
                }
            }

            var updated = def with
            {
                Version = version,
                CurrentCode = code,
                ModifiedAt = DateTimeOffset.UtcNow
            };

            _scripts[key] = updated;

            _versions.Add(new ServerScriptVersion
            {
                Id = $"{def.Id}_v{version}",
                TenantId = def.TenantId,
                ScriptId = def.Id,
                Version = version,
                Code = code,
                CreatedBy = userId,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        return Task.CompletedTask;
    }

    public Task SetActiveAsync(string tenantId, string id, bool active, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{id}";
        if (_scripts.TryGetValue(key, out var existing))
        {
            _scripts[key] = existing with { IsActive = active, ModifiedAt = DateTimeOffset.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var key = $"{tenantId}:{id}";
        _scripts.TryRemove(key, out _);
        lock (_lock)
        {
            _versions.RemoveAll(v => v.TenantId == tenantId && v.ScriptId == id);
            _runs.RemoveAll(r => r.TenantId == tenantId && r.ScriptId == id);
        }
        return Task.CompletedTask;
    }

    public Task RecordRunAsync(ServerScriptRun run, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _runs.Add(run);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ServerScriptRun>> ListRunsAsync(string tenantId, string scriptId, int limit = 50, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _runs
                .Where(r => r.TenantId == tenantId && r.ScriptId == scriptId)
                .OrderByDescending(r => r.ExecutedAt)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<ServerScriptRun>>(list);
        }
    }

    public Task<IReadOnlyList<ServerScriptVersion>> ListVersionsAsync(string tenantId, string scriptId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _versions
                .Where(v => v.TenantId == tenantId && v.ScriptId == scriptId)
                .OrderByDescending(v => v.Version)
                .ToList();
            return Task.FromResult<IReadOnlyList<ServerScriptVersion>>(list);
        }
    }
}
