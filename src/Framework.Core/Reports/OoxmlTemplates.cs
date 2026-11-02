using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Framework.Dashboards;

namespace Framework.Reports;

// ============================================================================
//  Mesin template Excel (.xlsx) & Word (.docx) tanpa library pihak ketiga.
//  File Office Open XML adalah ZIP berisi XML; diproses langsung dengan System.IO.Compression + System.Xml.Linq.
//  Tag:  {{Nama}}            data tunggal (parameter → query header → baris pertama detail → tag sistem)
//        {{Sum:Kolom}}        agregat detail (Sum, Avg, Min, Max, Count)
//        [[Kolom]] / [[No]]   perulangan: baris Excel / baris tabel Word / paragraf Word diulang per baris data
//        ...|format           format opsional, mis. {{Tanggal|dd MMMM yyyy}}, [[Gaji|N0]]
// ============================================================================

public sealed class TemplateException(string message) : Exception(message);

public static class ReportFormats
{
    public const string Xlsx = "xlsx", Docx = "docx";
    public static string ContentType(string f) => f == Xlsx
        ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
}

public enum TagKind { Scalar, Aggregate, Loop }

public sealed record TemplateTag(TagKind Kind, string Name, string? Column, string? Format, string Raw, string Location);

public static class TemplateTags
{
    public static readonly Regex ScalarRx = new(@"\{\{\s*(?<name>[\p{L}_][\p{L}\p{N}_]*)(?:\s*:\s*(?<col>[\p{L}_][\p{L}\p{N}_]*))?\s*(?:\|(?<fmt>[^}]*))?\}\}", RegexOptions.Compiled);
    public static readonly Regex LoopRx = new(@"\[\[\s*(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*(?:\|(?<fmt>[^\]]*))?\]\]", RegexOptions.Compiled);
    public static readonly string[] Aggregates = ["Sum", "Avg", "Min", "Max", "Count"];
    public static readonly string[] System = ["Today", "Now", "User", "UserId", "Tenant", "RowCount"];
    public const string RowNumber = "No";

    public static IEnumerable<TemplateTag> Find(string text, string location)
    {
        foreach (Match m in ScalarRx.Matches(text))
        {
            var col = m.Groups["col"].Success ? m.Groups["col"].Value : null;
            yield return new TemplateTag(col is null && !Aggregates.Contains(m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) ? TagKind.Scalar : TagKind.Aggregate,
                m.Groups["name"].Value, col, Fmt(m), m.Value, location);
        }
        foreach (Match m in LoopRx.Matches(text))
            yield return new TemplateTag(TagKind.Loop, m.Groups["name"].Value, null, Fmt(m), m.Value, location);
    }

    private static string? Fmt(Match m) => m.Groups["fmt"].Success && m.Groups["fmt"].Value.Trim().Length > 0 ? m.Groups["fmt"].Value.Trim() : null;

    /// <summary>Mencocokkan tag dengan kolom hasil query & parameter. Mengembalikan daftar masalah (kosong = valid).</summary>
    public static IReadOnlyList<string> Validate(IEnumerable<TemplateTag> tags, IReadOnlyList<ResultColumn> detail,
        IReadOnlyList<ResultColumn>? header, IEnumerable<string> parameters)
    {
        var problems = new List<string>();
        bool Has(IEnumerable<ResultColumn>? cols, string n) => cols?.Any(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) == true;
        var ps = parameters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags.DistinctBy(t => (t.Kind, t.Name.ToLowerInvariant(), t.Column?.ToLowerInvariant())))
        {
            switch (t.Kind)
            {
                case TagKind.Scalar when !ps.Contains(t.Name) && !Has(header, t.Name) && !Has(detail, t.Name) && !System.Contains(t.Name, StringComparer.OrdinalIgnoreCase):
                    problems.Add($"{t.Raw} ({t.Location}): tidak ada parameter, kolom query, atau tag sistem bernama '{t.Name}'");
                    break;
                case TagKind.Aggregate when !Aggregates.Contains(t.Name, StringComparer.OrdinalIgnoreCase):
                    problems.Add($"{t.Raw} ({t.Location}): fungsi '{t.Name}' tidak dikenal (pakai {string.Join(", ", Aggregates)})");
                    break;
                case TagKind.Aggregate when t.Column is not null && !Has(detail, t.Column):
                    problems.Add($"{t.Raw} ({t.Location}): kolom '{t.Column}' tidak ada di hasil query detail");
                    break;
                case TagKind.Aggregate when !t.Name.Equals("Count", StringComparison.OrdinalIgnoreCase)
                                            && (t.Column is null || detail.First(c => c.Name.Equals(t.Column, StringComparison.OrdinalIgnoreCase)).Kind != "number"):
                    problems.Add($"{t.Raw} ({t.Location}): {t.Name} butuh kolom angka");
                    break;
                case TagKind.Loop when !Has(detail, t.Name) && !t.Name.Equals(RowNumber, StringComparison.OrdinalIgnoreCase):
                    problems.Add($"{t.Raw} ({t.Location}): kolom '{t.Name}' tidak ada di hasil query detail");
                    break;
            }
        }
        return problems;
    }
}

