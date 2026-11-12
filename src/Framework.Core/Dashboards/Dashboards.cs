using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Dashboards;

// ============================================================================
//  Model widget (disimpan sebagai JSON configuration — skema tabel tidak berubah saat fitur bertambah)
// ============================================================================

public static class ParamTypes
{
    public const string Text = "text", Number = "number", Date = "date", Boolean = "boolean";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Text, Number, Date, Boolean };
}

/// <summary>Sumber nilai parameter saat widget tampil di dashboard.</summary>
public static class ParamBindings
{
    public const string Default = "default";            // nilai default yang disimpan di widget
    public const string GlobalStart = "global:start";   // filter global: tanggal mulai
    public const string GlobalEnd = "global:end";       // filter global: tanggal akhir
    public const string GlobalDepartment = "global:department";
    public const string UserId = "user:id";             // pengguna yang sedang melihat
    public const string UserDepartment = "user:department";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Default, GlobalStart, GlobalEnd, GlobalDepartment, UserId, UserDepartment };
}

public static class ChartTypes
{
    // Built-in SVG renderers (no JavaScript).
    public const string Bar = "bar", Line = "line", Pie = "pie", Doughnut = "doughnut", Number = "number", Table = "table";

    // BlazorBlueprint chart components (Apache ECharts): https://blazorblueprintui.com/charts
    public const string Area = "area", Composed = "composed", Scatter = "scatter", Radar = "radar", RadialBar = "radial-bar",
        Rose = "rose", Funnel = "funnel", Gauge = "gauge", Heatmap = "heatmap", Candlestick = "candlestick", Sankey = "sankey", Map = "map";

    public static readonly IReadOnlyList<string> All =
        [Bar, Line, Pie, Doughnut, Number, Table, Area, Composed, Scatter, Radar, RadialBar, Rose, Funnel, Gauge, Heatmap, Candlestick, Sankey, Map];

    /// <summary>Maximum number of value columns for multi-series charts.</summary>
    public const int MaxSeries = 8;

    /// <summary>Chart needs a category/label column (XColumn).</summary>
    public static bool UsesXColumn(string type) => type is not (Number or Table or Gauge);

    /// <summary>Chart needs a second category column (GroupColumn): heatmap Y axis or sankey target node.</summary>
    public static bool UsesGroupColumn(string type) => type is Heatmap or Sankey;

    /// <summary>Chart accepts more than one value column.</summary>
    public static bool AllowsMultipleY(string type) => type is Bar or Line or Area or Composed or Scatter or Radar or Candlestick;

    /// <summary>Chart uses an explicit maximum value (gauge scale).</summary>
    public static bool UsesMax(string type) => type is Gauge;

    /// <summary>Exact / minimum number of value columns for charts with a fixed shape.</summary>
    public static (int Min, int Max) YColumnRange(string type) => type switch
    {
        Table => (0, 0),
        Candlestick => (4, 4),
        Composed => (2, MaxSeries),
        _ when AllowsMultipleY(type) => (1, MaxSeries),
        _ => (1, 1),
    };

    /// <summary>Short hint shown in the widget builder for the selected chart type.</summary>
    public static string MappingHint(string type) => type switch
    {
        Composed => "First value column is drawn as bars, the remaining columns as lines.",
        Scatter => "X column may be numeric (value axis) or text/date (category axis).",
        Radar => "Each X value becomes a radar axis; each value column becomes one series.",
        Candlestick => "Select exactly 4 value columns in this order: open, high, low, close.",
        Heatmap => "X column = horizontal category, Group column = vertical category, value = cell intensity.",
        Sankey => "X column = source node, Group column = target node, value = flow size.",
        Map => "X column must contain country names in English (e.g. Indonesia, Japan).",
        Gauge => "Shows the first row's value on a gauge; set a maximum or leave empty for automatic scale.",
        _ => "",
    };
}

public sealed class WidgetParameter
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = ParamTypes.Text;
    /// <summary>Nilai default / nilai uji (teks invarian: angka "123.5", tanggal "yyyy-MM-dd", boolean "true").</summary>
    public string? DefaultValue { get; set; }
    public string Binding { get; set; } = ParamBindings.Default;
}

public sealed class VisualConfig
{
    public string Type { get; set; } = ChartTypes.Bar;
    /// <summary>Kolom kategori/label (sumbu X). Tidak dipakai untuk Number dan Table.</summary>
    public string? XColumn { get; set; }
    /// <summary>Kolom nilai (sumbu Y), wajib numerik. Pie/Doughnut/Number memakai satu kolom.</summary>
    public List<string> YColumns { get; set; } = [];
    /// <summary>Second category column: heatmap vertical axis or sankey target node.</summary>
    public string? GroupColumn { get; set; }
    /// <summary>Gauge maximum. Null = automatic scale from the value.</summary>
    public decimal? Max { get; set; }
}

