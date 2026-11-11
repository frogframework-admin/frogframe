using System.Security.Cryptography;
using Framework.Expressions;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Integrations;

public sealed record SaveWebhookRequest(
    string? Id,
    string Name,
    string DocType,
    IReadOnlyList<string> Events,
    string? Condition,
    string Url,
    IReadOnlyDictionary<string, string>? Headers,
    string? Secret, // Plaintext secret provided when creating or regenerating
    IReadOnlyList<string>? Fields,
    bool IsActive = true
);

public sealed record SaveWebhookResult(WebhookDef Webhook, string? PlaintextSecret);

public sealed class WebhookService(
    IWebhookStore store,
    IMetadataProvider metadata,
    FunctionRegistry functions,
    ISecretProtector secretProtector,
    bool isDevelopment = false,
    IEnumerable<string>? allowedPrivateHosts = null)
{
    private static void DemandAdmin(UserContext user)
    {
        if (!user.Roles.Contains(UserContext.SystemManager))
            throw new PermissionDeniedException("Managing webhooks requires System Manager permissions.");
    }

    public async Task<IReadOnlyList<WebhookDef>> ListAsync(UserContext user, string? docType = null, CancellationToken ct = default)
    {
        DemandAdmin(user);
        return await store.ListAsync(user.TenantId, docType, onlyActive: false, ct);
    }

    public async Task<WebhookDef?> GetAsync(UserContext user, string id, CancellationToken ct = default)
    {
        DemandAdmin(user);
        return await store.GetAsync(user.TenantId, id, ct);
    }

    public static string GenerateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<SaveWebhookResult> SaveAsync(UserContext user, SaveWebhookRequest req, CancellationToken ct = default)
    {
        DemandAdmin(user);

        if (string.IsNullOrWhiteSpace(req.Name))
            throw new InvalidOperationException("Webhook name is required.");

        if (string.IsNullOrWhiteSpace(req.DocType))
            throw new InvalidOperationException("Target DocType is required.");

        if (!metadata.Current.TryGet(req.DocType, out var meta))
            throw new InvalidOperationException($"DocType '{req.DocType}' does not exist.");

        if (req.Events is null || req.Events.Count == 0)
            throw new InvalidOperationException("At least one event must be selected.");

        // Validate condition expression if present
        if (!string.IsNullOrWhiteSpace(req.Condition))
        {
            try
            {
                var parser = new ExpressionParser(functions);
                parser.Parse(req.Condition);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid condition expression: {ex.Message}", ex);
            }
        }

        // Validate fields if specified
        var fields = (req.Fields ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        foreach (var f in fields)
        {
            if (!meta.Fields.Any(field => string.Equals(field.Name, f, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Field '{f}' does not exist on DocType '{req.DocType}'.");
        }

        // Validate destination URL (SSRF defense)
        WebhookUrlValidator.ValidateUrl(req.Url, allowHttp: isDevelopment, allowedPrivateHosts: allowedPrivateHosts);

        string? returnedPlaintextSecret = null;
        string secretEncrypted;

        var existing = !string.IsNullOrWhiteSpace(req.Id) ? await store.GetAsync(user.TenantId, req.Id, ct) : null;

        if (!string.IsNullOrWhiteSpace(req.Secret))
        {
            returnedPlaintextSecret = req.Secret.Trim();
            secretEncrypted = secretProtector.Protect(returnedPlaintextSecret);
        }
        else if (existing is not null)
        {
            secretEncrypted = existing.SecretEncrypted;
        }
        else
        {
            returnedPlaintextSecret = GenerateSecret();
            secretEncrypted = secretProtector.Protect(returnedPlaintextSecret);
        }

        var id = existing?.Id ?? ("whk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        var now = DateTimeOffset.UtcNow;

        var webhook = new WebhookDef
        {
            Id = id,
            TenantId = user.TenantId,
            Name = req.Name.Trim(),
            DocType = meta.Name,
            Events = req.Events.Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList(),
            Condition = string.IsNullOrWhiteSpace(req.Condition) ? null : req.Condition.Trim(),
            Url = req.Url.Trim(),
            Headers = req.Headers ?? new Dictionary<string, string>(),
            SecretEncrypted = secretEncrypted,
            Fields = fields,
            IsActive = req.IsActive,
            CreatedAt = existing?.CreatedAt ?? now,
            ModifiedAt = now
        };

        await store.SaveAsync(webhook, ct);
        return new SaveWebhookResult(webhook, returnedPlaintextSecret);
    }

    public async Task DeleteAsync(UserContext user, string id, CancellationToken ct = default)
    {
        DemandAdmin(user);
        await store.DeleteAsync(user.TenantId, id, ct);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(UserContext user, string webhookId, int limit = 50, CancellationToken ct = default)
    {
        DemandAdmin(user);
        return await store.ListDeliveriesAsync(user.TenantId, webhookId, limit, ct);
    }

    public async Task<WebhookDelivery> ResendDeliveryAsync(UserContext user, Guid deliveryId, CancellationToken ct = default)
    {
        DemandAdmin(user);
        var delivery = await store.GetDeliveryAsync(user.TenantId, deliveryId, ct)
                       ?? throw new InvalidOperationException($"Webhook delivery '{deliveryId}' not found.");

        var resend = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = delivery.TenantId,
            WebhookId = delivery.WebhookId,
            EventId = Guid.NewGuid(), // New EventId to allow re-dispatch
            EventType = delivery.EventType,
            DocName = delivery.DocName,
            PayloadJson = delivery.PayloadJson,
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.EnqueueAsync(resend, ct);
        return resend;
    }

    public async Task<WebhookDelivery> SendTestPingAsync(UserContext user, string webhookId, CancellationToken ct = default)
    {
        DemandAdmin(user);
        var webhook = await store.GetAsync(user.TenantId, webhookId, ct)
                      ?? throw new InvalidOperationException($"Webhook '{webhookId}' not found.");

        var pingPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString(),
            @event = "webhook.ping",
            occurredAt = DateTimeOffset.UtcNow.ToString("O"),
            tenant = user.TenantId,
            docType = webhook.DocType,
            name = "TEST-PING",
            version = 1,
            actor = user.Id,
            data = new Dictionary<string, object?> { ["message"] = "Test webhook delivery from FrogFrame" }
        });

        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            WebhookId = webhook.Id,
            EventId = Guid.NewGuid(),
            EventType = "webhook.ping",
            DocName = "TEST-PING",
            PayloadJson = pingPayload,
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.EnqueueAsync(delivery, ct);
        return delivery;
    }
}