/// <summary>Data untuk mengisi template. Kolom dicocokkan tanpa membedakan huruf besar/kecil.</summary>
public sealed class TemplateData
{
    public required QueryResultSet Detail { get; init; }
    public QueryResultSet? Header { get; init; }
    /// <summary>Nilai parameter yang sudah bertipe (string, decimal, DateOnly, bool, null) beserta tipe parameternya.</summary>
    public IReadOnlyDictionary<string, (object? Value, string Type)> Parameters { get; init; } = new Dictionary<string, (object?, string)>();
    public string UserName { get; init; } = "";
    public string UserId { get; init; } = "";
    public string TenantId { get; init; } = "";
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    private static int Col(QueryResultSet? r, string name) =>
        r is null ? -1 : r.Columns.Select((c, i) => (c, i)).FirstOrDefault(x => x.c.Name.Equals(name, StringComparison.OrdinalIgnoreCase), (null!, -1)).Item2;

    /// <summary>Nilai + jenis (number | date | text | boolean) untuk tag tunggal/agregat.</summary>
    public (object? Value, string Kind) Scalar(TemplateTag t)
    {
        if (t.Kind == TagKind.Aggregate) return Aggregate(t);
        var p = Parameters.FirstOrDefault(kv => kv.Key.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        if (p.Key is not null)
            return (p.Value.Value, p.Value.Type switch { ParamTypes.Number => "number", ParamTypes.Date => "date", ParamTypes.Boolean => "boolean", _ => "text" });
        foreach (var src in new[] { Header, Detail })
        {
            var i = Col(src, t.Name);
            if (i >= 0) return (src!.Rows.Count > 0 ? src.Rows[0][i] : null, src.Columns[i].Kind);
        }
        return t.Name.ToLowerInvariant() switch
        {
            "today" => (DateOnly.FromDateTime(Now.DateTime), "date"),
            "now" => (Now.DateTime, "date"),
            "user" => (UserName, "text"),
            "userid" => (UserId, "text"),
            "tenant" => (TenantId, "text"),
            "rowcount" => ((decimal)Detail.Rows.Count, "number"),
            _ => throw new TemplateException($"{t.Raw}: value not found"),
        };
    }

    private (object? Value, string Kind) Aggregate(TemplateTag t)
    {
        var fn = t.Name.ToLowerInvariant();
        if (fn == "count") return ((decimal)Detail.Rows.Count, "number");
        var i = Col(Detail, t.Column ?? "");
        if (i < 0) throw new TemplateException($"{t.Raw}: column '{t.Column}' not found in query results");
        var nums = Detail.Rows.Select(r => r[i]).OfType<decimal>().ToList();
        if (nums.Count == 0) return (fn == "sum" ? 0m : null, "number");
        return (fn switch { "sum" => nums.Sum(), "avg" => nums.Average(), "min" => nums.Min(), "max" => nums.Max(), _ => throw new TemplateException($"{t.Raw}: unknown function") }, "number");
    }

    public (object? Value, string Kind) Loop(TemplateTag t, int row)
    {
        var i = Col(Detail, t.Name);
        if (i >= 0) return (row < Detail.Rows.Count ? Detail.Rows[row][i] : null, Detail.Columns[i].Kind);
        if (t.Name.Equals(TemplateTags.RowNumber, StringComparison.OrdinalIgnoreCase)) return (row < Detail.Rows.Count ? (decimal)(row + 1) : null, "number");
        throw new TemplateException($"{t.Raw}: column '{t.Name}' not found in query results");
    }

    // ---------- format teks (Word, dan sel Excel yang berisi teks campuran / format eksplisit) ----------
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");

    public static DateTime? AsDate(object? v) => v switch
    {
        DateTime d => d,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset o => o.DateTime,
        string s when DateTime.TryParseExact(s, ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null,
    };

    public static string Text(object? v, string kind, string? fmt)
    {
        if (v is null) return "";
        try
        {
            if (kind == "date" && AsDate(v) is { } d)
                return d.ToString(fmt ?? (d.TimeOfDay == TimeSpan.Zero ? "dd MMMM yyyy" : "dd MMMM yyyy HH:mm"), Id);
            return v switch
            {
                decimal n => n.ToString(fmt ?? (n == Math.Round(n) ? "#,0" : "#,0.##"), Id),
                bool b => b ? "Ya" : "Tidak",
                _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
            };
        }
        catch (FormatException) { throw new TemplateException($"Format '{fmt}' is invalid for value {v}"); }
    }
}

// ============================================================================
//  Paket ZIP Office yang dibaca dengan aman
// ============================================================================

public sealed class OoxmlPackage
{
    public const long MaxTemplateBytes = 5 * 1024 * 1024;
    public const int MaxEntries = 2_000;
    public const long MaxUncompressedBytes = 80 * 1024 * 1024;

    private readonly List<(string Name, byte[] Data)> _entries = [];
    public IEnumerable<string> Names => _entries.Select(e => e.Name);

    public static OoxmlPackage Open(byte[] bytes, string expectedFormat, long? maxBytes = null)
    {
        var limit = maxBytes ?? MaxTemplateBytes;
        if (bytes.Length > limit) throw new TemplateException($"File size exceeds maximum limit of {limit / 1024 / 1024} MB");
        var pkg = new OoxmlPackage();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw new TemplateException("Template contains too many parts");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                // Batas dihitung dari byte yang benar-benar didekompresi (tidak percaya ukuran di header ZIP): pertahanan zip bomb.
                while ((n = s.Read(buf)) > 0)
                {
                    total += n;
                    if (total > MaxUncompressedBytes) throw new TemplateException("Template uncompressed size is too large (file may be corrupted or malicious)");
                    ms.Write(buf, 0, n);
                }
                pkg._entries.Add((e.FullName, ms.ToArray()));
            }
        }
        catch (InvalidDataException) { throw new TemplateException("File is not a valid Office document (.xlsx/.docx)"); }

        var main = expectedFormat == ReportFormats.Xlsx ? "xl/workbook.xml" : "word/document.xml";
        if (!pkg.Has(main) || !pkg.Has("[Content_Types].xml"))
            throw new TemplateException(expectedFormat == ReportFormats.Xlsx ? "File is not an Excel workbook (.xlsx)" : "File is not a Word document (.docx)");
        if (pkg.Names.Any(n => n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)) ||
            Encoding.UTF8.GetString(pkg.Get("[Content_Types].xml")!).Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
            throw new TemplateException("Template contains VBA macros and is rejected. Please save as standard .xlsx/.docx without macros.");
        return pkg;
    }

