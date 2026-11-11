using System.Net;
using System.Text.Json;
using Framework.Documents;
using Framework.Expressions;
using Framework.Integrations;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;
using Framework.Web.Integrations;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Regression & unit tests for Phase 3 Workstream A: Outbound Webhooks.</summary>
sealed partial class Tests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class MockHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Test]
    public async Task Webhook_enqueue_idempotency_ignores_duplicate_event()
    {
        var store = new InMemoryWebhookStore();
        var eventId = Guid.NewGuid();
        var webhookId = "whk_test1";

        var delivery1 = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = webhookId,
            EventId = eventId,
            EventType = EventTypes.DocumentInserted,
            DocName = "PR-001",
            PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var delivery2 = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = webhookId,
            EventId = eventId,
            EventType = EventTypes.DocumentInserted,
            DocName = "PR-001",
            PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Pending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await store.EnqueueAsync(delivery1);
        await store.EnqueueAsync(delivery2); // Duplicate event for same webhook

        var claimed = await store.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        Assert.Equal(1, claimed.Count, "Duplicate event for same webhook is enqueued only once");
        Assert.Equal(delivery1.Id, claimed[0].Id, "First delivery ID is preserved");
    }

    [Test]
    public async Task Webhook_condition_and_tenant_isolation()
    {
        var store = new InMemoryWebhookStore();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var functions = sp.GetRequiredService<FunctionRegistry>();
        var evaluator = sp.GetRequiredService<DocumentEvaluator>();
        var handler = new WebhookEventHandler(store, meta, functions, evaluator);

        // Webhook for acme tenant with condition total_amount > 1000
        var webhookAcme = new WebhookDef
        {
            Id = "whk_acme",
            TenantId = "acme",
            Name = "Acme High Value PR",
            DocType = PR,
            Events = [EventTypes.DocumentInserted],
            Condition = "total_amount > 1000",
            Url = "https://example.com/webhook",
            SecretEncrypted = "sec:dp:test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(webhookAcme);

        // Webhook for other tenant
        var webhookOther = new WebhookDef
        {
            Id = "whk_other",
            TenantId = "other_tenant",
            Name = "Other Tenant PR",
            DocType = PR,
            Events = [EventTypes.DocumentInserted],
            Url = "https://example.com/webhook",
            SecretEncrypted = "sec:dp:test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(webhookOther);

        // 1. Low amount event in acme (500) -> condition fails, no delivery
        var outboxLow = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Type = EventTypes.DocumentInserted,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Payload = new()
            {
                ["docType"] = PR,
                ["name"] = "PR-001",
                ["data"] = JsonSerializer.Serialize(new Dictionary<string, object?> { ["total_amount"] = 500 })
            }
        };
        await handler.HandleAsync(outboxLow, CancellationToken.None);
        var deliveriesAcmeLow = await store.ListDeliveriesAsync("acme", "whk_acme");
        Assert.Equal(0, deliveriesAcmeLow.Count, "Condition total_amount > 1000 was false; delivery was skipped");

        // 2. High amount event in acme (2500) -> condition passes, delivery enqueued
        var outboxHigh = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Type = EventTypes.DocumentInserted,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Payload = new()
            {
                ["docType"] = PR,
                ["name"] = "PR-002",
                ["data"] = JsonSerializer.Serialize(new Dictionary<string, object?> { ["total_amount"] = 2500 })
            }
        };
        await handler.HandleAsync(outboxHigh, CancellationToken.None);
        var deliveriesAcmeHigh = await store.ListDeliveriesAsync("acme", "whk_acme");
        Assert.Equal(1, deliveriesAcmeHigh.Count, "Condition passed; delivery was enqueued for acme");

        // 3. Verify tenant isolation: other_tenant webhook was NOT triggered by acme event
        var deliveriesOther = await store.ListDeliveriesAsync("other_tenant", "whk_other");
        Assert.Equal(0, deliveriesOther.Count, "Tenant isolation preserved; other tenant webhook not triggered");
    }

    [Test]
    public Task Webhook_hmac_sha256_signature_matches_test_vector()
    {
        var secret = "whsec_test_secret_key";
        var payload = "1731326400.{\"event\":\"document.inserted\",\"name\":\"PR-001\"}";

        var signature = WebhookDispatcher.ComputeHmacSha256(secret, payload);

        // Manually compute with standard HMACSHA256
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
        var expectedHex = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        Assert.Equal(expectedHex, signature, "HMAC-SHA256 signature matches expected digest");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Webhook_dispatcher_handles_500_retry_400_dead_and_410_deactivation()
    {
        var store = new InMemoryWebhookStore();
        ISecretProtector secretProtector = new InMemorySecretProtector();
        var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookDispatcher>>();

        var webhook = new WebhookDef
        {
            Id = "whk_dispatch_test",
            TenantId = "acme",
            Name = "Dispatch Test",
            DocType = PR,
            Events = [EventTypes.DocumentInserted],
            Url = "https://example.com/api",
            SecretEncrypted = secretProtector.Protect("secret123"),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(webhook);

        // Scenario 1: 500 Internal Server Error -> Failed with retry scheduled
        var client500 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Server busy")
        }));
        var dispatcher500 = new WebhookDispatcher(new MockHttpClientFactory(client500), store, secretProtector, logger);

        var delivery1 = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = webhook.Id,
            EventId = Guid.NewGuid(),
            EventType = EventTypes.DocumentInserted,
            DocName = "PR-101",
            PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Sending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.EnqueueAsync(delivery1);

        var res1 = await dispatcher500.DispatchAsync(delivery1);
        Assert.Equal(WebhookDeliveryStatus.Failed, res1.Status, "HTTP 500 transitions delivery to Failed");
        Assert.Equal(1, res1.Attempts, "Attempt incremented to 1");
        Assert.True(res1.NextAttemptAt > DateTimeOffset.UtcNow, "Next attempt timestamp scheduled in the future");

        // Scenario 2: 400 Bad Request -> Dead immediately without retry
        var client400 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Malformed schema")
        }));
        var dispatcher400 = new WebhookDispatcher(new MockHttpClientFactory(client400), store, secretProtector, logger);

        var delivery2 = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = webhook.Id,
            EventId = Guid.NewGuid(),
            EventType = EventTypes.DocumentInserted,
            DocName = "PR-102",
            PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Sending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.EnqueueAsync(delivery2);

        var res2 = await dispatcher400.DispatchAsync(delivery2);
        Assert.Equal(WebhookDeliveryStatus.Dead, res2.Status, "HTTP 400 transitions delivery to Dead without retry");

        // Scenario 3: 410 Gone -> Dead and webhook deactivated
        var client410 = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Gone)
        {
            Content = new StringContent("Endpoint permanently retired")
        }));
        var dispatcher410 = new WebhookDispatcher(new MockHttpClientFactory(client410), store, secretProtector, logger);

        var delivery3 = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            WebhookId = webhook.Id,
            EventId = Guid.NewGuid(),
            EventType = EventTypes.DocumentInserted,
            DocName = "PR-103",
            PayloadJson = "{}",
            Status = WebhookDeliveryStatus.Sending,
            Attempts = 0,
            NextAttemptAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.EnqueueAsync(delivery3);

        var res3 = await dispatcher410.DispatchAsync(delivery3);
        Assert.Equal(WebhookDeliveryStatus.Dead, res3.Status, "HTTP 410 transitions delivery to Dead");

        var updatedWebhook = await store.GetAsync("acme", webhook.Id);
        Assert.True(!updatedWebhook!.IsActive, "HTTP 410 automatically deactivates target webhook");
    }

    [Test]
    public Task Webhook_ssrf_validator_rejects_private_and_loopback_ips()
    {
        // 1. Loopback addresses
        var loopbackEx = false;
        try { WebhookUrlValidator.ValidateUrl("https://127.0.0.1/webhook"); } catch (InvalidOperationException) { loopbackEx = true; }
        Assert.True(loopbackEx, "127.0.0.1 rejected by SSRF validator");

        var localhostEx = false;
        try { WebhookUrlValidator.ValidateUrl("https://localhost:8080/webhook"); } catch (InvalidOperationException) { localhostEx = true; }
        Assert.True(localhostEx, "localhost rejected by SSRF validator");

        // 2. RFC 1918 Private networks
        var priv10Ex = false;
        try { WebhookUrlValidator.ValidateUrl("https://10.0.0.5/api"); } catch (InvalidOperationException) { priv10Ex = true; }
        Assert.True(priv10Ex, "10.0.0.5 rejected");

        var priv172Ex = false;
        try { WebhookUrlValidator.ValidateUrl("https://172.18.0.2:5000/webhook"); } catch (InvalidOperationException) { priv172Ex = true; }
        Assert.True(priv172Ex, "172.18.0.2 rejected");

        var priv192Ex = false;
        try { WebhookUrlValidator.ValidateUrl("https://192.168.1.1/webhook"); } catch (InvalidOperationException) { priv192Ex = true; }
        Assert.True(priv192Ex, "192.168.1.1 rejected");

        // 3. Link-local cloud metadata (AWS/Azure/GCP: 169.254.169.254)
        var metaEx = false;
        try { WebhookUrlValidator.ValidateUrl("https://169.254.169.254/latest/meta-data"); } catch (InvalidOperationException) { metaEx = true; }
        Assert.True(metaEx, "169.254.169.254 cloud metadata rejected");

        // 4. Insecure HTTP rejected outside dev
        var httpEx = false;
        try { WebhookUrlValidator.ValidateUrl("http://example.com/api", allowHttp: false); } catch (InvalidOperationException) { httpEx = true; }
        Assert.True(httpEx, "Insecure HTTP rejected when allowHttp is false");

        // 5. Explicitly allowed private host
        var allowedPass = true;
        try { WebhookUrlValidator.ValidateUrl("https://internal.local/webhook", allowHttp: false, allowedPrivateHosts: ["internal.local"]); }
        catch { allowedPass = false; }
        Assert.True(allowedPass, "Explicitly allowed private host is accepted");

        return Task.CompletedTask;
    }

    [Test]
    public async Task Webhook_field_permission_filtering_omits_permlevel_above_zero()
    {
        var store = new InMemoryWebhookStore();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var functions = sp.GetRequiredService<FunctionRegistry>();
        var evaluator = sp.GetRequiredService<DocumentEvaluator>();
        var handler = new WebhookEventHandler(store, meta, functions, evaluator);

        // Webhook without specific fields selected -> should only send PermLevel 0 fields
        var webhook = new WebhookDef
        {
            Id = "whk_permlevel_test",
            TenantId = "acme",
            Name = "PermLevel Test",
            DocType = PR,
            Events = [EventTypes.DocumentInserted],
            Url = "https://example.com/webhook",
            SecretEncrypted = "sec:dp:test",
            Fields = [], // empty = default PermLevel 0
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(webhook);

        var dataWithPermLevel = new Dictionary<string, object?>
        {
            ["request_date"] = "2026-11-11",
            ["total_amount"] = 5000000,
            ["vendor"] = "VND-0001",
            ["secret_internal_notes"] = "Confidential approval remarks" // Not in PermLevel 0
        };

        var outbox = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Type = EventTypes.DocumentInserted,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Payload = new()
            {
                ["docType"] = PR,
                ["name"] = "PR-PERM-01",
                ["data"] = JsonSerializer.Serialize(dataWithPermLevel)
            }
        };

        await handler.HandleAsync(outbox, CancellationToken.None);
        var deliveries = await store.ListDeliveriesAsync("acme", webhook.Id);
        Assert.Equal(1, deliveries.Count, "Delivery enqueued");

        using var jsonDoc = JsonDocument.Parse(deliveries[0].PayloadJson);
        var dataProp = jsonDoc.RootElement.GetProperty("data");
        Assert.True(dataProp.TryGetProperty("request_date", out _), "PermLevel 0 field request_date is included");
        Assert.True(dataProp.TryGetProperty("vendor", out _), "PermLevel 0 field vendor is included");
        Assert.True(!dataProp.TryGetProperty("secret_internal_notes", out _), "Non-PermLevel 0 field is excluded by default");
    }

    [Test]
    public async Task Webhook_failure_does_not_redeliver_outbox_notifications_or_emails()
    {
        var outboxStore = new InMemoryDocumentStore();
        var emailStore = new List<string>();
        var webhookStore = new InMemoryWebhookStore();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var functions = sp.GetRequiredService<FunctionRegistry>();
        var evaluator = sp.GetRequiredService<DocumentEvaluator>();
        var webhookHandler = new WebhookEventHandler(webhookStore, meta, functions, evaluator);

        // Configure a webhook that will fail on dispatch
        var webhook = new WebhookDef
        {
            Id = "whk_isolated_test",
            TenantId = "acme",
            Name = "Isolated Failure Test",
            DocType = PR,
            Events = [EventTypes.DocumentInserted],
            Url = "https://example.com/fail",
            SecretEncrypted = "sec:dp:test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await webhookStore.SaveAsync(webhook);

        // Simulated email handler that tracks executions
        var emailSendCount = 0;
        var emailHandler = new MockEmailHandler(() => emailSendCount++);

        var processor = new OutboxProcessor(outboxStore, [webhookHandler, emailHandler], TimeProvider.System);

        // Queue an outbox event
        var msg = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Type = EventTypes.DocumentInserted,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Payload = new()
            {
                ["docType"] = PR,
                ["name"] = "PR-ISO-01",
                ["data"] = "{}"
            }
        };
        await outboxStore.EnqueueAsync(msg);

        // First outbox processing run
        var res1 = await processor.ProcessDueAsync();
        Assert.Equal(1, emailSendCount, "Email handler executed once during outbox processing");

        var webhookDeliveries = await webhookStore.ListDeliveriesAsync("acme", webhook.Id);
        Assert.Equal(1, webhookDeliveries.Count, "Webhook delivery enqueued into dedicated queue");

        // Now dispatch the webhook via dispatcher, where HTTP fails
        ISecretProtector secretProtector = new InMemorySecretProtector();
        var failingClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var dispatcher = new WebhookDispatcher(new MockHttpClientFactory(failingClient), webhookStore, secretProtector, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookDispatcher>>());

        await dispatcher.DispatchAsync(webhookDeliveries[0]);
        var failedDelivery = await webhookStore.GetDeliveryAsync("acme", webhookDeliveries[0].Id);
        Assert.Equal(WebhookDeliveryStatus.Failed, failedDelivery!.Status, "Webhook failed in dedicated queue");

        // Process outbox again: outbox event was marked processed, email was NOT resent!
        var res2 = await processor.ProcessDueAsync();
        Assert.Equal(1, emailSendCount, "Email handler was NOT redelivered; outbox queue remained clean");
    }

    private sealed class MockEmailHandler(Action onHandled) : IEventHandler
    {
        public bool CanHandle(string eventType) => eventType == EventTypes.DocumentInserted;
        public Task HandleAsync(OutboxMessage message, CancellationToken ct)
        {
            onHandled();
            return Task.CompletedTask;
        }
    }
}

