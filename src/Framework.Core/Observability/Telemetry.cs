using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Framework.Observability;

/// <summary>
/// DocFrame OpenTelemetry-compatible metrics and activity tracing sources.
/// Compatible with standard .NET OpenTelemetry collectors without external dependencies.
/// </summary>
public static class DocFrameTelemetry
{
    public const string SourceName = "DocFrame";
    public const string Version = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    // Meters and Counters
    public static readonly Counter<long> DocumentOperations =
        Meter.CreateCounter<long>("docframe.documents.operations", "operations", "Count of document lifecycle operations (insert, update, submit, cancel, delete)");

    public static readonly Counter<long> ScheduledJobRuns =
        Meter.CreateCounter<long>("docframe.scheduled_jobs.runs", "runs", "Count of scheduled job runs executed");

    public static readonly Counter<long> OutboxEventsProcessed =
        Meter.CreateCounter<long>("docframe.outbox.processed", "events", "Count of transactional outbox events processed");

    public static readonly Counter<long> OutboxEventsFailed =
        Meter.CreateCounter<long>("docframe.outbox.failed", "events", "Count of transactional outbox events that failed or dead-lettered");

    public static readonly Counter<long> HttpErrors =
        Meter.CreateCounter<long>("docframe.http.errors", "errors", "Count of unhandled HTTP server exceptions captured");
}

