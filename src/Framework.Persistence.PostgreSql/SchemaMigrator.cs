using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

using Framework.Persistence;

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

public sealed class SchemaMigrator(SchemaDataSource schema, ILogger<SchemaMigrator> log, IEnumerable<IPostgresMigrationSource> migrationSources,
    StartupReadiness? startup = null)
{
    private readonly NpgsqlDataSource db = schema.Source;
    private const long LockKey = 0x46726F674672616D; // "FrogFrame"

    public async Task<IReadOnlyList<PostgresMigration>> GetPendingMigrationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenReadyConnectionAsync(ct);
        var applied = new HashSet<int>();
        await using (var checkCmd = new NpgsqlCommand("SELECT to_regclass('public.frogframe_schema_migrations') IS NOT NULL", conn))
        {
            var exists = await checkCmd.ExecuteScalarAsync(ct) is true;
            if (exists)
            {
                await using var cmd = new NpgsqlCommand("SELECT version FROM frogframe_schema_migrations", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
            }
        }

        var scripts = Scripts().OrderBy(s => s.Version).ToList();
        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException($"Database migration version {duplicate.Key} is registered more than once.");
        return scripts.Where(s => !applied.Contains(s.Version)).ToList();
    }

    public async Task EnsureMigratedAsync(CancellationToken ct = default)
    {
        var pending = await GetPendingMigrationsAsync(ct);
        if (pending.Count > 0)
        {
            var list = string.Join(", ", pending.Take(5).Select(m => $"{m.Version:000}_{m.Name}"));
            var suffix = pending.Count > 5 ? $" (+{pending.Count - 5} more)" : "";
            throw new InvalidOperationException(
                $"Database schema has {pending.Count} pending migration(s): {list}{suffix}. " +
                "Run 'dotnet Apps.Erp.dll --migrate-only' before starting web replicas when Persistence:MigrateOnStartup is false.");
        }
        log.LogInformation("Database schema verified: all migrations are applied.");
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenReadyConnectionAsync(ct);
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

    private async Task<NpgsqlConnection> OpenReadyConnectionAsync(CancellationToken ct)
    {
        const int maxAttempts = 150;
        var delay = TimeSpan.FromSeconds(3);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var conn = await db.OpenConnectionAsync(ct);
                if (attempt > 1)
                    log.LogInformation("PostgreSQL database is now ready after {Attempts} attempts.", attempt);
                return conn;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested && IsTransientStartupError(ex, out var reason))
            {
                startup?.ReportDatabaseWaiting(reason);
                log.LogWarning(
                    "Waiting for the database to be ready ({Reason}). Retrying in {DelaySeconds}s (attempt {Attempt}/{MaxAttempts})...",
                    reason, (int)delay.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransientStartupError(Exception ex, out string reason)
    {
        if (ex is PostgresException pg)
        {
            // 57P03 = cannot_connect_now ("the database system is starting up" / "the database system is in recovery mode")
            // 57P01 = admin_shutdown, 57P02 = crash_shutdown, 53300 = too_many_connections
            if (pg.SqlState is "57P03" or "57P01" or "57P02" or "53300" || pg.SqlState.StartsWith("08", StringComparison.Ordinal))
            {
                reason = $"SQLSTATE {pg.SqlState}: {pg.MessageText}";
                return true;
            }
        }
        if (ex is NpgsqlException or SocketException or TimeoutException)
        {
            var cur = ex;
            while (cur.InnerException is not null) cur = cur.InnerException;
            reason = cur.Message;
            return true;
        }
        reason = ex.Message;
        return false;
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