public sealed class WidgetConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Sql { get; set; } = "";
    public List<WidgetParameter> Parameters { get; set; } = [];
    public VisualConfig Visual { get; set; } = new();
    /// <summary>Role yang boleh melihat widget. Kosong = semua pengguna tenant.</summary>
    public List<string> Roles { get; set; } = [];
}

public sealed class WidgetDef
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public WidgetConfig Config { get; set; } = new();
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class DashboardItem
{
    public string WidgetId { get; set; } = "";
    /// <summary>Lebar kartu dalam kolom grid (1–3).</summary>
    public int Span { get; set; } = 1;
}

public sealed class DashboardLayout
{
    public List<DashboardItem> Items { get; set; } = [];
}

public sealed class DashboardDef
{
    public const string DefaultDashboardId = "d_default";

    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string OwnerId { get; set; } = "";
    public bool IsShared { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>Roles allowed to view a shared dashboard. Empty = all users in the tenant.</summary>
    public List<string> Roles { get; set; } = [];
    public DashboardLayout Layout { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ============================================================================
//  Analisis SQL: parameter @Nama, satu statement SELECT/WITH saja
// ============================================================================

public sealed record SqlAnalysis(bool Ok, string? Error, string ParameterizedSql, IReadOnlyList<string> Parameters);

public static class SqlGuard
{
    public const int MaxLength = 10_000;

    /// <summary>
    /// Memindai query: @Nama di luar string literal, identifier berkutip, dan komentar diganti placeholder $1, $2, …
    /// (nama sama → nomor sama). Menolak lebih dari satu statement, dollar-quoting, dan perintah selain SELECT/WITH.
    /// Ini lapisan pertama; pertahanan utama tetap koneksi read-only + transaksi READ ONLY di eksekutor.
    /// </summary>
    public static SqlAnalysis Analyze(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Fail("Query kosong");
        if (sql.Length > MaxLength) return Fail($"Query melebihi {MaxLength} karakter");

        var sb = new StringBuilder(sql.Length);
        var names = new List<string>();
        var i = 0;
        var significant = new StringBuilder(); // teks tanpa string/komentar untuk cek keyword
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'' || c == '"')
            {
                var start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == c) { if (i + 1 < sql.Length && sql[i + 1] == c) { i += 2; continue; } i++; break; }
                    i++;
                }
                if (i > sql.Length || sql[i - 1] != c || i - start < 2) return Fail(c == '\'' ? "String literal tidak ditutup" : "Identifier berkutip tidak ditutup");
                sb.Append(sql, start, i - start);
                significant.Append(c == '"' ? " ident " : " 'str' ");
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') sb.Append(sql[i++]);
                significant.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return Fail("Komentar /* tidak ditutup");
                sb.Append(sql, i, end + 2 - i);
                i = end + 2;
                significant.Append(' ');
                continue;
            }
            if (c == '$' && i + 1 < sql.Length && (sql[i + 1] == '$' || char.IsLetter(sql[i + 1])))
                return Fail("Dollar-quoting ($$ … $$) tidak diizinkan");
            if (c == '$' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
                return Fail("Gunakan parameter bernama @Nama, bukan $1");
            if (c == '@' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_') && (i == 0 || !IsIdent(sql[i - 1]) && sql[i - 1] != '@'))
            {
                var start = ++i;
                while (i < sql.Length && IsIdent(sql[i])) i++;
                var name = sql[start..i];
                var idx = names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) { names.Add(name); idx = names.Count - 1; }
                sb.Append('$').Append(idx + 1);
                significant.Append(" $p ");
                continue;
            }
            if (c == ';')
            {
                var rest = sql[(i + 1)..];
                if (!string.IsNullOrWhiteSpace(StripComments(rest))) return Fail("Hanya satu statement yang diizinkan (hapus ';' di tengah query)");
                i++;
                continue; // titik koma penutup diabaikan
            }
            sb.Append(c);
            significant.Append(c);
            i++;
        }

        var first = FirstKeyword(significant.ToString());
        if (first is not ("select" or "with"))
            return Fail("Hanya query SELECT (atau WITH … SELECT) yang diizinkan");
        return new SqlAnalysis(true, null, sb.ToString().TrimEnd(), names);
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static SqlAnalysis Fail(string e) => new(false, e, "", []);

    private static string FirstKeyword(string s)
    {
        var t = s.TrimStart(' ', '\t', '\r', '\n', '(');
        var end = 0;
        while (end < t.Length && char.IsLetter(t[end])) end++;
        return t[..end].ToLowerInvariant();
    }

    private static string StripComments(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '-') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? s.Length : e + 1; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

// ============================================================================
//  Eksekusi query (implementasi PostgreSQL ada di Framework.Persistence.PostgreSql)
// ============================================================================

public sealed record ResultColumn(string Name, string Kind); // Kind: number | text | date | boolean
/// <summary>Nilai parameter beserta tipe yang dideklarasikan — tipe tetap dikirim walau nilainya NULL
/// (pola filter opsional "(@X IS NULL OR kolom = @X)" butuh tipe yang pasti).</summary>
public sealed record SqlParam(object? Value, string Type);
public sealed record QueryResultSet(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<object?[]> Rows, bool Truncated, long ElapsedMs);

public sealed class QueryRejectedException(string message) : Exception(message);
public sealed class ReportingUnavailableException(string message) : Exception(message);

public interface IReportingQueryExecutor
{
    bool IsAvailable { get; }
    /// <param name="parameters">Nilai sudah dikonversi ke tipe .NET (string, decimal, DateOnly, bool, null), sesuai urutan placeholder.</param>
    /// <param name="timeout">Batas waktu khusus panggilan ini (mis. laporan file); null = bawaan konfigurasi.</param>
    Task<QueryResultSet> ExecuteAsync(string tenantId, string parameterizedSql, IReadOnlyList<SqlParam> parameters, int limit, CancellationToken ct = default, TimeSpan? timeout = null);
}

public sealed class UnavailableReportingExecutor : IReportingQueryExecutor
{
    public bool IsAvailable => false;
    public Task<QueryResultSet> ExecuteAsync(string tenantId, string sql, IReadOnlyList<SqlParam> parameters, int limit, CancellationToken ct = default, TimeSpan? timeout = null) =>
        throw new ReportingUnavailableException("SQL dashboards require PostgreSQL mode and a read-only connection string (ConnectionStrings:FrogFrameReadOnly).");
}

// ============================================================================
//  Penyimpanan widget & layout
// ============================================================================

public interface IWidgetStore
{
    Task<IReadOnlyList<WidgetDef>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<WidgetDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task SaveAsync(WidgetDef widget, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
    Task<DashboardLayout> GetLayoutAsync(string tenantId, CancellationToken ct = default);
    Task SaveLayoutAsync(string tenantId, DashboardLayout layout, CancellationToken ct = default);

    Task<IReadOnlyList<DashboardDef>> ListDashboardsAsync(string tenantId, CancellationToken ct = default);
    Task<DashboardDef?> GetDashboardAsync(string tenantId, string id, CancellationToken ct = default);
    Task SaveDashboardAsync(DashboardDef dashboard, CancellationToken ct = default);
    Task DeleteDashboardAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryWidgetStore : IWidgetStore
{
    private readonly ConcurrentDictionary<(string, string), string> _widgets = new();
    private readonly ConcurrentDictionary<(string, string), string> _dashboards = new();

    private static WidgetDef Clone(WidgetDef w) => JsonSerializer.Deserialize<WidgetDef>(JsonSerializer.Serialize(w, DashboardJson.Options), DashboardJson.Options)!;
    private static DashboardDef Clone(DashboardDef d) => JsonSerializer.Deserialize<DashboardDef>(JsonSerializer.Serialize(d, DashboardJson.Options), DashboardJson.Options)!;

    public Task<IReadOnlyList<WidgetDef>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WidgetDef>>(_widgets.Where(kv => kv.Key.Item1 == tenantId)
            .Select(kv => JsonSerializer.Deserialize<WidgetDef>(kv.Value, DashboardJson.Options)!).OrderBy(w => w.Name).ToList());

    public Task<WidgetDef?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_widgets.TryGetValue((tenantId, id), out var j) ? JsonSerializer.Deserialize<WidgetDef>(j, DashboardJson.Options) : null);

    public Task SaveAsync(WidgetDef widget, CancellationToken ct = default)
    {
        _widgets[(widget.TenantId, widget.Id)] = JsonSerializer.Serialize(Clone(widget), DashboardJson.Options);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default) { _widgets.TryRemove((tenantId, id), out _); return Task.CompletedTask; }

    public async Task<DashboardLayout> GetLayoutAsync(string tenantId, CancellationToken ct = default)
    {
        var d = await GetDashboardAsync(tenantId, DashboardDef.DefaultDashboardId, ct);
        return d?.Layout ?? new DashboardLayout();
    }

    public async Task SaveLayoutAsync(string tenantId, DashboardLayout layout, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await GetDashboardAsync(tenantId, DashboardDef.DefaultDashboardId, ct) ?? new DashboardDef
        {
            Id = DashboardDef.DefaultDashboardId,
            TenantId = tenantId,
            Name = "Main Dashboard",
            Description = "Default shared tenant dashboard",
            OwnerId = "admin",
            IsShared = true,
            IsDefault = true,
            CreatedAt = now
        };
        existing.Layout = layout;
        existing.UpdatedAt = now;
        await SaveDashboardAsync(existing, ct);
    }

    public Task<IReadOnlyList<DashboardDef>> ListDashboardsAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DashboardDef>>(_dashboards.Where(kv => kv.Key.Item1 == tenantId)
            .Select(kv => JsonSerializer.Deserialize<DashboardDef>(kv.Value, DashboardJson.Options)!)
            .OrderByDescending(d => d.IsDefault)
            .ThenByDescending(d => d.IsShared)
            .ThenBy(d => d.Name)
            .ToList());

    public Task<DashboardDef?> GetDashboardAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_dashboards.TryGetValue((tenantId, id), out var j) ? JsonSerializer.Deserialize<DashboardDef>(j, DashboardJson.Options) : null);

    public Task SaveDashboardAsync(DashboardDef dashboard, CancellationToken ct = default)
    {
        _dashboards[(dashboard.TenantId, dashboard.Id)] = JsonSerializer.Serialize(Clone(dashboard), DashboardJson.Options);
        return Task.CompletedTask;
    }

    public Task DeleteDashboardAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _dashboards.TryRemove((tenantId, id), out _);
        return Task.CompletedTask;
    }
}

// ============================================================================
//  Layanan dashboard: validasi, binding parameter, izin
// ============================================================================

public sealed record DashboardFilters(DateOnly? Start, DateOnly? End, string? Department);

public sealed record WidgetRun(WidgetDef Widget, QueryResultSet? Result, string? Error);

public sealed class DashboardService(IWidgetStore store, IReportingQueryExecutor executor, TimeProvider clock,
    int previewLimit = 100, int widgetRowLimit = 1000)
{
    public bool IsAvailable => executor.IsAvailable;
    public const string DesignerRole = UserContext.SystemManager;

    public static bool CanDesign(UserContext user) => user.Roles.Contains(DesignerRole);

    private static void DemandDesigner(UserContext user)
    {
        if (!CanDesign(user)) throw new PermissionDeniedException("Designing dashboard widgets is restricted to System Manager.");
    }

    /// <summary>Langkah 1: deteksi parameter. Tipe awal ditebak dari nama (…Date/Tanggal → tanggal, Is…/Aktif → boolean).</summary>
    public static (SqlAnalysis Analysis, List<WidgetParameter> Parameters) DetectParameters(string sql, IReadOnlyList<WidgetParameter>? existing = null)
    {
        var a = SqlGuard.Analyze(sql);
        if (!a.Ok) return (a, []);
        var list = a.Parameters.Select(n => existing?.FirstOrDefault(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? new WidgetParameter
        {
            Name = n, Label = n,
            Type = n.Contains("date", StringComparison.OrdinalIgnoreCase) || n.Contains("tanggal", StringComparison.OrdinalIgnoreCase) ? ParamTypes.Date
                 : n.StartsWith("is", StringComparison.OrdinalIgnoreCase) || n.Contains("aktif", StringComparison.OrdinalIgnoreCase) ? ParamTypes.Boolean
                 : n.Contains("min", StringComparison.OrdinalIgnoreCase) || n.Contains("max", StringComparison.OrdinalIgnoreCase) || n.Contains("jumlah", StringComparison.OrdinalIgnoreCase) ? ParamTypes.Number
                 : ParamTypes.Text,
            Binding = n.Contains("start", StringComparison.OrdinalIgnoreCase) || n.Contains("mulai", StringComparison.OrdinalIgnoreCase) || n.Contains("dari", StringComparison.OrdinalIgnoreCase) ? ParamBindings.GlobalStart
                    : n.Contains("end", StringComparison.OrdinalIgnoreCase) || n.Contains("akhir", StringComparison.OrdinalIgnoreCase) || n.Contains("sampai", StringComparison.OrdinalIgnoreCase) ? ParamBindings.GlobalEnd
                    : ParamBindings.Default,
        }).ToList();
        return (a, list);
    }

    /// <summary>Konversi nilai teks ke tipe parameter. Gagal → pesan yang jelas (bukan dieksekusi sebagai teks).</summary>
    public static object? Convert(WidgetParameter p, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        return p.Type switch
        {
            ParamTypes.Number => decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d
                : throw new QueryRejectedException($"Parameter @{p.Name} must be a number."),
            ParamTypes.Date => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt
                : throw new QueryRejectedException($"Parameter @{p.Name} must be date yyyy-MM-dd."),
            ParamTypes.Boolean => raw.ToLowerInvariant() is "true" or "1" or "ya" ? true : raw.ToLowerInvariant() is "false" or "0" or "tidak" ? false
                : throw new QueryRejectedException($"Parameter @{p.Name} must be true or false."),
            _ => raw,
        };
    }

    private static string? BoundValue(WidgetParameter p, UserContext user, DashboardFilters? f) => p.Binding switch
    {
        ParamBindings.GlobalStart => f?.Start?.ToString("yyyy-MM-dd") ?? p.DefaultValue,
        ParamBindings.GlobalEnd => f?.End?.ToString("yyyy-MM-dd") ?? p.DefaultValue,
        ParamBindings.GlobalDepartment => string.IsNullOrWhiteSpace(f?.Department) ? p.DefaultValue : f!.Department,
        ParamBindings.UserId => user.Id,
        ParamBindings.UserDepartment => user.Attr("department") ?? p.DefaultValue,
        _ => p.DefaultValue,
    };

    private async Task<QueryResultSet> RunAsync(UserContext user, string sql, IReadOnlyList<WidgetParameter> parameters,
        Func<WidgetParameter, string?> valueOf, int limit, CancellationToken ct)
    {
        var a = SqlGuard.Analyze(sql);
        if (!a.Ok) throw new QueryRejectedException(a.Error!);
        var values = new List<SqlParam>();
        foreach (var name in a.Parameters)
        {
            var p = parameters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new QueryRejectedException($"Parameter @{name} is not defined.");
            if (!ParamTypes.All.Contains(p.Type)) throw new QueryRejectedException($"Unknown parameter type for @{name}.");
            values.Add(new SqlParam(Convert(p, valueOf(p)), p.Type));
        }
        return await executor.ExecuteAsync(user.TenantId, a.ParameterizedSql, values, limit, ct);
    }

    /// <summary>Langkah 2: pratinjau dengan nilai uji (maks. previewLimit baris).</summary>
    public Task<QueryResultSet> PreviewAsync(UserContext user, string sql, IReadOnlyList<WidgetParameter> parameters, CancellationToken ct = default)
    {
        DemandDesigner(user);
        return RunAsync(user, sql, parameters, p => p.DefaultValue, previewLimit, ct);
    }

    public static IReadOnlyList<string> ValidateVisual(VisualConfig v, IReadOnlyList<ResultColumn> columns)
    {
        var errors = new List<string>();
        if (!ChartTypes.All.Contains(v.Type)) { errors.Add("Jenis grafik tidak dikenal"); return errors; }
        if (v.Type == ChartTypes.Table) return errors;

        var byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        if (ChartTypes.UsesXColumn(v.Type) && (v.XColumn is null || !byName.ContainsKey(v.XColumn)))
            errors.Add("Pilih kolom sumbu X (kategori)");

        if (ChartTypes.UsesGroupColumn(v.Type) && (v.GroupColumn is null || !byName.ContainsKey(v.GroupColumn)))
            errors.Add(v.Type == ChartTypes.Sankey ? "Pilih kolom target node (Group Column)" : "Pilih kolom sumbu Y kategori (Group Column)");

        var (minY, maxY) = ChartTypes.YColumnRange(v.Type);
        if (minY > 0 && v.YColumns.Count < minY)
            errors.Add(minY == maxY ? $"Grafik ini memerlukan tepat {minY} kolom nilai" : $"Pilih minimal {minY} kolom nilai (sumbu Y)");

        if (maxY > 0 && v.YColumns.Count > maxY)
            errors.Add(minY == maxY ? $"Grafik ini memerlukan tepat {maxY} kolom nilai" : $"Maksimal {maxY} kolom nilai untuk grafik ini");

        foreach (var y in v.YColumns)
        {
            if (!byName.TryGetValue(y, out var c))
                errors.Add($"Kolom '{y}' tidak ada di hasil query");
            else if (c.Kind != "number")
                errors.Add($"Kolom '{y}' bukan angka — sumbu Y wajib numerik");
        }

        return errors;
    }

    /// <summary>Langkah 4: simpan. Query divalidasi ulang dan dijalankan sekali (dengan nilai default) agar konfigurasi pasti cocok dengan kolom.</summary>
    public async Task<WidgetDef> SaveAsync(UserContext user, WidgetDef widget, bool pin = false, CancellationToken ct = default)
    {
        DemandDesigner(user);
        if (string.IsNullOrWhiteSpace(widget.Name)) throw new QueryRejectedException("Widget name is required.");
        foreach (var p in widget.Config.Parameters)
            if (!ParamBindings.All.Contains(p.Binding)) throw new QueryRejectedException($"Unknown parameter binding for @{p.Name}.");
        var result = await RunAsync(user, widget.Config.Sql, widget.Config.Parameters, p => p.DefaultValue, 1, ct);
        var errors = ValidateVisual(widget.Config.Visual, result.Columns);
        if (errors.Count > 0) throw new QueryRejectedException(string.Join(" ", errors));

        var now = clock.GetUtcNow();
        var existing = string.IsNullOrEmpty(widget.Id) ? null : await store.GetAsync(user.TenantId, widget.Id, ct);
        widget.TenantId = user.TenantId; // tenant selalu dari identitas
        if (existing is null)
        {
            widget.Id = "w_" + Guid.NewGuid().ToString("N")[..12];
            widget.CreatedBy = user.Id; widget.CreatedAt = now; widget.Version = 1;
        }
        else
        {
            widget.CreatedBy = existing.CreatedBy; widget.CreatedAt = existing.CreatedAt; widget.Version = existing.Version + 1;
        }
        widget.UpdatedAt = now;
        await store.SaveAsync(widget, ct);
        if (pin) await PinAsync(user, widget.Id, ct);
        return widget;
    }

    public async Task<IReadOnlyList<WidgetDef>> ListAsync(UserContext user, CancellationToken ct = default) =>
        (await store.ListAsync(user.TenantId, ct)).Where(w => CanSee(user, w)).ToList();

    public static bool CanSee(UserContext user, WidgetDef w) =>
        CanDesign(user) || w.Config.Roles.Count == 0 || w.Config.Roles.Any(user.Roles.Contains);

    public async Task<WidgetDef?> GetAsync(UserContext user, string id, CancellationToken ct = default) =>
        await store.GetAsync(user.TenantId, id, ct) is { } w && CanSee(user, w) ? w : null;

    public async Task DeleteAsync(UserContext user, string id, CancellationToken ct = default)
    {
        DemandDesigner(user);
        await store.DeleteAsync(user.TenantId, id, ct);
        var dashboards = await store.ListDashboardsAsync(user.TenantId, ct);
        foreach (var d in dashboards)
        {
            if (d.Layout.Items.RemoveAll(i => i.WidgetId == id) > 0)
            {
                d.UpdatedAt = clock.GetUtcNow();
                await store.SaveDashboardAsync(d, ct);
            }
        }
    }

    // ---------------- Standalone Dashboards (Shared & Personal) ----------------

    public static bool CanViewDashboard(UserContext user, DashboardDef d) =>
        string.Equals(d.OwnerId, user.Id, StringComparison.OrdinalIgnoreCase) ||
        (d.IsShared && (CanDesign(user) || d.Roles.Count == 0 || d.Roles.Any(user.Roles.Contains)));

    public static bool CanEditDashboard(UserContext user, DashboardDef d) =>
        d.IsShared ? CanDesign(user) : (string.Equals(d.OwnerId, user.Id, StringComparison.OrdinalIgnoreCase) || CanDesign(user));

    public async Task<IReadOnlyList<DashboardDef>> ListDashboardsAsync(UserContext user, CancellationToken ct = default) =>
        (await store.ListDashboardsAsync(user.TenantId, ct))
            .Where(d => CanViewDashboard(user, d))
            .OrderByDescending(d => d.IsDefault && d.IsShared)
            .ThenByDescending(d => string.Equals(d.OwnerId, user.Id, StringComparison.OrdinalIgnoreCase) && !d.IsShared)
            .ThenByDescending(d => d.IsShared)
            .ThenBy(d => d.Name)
            .ToList();

    public async Task<DashboardDef?> GetDashboardAsync(UserContext user, string id, CancellationToken ct = default) =>
        await store.GetDashboardAsync(user.TenantId, id, ct) is { } d && CanViewDashboard(user, d) ? d : null;

    public async Task<DashboardDef> SaveDashboardAsync(UserContext user, DashboardDef dashboard, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dashboard.Name))
            throw new QueryRejectedException("Dashboard name is required.");

        var isDesigner = CanDesign(user);
        var existing = string.IsNullOrEmpty(dashboard.Id) ? null : await store.GetDashboardAsync(user.TenantId, dashboard.Id, ct);
        if (existing is not null && !CanEditDashboard(user, existing))
            throw new PermissionDeniedException("You do not have permission to edit this dashboard.");

        if (!isDesigner)
        {
            if (dashboard.IsShared || dashboard.IsDefault)
                throw new PermissionDeniedException("Only System Managers can create or modify shared/default dashboards.");
            dashboard.IsShared = false;
            dashboard.IsDefault = false;
            dashboard.Roles = [];
        }

        // Validate and sanitize layout items
        var existingWidgetIds = existing?.Layout.Items.Select(i => i.WidgetId).ToHashSet(StringComparer.Ordinal) ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var validItems = new List<DashboardItem>();
        foreach (var item in dashboard.Layout.Items)
        {
            if (string.IsNullOrWhiteSpace(item.WidgetId) || !seen.Add(item.WidgetId)) continue;
            item.Span = Math.Clamp(item.Span, 1, 3);
            if (!existingWidgetIds.Contains(item.WidgetId))
            {
                var w = await store.GetAsync(user.TenantId, item.WidgetId, ct)
                    ?? throw new QueryRejectedException($"Widget '{item.WidgetId}' does not exist.");
                if (!CanSee(user, w))
                    throw new PermissionDeniedException($"You do not have permission to add widget '{w.Name}'.");
            }
            validItems.Add(item);
        }
        dashboard.Layout.Items = validItems;

        var now = clock.GetUtcNow();
        dashboard.Name = dashboard.Name.Trim();
        dashboard.Description = string.IsNullOrWhiteSpace(dashboard.Description) ? null : dashboard.Description.Trim();
        dashboard.TenantId = user.TenantId;

        if (existing is null)
        {
            if (string.IsNullOrEmpty(dashboard.Id))
                dashboard.Id = "d_" + Guid.NewGuid().ToString("N")[..12];
            dashboard.OwnerId = user.Id;
            dashboard.CreatedAt = now;
        }
        else
        {
            dashboard.OwnerId = existing.OwnerId;
            dashboard.CreatedAt = existing.CreatedAt;
        }
        dashboard.UpdatedAt = now;

        if (!dashboard.IsShared)
        {
            dashboard.IsDefault = false;
            dashboard.Roles = [];
        }

        if (dashboard.IsShared && dashboard.IsDefault)
        {
            var all = await store.ListDashboardsAsync(user.TenantId, ct);
            foreach (var other in all.Where(o => o.Id != dashboard.Id && o.IsDefault))
            {
                other.IsDefault = false;
                other.UpdatedAt = now;
                await store.SaveDashboardAsync(other, ct);
            }
        }

        await store.SaveDashboardAsync(dashboard, ct);
        return dashboard;
    }

