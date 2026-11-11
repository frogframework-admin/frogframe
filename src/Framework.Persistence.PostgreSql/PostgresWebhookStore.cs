using System.Text.Json;
using Framework.Integrations;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public sealed class PostgresWebhookStore(NpgsqlDataSource db) : IWebhookStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WebhookDef>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default)
    {
        var sql = "SELECT id, tenant_id, name, doctype, events, condition, url, headers, secret_enc, fields, is_active, created_at, modified_at FROM frogframe_webhooks WHERE tenant_id = $1";
        if (onlyActive) sql += " AND is_active = true";
        if (!string.IsNullOrEmpty(docType)) sql += " AND doctype = $2";
        sql += " ORDER BY modified_at DESC";

        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(tenantId);
        if (!string.IsNullOrEmpty(docType)) cmd.Parameters.AddWithValue(docType);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<WebhookDef>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadWebhook(r));
        }
        return list;
    }

    public async Task<WebhookDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, tenant_id, name, doctype, events, condition, url, headers, secret_enc, fields, is_active, created_at, modified_at FROM frogframe_webhooks WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadWebhook(r);
        }
        return null;
    }

    public async Task SaveAsync(WebhookDef webhook, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_webhooks
                (id, tenant_id, name, doctype, events, condition, url, headers, secret_enc, fields, is_active, created_at, modified_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9, $10, $11, $12, $13)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                doctype = EXCLUDED.doctype,
                events = EXCLUDED.events,
                condition = EXCLUDED.condition,
                url = EXCLUDED.url,
                headers = EXCLUDED.headers,
                secret_enc = EXCLUDED.secret_enc,
                fields = EXCLUDED.fields,
                is_active = EXCLUDED.is_active,
                modified_at = EXCLUDED.modified_at");

        cmd.Parameters.AddWithValue(webhook.Id);
        cmd.Parameters.AddWithValue(webhook.TenantId);
        cmd.Parameters.AddWithValue(webhook.Name);
        cmd.Parameters.AddWithValue(webhook.DocType);
        cmd.Parameters.AddWithValue((string[])webhook.Events.ToArray());
        cmd.Parameters.AddWithValue((object?)webhook.Condition ?? DBNull.Value);
        cmd.Parameters.AddWithValue(webhook.Url);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(webhook.Headers, JsonOpts));
        cmd.Parameters.AddWithValue(webhook.SecretEncrypted);
        cmd.Parameters.AddWithValue((string[])webhook.Fields.ToArray());
        cmd.Parameters.AddWithValue(webhook.IsActive);
        cmd.Parameters.AddWithValue(webhook.CreatedAt);
        cmd.Parameters.AddWithValue(webhook.ModifiedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM frogframe_webhooks WHERE tenant_id = $1 AND id = $2");
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            INSERT INTO frogframe_webhook_deliveries
                (id, tenant_id, webhook_id, event_id, event_type, doc_name, payload, status, attempts, next_attempt_at, created_at)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10, $11)
            ON CONFLICT (event_id, webhook_id) DO NOTHING");

        cmd.Parameters.AddWithValue(delivery.Id);
        cmd.Parameters.AddWithValue(delivery.TenantId);
        cmd.Parameters.AddWithValue(delivery.WebhookId);
        cmd.Parameters.AddWithValue(delivery.EventId);
        cmd.Parameters.AddWithValue(delivery.EventType);
        cmd.Parameters.AddWithValue(delivery.DocName);
        cmd.Parameters.AddWithValue(delivery.PayloadJson);
        cmd.Parameters.AddWithValue(delivery.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue(delivery.Attempts);
        cmd.Parameters.AddWithValue(delivery.NextAttemptAt);
        cmd.Parameters.AddWithValue(delivery.CreatedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(int limit, TimeSpan leaseFor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseFor);

        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_webhook_deliveries
            SET status = 'sending', lease_until = $2
            WHERE id IN (
                SELECT id FROM frogframe_webhook_deliveries
                WHERE (
                        ((status = 'pending' OR status = 'failed') AND next_attempt_at <= $1)
                        -- A worker that crashed or was stopped mid-send leaves the row in 'sending'.
                        -- Reclaim it once its lease has expired, otherwise the delivery is lost forever.
                        OR status = 'sending'
                      )
                  AND (lease_until IS NULL OR lease_until <= $1)
                ORDER BY next_attempt_at ASC
                LIMIT $3
                FOR UPDATE SKIP LOCKED
            )
            RETURNING id, tenant_id, webhook_id, event_id, event_type, doc_name, payload::text, status, attempts, next_attempt_at, lease_until, last_status_code, last_error, response_snippet, duration_ms, created_at, sent_at");

        cmd.Parameters.AddWithValue(now);
        cmd.Parameters.AddWithValue(leaseUntil);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<WebhookDelivery>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadDelivery(r));
        }
        return list;
    }

    public async Task UpdateDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            UPDATE frogframe_webhook_deliveries
            SET status = $2,
                attempts = $3,
                next_attempt_at = $4,
                lease_until = $5,
                last_status_code = $6,
                last_error = $7,
                response_snippet = $8,
                duration_ms = $9,
                sent_at = $10
            WHERE id = $1");

        cmd.Parameters.AddWithValue(delivery.Id);
        cmd.Parameters.AddWithValue(delivery.Status.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue(delivery.Attempts);
        cmd.Parameters.AddWithValue(delivery.NextAttemptAt);
        cmd.Parameters.AddWithValue((object?)delivery.LeaseUntil ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)delivery.LastStatusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)delivery.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)delivery.ResponseSnippet ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)delivery.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)delivery.SentAt ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(string tenantId, string webhookId, int limit = 50, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, webhook_id, event_id, event_type, doc_name, payload::text, status, attempts, next_attempt_at, lease_until, last_status_code, last_error, response_snippet, duration_ms, created_at, sent_at
            FROM frogframe_webhook_deliveries
            WHERE tenant_id = $1 AND webhook_id = $2
            ORDER BY created_at DESC
            LIMIT $3");

        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(webhookId);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<WebhookDelivery>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadDelivery(r));
        }
        return list;
    }

    public async Task<WebhookDelivery?> GetDeliveryAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(@"
            SELECT id, tenant_id, webhook_id, event_id, event_type, doc_name, payload::text, status, attempts, next_attempt_at, lease_until, last_status_code, last_error, response_snippet, duration_ms, created_at, sent_at
            FROM frogframe_webhook_deliveries
            WHERE tenant_id = $1 AND id = $2");

        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(id);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadDelivery(r);
        }
        return null;
    }

    private static WebhookDef ReadWebhook(NpgsqlDataReader r)
    {
        var headersRaw = r.GetString(7);
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersRaw, JsonOpts) ?? new();

        return new WebhookDef
        {
            Id = r.GetString(0),
            TenantId = r.GetString(1),
            Name = r.GetString(2),
            DocType = r.GetString(3),
            Events = r.GetFieldValue<string[]>(4),
            Condition = r.IsDBNull(5) ? null : r.GetString(5),
            Url = r.GetString(6),
            Headers = headers,
            SecretEncrypted = r.GetString(8),
            Fields = r.GetFieldValue<string[]>(9),
            IsActive = r.GetBoolean(10),
            CreatedAt = r.GetDateTime(11),
            ModifiedAt = r.GetDateTime(12)
        };
    }

    private static WebhookDelivery ReadDelivery(NpgsqlDataReader r)
    {
        var statusStr = r.GetString(7);
        var status = statusStr.ToLowerInvariant() switch
        {
            "sending" => WebhookDeliveryStatus.Sending,
            "sent" => WebhookDeliveryStatus.Sent,
            "failed" => WebhookDeliveryStatus.Failed,
            "dead" => WebhookDeliveryStatus.Dead,
            _ => WebhookDeliveryStatus.Pending
        };

        return new WebhookDelivery
        {
            Id = r.GetGuid(0),
            TenantId = r.GetString(1),
            WebhookId = r.GetString(2),
            EventId = r.GetGuid(3),
            EventType = r.GetString(4),
            DocName = r.GetString(5),
            PayloadJson = r.GetString(6),
            Status = status,
            Attempts = r.GetInt32(8),
            NextAttemptAt = r.GetDateTime(9),
            LeaseUntil = r.IsDBNull(10) ? null : r.GetDateTime(10),
            LastStatusCode = r.IsDBNull(11) ? null : r.GetInt32(11),
            LastError = r.IsDBNull(12) ? null : r.GetString(12),
            ResponseSnippet = r.IsDBNull(13) ? null : r.GetString(13),
            DurationMs = r.IsDBNull(14) ? null : r.GetInt32(14),
            CreatedAt = r.GetDateTime(15),
            SentAt = r.IsDBNull(16) ? null : r.GetDateTime(16)
        };
    }
}

