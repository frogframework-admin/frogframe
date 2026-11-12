using System.Security.Cryptography;
using System.Text;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Membuat indeks frogframe_documents dari metadata: setiap field yang dipakai untuk filter / urut di daftar
/// (listView.filters, defaultSort) mendapat indeks B-tree parsial per DocType, setiap field pencarian
/// (listView.searchFields) mendapat indeks trigram GIN. Indeks dikelola dengan awalan ix_ff_ — yang tidak lagi
/// dibutuhkan dibuang. Dibangun dengan CONCURRENTLY sehingga tabel tetap bisa ditulis selama pembangunan.
/// Ekspresi indeks harus identik dengan ekspresi yang dihasilkan SqlQueryBuilder agar planner memakainya.
/// </summary>
public sealed class DocumentIndexSync(SchemaDataSource schema, IMetadataProvider metadata, ILogger<DocumentIndexSync> log)
{
    private readonly NpgsqlDataSource db = schema.Source;
    public const string Prefix = "ix_ff_";
    private const long LockKey = 0x496E646578; // "Index"
    private int _subscribed;

    public sealed record IndexSpec(string Name, string DocType, string Field, string Kind, string Ddl);

    /// <summary>Ekspresi SQL atas kolom data — sama persis dengan SqlQueryBuilder.Field (tanpa alias tabel).</summary>
    public static string Expression(CompiledField f, string alias = "")
    {
        var key = "'" + f.Name.Replace("'", "''") + "'";
        var col = $"({alias}data ->> {key})";
        return f.Type.ClrType == typeof(long) || f.Type.ClrType == typeof(decimal) ? $"{col}::numeric"
             : f.Type.ClrType == typeof(bool) ? $"{col}::boolean"
             : col;
    }

    public static IReadOnlyList<IndexSpec> Desired(MetadataSnapshot snapshot)
    {
        var list = new List<IndexSpec>();
        foreach (var meta in snapshot.DocTypes.Values.Where(m => !m.Def.IsChild).OrderBy(m => m.Name))
        {
            var lv = meta.Def.ListView;
            bool Indexable(string f) => !StandardFields.All.Contains(f) && meta.ByName.TryGetValue(f, out var cf) && FieldKinds.IsQueryable(cf.Def.Type);
            // Field urut default: indeks dengan arah urut persis sama dengan query (+ name sebagai penentu urutan kedua),
            // sehingga "ORDER BY … LIMIT 20" cukup membaca 20 entri indeks, bukan mengurutkan semua baris.
            var sorted = new HashSet<string>();
            foreach (var sd in lv.DefaultSort.Where(x => Indexable(x.Field)))
            {
                var f = meta.ByName[sd.Field];
                var desc = sd.Direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
                list.Add(Spec(meta.Name, f, "sort",
                    $"ON frogframe_documents (tenant_id, ({Expression(f)}) {(desc ? "DESC NULLS LAST" : "ASC NULLS FIRST")}, name) WHERE doctype = '{meta.Name}'"));
                sorted.Add(sd.Field);
            }
            foreach (var name in lv.Filters.Where(Indexable).Where(f => !sorted.Contains(f)).Distinct())
            {
                var f = meta.ByName[name];
                list.Add(Spec(meta.Name, f, "btree",
                    $"ON frogframe_documents (tenant_id, ({Expression(f)})) WHERE doctype = '{meta.Name}'"));
            }
            var trgm = lv.SearchFields
                .Where(f => !StandardFields.All.Contains(f) && meta.ByName.TryGetValue(f, out var cf) && cf.Type.ClrType == typeof(string) && FieldKinds.IsQueryable(cf.Def.Type))
                .Distinct();
            foreach (var name in trgm)
            {
                var f = meta.ByName[name];
                list.Add(Spec(meta.Name, f, "trgm",
                    $"ON frogframe_documents USING gin (({Expression(f)}) gin_trgm_ops) WHERE doctype = '{meta.Name}'"));
            }
        }
        return list;
    }

    private static IndexSpec Spec(string docType, CompiledField f, string kind, string body)
    {
        // Nama memuat hash definisi: bila ekspresi/tipe berubah, nama berubah → indeks lama dibuang, yang baru dibuat.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..8].ToLowerInvariant();
        var readable = $"{Prefix}{Snake(docType)}_{f.Name}";
        if (readable.Length > 50) readable = readable[..50];
        return new IndexSpec($"{readable}_{kind[0]}{hash}", docType, f.Name, kind, body);
    }

    private static string Snake(string pascal)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0 && !char.IsUpper(pascal[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    /// <summary>Menyamakan indeks dengan metadata aktif. Aman dipanggil berulang dan dari beberapa instance.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        var desired = Desired(metadata.Current).ToDictionary(i => i.Name);
        // CONCURRENTLY tidak boleh di dalam transaksi → koneksi autocommit + advisory lock tingkat sesi.
        await using var conn = await db.OpenConnectionAsync(ct);
        if (!await Bool(conn, $"SELECT pg_try_advisory_lock({LockKey})", ct))
        {
            log.LogInformation("Sinkronisasi indeks sedang dijalankan instance lain; dilewati");
            return;
        }
        try
        {
            var existing = new Dictionary<string, bool>(); // nama → valid
            await using (var cmd = new NpgsqlCommand(
                "SELECT c.relname, i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid " +
                "JOIN pg_class t ON t.oid = i.indrelid WHERE t.relname = 'frogframe_documents' AND c.relname LIKE 'ix\\_df\\_%'", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) existing[r.GetString(0)] = r.GetBoolean(1);

            foreach (var (name, valid) in existing)
                if (!desired.ContainsKey(name) || !valid)
                {
                    await Exec(conn, $"DROP INDEX CONCURRENTLY IF EXISTS \"{name}\"", ct);
                    log.LogInformation("Indeks {Index} dibuang ({Reason})", name, valid ? "tidak dipakai metadata" : "tidak valid");
                }
            foreach (var spec in desired.Values.Where(s => !existing.TryGetValue(s.Name, out var v) || !v))
            {
                await Exec(conn, $"CREATE INDEX CONCURRENTLY IF NOT EXISTS \"{spec.Name}\" {spec.Ddl}", ct);
                log.LogInformation("Indeks {Index} dibuat untuk {DocType}.{Field} ({Kind})", spec.Name, spec.DocType, spec.Field, spec.Kind);
            }
        }
        finally
        {
            await Exec(conn, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
        }

        if (Interlocked.Exchange(ref _subscribed, 1) == 0 && metadata is MetadataRegistry reg)
            reg.Published += snapshot => _ = Task.Run(async () =>
            {
                try { await SyncAsync(); } catch (Exception ex) { log.LogError(ex, "Sinkronisasi indeks gagal"); }
            });
    }

    /// <summary>Membuang semua indeks terkelola (dipakai benchmark untuk mengukur kondisi "sebelum").</summary>
    public async Task DropManagedAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT indexname FROM pg_indexes WHERE tablename = 'frogframe_documents' AND indexname LIKE 'ix\\_df\\_%'", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        foreach (var n in names) await Exec(conn, $"DROP INDEX CONCURRENTLY IF EXISTS \"{n}\"", ct);
    }

    private static async Task Exec(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> Bool(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