    public async Task DeleteDashboardAsync(UserContext user, string id, CancellationToken ct = default)
    {
        var existing = await store.GetDashboardAsync(user.TenantId, id, ct);
        if (existing is null) return;
        if (!CanEditDashboard(user, existing))
            throw new PermissionDeniedException("You do not have permission to delete this dashboard.");
        await store.DeleteDashboardAsync(user.TenantId, id, ct);
    }

    public async Task<DashboardDef> AddWidgetToDashboardAsync(UserContext user, string dashboardId, string widgetId, int span = 1, CancellationToken ct = default)
    {
        var d = await store.GetDashboardAsync(user.TenantId, dashboardId, ct)
            ?? throw new QueryRejectedException("Dashboard not found.");
        if (!CanEditDashboard(user, d))
            throw new PermissionDeniedException("You do not have permission to edit this dashboard.");

        var w = await store.GetAsync(user.TenantId, widgetId, ct)
            ?? throw new QueryRejectedException("Widget not found.");
        if (!CanSee(user, w))
            throw new PermissionDeniedException($"You do not have permission to add widget '{w.Name}'.");

        if (d.Layout.Items.All(i => i.WidgetId != widgetId))
        {
            d.Layout.Items.Add(new DashboardItem { WidgetId = widgetId, Span = Math.Clamp(span, 1, 3) });
            d.UpdatedAt = clock.GetUtcNow();
            await store.SaveDashboardAsync(d, ct);
        }
        return d;
    }

