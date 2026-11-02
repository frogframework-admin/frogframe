using Framework.Observability;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>
/// PostgreSQL store for capturing unhandled exceptions and error logs across HTTP requests, API calls, and background jobs.
/// </summary>
public sealed class PostgresErrorLogStore(NpgsqlDataSource db) : IErrorLogStore
{
    public async Task LogAsync(ErrorLogEntry entry, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO docframe_error_logs (id, tenant_id, timestamp, method, path, user_id, error_type, message, stack_trace, seen) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.AddWithValue(entry.Id);
        cmd.Parameters.AddWithValue(entry.TenantId);
        cmd.Parameters.AddWithValue(entry.Timestamp);
        cmd.Parameters.AddWithValue(entry.Method);
        cmd.Parameters.AddWithValue(entry.Path);
        cmd.Parameters.AddWithValue((object?)entry.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.ErrorType);
        cmd.Parameters.AddWithValue(entry.Message);
        cmd.Parameters.AddWithValue((object?)entry.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.Seen);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ErrorLogEntry>> GetRecentAsync(string? tenantId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, timestamp, method, path, user_id, error_type, message, stack_trace, seen " +
            "FROM docframe_error_logs " +
            "WHERE ($1 IS NULL OR tenant_id = $1) " +
            "ORDER BY timestamp DESC LIMIT $2");
        cmd.Parameters.AddWithValue((object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(limit);

        var list = new List<ErrorLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ErrorLogEntry
            {
                Id = reader.GetGuid(0),
                TenantId = reader.GetString(1),
                Timestamp = reader.GetFieldValue<DateTimeOffset>(2),
                Method = reader.GetString(3),
                Path = reader.GetString(4),
                UserId = reader.IsDBNull(5) ? null : reader.GetString(5),
                ErrorType = reader.GetString(6),
                Message = reader.GetString(7),
                StackTrace = reader.IsDBNull(8) ? null : reader.GetString(8),
                Seen = reader.GetBoolean(9)
            });
        }
        return list;
    }

    public async Task MarkSeenAsync(Guid id, string? tenantId = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE docframe_error_logs SET seen = true WHERE id = $1 AND ($2 IS NULL OR tenant_id = $2)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue((object?)tenantId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(string? tenantId = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "DELETE FROM docframe_error_logs WHERE ($1 IS NULL OR tenant_id = $1)");
        cmd.Parameters.AddWithValue((object?)tenantId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

