using System.Diagnostics;
using Framework.Dashboards;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Benchmark performa query di PostgreSQL. Data sintetis dibuat langsung dengan SQL (jauh lebih cepat dari pipeline
/// dokumen), lalu yang diukur adalah jalur kode sungguhan: IDocumentStore.QueryAsync (halaman daftar) dan agregasi
/// dashboard lewat eksekutor read-only.
///   DOCFRAME_TEST_PG=... [DOCFRAME_TEST_PG_RO=...] dotnet run --project tests/Framework.Tests -- --bench 1000000
/// </summary>
public static class Bench
{
    private const string Tenant = "acme";
    private const string PR = "PurchaseRequest";

    public static async Task<int> RunAsync(string[] args)
    {
        if (Tests.PgConnection is null) { Console.WriteLine("Benchmark butuh DOCFRAME_TEST_PG."); return 2; }
        var count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 200_000;
        var reuse = args.Contains("--reuse");

        if (!reuse) await Tests.ResetDatabaseForBenchAsync();
        var sp = await Tests.BuildProviderForBenchAsync();
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        if (!reuse)
        {
            await Tests.SeedForBenchAsync(sp);
            await GenerateAsync(db, count);
        }
        await Exec(db, "ANALYZE docframe_documents");
        var total = await Scalar(db, $"SELECT count(*) FROM docframe_documents WHERE tenant_id = '{Tenant}' AND doctype = '{PR}'");
        var size = await Scalar(db, "SELECT pg_size_pretty(pg_total_relation_size('docframe_documents'))");
        Console.WriteLine($"\nData: {total:N0} {PR} (tenant {Tenant}) · tabel+indeks {size}\n");

        var sync = sp.GetService<Framework.Persistence.PostgreSql.DocumentIndexSync>();
        if (sync is not null && !args.Contains("--only-after")) await sync.DropManagedAsync();
        await Exec(db, "ANALYZE docframe_documents");
        var before = await SuiteAsync(sp, db, "Sebelum (indeks bawaan saja)");
        if (sync is not null)
        {
            var sw = Stopwatch.StartNew();
            await sync.SyncAsync();
            await Exec(db, "ANALYZE docframe_documents");
            Console.WriteLine($"Indeks dari metadata dibuat dalam {sw.Elapsed.TotalSeconds:N1} detik\n");
            var after = await SuiteAsync(sp, db, "Sesudah (indeks dari metadata)");
            Console.WriteLine("\nRingkasan (median ms):");
            Console.WriteLine($"{"Query",-46} {"Sebelum",10} {"Sesudah",10} {"Lebih cepat",12}");
            foreach (var (k, b) in before)
                if (after.TryGetValue(k, out var a))
                    Console.WriteLine($"{k,-46} {b,10:N1} {a,10:N1} {(a > 0 ? b / a : 0),11:N1}x");
        }
        return 0;
    }

    /// <summary>Data sintetis yang menyerupai pengajuan pembelian: 500 karyawan, 6 departemen, 3 prioritas, 5 status.</summary>
    private static async Task GenerateAsync(NpgsqlDataSource db, int count)
    {
        var sw = Stopwatch.StartNew();
        const int batch = 100_000;
        for (var start = 1; start <= count; start += batch)
        {
            var end = Math.Min(count, start + batch - 1);
            await Exec(db, $$"""
                INSERT INTO docframe_documents (tenant_id, doctype, name, owner, docstatus, workflow_state, version, schema_version, data, created, modified, modified_by)
                SELECT '{{Tenant}}', '{{PR}}', 'BPR-' || lpad(i::text, 8, '0'),
                       'user' || (i % 50), CASE WHEN i % 5 = 0 THEN 0 ELSE 1 END,
                       (ARRAY['Draft','Menunggu Atasan','Menunggu Finance','Disetujui','Ditolak'])[1 + i % 5],
                       1, 1,
                       jsonb_build_object(
                         'requester', 'EMP-' || lpad((i % 500)::text, 4, '0'),
                         'requester_name', 'Karyawan ' || (i % 500),
                         'department', (ARRAY['IT','Marketing','Finance','Operasional','Direksi','Infrastruktur'])[1 + i % 6],
                         'priority', (ARRAY['Rendah','Normal','Tinggi'])[1 + (i / 7) % 3],
                         'request_date', to_char(date '2024-01-01' + (i % 900), 'YYYY-MM-DD'),
                         'justification', 'Kebutuhan operasional nomor ' || i,
                         'total', ((i::bigint * 7919) % 150000) * 1000,
                         'items', jsonb_build_array(jsonb_build_object('item_name', 'Barang ' || (i % 120), 'qty', 1 + i % 4, 'rate', ((i::bigint * 7919) % 150000) * 250, 'amount', ((i::bigint * 7919) % 150000) * 1000))),
                       now() - (i % 900) * interval '1 day', now() - (i % 900) * interval '1 day', 'user' || (i % 50)
                FROM generate_series({{start}}, {{end}}) AS g(i);
                """);
            // Dokumen DocType lain sebagai "derau" agar indeks parsial per DocType benar-benar diuji.
            await Exec(db, $$"""
                INSERT INTO docframe_documents (tenant_id, doctype, name, owner, docstatus, version, schema_version, data, created, modified, modified_by)
                SELECT '{{Tenant}}', 'Vendor', 'BVN-' || lpad(i::text, 8, '0'), 'rina', 0, 1, 1,
                       jsonb_build_object('vendor_name', 'Pemasok ' || i, 'city', 'Kota ' || (i % 80)),
                       now(), now(), 'rina'
                FROM generate_series({{start}}, {{start + (end - start) / 5}}) AS g(i);
                """);
            Console.Write($"\r  membuat data: {end:N0}/{count:N0} ({sw.Elapsed.TotalSeconds:N0} dtk)   ");
        }
        Console.WriteLine();
    }

    private static async Task<Dictionary<string, double>> SuiteAsync(IServiceProvider sp, NpgsqlDataSource db, string title)
    {
        Console.WriteLine($"== {title}");
        var store = sp.GetRequiredService<IDocumentStore>();
        var results = new Dictionary<string, double>();
        static SortDef S(string f, string d) => new() { Field = f, Direction = d };
        var cases = new (string Name, StoreQuery Q)[]
        {
            ("Daftar default (urut modified)", Q([], null, [S("modified", "desc")], 0)),
            ("Filter priority = Tinggi", Q([new("priority", "=", "Tinggi")], null, [S("modified", "desc")], 0)),
            ("Filter departemen + total >= 100 jt, urut total", Q([new("department", "=", "IT"), new("total", ">=", 100_000_000m)], null, [S("total", "desc")], 0)),
            ("Filter tanggal pengajuan (rentang 30 hari)", Q([new("request_date", ">=", "2025-06-01"), new("request_date", "<", "2025-07-01")], null, [S("modified", "desc")], 0)),
            ("Cari 'Karyawan 123' (nama pemohon)", Q([], "Karyawan 123", [S("modified", "desc")], 0)),
            ("Halaman dalam (lewati 5.000 baris)", Q([], null, [S("modified", "desc")], 5_000)),
        };
        foreach (var (name, q) in cases)
        {
            var ms = await Median(async () => await store.QueryAsync(q));
            var r = await store.QueryAsync(q);
            results[name] = ms;
            Console.WriteLine($"  {name,-46} {ms,8:N1} ms   ({r.Items.Count} baris, total {(r.TotalCapped ? r.Total.ToString("N0") + "+" : r.Total.ToString("N0"))})");
        }
        var exec = sp.GetService<IReportingQueryExecutor>();
        if (exec is { IsAvailable: true })
        {
            var a = SqlGuard.Analyze("SELECT department, COUNT(*) AS jumlah, SUM(total) AS nilai FROM reporting.purchase_request WHERE request_date BETWEEN @Start AND @End GROUP BY department");
            IReadOnlyList<SqlParam> ps = [new(new DateOnly(2025, 1, 1), ParamTypes.Date), new(new DateOnly(2025, 12, 31), ParamTypes.Date)];
            try
            {
                var ms = await Median(async () => await exec.ExecuteAsync(Tenant, a.ParameterizedSql, ps, 1000));
                results["Dashboard: agregasi per departemen (1 tahun)"] = ms;
                Console.WriteLine($"  {"Dashboard: agregasi per departemen (1 tahun)",-46} {ms,8:N1} ms");
            }
            catch (QueryRejectedException ex)
            {
                Console.WriteLine($"  {"Dashboard: agregasi per departemen (1 tahun)",-46} {"GAGAL",8}    ({ex.Message})");
            }
        }
        // Agregasi yang sama langsung ke tabel dokumen (ekstraksi JSONB, tanpa batas waktu): biaya sebenarnya.
        var direct = await Median(async () => await Scalar(db,
            "SELECT count(*) FROM (SELECT data ->> 'department', count(*), sum((data ->> 'total')::numeric) FROM docframe_documents " +
            $"WHERE tenant_id = '{Tenant}' AND doctype = '{PR}' AND (data ->> 'request_date') BETWEEN '2025-01-01' AND '2025-12-31' GROUP BY 1) s"), 3);
        results["Agregasi JSONB langsung (tanpa batas waktu)"] = direct;
        Console.WriteLine($"  {"Agregasi JSONB langsung (tanpa batas waktu)",-46} {direct,8:N1} ms");
        Console.WriteLine();
        return results;

        static StoreQuery Q(IReadOnlyList<FilterClause> f, string? search, IReadOnlyList<SortDef> sort, int skip) =>
            new(Tenant, PR, RowScope.All, f, search, search is null ? [] : ["name", "requester_name"], sort, skip, 20);
    }

    private static async Task<double> Median(Func<Task> run, int times = 5)
    {
        await run(); // pemanasan (cache plan & buffer)
        var list = new List<double>();
        for (var i = 0; i < times; i++)
        {
            var sw = Stopwatch.StartNew();
            await run();
            list.Add(sw.Elapsed.TotalMilliseconds);
        }
        list.Sort();
        return list[times / 2];
    }

    private static async Task Exec(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await cmd.ExecuteScalarAsync();
    }
}