    public async Task<DashboardDef> RemoveWidgetFromDashboardAsync(UserContext user, string dashboardId, string widgetId, CancellationToken ct = default)
    {
        var d = await store.GetDashboardAsync(user.TenantId, dashboardId, ct)
            ?? throw new QueryRejectedException("Dashboard not found.");
        if (!CanEditDashboard(user, d))
            throw new PermissionDeniedException("You do not have permission to edit this dashboard.");

        if (d.Layout.Items.RemoveAll(i => i.WidgetId == widgetId) > 0)
        {
            d.UpdatedAt = clock.GetUtcNow();
            await store.SaveDashboardAsync(d, ct);
        }
        return d;
    }

    public Task<DashboardLayout> GetLayoutAsync(UserContext user, CancellationToken ct = default) => store.GetLayoutAsync(user.TenantId, ct);

    public async Task SaveLayoutAsync(UserContext user, DashboardLayout layout, CancellationToken ct = default)
    {
        DemandDesigner(user);
        foreach (var i in layout.Items) i.Span = Math.Clamp(i.Span, 1, 3);
        await store.SaveLayoutAsync(user.TenantId, layout, ct);
    }

    public async Task PinAsync(UserContext user, string widgetId, CancellationToken ct = default)
    {
        DemandDesigner(user);
        var layout = await store.GetLayoutAsync(user.TenantId, ct);
        if (layout.Items.All(i => i.WidgetId != widgetId)) layout.Items.Add(new DashboardItem { WidgetId = widgetId, Span = 1 });
        await store.SaveLayoutAsync(user.TenantId, layout, ct);
    }

