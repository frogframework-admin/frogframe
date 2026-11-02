using System.IO.Compression;
using System.Text;
using Framework.Documents;
using Framework.Imports;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Reports;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Impor Excel/CSV ke DocType.</summary>
sealed partial class Tests
{
    private DataImportService Imports => sp.GetRequiredService<DataImportService>();

    /// <summary>File seperti yang diisi pengguna dari template: judul kolom diambil dari template asli.</summary>
    private byte[] FilledPurchaseRequests(bool includeBadRows)
    {
        var template = SpreadsheetReader.Read(Imports.BuildTemplate(U("andi"), PR), "t.xlsx", 10_000_000);
        var mainHeaders = template[0].Rows[0].Select(h => (string)h!).ToList();
        object?[] Main(params (string Label, object? Value)[] cells)
        {
            var row = new object?[mainHeaders.Count];
            foreach (var (label, v) in cells) row[mainHeaders.FindIndex(h => h.TrimEnd(' ', '*') == label)] = v;
            return row;
        }
        var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get(PR);
        string L(string f) => meta.ByName[f].Label;
        var rows = new List<object?[]>
        {
            Main(("Kunci impor", 1m), (L("requester"), "andi pratama"), (L("request_date"), new DateTime(2026, 10, 1)), (L("priority"), "tinggi"), (L("justification"), "Impor 1")),
            Main(("Kunci impor", 2m), (L("requester"), "Andi Pratama"), (L("request_date"), "15/10/2026"), (L("priority"), "Normal"), (L("justification"), "Impor 2")),
        };
        if (includeBadRows)
        {
            rows.Add(Main(("Kunci impor", 3m), (L("requester"), "Orang Tidak Ada"), (L("request_date"), "2026-10-20"), (L("priority"), "Sangat tinggi")));
            rows.Add(Main(("Kunci impor", 4m), (L("requester"), "Andi Pratama"), (L("request_date"), "kemarin"), (L("priority"), "Rendah")));
            rows.Add(Main(("Kunci impor", 5m), (L("requester"), "Andi Pratama"), (L("request_date"), "2026-10-21"), (L("priority"), "Rendah"))); // tanpa barang: wajib dari pipeline
        }
        var child = new XlsxSheet
        {
            Name = template[1].Name, Headers = ["Kunci impor", "item_name", "qty", "rate", "uom"], // sheet anak memakai nama field (juga didukung)
            Rows =
            [
                [1m, "Mouse wireless", 2m, "150.000", "pcs"],
                [1m, "Keyboard mekanik", 1m, 450000m, "PCS"],
                [2m, "Monitor 27 inci", 1m, "2.500.000,00", "unit"],
                .. includeBadRows ? new object?[][] { [3m, "Kertas", 5m, 50000m, "box"], [9m, "Yatim", 1m, 1m, "pcs"] } : [],
            ],
        };
        return XlsxWriter.Build([new XlsxSheet { Name = template[0].Name, Headers = mainHeaders, Rows = rows }, child]);
    }

    private async Task<int> CountPR() => (await Docs.QueryForReportAsync(U("admin"), PR, [], DocActions.Read)).Count;

