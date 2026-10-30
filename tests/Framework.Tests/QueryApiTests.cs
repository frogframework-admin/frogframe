using Framework.Api;
using Framework.Dashboards;
using Framework.Documents;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>API dari query tersimpan (jalur yang sama dengan dashboard & laporan).</summary>
sealed partial class Tests
{
    private QueryApiService QueryApis => sp.GetRequiredService<QueryApiService>();

    private sealed class CountingExecutor(IReportingQueryExecutor inner) : IReportingQueryExecutor
    {
        public int Calls;
        public bool IsAvailable => inner.IsAvailable;
        public Task<QueryResultSet> ExecuteAsync(string tenantId, string sql, IReadOnlyList<SqlParam> ps, int limit, CancellationToken ct = default, TimeSpan? timeout = null)
        { Interlocked.Increment(ref Calls); return inner.ExecuteAsync(tenantId, sql, ps, limit, ct, timeout); }
    }

    private static QueryApiDef PengajuanApi(string slug = "pengajuan-per-dept") => new()
    {
        Slug = slug, Name = "Pengajuan per departemen",
        Config = new()
        {
            Sql = "SELECT name AS nomor, requester_name AS pemohon, total FROM reporting.purchase_request WHERE department = @Dept AND (@MinTotal IS NULL OR total >= @MinTotal) ORDER BY name",
            Parameters =
            [
                new() { Name = "Dept", Label = "Departemen", Type = ParamTypes.Text, DefaultValue = "IT" },
                new() { Name = "MinTotal", Label = "Total minimal", Type = ParamTypes.Number },
            ],
            Roles = ["Finance"],
        },
    };

    [Test, ReportingTest]
    public async Task QueryApi_terbit_dipanggil_dengan_parameter_dan_argumen_divalidasi()
    {
        var admin = U("admin");
        var (err, ps) = QueryApiService.DetectParameters(PengajuanApi().Config.Sql, null);
        Assert.True(err is null && ps.Select(p => p.Name).SequenceEqual(["Dept", "MinTotal"]), "parameter terdeteksi");
        var saved = await QueryApis.SaveAsync(admin, PengajuanApi());
        Assert.True(saved.Id.StartsWith("qa_") && saved.Version == 1, "terbit");

        var it = await QueryApis.ExecuteAsync(U("dewi"), saved.Slug, new Dictionary<string, string?>());
        Assert.Equal(3, it.Data.Rows.Count, "default Dept=IT");
        var mk = await QueryApis.ExecuteAsync(U("dewi"), saved.Slug, new Dictionary<string, string?> { ["dept"] = "Marketing", ["MinTotal"] = "1000000" });
        Assert.True(mk.Data.Rows.Count >= 1 && mk.Data.Rows.All(r => (decimal)r[2]! >= 1_000_000m), "parameter dari pemanggil (nama tidak peka huruf besar)");

        var bad = await Assert.Throws<ValidationFailedException>(() => QueryApis.ExecuteAsync(U("dewi"), saved.Slug,
            new Dictionary<string, string?> { ["Dept"] = "IT", ["tenant"] = "globex", ["MinTotal"] = "banyak" }), "argumen ditolak");
        Assert.True(bad.Errors.Any(e => e.Path == "tenant" && e.Message.Contains("tidak dikenal")) && bad.Errors.Any(e => e.Path == "MinTotal"), "asing & tipe: " + string.Join("; ", bad.Errors));
        await Assert.Throws<PermissionDeniedException>(() => QueryApis.ExecuteAsync(U("andi"), saved.Slug, new Dictionary<string, string?>()), "role lain ditolak");
        await Assert.Throws<KeyNotFoundException>(() => QueryApis.ExecuteAsync(U("dewi"), "tidak-ada", new Dictionary<string, string?>()), "slug tidak ada");

        saved.Config.Enabled = false; await QueryApis.SaveAsync(admin, saved);
        await Assert.Throws<KeyNotFoundException>(() => QueryApis.ExecuteAsync(U("dewi"), saved.Slug, new Dictionary<string, string?>()), "nonaktif → 404, keberadaan tidak dibocorkan");

        await Assert.Throws<ApiMethodException>(() => QueryApis.SaveAsync(admin, PengajuanApi("Slug Salah!")), "slug tidak valid");
        var dup = await Assert.Throws<ApiMethodException>(() => QueryApis.SaveAsync(admin, PengajuanApi()), "slug ganda");
        Assert.True(dup.Message.Contains("sudah dipakai"), dup.Message);
        await Assert.Throws<PermissionDeniedException>(() => QueryApis.SaveAsync(U("dewi"), PengajuanApi("lain")), "hanya System Manager menerbitkan");
        await Assert.Throws<QueryRejectedException>(() => QueryApis.SaveAsync(admin, new QueryApiDef { Slug = "rusak", Name = "x", Config = new() { Sql = "DELETE FROM x" } }), "query non-SELECT ditolak saat terbit");
    }

