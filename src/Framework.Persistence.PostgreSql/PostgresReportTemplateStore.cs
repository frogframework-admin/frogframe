using System.Text.Json;
using Framework.Dashboards;
using Framework.Reports;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresReportTemplateStore(NpgsqlDataSource db) : IReportTemplateStore
{
    private const string Cols = "tenant_id, id, name, description, config::text, template_file_name, created_by, created_at, updated_at, version";

    private static ReportTemplateDef Read(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetString(0), Id = r.GetString(1), Name = r.GetString(2), Description = r.IsDBNull(3) ? null : r.GetString(3),
        Config = JsonSerializer.Deserialize<ReportTemplateConfig>(r.GetString(4), DashboardJson.Options) ?? new(),
        TemplateFileName = r.GetString(5), CreatedBy = r.GetString(6), CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(8), Version = r.GetInt32(9),
    };

    public async Task<IReadOnlyList<ReportTemplateDef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_report_templates WHERE tenant_id = $1 ORDER BY name");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<ReportTemplateDef>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<ReportTemplateDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Cols} FROM docframe_report_templates WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<byte[]?> GetFileAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT template FROM docframe_report_templates WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task SaveAsync(ReportTemplateDef d, byte[]? file, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(file is null
            ? "UPDATE docframe_report_templates SET name=$3, description=$4, config=$5::jsonb, updated_at=$6, version=$7 WHERE tenant_id=$1 AND id=$2"
            : "INSERT INTO docframe_report_templates (tenant_id, id, name, description, config, template_file_name, template, created_by, created_at, updated_at, version) " +
              "VALUES ($1,$2,$3,$4,$5::jsonb,$8,$9,$10,$11,$6,$7) ON CONFLICT (tenant_id, id) DO UPDATE SET name=$3, description=$4, config=$5::jsonb, " +
              "template_file_name=$8, template=$9, updated_at=$6, version=$7");
        cmd.Parameters.AddWithValue(d.TenantId); cmd.Parameters.AddWithValue(d.Id); cmd.Parameters.AddWithValue(d.Name);
        cmd.Parameters.AddWithValue((object?)d.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(d.Config, DashboardJson.Options));
        cmd.Parameters.AddWithValue(d.UpdatedAt); cmd.Parameters.AddWithValue(d.Version);
        if (file is not null)
        {
            cmd.Parameters.AddWithValue(d.TemplateFileName); cmd.Parameters.AddWithValue(file);
            cmd.Parameters.AddWithValue(d.CreatedBy); cmd.Parameters.AddWithValue(d.CreatedAt);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM docframe_report_templates WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
