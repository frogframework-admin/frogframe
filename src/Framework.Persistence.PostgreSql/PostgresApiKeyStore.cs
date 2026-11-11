using Framework.Security;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresApiKeyStore(NpgsqlDataSource db) : IApiKeyStore
{
    private const string Cols = "id, tenant_id, user_id, name, secret_hash, scopes, allowed_ips, created_by, created_at, expires_at, revoked_at, last_used_at, last_used_ip";

    private static ApiKey Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), TenantId = r.GetString(1), UserId = r.GetString(2), Name = r.GetString(3), SecretHash = r.GetString(4),
        Scopes = r.GetFieldValue<string[]>(5).ToList(), AllowedIps = r.GetFieldValue<string[]>(6).ToList(), CreatedBy = r.GetString(7),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(8),
        ExpiresAt = r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), RevokedAt = r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        LastUsedAt = r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), LastUsedIp = r.IsDBNull(12) ? null : r.GetString(12),
    };

    public async Task<ApiKey?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_api_keys WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM frogframe_api_keys WHERE tenant_id = $1 ORDER BY created_at DESC");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<ApiKey>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task SaveAsync(ApiKey k, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO frogframe_api_keys ({Cols}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13) " +
            // revoked_at uses COALESCE: a previously revoked key cannot be reactivated by saving a stale instance.
            "ON CONFLICT (id) DO UPDATE SET name=$4, scopes=$6, allowed_ips=$7, expires_at=$10, " +
            "revoked_at=COALESCE(frogframe_api_keys.revoked_at, EXCLUDED.revoked_at), last_used_at=$12, last_used_ip=$13");
        object N(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue(k.Id); cmd.Parameters.AddWithValue(k.TenantId); cmd.Parameters.AddWithValue(k.UserId);
        cmd.Parameters.AddWithValue(k.Name); cmd.Parameters.AddWithValue(k.SecretHash);
        cmd.Parameters.AddWithValue(k.Scopes.ToArray()); cmd.Parameters.AddWithValue(k.AllowedIps.ToArray());
        cmd.Parameters.AddWithValue(k.CreatedBy); cmd.Parameters.AddWithValue(k.CreatedAt);
        cmd.Parameters.AddWithValue(N(k.ExpiresAt)); cmd.Parameters.AddWithValue(N(k.RevokedAt));
        cmd.Parameters.AddWithValue(N(k.LastUsedAt)); cmd.Parameters.AddWithValue(N(k.LastUsedIp));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordUsageAsync(string id, DateTimeOffset usedAt, string? ip, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE frogframe_api_keys SET last_used_at = $2, last_used_ip = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(usedAt);
        cmd.Parameters.AddWithValue((object?)ip ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
