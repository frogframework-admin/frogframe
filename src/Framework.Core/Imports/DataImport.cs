using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Reports;
using Framework.Security;

namespace Framework.Imports;

// ============================================================================
//  Impor Excel/CSV ke DocType. Setiap baris diperlakukan PERSIS seperti mengisi form: lewat DocumentService
//  (permission, cakupan baris, level field, validasi, hook, penomoran, workflow awal, audit, outbox).
// ============================================================================

public sealed class ImportLimits
{
    public int MaxRows { get; set; } = 10_000;
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>Satu kolom file → field DocType (null = diabaikan).</summary>
public sealed class ImportColumn
{
    public int Index { get; init; }
    public string Header { get; init; } = "";
    public string? Field { get; set; }
    public IReadOnlyList<string> Samples { get; init; } = [];
}

public sealed class ImportSheet
{
    public required SheetData Data { get; init; }
    public required List<ImportColumn> Columns { get; init; }
    /// <summary>Indeks kolom "Kunci impor" (penghubung sheet utama ↔ sheet child table), -1 bila tidak ada.</summary>
    public int KeyColumn { get; init; } = -1;
    public int RowNumber(int dataIndex) => Data.FirstRowNumber + 1 + dataIndex; // baris Excel (judul = FirstRowNumber)
    public IEnumerable<(int Index, object?[] Row)> DataRows() => Data.Rows.Skip(1).Select((r, i) => (i, r)).Where(x => x.r.Any(v => v is not null && !(v is string s && s.Trim().Length == 0)));
}

/// <summary>Status wizard impor (disimpan di sirkuit Blazor / permintaan API).</summary>
public sealed class ImportSession
{
    public required string DocType { get; init; }
    public required string FileName { get; init; }
    public required ImportSheet Main { get; init; }
    /// <summary>Nama field Table → sheet child table.</summary>
    public Dictionary<string, ImportSheet> Children { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public int RowCount => Main.DataRows().Count();
}

public sealed record ImportRowResult(int RowNumber, string? Key, bool Ok, IReadOnlyList<string> Errors, string? CreatedName = null);

public sealed record ImportPreview(IReadOnlyList<ImportRowResult> Rows, IReadOnlyList<string> Warnings)
{
    public int Ok => Rows.Count(r => r.Ok);
    public int Failed => Rows.Count(r => !r.Ok);
}

public sealed class ImportLog
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string DocType { get; set; } = "";
    public string FileName { get; set; } = "";
    public bool Atomic { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    /// <summary>Nomor dokumen yang dibuat (maks. 1.000 disimpan di log; selebihnya cukup lewat audit).</summary>
    public List<string> CreatedNames { get; set; } = [];
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public interface IImportLogStore
{
    Task SaveAsync(ImportLog log, CancellationToken ct = default);
    Task<IReadOnlyList<ImportLog>> ListAsync(string tenantId, string docType, int take, CancellationToken ct = default);
}

public sealed class InMemoryImportLogStore : IImportLogStore
{
    private readonly ConcurrentDictionary<string, string> _logs = new();
    public Task SaveAsync(ImportLog log, CancellationToken ct = default) { _logs[log.Id] = JsonSerializer.Serialize(log); return Task.CompletedTask; }
    public Task<IReadOnlyList<ImportLog>> ListAsync(string tenantId, string docType, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportLog>>(_logs.Values.Select(j => JsonSerializer.Deserialize<ImportLog>(j)!)
            .Where(l => l.TenantId == tenantId && l.DocType == docType).OrderByDescending(l => l.At).Take(take).ToList());
}

public sealed record ImportExecution(ImportLog Log, IReadOnlyList<ImportRowResult> Rows);

public sealed partial class DataImportService(DocumentService docs, IMetadataProvider metadata, IPermissionService permissions,
    IImportLogStore logs, TimeProvider clock, ImportLimits limits)
{
    public const string KeyHeader = "Kunci impor";
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");
    public ImportLimits Limits => limits;

    // ---------------- metadata ----------------

    public bool CanImport(UserContext u, string docType) =>
        metadata.Current.DocTypes.TryGetValue(docType, out var m) && !m.Def.IsChild && m.Def.Import.Enabled && permissions.Can(u, m, DocActions.Create);

    private CompiledDocType Meta(UserContext u, string docType)
    {
        if (!metadata.Current.DocTypes.TryGetValue(docType, out var m) || m.Def.IsChild) throw new TemplateException($"DocType {docType} not found.");
        if (!m.Def.Import.Enabled) throw new TemplateException($"Import is disabled for {m.Title}.");
        if (!permissions.Can(u, m, DocActions.Create)) throw new PermissionDeniedException($"You are not allowed to create {m.Title}.");
        return m;
    }

    private static bool Always(string? expr, string value) => string.Equals(expr?.Trim(), value, StringComparison.OrdinalIgnoreCase);
    public static bool IsRequired(CompiledField f) => Always(f.Def.RequiredWhen, "true");

    /// <summary>Field yang boleh diisi lewat impor oleh pengguna ini — sama dengan yang boleh ia isi di form.</summary>
    public IReadOnlyList<CompiledField> Fields(UserContext u, CompiledDocType m, bool child = false) => m.Fields.Where(f =>
        !StandardFields.All.Contains(f.Name) && FieldKinds.IsImportable(f.Def.Type) && !f.IsDerived
        && !Always(f.Def.ReadOnlyWhen, "true") && !Always(f.Def.VisibleWhen, "false")
        && (child || permissions.CanWriteField(u, m, f))).ToList();

    public IReadOnlyList<(CompiledField Table, CompiledDocType Child)> Tables(UserContext u, CompiledDocType m) =>
        m.Fields.Where(f => f.Def.Type == "Table" && !f.IsDerived && permissions.CanWriteField(u, m, f) && f.Def.ChildDocType is not null
                            && metadata.Current.DocTypes.ContainsKey(f.Def.ChildDocType))
            .Select(f => (f, metadata.Current.DocTypes[f.Def.ChildDocType!])).ToList();

    private static string Hint(CompiledField f) => f.Def.Type switch
    {
        "Date" => "Tanggal: sel tanggal Excel, 2026-10-30, atau 30/10/2026",
        "Int" => "Bilangan bulat",
        "Float" or "Currency" => "Angka: sel angka atau teks 1.500.000,50",
        "Check" => "Ya / Tidak",
        "Select" => "Salah satu: " + string.Join(", ", f.Def.Options ?? []),
        "Link" => $"Nomor atau nama {f.Def.LinkTo} yang sudah ada",
        "Email" => "Alamat email",
        "Datetime" => "Tanggal dan jam: sel tanggal-waktu Excel atau 2026-10-30 14:30 (WIB)",
        "Time" => "Jam: 14:30 atau 14:30:00",
        "Duration" => "Durasi: detik, 01:30:00, atau 1h 30m",
        "Percent" => "Persen: 12,5",
        "Phone" => "Nomor telepon: 081234567890 atau +6281234567890",
        "Rating" => $"Rating 0 sampai {f.Def.Max ?? 5}",
        "Color" => "Warna hex, mis. #1f6feb",
        "TableMultiSelect" => $"Beberapa {f.Def.LinkTo} dipisah koma",
        "DynamicLink" => $"Nama dokumen sesuai jenis di kolom {f.Def.LinkToField}",
        "Geolocation" => "Lintang,bujur: -6.2,106.816666",
        _ => f.Def.MaxLength is { } n ? $"Teks (maks. {n} karakter)" : "Teks",
    };

    // ---------------- template ----------------

    public byte[] BuildTemplate(UserContext u, string docType)
    {
        var m = Meta(u, docType);
        var fields = Fields(u, m);
        var tables = Tables(u, m);
        var withKey = tables.Count > 0;
        XlsxSheet SheetFor(string name, IReadOnlyList<CompiledField> fs, bool key) => new()
        {
            Name = name,
            Headers = [.. key ? [KeyHeader] : Array.Empty<string>(), .. fs.Select(f => f.Label + (IsRequired(f) ? " *" : ""))],
            RequiredColumns = fs.Select((f, i) => (f, i)).Where(x => IsRequired(x.f)).Select(x => x.i + (key ? 1 : 0)).ToHashSet(),
            Lists = fs.Select((f, i) => (f, i)).Where(x => x.f.Def.Type is "Select" or "Check")
                .ToDictionary(x => x.i + (key ? 1 : 0), x => (IReadOnlyList<string>)(x.f.Def.Type == "Check" ? ["Ya", "Tidak"] : x.f.Def.Options ?? [])),
        };
        var sheets = new List<XlsxSheet> { SheetFor(SheetName(m.Title), fields, withKey) };
        foreach (var (t, child) in tables) sheets.Add(SheetFor(SheetName(t.Label), Fields(u, child, child: true), true));
        var help = new List<object?[]>
        {
            new object?[] { $"Impor {m.Title}", "", "", "" },
            new object?[] { "Isi satu baris per dokumen di sheet \"" + SheetName(m.Title) + "\". Kolom bertanda * wajib diisi. Baris kosong diabaikan.", "", "", "" },
            new object?[] { withKey ? $"Baris tabel (mis. {string.Join(", ", tables.Select(t => t.Table.Label))}) diisi di sheet masing-masing; kolom \"{KeyHeader}\" menghubungkannya ke baris dokumen (isi bebas: 1, 2, 3, …)." : "", "", "", "" },
            new object?[] { "Dokumen hasil impor dibuat sebagai draft / tahap awal alur persetujuan.", "", "", "" },
            new object?[] { null, null, null, null },
            new object?[] { "Kolom", "Nama field", "Wajib", "Nilai yang diterima" },
        };
        foreach (var f in fields) help.Add([f.Label, f.Name, IsRequired(f) ? "Ya" : "", Hint(f)]);
        foreach (var (t, child) in tables)
            foreach (var f in Fields(u, child, true)) help.Add([$"{t.Label} › {f.Label}", $"{t.Name}.{f.Name}", IsRequired(f) ? "Ya" : "", Hint(f)]);
        sheets.Add(new XlsxSheet { Name = "Petunjuk", Headers = ["Petunjuk", "", "", ""], Rows = help, WrapText = true, Widths = new() { [0] = 48, [1] = 24, [2] = 8, [3] = 60 } });
        return XlsxWriter.Build(sheets);
    }

    private static string SheetName(string s) => Regex.Replace(s, @"[\[\]\*\?/\\:]", " ").Trim() is var x && x.Length > 31 ? x[..31] : x;

    // ---------------- membuka file ----------------

    private static string Norm(string? h) => Regex.Replace((h ?? "").Replace("*", ""), @"\s+", " ").Trim().ToLowerInvariant();

    public ImportSession Open(UserContext u, string docType, string fileName, byte[] file)
    {
        var m = Meta(u, docType);
        var sheets = SpreadsheetReader.Read(file, fileName, limits.MaxFileBytes)
            .Where(s => !Norm(s.Name).Equals("petunjuk") && !s.Name.StartsWith('_') && s.Rows.Count > 0).ToList();
        if (sheets.Count == 0) throw new TemplateException("File contains no data.");
        var tables = Tables(u, m);
        var childSheets = new Dictionary<string, SheetData>();
        foreach (var (t, _) in tables)
            if (sheets.FirstOrDefault(s => Norm(s.Name) == Norm(t.Label) || Norm(s.Name) == Norm(t.Name) || Norm(s.Name) == Norm(SheetName(t.Label))) is { } cs)
                childSheets[t.Name] = cs;
        var main = sheets.FirstOrDefault(s => Norm(s.Name) == Norm(SheetName(m.Title)) || Norm(s.Name) == Norm(m.Name))
                   ?? sheets.First(s => !childSheets.Values.Contains(s));

        var session = new ImportSession { DocType = docType, FileName = fileName, Main = Sheet(main, Fields(u, m)) };
        foreach (var (t, child) in tables)
            if (childSheets.TryGetValue(t.Name, out var cs)) session.Children[t.Name] = Sheet(cs, Fields(u, child, true));
        if (session.RowCount == 0) throw new TemplateException($"Sheet \"{main.Name}\" contains no data rows.");
        if (session.RowCount > limits.MaxRows) throw new TemplateException($"Maximum {limits.MaxRows:N0} rows per import (file contains {session.RowCount:N0}). Split into multiple files.");
        if (session.Children.Count > 0 && session.Main.KeyColumn < 0)
            session.Warnings.Add($"Sheet tabel ditemukan, tetapi sheet utama tidak punya kolom \"{KeyHeader}\" — baris tabel tidak bisa dihubungkan dan diabaikan.");
        var formulas = sheets.Sum(s => s.FormulaCells);
        if (formulas > 0) session.Warnings.Add($"{formulas:N0} sel berisi rumus; nilai hasil hitung yang tersimpan di file yang dipakai.");
        foreach (var s in sheets.Where(s => s != main && !childSheets.Values.Contains(s)))
            session.Warnings.Add($"Sheet \"{s.Name}\" tidak dikenali dan diabaikan.");
        return session;
    }

    private static ImportSheet Sheet(SheetData data, IReadOnlyList<CompiledField> fields)
    {
        var header = data.Rows[0];
        var cols = new List<ImportColumn>();
        var key = -1;
        for (var i = 0; i < header.Length; i++)
        {
            var h = Convert.ToString(header[i], CultureInfo.InvariantCulture) ?? "";
            if (Norm(h) == Norm(KeyHeader)) { key = i; continue; }
            if (h.Trim().Length == 0) continue;
            var match = fields.FirstOrDefault(f => Norm(f.Label) == Norm(h)) ?? fields.FirstOrDefault(f => Norm(f.Name) == Norm(h));
            cols.Add(new ImportColumn
            {
                Index = i, Header = h.Trim(), Field = match?.Name,
                Samples = data.Rows.Skip(1).Select(r => i < r.Length ? r[i] : null).Where(v => v is not null).Take(3).Select(v => Display(v)).ToList(),
            });
        }
        // Satu field hanya boleh dipetakan sekali: kolom berikutnya dengan field yang sama diabaikan.
        foreach (var dup in cols.Where(c => c.Field is not null).GroupBy(c => c.Field).SelectMany(g => g.Skip(1))) dup.Field = null;
        return new ImportSheet { Data = data, Columns = cols, KeyColumn = key };
    }

    public static string Display(object? v) => v switch
    {
        null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : d.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
        decimal n => n.ToString("#,0.##", Id),
        bool b => b ? "Ya" : "Tidak",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Field wajib yang belum dipetakan ke kolom mana pun (impor tidak bisa dilanjutkan).</summary>
    public IReadOnlyList<string> MissingRequired(UserContext u, ImportSession s)
    {
        var m = Meta(u, s.DocType);
        var mapped = s.Main.Columns.Where(c => c.Field is not null).Select(c => c.Field!).ToHashSet();
        var missing = Fields(u, m).Where(f => IsRequired(f) && f.StaticDefault is null && f.DynamicDefault is null && !mapped.Contains(f.Name)).Select(f => f.Label).ToList();
        foreach (var (t, child) in Tables(u, m))
            if (s.Children.TryGetValue(t.Name, out var cs))
            {
                var cm = cs.Columns.Where(c => c.Field is not null).Select(c => c.Field!).ToHashSet();
                missing.AddRange(Fields(u, child, true).Where(f => IsRequired(f) && f.StaticDefault is null && f.DynamicDefault is null && !cm.Contains(f.Name)).Select(f => $"{t.Label} › {f.Label}"));
            }
        return missing;
    }

    // ---------------- konversi nilai ----------------

    private sealed class LinkCache(DocumentService docs, IMetadataProvider metadata, UserContext user)
    {
        private readonly Dictionary<string, (Dictionary<string, string> ByName, Dictionary<string, List<string>> ByTitle)> _targets = new();

        public async Task<(string? Name, string? Error)> ResolveAsync(string target, string value, CancellationToken ct)
        {
            if (!_targets.TryGetValue(target, out var t))
            {
                var meta = metadata.Current.DocTypes[target];
                var titleField = meta.Def.ListView.TitleField;
                var rows = await docs.QueryForReportAsync(user, target, [], DocActions.Read, ct);
                t = (rows.GroupBy(d => d.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Name),
                     titleField is null ? [] : rows.Where(d => d.Data.GetValueOrDefault(titleField) is string).GroupBy(d => ((string)d.Data[titleField]!).Trim().ToLowerInvariant())
                         .ToDictionary(g => g.Key, g => g.Select(d => d.Name).ToList()));
                _targets[target] = t;
            }
            var key = value.Trim().ToLowerInvariant();
            if (t.ByName.TryGetValue(key, out var name)) return (name, null);
            if (t.ByTitle.TryGetValue(key, out var names))
                return names.Count == 1 ? (names[0], null) : (null, $"'{value}' cocok dengan {names.Count} {metadata.Current.DocTypes[target].Title} ({string.Join(", ", names.Take(3))}) — pakai nomornya");
            return (null, $"'{value}' tidak ditemukan di {metadata.Current.DocTypes[target].Title}");
        }
    }

    private static readonly string[] DateTimeFormats =
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "dd/MM/yyyy HH:mm", "d/M/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "d MMM yyyy HH:mm"];

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "yyyy/MM/dd", "d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    public static bool TryNumber(object? v, out decimal d)
    {
        d = 0;
        switch (v)
        {
            case decimal n: d = n; return true;
            case bool: return false;
            case string s:
                s = Regex.Replace(s, @"(?i)\s|rp\.?|idr", "");
                if (s.Length == 0) return false;
                var lastDot = s.LastIndexOf('.'); var lastComma = s.LastIndexOf(',');
                if (lastDot >= 0 && lastComma >= 0)
                    s = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
                else if (lastComma >= 0)
                    s = Regex.IsMatch(s, @"^-?\d{1,3}(,\d{3})+$") ? s.Replace(",", "") : s.Replace(',', '.');
                else if (lastDot >= 0 && Regex.IsMatch(s, @"^-?\d{1,3}(\.\d{3})+$"))
                    s = s.Replace(".", ""); // 1.500.000 = format ribuan Indonesia
                return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
        return false;
    }

    private async Task<(object? Value, string? Error)> ConvertAsync(CompiledField f, object? raw, LinkCache links, CancellationToken ct)
    {
        if (raw is null || raw is string e && e.Trim().Length == 0) return (null, null);
        if (raw is string err && err.StartsWith("#ERROR ")) return (null, $"sel berisi error Excel ({err[7..]})");
        switch (f.Def.Type)
        {
            case "Int":
                return TryNumber(raw, out var i) && i == Math.Truncate(i) ? ((long)i, null) : (null, $"'{Display(raw)}' bukan bilangan bulat");
            case "Float" or "Currency":
                return TryNumber(raw, out var d) ? (d, null) : (null, $"'{Display(raw)}' bukan angka");
            case "Check":
                return raw switch
                {
                    bool b => (b, null),
                    decimal n when n is 0 or 1 => (n == 1, null),
                    string s when Regex.IsMatch(s.Trim(), "^(?i)(ya|y|yes|true|1|✓|v)$") => (true, null),
                    string s when Regex.IsMatch(s.Trim(), "^(?i)(tidak|t|no|n|false|0|-)$") => (false, null),
                    _ => (null, $"'{Display(raw)}' harus Ya atau Tidak"),
                };
            case "Date":
                if (raw is DateTime dt) return (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                if (raw is decimal serial && serial is > 0 and < 2958466) return (DateTime.FromOADate((double)serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                var text = Convert.ToString(raw, CultureInfo.InvariantCulture)!.Trim();
                if (DateTime.TryParseExact(text, DateFormats, Id, DateTimeStyles.None, out var p) || DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out p))
                    return (p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                return (null, $"'{text}' bukan tanggal (pakai 2026-10-30 atau 30/10/2026)");
            case "Datetime":
                // Excel date-time cells and local text are read in the default time zone (Asia/Jakarta).
                if (raw is DateTime ldt) return (ldt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), null);
                if (raw is decimal oa && oa is > 0 and < 2958466) return (DateTime.FromOADate((double)oa).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), null);
                var dtText = Convert.ToString(raw, CultureInfo.InvariantCulture)!.Trim();
                if (DateTime.TryParseExact(dtText, DateTimeFormats, Id, DateTimeStyles.None, out var pdt) || DateTime.TryParseExact(dtText, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out pdt))
                    return (pdt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), null);
                var dtn = f.Type.Normalize(dtText);
                return dtn.Ok ? (dtn.Value, null) : (null, $"'{dtText}' bukan tanggal-waktu (pakai 2026-10-30 14:30)");
            case "Time":
                if (raw is DateTime tdt) return (tdt.ToString("HH:mm:ss", CultureInfo.InvariantCulture), null);
                if (raw is decimal frac && frac is >= 0 and < 1) return (TimeSpan.FromDays((double)frac).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), null);
                return (Display(raw).Trim(), null);
            case "Select":
                var sv = Display(raw).Trim();
                var opt = f.Def.Options?.FirstOrDefault(o => o.Equals(sv, StringComparison.OrdinalIgnoreCase));
                return opt is not null ? (opt, null) : (null, $"'{sv}' bukan pilihan yang valid ({string.Join(", ", f.Def.Options ?? [])})");
            case "Link":
                var (name, error) = await links.ResolveAsync(f.Def.LinkTo!, Display(raw), ct);
                return (name, error);
            default:
                return (raw switch
                {
                    decimal n => n.ToString(CultureInfo.InvariantCulture),
                    DateTime x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    bool b => b ? "Ya" : "Tidak",
                    _ => Convert.ToString(raw, CultureInfo.InvariantCulture)!.Trim(),
                }, null);
        }
    }

    private sealed record Prepared(int RowNumber, string? Key, Dictionary<string, object?>? Payload, List<string> Errors);

    private static string? KeyOf(ImportSheet s, object?[] row) =>
        s.KeyColumn >= 0 && s.KeyColumn < row.Length && row[s.KeyColumn] is { } k ? Display(k).Trim() is { Length: > 0 } x ? x : null : null;

    private async Task<(List<Prepared> Rows, List<string> Warnings)> PrepareAsync(UserContext u, ImportSession s, CancellationToken ct)
    {
        var m = Meta(u, s.DocType);
        var missing = MissingRequired(u, s);
        if (missing.Count > 0) throw new TemplateException("Required columns not mapped: " + string.Join(", ", missing));
        var links = new LinkCache(docs, metadata, u);
        var warnings = new List<string>();

        async Task<(Dictionary<string, object?> Values, List<string> Errors)> RowAsync(ImportSheet sheet, CompiledDocType meta, object?[] row, string prefix)
        {
            var values = new Dictionary<string, object?>();
            var errors = new List<string>();
            foreach (var c in sheet.Columns.Where(c => c.Field is not null))
            {
                var f = meta.ByName[c.Field!];
                var (v, e) = await ConvertAsync(f, c.Index < row.Length ? row[c.Index] : null, links, ct);
                if (e is not null) errors.Add($"{prefix}{c.Header.TrimEnd(' ', '*')}: {e}");
                else if (v is not null) values[f.Name] = v;
            }
            return (values, errors);
        }

        // Baris child table dikelompokkan per kunci impor.
        var childRows = new Dictionary<string, Dictionary<string, List<(int Row, object?[] Data)>>>();
        foreach (var (table, sheet) in s.Children)
        {
            var groups = new Dictionary<string, List<(int, object?[])>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (i, row) in sheet.DataRows())
            {
                var key = KeyOf(sheet, row);
                if (key is null) { warnings.Add($"Sheet \"{sheet.Data.Name}\" baris {sheet.RowNumber(i)}: kolom \"{KeyHeader}\" kosong — diabaikan."); continue; }
                (groups.TryGetValue(key, out var l) ? l : groups[key] = []).Add((sheet.RowNumber(i), row));
            }
            childRows[table] = groups;
        }

        var result = new List<Prepared>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (i, row) in s.Main.DataRows())
        {
            var key = KeyOf(s.Main, row);
            var (values, errors) = await RowAsync(s.Main, m, row, "");
            if (key is not null && !seenKeys.Add(key)) errors.Add($"{KeyHeader} '{key}' dipakai lebih dari satu baris");
            foreach (var (t, child) in Tables(u, m))
            {
                if (key is null || !childRows.TryGetValue(t.Name, out var groups) || !groups.TryGetValue(key, out var lines)) continue;
                var list = new List<Dictionary<string, object?>>();
                foreach (var (rn, data) in lines)
                {
                    var (cv, ce) = await RowAsync(s.Children[t.Name], child, data, $"{t.Label} baris {rn} › ");
                    errors.AddRange(ce);
                    list.Add(cv);
                }
                values[t.Name] = list;
            }
            result.Add(new Prepared(s.Main.RowNumber(i), key, errors.Count == 0 ? values : null, errors));
        }
        foreach (var (table, groups) in childRows)
            foreach (var key in groups.Keys.Where(k => !seenKeys.Contains(k)))
                warnings.Add($"Sheet \"{s.Children[table].Data.Name}\": {KeyHeader} '{key}' tidak ada di sheet utama — {groups[key].Count} baris diabaikan.");
        return (result, warnings);
    }

    private string Label(CompiledDocType m, string path) =>
        m.ByName.TryGetValue(path.Split('.', '[')[0], out var f) ? f.Label : path;

    private IReadOnlyList<string> Errors(CompiledDocType m, Exception ex) => ex switch
    {
        ValidationFailedException v => v.Errors.Select(e => string.IsNullOrEmpty(e.Path) || e.Message.Contains(Label(m, e.Path)) ? e.Message : $"{Label(m, e.Path)}: {e.Message}").ToList(),
        InvalidPayloadException p => [$"{p.Message}: {string.Join(", ", p.Fields)}"],
        BatchRowException b => Errors(m, b.InnerException!),
        _ => [ex.Message],
    };

    // ---------------- simulasi & eksekusi ----------------

    /// <summary>Dry-run: setiap baris dijalankan lewat seluruh aturan DocumentService tanpa disimpan.</summary>
    public async Task<ImportPreview> SimulateAsync(UserContext u, ImportSession s, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var m = Meta(u, s.DocType);
        var (rows, warnings) = await PrepareAsync(u, s, ct);
        var results = new List<ImportRowResult>();
        var names = NamingFieldOf(m) is { } nf ? new Dictionary<string, int>(StringComparer.Ordinal) : null;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Payload is null) { results.Add(new(r.RowNumber, r.Key, false, r.Errors)); continue; }
            try
            {
                var draft = await docs.ValidateInsertAsync(u, s.DocType, r.Payload, ct);
                if (names is not null && !names.TryAdd(draft.Name, r.RowNumber))
                    results.Add(new(r.RowNumber, r.Key, false, [$"Nomor '{draft.Name}' sama dengan baris {names[draft.Name]}"]));
                else results.Add(new(r.RowNumber, r.Key, true, []));
            }
            catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException or InvalidStateException)
            {
                results.Add(new(r.RowNumber, r.Key, false, Errors(m, ex)));
            }
            if (i % 50 == 0) progress?.Report(i);
        }
        return new ImportPreview(results, [.. s.Warnings, .. warnings]);
    }

