using System.Collections.Concurrent;
using System.Text.Json;

namespace Framework.Preferences;

/// <summary>
/// Konfigurasi sebuah kolom pada DataGrid untuk seorang pengguna.
/// </summary>
public sealed record GridColumnPreference
{
    public bool Visible { get; init; } = true;
    public string? Width { get; init; }
    public int? Order { get; init; }
}

/// <summary>
/// Preferensi tata letak DataGrid secara keseluruhan untuk sebuah DocType.
/// </summary>
public sealed record DataGridPreference
{
    public Dictionary<string, GridColumnPreference> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Store untuk menyimpan preferensi pengguna (grid layout, UI states) yang terisolasi per tenant dan user.
/// </summary>
public interface IUserPreferenceStore
{
    Task<T?> GetAsync<T>(string tenantId, string userId, string key, CancellationToken ct = default);
    Task SetAsync<T>(string tenantId, string userId, string key, T value, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string userId, string key, CancellationToken ct = default);
}

/// <summary>
/// Implementasi in-memory untuk IUserPreferenceStore (cocok untuk testing dan local dev).
/// </summary>
public sealed class InMemoryUserPreferenceStore : IUserPreferenceStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string Key(string tenantId, string userId, string key) => $"{tenantId}:{userId}:{key}";

    public Task<T?> GetAsync<T>(string tenantId, string userId, string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(Key(tenantId, userId, key), out var json))
        {
            var val = JsonSerializer.Deserialize<T>(json, JsonOpts);
            return Task.FromResult(val);
        }
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string tenantId, string userId, string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        _store[Key(tenantId, userId, key)] = json;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string userId, string key, CancellationToken ct = default)
    {
        _store.TryRemove(Key(tenantId, userId, key), out _);
        return Task.CompletedTask;
    }
}

