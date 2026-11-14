using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Dashboards;
using Framework.Documents;
using Framework.Security;

namespace Framework.Api;

public static class QueryApiOutputs
{
    public const string Rows = "rows", Object = "object", Csv = "csv";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Rows, Object, Csv };
}

/// <summary>Konfigurasi API dari query tersimpan (JSON). Jalur yang sama dengan widget dashboard &amp; template laporan.</summary>
public sealed class QueryApiConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Sql { get; set; } = "";
    public List<WidgetParameter> Parameters { get; set; } = [];
    /// <summary>rows = daftar baris JSON · object = baris pertama sebagai objek (KPI) · csv = file CSV.</summary>
    public string Output { get; set; } = QueryApiOutputs.Rows;
    public int MaxRows { get; set; } = 1000;
    /// <summary>0 = tanpa cache. Cache per tenant + nilai parameter (termasuk parameter dari akun).</summary>
    public int CacheSeconds { get; set; }
    /// <summary>Role yang boleh memanggil. Kosong = semua pengguna tenant.</summary>
    public List<string> Roles { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

public sealed class QueryApiDef
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    /// <summary>Bagian URL: GET /api/query/{Slug}. Huruf kecil, angka, tanda hubung.</summary>
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public QueryApiConfig Config { get; set; } = new();
    /// <summary>Asal bila diterbitkan dari wizard lain, mis. "widget:w_123" atau "report:rt_456"; null = dibuat langsung.</summary>
    public string? SourceRef { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public interface IQueryApiStore
{
    Task<IReadOnlyList<QueryApiDef>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<QueryApiDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<QueryApiDef?> GetBySlugAsync(string tenantId, string slug, CancellationToken ct = default);
    Task SaveAsync(QueryApiDef def, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryQueryApiStore : IQueryApiStore
{
    private readonly ConcurrentDictionary<(string, string), string> _items = new();
    private static QueryApiDef Read(string j) => JsonSerializer.Deserialize<QueryApiDef>(j, DashboardJson.Options)!;
    private IEnumerable<QueryApiDef> Of(string t) => _items.Where(kv => kv.Key.Item1 == t).Select(kv => Read(kv.Value));
    public Task<IReadOnlyList<QueryApiDef>> ListAsync(string tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<QueryApiDef>>(Of(tenantId).OrderBy(d => d.Slug).ToList());
    public Task<QueryApiDef?> GetAsync(string tenantId, string id, CancellationToken ct = default) => Task.FromResult(_items.TryGetValue((tenantId, id), out var j) ? Read(j) : null);
    public Task<QueryApiDef?> GetBySlugAsync(string tenantId, string slug, CancellationToken ct = default) => Task.FromResult(Of(tenantId).FirstOrDefault(d => d.Slug == slug));
    public Task SaveAsync(QueryApiDef def, CancellationToken ct = default) { _items[(def.TenantId, def.Id)] = JsonSerializer.Serialize(def, DashboardJson.Options); return Task.CompletedTask; }
    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default) { _items.TryRemove((tenantId, id), out _); return Task.CompletedTask; }
}

public sealed record QueryApiResult(QueryApiDef Def, QueryResultSet Data, bool FromCache);

/// <summary>
/// API dari query tersimpan: GET /api/query/{slug}?Param=nilai. Memakai eksekutor read-only dashboard
/// (tenant terisolasi, READ ONLY, statement_timeout, parameter native). Bisa dibuat langsung atau diterbitkan
/// sebagai opsi dari wizard widget dashboard / template laporan (satu jalur query yang sama).
/// </summary>
public sealed partial class QueryApiService(IQueryApiStore store, IReportingQueryExecutor executor, TimeProvider clock)
{
    public const int MaxRowsCap = 10_000, PreviewRows = 100;
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")] private static partial Regex SlugRx();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, QueryResultSet Data)> _cache = new();

    public bool IsAvailable => executor.IsAvailable;
    public static bool CanDesign(UserContext u) => u.Roles.Contains(UserContext.SystemManager);
    public static bool CanCall(UserContext u, QueryApiDef d) => CanDesign(u) || d.Config.Roles.Count == 0 || d.Config.Roles.Any(u.Roles.Contains);
    public static bool IsUserBound(WidgetParameter p) => p.Binding is ParamBindings.UserId or ParamBindings.UserDepartment;
    public static string Slugify(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 1 } x ? x[..Math.Min(x.Length, 60)] : "query";

    private static void DemandDesigner(UserContext u)
    {
        if (!CanDesign(u)) throw new PermissionDeniedException("Publishing query APIs is restricted to System Manager.");
    }

    public static (string? Error, List<WidgetParameter> Parameters) DetectParameters(string sql, IReadOnlyList<WidgetParameter>? existing)
    {
        var (a, ps) = DashboardService.DetectParameters(sql, existing);
        if (!a.Ok) return (a.Error, []);
        foreach (var p in ps.Where(p => p.Binding is not (ParamBindings.Default or ParamBindings.UserId or ParamBindings.UserDepartment)))
            p.Binding = ParamBindings.Default; // filter dashboard tidak berlaku: nilai dikirim pemanggil API
        return (null, ps);
    }

    /// <summary>Nilai parameter: dari akun (tidak bisa ditimpa) atau dari pemanggil (default bila tidak dikirim).
    /// Argumen yang tidak dideklarasikan ditolak.</summary>
    private static List<(WidgetParameter P, object? V)> Bind(UserContext u, QueryApiConfig c, IReadOnlyDictionary<string, string?> args)
    {
        var errors = new List<FieldError>();
        foreach (var k in args.Keys.Where(k => c.Parameters.All(p => !p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))))
            errors.Add(new FieldError(k, "unknown parameter"));
        var values = new List<(WidgetParameter, object?)>();
        foreach (var p in c.Parameters)
        {
            var given = args.FirstOrDefault(kv => kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (IsUserBound(p) && given.Key is not null) { errors.Add(new FieldError(p.Name, "automatically populated from caller account and cannot be provided")); continue; }
            var raw = p.Binding switch
            {
                ParamBindings.UserId => u.Id,
                ParamBindings.UserDepartment => u.Attr("department"),
                _ => given.Key is not null ? given.Value : p.DefaultValue,
            };
            try { values.Add((p, DashboardService.Convert(p, raw))); }
            catch (QueryRejectedException ex) { errors.Add(new FieldError(p.Name, ex.Message)); }
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        return values;
    }

    private async Task<QueryResultSet> RunAsync(UserContext u, QueryApiConfig c, List<(WidgetParameter P, object? V)> values, int limit, CancellationToken ct)
    {
        var a = SqlGuard.Analyze(c.Sql);
        if (!a.Ok) throw new QueryRejectedException(a.Error!);
        var ps = a.Parameters.Select(n => values.FirstOrDefault(v => v.P.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { P: not null } v
            ? new SqlParam(v.V, v.P.Type) : throw new QueryRejectedException($"Parameter @{n} is not defined.")).ToList();
        return await executor.ExecuteAsync(u.TenantId, a.ParameterizedSql, ps, limit, ct);
    }

    public Task<QueryResultSet> PreviewAsync(UserContext u, QueryApiConfig c, CancellationToken ct = default)
    {
        DemandDesigner(u);
        return RunAsync(u, c, Bind(u, c, new Dictionary<string, string?>()), PreviewRows, ct);
    }

    private async Task Validate(UserContext u, QueryApiDef def, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(def.Name)) throw new ApiMethodException("API name is required.");
        if (!SlugRx().IsMatch(def.Slug)) throw new ApiMethodException("Slug must be 2–63 characters: lowercase letters, numbers, or hyphens (e.g. pr-summary).");
        if (!QueryApiOutputs.All.Contains(def.Config.Output)) throw new ApiMethodException("Unknown output format.");
        if (def.Config.MaxRows is < 1 or > MaxRowsCap) throw new ApiMethodException($"Row limit must be between 1 and {MaxRowsCap:N0}.");
        if (def.Config.CacheSeconds is < 0 or > 3600) throw new ApiMethodException("Cache must be between 0 and 3600 seconds.");
        var (err, _) = DetectParameters(def.Config.Sql, def.Config.Parameters);
        if (err is not null) throw new QueryRejectedException(err);
        foreach (var p in def.Config.Parameters)
            if (p.Binding is not (ParamBindings.Default or ParamBindings.UserId or ParamBindings.UserDepartment)) p.Binding = ParamBindings.Default;
        await RunAsync(u, def.Config, Bind(u, def.Config, new Dictionary<string, string?>()), 1, ct); // query & parameter pasti valid sebelum terbit
    }

    public async Task<QueryApiDef> SaveAsync(UserContext u, QueryApiDef def, CancellationToken ct = default)
    {
        DemandDesigner(u);
        await Validate(u, def, ct);
        var existing = string.IsNullOrEmpty(def.Id) ? null : await store.GetAsync(u.TenantId, def.Id, ct);
        if (await store.GetBySlugAsync(u.TenantId, def.Slug, ct) is { } other && other.Id != existing?.Id)
            throw new ApiMethodException($"Slug '{def.Slug}' is already used by API \"{other.Name}\".");
        var now = clock.GetUtcNow();
        def.TenantId = u.TenantId;
        if (existing is null) { def.Id = "qa_" + Guid.NewGuid().ToString("N")[..12]; def.CreatedBy = u.Id; def.CreatedAt = now; def.Version = 1; }
        else { def.CreatedBy = existing.CreatedBy; def.CreatedAt = existing.CreatedAt; def.Version = existing.Version + 1; def.SourceRef ??= existing.SourceRef; }
        def.UpdatedAt = now;
        await store.SaveAsync(def, ct);
        return def;
    }

    /// <summary>Opsi "terbitkan juga sebagai API" dari wizard widget/laporan: query &amp; parameter disalin dan ikut diperbarui
    /// setiap kali sumbernya disimpan. Pengaturan API (keluaran, batas, cache) dari penerbitan sebelumnya dipertahankan.</summary>
    public async Task<QueryApiDef> PublishFromAsync(UserContext u, string sourceRef, string slug, string name, string? description,
        string sql, IReadOnlyList<WidgetParameter> parameters, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        DemandDesigner(u);
        var linked = await GetBySourceAsync(u, sourceRef, ct);
        var bySlug = await store.GetBySlugAsync(u.TenantId, slug, ct);
        if (bySlug is not null && bySlug.SourceRef != sourceRef)
            throw new ApiMethodException($"Slug '{slug}' is already used by API \"{bySlug.Name}\".");
        var def = linked ?? new QueryApiDef { SourceRef = sourceRef };
        def.Slug = slug; def.Name = name; def.Description = description;
        def.Config.Sql = sql;
        def.Config.Parameters = JsonSerializer.Deserialize<List<WidgetParameter>>(JsonSerializer.Serialize(parameters, DashboardJson.Options), DashboardJson.Options)!;
        def.Config.Roles = roles.ToList();
        return await SaveAsync(u, def, ct);
    }

    public async Task<QueryApiDef?> GetBySourceAsync(UserContext u, string sourceRef, CancellationToken ct = default) =>
        (await store.ListAsync(u.TenantId, ct)).FirstOrDefault(d => d.SourceRef == sourceRef);

    public async Task<IReadOnlyList<QueryApiDef>> ListAsync(UserContext u, CancellationToken ct = default) =>
        (await store.ListAsync(u.TenantId, ct)).Where(d => CanDesign(u) || d.Config.Enabled && CanCall(u, d)).ToList();

    public async Task<QueryApiDef?> GetAsync(UserContext u, string id, CancellationToken ct = default)
    {
        DemandDesigner(u);
        return await store.GetAsync(u.TenantId, id, ct);
    }

    public async Task DeleteAsync(UserContext u, string id, CancellationToken ct = default)
    {
        DemandDesigner(u);
        await store.DeleteAsync(u.TenantId, id, ct);
    }

    /// <summary>Menjalankan API. Nonaktif / tidak ada → 404 (keberadaan tidak dibocorkan); role → 403; argumen → 422.</summary>
    public async Task<QueryApiResult> ExecuteAsync(UserContext u, string slug, IReadOnlyDictionary<string, string?> args, CancellationToken ct = default)
    {
        var def = await store.GetBySlugAsync(u.TenantId, slug, ct);
        if (def is null || !def.Config.Enabled) throw new KeyNotFoundException($"Query API '{slug}' not found.");
        if (!CanCall(u, def)) throw new PermissionDeniedException("You do not have access to this API.");
        var values = Bind(u, def.Config, args);
        var key = $"{u.TenantId}|{def.Id}|v{def.Version}|" + string.Join("&", values.Select(v => $"{v.P.Name}={JsonSerializer.Serialize(v.V)}"));
        var now = clock.GetUtcNow();
        if (def.Config.CacheSeconds > 0 && _cache.TryGetValue(key, out var hit) && hit.Expires > now)
            return new QueryApiResult(def, hit.Data, true);
        var data = await RunAsync(u, def.Config, values, def.Config.MaxRows, ct);
        if (def.Config.CacheSeconds > 0)
        {
            foreach (var kv in _cache.Where(kv => kv.Value.Expires <= now).ToList()) _cache.TryRemove(kv.Key, out _);
            _cache[key] = (now.AddSeconds(def.Config.CacheSeconds), data);
        }
        return new QueryApiResult(def, data, false);
    }
}
