using Framework.Scripting;
using Framework.Scripting.Abstractions;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresServerScriptStore(NpgsqlDataSource db) : IServerScriptStore
{
    public async Task<ServerScriptDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, script_type, doctype, doc_event, api_method,
                   cron_expr, run_as_user_id, is_active, version, current_code,
                   created_at, modified_at
            FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadScript(r);
        }
        return null;
    }

    public async Task<IReadOnlyList<ServerScriptDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, script_type, doctype, doc_event, api_method,
                   cron_expr, run_as_user_id, is_active, version, current_code,
                   created_at, modified_at
            FROM frogframe_server_scripts
            WHERE tenant_id = $1
            ORDER BY modified_at DESC");
        cmd.Parameters.AddWithValue(tenantId);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ServerScriptDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadScript(r));
        }
        return list;
    }

    public async Task<IReadOnlyList<ServerScriptDef>> ListDocEventScriptsAsync(string tenantId, string docType, DocEventStage docEvent, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, script_type, doctype, doc_event, api_method,
                   cron_expr, run_as_user_id, is_active, version, current_code,
                   created_at, modified_at
            FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND is_active = true AND script_type = 'DocEvent'
                  AND doctype = $2 AND doc_event = $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docEvent.ToString());

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ServerScriptDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadScript(r));
        }
        return list;
    }

    public async Task<ServerScriptDef?> GetApiScriptAsync(string tenantId, string apiMethod, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, script_type, doctype, doc_event, api_method,
                   cron_expr, run_as_user_id, is_active, version, current_code,
                   created_at, modified_at
            FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND is_active = true AND script_type = 'Api'
                  AND api_method = $2
            LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(apiMethod);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadScript(r);
        }
        return null;
    }

    public async Task<IReadOnlyList<ServerScriptDef>> ListScheduledScriptsAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, script_type, doctype, doc_event, api_method,
                   cron_expr, run_as_user_id, is_active, version, current_code,
                   created_at, modified_at
            FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND is_active = true AND script_type = 'Scheduled'
                  AND cron_expr IS NOT NULL AND cron_expr <> ''");
        cmd.Parameters.AddWithValue(tenantId);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ServerScriptDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadScript(r));
        }
        return list;
    }

    public async Task SaveAsync(ServerScriptDef def, string code, string userId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Check if existing script exists to manage version increment
        var currentVersion = def.Version;
        await using (var checkCmd = new NpgsqlCommand(@"
            SELECT version, current_code FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND id = $2", conn, tx))
        {
            checkCmd.Parameters.AddWithValue(def.TenantId);
            checkCmd.Parameters.AddWithValue(def.Id);
            await using var r = await checkCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var existingVer = r.GetInt32(0);
                var existingCode = r.GetString(1);
                currentVersion = existingCode != code ? existingVer + 1 : existingVer;
            }
        }

        // Upsert script definition
        await using (var upsertCmd = new NpgsqlCommand(@"
            INSERT INTO frogframe_server_scripts (
                id, tenant_id, name, script_type, doctype, doc_event, api_method,
                cron_expr, run_as_user_id, is_active, version, current_code,
                created_at, modified_at
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (tenant_id, id) DO UPDATE SET
                name = EXCLUDED.name,
                script_type = EXCLUDED.script_type,
                doctype = EXCLUDED.doctype,
                doc_event = EXCLUDED.doc_event,
                api_method = EXCLUDED.api_method,
                cron_expr = EXCLUDED.cron_expr,
                run_as_user_id = EXCLUDED.run_as_user_id,
                is_active = EXCLUDED.is_active,
                version = EXCLUDED.version,
                current_code = EXCLUDED.current_code,
                modified_at = EXCLUDED.modified_at", conn, tx))
        {
            upsertCmd.Parameters.AddWithValue(def.Id);
            upsertCmd.Parameters.AddWithValue(def.TenantId);
            upsertCmd.Parameters.AddWithValue(def.Name);
            upsertCmd.Parameters.AddWithValue(def.ScriptType.ToString());
            upsertCmd.Parameters.AddWithValue((object?)def.DocType ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue((object?)def.DocEvent?.ToString() ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue((object?)def.ApiMethod ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue((object?)def.CronExpr ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue((object?)def.RunAsUserId ?? DBNull.Value);
            upsertCmd.Parameters.AddWithValue(def.IsActive);
            upsertCmd.Parameters.AddWithValue(currentVersion);
            upsertCmd.Parameters.AddWithValue(code);
            upsertCmd.Parameters.AddWithValue(def.CreatedAt);
            upsertCmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

            await upsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Save historical version
        await using (var verCmd = new NpgsqlCommand(@"
            INSERT INTO frogframe_server_script_versions (
                id, tenant_id, script_id, version, code, created_by, created_at
            ) VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (tenant_id, script_id, version) DO UPDATE SET
                code = EXCLUDED.code,
                created_by = EXCLUDED.created_by", conn, tx))
        {
            verCmd.Parameters.AddWithValue($"{def.Id}_v{currentVersion}");
            verCmd.Parameters.AddWithValue(def.TenantId);
            verCmd.Parameters.AddWithValue(def.Id);
            verCmd.Parameters.AddWithValue(currentVersion);
            verCmd.Parameters.AddWithValue(code);
            verCmd.Parameters.AddWithValue(userId);
            verCmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

            await verCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task SetActiveAsync(string tenantId, string id, bool active, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_server_scripts
            SET is_active = $3, modified_at = $4
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(active);
        cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            DELETE FROM frogframe_server_scripts
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordRunAsync(ServerScriptRun run, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_server_script_runs (
                id, tenant_id, script_id, version, trigger_type, doc_name,
                status, duration_ms, error, executed_at
            ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.AddWithValue(run.Id);
        cmd.Parameters.AddWithValue(run.TenantId);
        cmd.Parameters.AddWithValue(run.ScriptId);
        cmd.Parameters.AddWithValue(run.Version);
        cmd.Parameters.AddWithValue(run.TriggerType);
        cmd.Parameters.AddWithValue((object?)run.DocName ?? DBNull.Value);
        cmd.Parameters.AddWithValue(run.Status);
        cmd.Parameters.AddWithValue(run.DurationMs);
        cmd.Parameters.AddWithValue((object?)run.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue(run.ExecutedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServerScriptRun>> ListRunsAsync(string tenantId, string scriptId, int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, script_id, version, trigger_type, doc_name,
                   status, duration_ms, error, executed_at
            FROM frogframe_server_script_runs
            WHERE tenant_id = $1 AND script_id = $2
            ORDER BY executed_at DESC
            LIMIT $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(scriptId);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ServerScriptRun>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ServerScriptRun
            {
                Id = r.GetGuid(0),
                TenantId = r.GetString(1),
                ScriptId = r.GetString(2),
                Version = r.GetInt32(3),
                TriggerType = r.GetString(4),
                DocName = r.IsDBNull(5) ? null : r.GetString(5),
                Status = r.GetString(6),
                DurationMs = r.GetInt32(7),
                Error = r.IsDBNull(8) ? null : r.GetString(8),
                ExecutedAt = r.GetFieldValue<DateTimeOffset>(9)
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<ServerScriptVersion>> ListVersionsAsync(string tenantId, string scriptId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, script_id, version, code, created_by, created_at
            FROM frogframe_server_script_versions
            WHERE tenant_id = $1 AND script_id = $2
            ORDER BY version DESC");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(scriptId);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ServerScriptVersion>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ServerScriptVersion
            {
                Id = r.GetString(0),
                TenantId = r.GetString(1),
                ScriptId = r.GetString(2),
                Version = r.GetInt32(3),
                Code = r.GetString(4),
                CreatedBy = r.GetString(5),
                CreatedAt = r.GetFieldValue<DateTimeOffset>(6)
            });
        }
        return list;
    }

    private static ServerScriptDef ReadScript(NpgsqlDataReader r)
    {
        Enum.TryParse<ServerScriptType>(r.GetString(3), ignoreCase: true, out var scriptType);
        DocEventStage? docEvent = null;
        if (!r.IsDBNull(5) && Enum.TryParse<DocEventStage>(r.GetString(5), ignoreCase: true, out var parsedEvent))
        {
            docEvent = parsedEvent;
        }

        return new ServerScriptDef
        {
            Id = r.GetString(0),
            TenantId = r.GetString(1),
            Name = r.GetString(2),
            ScriptType = scriptType,
            DocType = r.IsDBNull(4) ? null : r.GetString(4),
            DocEvent = docEvent,
            ApiMethod = r.IsDBNull(6) ? null : r.GetString(6),
            CronExpr = r.IsDBNull(7) ? null : r.GetString(7),
            RunAsUserId = r.IsDBNull(8) ? null : r.GetString(8),
            IsActive = r.GetBoolean(9),
            Version = r.GetInt32(10),
            CurrentCode = r.GetString(11),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(12),
            ModifiedAt = r.GetFieldValue<DateTimeOffset>(13)
        };
    }
}
