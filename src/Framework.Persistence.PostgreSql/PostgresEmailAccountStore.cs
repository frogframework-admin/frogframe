using System.Text.Json;
using Framework.Communication;
using Npgsql;
using NpgsqlTypes;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresEmailAccountStore(NpgsqlDataSource db) : IEmailAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EmailAccount>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, email_address, imap_host, imap_port, use_ssl,
                   username, password_enc, folder, append_to_doctype, field_map,
                   service_user_id, uid_validity, last_uid, is_active, last_polled_at,
                   last_error, created_at, modified_at
            FROM frogframe_email_accounts
            WHERE is_active = TRUE
            ORDER BY name ASC");

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<EmailAccount>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAccount(r));
        }
        return list;
    }

    public async Task<IReadOnlyList<EmailAccount>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, email_address, imap_host, imap_port, use_ssl,
                   username, password_enc, folder, append_to_doctype, field_map,
                   service_user_id, uid_validity, last_uid, is_active, last_polled_at,
                   last_error, created_at, modified_at
            FROM frogframe_email_accounts
            WHERE tenant_id = $1
            ORDER BY name ASC");
        cmd.Parameters.AddWithValue(tenantId);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<EmailAccount>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadAccount(r));
        }
        return list;
    }

    public async Task<EmailAccount?> GetByIdAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, name, email_address, imap_host, imap_port, use_ssl,
                   username, password_enc, folder, append_to_doctype, field_map,
                   service_user_id, uid_validity, last_uid, is_active, last_polled_at,
                   last_error, created_at, modified_at
            FROM frogframe_email_accounts
            WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadAccount(r);
        }
        return null;
    }

    public async Task SaveAsync(EmailAccount account, CancellationToken ct = default)
    {
        var sql = @"
            INSERT INTO frogframe_email_accounts (
                id, tenant_id, name, email_address, imap_host, imap_port, use_ssl,
                username, password_enc, folder, append_to_doctype, field_map,
                service_user_id, uid_validity, last_uid, is_active, last_polled_at,
                last_error, created_at, modified_at
            ) VALUES (
                $1, $2, $3, $4, $5, $6, $7,
                $8, $9, $10, $11, $12::jsonb,
                $13, $14, $15, $16, $17,
                $18, $19, $20
            )
            ON CONFLICT (id) DO UPDATE SET
                tenant_id = EXCLUDED.tenant_id,
                name = EXCLUDED.name,
                email_address = EXCLUDED.email_address,
                imap_host = EXCLUDED.imap_host,
                imap_port = EXCLUDED.imap_port,
                use_ssl = EXCLUDED.use_ssl,
                username = EXCLUDED.username,
                password_enc = EXCLUDED.password_enc,
                folder = EXCLUDED.folder,
                append_to_doctype = EXCLUDED.append_to_doctype,
                field_map = EXCLUDED.field_map,
                service_user_id = EXCLUDED.service_user_id,
                uid_validity = EXCLUDED.uid_validity,
                last_uid = EXCLUDED.last_uid,
                is_active = EXCLUDED.is_active,
                last_polled_at = EXCLUDED.last_polled_at,
                last_error = EXCLUDED.last_error,
                modified_at = EXCLUDED.modified_at";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(account.Id);
        cmd.Parameters.AddWithValue(account.TenantId);
        cmd.Parameters.AddWithValue(account.Name);
        cmd.Parameters.AddWithValue(account.EmailAddress);
        cmd.Parameters.AddWithValue(account.ImapHost);
        cmd.Parameters.AddWithValue(account.ImapPort);
        cmd.Parameters.AddWithValue(account.UseSsl);
        cmd.Parameters.AddWithValue(account.Username);
        cmd.Parameters.AddWithValue(account.PasswordEnc);
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(account.Folder) ? "INBOX" : account.Folder);
        cmd.Parameters.AddWithValue((object?)account.AppendToDocType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(account.FieldMap, JsonOptions));
        cmd.Parameters.AddWithValue(account.ServiceUserId);
        cmd.Parameters.AddWithValue(account.UidValidity);
        cmd.Parameters.AddWithValue(account.LastUid);
        cmd.Parameters.AddWithValue(account.IsActive);
        cmd.Parameters.AddWithValue((object?)account.LastPolledAt?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)account.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue(account.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue(account.ModifiedAt.ToUniversalTime());

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_email_accounts WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdatePollingStateAsync(
        Guid id,
        long uidValidity,
        long lastUid,
        DateTimeOffset polledAt,
        string? lastError,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_email_accounts
            SET uid_validity = $2,
                last_uid = $3,
                last_polled_at = $4,
                last_error = $5,
                modified_at = $4
            WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(uidValidity);
        cmd.Parameters.AddWithValue(lastUid);
        cmd.Parameters.AddWithValue(polledAt.ToUniversalTime());
        cmd.Parameters.AddWithValue((object?)lastError ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static EmailAccount ReadAccount(NpgsqlDataReader r)
    {
        var rawFieldMap = r.IsDBNull(11) ? null : r.GetString(11);
        var fieldMap = !string.IsNullOrWhiteSpace(rawFieldMap)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(rawFieldMap, JsonOptions) ?? []
            : [];

        return new EmailAccount
        {
            Id = r.GetGuid(0),
            TenantId = r.GetString(1),
            Name = r.GetString(2),
            EmailAddress = r.GetString(3),
            ImapHost = r.GetString(4),
            ImapPort = r.GetInt32(5),
            UseSsl = r.GetBoolean(6),
            Username = r.GetString(7),
            PasswordEnc = r.GetString(8),
            Folder = r.GetString(9),
            AppendToDocType = r.IsDBNull(10) ? null : r.GetString(10),
            FieldMap = fieldMap,
            ServiceUserId = r.GetString(12),
            UidValidity = r.GetInt64(13),
            LastUid = r.GetInt64(14),
            IsActive = r.GetBoolean(15),
            LastPolledAt = r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16),
            LastError = r.IsDBNull(17) ? null : r.GetString(17),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(18),
            ModifiedAt = r.GetFieldValue<DateTimeOffset>(19)
        };
    }
}
