using System.Collections.Concurrent;
using System.Text.Json;
using Framework.Persistence;

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
/// Klausul filter tersimpan dalam sebuah custom view.
/// </summary>
public sealed record SavedFilterClause
{
    public string Field { get; init; } = "";
    public string Op { get; init; } = "=";
    public object? Value { get; init; }

    public FilterClause ToFilterClause() => new(Field, Op, CustomViewExtensions.UnwrapValue(Value));
}

/// <summary>
/// Definisi sebuah Saved Filter / Custom View bernama per DocType.
/// </summary>
public sealed record SavedCustomView
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string? Search { get; init; }
    public List<SavedFilterClause> Filters { get; init; } = [];
    public string? SortField { get; init; }
    public string? SortDirection { get; init; }
    public bool IsDefault { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Kumpulan custom views tersimpan untuk sebuah DocType pada level pengguna dan tenant.
/// </summary>
public sealed record DocTypeCustomViewsPreference
{
    public List<SavedCustomView> Views { get; init; } = [];
    public string? DefaultViewId { get; init; }
}

/// <summary>
/// Helper dan ekstensi untuk manajemen Saved Filters / Custom Views via IUserPreferenceStore.
/// </summary>
public static class CustomViewExtensions
{
    public static string ViewsKey(string docType) => $"views:{docType}";

    public static object? UnwrapValue(object? val) => val switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => e.EnumerateArray().Select(x => UnwrapValue(x)).ToList(),
            _ => e.ToString()
        },
        _ => val
    };

    public static async Task<DocTypeCustomViewsPreference> GetCustomViewsAsync(
        this IUserPreferenceStore store,
        string tenantId,
        string userId,
        string docType,
        CancellationToken ct = default)
    {
        var pref = await store.GetAsync<DocTypeCustomViewsPreference>(tenantId, userId, ViewsKey(docType), ct).ConfigureAwait(false);
        if (pref is null) return new DocTypeCustomViewsPreference();

        var cleanedViews = pref.Views.Select(v => v with
        {
            Filters = v.Filters.Select(f => f with { Value = UnwrapValue(f.Value) }).ToList()
        }).ToList();

        return pref with { Views = cleanedViews };
    }

    public static async Task SaveCustomViewAsync(
        this IUserPreferenceStore store,
        string tenantId,
        string userId,
        string docType,
        SavedCustomView view,
        CancellationToken ct = default)
    {
        var pref = await store.GetCustomViewsAsync(tenantId, userId, docType, ct).ConfigureAwait(false);
        var existingList = pref.Views.ToList();

        if (view.IsDefault)
        {
            for (int i = 0; i < existingList.Count; i++)
            {
                if (existingList[i].Id != view.Id && existingList[i].IsDefault)
                    existingList[i] = existingList[i] with { IsDefault = false };
            }
        }

        var idx = existingList.FindIndex(v => v.Id == view.Id || string.Equals(v.Name, view.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            existingList[idx] = view;
        }
        else
        {
            existingList.Add(view);
        }

        var defaultId = view.IsDefault ? view.Id : (pref.DefaultViewId == view.Id ? null : pref.DefaultViewId);
        if (existingList.Any(v => v.IsDefault))
            defaultId = existingList.First(v => v.IsDefault).Id;

        var updated = new DocTypeCustomViewsPreference
        {
            Views = existingList,
            DefaultViewId = defaultId
        };

        await store.SetAsync(tenantId, userId, ViewsKey(docType), updated, ct).ConfigureAwait(false);
    }

    public static async Task DeleteCustomViewAsync(
        this IUserPreferenceStore store,
        string tenantId,
        string userId,
        string docType,
        string viewId,
        CancellationToken ct = default)
    {
        var pref = await store.GetCustomViewsAsync(tenantId, userId, docType, ct).ConfigureAwait(false);
        var existingList = pref.Views.Where(v => v.Id != viewId).ToList();
        var defaultId = pref.DefaultViewId == viewId ? null : pref.DefaultViewId;

        var updated = new DocTypeCustomViewsPreference
        {
            Views = existingList,
            DefaultViewId = defaultId
        };

        await store.SetAsync(tenantId, userId, ViewsKey(docType), updated, ct).ConfigureAwait(false);
    }

    public static async Task SetDefaultCustomViewAsync(
        this IUserPreferenceStore store,
        string tenantId,
        string userId,
        string docType,
        string? viewId,
        CancellationToken ct = default)
    {
        var pref = await store.GetCustomViewsAsync(tenantId, userId, docType, ct).ConfigureAwait(false);
        var existingList = pref.Views.Select(v => v with { IsDefault = (v.Id == viewId) }).ToList();

        var updated = new DocTypeCustomViewsPreference
        {
            Views = existingList,
            DefaultViewId = viewId
        };

        await store.SetAsync(tenantId, userId, ViewsKey(docType), updated, ct).ConfigureAwait(false);
    }
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

