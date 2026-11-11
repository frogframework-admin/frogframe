using Framework.Communication;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresCommunicationStore(NpgsqlDataSource db) : ICommunicationStore
{
    public async Task<bool> ExistsMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT 1 FROM frogframe_communications
            WHERE tenant_id = $1 AND message_id = $2
            LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(messageId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    public async Task<CommunicationEntry?> GetByMessageIdAsync(string tenantId, string messageId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, reference_doctype, reference_name, direction,
                   message_id, in_reply_to, subject, sender, recipients, cc,
                   content_html, content_text, email_account_id, occurred_at, created_at
            FROM frogframe_communications
            WHERE tenant_id = $1 AND message_id = $2
            LIMIT 1");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(messageId);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadEntry(r);
        }
        return null;
    }

    public async Task<IReadOnlyList<CommunicationEntry>> ListForDocumentAsync(
        string tenantId,
        string docType,
        string docName,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, reference_doctype, reference_name, direction,
                   message_id, in_reply_to, subject, sender, recipients, cc,
                   content_html, content_text, email_account_id, occurred_at, created_at
            FROM frogframe_communications
            WHERE tenant_id = $1 AND reference_doctype = $2 AND reference_name = $3
            ORDER BY occurred_at DESC");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<CommunicationEntry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadEntry(r));
        }
        return list;
    }

    public async Task<IReadOnlyList<CommunicationEntry>> ListUnlinkedAsync(
        string tenantId,
        int limit = 50,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, reference_doctype, reference_name, direction,
                   message_id, in_reply_to, subject, sender, recipients, cc,
                   content_html, content_text, email_account_id, occurred_at, created_at
            FROM frogframe_communications
            WHERE tenant_id = $1 AND reference_doctype IS NULL
            ORDER BY created_at DESC
            LIMIT $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<CommunicationEntry>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadEntry(r));
        }
        return list;
    }

    public async Task LinkAsync(string tenantId, Guid communicationId, string docType, string docName, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_communications
            SET reference_doctype = $3,
                reference_name = $4
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(communicationId);
        cmd.Parameters.AddWithValue(docType);
        cmd.Parameters.AddWithValue(docName);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(CommunicationEntry entry, CancellationToken ct = default)
    {
        var sql = @"
            INSERT INTO frogframe_communications (
                id, tenant_id, reference_doctype, reference_name, direction,
                message_id, in_reply_to, subject, sender, recipients, cc,
                content_html, content_text, email_account_id, occurred_at, created_at
            ) VALUES (
                $1, $2, $3, $4, $5,
                $6, $7, $8, $9, $10, $11,
                $12, $13, $14, $15, $16
            )
            ON CONFLICT (tenant_id, message_id) DO NOTHING";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(entry.Id);
        cmd.Parameters.AddWithValue(entry.TenantId);
        cmd.Parameters.AddWithValue((object?)entry.ReferenceDocType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.ReferenceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.Direction.ToString());
        cmd.Parameters.AddWithValue(entry.MessageId);
        cmd.Parameters.AddWithValue((object?)entry.InReplyTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.Subject);
        cmd.Parameters.AddWithValue(entry.Sender);
        cmd.Parameters.AddWithValue(entry.Recipients.ToArray());
        cmd.Parameters.AddWithValue(entry.Cc.ToArray());
        cmd.Parameters.AddWithValue((object?)entry.ContentHtml ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.ContentText ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)entry.EmailAccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(entry.OccurredAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(entry.CreatedAt.ToUniversalTime());

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static CommunicationEntry ReadEntry(NpgsqlDataReader r)
    {
        var dirStr = r.GetString(4);
        var dir = Enum.TryParse<CommunicationDirection>(dirStr, ignoreCase: true, out var d) ? d : CommunicationDirection.In;

        var recipients = r.IsDBNull(9) ? [] : (r.GetValue(9) as string[])?.ToList() ?? [];
        var cc = r.IsDBNull(10) ? [] : (r.GetValue(10) as string[])?.ToList() ?? [];

        return new CommunicationEntry
        {
            Id = r.GetGuid(0),
            TenantId = r.GetString(1),
            ReferenceDocType = r.IsDBNull(2) ? null : r.GetString(2),
            ReferenceName = r.IsDBNull(3) ? null : r.GetString(3),
            Direction = dir,
            MessageId = r.GetString(5),
            InReplyTo = r.IsDBNull(6) ? null : r.GetString(6),
            Subject = r.GetString(7),
            Sender = r.GetString(8),
            Recipients = recipients,
            Cc = cc,
            ContentHtml = r.IsDBNull(11) ? null : r.GetString(11),
            ContentText = r.IsDBNull(12) ? null : r.GetString(12),
            EmailAccountId = r.IsDBNull(13) ? null : r.GetGuid(13),
            OccurredAt = r.GetFieldValue<DateTimeOffset>(14),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(15)
        };
    }
}