    public async Task UnpinAsync(UserContext user, string widgetId, CancellationToken ct = default)
    {
        DemandDesigner(user);
        var layout = await store.GetLayoutAsync(user.TenantId, ct);
        layout.Items.RemoveAll(i => i.WidgetId == widgetId);
        await store.SaveLayoutAsync(user.TenantId, layout, ct);
    }

    /// <summary>Runtime: parameter diikat ke filter global dashboard / konteks pengguna, lalu dieksekusi.</summary>
    public async Task<WidgetRun> RunWidgetAsync(UserContext user, WidgetDef widget, DashboardFilters filters, CancellationToken ct = default)
    {
        if (!CanSee(user, widget)) return new WidgetRun(widget, null, "Anda tidak memiliki akses ke widget ini");
        try
        {
            var r = await RunAsync(user, widget.Config.Sql, widget.Config.Parameters, p => BoundValue(p, user, filters), widgetRowLimit, ct);
            return new WidgetRun(widget, r, null);
        }
        catch (Exception ex) when (ex is QueryRejectedException or ReportingUnavailableException)
        {
            return new WidgetRun(widget, null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Satu widget yang gagal tidak boleh menjatuhkan seluruh halaman dashboard.
            return new WidgetRun(widget, null, $"Widget gagal dijalankan ({ex.GetType().Name}): {ex.Message}");
        }
    }
}

// ============================================================================
//  Katalog view pelaporan (nama & kolom), dipakai sinkronisasi skema dan panduan di UI
// ============================================================================

public sealed record ReportingView(string Name, string Title, IReadOnlyList<(string Column, string Type)> Columns);

public static class ReportingCatalog
{
    public static string SnakeCase(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0 && !char.IsUpper(pascal[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    /// <summary>Field yang diekspos ke view: hanya level izin 0; child table jadi view sendiri; gambar tidak diekspos.</summary>
    public static bool Exposed(CompiledField f) => f.Def.PermLevel == 0 && FieldKinds.IsReported(f.Def.Type);

    public static string TypeLabel(CompiledField f) =>
        f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal) ? "angka" : f.Type.ClrType == typeof(bool) ? "boolean"
        : f.Def.Type == "Date" ? "tanggal" : f.Def.Type == "Datetime" ? "waktu" : f.Def.Type == "Time" ? "jam" : "teks";

    public static IReadOnlyList<ReportingView> Describe(MetadataSnapshot snap)
    {
        var list = new List<ReportingView>();
        foreach (var meta in snap.DocTypes.Values.Where(m => !m.Def.IsChild).OrderBy(m => m.Name))
        {
            var cols = new List<(string, string)> { ("name", "teks"), ("owner", "teks"), ("docstatus", "angka"), ("workflow_state", "teks"), ("created", "waktu"), ("modified", "waktu") };
            cols.AddRange(meta.Fields.Where(Exposed).Select(f => (f.Name, TypeLabel(f))));
            list.Add(new ReportingView($"reporting.{SnakeCase(meta.Name)}", meta.Title, cols));
            foreach (var t in meta.TableFields.Where(t => t.Def.PermLevel == 0))
                if (snap.TryGet(t.Def.ChildDocType!, out var child))
                {
                    var cc = new List<(string, string)> { ("parent", "teks"), ("idx", "angka") };
                    cc.AddRange(child.Fields.Where(Exposed).Select(f => (f.Name, TypeLabel(f))));
                    list.Add(new ReportingView($"reporting.{SnakeCase(meta.Name)}_{t.Name}", $"{meta.Title} · {t.Label}", cc));
                }
        }
        return list;
    }
}
