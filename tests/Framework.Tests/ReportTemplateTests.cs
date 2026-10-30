using Framework.Security;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Framework.Dashboards;
using Framework.Reports;

/// <summary>Test mesin template Excel/Word. Hasil dibuka dengan LibreOffice (bila tersedia) untuk membuktikan file valid
/// dan rumus dihitung ulang — bukan sekadar memeriksa XML.</summary>
sealed partial class Tests
{
    private static readonly XNamespace XS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace XW = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static QueryResultSet Gaji() => new(
        [new("nama", "text"), new("departemen", "text"), new("gaji", "number"), new("masuk", "date")],
        [
            ["Andi Pratama", "IT", 8_500_000m, "2023-01-16"],
            ["=HYPERLINK(\"http://evil\",\"klik\")", "Marketing", 7_250_000m, "2024-03-01"],
            ["Budi Santoso", "IT", 12_000_000m, "2021-07-05"],
        ], false, 1);

    private static TemplateData Data(QueryResultSet detail) => new()
    {
        Detail = detail,
        Header = new([new("perusahaan", "text")], [["PT Acme Indonesia"]], false, 1),
        Parameters = new Dictionary<string, (object?, string)> { ["Periode"] = ("Oktober 2026", ParamTypes.Text) },
        UserName = "Admin Sistem", UserId = "admin", TenantId = "acme", Now = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero),
    };

    private static XDocument Part(byte[] pkg, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(pkg));
        using var s = zip.GetEntry(name)!.Open();
        return XDocument.Load(s);
    }

    /// <summary>Konversi dengan LibreOffice (headless). Null bila LibreOffice tidak tersedia.</summary>
    private static string? LibreOffice(byte[] file, string ext, string to)
    {
        if (!File.Exists("/usr/bin/soffice")) return null;
        var dir = Directory.CreateTempSubdirectory("lo").FullName;
        var src = Path.Combine(dir, "doc." + ext);
        File.WriteAllBytes(src, file);
        var psi = new ProcessStartInfo("soffice", ["--headless", "--norestore", "--convert-to", to, "--outdir", dir, src]) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["HOME"] = "/tmp/lohome";
        using var p = Process.Start(psi)!;
        p.WaitForExit(90_000);
        var outFile = Directory.GetFiles(dir).FirstOrDefault(f => f != src);
        return outFile is null ? throw new Exception("LibreOffice gagal membuka file: " + p.StandardError.ReadToEnd()) : File.ReadAllText(outFile);
    }

    [Test]
    public Task Template_excel_mengulang_baris_nilai_bertipe_dan_rumus_total_melebar()
    {
        var detail = Gaji();
        var template = XlsxTemplate.Starter("Laporan Gaji {{Periode}}", detail.Columns, [new("perusahaan", "text")]);
        var output = XlsxTemplate.Render(OoxmlPackage.Open(template, ReportFormats.Xlsx), Data(detail));
        var rows = Part(output, "xl/worksheets/sheet1.xml").Descendants(XS + "row").ToList();
        string? Cell(string r) => rows.SelectMany(x => x.Elements(XS + "c")).FirstOrDefault(c => (string?)c.Attribute("r") == r) is { } c
            ? c.Element(XS + "f")?.Value is { } f ? "=" + f : (string?)c.Element(XS + "v") ?? string.Concat(c.Descendants(XS + "t").Select(t => t.Value)) : null;
        // Baris 1 judul, 2 dicetak, 3 header query, 5 judul kolom, 6..8 data, 9 total
        Assert.Equal("Laporan Gaji Oktober 2026", Cell("A1"), "tag tunggal dari parameter");
        Assert.Equal("perusahaan: PT Acme Indonesia", Cell("A3"), "tag tunggal dari query header");
        Assert.Equal("Dicetak 29 Oktober 2026 oleh Admin Sistem", Cell("A2"), "tag sistem + format");
        Assert.Equal("1", Cell("A6"), "[[No]] nomor urut");
        Assert.Equal("Budi Santoso", Cell("B8"), "baris data ketiga");
        Assert.Equal("8500000", Cell("D6"), "angka ditulis sebagai angka, bukan teks");
        Assert.Equal("=SUM(D6:D8)", Cell("D9"), "rumus total melebar mengikuti jumlah baris");
        var inj = rows.SelectMany(x => x.Elements(XS + "c")).First(c => (string?)c.Attribute("r") == "B7");
        Assert.True((string?)inj.Attribute("t") == "inlineStr" && inj.Element(XS + "f") is null, "nilai '=…' ditulis sebagai teks, bukan rumus");
        var date = rows.SelectMany(x => x.Elements(XS + "c")).First(c => (string?)c.Attribute("r") == "E6");
        Assert.True(double.Parse((string)date.Element(XS + "v")!, System.Globalization.CultureInfo.InvariantCulture) == (new DateTime(2023, 1, 16) - new DateTime(1899, 12, 30)).TotalDays, "tanggal sebagai nomor seri Excel");

        if (LibreOffice(output, "xlsx", "csv") is { } csv)
        {
            Assert.True(csv.Contains("PT Acme Indonesia") && csv.Contains("Budi Santoso"), "LibreOffice membuka file: " + csv);
            Assert.True(csv.Contains("27,750,000") || csv.Contains("27750000"), "total dihitung ulang saat dibuka (27.750.000): " + csv);
            Assert.True(csv.Contains("=HYPERLINK"), "teks berbahaya tampil apa adanya, tidak dieksekusi");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task Template_excel_sel_gabungan_rumus_per_baris_dan_shared_formula_ikut_bergeser()
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheet = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="{ns}"><dimension ref="A1:D5"/><sheetData>
              <row r="1"><c r="A1" t="inlineStr"><is><t>{"{{Judul}}"}</t></is></c></row>
              <row r="3"><c r="A3" t="inlineStr"><is><t>[[nama]]</t></is></c><c r="B3" t="inlineStr"><is><t>[[gaji]]</t></is></c><c r="C3"><f>B3*2</f><v>0</v></c><c r="D3"><f t="shared" ref="D3:D4" si="0">B3+1</f></c></row>
              <row r="4"><c r="A4" t="inlineStr"><is><t>Catatan</t></is></c><c r="C4"><f>SUM(C3:C3)</f></c><c r="D4"><f t="shared" si="0"/></c></row>
              <row r="5"><c r="A5"><f>COUNTA(A3:A3)&amp;" orang, total "&amp;C4&amp;" (A9 di teks tetap)"</f></c></row>
            </sheetData><mergeCells count="2"><mergeCell ref="A1:D1"/><mergeCell ref="A4:B4"/></mergeCells></worksheet>
            """;
        var pkg = OoxmlPackage.FromParts([
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Data Gaji\" sheetId=\"1\" r:id=\"rId1\"/></sheets><definedNames><definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">'Data Gaji'!$A$1:$D$5</definedName></definedNames></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", sheet.Trim()),
        ]);
        var output = XlsxTemplate.Render(OoxmlPackage.Open(pkg.Save(), ReportFormats.Xlsx), new TemplateData { Detail = Gaji(), Parameters = new Dictionary<string, (object?, string)> { ["Judul"] = ("Rekap", ParamTypes.Text) } });
        var ws = Part(output, "xl/worksheets/sheet1.xml");
        string? F(string r) => ws.Descendants(XS + "c").FirstOrDefault(c => (string?)c.Attribute("r") == r)?.Element(XS + "f")?.Value;
        Assert.Equal("B4*2", F("C4"), "rumus di baris template ikut bergeser per baris (fill-down)");
        Assert.Equal("B5*2", F("C5"), "baris data ketiga");
        Assert.Equal("SUM(C3:C5)", F("C6"), "rumus total melebar");
        Assert.Equal("B5+1", F("D5"), "shared formula diurai lalu disalin per baris");
        Assert.True(F("A7")!.Contains("COUNTA(A3:A5)") && F("A7")!.Contains("&C6&") && F("A7")!.Contains("(A9 di teks tetap)"), "referensi bergeser, teks dalam tanda kutip tidak: " + F("A7"));
        var merges = ws.Descendants(XS + "mergeCell").Select(m => (string)m.Attribute("ref")!).ToList();
        Assert.True(merges.Contains("A1:D1") && merges.Contains("A6:B6"), "sel gabungan di bawah tabel ikut turun: " + string.Join(",", merges));
        var wb = Part(output, "xl/workbook.xml");
        Assert.Equal("'Data Gaji'!$A$1:$D$7", wb.Descendants(XS + "definedName").Single().Value, "area cetak ikut melebar");
        if (LibreOffice(output, "xlsx", "csv") is { } csv)
            Assert.True(csv.Contains("55500000") || csv.Contains("55,500,000"), "LibreOffice menghitung SUM baru (2 × 27.750.000): " + csv);
        return Task.CompletedTask;
    }

    [Test]
    public Task Template_word_tag_terpecah_run_tabel_dan_paragraf_berulang()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="{w}"><w:body>
              <w:p><w:r><w:t xml:space="preserve">Kepada {"{{"}Peri</w:t></w:r><w:proofErr w:type="spellStart"/><w:r><w:rPr><w:b/></w:rPr><w:t>ode</w:t></w:r><w:r><w:t>{"}}"} di {"{{"}perusahaan{"}}"}</w:t></w:r></w:p>
              <w:tbl><w:tblPr/><w:tr><w:tc><w:p><w:r><w:t>Nama</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Gaji</w:t></w:r></w:p></w:tc></w:tr>
                <w:tr><w:trPr><w:cantSplit/></w:trPr><w:tc><w:p><w:r><w:rPr><w:i/></w:rPr><w:t>[[nama]]</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Rp [[gaji|N0]]</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              <w:p><w:r><w:t>- [[No]]. [[nama]] ([[masuk|dd/MM/yyyy]])</w:t></w:r></w:p>
              <w:p><w:r><w:t>Total gaji Rp {"{{"}Sum:gaji|N0{"}}"} untuk {"{{"}RowCount{"}}"} orang</w:t></w:r></w:p>
            </w:body></w:document>
            """;
        var pkg = OoxmlPackage.FromParts([
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", document.Trim()),
            ("word/footer1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr xmlns:w=\"{w}\"><w:p><w:r><w:t>Dicetak oleh {{{{User}}}}</w:t></w:r></w:p></w:ftr>"),
        ]);
        var tags = DocxTemplate.Scan(OoxmlPackage.Open(pkg.Save(), ReportFormats.Docx));
        Assert.True(tags.Any(t => t.Name == "Periode" && t.Kind == TagKind.Scalar), "tag yang terpecah tiga run tetap dikenali saat scan");
        var output = DocxTemplate.Render(OoxmlPackage.Open(pkg.Save(), ReportFormats.Docx), Data(Gaji()));
        var doc = Part(output, "word/document.xml");
        var paras = doc.Descendants(XW + "p").Select(p => string.Concat(p.Descendants(XW + "t").Select(t => t.Value))).ToList();
        Assert.Equal("Kepada Oktober 2026 di PT Acme Indonesia", paras[0], "tag terpecah run diganti utuh");
        var rows = doc.Descendants(XW + "tr").ToList();
        Assert.Equal(4, rows.Count, "1 baris judul + 3 baris data, template dihapus");
        Assert.True(rows.Skip(1).All(r => r.Element(XW + "trPr")?.Element(XW + "cantSplit") is not null) && rows[1].Descendants(XW + "i").Any(), "format baris & run ikut tersalin");
        Assert.True(string.Concat(rows[3].Descendants(XW + "t").Select(t => t.Value)) == "Budi SantosoRp 12.000.000", "format angka id-ID");
        Assert.True(paras.Contains("- 2. =HYPERLINK(\"http://evil\",\"klik\") (01/03/2024)"), "paragraf berulang + format tanggal: " + string.Join(" | ", paras));
        Assert.True(paras.Contains("Total gaji Rp 27.750.000 untuk 3 orang"), "agregat & tag sistem");
        Assert.Equal("Dicetak oleh Admin Sistem", string.Concat(Part(output, "word/footer1.xml").Descendants(XW + "t").Select(t => t.Value)), "footer ikut diisi");
        return Task.CompletedTask;
    }

    [Test]
    public Task Template_word_contoh_bisa_dibuka_libreoffice()
    {
        var detail = Gaji();
        var output = DocxTemplate.Render(OoxmlPackage.Open(DocxTemplate.Starter("Daftar Gaji {{Periode}}", detail.Columns, [new("perusahaan", "text")]), ReportFormats.Docx), Data(detail));
        if (LibreOffice(output, "docx", "txt:Text") is { } txt)
        {
            Assert.True(txt.Contains("Daftar Gaji Oktober 2026") && txt.Contains("Budi Santoso") && txt.Contains("12.000.000"), "isi dokumen: " + txt);
            Assert.True(txt.Contains("Jumlah data: 3") && txt.Contains("PT Acme Indonesia"), "tag tunggal terisi");
        }
        return Task.CompletedTask;
    }

    [Test]
    public Task Template_berbahaya_atau_rusak_ditolak()
    {
        var good = XlsxTemplate.Starter("x", [new("a", "text")], null);
        byte[] With(byte[] src, string name, byte[] content)
        {
            var ms = new MemoryStream(); ms.Write(src);
            using (var z = new ZipArchive(ms, ZipArchiveMode.Update, true)) { using var s = z.CreateEntry(name).Open(); s.Write(content); }
            return ms.ToArray();
        }
        Assert.Throws<TemplateException>(() => OoxmlPackage.Open(With(good, "xl/vbaProject.bin", [1, 2, 3]), ReportFormats.Xlsx), "template bermakro ditolak");
        Assert.Throws<TemplateException>(() => OoxmlPackage.Open(Encoding.UTF8.GetBytes("bukan zip"), ReportFormats.Xlsx), "bukan ZIP ditolak");
        Assert.Throws<TemplateException>(() => OoxmlPackage.Open(good, ReportFormats.Docx), "xlsx diunggah sebagai Word ditolak");
        var bomb = With(good, "xl/media/bomb.bin", new byte[OoxmlPackage.MaxUncompressedBytes + 1]);
        Assert.True(bomb.Length < OoxmlPackage.MaxTemplateBytes, "uji zip bomb: file kecil, isi besar");
        Assert.Throws<TemplateException>(() => OoxmlPackage.Open(bomb, ReportFormats.Xlsx), "zip bomb ditolak");
        var xxe = OoxmlPackage.FromParts([("[Content_Types].xml", "<Types/>"), ("xl/workbook.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><workbook>&e;</workbook>")]).Save();
        Assert.Throws<TemplateException>(() => XlsxTemplate.Scan(OoxmlPackage.Open(xxe, ReportFormats.Xlsx)), "DTD / XXE ditolak");
        var problems = TemplateTags.Validate(TemplateTags.Find("{{Tidakada}} [[kolom_x]] {{Sum:nama}} {{Rata:gaji}} {{Count}} [[No]] {{Today}}", "uji"),
            Gaji().Columns, null, ["Periode"]);
        Assert.Equal(4, problems.Count, "tag tidak dikenal, kolom tidak ada, Sum kolom teks, fungsi tidak dikenal: " + string.Join(" | ", problems));
        return Task.CompletedTask;
    }
}

