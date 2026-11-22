using System.Text.Json;
using Framework.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>Tenant registry in <c>frogframe_tenants</c> (migration 045).</summary>
public sealed class PostgresTenantStore(NpgsqlDataSource db) : ITenantStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Columns = "id, name, status, suspended_reason, suspended_at, settings::text, created_at, updated_at, updated_by";

    private static Tenant Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), Status = r.GetString(2),
        SuspendedReason = r.IsDBNull(3) ? null : r.GetString(3),
        SuspendedAt = r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        Settings = JsonSerializer.Deserialize<TenantSettings>(r.GetString(5), Json) ?? new(),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(6), UpdatedAt = r.GetFieldValue<DateTimeOffset>(7),
        UpdatedBy = r.IsDBNull(8) ? null : r.GetString(8),
    };

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_tenants ORDER BY lower(name), id");
        var list = new List<Tenant>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<Tenant?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_tenants WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task SaveAsync(Tenant t, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO frogframe_tenants (id, name, status, suspended_reason, suspended_at, settings, created_at, updated_at, updated_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, status = EXCLUDED.status, suspended_reason = EXCLUDED.suspended_reason,
                suspended_at = EXCLUDED.suspended_at, settings = EXCLUDED.settings, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue(t.Id);
        cmd.Parameters.AddWithValue(t.Name);
        cmd.Parameters.AddWithValue(t.Status);
        cmd.Parameters.AddWithValue((object?)t.SuspendedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)t.SuspendedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(t.Settings, Json));
        cmd.Parameters.AddWithValue(t.CreatedAt == default ? DateTimeOffset.UtcNow : t.CreatedAt);
        cmd.Parameters.AddWithValue(t.UpdatedAt == default ? DateTimeOffset.UtcNow : t.UpdatedAt);
        cmd.Parameters.AddWithValue((object?)t.UpdatedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// One query per source: user counts from <c>frogframe_users</c>, document counts and the last change from
    /// <c>frogframe_documents</c> (index on tenant_id, doctype, modified). The document count reads the whole index, so
    /// the tenant page caches the result.
    /// </summary>
    public async Task<IReadOnlyList<TenantUsage>> UsageAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH u AS (SELECT tenant_id, count(*) AS users, count(*) FILTER (WHERE is_active) AS active FROM frogframe_users GROUP BY tenant_id),
                 d AS (SELECT tenant_id, count(*) AS docs, max(modified) AS last FROM frogframe_documents GROUP BY tenant_id),
                 ids AS (SELECT id AS tenant_id FROM frogframe_tenants UNION SELECT tenant_id FROM u UNION SELECT tenant_id FROM d)
            SELECT ids.tenant_id, COALESCE(u.users, 0), COALESCE(u.active, 0), COALESCE(d.docs, 0), d.last
            FROM ids LEFT JOIN u USING (tenant_id) LEFT JOIN d USING (tenant_id)
            """);
        var list = new List<TenantUsage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TenantUsage(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4)));
        return list;
    }
}