    private static CompiledField? NamingFieldOf(CompiledDocType m) =>
        m.Def.Naming.Pattern.StartsWith("field:") && m.ByName.TryGetValue(m.Def.Naming.Pattern["field:".Length..], out var cf) ? cf : null;

    /// <summary>
    /// Menjalankan impor. <paramref name="atomic"/> = semua atau tidak sama sekali (satu commit); selain itu baris yang
    /// gagal dilewati dan baris lain tetap tersimpan. Hasil dicatat di riwayat impor.
    /// </summary>
    public async Task<ImportExecution> ExecuteAsync(UserContext u, ImportSession s, bool atomic, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var m = Meta(u, s.DocType);
        var (rows, _) = await PrepareAsync(u, s, ct);
        var log = new ImportLog
        {
            Id = "imp_" + Guid.NewGuid().ToString("N")[..10], TenantId = u.TenantId, DocType = s.DocType, FileName = s.FileName,
            Atomic = atomic, Total = rows.Count, UserId = u.Id, UserName = u.FullName, At = clock.GetUtcNow(),
        };
        var action = $"Dibuat lewat impor #{log.Id}";
        var results = new List<ImportRowResult>();
        if (atomic)
        {
            if (rows.Any(r => r.Payload is null))
                results.AddRange(rows.Select(r => r.Payload is null ? new ImportRowResult(r.RowNumber, r.Key, false, r.Errors)
                    : new ImportRowResult(r.RowNumber, r.Key, false, ["Tidak disimpan: ada baris lain yang gagal (mode semua atau tidak sama sekali)"])));
            else
            {
                try
                {
                    var created = await docs.InsertBatchAsync(u, s.DocType, rows.Select(r => (IDictionary<string, object?>)r.Payload!).ToList(), action, ct);
                    results.AddRange(rows.Select((r, i) => new ImportRowResult(r.RowNumber, r.Key, true, [], created[i].Name)));
                }
                catch (BatchRowException ex)
                {
                    results.AddRange(rows.Select((r, i) => i == ex.Index ? new ImportRowResult(r.RowNumber, r.Key, false, Errors(m, ex))
                        : new ImportRowResult(r.RowNumber, r.Key, false, [$"Tidak disimpan: baris {rows[ex.Index].RowNumber} gagal (mode semua atau tidak sama sekali)"])));
                }
            }
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Payload is null) { results.Add(new(r.RowNumber, r.Key, false, r.Errors)); continue; }
                try
                {
                    var doc = await docs.InsertAsync(u, s.DocType, r.Payload, action, ct);
                    results.Add(new(r.RowNumber, r.Key, true, [], doc.Name));
                }
                catch (Exception ex) when (ex is ValidationFailedException or PermissionDeniedException or InvalidPayloadException or InvalidStateException)
                {
                    results.Add(new(r.RowNumber, r.Key, false, Errors(m, ex)));
                }
                if (i % 25 == 0) progress?.Report(i);
            }
        }
        log.Succeeded = results.Count(r => r.Ok);
        log.Failed = results.Count - log.Succeeded;
        log.CreatedNames = results.Where(r => r.CreatedName is not null).Select(r => r.CreatedName!).Take(1000).ToList();
        await logs.SaveAsync(log, ct);
        return new ImportExecution(log, results);
    }

    public Task<IReadOnlyList<ImportLog>> HistoryAsync(UserContext u, string docType, CancellationToken ct = default) =>
        logs.ListAsync(u.TenantId, docType, 20, ct);

    /// <summary>File berisi baris yang gagal (nilai asli + kolom "Alasan"), siap diperbaiki dan diimpor ulang.</summary>
    public static byte[] FailureReport(ImportSession s, IReadOnlyList<ImportRowResult> results)
    {
        var failed = results.Where(r => !r.Ok).ToDictionary(r => r.RowNumber);
        var keys = results.Where(r => !r.Ok && r.Key is not null).Select(r => r.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        XlsxSheet Copy(ImportSheet sheet, Func<int, object?[], bool> include, Func<int, string?>? reason)
        {
            var headers = sheet.Data.Rows[0].Select(h => Convert.ToString(h, CultureInfo.InvariantCulture) ?? "").ToList();
            if (reason is not null) headers.Add("Alasan");
            return new XlsxSheet
            {
                Name = sheet.Data.Name, Headers = headers,
                Rows = sheet.DataRows().Where(x => include(x.Index, x.Row))
                    .Select(x => reason is null ? x.Row : [.. x.Row, .. new object?[headers.Count - 1 - x.Row.Length], reason(sheet.RowNumber(x.Index))]).ToList(),
                Widths = reason is null ? [] : new() { [headers.Count - 1] = 70 },
            };
        }
        var sheets = new List<XlsxSheet> { Copy(s.Main, (i, _) => failed.ContainsKey(s.Main.RowNumber(i)), rn => string.Join("; ", failed[rn].Errors)) };
        foreach (var (_, child) in s.Children)
            sheets.Add(Copy(child, (_, row) => KeyOf(child, row) is { } k && keys.Contains(k), null));
        return XlsxWriter.Build(sheets);
    }
}
