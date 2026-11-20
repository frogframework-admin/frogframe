using Framework.Documents;
using Framework.Printing;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

/// <summary>Letterheads and print formats in <c>frogframe_print_designs</c> (migration 039).</summary>
public sealed class PostgresPrintDesignStore(NpgsqlDataSource db) : IPrintDesignStore
{
    private const string Columns = "id, tenant_id, kind, doctype, name, is_default, definition::text, version, modified_at, modified_by";

    public async Task<IReadOnlyList<PrintDesignRecord>> ListAsync(string tenantId, string kind, string? docType, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {Columns} FROM frogframe_print_designs WHERE tenant_id = $1 AND kind = $2 AND ($3::text IS NULL OR doctype = $3) ORDER BY LOWER(name), id");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)docType ?? DBNull.Value);
        var list = new List<PrintDesignRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Read(r));
        return list;
    }

    public async Task<PrintDesignRecord?> GetAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Columns} FROM frogframe_print_designs WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task SaveAsync(PrintDesignRecord rec, int? expectedVersion, CancellationToken ct = default)
    {
        await using var cmd = expectedVersion is null
            ? db.CreateCommand(@"
                INSERT INTO frogframe_print_designs (id, tenant_id, kind, doctype, name, is_default, definition, version, modified_at, modified_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10) ON CONFLICT (id) DO NOTHING")
            : db.CreateCommand(@"
                UPDATE frogframe_print_designs SET kind = $3, doctype = $4, name = $5, is_default = $6, definition = $7::jsonb,
                    version = $8, modified_at = $9, modified_by = $10
                WHERE id = $1 AND tenant_id = $2 AND version = $11");
        cmd.Parameters.AddWithValue(rec.Id);
        cmd.Parameters.AddWithValue(rec.TenantId);
        cmd.Parameters.AddWithValue(rec.Kind);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)rec.DocType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(rec.Name);
        cmd.Parameters.AddWithValue(rec.IsDefault);
        cmd.Parameters.AddWithValue(rec.Json);
        cmd.Parameters.AddWithValue(rec.Version);
        cmd.Parameters.AddWithValue(rec.ModifiedAt);
        cmd.Parameters.AddWithValue(rec.ModifiedBy);
        if (expectedVersion is { } v) cmd.Parameters.AddWithValue(v);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new ConcurrencyException(rec.Name, expectedVersion ?? 0, -1);
    }

    public async Task<bool> DeleteAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_print_designs WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static PrintDesignRecord Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetBoolean(5),
        r.GetString(6), r.GetInt32(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9));
}
