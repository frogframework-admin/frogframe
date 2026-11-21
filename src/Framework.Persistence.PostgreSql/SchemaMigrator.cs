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

    /// <summary>Migrations not applied yet, in order (all of them when the database is new).</summary>
    public async Task<IReadOnlyList<(int Version, string Name)>> PendingAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenReadyConnectionAsync(ct);
        var applied = new HashSet<int>();
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('frogframe_schema_migrations') IS NOT NULL", conn))
            if ((bool)(await exists.ExecuteScalarAsync(ct))!)
            {
                await using var cmd = new NpgsqlCommand("SELECT version FROM frogframe_schema_migrations", conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
            }
        return Scripts().Where(s => !applied.Contains(s.Version)).OrderBy(s => s.Version).Select(s => (s.Version, s.Name)).ToList();
    }

    /// <summary>
    /// For an application that does not migrate on startup (a separate migrate step does): waits until no migration
    /// is pending, reporting the wait on the startup page, and fails after <paramref name="timeout"/>.
    /// </summary>
    public async Task WaitUntilMigratedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var pending = await PendingAsync(ct);
            if (pending.Count == 0) return;
            var what = $"{pending.Count} database migration(s) pending, first {pending[0].Version:000} {pending[0].Name}";
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"{what}. This instance does not migrate on startup (Database:MigrateOnStartup=false): run the migrate step " +
                    "(\"dotnet Apps.Erp.dll --migrate-only\", or the 'migrate' service in docker compose) and start it again.");
            startup?.ReportDatabaseWaiting($"{what}; waiting for the migrate step");
            log.LogWarning("{What}; waiting for the migrate step (Database:MigrateOnStartup=false).", what);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
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
            // Any other server error (wrong password, unknown database, missing permission) will not fix itself:
            // fail now instead of retrying for minutes.
            reason = $"SQLSTATE {pg.SqlState}: {pg.MessageText}";
            return false;
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
