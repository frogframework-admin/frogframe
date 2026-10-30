using Framework.Imports;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresImportLogStore(NpgsqlDataSource db) : IImportLogStore
{
    public async Task SaveAsync(ImportLog l, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO docframe_imports (id, tenant_id, doctype, file_name, atomic, total, succeeded, failed, created_names, user_id, user_name, created_at) " +
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)");
        foreach (var v in new object[] { l.Id, l.TenantId, l.DocType, l.FileName, l.Atomic, l.Total, l.Succeeded, l.Failed, l.CreatedNames.ToArray(), l.UserId, l.UserName, l.At })
            cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ImportLog>> ListAsync(string tenantId, string docType, int take, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, tenant_id, doctype, file_name, atomic, total, succeeded, failed, created_names, user_id, user_name, created_at " +
            "FROM docframe_imports WHERE tenant_id = $1 AND doctype = $2 ORDER BY created_at DESC LIMIT $3");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(docType); cmd.Parameters.AddWithValue(take);
        var list = new List<ImportLog>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ImportLog
            {
                Id = r.GetString(0), TenantId = r.GetString(1), DocType = r.GetString(2), FileName = r.GetString(3), Atomic = r.GetBoolean(4),
                Total = r.GetInt32(5), Succeeded = r.GetInt32(6), Failed = r.GetInt32(7), CreatedNames = r.GetFieldValue<string[]>(8).ToList(),
                UserId = r.GetString(9), UserName = r.GetString(10), At = r.GetFieldValue<DateTimeOffset>(11),
            });
        return list;
    }
}
