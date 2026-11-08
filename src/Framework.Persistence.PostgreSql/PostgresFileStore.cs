using Framework.Files;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresFileStore(NpgsqlDataSource db) : IFileStore
{
    private const string InfoColumns = "id, tenant_id, uploaded_by, content_type, size, sha256, created_at, doctype, doc_name, field";

    private static FileRecord Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), TenantId = r.GetString(1), UploadedBy = r.GetString(2), ContentType = r.GetString(3),
        Size = r.GetInt64(4), Sha256 = r.GetString(5), CreatedAt = r.GetFieldValue<DateTimeOffset>(6),
        DocType = r.IsDBNull(7) ? null : r.GetString(7), DocName = r.IsDBNull(8) ? null : r.GetString(8), Field = r.IsDBNull(9) ? null : r.GetString(9),
    };

    public async Task SaveAsync(FileRecord f, byte[] content, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"INSERT INTO frogframe_files ({InfoColumns}, content) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)");
        cmd.Parameters.AddWithValue(f.Id); cmd.Parameters.AddWithValue(f.TenantId); cmd.Parameters.AddWithValue(f.UploadedBy);
        cmd.Parameters.AddWithValue(f.ContentType); cmd.Parameters.AddWithValue(f.Size); cmd.Parameters.AddWithValue(f.Sha256);
        cmd.Parameters.AddWithValue(f.CreatedAt); cmd.Parameters.AddWithValue((object?)f.DocType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)f.DocName ?? DBNull.Value); cmd.Parameters.AddWithValue((object?)f.Field ?? DBNull.Value);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {InfoColumns} FROM frogframe_files WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {InfoColumns}, content FROM frogframe_files WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (Read(r), r.GetFieldValue<byte[]>(10)) : null;
    }

    public async Task<int> PruneOrphansAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Hanya file yang belum pernah ditautkan (doctype kosong) dan sudah lewat batas waktu.
        await using var cmd = db.CreateCommand(
            "DELETE FROM frogframe_files WHERE id IN (SELECT id FROM frogframe_files WHERE doctype IS NULL AND created_at < $1 LIMIT 500)");
        cmd.Parameters.AddWithValue(cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE frogframe_files SET doctype = $3, doc_name = $4, field = $5 WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId); cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName); cmd.Parameters.AddWithValue(field);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
