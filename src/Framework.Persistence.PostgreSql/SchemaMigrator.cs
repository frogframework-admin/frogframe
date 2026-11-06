using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Migrasi skema berurutan dari file Migrations/NNN_nama.sql (embedded resource).
/// Aman untuk banyak instance: dikunci dengan advisory lock dan tiap file berjalan dalam satu transaksi.
/// </summary>
/// <summary>Koneksi pemilik skema untuk DDL. Sama dengan koneksi app bila peran tidak dipisah.</summary>
public sealed class SchemaDataSource(NpgsqlDataSource source, bool owns = false) : IAsyncDisposable
{
    public NpgsqlDataSource Source { get; } = source;
    public ValueTask DisposeAsync() => owns ? Source.DisposeAsync() : ValueTask.CompletedTask;
}

public sealed class SchemaMigrator(SchemaDataSource schema, ILogger<SchemaMigrator> log)
{
    private readonly NpgsqlDataSource db = schema.Source;
    private const long LockKey = 0x446F634672616D65; // "DocFrame"

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await Exec(conn, null, "CREATE TABLE IF NOT EXISTS docframe_schema_migrations (version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        await Exec(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            var applied = new HashSet<int>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM docframe_schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));

            foreach (var (version, name, sql) in Scripts().Where(s => !applied.Contains(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, tx, sql, ct);
                await using (var ins = new NpgsqlCommand("INSERT INTO docframe_schema_migrations (version, name) VALUES ($1, $2)", conn, tx))
                {
                    ins.Parameters.AddWithValue(version);
                    ins.Parameters.AddWithValue(name);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                log.LogInformation("Migrasi database {Version:000} {Name} diterapkan", version, name);
            }
        }
        finally
        {
            await Exec(conn, null, $"SELECT pg_advisory_unlock({LockKey})", ct);
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> Scripts()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order())
        {
            var file = res.Split('.')[^2];                 // "...Migrations.001_initial.sql" → "001_initial"
            var version = int.Parse(file[..file.IndexOf('_')]);
            using var s = new StreamReader(asm.GetManifestResourceStream(res)!);
            yield return (version, file, s.ReadToEnd());
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
