using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Framework.Cluster;

namespace Framework.Tenancy;

/// <summary>Status of a tenant. A suspended tenant cannot sign in, its sessions and API keys are refused and its scheduled jobs do not run.</summary>
public static class TenantStatus
{
    public const string Active = "Active";
    public const string Suspended = "Suspended";
}

/// <summary>Settings an administrator of the group sets per tenant. Every value is optional; empty means "installation default".</summary>
public sealed class TenantSettings
{
    /// <summary>UI language for users who have not chosen one ("id" or "en").</summary>
    public string? DefaultLanguage { get; set; }

    /// <summary>Maximum number of active user accounts (API service accounts included). Null = no limit.</summary>
    public int? MaxUsers { get; set; }

    /// <summary>Roles that must use two-factor authentication in this tenant, on top of <c>Security:TwoFactor:RequiredForRoles</c>.</summary>
    public List<string> TwoFactorRequiredRoles { get; set; } = [];

    /// <summary>Free text for the group administrators (contact, contract, notes).</summary>
    public string? Notes { get; set; }
}

/// <summary>One tenant (for a group of companies: one subsidiary). Data of all tenants lives in one database, separated by tenant id.</summary>
public sealed class Tenant
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string Status { get; set; } = TenantStatus.Active;
    public string? SuspendedReason { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public TenantSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public bool IsActive => Status == TenantStatus.Active;
}

/// <summary>Size of a tenant, for the tenant list.</summary>
public sealed record TenantUsage(string TenantId, int Users, int ActiveUsers, long Documents, DateTimeOffset? LastActivity);

public interface ITenantStore
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default);
    Task<Tenant?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Inserts or updates.</summary>
    Task SaveAsync(Tenant tenant, CancellationToken ct = default);

    /// <summary>Users, active users, documents and last change per tenant id (tenants without a registry row included).</summary>
    Task<IReadOnlyList<TenantUsage>> UsageAsync(CancellationToken ct = default);
}

public static class TenantIds
{
    private static readonly Regex Pattern = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant);

    /// <summary>2–32 characters: lower-case letters, digits and hyphens, not starting with a hyphen. The id is permanent.</summary>
    public static bool IsValid(string? id) => id is not null && Pattern.IsMatch(id);
}

public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new(StringComparer.Ordinal);
    private readonly Security.IUserAccountRepository? _users;

    public InMemoryTenantStore(Security.IUserAccountRepository? users = null) => _users = users;

    public Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Tenant>>(_tenants.Values.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(Copy).ToList());

    public Task<Tenant?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_tenants.TryGetValue(id, out var t) ? Copy(t) : null);

    public Task SaveAsync(Tenant tenant, CancellationToken ct = default)
    {
        _tenants[tenant.Id] = Copy(tenant);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TenantUsage>> UsageAsync(CancellationToken ct = default)
    {
        var result = new List<TenantUsage>();
        foreach (var t in _tenants.Keys)
        {
            var users = _users is null ? [] : await _users.ListAsync(t, ct);
            result.Add(new TenantUsage(t, users.Count, users.Count(u => u.IsActive), 0, null));
        }
        return result;
    }

    private static Tenant Copy(Tenant t) => new()
    {
        Id = t.Id, Name = t.Name, Status = t.Status, SuspendedReason = t.SuspendedReason, SuspendedAt = t.SuspendedAt,
        CreatedAt = t.CreatedAt, UpdatedAt = t.UpdatedAt, UpdatedBy = t.UpdatedBy,
        Settings = new TenantSettings
        {
            DefaultLanguage = t.Settings.DefaultLanguage, MaxUsers = t.Settings.MaxUsers, Notes = t.Settings.Notes,
            TwoFactorRequiredRoles = [.. t.Settings.TwoFactorRequiredRoles],
        },
    };
}

/// <summary>
/// Cached view of the tenant registry for checks on every request (status, settings). Changes made on this node take
/// effect at once; other nodes are told through the cluster bus and otherwise refresh within <see cref="CacheTtl"/>.
/// A tenant id without a registry row (data from before the registry) counts as active with default settings.
/// </summary>
public sealed class TenantDirectory : IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private const string Topic = "tenants.changed";

    private readonly ITenantStore _store;
    private readonly TimeProvider _clock;
    private readonly IClusterBus? _bus;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, (Tenant? Tenant, DateTimeOffset LoadedAt)> _cache = new(StringComparer.Ordinal);

    public TenantDirectory(ITenantStore store, TimeProvider clock, IClusterBus? bus = null)
    {
        _store = store;
        _clock = clock;
        _bus = bus;
        _subscription = bus?.Subscribe(Topic, id => _cache.TryRemove(id, out _));
    }

    public async Task<Tenant?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(tenantId, out var hit) && now - hit.LoadedAt < CacheTtl) return hit.Tenant;
        var tenant = await _store.GetAsync(tenantId, ct);
        _cache[tenantId] = (tenant, now);
        return tenant;
    }

    public async Task<bool> IsActiveAsync(string tenantId, CancellationToken ct = default) =>
        await GetAsync(tenantId, ct) is not { IsActive: false };

    public async Task<TenantSettings> SettingsAsync(string tenantId, CancellationToken ct = default) =>
        (await GetAsync(tenantId, ct))?.Settings ?? new TenantSettings();

    /// <summary>Saves a tenant and makes every node re-read it.</summary>
    public async Task SaveAsync(Tenant tenant, CancellationToken ct = default)
    {
        await _store.SaveAsync(tenant, ct);
        _cache.TryRemove(tenant.Id, out _);
        _bus?.Publish(Topic, tenant.Id);
    }

    public void Dispose() => _subscription?.Dispose();
}