    [Test]
    public async Task Impor_template_mengikuti_metadata_dan_izin_pengguna()
    {
        var bytes = Imports.BuildTemplate(U("andi"), PR);
        var sheets = SpreadsheetReader.Read(bytes, "t.xlsx", 10_000_000);
        var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get(PR);
        var headers = sheets[0].Rows[0].Select(h => (string)h!).ToList();
        Assert.True(headers[0] == DataImportService.KeyHeader && headers.Contains(meta.ByName["requester"].Label + " *"), "kunci impor + wajib bertanda *: " + string.Join(" | ", headers));
        Assert.True(!headers.Any(h => h.StartsWith(meta.ByName["total"].Label)) && !headers.Any(h => h.StartsWith(meta.ByName["requester_name"].Label)), "field turunan tidak ada di template");
        var perm = meta.Fields.Where(f => f.Def.PermLevel > 0).Select(f => f.Label).ToList();
        Assert.True(perm.Count > 0 && perm.All(l => !headers.Any(h => h.StartsWith(l))), "field berlevel izin lebih tinggi tidak tampil untuk karyawan: " + string.Join(", ", perm));
        Assert.True(sheets.Any(s => s.Name == meta.ByName["items"].Label) && sheets.Any(s => s.Name == "Petunjuk") && sheets.All(s => s.Name != "_pilihan"), "sheet tabel + petunjuk; daftar pilihan tersembunyi");
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        using (var r = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()))
            Assert.True(r.ReadToEnd().Contains("<dataValidation type=\"list\""), "dropdown untuk Select");
        await Assert.Throws<PermissionDeniedException>(() => Task.FromResult(Imports.BuildTemplate(U("andi"), "Vendor")), "tanpa izin buat → tidak ada template");
        if (LibreOffice(bytes, "xlsx", "csv") is { } csv) Assert.True(csv.Contains(DataImportService.KeyHeader), "template dibuka LibreOffice");
    }

    [Test]
    public async Task Impor_simulasi_lalu_eksekusi_melewati_baris_gagal()
    {
        var andi = U("andi");
        var before = await CountPR();
        var session = Imports.Open(andi, PR, "pengajuan.xlsx", FilledPurchaseRequests(includeBadRows: true));
        Assert.Equal(5, session.RowCount, "5 baris data");
        Assert.True(session.Main.Columns.Count(c => c.Field is not null) >= 5 && session.Children.Count == 1, "kolom & sheet tabel terpetakan otomatis");
        Assert.Equal(0, Imports.MissingRequired(andi, session).Count, "semua kolom wajib terpetakan");

        var preview = await Imports.SimulateAsync(andi, session);
        string Err(int key) => string.Join(" | ", preview.Rows.Single(r => r.Key == key.ToString()).Errors);
        Assert.True(preview.Ok == 2 && preview.Failed == 3, $"2 siap, 3 bermasalah: {string.Join(" || ", preview.Rows.Select(r => $"{r.Key}:{string.Join(";", r.Errors)}"))}");
        Assert.True(Err(3).Contains("'Orang Tidak Ada' tidak ditemukan") && Err(3).Contains("'Sangat tinggi' bukan pilihan"), "Link & Select: " + Err(3));
        Assert.True(Err(4).Contains("'kemarin' bukan tanggal"), "tanggal: " + Err(4));
        Assert.True(Err(5).Length > 0, "aturan wajib dari pipeline dokumen (tabel barang kosong): " + Err(5));
        Assert.True(preview.Rows.Single(r => r.Key == "3").RowNumber == 4, "nomor baris = nomor baris Excel");
        Assert.True(preview.Warnings.Any(w => w.Contains("'9'") && w.Contains("tidak ada di sheet utama")), "baris tabel yatim diperingatkan");
        Assert.Equal(before, await CountPR(), "simulasi tidak menyimpan apa pun");

        var run = await Imports.ExecuteAsync(andi, session, atomic: false);
        Assert.True(run.Log.Succeeded == 2 && run.Log.Failed == 3 && await CountPR() == before + 2, "2 tersimpan, 3 dilewati");
        var doc = await Docs.GetAsync(andi, PR, run.Rows.First(r => r.Ok).CreatedName!);
        var items = (IList<Dictionary<string, object?>>)doc.Data["items"]!;
        Assert.True(items.Count == 2 && (decimal)doc.Data["total"]! == 750_000m && (string)doc.Data["priority"]! == "Tinggi" && (string)doc.Data["request_date"]! == "2026-10-01",
            $"nilai dikonversi & dihitung: items={items.Count}, total={doc.Data["total"]}, prioritas={doc.Data["priority"]}, tanggal={doc.Data["request_date"]}");
        Assert.True((string)items[1]["uom"]! == "pcs", "Select di tabel tidak peka huruf besar");
        var second = await Docs.GetAsync(andi, PR, run.Rows.Where(r => r.Ok).ElementAt(1).CreatedName!);
        Assert.Equal(2_500_000m, (decimal)second.Data["total"]!, "angka '2.500.000,00' format Indonesia");
        Assert.True(doc.WorkflowState == "Draft" && doc.Owner == "andi", "dibuat sebagai draft milik pengimpor");
        var audit = await Docs.GetAuditAsync(andi, PR, doc.Name);
        Assert.True(audit.Any(a => a.Action == $"Dibuat lewat impor #{run.Log.Id}"), "audit menyebut asal impor");
        var history = await Imports.HistoryAsync(andi, PR);
        Assert.True(history.Single().Id == run.Log.Id && history.Single().CreatedNames.Count == 2, "riwayat impor tercatat");

        var report = SpreadsheetReader.Read(DataImportService.FailureReport(session, run.Rows), "gagal.xlsx", 10_000_000);
        Assert.True(report[0].Rows.Count == 4 && (string)report[0].Rows[0][^1]! == "Alasan" && report[0].Rows.Skip(1).All(r => r[^1] is string s && s.Length > 0), "file baris gagal + alasan");
        Assert.True(report[1].Rows.Count == 2 && (decimal)report[1].Rows[1][0]! == 3m, "baris tabel ikut untuk baris gagal saja");
    }

    [Test]
    public async Task Impor_atomik_semua_atau_tidak_sama_sekali()
    {
        var andi = U("andi");
        var before = await CountPR();
        var bad = await Imports.ExecuteAsync(andi, Imports.Open(andi, PR, "a.xlsx", FilledPurchaseRequests(includeBadRows: true)), atomic: true);
        Assert.True(bad.Log.Succeeded == 0 && await CountPR() == before, "satu baris gagal → tidak ada yang tersimpan");
        Assert.True(bad.Rows.Where(r => r.Key is "1" or "2").All(r => r.Errors.Single().Contains("ada baris lain yang gagal")), "baris valid diberi alasan");
        var good = await Imports.ExecuteAsync(andi, Imports.Open(andi, PR, "b.xlsx", FilledPurchaseRequests(includeBadRows: false)), atomic: true);
        Assert.True(good.Log.Succeeded == 2 && await CountPR() == before + 2 && good.Rows.All(r => r.CreatedName!.StartsWith("PR-")), "semua valid → tersimpan sekaligus");
    }

    [Test]
    public async Task Impor_csv_dan_batasan()
    {
        var rina = U("rina");
        var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get("Vendor");
        var csv = $"{meta.ByName["vendor_name"].Label};{meta.ByName["category"].Label};email;{meta.ByName["blacklisted"].Label}\n" +
                  "PT Sumber Baru;atk;halo@sumber.co;Ya\n\"Toko \"\"Jaya\"\"; Abadi\";jasa;;tidak\n";
        var s = Imports.Open(rina, "Vendor", "pemasok.csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray());
        Assert.True(s.RowCount == 2 && s.Main.Columns.All(c => c.Field is not null), "CSV ';' + BOM + kutip terbaca, kolom terpetakan (label & nama field)");
        var run = await Imports.ExecuteAsync(rina, s, atomic: false);
        Assert.Equal(2, run.Log.Succeeded, "2 pemasok dibuat: " + string.Join(";", run.Rows.SelectMany(r => r.Errors)));
        var v = await Docs.GetAsync(rina, "Vendor", run.Rows[1].CreatedName!);
        Assert.True((string)v.Data["vendor_name"]! == "Toko \"Jaya\"; Abadi" && (string)v.Data["category"]! == "Jasa" && (bool)v.Data["blacklisted"]! == false, "kutip, Select, Check");

        await Assert.Throws<PermissionDeniedException>(() => Task.FromResult(Imports.Open(U("andi"), "Vendor", "x.csv", Encoding.UTF8.GetBytes(csv))), "tanpa izin buat ditolak");
        var small = new DataImportService(Docs, sp.GetRequiredService<IMetadataProvider>(), sp.GetRequiredService<IPermissionService>(),
            sp.GetRequiredService<IImportLogStore>(), TimeProvider.System, new ImportLimits { MaxRows = 1 });
        var big = await Assert.Throws<TemplateException>(() => Task.FromResult(small.Open(rina, "Vendor", "x.csv", Encoding.UTF8.GetBytes(csv))), "batas baris");
        Assert.True(big.Message.Contains("Maximum 1") || big.Message.Contains("Maksimal 1"), big.Message);
        await Assert.Throws<TemplateException>(() => Task.FromResult(Imports.Open(rina, "Vendor", "lama.xls", [1, 2, 3])), ".xls lama ditolak dengan petunjuk");
        var noKey = Imports.Open(rina, "Vendor", "x.csv", Encoding.UTF8.GetBytes("email\nx@y.z\n"));
        Assert.True(Imports.MissingRequired(rina, noKey).Contains(meta.ByName["vendor_name"].Label), "kolom wajib yang tidak ada dilaporkan sebelum simulasi");
    }
}
