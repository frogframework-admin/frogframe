using System.Collections.Concurrent;
using System.Text.Json;

namespace Framework.Integrations;

/// <summary>
/// Configuration for an outbound webhook triggered by document events.
/// </summary>
public sealed record WebhookDef
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required string DocType { get; init; }
    public required IReadOnlyList<string> Events { get; init; }
    public string? Condition { get; init; }
    public required string Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public required string SecretEncrypted { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>
/// Execution status of a webhook delivery task.
/// </summary>
public enum WebhookDeliveryStatus
{
    Pending,
    Sending,
    Sent,
    Failed,
    Dead
}

/// <summary>
/// A queued or dispatched webhook delivery task.
/// </summary>
public sealed record WebhookDelivery
{
    public required Guid Id { get; init; }
    public required string TenantId { get; init; }
    public required string WebhookId { get; init; }
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required string DocName { get; init; }
    public required string PayloadJson { get; init; }
    public WebhookDeliveryStatus Status { get; init; } = WebhookDeliveryStatus.Pending;
    public int Attempts { get; init; } = 0;
    public DateTimeOffset NextAttemptAt { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public string? ResponseSnippet { get; init; }
    public int? DurationMs { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
}

/// <summary>
/// Storage interface for webhook configurations and delivery queues.
/// </summary>
public interface IWebhookStore
{
    Task<IReadOnlyList<WebhookDef>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default);
    Task<WebhookDef?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task SaveAsync(WebhookDef webhook, CancellationToken ct = default);
    Task DeleteAsync(string tenantId, string id, CancellationToken ct = default);

    Task EnqueueAsync(WebhookDelivery delivery, CancellationToken ct = default);
    Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(int limit, TimeSpan leaseFor, CancellationToken ct = default);
    Task UpdateDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default);
    Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(string tenantId, string webhookId, int limit = 50, CancellationToken ct = default);
    Task<WebhookDelivery?> GetDeliveryAsync(string tenantId, Guid id, CancellationToken ct = default);
}

/// <summary>
/// Thread-safe in-memory store for webhook definitions and deliveries (used for testing and standalone dev).
/// </summary>
public sealed class InMemoryWebhookStore : IWebhookStore
{
    private readonly ConcurrentDictionary<string, WebhookDef> _webhooks = new();
    private readonly ConcurrentDictionary<Guid, WebhookDelivery> _deliveries = new();
    private readonly ConcurrentDictionary<string, Guid> _idempotencyKeys = new();
    private readonly object _gate = new();

    private static string WebhookKey(string tenantId, string id) => $"{tenantId}:{id}";
    private static string IdempotencyKey(Guid eventId, string webhookId) => $"{eventId}:{webhookId}";

    public Task<IReadOnlyList<WebhookDef>> ListAsync(string tenantId, string? docType = null, bool onlyActive = false, CancellationToken ct = default)
    {
        var list = _webhooks.Values
            .Where(w => w.TenantId == tenantId)
            .Where(w => !onlyActive || w.IsActive)
            .Where(w => string.IsNullOrEmpty(docType) || string.Equals(w.DocType, docType, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.ModifiedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<WebhookDef>>(list);
    }

    public Task<WebhookDef?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _webhooks.TryGetValue(WebhookKey(tenantId, id), out var w);
        return Task.FromResult(w);
    }

    public Task SaveAsync(WebhookDef webhook, CancellationToken ct = default)
    {
        _webhooks[WebhookKey(webhook.TenantId, webhook.Id)] = webhook;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string tenantId, string id, CancellationToken ct = default)
    {
        _webhooks.TryRemove(WebhookKey(tenantId, id), out _);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        var key = IdempotencyKey(delivery.EventId, delivery.WebhookId);
        lock (_gate)
        {
            if (_idempotencyKeys.TryAdd(key, delivery.Id))
            {
                _deliveries[delivery.Id] = delivery;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookDelivery>> ClaimDueAsync(int limit, TimeSpan leaseFor, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = new List<WebhookDelivery>();

        lock (_gate)
        {
            var due = _deliveries.Values
                .Where(d => (d.Status == WebhookDeliveryStatus.Pending || d.Status == WebhookDeliveryStatus.Failed)
                            && d.NextAttemptAt <= now
                            && (d.LeaseUntil is null || d.LeaseUntil <= now))
                .OrderBy(d => d.NextAttemptAt)
                .Take(limit)
                .ToList();

            foreach (var item in due)
            {
                var leased = item with
                {
                    Status = WebhookDeliveryStatus.Sending,
                    LeaseUntil = now.Add(leaseFor)
                };
                _deliveries[item.Id] = leased;
                claimed.Add(leased);
            }
        }

        return Task.FromResult<IReadOnlyList<WebhookDelivery>>(claimed);
    }

    public Task UpdateDeliveryAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        _deliveries[delivery.Id] = delivery;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(string tenantId, string webhookId, int limit = 50, CancellationToken ct = default)
    {
        var list = _deliveries.Values
            .Where(d => d.TenantId == tenantId && d.WebhookId == webhookId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<WebhookDelivery>>(list);
    }

    public Task<WebhookDelivery?> GetDeliveryAsync(string tenantId, Guid id, CancellationToken ct = default)
    {
        _deliveries.TryGetValue(id, out var d);
        if (d is not null && d.TenantId != tenantId) d = null;
        return Task.FromResult(d);
    }
}

