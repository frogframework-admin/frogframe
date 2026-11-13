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

public sealed class SchemaMigrator(SchemaDataSource schema, ILogger<SchemaMigrator> log, IEnumerable<IPostgresMigrationSource> migrationSources)
{
    private readonly NpgsqlDataSource db = schema.Source;
    private const long LockKey = 0x46726F674672616D; // "FrogFrame"

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await Exec(conn, null, "CREATE TABLE IF NOT EXISTS frogframe_schema_migrations (version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct);
        await Exec(conn, null, $"SELECT pg_advisory_lock({LockKey})", ct);
        try
        {
            var applied = new HashSet<int>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM frogframe_schema_migrations", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));

            var scripts = Scripts().OrderBy(s => s.Version).ToList();
            var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null) throw new InvalidOperationException($"Database migration version {duplicate.Key} is registered more than once.");
            foreach (var (version, name, sql) in scripts.Where(s => !applied.Contains(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, tx, sql, ct);
                await using (var ins = new NpgsqlCommand("INSERT INTO frogframe_schema_migrations (version, name) VALUES ($1, $2)", conn, tx))
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

    private IEnumerable<PostgresMigration> Scripts()
    {
        var asm = typeof(SchemaMigrator).Assembly;
        return new EmbeddedPostgresMigrationSource(asm).GetMigrations().Concat(migrationSources.SelectMany(source => source.GetMigrations()));
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
