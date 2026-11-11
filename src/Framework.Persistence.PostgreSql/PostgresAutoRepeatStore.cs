using Framework.Integrations;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresAutoRepeatStore(NpgsqlDataSource db) : IAutoRepeatStore
{
    public async Task<IReadOnlyList<AutoRepeatDef>> ListAsync(string tenantId, string? docType = null, AutoRepeatStatus? status = null, CancellationToken ct = default)
    {
        var sql = @"SELECT id, tenant_id, name, reference_doctype, reference_docname, frequency,
                           start_date, end_date, next_schedule_date, status, run_as_user_id,
                           notify_by_email, recipients, submit_on_creation, created_at, modified_at
                    FROM frogframe_auto_repeats
                    WHERE tenant_id = $1";

        if (!string.IsNullOrEmpty(docType)) sql += " AND reference_doctype = $2";
        if (status.HasValue) sql += " AND status = " + (string.IsNullOrEmpty(docType) ? "$2" : "$3");
        sql += " ORDER BY modified_at DESC";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);
        if (!string.IsNullOrEmpty(docType)) cmd.Parameters.AddWithValue(docType);
        if (status.HasValue) cmd.Parameters.AddWithValue(status.Value.ToString());

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AutoRepeatDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAutoRepeat(r));
        }
        return list;
    }

    public async Task<AutoRepeatDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, reference_doctype, reference_docname, frequency,
                   start_date, end_date, next_schedule_date, status, run_as_user_id,
                   notify_by_email, recipients, submit_on_creation, created_at, modified_at
            FROM frogframe_auto_repeats
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadAutoRepeat(r);
        }
        return null;
    }

    public async Task<AutoRepeatDef?> GetByReferenceAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, reference_doctype, reference_docname, frequency,
                   start_date, end_date, next_schedule_date, status, run_as_user_id,
                   notify_by_email, recipients, submit_on_creation, created_at, modified_at
            FROM frogframe_auto_repeats
            WHERE tenant_id = $1 AND reference_doctype = $2 AND reference_docname = $3
            LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadAutoRepeat(r);
        }
        return null;
    }

    public async Task SaveAsync(AutoRepeatDef def, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_auto_repeats
                (id, tenant_id, name, reference_doctype, reference_docname, frequency,
                 start_date, end_date, next_schedule_date, status, run_as_user_id,
                 notify_by_email, recipients, submit_on_creation, created_at, modified_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
            ON CONFLICT (tenant_id, id) DO UPDATE SET
                name = EXCLUDED.name,
                reference_doctype = EXCLUDED.reference_doctype,
                reference_docname = EXCLUDED.reference_docname,
                frequency = EXCLUDED.frequency,
                start_date = EXCLUDED.start_date,
                end_date = EXCLUDED.end_date,
                next_schedule_date = EXCLUDED.next_schedule_date,
                status = EXCLUDED.status,
                run_as_user_id = EXCLUDED.run_as_user_id,
                notify_by_email = EXCLUDED.notify_by_email,
                recipients = EXCLUDED.recipients,
                submit_on_creation = EXCLUDED.submit_on_creation,
                modified_at = EXCLUDED.modified_at");

        cmd.Parameters.AddWithValue(def.Id);
        cmd.Parameters.AddWithValue(def.TenantId);
        cmd.Parameters.AddWithValue(def.Name);
        cmd.Parameters.AddWithValue(def.ReferenceDocType);
        cmd.Parameters.AddWithValue(def.ReferenceDocName);
        cmd.Parameters.AddWithValue(def.Frequency.ToString());
        cmd.Parameters.AddWithValue(def.StartDate);
        cmd.Parameters.AddWithValue((object?)def.EndDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue(def.NextScheduleDate);
        cmd.Parameters.AddWithValue(def.Status.ToString());
        cmd.Parameters.AddWithValue(def.RunAsUserId);
        cmd.Parameters.AddWithValue(def.NotifyByEmail);
        cmd.Parameters.AddWithValue((string[])def.Recipients.ToArray());
        cmd.Parameters.AddWithValue(def.SubmitOnCreation);
        cmd.Parameters.AddWithValue(def.CreatedAt);
        cmd.Parameters.AddWithValue(def.ModifiedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_auto_repeats WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutoRepeatDef>> ListDueAsync(string tenantId, DateOnly today, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, reference_doctype, reference_docname, frequency,
                   start_date, end_date, next_schedule_date, status, run_as_user_id,
                   notify_by_email, recipients, submit_on_creation, created_at, modified_at
            FROM frogframe_auto_repeats
            WHERE tenant_id = $1 AND status = 'Active' AND next_schedule_date <= $2
            ORDER BY next_schedule_date ASC");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(today);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AutoRepeatDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAutoRepeat(r));
        }
        return list;
    }

    public async Task<bool> RecordRunAsync(AutoRepeatRun run, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_auto_repeat_runs
                (id, tenant_id, auto_repeat_id, scheduled_for, generated_docname, status, error, executed_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (tenant_id, auto_repeat_id, scheduled_for) DO NOTHING");

        cmd.Parameters.AddWithValue(run.Id);
        cmd.Parameters.AddWithValue(run.TenantId);
        cmd.Parameters.AddWithValue(run.AutoRepeatId);
        cmd.Parameters.AddWithValue(run.ScheduledFor);
        cmd.Parameters.AddWithValue(run.GeneratedDocName);
        cmd.Parameters.AddWithValue(run.Status);
        cmd.Parameters.AddWithValue((object?)run.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue(run.ExecutedAt);

        var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<AutoRepeatRun>> ListRunsAsync(string tenantId, string autoRepeatId, int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, auto_repeat_id, scheduled_for, generated_docname, status, error, executed_at
            FROM frogframe_auto_repeat_runs
            WHERE tenant_id = $1 AND auto_repeat_id = $2
            ORDER BY executed_at DESC
            LIMIT $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(autoRepeatId);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AutoRepeatRun>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AutoRepeatRun
            {
                Id = r.GetGuid(0),
                TenantId = r.GetString(1),
                AutoRepeatId = r.GetString(2),
                ScheduledFor = r.GetFieldValue<DateOnly>(3),
                GeneratedDocName = r.GetString(4),
                Status = r.GetString(5),
                Error = r.IsDBNull(6) ? null : r.GetString(6),
                ExecutedAt = r.GetFieldValue<DateTimeOffset>(7)
            });
        }
        return list;
    }

    private static AutoRepeatDef ReadAutoRepeat(NpgsqlDataReader r)
    {
        var freqStr = r.GetString(5);
        Enum.TryParse<AutoRepeatFrequency>(freqStr, ignoreCase: true, out var freq);

        var statusStr = r.GetString(9);
        Enum.TryParse<AutoRepeatStatus>(statusStr, ignoreCase: true, out var status);

        return new AutoRepeatDef
        {
            Id = r.GetString(0),
            TenantId = r.GetString(1),
            Name = r.GetString(2),
            ReferenceDocType = r.GetString(3),
            ReferenceDocName = r.GetString(4),
            Frequency = freq,
            StartDate = r.GetFieldValue<DateOnly>(6),
            EndDate = r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7),
            NextScheduleDate = r.GetFieldValue<DateOnly>(8),
            Status = status,
            RunAsUserId = r.GetString(10),
            NotifyByEmail = r.GetBoolean(11),
            Recipients = r.IsDBNull(12) ? [] : (string[])r.GetValue(12),
            SubmitOnCreation = r.GetBoolean(13),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(14),
            ModifiedAt = r.GetFieldValue<DateTimeOffset>(15)
        };
    }
}

