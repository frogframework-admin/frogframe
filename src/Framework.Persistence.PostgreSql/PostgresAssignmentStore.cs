using System.Text;
using Framework.Assignments;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresAssignmentStore(NpgsqlDataSource db) : IAssignmentStore
{
    private const string Columns = @"
        id, tenant_id, doctype, doc_name, assigned_to, assigned_by, description,
        due_date, priority, status, created_at, closed_at, closed_by, close_note, version";

    public async Task<Assignment?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_assignments WHERE tenant_id = $1 AND id = $2 LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<Assignment>> QueryAsync(AssignmentQuery q, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM frogframe_assignments WHERE tenant_id = $1");
        await using var cmd = db.CreateCommand();
        cmd.Parameters.AddWithValue(q.TenantId);
        var idx = 2;

        if (q.AssignedTo is not null)
        {
            sql.Append($" AND LOWER(assigned_to) = LOWER(${idx++})");
            cmd.Parameters.AddWithValue(q.AssignedTo);
        }
        if (q.AssignedBy is not null)
        {
            sql.Append($" AND LOWER(assigned_by) = LOWER(${idx++})");
            cmd.Parameters.AddWithValue(q.AssignedBy);
        }
        if (q.Statuses is { Count: > 0 })
        {
            var statusInts = q.Statuses.Select(s => (short)s).ToArray();
            sql.Append($" AND status = ANY(${idx++})");
            cmd.Parameters.AddWithValue(statusInts);
        }
        if (q.DocType is not null)
        {
            sql.Append($" AND doctype = ${idx++}");
            cmd.Parameters.AddWithValue(q.DocType);
        }
        if (q.DocName is not null)
        {
            sql.Append($" AND doc_name = ${idx++}");
            cmd.Parameters.AddWithValue(q.DocName);
        }
        if (q.DueOnOrBefore is { } due)
        {
            sql.Append($" AND due_date IS NOT NULL AND due_date <= ${idx++}");
            cmd.Parameters.AddWithValue(due);
        }

        var take = Math.Clamp(q.Take, 1, AssignmentLimits.MaxQueryTake);
        sql.Append($@"
            ORDER BY CASE WHEN status = 0 THEN 0 ELSE 1 END,
                     CASE WHEN due_date IS NULL THEN 1 ELSE 0 END,
                     due_date ASC,
                     created_at DESC
            LIMIT ${idx}");
        cmd.Parameters.AddWithValue(take);
        cmd.CommandText = sql.ToString();

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Assignment>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(Read(r));
        return list;
    }

    public async Task<bool> AddAsync(Assignment a, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_assignments (
                id, tenant_id, doctype, doc_name, assigned_to, assigned_by, description,
                due_date, priority, status, created_at, closed_at, closed_by, close_note, version
            ) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            ON CONFLICT (tenant_id, doctype, doc_name, LOWER(assigned_to)) WHERE status = 0
            DO NOTHING");

        cmd.Parameters.AddWithValue(a.Id);
        cmd.Parameters.AddWithValue(a.TenantId);
        cmd.Parameters.AddWithValue(a.DocType);
        cmd.Parameters.AddWithValue(a.DocName);
        cmd.Parameters.AddWithValue(a.AssignedTo);
        cmd.Parameters.AddWithValue(a.AssignedBy);
        cmd.Parameters.AddWithValue((object?)a.Description ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)a.DueDate ?? DBNull.Value });
        cmd.Parameters.AddWithValue((short)a.Priority);
        cmd.Parameters.AddWithValue((short)a.Status);
        cmd.Parameters.AddWithValue(a.CreatedAt);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)a.ClosedAt ?? DBNull.Value });
        cmd.Parameters.AddWithValue((object?)a.ClosedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)a.CloseNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue(a.Version);

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> UpdateAsync(Assignment a, int expectedVersion, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_assignments
            SET assigned_to = $1,
                description = $2,
                due_date    = $3,
                priority    = $4,
                status      = $5,
                closed_at   = $6,
                closed_by   = $7,
                close_note  = $8,
                version     = version + 1
            WHERE id = $9 AND tenant_id = $10 AND version = $11");

        cmd.Parameters.AddWithValue(a.AssignedTo);
        cmd.Parameters.AddWithValue((object?)a.Description ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = (object?)a.DueDate ?? DBNull.Value });
        cmd.Parameters.AddWithValue((short)a.Priority);
        cmd.Parameters.AddWithValue((short)a.Status);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = (object?)a.ClosedAt ?? DBNull.Value });
        cmd.Parameters.AddWithValue((object?)a.ClosedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)a.CloseNote ?? DBNull.Value);
        cmd.Parameters.AddWithValue(a.Id);
        cmd.Parameters.AddWithValue(a.TenantId);
        cmd.Parameters.AddWithValue(expectedVersion);

        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<AssignmentCounts> CountOpenAsync(string tenantId, string userId, DateOnly today, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT
                COUNT(*)::INT,
                COUNT(*) FILTER (WHERE due_date = $3)::INT,
                COUNT(*) FILTER (WHERE due_date IS NOT NULL AND due_date < $3)::INT
            FROM frogframe_assignments
            WHERE tenant_id = $1 AND LOWER(assigned_to) = LOWER($2) AND status = 0");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(today);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
            return new AssignmentCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        return new AssignmentCounts(0, 0, 0);
    }

    private static Assignment Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetString(1),
        DocType = r.GetString(2),
        DocName = r.GetString(3),
        AssignedTo = r.GetString(4),
        AssignedBy = r.GetString(5),
        Description = r.IsDBNull(6) ? null : r.GetString(6),
        DueDate = r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7),
        Priority = (AssignmentPriority)r.GetInt16(8),
        Status = (AssignmentStatus)r.GetInt16(9),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(10),
        ClosedAt = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11),
        ClosedBy = r.IsDBNull(12) ? null : r.GetString(12),
        CloseNote = r.IsDBNull(13) ? null : r.GetString(13),
        Version = r.GetInt32(14)
    };
}
