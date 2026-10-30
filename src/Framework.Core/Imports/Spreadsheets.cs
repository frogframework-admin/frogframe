using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Framework.Reports;

namespace Framework.Imports;

/// <summary>Satu sheet: baris 0 = judul kolom. Nilai: string, decimal, bool, DateTime, atau null.</summary>
public sealed record SheetData(string Name, List<object?[]> Rows, int FormulaCells = 0)
{
    /// <summary>Nomor baris Excel dari Rows[0] (judul kolom).</summary>
    public int FirstRowNumber { get; init; } = 1;
}

/// <summary>Membaca .xlsx (dengan pengaman paket yang sama dengan template laporan) dan .csv.</summary>
public static class SpreadsheetReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static List<SheetData> Read(byte[] file, string fileName, long maxBytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ReadXlsx(file, maxBytes),
            ".csv" or ".txt" => [ReadCsv(file, Path.GetFileNameWithoutExtension(fileName), maxBytes)],
            ".xls" => throw new TemplateException("Format .xls lama tidak didukung. Simpan ulang sebagai .xlsx (Excel Workbook) atau .csv."),
            _ => throw new TemplateException("File harus .xlsx atau .csv"),
        };
    }

    private static List<SheetData> ReadXlsx(byte[] file, long maxBytes)
    {
        var pkg = OoxmlPackage.Open(file, ReportFormats.Xlsx, maxBytes);
        var sst = !pkg.Has("xl/sharedStrings.xml") ? [] : pkg.Xml("xl/sharedStrings.xml").Root!.Elements(S + "si")
            .Select(si => string.Concat(si.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value))).ToList();
        var dateStyles = DateStyles(pkg);
        var wb = pkg.Xml("xl/workbook.xml");
        var rels = pkg.Xml("xl/_rels/workbook.xml.rels").Root!.Elements(PR + "Relationship").ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var result = new List<SheetData>();
        foreach (var sh in wb.Root!.Element(S + "sheets")!.Elements(S + "sheet"))
        {
            if ((string?)sh.Attribute("state") is "hidden" or "veryHidden") continue;
            var target = rels[(string)sh.Attribute(R + "id")!];
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!pkg.Has(path)) continue;
            var rows = new SortedDictionary<int, Dictionary<int, object?>>();
            var formulas = 0;
            foreach (var row in pkg.Xml(path).Descendants(S + "row"))
            {
                var r = (int?)row.Attribute("r") ?? rows.Count + 1;
                var cells = new Dictionary<int, object?>();
                var col = 0;
                foreach (var c in row.Elements(S + "c"))
                {
                    col = c.Attribute("r") is { } ra ? XlsxTemplate.ColIndex(Regex.Match((string)ra, "^[A-Z]+").Value) : col + 1;
                    if (c.Element(S + "f") is not null) formulas++;
                    cells[col] = Value(c, sst, dateStyles);
                }
                if (cells.Values.Any(v => v is not null && !(v is string s && s.Trim().Length == 0))) rows[r] = cells;
            }
            if (rows.Count == 0) continue;
            var width = rows.Values.SelectMany(c => c.Keys).DefaultIfEmpty(0).Max();
            // Baris kosong di tengah dipertahankan agar nomor baris di pesan error sama dengan nomor baris di Excel.
            var first = rows.Keys.First();
            var list = new List<object?[]>();
            for (var r = first; r <= rows.Keys.Last(); r++)
            {
                var arr = new object?[width];
                if (rows.TryGetValue(r, out var cells)) foreach (var (c, v) in cells) arr[c - 1] = v;
                list.Add(arr);
            }
            result.Add(new SheetData((string)sh.Attribute("name")!, list, formulas) { FirstRowNumber = first });
        }
        return result;
    }

    private static object? Value(XElement c, List<string> sst, HashSet<int> dateStyles)
    {
        var v = c.Element(S + "v")?.Value;
        switch ((string?)c.Attribute("t"))
        {
            case "s": return int.TryParse(v, out var i) && i < sst.Count ? sst[i] : null;
            case "inlineStr": return string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);
            case "str": return v;
            case "b": return v == "1";
            case "e": return "#ERROR " + v;
        }
        if (v is null) return null;
        if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return v;
        if (dateStyles.Contains((int?)c.Attribute("s") ?? 0) && d is > 0 and < 2958466)
            return DateTime.FromOADate((double)d);
        return d;
    }

    private static HashSet<int> DateStyles(OoxmlPackage pkg)
    {
        var set = new HashSet<int>();
        if (!pkg.Has("xl/styles.xml")) return set;
        var doc = pkg.Xml("xl/styles.xml");
        var custom = doc.Root!.Element(S + "numFmts")?.Elements(S + "numFmt")
            .ToDictionary(n => (int)n.Attribute("numFmtId")!, n => Regex.Replace((string)n.Attribute("formatCode")!, "\"[^\"]*\"|\\[[^\\]]*\\]", "")) ?? [];
        var xfs = doc.Root!.Element(S + "cellXfs")?.Elements(S + "xf").ToList() ?? [];
        for (var i = 0; i < xfs.Count; i++)
        {
            var id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.TryGetValue(id, out var code) && code.IndexOfAny(['d', 'y', 'D', 'Y']) >= 0) set.Add(i);
        }
        return set;
    }

    private static SheetData ReadCsv(byte[] file, string name, long maxBytes)
    {
        if (file.Length > maxBytes) throw new TemplateException($"File maksimal {maxBytes / 1024 / 1024} MB");
        var text = new UTF8Encoding(false).GetString(file).TrimStart('\uFEFF');
        var firstLine = text.Split('\n')[0];
        var sep = new[] { ',', ';', '\t' }.OrderByDescending(ch => firstLine.Count(x => x == ch)).First();
        var rows = new List<object?[]>();
        var row = new List<object?>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == sep) { row.Add(Cell(cell)); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(Cell(cell)); cell.Clear(); rows.Add([.. row]); row.Clear();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(Cell(cell)); rows.Add([.. row]); }
        while (rows.Count > 0 && rows[^1].All(v => v is null)) rows.RemoveAt(rows.Count - 1);
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        return new SheetData(name, rows.Select(r => r.Length == width ? r : [.. r, .. new object?[width - r.Length]]).ToList()) { FirstRowNumber = 1 };

        static object? Cell(StringBuilder sb) => sb.Length == 0 ? null : sb.ToString();
    }
}

