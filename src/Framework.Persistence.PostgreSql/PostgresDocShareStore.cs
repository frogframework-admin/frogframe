using Framework.Sharing;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>Row sharing grants in <c>frogframe_doc_shares</c> (migration 037).</summary>
public sealed class PostgresDocShareStore(NpgsqlDataSource db) : IDocShareStore
{
    private const string Columns = "tenant_id, doctype, doc_name, user_id, can_write, can_share, shared_by, shared_at";

    public async Task<IReadOnlyList<DocShare>> ForDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {Columns} FROM frogframe_doc_shares WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 ORDER BY shared_at, LOWER(user_id)");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);
        var list = new List<DocShare>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(Read(r));
        return list;
    }

    public async Task<DocShare?> FindAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            $"SELECT {Columns} FROM frogframe_doc_shares WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 AND LOWER(user_id) = LOWER($4) LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);
        cmd.Parameters.AddWithValue(userId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    public async Task<IReadOnlyList<string>> SharedNamesAsync(string tenantId, string docType, string userId, bool writeOnly, int max, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT doc_name FROM frogframe_doc_shares WHERE tenant_id = $1 AND doctype = $2 AND LOWER(user_id) = LOWER($3)" +
            (writeOnly ? " AND can_write" : "") + " ORDER BY doc_name LIMIT $4");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(max);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
        return list;
    }

    public async Task UpsertAsync(DocShare share, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_doc_shares (tenant_id, doctype, doc_name, user_id, can_write, can_share, shared_by, shared_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (tenant_id, doctype, doc_name, LOWER(user_id)) DO UPDATE SET
                can_write = EXCLUDED.can_write,
                can_share = EXCLUDED.can_share,
                shared_by = EXCLUDED.shared_by");
        cmd.Parameters.AddWithValue(share.TenantId);
        cmd.Parameters.AddWithValue(share.DocType);
        cmd.Parameters.AddWithValue(share.DocName);
        cmd.Parameters.AddWithValue(share.UserId);
        cmd.Parameters.AddWithValue(share.CanWrite);
        cmd.Parameters.AddWithValue(share.CanShare);
        cmd.Parameters.AddWithValue(share.SharedBy);
        cmd.Parameters.AddWithValue(share.SharedAt);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string tenantId, string docType, string docName, string userId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "DELETE FROM frogframe_doc_shares WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3 AND LOWER(user_id) = LOWER($4)");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);
        cmd.Parameters.AddWithValue(userId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<int> RemoveDocumentAsync(string tenantId, string docType, string docName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_doc_shares WHERE tenant_id = $1 AND doctype = $2 AND doc_name = $3");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DocShare Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7));
}
