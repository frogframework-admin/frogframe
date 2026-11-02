using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Dashboards;
using Framework.Security;

namespace Framework.Reports;

/// <summary>Konfigurasi template laporan (disimpan sebagai JSON; file template disimpan terpisah).</summary>
public sealed class ReportTemplateConfig
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Query detail: baris-barisnya mengisi tag [[Kolom]].</summary>
    public string Sql { get; set; } = "";
    /// <summary>Opsional: query header satu baris untuk tag {{Kolom}} (mis. nama perusahaan, total).</summary>
    public string? HeaderSql { get; set; }
    public List<WidgetParameter> Parameters { get; set; } = [];
    public string Format { get; set; } = ReportFormats.Xlsx;
    /// <summary>Pola nama file, boleh memakai tag tunggal, mis. "Gaji {{Periode}}". Ekstensi ditambahkan otomatis.</summary>
    public string FileNamePattern { get; set; } = "";
    /// <summary>Role yang boleh membuat laporan ini. Kosong = semua pengguna tenant.</summary>
    public List<string> Roles { get; set; } = [];
}

public sealed class ReportTemplateDef
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ReportTemplateConfig Config { get; set; } = new();
    public string TemplateFileName { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public interface IReportTemplateStore
{
    Task<IReadOnlyList<ReportTemplateDef>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<ReportTemplateDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<byte[]?> GetFileAsync(string tenantId, string id, CancellationToken ct = default);
    /// <summary><paramref name="file"/> null = file template lama dipertahankan.</summary>
    Task SaveAsync(ReportTemplateDef def, byte[]? file, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);
}

public sealed class InMemoryReportTemplateStore : IReportTemplateStore
{
    private readonly ConcurrentDictionary<(string, string), (string Json, byte[] File)> _items = new();
    private static ReportTemplateDef Read(string json) => JsonSerializer.Deserialize<ReportTemplateDef>(json, DashboardJson.Options)!;

    public Task<IReadOnlyList<ReportTemplateDef>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReportTemplateDef>>(_items.Where(kv => kv.Key.Item1 == tenantId).Select(kv => Read(kv.Value.Json)).OrderBy(d => d.Name).ToList());
    public Task<ReportTemplateDef?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue((tenantId, id), out var v) ? Read(v.Json) : null);
    public Task<byte[]?> GetFileAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue((tenantId, id), out var v) ? v.File : null);
    public Task SaveAsync(ReportTemplateDef def, byte[]? file, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(def, DashboardJson.Options);
        _items.AddOrUpdate((def.TenantId, def.Id), _ => (json, file ?? []), (_, old) => (json, file ?? old.File));
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default) { _items.TryRemove((tenantId, id), out _); return Task.CompletedTask; }
}

public sealed record ReportPreview(QueryResultSet Detail, QueryResultSet? Header);
public sealed record TemplateCheck(IReadOnlyList<TemplateTag> Tags, IReadOnlyList<string> Problems);
public sealed record ReportOutput(byte[] Content, string FileName, string ContentType, int Rows);

public sealed class ReportLimits
{
    public int PreviewRows { get; set; } = 100;
    /// <summary>Batas baris laporan Excel. Melebihi batas = ditolak (bukan dipotong diam-diam).</summary>
    public int MaxRowsExcel { get; set; } = 50_000;
    public int MaxRowsWord { get; set; } = 5_000;
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Template laporan Excel/Word: query (+ query header opsional) → parameter → template bertag → file.
/// Memakai eksekutor read-only yang sama dengan dashboard (tenant terisolasi, READ ONLY, parameter native).
/// </summary>
public sealed class ReportTemplateService(IReportTemplateStore store, IReportingQueryExecutor executor, TimeProvider clock, ReportLimits limits)
{
    public bool IsAvailable => executor.IsAvailable;
    public ReportLimits Limits => limits;
    public static bool CanDesign(UserContext u) => u.Roles.Contains(UserContext.SystemManager);
    public static bool CanRun(UserContext u, ReportTemplateDef d) => CanDesign(u) || d.Config.Roles.Count == 0 || d.Config.Roles.Any(u.Roles.Contains);
    private static void DemandDesigner(UserContext u)
    {
        if (!CanDesign(u)) throw new PermissionDeniedException("Designing report templates is restricted to System Manager.");
    }

    /// <summary>Parameter dari query detail dan header digabung (nama sama = parameter yang sama).</summary>
    public static (string? Error, List<WidgetParameter> Parameters) DetectParameters(string sql, string? headerSql, IReadOnlyList<WidgetParameter>? existing)
    {
        var (a, ps) = DashboardService.DetectParameters(sql, existing);
        if (!a.Ok) return ($"Query detail: {a.Error}", []);
        if (!string.IsNullOrWhiteSpace(headerSql))
        {
            var (h, hp) = DashboardService.DetectParameters(headerSql, existing);
            if (!h.Ok) return ($"Query header: {h.Error}", []);
            ps.AddRange(hp.Where(p => ps.All(x => !x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))));
        }
        // Binding dashboard (filter global) tidak berlaku di laporan: nilai diisi saat membuat laporan.
        foreach (var p in ps.Where(p => p.Binding is not (ParamBindings.Default or ParamBindings.UserId or ParamBindings.UserDepartment)))
            p.Binding = ParamBindings.Default;
        return (null, ps);
    }