sealed partial class Tests
{
    private ReportTemplateService Reports => sp.GetRequiredService<ReportTemplateService>();

    private static ReportTemplateConfig PengajuanConfig(string format) => new()
    {
        Sql = "SELECT name AS nomor, requester_name AS pemohon, department AS departemen, request_date AS tanggal, total FROM reporting.purchase_request WHERE department = @Dept ORDER BY name",
        HeaderSql = "SELECT COUNT(*) AS jumlah_semua, 'PT Acme Indonesia' AS perusahaan FROM reporting.purchase_request",
        Parameters = [new() { Name = "Dept", Label = "Departemen", Type = ParamTypes.Text, DefaultValue = "IT" }],
        Format = format,
        FileNamePattern = "Pengajuan {{Dept}} {{Today}}",
        Roles = ["Finance"],
    };

    [Test, ReportingTest]
    public async Task Laporan_template_disimpan_lalu_dibuat_dengan_parameter_dan_role()
    {
        var admin = U("admin");
        foreach (var format in new[] { ReportFormats.Xlsx, ReportFormats.Docx })
        {
            var config = PengajuanConfig(format);
            var (err, ps) = ReportTemplateService.DetectParameters(config.Sql, config.HeaderSql, config.Parameters);
            Assert.True(err is null && ps.Single().Name == "Dept", "parameter dari query detail & header");
            var preview = await Reports.PreviewAsync(admin, config);
            Assert.True(preview.Detail.Rows.Count == 3 && preview.Header!.Rows.Count == 1, "pratinjau detail (IT) & header");
            var starter = ReportTemplateService.Starter(config, "Daftar Pengajuan {{Dept}}", preview);
            Assert.Equal(0, ReportTemplateService.CheckTemplate(config, starter, preview).Problems.Count, "template contoh langsung cocok");

            var saved = await Reports.SaveAsync(admin, new ReportTemplateDef { Name = $"Pengajuan ({format})", Config = config, TemplateFileName = "contoh." + format }, starter);
            Assert.True(saved.Id.StartsWith("rt_") && saved.Version == 1, "tersimpan");
            await Assert.Throws<PermissionDeniedException>(() => Reports.GenerateAsync(U("andi"), saved.Id, new Dictionary<string, string?>()), "role lain ditolak");
            Assert.True((await Reports.ListAsync(U("andi"))).All(d => d.Id != saved.Id), "tidak tampil di daftar role lain");

            var output = await Reports.GenerateAsync(U("dewi"), saved.Id, new Dictionary<string, string?> { ["Dept"] = "Marketing" });
            Assert.True(output.Rows == 2 && output.FileName.StartsWith("Pengajuan Marketing 20") && output.FileName.EndsWith("." + format), $"parameter dipakai: {output.FileName}, {output.Rows} baris");
            var text = format == ReportFormats.Xlsx ? LibreOffice(output.Content, "xlsx", "csv") : LibreOffice(output.Content, "docx", "txt:Text");
            if (text is not null)
                Assert.True(text.Contains("PR-2026-00004") && text.Contains("Sari Wulandari") && !text.Contains("PR-2026-00001") && text.Contains("PT Acme Indonesia"),
                    $"isi {format} sesuai parameter: " + text);
        }
    }