    public bool Has(string name) => _entries.Any(e => e.Name == name);
    public byte[]? Get(string name) => _entries.FirstOrDefault(e => e.Name == name).Data;
    public void Remove(string name) => _entries.RemoveAll(e => e.Name == name);
    public void Set(string name, byte[] data)
    {
        var i = _entries.FindIndex(e => e.Name == name);
        if (i >= 0) _entries[i] = (name, data); else _entries.Add((name, data));
    }

    /// <summary>XML diparse tanpa DTD dan tanpa resolver eksternal (mencegah XXE / entity expansion).</summary>
    public XDocument Xml(string name)
    {
        var data = Get(name) ?? throw new TemplateException($"Part '{name}' not found in template");
        try
        {
            using var r = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(r, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) { throw new TemplateException($"Template is corrupted or unsafe ({name}): {ex.Message}"); }
    }

    public void SetXml(string name, XDocument doc)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            doc.Save(w);
        Set(name, ms.ToArray());
    }

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data) in _entries.OrderBy(e => e.Name == "[Content_Types].xml" ? 0 : 1))
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    public static OoxmlPackage FromParts(IEnumerable<(string Name, string Xml)> parts)
    {
        var p = new OoxmlPackage();
        foreach (var (n, x) in parts) p._entries.Add((n, new UTF8Encoding(false).GetBytes(x)));
        return p;
    }
}

// ============================================================================
//  Excel
// ============================================================================

