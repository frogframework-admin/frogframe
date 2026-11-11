using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Framework.Observability;

/// <summary>
/// FrogFrame OpenTelemetry-compatible metrics and activity tracing sources.
/// Compatible with standard .NET OpenTelemetry collectors without external dependencies.
/// </summary>
public static class FrogFrameTelemetry
{
    public const string SourceName = "FrogFrame";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    // Meters and Counters
    public static readonly Counter<long> DocumentOperations =
        Meter.CreateCounter<long>("frogframe.documents.operations", "operations", "Count of document lifecycle operations (insert, update, submit, cancel, delete)");

    public static readonly Counter<long> HttpRequests =
        Meter.CreateCounter<long>("frogframe.http.requests", "requests", "Count of incoming HTTP requests");

    public static readonly Counter<long> ScheduledJobRuns =
        Meter.CreateCounter<long>("frogframe.scheduled_jobs.runs", "runs", "Count of scheduled job runs executed");

    public static readonly Counter<long> OutboxEventsProcessed =
        Meter.CreateCounter<long>("frogframe.outbox.processed", "events", "Count of transactional outbox events processed");

    public static readonly Counter<long> OutboxEventsFailed =
        Meter.CreateCounter<long>("frogframe.outbox.failed", "events", "Count of transactional outbox events that failed or dead-lettered");

    public static readonly Counter<long> HttpErrors =
        Meter.CreateCounter<long>("frogframe.http.errors", "errors", "Count of unhandled HTTP server exceptions captured");

    // Automation & Integration Metrics (Phase 3)
    public static readonly Counter<long> WebhooksDispatched =
        Meter.CreateCounter<long>("frogframe.webhooks.dispatched", "deliveries", "Count of outgoing webhook deliveries attempted");

    public static readonly Counter<long> WebhooksFailed =
        Meter.CreateCounter<long>("frogframe.webhooks.failed", "failures", "Count of outgoing webhook delivery failures");

    public static readonly Counter<long> AutoRepeatRuns =
        Meter.CreateCounter<long>("frogframe.autorepeat.runs", "runs", "Count of recurring document auto-repeat executions");

    public static readonly Counter<long> ServerScriptRuns =
        Meter.CreateCounter<long>("frogframe.scripts.runs", "runs", "Count of dynamic C# server scripts executed");
}