    [Test, ReportingTest]
    public async Task Laporan_template_tidak_cocok_ditolak_dan_hasil_terlalu_besar_tidak_dipotong()
    {
        var admin = U("admin");
        var config = PengajuanConfig(ReportFormats.Xlsx);
        var wrong = XlsxTemplate.Starter("x {{TidakAda}}", [new("kolom_lama", "text")], null);
        var ex = await Assert.Throws<TemplateException>(() => Reports.SaveAsync(admin, new ReportTemplateDef { Name = "Salah", Config = config }, wrong), "template tidak cocok");
        Assert.True(ex.Message.Contains("kolom_lama") && ex.Message.Contains("TidakAda"), "pesan menyebut tag yang bermasalah: " + ex.Message);
        await Assert.Throws<PermissionDeniedException>(() => Reports.SaveAsync(U("dewi"), new ReportTemplateDef { Name = "X", Config = config }, wrong), "hanya System Manager merancang");

        var preview = await Reports.PreviewAsync(admin, config);
        var saved = await Reports.SaveAsync(admin, new ReportTemplateDef { Name = "Kecil", Config = config }, ReportTemplateService.Starter(config, "x", preview));
        var tiny = new ReportTemplateService(sp.GetRequiredService<IReportTemplateStore>(), sp.GetRequiredService<IReportingQueryExecutor>(), TimeProvider.System,
            new ReportLimits { MaxRowsExcel = 2 });
        var big = await Assert.Throws<TemplateException>(() => tiny.GenerateAsync(admin, saved.Id, new Dictionary<string, string?> { ["Dept"] = "IT" }), "3 baris > batas 2");
        Assert.True(big.Message.Contains("melebihi"), "pesan meminta mempersempit parameter: " + big.Message);
        var ok = await tiny.GenerateAsync(admin, saved.Id, new Dictionary<string, string?> { ["Dept"] = "Marketing" });
        Assert.Equal(2, ok.Rows, "tepat di batas tetap dibuat");
    }
}