public static class XlsxTemplate
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace XmlNs = XNamespace.Xml;

    private sealed record Sheet(string Name, string Path);

    private static List<Sheet> Sheets(OoxmlPackage pkg)
    {
        var wb = pkg.Xml("xl/workbook.xml");
        var rels = pkg.Xml("xl/_rels/workbook.xml.rels").Root!.Elements(PR + "Relationship").ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        return wb.Root!.Element(S + "sheets")!.Elements(S + "sheet").Select(s =>
        {
            var target = rels[(string)s.Attribute(R + "id")!];
            return new Sheet((string)s.Attribute("name")!, target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target);
        }).ToList();
    }

    private static List<string> SharedStrings(OoxmlPackage pkg) => !pkg.Has("xl/sharedStrings.xml") ? []
        : pkg.Xml("xl/sharedStrings.xml").Root!.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value))).ToList();

    private static string? CellText(XElement c, List<string> sst)
    {
        if (c.Element(S + "f") is not null) return null; // tag di dalam rumus tidak didukung
        return (string?)c.Attribute("t") switch
        {
            "s" => int.TryParse(c.Element(S + "v")?.Value, out var i) && i < sst.Count ? sst[i] : null,
            "inlineStr" => string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []),
            "str" => c.Element(S + "v")?.Value,
            _ => null,
        };
    }

    public static IReadOnlyList<TemplateTag> Scan(OoxmlPackage pkg)
    {
        var sst = SharedStrings(pkg);
        var tags = new List<TemplateTag>();
        foreach (var sh in Sheets(pkg))
            foreach (var c in pkg.Xml(sh.Path).Descendants(S + "c"))
                if (CellText(c, sst) is { } text)
                    tags.AddRange(TemplateTags.Find(text, $"{sh.Name}!{(string?)c.Attribute("r")}"));
        return tags;
    }

    // ---------- referensi sel & rumus ----------
    public static int ColIndex(string letters) => letters.Aggregate(0, (a, ch) => a * 26 + (ch - 'A' + 1));
    public static string ColLetters(int idx)
    {
        var s = "";
        while (idx > 0) { var m = (idx - 1) % 26; s = (char)('A' + m) + s; idx = (idx - 1) / 26; }
        return s;
    }
    private static readonly Regex CellRefRx = new(@"^(?<c>[A-Z]{1,3})(?<r>\d+)$");
    private static (int Col, int Row) ParseRef(string r) { var m = CellRefRx.Match(r); return (ColIndex(m.Groups["c"].Value), int.Parse(m.Groups["r"].Value)); }

    private static readonly Regex RefRx = new(
        @"(?<![A-Za-z0-9_.$!'""])(?:(?<sheet>'(?:[^']|'')+'|[A-Za-z_][A-Za-z0-9_.]*)!)?(?<ca>\$?)(?<c1>[A-Z]{1,3})(?<ra>\$?)(?<r1>\d+)(?::(?<cb>\$?)(?<c2>[A-Z]{1,3})(?<rb>\$?)(?<r2>\d+))?(?![A-Za-z0-9_(])",
        RegexOptions.Compiled);

    /// <summary>Menerapkan pemetaan ke referensi A1 dalam rumus, di luar string literal; hanya referensi sheet ini.</summary>
    private static string MapRefs(string formula, string sheet, Func<(int c1, int r1, int c2, int r2, bool ca, bool ra, bool cb, bool rb, bool range), (int c1, int r1, int c2, int r2)> map)
    {
        var parts = Regex.Split(formula, "(\"(?:[^\"]|\"\")*\")");
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = RefRx.Replace(parts[i], m =>
            {
                if (m.Groups["sheet"].Success)
                {
                    var name = m.Groups["sheet"].Value;
                    if (name.StartsWith('\'')) name = name[1..^1].Replace("''", "'");
                    if (!name.Equals(sheet, StringComparison.OrdinalIgnoreCase)) return m.Value;
                }
                var range = m.Groups["c2"].Success;
                var src = (ColIndex(m.Groups["c1"].Value), int.Parse(m.Groups["r1"].Value),
                           range ? ColIndex(m.Groups["c2"].Value) : 0, range ? int.Parse(m.Groups["r2"].Value) : 0,
                           m.Groups["ca"].Value == "$", m.Groups["ra"].Value == "$", m.Groups["cb"].Value == "$", m.Groups["rb"].Value == "$", range);
                var (c1, r1, c2, r2) = map(src);
                var prefix = m.Groups["sheet"].Success ? m.Groups["sheet"].Value + "!" : "";
                var a = $"{m.Groups["ca"].Value}{ColLetters(Math.Max(1, c1))}{m.Groups["ra"].Value}{Math.Max(1, r1)}";
                return prefix + a + (range ? $":{m.Groups["cb"].Value}{ColLetters(Math.Max(1, c2))}{m.Groups["rb"].Value}{Math.Max(1, r2)}" : "");
            });
        return string.Concat(parts);
    }

    /// <summary>Penyisipan <paramref name="delta"/> baris setelah baris T (aturan Excel): referensi di bawah T bergeser,
    /// rentang yang memuat T melebar.</summary>
    private static string InsertShift(string f, string sheet, int t, int delta) =>
        MapRefs(f, sheet, x => x.range
            ? (x.c1, x.r1 > t ? x.r1 + delta : x.r1, x.c2, x.r2 >= t ? x.r2 + delta : x.r2)
            : (x.c1, x.r1 > t ? x.r1 + delta : x.r1, 0, 0));

    /// <summary>Menyalin rumus ke sel lain (seperti fill-down Excel): referensi relatif ikut bergeser.</summary>
    private static string Translate(string f, string sheet, int dRow, int dCol) =>
        MapRefs(f, sheet, x => (x.ca ? x.c1 : x.c1 + dCol, x.ra ? x.r1 : x.r1 + dRow, x.cb ? x.c2 : x.c2 + dCol, x.rb ? x.r2 : x.r2 + dRow));

    private static string InsertShiftRanges(string sqref, int t, int delta) => string.Join(" ", sqref.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r =>
    {
        var ab = r.Split(':');
        var (c1, r1) = ParseRef(ab[0]);
        var (c2, r2) = ab.Length > 1 ? ParseRef(ab[1]) : (c1, r1);
        r1 = r1 > t ? r1 + delta : r1;
        r2 = ab.Length > 1 ? (r2 >= t ? r2 + delta : r2) : (r2 > t ? r2 + delta : r2);
        return ab.Length > 1 ? $"{ColLetters(c1)}{r1}:{ColLetters(c2)}{r2}" : $"{ColLetters(c1)}{r1}";
    }));

    // ---------- gaya tanggal ----------
    private sealed class Styles(OoxmlPackage pkg)
    {
        private XDocument? _doc;
        private readonly Dictionary<int, int> _dateOf = [];
        private XDocument Doc => _doc ??= pkg.Xml("xl/styles.xml");
        public bool Changed { get; private set; }

        private bool IsDateFormat(int numFmtId)
        {
            if (numFmtId is >= 14 and <= 22 or >= 45 and <= 47) return true;
            var custom = Doc.Root!.Element(S + "numFmts")?.Elements(S + "numFmt").FirstOrDefault(n => (int)n.Attribute("numFmtId")! == numFmtId);
            if (custom is null) return false;
            var code = Regex.Replace((string)custom.Attribute("formatCode")!, "\"[^\"]*\"|\\[[^\\]]*\\]", "");
            return code.IndexOfAny(['d', 'y', 'D', 'Y']) >= 0;
        }

        /// <summary>Indeks gaya yang menampilkan tanggal: gaya sel template dipakai bila sudah bertipe tanggal, selain itu disalin dengan format tanggal.</summary>
        public int DateStyle(int s)
        {
            if (!pkg.Has("xl/styles.xml")) return s;
            if (_dateOf.TryGetValue(s, out var cached)) return cached;
            var xfs = Doc.Root!.Element(S + "cellXfs")!;
            var list = xfs.Elements(S + "xf").ToList();
            var xf = s < list.Count ? list[s] : list[0];
            if (IsDateFormat((int?)xf.Attribute("numFmtId") ?? 0)) return _dateOf[s] = s;
            var copy = new XElement(xf);
            copy.SetAttributeValue("numFmtId", 14);
            copy.SetAttributeValue("applyNumberFormat", 1);
            xfs.Add(copy);
            xfs.SetAttributeValue("count", list.Count + 1);
            Changed = true;
            return _dateOf[s] = list.Count;
        }

        public void Save() { if (Changed) pkg.SetXml("xl/styles.xml", Doc); }
    }

    // ---------- render ----------
    public static byte[] Render(OoxmlPackage pkg, TemplateData data)
    {
        var sst = SharedStrings(pkg);
        var styles = new Styles(pkg);
        var wb = pkg.Xml("xl/workbook.xml");
        var defined = wb.Root!.Element(S + "definedNames")?.Elements(S + "definedName").ToList() ?? [];
        foreach (var sh in Sheets(pkg))
        {
            var doc = pkg.Xml(sh.Path);
            var ws = doc.Root!;
            var sheetData = ws.Element(S + "sheetData")!;
            DeShare(sheetData, sh.Name);
            var ops = new List<(int T, int Delta)>();

            while (sheetData.Elements(S + "row").FirstOrDefault(r => r.Elements(S + "c").Any(c => CellText(c, sst)?.Contains("[[") == true)) is { } template)
            {
                var t = (int)template.Attribute("r")!;
                var count = Math.Max(data.Detail.Rows.Count, 1); // tanpa data: satu baris kosong (rumus total tetap valid)
                var delta = count - 1;
                var rowMerges = ws.Element(S + "mergeCells")?.Elements(S + "mergeCell")
                    .Where(m => { var ab = ((string)m.Attribute("ref")!).Split(':'); return ab.Length == 2 && ParseRef(ab[0]).Row == t && ParseRef(ab[1]).Row == t; }).ToList() ?? [];
                if (delta > 0)
                {
                    ops.Add((t, delta));
                    foreach (var row in sheetData.Elements(S + "row").Where(r => (int)r.Attribute("r")! > t).ToList())
                    {
                        var nr = (int)row.Attribute("r")! + delta;
                        row.SetAttributeValue("r", nr);
                        foreach (var c in row.Elements(S + "c")) { var (col, _) = ParseRef((string)c.Attribute("r")!); c.SetAttributeValue("r", ColLetters(col) + nr); }
                    }
                    foreach (var f in sheetData.Descendants(S + "f")) f.Value = InsertShift(f.Value, sh.Name, t, delta);
                    foreach (var m in ws.Element(S + "mergeCells")?.Elements(S + "mergeCell").Except(rowMerges) ?? [])
                        m.SetAttributeValue("ref", InsertShiftRanges((string)m.Attribute("ref")!, t, delta));
                    foreach (var el in ws.Descendants().Where(e => e.Name.LocalName is "conditionalFormatting" or "dataValidation" && e.Attribute("sqref") is not null))
                        el.SetAttributeValue("sqref", InsertShiftRanges((string)el.Attribute("sqref")!, t, delta));
                    foreach (var el in ws.Descendants().Where(e => e.Name.LocalName is "hyperlink" or "autoFilter" && e.Attribute("ref") is not null))
                        el.SetAttributeValue("ref", InsertShiftRanges((string)el.Attribute("ref")!, t, delta));
                }
                var clones = new List<XElement>();
                for (var k = 0; k < count; k++)
                {
                    var row = new XElement(template);
                    row.SetAttributeValue("r", t + k);
                    foreach (var c in row.Elements(S + "c"))
                    {
                        var (col, _) = ParseRef((string)c.Attribute("r")!);
                        c.SetAttributeValue("r", ColLetters(col) + (t + k));
                        if (c.Element(S + "f") is { } f && k > 0) f.Value = Translate(f.Value, sh.Name, k, 0);
                        if (CellText(c, sst) is { } text && text.Contains("[["))
                        {
                            var idx = k < data.Detail.Rows.Count ? k : int.MaxValue;
                            Fill(c, text, TemplateTags.LoopRx, m => data.Loop(Tag(m, TagKind.Loop), idx), styles);
                        }
                    }
                    clones.Add(row);
                }
                template.ReplaceWith(clones);
                foreach (var m in rowMerges)
                {
                    var ab = ((string)m.Attribute("ref")!).Split(':');
                    var (c1, _) = ParseRef(ab[0]); var (c2, _) = ParseRef(ab[1]);
                    for (var k = 1; k < count; k++) m.Parent!.Add(new XElement(S + "mergeCell", new XAttribute("ref", $"{ColLetters(c1)}{t + k}:{ColLetters(c2)}{t + k}")));
                }
                if (ws.Element(S + "mergeCells") is { } mc) mc.SetAttributeValue("count", mc.Elements(S + "mergeCell").Count());
            }

            foreach (var c in sheetData.Descendants(S + "c").ToList())
                if (CellText(c, sst) is { } text && text.Contains("{{"))
                    Fill(c, text, TemplateTags.ScalarRx, m => data.Scalar(Tag(m, TemplateTags.Aggregates.Contains(m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) ? TagKind.Aggregate : TagKind.Scalar)), styles);

            // Nilai rumus yang tersimpan sudah usang: dihapus, Excel menghitung ulang saat dibuka (fullCalcOnLoad).
            foreach (var c in sheetData.Descendants(S + "c").Where(c => c.Element(S + "f") is not null)) { c.Element(S + "v")?.Remove(); if ((string?)c.Attribute("t") is "str" or "e") c.Attribute("t")!.Remove(); }

            var maxRow = sheetData.Elements(S + "row").Select(r => (int)r.Attribute("r")!).DefaultIfEmpty(1).Max();
            if (ws.Element(S + "dimension") is { } dim && ((string)dim.Attribute("ref")!).Split(':') is { Length: 2 } ab2)
            {
                var (c2, _) = ParseRef(ab2[1]);
                dim.SetAttributeValue("ref", $"{ab2[0]}:{ColLetters(c2)}{maxRow}");
            }
            foreach (var (t, delta) in ops)
            {
                foreach (var dn in defined) dn.Value = InsertShift(dn.Value, sh.Name, t, delta);
                ShiftTables(pkg, sh.Path, t, delta);
            }
            pkg.SetXml(sh.Path, doc);
        }

        var calc = wb.Root!.Element(S + "calcPr");
        if (calc is null) wb.Root!.Add(calc = new XElement(S + "calcPr"));
        calc.SetAttributeValue("fullCalcOnLoad", 1);
        pkg.SetXml("xl/workbook.xml", wb);
        RemoveCalcChain(pkg);
        styles.Save();
        return pkg.Save();
    }

    private static TemplateTag Tag(Match m, TagKind kind) => new(kind, m.Groups["name"].Value,
        m.Groups["col"].Success ? m.Groups["col"].Value : null,
        m.Groups["fmt"].Success && m.Groups["fmt"].Value.Trim().Length > 0 ? m.Groups["fmt"].Value.Trim() : null, m.Value, "");

    /// <summary>Isi sel: bila sel hanya berisi satu tag tanpa format → nilai bertipe (angka/tanggal/boolean tetap bisa dihitung);
    /// selain itu teks hasil penggantian. Teks selalu ditulis sebagai inline string — nilai "=…" tidak pernah menjadi rumus.</summary>
    private static void Fill(XElement c, string text, Regex rx, Func<Match, (object? Value, string Kind)> resolve, Styles styles)
    {
        var whole = rx.Match(text.Trim());
        if (whole.Success && whole.Value.Length == text.Trim().Length && !whole.Groups["fmt"].Success)
        {
            var (v, kind) = resolve(whole);
            SetTyped(c, v, kind, styles);
            return;
        }
        SetString(c, rx.Replace(text, m => { var (v, kind) = resolve(m); return TemplateData.Text(v, kind, m.Groups["fmt"].Success ? m.Groups["fmt"].Value.Trim() : null); }));
    }

    private static void Clear(XElement c)
    {
        c.Elements(S + "v").Remove(); c.Elements(S + "is").Remove(); c.Attribute("t")?.Remove();
    }

    private static void SetString(XElement c, string s)
    {
        Clear(c);
        if (s.Length == 0) return;
        c.SetAttributeValue("t", "inlineStr");
        c.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XmlNs + "space", "preserve"), s)));
    }

    private static void SetTyped(XElement c, object? v, string kind, Styles styles)
    {
        Clear(c);
        if (v is null) return;
        if (kind == "date" && TemplateData.AsDate(v) is { } d)
        {
            c.Add(new XElement(S + "v", (d - new DateTime(1899, 12, 30)).TotalDays.ToString(CultureInfo.InvariantCulture)));
            c.SetAttributeValue("s", styles.DateStyle((int?)c.Attribute("s") ?? 0));
            return;
        }
        switch (v)
        {
            case decimal n: c.Add(new XElement(S + "v", n.ToString(CultureInfo.InvariantCulture))); break;
            case bool b: c.SetAttributeValue("t", "b"); c.Add(new XElement(S + "v", b ? 1 : 0)); break;
            default: SetString(c, Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    /// <summary>Shared formula diubah menjadi rumus biasa per sel agar bisa disalin/digeser dengan benar.</summary>
    private static void DeShare(XElement sheetData, string sheet)
    {
        var masters = new Dictionary<string, (int Col, int Row, string F)>();
        foreach (var c in sheetData.Descendants(S + "c"))
            if (c.Element(S + "f") is { } f && (string?)f.Attribute("t") == "shared" && f.Value.Length > 0)
            {
                var (col, row) = ParseRef((string)c.Attribute("r")!);
                masters[(string)f.Attribute("si")!] = (col, row, f.Value);
            }
        foreach (var c in sheetData.Descendants(S + "c"))
            if (c.Element(S + "f") is { } f && (string?)f.Attribute("t") == "shared")
            {
                if (f.Value.Length == 0 && masters.TryGetValue((string)f.Attribute("si")!, out var m))
                {
                    var (col, row) = ParseRef((string)c.Attribute("r")!);
                    f.Value = Translate(m.F, sheet, row - m.Row, col - m.Col);
                }
                f.Attribute("t")?.Remove(); f.Attribute("si")?.Remove(); f.Attribute("ref")?.Remove();
            }
    }

    /// <summary>Excel Table (ListObject) di sheet ikut melebar agar format & filter tabel mencakup baris baru.</summary>
    private static void ShiftTables(OoxmlPackage pkg, string sheetPath, int t, int delta)
    {
        var relsPath = Path.GetDirectoryName(sheetPath)!.Replace('\\', '/') + "/_rels/" + Path.GetFileName(sheetPath) + ".rels";
        if (!pkg.Has(relsPath)) return;
        foreach (var rel in pkg.Xml(relsPath).Root!.Elements(PR + "Relationship").Where(r => ((string)r.Attribute("Type")!).EndsWith("/table")))
        {
            var target = (string)rel.Attribute("Target")!;
            var path = target.StartsWith('/') ? target.TrimStart('/') : Path.GetFullPath(Path.Combine("/" + Path.GetDirectoryName(sheetPath), target)).TrimStart('/').Replace('\\', '/');
            if (!pkg.Has(path)) continue;
            var tbl = pkg.Xml(path);
            foreach (var el in new[] { tbl.Root! }.Concat(tbl.Root!.Elements(S + "autoFilter")).Where(e => e.Attribute("ref") is not null))
                el.SetAttributeValue("ref", InsertShiftRanges((string)el.Attribute("ref")!, t, delta));
            pkg.SetXml(path, tbl);
        }
    }

    private static void RemoveCalcChain(OoxmlPackage pkg)
    {
        if (!pkg.Has("xl/calcChain.xml")) return;
        pkg.Remove("xl/calcChain.xml");
        var rels = pkg.Xml("xl/_rels/workbook.xml.rels");
        rels.Root!.Elements(PR + "Relationship").Where(r => ((string)r.Attribute("Target")!).EndsWith("calcChain.xml")).Remove();
        pkg.SetXml("xl/_rels/workbook.xml.rels", rels);
        var ct = pkg.Xml("[Content_Types].xml");
        ct.Root!.Elements(CT + "Override").Where(o => ((string)o.Attribute("PartName")!).EndsWith("calcChain.xml")).Remove();
        pkg.SetXml("[Content_Types].xml", ct);
    }

    // ---------- template contoh ----------
    public static byte[] Starter(string title, IReadOnlyList<ResultColumn> detail, IReadOnlyList<ResultColumn>? header)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s)!;
        var rows = new StringBuilder();
        var r = 1;
        void Row(params (string Col, string Xml)[] cells) { rows.Append($"<row r=\"{r}\">").Append(string.Concat(cells.Select(c => c.Xml))).Append("</row>"); r++; }
        string Str(string col, string text, int style = 0) => $"<c r=\"{col}{r}\" t=\"inlineStr\"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>";
        string Formula(string col, string f, int style) => $"<c r=\"{col}{r}\" s=\"{style}\"><f>{Esc(f)}</f></c>";

        Row(("A", Str("A", title, 1)));
        Row(("A", Str("A", "Dicetak {{Today|dd MMMM yyyy}} oleh {{User}}")));
        foreach (var h in header ?? []) Row(("A", Str("A", $"{h.Name}: {{{{{h.Name}}}}}")));
        r++;
        var cols = new List<ResultColumn> { new(TemplateTags.RowNumber, "number") }.Concat(detail).ToList();
        Row(cols.Select((c, i) => (ColLetters(i + 1), Str(ColLetters(i + 1), c.Name, 2))).ToArray());
        var templateRow = r;
        Row(cols.Select((c, i) => (ColLetters(i + 1), Str(ColLetters(i + 1), $"[[{c.Name}]]", c.Kind == "date" ? 3 : c.Kind == "number" ? 4 : 0))).ToArray());
        Row(cols.Select((c, i) =>
        {
            var l = ColLetters(i + 1);
            return i == 0 ? (l, Str(l, "Total", 1)) : c.Kind == "number" ? (l, Formula(l, $"SUM({l}{templateRow}:{l}{templateRow})", 5)) : (l, "");
        }).Where(x => x.Item2.Length > 0).ToArray());
        var last = ColLetters(cols.Count);
        var sheet = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{S}\" xmlns:r=\"{R}\"><dimension ref=\"A1:{last}{r - 1}\"/>" +
                    $"<cols><col min=\"1\" max=\"1\" width=\"6\" customWidth=\"1\"/><col min=\"2\" max=\"{cols.Count}\" width=\"20\" customWidth=\"1\"/></cols>" +
                    $"<sheetData>{rows}</sheetData></worksheet>";
        const string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E6F7\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
            "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"3\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        return OoxmlPackage.FromParts([
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Laporan\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcId=\"191029\"/></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", sheet),
            ("xl/styles.xml", styles),
        ]).Save();
    }
}

