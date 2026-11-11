using System.Text.Json;
using Framework.Documents;
using Framework.Expressions;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Security;
using Framework.Validation;

namespace Framework.Integrations;

public sealed class WebhookEventHandler(
    IWebhookStore webhookStore,
    IMetadataProvider metadata,
    FunctionRegistry functions,
    DocumentEvaluator evaluator) : IEventHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public bool CanHandle(string eventType) =>
        eventType.StartsWith("document.", StringComparison.OrdinalIgnoreCase) ||
        eventType.StartsWith("workflow.", StringComparison.OrdinalIgnoreCase) ||
        eventType.StartsWith("webhook.", StringComparison.OrdinalIgnoreCase);

    public async Task HandleAsync(OutboxMessage m, CancellationToken ct)
    {
        var p = m.Payload;
        var docType = p.GetValueOrDefault("docType");
        var name = p.GetValueOrDefault("name");
        if (string.IsNullOrEmpty(docType) || string.IsNullOrEmpty(name)) return;

        // Load active webhooks for this tenant and DocType
        var webhooks = await webhookStore.ListAsync(m.TenantId, docType, onlyActive: true, ct);
        if (webhooks.Count == 0) return;

        // Extract document data snapshot from outbox payload
        var rawData = p.GetValueOrDefault("data");
        Dictionary<string, object?> docData = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(rawData))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawData, JsonOpts);
                if (parsed is not null)
                {
                    foreach (var (k, v) in parsed)
                    {
                        if (v is JsonElement el)
                        {
                            docData[k] = el.ValueKind switch
                            {
                                JsonValueKind.String => el.GetString(),
                                JsonValueKind.Number when el.TryGetInt64(out var l) => l,
                                JsonValueKind.Number => el.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                JsonValueKind.Null => null,
                                _ => el.GetRawText()
                            };
                        }
                        else
                        {
                            docData[k] = v;
                        }
                    }
                }
            }
            catch
            {
                // If deserialization fails, continue with empty data
            }
        }

        metadata.Current.TryGet(docType, out var meta);
        var actor = p.GetValueOrDefault("actor") ?? "system";
        var actorUser = new UserContext(actor, p.GetValueOrDefault("actorName") ?? "Actor", m.TenantId, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System Manager" }, new Dictionary<string, string>());

        foreach (var w in webhooks)
        {
            // Check if this webhook subscribes to the event
            if (!w.Events.Contains(m.Type, StringComparer.OrdinalIgnoreCase) && !w.Events.Contains("*"))
                continue;

            // Evaluate condition expression if present
            if (!string.IsNullOrWhiteSpace(w.Condition))
            {
                try
                {
                    var parser = new ExpressionParser(functions);
                    var parsed = parser.Parse(w.Condition);
                    var passed = evaluator.Bool(parsed, fallback: false, docData, actorUser);
                    if (!passed) continue;
                }
                catch
                {
                    // Condition evaluation failed; skip
                    continue;
                }
            }

            // Filter data fields based on webhook configuration and PermLevel
            var filteredData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (w.Fields.Count > 0)
            {
                foreach (var field in w.Fields)
                {
                    if (docData.TryGetValue(field, out var val))
                    {
                        filteredData[field] = val;
                    }
                }
            }
            else
            {
                // Default: Include all fields with PermLevel 0
                if (meta is not null)
                {
                    foreach (var f in meta.Fields)
                    {
                        if (f.Def.PermLevel == 0 && docData.TryGetValue(f.Name, out var val))
                        {
                            filteredData[f.Name] = val;
                        }
                    }
                }
                else
                {
                    foreach (var (k, v) in docData)
                    {
                        filteredData[k] = v;
                    }
                }
            }

            var deliveryId = Guid.NewGuid();
            int.TryParse(p.GetValueOrDefault("version"), out var docVersion);

            var payloadObject = new
            {
                id = deliveryId.ToString(),
                @event = m.Type,
                occurredAt = p.GetValueOrDefault("occurredAt") ?? m.CreatedAt.ToString("O"),
                tenant = m.TenantId,
                docType = docType,
                name = name,
                version = docVersion,
                actor = actor,
                data = filteredData
            };

            var delivery = new WebhookDelivery
            {
                Id = deliveryId,
                TenantId = m.TenantId,
                WebhookId = w.Id,
                EventId = m.Id,
                EventType = m.Type,
                DocName = name,
                PayloadJson = JsonSerializer.Serialize(payloadObject, JsonOpts),
                Status = WebhookDeliveryStatus.Pending,
                Attempts = 0,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await webhookStore.EnqueueAsync(delivery, ct);
        }
    }
}
