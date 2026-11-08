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
    public const string Bar = "bar", Line = "line", Pie = "pie", Doughnut = "doughnut", Number = "number", Table = "table";
    public static readonly IReadOnlyList<string> All = [Bar, Line, Pie, Doughnut, Number, Table];
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
}

public sealed class InMemoryWidgetStore : IWidgetStore
{
    private readonly ConcurrentDictionary<(string, string), string> _widgets = new();
    private readonly ConcurrentDictionary<string, string> _layouts = new();

    private static WidgetDef Clone(WidgetDef w) => JsonSerializer.Deserialize<WidgetDef>(JsonSerializer.Serialize(w, DashboardJson.Options), DashboardJson.Options)!;

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

    public Task<DashboardLayout> GetLayoutAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_layouts.TryGetValue(tenantId, out var j) ? JsonSerializer.Deserialize<DashboardLayout>(j, DashboardJson.Options)! : new DashboardLayout());

    public Task SaveLayoutAsync(string tenantId, DashboardLayout layout, CancellationToken ct = default)
    {
        _layouts[tenantId] = JsonSerializer.Serialize(layout, DashboardJson.Options);
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
        if (v.Type is not ChartTypes.Number && (v.XColumn is null || !byName.ContainsKey(v.XColumn))) errors.Add("Pilih kolom sumbu X (kategori)");
        if (v.YColumns.Count == 0) errors.Add("Pilih minimal satu kolom nilai (sumbu Y)");
        foreach (var y in v.YColumns)
            if (!byName.TryGetValue(y, out var c)) errors.Add($"Kolom '{y}' tidak ada di hasil query");
            else if (c.Kind != "number") errors.Add($"Kolom '{y}' bukan angka — sumbu Y wajib numerik");
        if (v.Type is ChartTypes.Pie or ChartTypes.Doughnut or ChartTypes.Number && v.YColumns.Count > 1) errors.Add("Grafik ini hanya memakai satu kolom nilai");
        return errors;
    }

    /// <summary>Langkah 4: simpan. Query divalidasi ulang dan dijalankan sekali (dengan nilai default) agar konfigurasi pasti cocok dengan kolom.</summary>
    public async Task<WidgetDef> SaveAsync(UserContext user, WidgetDef widget, bool pin, CancellationToken ct = default)
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
        var layout = await store.GetLayoutAsync(user.TenantId, ct);
        if (layout.Items.RemoveAll(i => i.WidgetId == id) > 0) await store.SaveLayoutAsync(user.TenantId, layout, ct);
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
    public static bool Exposed(CompiledField f) => f.Def.PermLevel == 0 && f.Def.Type is not ("Table" or "Image");

    public static string TypeLabel(CompiledField f) =>
        f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal) ? "angka" : f.Type.ClrType == typeof(bool) ? "boolean" : f.Def.Type == "Date" ? "tanggal" : "teks";

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