// ============================================================================
//  Word
// ============================================================================

public static class DocxTemplate
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace XmlNs = XNamespace.Xml;
    private static readonly Regex PartRx = new(@"^word/(document|header\d*|footer\d*)\.xml$");

    private static IEnumerable<string> Parts(OoxmlPackage pkg) => pkg.Names.Where(n => PartRx.IsMatch(n)).OrderBy(n => n);

    /// <summary>Elemen teks milik paragraf ini sendiri (bukan paragraf di dalam text box bersarang).</summary>
    private static List<XElement> Texts(XElement p) => p.Descendants(W + "t").Where(t => t.Ancestors(W + "p").First() == p).ToList();
    private static string ParagraphText(XElement p) => string.Concat(Texts(p).Select(t => t.Value));

    public static IReadOnlyList<TemplateTag> Scan(OoxmlPackage pkg)
    {
        var tags = new List<TemplateTag>();
        foreach (var part in Parts(pkg))
            foreach (var p in pkg.Xml(part).Descendants(W + "p"))
                tags.AddRange(TemplateTags.Find(ParagraphText(p), part.Replace("word/", "").Replace(".xml", "")));
        return tags;
    }

    /// <summary>
    /// Penggantian dalam satu paragraf walau tag terpecah ke beberapa run (Word sering memecah "{{Na" + "ma}}" karena
    /// koreksi ejaan / riwayat edit). Teks pengganti ditaruh di run awal tag (format run itu yang dipakai), sisa tag
    /// di run berikutnya dihapus.
    /// </summary>
    private static void ReplaceInParagraph(XElement p, Regex rx, Func<Match, string> value)
    {
        var texts = Texts(p);
        if (texts.Count == 0) return;
        var full = string.Concat(texts.Select(t => t.Value));
        var matches = rx.Matches(full).Cast<Match>().ToList();
        if (matches.Count == 0) return;
        var starts = new int[texts.Count];
        for (int i = 0, pos = 0; i < texts.Count; pos += texts[i].Value.Length, i++) starts[i] = pos;
        int Locate(int index) { for (var i = texts.Count - 1; i >= 0; i--) if (starts[i] <= index && (texts[i].Value.Length > 0 || i == 0)) return i; return 0; }
        foreach (var m in Enumerable.Reverse(matches))
        {
            var replacement = value(m).Replace("\r", "").Replace("\n", " ");
            var a = Locate(m.Index); var b = Locate(m.Index + m.Length - 1);
            var ta = texts[a]; var tb = texts[b];
            var offA = m.Index - starts[a]; var endB = m.Index + m.Length - starts[b];
            if (a == b) ta.Value = ta.Value[..offA] + replacement + ta.Value[(offA + m.Length)..];
            else
            {
                tb.Value = tb.Value[endB..];
                for (var i = a + 1; i < b; i++) texts[i].Value = "";
                ta.Value = ta.Value[..offA] + replacement;
            }
            foreach (var t in new[] { ta, tb }) t.SetAttributeValue(XmlNs + "space", "preserve");
        }
    }

    public static byte[] Render(OoxmlPackage pkg, TemplateData data)
    {
        string Loop(Match m, int row)
        {
            var tag = new TemplateTag(TagKind.Loop, m.Groups["name"].Value, null, m.Groups["fmt"].Success ? m.Groups["fmt"].Value.Trim() : null, m.Value, "");
            var (v, kind) = data.Loop(tag, row);
            return TemplateData.Text(v, kind, tag.Format);
        }
        foreach (var part in Parts(pkg))
        {
            var doc = pkg.Xml(part);
            // 1) Baris tabel berisi [[...]] → diulang per baris data (format baris, border, font ikut tersalin).
            foreach (var tr in doc.Descendants(W + "tr").Where(tr => tr.Descendants(W + "p").Any(p => ParagraphText(p).Contains("[["))).ToList())
                Repeat(tr, data, Loop);
            // 2) Paragraf di luar tabel berisi [[...]] → diulang (mis. daftar berpoin).
            foreach (var p in doc.Descendants(W + "p").Where(p => !p.Ancestors(W + "tbl").Any() && ParagraphText(p).Contains("[[")).ToList())
                Repeat(p, data, Loop);
            // 3) Data tunggal & agregat di seluruh bagian (badan, header, footer).
            foreach (var p in doc.Descendants(W + "p").ToList())
                ReplaceInParagraph(p, TemplateTags.ScalarRx, m =>
                {
                    var col = m.Groups["col"].Success ? m.Groups["col"].Value : null;
                    var kind = col is not null || TemplateTags.Aggregates.Contains(m.Groups["name"].Value, StringComparer.OrdinalIgnoreCase) ? TagKind.Aggregate : TagKind.Scalar;
                    var tag = new TemplateTag(kind, m.Groups["name"].Value, col, m.Groups["fmt"].Success ? m.Groups["fmt"].Value.Trim() : null, m.Value, "");
                    var (v, k) = data.Scalar(tag);
                    return TemplateData.Text(v, k, tag.Format);
                });
            pkg.SetXml(part, doc);
        }
        return pkg.Save();
    }

    private static void Repeat(XElement template, TemplateData data, Func<Match, int, string> loop)
    {
        var clones = new List<XElement>();
        for (var k = 0; k < data.Detail.Rows.Count; k++)
        {
            var clone = new XElement(template);
            var row = k;
            foreach (var p in clone.DescendantsAndSelf(W + "p").ToList()) ReplaceInParagraph(p, TemplateTags.LoopRx, m => loop(m, row));
            clones.Add(clone);
        }
        template.ReplaceWith(clones); // tanpa data: baris template dihapus
    }

    // ---------- template contoh ----------
    public static byte[] Starter(string title, IReadOnlyList<ResultColumn> detail, IReadOnlyList<ResultColumn>? header)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s)!;
        static string P(string text, bool bold = false, int size = 0) =>
            $"<w:p><w:r>{(bold || size > 0 ? $"<w:rPr>{(bold ? "<w:b/>" : "")}{(size > 0 ? $"<w:sz w:val=\"{size}\"/>" : "")}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r></w:p>";
        static string Cell(string text, bool head) =>
            $"<w:tc><w:tcPr>{(head ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"E7E6F7\"/>" : "")}</w:tcPr>{P(text, head)}</w:tc>";
        var cols = new List<ResultColumn> { new(TemplateTags.RowNumber, "number") }.Concat(detail).ToList();
        var body = new StringBuilder();
        body.Append(P(title, true, 32));
        body.Append(P("Tanggal: {{Today|dd MMMM yyyy}} · Dibuat oleh: {{User}}"));
        foreach (var h in header ?? []) body.Append(P($"{h.Name}: {{{{{h.Name}}}}}"));
        body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/><w:tblBorders>" +
                    string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(b => $"<w:{b} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"999999\"/>")) +
                    "</w:tblBorders></w:tblPr>");
        body.Append("<w:tr><w:trPr><w:tblHeader/></w:trPr>").Append(string.Concat(cols.Select(c => Cell(c.Name, true)))).Append("</w:tr>");
        body.Append("<w:tr>").Append(string.Concat(cols.Select(c => Cell(c.Kind == "number" && c.Name != TemplateTags.RowNumber ? $"[[{c.Name}|N0]]" : $"[[{c.Name}]]", false)))).Append("</w:tr>");
        body.Append("</w:tbl>");
        body.Append(P("Jumlah data: {{RowCount}}"));
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>{body}" +
                       "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        return OoxmlPackage.FromParts([
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", document),
        ]).Save();
    }
}
