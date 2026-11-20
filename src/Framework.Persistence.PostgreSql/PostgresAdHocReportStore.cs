using System.Text.Json;
using Framework.Documents;
using Framework.Reports;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>Saved ad-hoc reports in <c>frogframe_adhoc_reports</c> (migration 038); the definition is stored as JSONB.</summary>
public sealed class PostgresAdHocReportStore(NpgsqlDataSource db) : IAdHocReportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Columns = "id, tenant_id, name, description, owner, shared_roles, definition::text, created_at, modified_at, version";

    public async Task<IReadOnlyList<SavedAdHocReport>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_adhoc_reports WHERE tenant_id = $1 ORDER BY LOWER(name), id");
        cmd.Parameters.AddWithValue(tenantId);
        var list = new List<SavedAdHocReport>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Read(r));
        return list;
    }

    public async Task<SavedAdHocReport?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_adhoc_reports WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task SaveAsync(SavedAdHocReport report, int? expectedVersion, CancellationToken ct = default)
    {
        await using var cmd = expectedVersion is null
            ? db.CreateCommand(@"
                INSERT INTO frogframe_adhoc_reports (id, tenant_id, name, description, owner, shared_roles, definition, created_at, modified_at, version)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10)
                ON CONFLICT (id) DO NOTHING")
            : db.CreateCommand(@"
                UPDATE frogframe_adhoc_reports SET name = $3, description = $4, owner = $5, shared_roles = $6, definition = $7::jsonb,
                    created_at = $8, modified_at = $9, version = $10
                WHERE id = $1 AND tenant_id = $2 AND version = $11");
        cmd.Parameters.AddWithValue(report.Id);
        cmd.Parameters.AddWithValue(report.TenantId);
        cmd.Parameters.AddWithValue(report.Name);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)report.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue(report.Owner);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Array | NpgsqlDbType.Text, report.SharedWithRoles.ToArray());
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(report.Definition, Json));
        cmd.Parameters.AddWithValue(report.CreatedAt);
        cmd.Parameters.AddWithValue(report.ModifiedAt);
        cmd.Parameters.AddWithValue(report.Version);
        if (expectedVersion is { } v) cmd.Parameters.AddWithValue(v);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new ConcurrencyException(report.Name, expectedVersion ?? 0, -1);
    }

    public async Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_adhoc_reports WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static SavedAdHocReport Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        TenantId = r.GetString(1),
        Name = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        Owner = r.GetString(4),
        SharedWithRoles = r.GetFieldValue<string[]>(5),
        Definition = JsonSerializer.Deserialize<AdHocReportDef>(r.GetString(6), Json) ?? new AdHocReportDef(),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
        ModifiedAt = r.GetFieldValue<DateTimeOffset>(8),
        Version = r.GetInt32(9),
    };
}
