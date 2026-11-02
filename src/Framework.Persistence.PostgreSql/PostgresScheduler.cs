using System.Security.Cryptography;
using System.Text;
using Framework.Jobs;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IDistributedLock using session-level pg_try_advisory_lock.
/// Holds an open connection while the lock is active; automatically releases upon dispose or connection termination.
/// </summary>
public sealed class PostgresDistributedLock(NpgsqlDataSource dataSource, ILogger<PostgresDistributedLock>? logger = null) : IDistributedLock
{
    public static long HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt64(bytes, 0);
    }

    public async Task<IDisposable?> TryAcquireAsync(string lockKey, TimeSpan timeout = default, CancellationToken ct = default)
    {
        var lockId = HashKey(lockKey);
        var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        bool acquired = false;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock($1)";
            cmd.Parameters.AddWithValue(lockId);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            acquired = result is true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error attempting to acquire advisory lock for key {LockKey} (id {LockId})", lockKey, lockId);
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!acquired)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        return new PostgresLockHandle(conn, lockId, lockKey, logger);
    }

    private sealed class PostgresLockHandle(NpgsqlConnection conn, long lockId, string lockKey, ILogger? logger) : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock($1)";
                cmd.Parameters.AddWithValue(lockId);
                cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to unlock advisory lock {LockKey} explicitly", lockKey);
            }
            finally
            {
                conn.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock($1)";
                cmd.Parameters.AddWithValue(lockId);
                await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to unlock advisory lock {LockKey} explicitly", lockKey);
            }
            finally
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Discovers active tenants from docframe_users table in PostgreSQL.
/// </summary>
public sealed class PostgresTenantProvider(NpgsqlDataSource dataSource, string defaultTenant = "acme") : ITenantProvider
{
    public async Task<IReadOnlyList<string>> GetActiveTenantsAsync(CancellationToken ct = default)
    {
        var list = new List<string>();
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT tenant_id FROM docframe_users WHERE is_active = true ORDER BY tenant_id";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var t = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(t))
                    list.Add(t);
            }
        }
        catch
        {
            // Fallback to defaultTenant if table does not exist yet or query fails
        }

        return list.Count > 0 ? list : [defaultTenant];
    }
}

/// <summary>
/// PostgreSQL store for recording scheduled job runs and looking up the last execution.
/// </summary>
public sealed class PostgresScheduledJobLogStore(NpgsqlDataSource db) : IScheduledJobLogStore
{
    public async Task<DateTimeOffset?> GetLastRunAsync(string jobName, string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT started_at FROM docframe_scheduled_job_runs WHERE job_name = $1 AND tenant_id = $2 ORDER BY started_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue(jobName);
        cmd.Parameters.AddWithValue(tenantId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is DateTimeOffset dto) return dto;
        if (result is DateTime dt) return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    public async Task RecordRunAsync(ScheduledJobRunEntry entry, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO docframe_scheduled_job_runs (job_name, tenant_id, started_at, completed_at, duration_ms, status, error) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7)");
        cmd.Parameters.AddWithValue(entry.JobName);
        cmd.Parameters.AddWithValue(entry.TenantId);
        cmd.Parameters.AddWithValue(entry.StartedAt);
        cmd.Parameters.AddWithValue((object?)entry.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.Status);
        cmd.Parameters.AddWithValue((object?)entry.Error ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduledJobRunEntry>> GetRecentRunsAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT job_name, tenant_id, started_at, completed_at, duration_ms, status, error " +
            "FROM docframe_scheduled_job_runs " +
            "WHERE ($1 IS NULL OR tenant_id = $1) " +
            "ORDER BY started_at DESC LIMIT $2");
        cmd.Parameters.AddWithValue((object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(limit);

        var list = new List<ScheduledJobRunEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ScheduledJobRunEntry
            {
                JobName = reader.GetString(0),
                TenantId = reader.GetString(1),
                StartedAt = reader.GetFieldValue<DateTimeOffset>(2),
                CompletedAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                DurationMs = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Status = reader.GetString(5),
                Error = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return list;
    }
}

