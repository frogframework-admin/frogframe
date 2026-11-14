using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Framework.Integrations;
using Framework.Jobs;
using Framework.Observability;
using Framework.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Integrations;

public sealed class WebhookDispatcher(
    IHttpClientFactory httpClientFactory,
    IWebhookStore store,
    ISecretProtector secretProtector,
    ILogger<WebhookDispatcher> logger)
{
    public const string HttpClientName = "frogframe-webhooks";

    public async Task<WebhookDelivery> DispatchAsync(WebhookDelivery delivery, CancellationToken ct = default)
    {
        var webhook = await store.GetAsync(delivery.TenantId, delivery.WebhookId, ct);
        var now = DateTimeOffset.UtcNow;

        if (webhook is null || !webhook.IsActive)
        {
            var deadDelivery = delivery with
            {
                Status = WebhookDeliveryStatus.Dead,
                LastError = webhook is null ? "Webhook definition not found." : "Webhook is inactive.",
                LeaseUntil = null
            };
            await store.UpdateDeliveryAsync(deadDelivery, ct);
            return deadDelivery;
        }

        var secret = secretProtector.Unprotect(webhook.SecretEncrypted);
        var timestamp = now.ToUnixTimeSeconds();
        var signaturePayload = $"{timestamp}.{delivery.PayloadJson}";
        var signature = ComputeHmacSha256(secret, signaturePayload);

        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url)
        {
            Content = new StringContent(delivery.PayloadJson, Encoding.UTF8, "application/json")
        };

        request.Headers.Add("User-Agent", "FrogFrame-Webhook/1");
        request.Headers.Add("X-FrogFrame-Event", delivery.EventType);
        request.Headers.Add("X-FrogFrame-Delivery", delivery.Id.ToString());
        request.Headers.Add("X-FrogFrame-Timestamp", timestamp.ToString());
        request.Headers.Add("X-FrogFrame-Signature", $"sha256={signature}");

        foreach (var (headerKey, headerVal) in webhook.Headers)
        {
            if (!string.IsNullOrWhiteSpace(headerKey) && !request.Headers.Contains(headerKey))
            {
                request.Headers.TryAddWithoutValidation(headerKey, headerVal);
            }
        }

        var sw = Stopwatch.StartNew();
        WebhookDelivery updatedDelivery;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var response = await client.SendAsync(request, linkedCts.Token);
            sw.Stop();

            var statusCode = (int)response.StatusCode;
            var durationMs = (int)sw.ElapsedMilliseconds;
            var responseSnippet = await ReadResponseSnippetAsync(response, ct);

            if (response.IsSuccessStatusCode)
            {
                updatedDelivery = delivery with
                {
                    Status = WebhookDeliveryStatus.Sent,
                    LastStatusCode = statusCode,
                    DurationMs = durationMs,
                    ResponseSnippet = responseSnippet,
                    LastError = null,
                    LeaseUntil = null,
                    SentAt = DateTimeOffset.UtcNow
                };
                FrogFrameTelemetry.WebhooksDispatched.Add(1);
            }
            else if (statusCode == 410) // 410 Gone: Destination permanently gone -> deactivate webhook
            {
                updatedDelivery = delivery with
                {
                    Status = WebhookDeliveryStatus.Dead,
                    LastStatusCode = 410,
                    DurationMs = durationMs,
                    ResponseSnippet = responseSnippet,
                    LastError = "Endpoint returned 410 Gone. Webhook has been automatically deactivated.",
                    LeaseUntil = null
                };
                // Automatically deactivate webhook
                var deactivatedWebhook = webhook with { IsActive = false, ModifiedAt = DateTimeOffset.UtcNow };
                await store.SaveAsync(deactivatedWebhook, ct);
                logger.LogWarning("Webhook {WebhookId} deactivated after endpoint returned 410 Gone", webhook.Id);
                FrogFrameTelemetry.WebhooksFailed.Add(1);
            }
            else if (statusCode is 408 or 429 || statusCode >= 500) // Transient failures -> retry
            {
                var nextAttempt = delivery.Attempts + 1;
                var shouldRetry = RetryPolicy.ShouldRetry(nextAttempt);

                updatedDelivery = delivery with
                {
                    Status = shouldRetry ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Dead,
                    Attempts = nextAttempt,
                    LastStatusCode = statusCode,
                    DurationMs = durationMs,
                    ResponseSnippet = responseSnippet,
                    LastError = $"Server returned HTTP {statusCode}. {(shouldRetry ? "Retrying" : "Max retry attempts reached.")}",
                    NextAttemptAt = shouldRetry ? RetryPolicy.CalculateNextAttempt(nextAttempt, now) : delivery.NextAttemptAt,
                    LeaseUntil = null
                };
                FrogFrameTelemetry.WebhooksFailed.Add(1);
            }
            else // Other 4xx client errors -> dead immediately without retry
            {
                updatedDelivery = delivery with
                {
                    Status = WebhookDeliveryStatus.Dead,
                    LastStatusCode = statusCode,
                    DurationMs = durationMs,
                    ResponseSnippet = responseSnippet,
                    LastError = $"Endpoint returned non-retryable HTTP {statusCode}.",
                    LeaseUntil = null
                };
                FrogFrameTelemetry.WebhooksFailed.Add(1);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            var nextAttempt = delivery.Attempts + 1;
            var shouldRetry = RetryPolicy.ShouldRetry(nextAttempt);

            updatedDelivery = delivery with
            {
                Status = shouldRetry ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Dead,
                Attempts = nextAttempt,
                DurationMs = (int)sw.ElapsedMilliseconds,
                LastError = $"Dispatch exception: {ex.Message}",
                NextAttemptAt = shouldRetry ? RetryPolicy.CalculateNextAttempt(nextAttempt, now) : delivery.NextAttemptAt,
                LeaseUntil = null
            };
            FrogFrameTelemetry.WebhooksFailed.Add(1);
        }

        await store.UpdateDeliveryAsync(updatedDelivery, ct);
        return updatedDelivery;
    }

    public static string ComputeHmacSha256(string secret, string data)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using var hmac = new HMACSHA256(keyBytes);
        return Convert.ToHexString(hmac.ComputeHash(dataBytes)).ToLowerInvariant();
    }

    private static async Task<string> ReadResponseSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var contentStream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1024]; // read at most 1KB to keep memory minimal
            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read == 0) return "";
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return text.Length > 500 ? text[..500] : text;
        }
        catch
        {
            return "";
        }
    }
}

public sealed class WebhookDeliveryWorker(
    WebhookDispatcher dispatcher,
    IWebhookStore store,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private const int BatchSize = 10;
    private static readonly TimeSpan PerDeliveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BatchLease = PerDeliveryTimeout * BatchSize + TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Deliveries in a batch are sent one after another, each with a 10 s timeout. The lease must outlast
                // the whole batch, otherwise another instance reclaims (and re-sends) deliveries still waiting here.
                var due = await store.ClaimDueAsync(limit: BatchSize, leaseFor: BatchLease, stoppingToken);
                foreach (var delivery in due)
                {
                    try
                    {
                        await dispatcher.DispatchAsync(delivery, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Failed to dispatch webhook delivery {DeliveryId}", delivery.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "WebhookDeliveryWorker execution error");
            }
        }
    }
}

