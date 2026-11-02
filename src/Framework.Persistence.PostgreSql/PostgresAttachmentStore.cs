using Framework.Files;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresAttachmentStore(NpgsqlDataSource db) : IAttachmentStore
{
    private const string InfoColumns = "id, tenant_id, doctype, doc_name, file_name, content_type, size, sha256, uploaded_by, created_at, is_private";

    private static AttachmentRecord Read(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        TenantId = r.GetString(1),
        DocType = r.GetString(2),
        DocName = r.GetString(3),
        FileName = r.GetString(4),
        ContentType = r.GetString(5),
        Size = r.GetInt64(6),
        Sha256 = r.GetString(7),
        UploadedBy = r.GetString(8),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
        IsPrivate = r.GetBoolean(10),
    };

    public async Task SaveAsync(AttachmentRecord record, byte[] content, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO docframe_attachments ({InfoColumns}, content) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)");
        cmd.Parameters.AddWithValue(record.Id);
        cmd.Parameters.AddWithValue(record.TenantId);
        cmd.Parameters.AddWithValue(record.DocType);
        cmd.Parameters.AddWithValue(record.DocName);
        cmd.Parameters.AddWithValue(record.FileName);
        cmd.Parameters.AddWithValue(record.ContentType);
        cmd.Parameters.AddWithValue(record.Size);
        cmd.Parameters.AddWithValue(record.Sha256);
        cmd.Parameters.AddWithValue(record.UploadedBy);
        cmd.Parameters.AddWithValue(record.CreatedAt);
        cmd.Parameters.AddWithValue(record.IsPrivate);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AttachmentRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {InfoColumns} FROM docframe_attachments WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<(AttachmentRecord Record, byte[] Content)?> GetContentAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {InfoColumns}, content FROM docframe_attachments WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (Read(r), r.GetFieldValue<byte[]>(11)) : null;
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ListAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {InfoColumns} FROM docframe_attachments WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 ORDER BY created_at DESC");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AttachmentRecord>();
        while (await r.ReadAsync(ct))
            list.Add(Read(r));
        return list;
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM docframe_attachments WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

