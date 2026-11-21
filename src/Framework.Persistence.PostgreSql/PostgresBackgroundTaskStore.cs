using Framework.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>Background tasks in frogframe_background_tasks (migration 041). Claims use FOR UPDATE SKIP LOCKED.</summary>
public sealed class PostgresBackgroundTaskStore(NpgsqlDataSource db) : IBackgroundTaskStore
{
    private const string Light = "id, tenant_id, user_id, kind, title, payload::text, status, progress, progress_message, result, output_name, error, " +
                                 "cancel_requested, created_at, started_at, finished_at, locked_until, locked_by";

    private static BackgroundTask Read(NpgsqlDataReader r, bool withInput = false) => new()
    {
        Id = r.GetGuid(0), TenantId = r.GetString(1), UserId = r.GetString(2), Kind = r.GetString(3), Title = r.GetString(4),
        Payload = r.GetString(5), Status = r.GetString(6), Progress = r.GetInt32(7),
        ProgressMessage = r.IsDBNull(8) ? null : r.GetString(8), Result = r.IsDBNull(9) ? null : r.GetString(9),
        OutputName = r.IsDBNull(10) ? null : r.GetString(10), Error = r.IsDBNull(11) ? null : r.GetString(11),
        CancelRequested = r.GetBoolean(12), CreatedAt = r.GetFieldValue<DateTimeOffset>(13),
        StartedAt = r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        FinishedAt = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        LockedUntil = r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16),
        LockedBy = r.IsDBNull(17) ? null : r.GetString(17),
        Input = withInput && !r.IsDBNull(18) ? r.GetFieldValue<byte[]>(18) : null,
    };

    private NpgsqlCommand Cmd(string sql, params object?[] args)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(a switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                byte[] b => new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = b },
                _ => new NpgsqlParameter { Value = a },
            });
        return cmd;
    }

    private static async Task<List<BackgroundTask>> All(NpgsqlCommand cmd, CancellationToken ct, bool withInput = false)
    {
        await using (cmd)
        {
            var list = new List<BackgroundTask>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(Read(r, withInput));
            return list;
        }
    }

    public async Task AddAsync(BackgroundTask t, CancellationToken ct = default)
    {
        await using var cmd = Cmd("INSERT INTO frogframe_background_tasks (id, tenant_id, user_id, kind, title, payload, input, status, created_at) " +
                                  "VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, $9)",
            t.Id, t.TenantId, t.UserId, t.Kind, t.Title, t.Payload, t.Input, t.Status, t.CreatedAt);
        if (t.Input is null) cmd.Parameters[6].NpgsqlDbType = NpgsqlDbType.Bytea;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<BackgroundTask?> GetAsync(string tenantId, Guid id, CancellationToken ct = default) =>
        (await All(Cmd($"SELECT {Light} FROM frogframe_background_tasks WHERE tenant_id = $1 AND id = $2", tenantId, id), ct)).FirstOrDefault();

    public async Task<BackgroundTask?> GetWithInputAsync(Guid id, CancellationToken ct = default) =>
        (await All(Cmd($"SELECT {Light}, input FROM frogframe_background_tasks WHERE id = $1", id), ct, withInput: true)).FirstOrDefault();

    public async Task<(byte[] Content, string Name)?> OutputAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = Cmd("SELECT output, output_name FROM frogframe_background_tasks WHERE tenant_id = $1 AND id = $2 AND output IS NOT NULL", tenantId, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetFieldValue<byte[]>(0), r.IsDBNull(1) ? "output" : r.GetString(1)) : null;
    }

    public async Task<IReadOnlyList<BackgroundTask>> ListAsync(string tenantId, string? userId, int take, CancellationToken ct = default) =>
        userId is null
            ? await All(Cmd($"SELECT {Light} FROM frogframe_background_tasks WHERE tenant_id = $1 ORDER BY created_at DESC LIMIT $2", tenantId, take), ct)
            : await All(Cmd($"SELECT {Light} FROM frogframe_background_tasks WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) ORDER BY created_at DESC LIMIT $3",
                tenantId, userId, take), ct);

    public async Task<int> CountActiveAsync(string tenantId, string userId, CancellationToken ct = default)
    {
        await using var cmd = Cmd("SELECT count(*)::int FROM frogframe_background_tasks WHERE tenant_id = $1 AND LOWER(user_id) = LOWER($2) " +
                                  "AND status IN ('Queued', 'Running')", tenantId, userId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<BackgroundTask?> ClaimAsync(string workerId, DateTimeOffset now, CancellationToken ct = default) =>
        (await All(Cmd($"""
            UPDATE frogframe_background_tasks SET status = 'Running', started_at = $2, locked_by = $1, locked_until = $3
            WHERE id = (SELECT id FROM frogframe_background_tasks WHERE status = 'Queued' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING {Light}
            """, workerId, now, now + BackgroundTaskLimits.Lease), ct)).FirstOrDefault();

    public async Task<bool> ProgressAsync(Guid id, string workerId, int progress, string? message, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        await using var cmd = Cmd("UPDATE frogframe_background_tasks SET progress = $3, progress_message = $4, locked_until = $5 " +
                                  "WHERE id = $1 AND locked_by = $2 AND status = 'Running' RETURNING cancel_requested",
            id, workerId, progress, message, leaseUntil);
        if (message is null) cmd.Parameters[3].NpgsqlDbType = NpgsqlDbType.Text;
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task FinishAsync(Guid id, string status, string? result, string? error, byte[]? output, string? outputName, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var cmd = Cmd("UPDATE frogframe_background_tasks SET status = $2, result = $3, error = $4, output = $5, output_name = $6, " +
                                  "finished_at = $7, locked_until = NULL, progress = CASE WHEN $2 = 'Succeeded' THEN 100 ELSE progress END WHERE id = $1",
            id, status, result, error, output, outputName, at);
        cmd.Parameters[2].NpgsqlDbType = NpgsqlDbType.Text; cmd.Parameters[3].NpgsqlDbType = NpgsqlDbType.Text;
        cmd.Parameters[4].NpgsqlDbType = NpgsqlDbType.Bytea; cmd.Parameters[5].NpgsqlDbType = NpgsqlDbType.Text;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RequestCancelAsync(string tenantId, Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var cmd = Cmd("""
            UPDATE frogframe_background_tasks
            SET cancel_requested = TRUE,
                status = CASE WHEN status = 'Queued' THEN 'Cancelled' ELSE status END,
                finished_at = CASE WHEN status = 'Queued' THEN $3 ELSE finished_at END
            WHERE tenant_id = $1 AND id = $2 AND status IN ('Queued', 'Running')
            """, tenantId, id, at);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<BackgroundTask>> InterruptExpiredAsync(DateTimeOffset now, CancellationToken ct = default) =>
        await All(Cmd($"""
            UPDATE frogframe_background_tasks SET status = 'Interrupted', finished_at = $1, locked_until = NULL,
                   error = 'The server stopped while this task was running.'
            WHERE status = 'Running' AND locked_until < $1
            RETURNING {Light}
            """, now), ct);

    public async Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken ct = default)
    {
        await using var cmd = Cmd("DELETE FROM frogframe_background_tasks WHERE finished_at < $1", finishedBefore);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