    [Test, ReportingTest]
    public async Task QueryApi_parameter_dari_akun_tidak_bisa_ditimpa_pemanggil()
    {
        var def = new QueryApiDef
        {
            Slug = "pengajuan-departemen-saya", Name = "Pengajuan departemen saya",
            Config = new() { Sql = "SELECT name, department FROM reporting.purchase_request WHERE department = @Dept", Parameters = [new() { Name = "Dept", Type = ParamTypes.Text, Binding = ParamBindings.UserDepartment }] },
        };
        await QueryApis.SaveAsync(U("admin"), def);
        var budi = await QueryApis.ExecuteAsync(U("budi"), def.Slug, new Dictionary<string, string?>());
        Assert.True(budi.Data.Rows.Count > 0 && budi.Data.Rows.All(r => (string)r[1]! == "IT"), "manager IT → data IT");
        var maya = await QueryApis.ExecuteAsync(U("maya"), def.Slug, new Dictionary<string, string?>());
        Assert.True(maya.Data.Rows.All(r => (string)r[1]! == "Marketing"), "manager Marketing → data Marketing");
        var hack = await Assert.Throws<ValidationFailedException>(() => QueryApis.ExecuteAsync(U("budi"), def.Slug, new Dictionary<string, string?> { ["Dept"] = "Marketing" }), "menimpa departemen");
        Assert.True(hack.Errors.Single().Message.Contains("otomatis dari akun"), "pesan jelas: " + hack.Errors.Single().Message);
    }

    [Test, ReportingTest]
    public async Task QueryApi_diterbitkan_dari_widget_bertaut_dan_cache_menghemat_query()
    {
        var admin = U("admin");
        var sql = "SELECT department, COUNT(*) AS jumlah FROM reporting.purchase_request GROUP BY department ORDER BY department";
        var a = await QueryApis.PublishFromAsync(admin, "widget:w_uji", "rekap-departemen", "Rekap per departemen", null, sql, [], ["Finance"]);
        Assert.True(a.SourceRef == "widget:w_uji" && a.Version == 1, "terbit dari widget");
        var b = await QueryApis.PublishFromAsync(admin, "widget:w_uji", "rekap-departemen", "Rekap per departemen (v2)", null, sql + " LIMIT 10", [], ["Finance"]);
        Assert.True(b.Id == a.Id && b.Version == 2 && b.Config.Sql.EndsWith("LIMIT 10"), "simpan ulang widget → API yang sama diperbarui");
        var other = await Assert.Throws<ApiMethodException>(() => QueryApis.PublishFromAsync(admin, "report:rt_lain", "rekap-departemen", "x", null, sql, [], []), "slug milik sumber lain");
        Assert.True(other.Message.Contains("sudah dipakai"), other.Message);

        var counting = new CountingExecutor(sp.GetRequiredService<IReportingQueryExecutor>());
        var cached = new QueryApiService(sp.GetRequiredService<IQueryApiStore>(), counting, TimeProvider.System);
        b.Config.CacheSeconds = 60; await QueryApis.SaveAsync(admin, b);
        var r1 = await cached.ExecuteAsync(U("dewi"), "rekap-departemen", new Dictionary<string, string?>());
        var r2 = await cached.ExecuteAsync(U("dewi"), "rekap-departemen", new Dictionary<string, string?>());
        Assert.True(!r1.FromCache && r2.FromCache && counting.Calls == 1, $"panggilan kedua dari cache (eksekusi: {counting.Calls})");
        b.Name = "ubah"; await QueryApis.SaveAsync(admin, b);
        await cached.ExecuteAsync(U("dewi"), "rekap-departemen", new Dictionary<string, string?>());
        Assert.Equal(2, counting.Calls, "versi baru → cache lama tidak dipakai");
    }
}