/// <summary>Penulis .xlsx sederhana: judul kolom tebal (wajib bertanda), baris judul dibekukan, dropdown dari sheet
/// daftar tersembunyi, tanggal sebagai tanggal Excel. Dipakai untuk template impor & laporan baris gagal.</summary>
public sealed class XlsxSheet
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }
    public List<object?[]> Rows { get; init; } = [];
    public HashSet<int> RequiredColumns { get; init; } = [];
    /// <summary>Kolom (indeks 0) → pilihan dropdown.</summary>
    public Dictionary<int, IReadOnlyList<string>> Lists { get; init; } = [];
    public Dictionary<int, double> Widths { get; init; } = [];
    public bool Hidden { get; init; }
    public bool WrapText { get; init; }
    public int ValidationRows { get; init; } = 1000;
}

public static class XlsxWriter
{
    private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Build(IReadOnlyList<XlsxSheet> input)
    {
        var sheets = input.ToList();
        // Pilihan dropdown ditaruh di sheet tersembunyi: tidak terbatas 255 karakter dan aman untuk pilihan yang memuat koma.
        var listCols = new List<(XlsxSheet Sheet, int Col, IReadOnlyList<string> Options)>();
        foreach (var s in sheets) foreach (var (c, o) in s.Lists) if (o.Count > 0) listCols.Add((s, c, o));
        if (listCols.Count > 0)
            sheets.Add(new XlsxSheet
            {
                Name = "_pilihan", Hidden = true, Headers = listCols.Select((_, i) => $"daftar{i + 1}").ToList(),
                Rows = Enumerable.Range(0, listCols.Max(l => l.Options.Count)).Select(r => listCols.Select(l => (object?)(r < l.Options.Count ? l.Options[r] : null)).ToArray()).ToList(),
            });

        var parts = new List<(string, string)>();
        var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        var wbSheets = new StringBuilder();
        var wbRels = new StringBuilder();
        for (var i = 0; i < sheets.Count; i++)
        {
            var s = sheets[i];
            ct.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            wbSheets.Append($"<sheet name=\"{Esc(s.Name)}\" sheetId=\"{i + 1}\"{(s.Hidden ? " state=\"hidden\"" : "")} r:id=\"rId{i + 1}\"/>");
            wbRels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            parts.Add(($"xl/worksheets/sheet{i + 1}.xml", Sheet(s, listCols.Select((l, n) => (l, n)).Where(x => x.l.Sheet == s).Select(x => (x.l.Col, x.n, x.l.Options.Count)).ToList())));
        }
        ct.Append("</Types>");
        wbRels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        parts.Insert(0, ("[Content_Types].xml", ct.ToString()));
        parts.Add(("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
        parts.Add(("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\"><sheets>{wbSheets}</sheets></workbook>"));
        parts.Add(("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{wbRels}</Relationships>"));
        parts.Add(("xl/styles.xml", Styles));
        return OoxmlPackage.FromParts(parts).Save();
    }

    private const string Styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFB42318\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE7E6F7\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";

    private static string Sheet(XlsxSheet s, List<(int Col, int ListIndex, int Count)> lists)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{NsMain}\" xmlns:r=\"{NsRel}\">");
        if (!s.Hidden) sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols>");
        for (var c = 0; c < s.Headers.Count; c++)
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{(s.Widths.TryGetValue(c, out var w) ? w : Math.Clamp(s.Headers[c].Length + 4, 12, 40)).ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        sb.Append("<row r=\"1\">");
        for (var c = 0; c < s.Headers.Count; c++) sb.Append(Str(c, 1, s.Headers[c], s.RequiredColumns.Contains(c) ? 2 : 1));
        sb.Append("</row>");
        for (var r = 0; r < s.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 2}\">");
            var row = s.Rows[r];
            for (var c = 0; c < row.Length; c++)
            {
                var v = row[c];
                var at = $"{XlsxTemplate.ColLetters(c + 1)}{r + 2}";
                switch (v)
                {
                    case null: break;
                    case decimal d: sb.Append($"<c r=\"{at}\"><v>{d.ToString(CultureInfo.InvariantCulture)}</v></c>"); break;
                    case long l: sb.Append($"<c r=\"{at}\"><v>{l}</v></c>"); break;
                    case DateTime dt: sb.Append($"<c r=\"{at}\" s=\"3\"><v>{dt.ToOADate().ToString(CultureInfo.InvariantCulture)}</v></c>"); break;
                    case bool b: sb.Append(Str(c, r + 2, b ? "Ya" : "Tidak", 0)); break;
                    default: sb.Append(Str(c, r + 2, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", s.WrapText ? 4 : 0)); break;
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (lists.Count > 0)
        {
            sb.Append($"<dataValidations count=\"{lists.Count}\">");
            foreach (var (col, li, count) in lists)
            {
                var letter = XlsxTemplate.ColLetters(col + 1);
                var src = XlsxTemplate.ColLetters(li + 1);
                sb.Append($"<dataValidation type=\"list\" allowBlank=\"1\" showErrorMessage=\"1\" errorTitle=\"Nilai tidak valid\" error=\"Pilih dari daftar\" sqref=\"{letter}2:{letter}{s.ValidationRows + 1}\">" +
                          $"<formula1>'_pilihan'!${src}$2:${src}${count + 1}</formula1></dataValidation>");
            }
            sb.Append("</dataValidations>");
        }
        return sb.Append("</worksheet>").ToString();
    }

    private static string Str(int col, int row, string text, int style) =>
        $"<c r=\"{XlsxTemplate.ColLetters(col + 1)}{row}\" t=\"inlineStr\"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>";

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";
}
