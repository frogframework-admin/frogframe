using System.Text.Json;
using Framework.Api;
using Framework.Dashboards;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresQueryApiStore(NpgsqlDataSource db) : IQueryApiStore
{
    private const string Cols = "tenant_id, id, slug, name, description, config::text, source_ref, created_by, created_at, updated_at, version";

    private static QueryApiDef Read(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetString(0), Id = r.GetString(1), Slug = r.GetString(2), Name = r.GetString(3), Description = r.IsDBNull(4) ? null : r.GetString(4),
        Config = JsonSerializer.Deserialize<QueryApiConfig>(r.GetString(5), DashboardJson.Options) ?? new(), SourceRef = r.IsDBNull(6) ? null : r.GetString(6),
        CreatedBy = r.GetString(7), CreatedAt = r.GetFieldValue<DateTimeOffset>(8), UpdatedAt = r.GetFieldValue<DateTimeOffset>(9), Version = r.GetInt32(10),
    };

    private async Task<List<QueryApiDef>> Query(string where, params object[] args)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_query_apis WHERE {where} ORDER BY slug");
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        var list = new List<QueryApiDef>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(Read(r));
        return list;
    }

    public async Task<IReadOnlyList<QueryApiDef>> ListAsync(string tenantId, CancellationToken ct = default) => await Query("tenant_id = $1", tenantId);
    public async Task<QueryApiDef?> GetAsync(string tenantId, string id, CancellationToken ct = default) => (await Query("tenant_id = $1 AND id = $2", tenantId, id)).FirstOrDefault();
    public async Task<QueryApiDef?> GetBySlugAsync(string tenantId, string slug, CancellationToken ct = default) => (await Query("tenant_id = $1 AND slug = $2", tenantId, slug)).FirstOrDefault();

    public async Task SaveAsync(QueryApiDef d, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_query_apis ({Cols.Replace("::text", "")}) VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7,$8,$9,$10,$11) " +
            "ON CONFLICT (tenant_id, id) DO UPDATE SET slug=$3, name=$4, description=$5, config=$6::jsonb, source_ref=$7, updated_at=$10, version=$11");
        cmd.Parameters.AddWithValue(d.TenantId); cmd.Parameters.AddWithValue(d.Id); cmd.Parameters.AddWithValue(d.Slug); cmd.Parameters.AddWithValue(d.Name);
        cmd.Parameters.AddWithValue((object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(d.Config, DashboardJson.Options));
        cmd.Parameters.AddWithValue((object?)d.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue(d.CreatedBy); cmd.Parameters.AddWithValue(d.CreatedAt); cmd.Parameters.AddWithValue(d.UpdatedAt); cmd.Parameters.AddWithValue(d.Version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM docframe_query_apis WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