    private static Dictionary<string, (object? Value, string Type)> Values(UserContext u, IReadOnlyList<WidgetParameter> ps, IReadOnlyDictionary<string, string?> input)
    {
        var d = new Dictionary<string, (object?, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ps)
        {
            var raw = p.Binding switch
            {
                ParamBindings.UserId => u.Id,
                ParamBindings.UserDepartment => u.Attr("department"),
                _ => input.FirstOrDefault(kv => kv.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Value ?? p.DefaultValue,
            };
            d[p.Name] = (DashboardService.Convert(p, raw), p.Type);
        }
        return d;
    }

    private async Task<QueryResultSet> RunAsync(UserContext u, string sql, Dictionary<string, (object? Value, string Type)> values, int limit, CancellationToken ct)
    {
        var a = SqlGuard.Analyze(sql);
        if (!a.Ok) throw new QueryRejectedException(a.Error!);
        var ps = a.Parameters.Select(n => values.TryGetValue(n, out var v) ? new SqlParam(v.Value, v.Type)
            : throw new QueryRejectedException($"Parameter @{n} is not defined.")).ToList();
        return await executor.ExecuteAsync(u.TenantId, a.ParameterizedSql, ps, limit, ct, limits.StatementTimeout);
    }

    private async Task<ReportPreview> QueryAsync(UserContext u, ReportTemplateConfig c, Dictionary<string, (object?, string)> values, int limit, CancellationToken ct)
    {
        var detail = await RunAsync(u, c.Sql, values, limit, ct);
        var header = string.IsNullOrWhiteSpace(c.HeaderSql) ? null : await RunAsync(u, c.HeaderSql, values, 2, ct);
        if (header is { Rows.Count: > 1 }) throw new QueryRejectedException("Header query must return exactly one row.");
        return new ReportPreview(detail, header);
    }

    public Task<ReportPreview> PreviewAsync(UserContext u, ReportTemplateConfig c, CancellationToken ct = default)
    {
        DemandDesigner(u);
        return QueryAsync(u, c, Values(u, c.Parameters, new Dictionary<string, string?>()), limits.PreviewRows, ct);
    }

    public static TemplateCheck CheckTemplate(ReportTemplateConfig c, byte[] template, ReportPreview preview)
    {
        try
        {
            var pkg = OoxmlPackage.Open(template, c.Format);
            var tags = c.Format == ReportFormats.Xlsx ? XlsxTemplate.Scan(pkg) : DocxTemplate.Scan(pkg);
            var problems = TemplateTags.Validate(tags, preview.Detail.Columns, preview.Header?.Columns, c.Parameters.Select(p => p.Name)).ToList();
            if (tags.Count == 0) problems.Add("Template tidak memuat tag {{…}} atau [[…]] sama sekali");
            return new TemplateCheck(tags, problems);
        }
        catch (TemplateException ex) { return new TemplateCheck([], [ex.Message]); }
    }

    public static byte[] Starter(ReportTemplateConfig c, string title, ReportPreview preview) => c.Format == ReportFormats.Xlsx
        ? XlsxTemplate.Starter(title, preview.Detail.Columns, preview.Header?.Columns)
        : DocxTemplate.Starter(title, preview.Detail.Columns, preview.Header?.Columns);

    private ReportOutput Render(UserContext u, string name, ReportTemplateConfig c, byte[] template, ReportPreview data, Dictionary<string, (object?, string)> values)
    {
        var td = new TemplateData
        {
            Detail = data.Detail, Header = data.Header, Parameters = values,
            UserName = u.FullName, UserId = u.Id, TenantId = u.TenantId, Now = clock.GetLocalNow(),
        };
        var pkg = OoxmlPackage.Open(template, c.Format);
        var bytes = c.Format == ReportFormats.Xlsx ? XlsxTemplate.Render(pkg, td) : DocxTemplate.Render(pkg, td);
        return new ReportOutput(bytes, FileName(c.FileNamePattern, name, td, c.Format), ReportFormats.ContentType(c.Format), data.Detail.Rows.Count);
    }

    private static string FileName(string pattern, string name, TemplateData td, string format)
    {
        var text = string.IsNullOrWhiteSpace(pattern) ? name : pattern;
        try
        {
            text = TemplateTags.ScalarRx.Replace(text, m =>
            {
                var col = m.Groups["col"].Success ? m.Groups["col"].Value : null;
                var tag = new TemplateTag(col is null && !TemplateTags.Aggregates.Contains(m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) ? TagKind.Scalar : TagKind.Aggregate,
                    m.Groups["name"].Value, col, m.Groups["fmt"].Success ? m.Groups["fmt"].Value.Trim() : null, m.Value, "");
                var (v, kind) = td.Scalar(tag);
                return TemplateData.Text(v, kind, tag.Format ?? (kind == "date" ? "yyyy-MM-dd" : null));
            });
        }
        catch (TemplateException) { text = name; }
        text = Regex.Replace(text, @"[\\/:*?""<>|\x00-\x1F]+", "-").Trim(' ', '.', '-');
        return (text.Length == 0 ? "laporan" : text[..Math.Min(text.Length, 120)]) + "." + format;
    }

    /// <summary>Uji coba di wizard: template diisi data pratinjau (maks. PreviewRows baris).</summary>
    public async Task<ReportOutput> TryRenderAsync(UserContext u, string name, ReportTemplateConfig c, byte[] template, CancellationToken ct = default)
    {
        DemandDesigner(u);
        var values = Values(u, c.Parameters, new Dictionary<string, string?>());
        return Render(u, name, c, template, await QueryAsync(u, c, values, limits.PreviewRows, ct), values);
    }

    public async Task<ReportTemplateDef> SaveAsync(UserContext u, ReportTemplateDef def, byte[]? newTemplate, CancellationToken ct = default)
    {
        DemandDesigner(u);
        if (string.IsNullOrWhiteSpace(def.Name)) throw new TemplateException("Report name is required.");
        if (def.Config.Format is not (ReportFormats.Xlsx or ReportFormats.Docx)) throw new TemplateException("Format must be xlsx or docx.");
        var existing = string.IsNullOrEmpty(def.Id) ? null : await store.GetAsync(u.TenantId, def.Id, ct);
        var template = newTemplate ?? (existing is null ? null : await store.GetFileAsync(u.TenantId, def.Id, ct));
        if (template is null || template.Length == 0) throw new TemplateException("Upload a template file first.");
        var (err, _) = DetectParameters(def.Config.Sql, def.Config.HeaderSql, def.Config.Parameters);
        if (err is not null) throw new QueryRejectedException(err);

        // Query dijalankan sekali dan tag dicek terhadap kolomnya: template yang tidak cocok ditolak saat disimpan,
        // bukan baru ketahuan saat pengguna membuat laporan.
        var preview = await QueryAsync(u, def.Config, Values(u, def.Config.Parameters, new Dictionary<string, string?>()), 1, ct);
        var check = CheckTemplate(def.Config, template, preview);
        if (check.Problems.Count > 0) throw new TemplateException("Template does not match query: " + string.Join("; ", check.Problems));

        var now = clock.GetUtcNow();
        def.TenantId = u.TenantId;
        if (existing is null) { def.Id = "rt_" + Guid.NewGuid().ToString("N")[..12]; def.CreatedBy = u.Id; def.CreatedAt = now; def.Version = 1; }
        else { def.CreatedBy = existing.CreatedBy; def.CreatedAt = existing.CreatedAt; def.Version = existing.Version + 1; if (newTemplate is null) def.TemplateFileName = existing.TemplateFileName; }
        def.UpdatedAt = now;
        await store.SaveAsync(def, newTemplate, ct);
        return def;
    }

    public async Task<IReadOnlyList<ReportTemplateDef>> ListAsync(UserContext u, CancellationToken ct = default) =>
        (await store.ListAsync(u.TenantId, ct)).Where(d => CanRun(u, d)).ToList();

    public async Task<ReportTemplateDef?> GetAsync(UserContext u, string id, CancellationToken ct = default) =>
        await store.GetAsync(u.TenantId, id, ct) is { } d && CanRun(u, d) ? d : null;

    public async Task<byte[]?> GetTemplateFileAsync(UserContext u, string id, CancellationToken ct = default)
    {
        DemandDesigner(u);
        return await store.GetFileAsync(u.TenantId, id, ct);
    }

    public async Task DeleteAsync(UserContext u, string id, CancellationToken ct = default)
    {
        DemandDesigner(u);
        await store.DeleteAsync(u.TenantId, id, ct);
    }

    /// <summary>Membuat file laporan. Hasil yang melebihi batas baris DITOLAK (laporan tidak boleh terpotong diam-diam).</summary>
    public async Task<ReportOutput> GenerateAsync(UserContext u, string id, IReadOnlyDictionary<string, string?> input, CancellationToken ct = default)
    {
        var def = await store.GetAsync(u.TenantId, id, ct) ?? throw new TemplateException("Report template not found.");
        if (!CanRun(u, def)) throw new PermissionDeniedException("You do not have access to this report.");
        var file = await store.GetFileAsync(u.TenantId, id, ct) ?? throw new TemplateException("Template file not found.");
        var max = def.Config.Format == ReportFormats.Docx ? limits.MaxRowsWord : limits.MaxRowsExcel;
        var values = Values(u, def.Config.Parameters, input);
        var data = await QueryAsync(u, def.Config, values, max, ct);
        if (data.Detail.Truncated)
            throw new TemplateException($"Results exceed {max:N0} rows. Narrow with parameters (e.g. date range) or split into multiple reports.");
        return Render(u, def.Name, def.Config, file, data, values);
    }
}
