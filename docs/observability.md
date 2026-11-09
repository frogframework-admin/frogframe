# FrogFramework Observability & Telemetry Guide

Unified observability and telemetry guide for FrogFramework: OpenTelemetry traces, metrics, structured logging, and administrative management interfaces.

---

## 1. Telemetry Architecture

FrogFramework implements standard .NET BCL telemetry instrumentation (`System.Diagnostics.DiagnosticSource` & `System.Diagnostics.Metrics`) within `Framework.Core` with **zero external runtime dependencies**:
- **Source / Meter Name**: `FrogFrame`
- **Version**: `1.0.0`

### 1.1 Available Metrics (`Meter`)

| Metric Name | Type | Description | Dimensions / Tags |
|---|---|---|---|
| `frogframe.http.requests` | Counter | Total incoming HTTP requests | `method`, `scope` |
| `frogframe.http.errors` | Counter | Total unhandled exceptions on HTTP/API pipeline | - |
| `frogframe.documents.operations` | Counter | Total document lifecycle operations (insert, update, delete) | `doctype`, `operation` |
| `frogframe.scheduled_jobs.runs` | Counter | Total executions of background scheduled jobs | `job`, `status` |
| `frogframe.outbox.processed` | Counter | Total transactional outbox events processed | `type` |
| `frogframe.outbox.failed` | Counter | Total transactional outbox events failed or dead-lettered | `type` |

### 1.2 Distributed Tracing (`ActivitySource` Spans)

| Span Name | Component | Core Attributes |
|---|---|---|
| `frogframe.document.insert` | DocumentService | `frogframe.doctype`, `frogframe.tenant`, `frogframe.user`, `frogframe.name` |
| `frogframe.document.update` | DocumentService | `frogframe.doctype`, `frogframe.name`, `frogframe.tenant`, `frogframe.user` |
| `frogframe.document.delete` | DocumentService | `frogframe.doctype`, `frogframe.name`, `frogframe.tenant`, `frogframe.user` |
| `frogframe.document.action` | DocumentService | `frogframe.doctype`, `frogframe.name`, `frogframe.action`, `frogframe.tenant`, `frogframe.user` |
| `frogframe.scheduler.job` | Scheduler | `frogframe.job_name`, `frogframe.tenant`, `frogframe.trigger` (`manual` / `scheduled`) |
| `frogframe.outbox.process` | OutboxProcessor | `frogframe.event_type`, `frogframe.event_id`, `frogframe.tenant` |

---

## 2. Enabling OpenTelemetry OTLP Exporter

FrogFramework supports direct telemetry export to standard OTLP collectors (OpenTelemetry Collector, Jaeger, Prometheus, Grafana Tempo/Mimir, Datadog) via configuration:

### Configuration in `appsettings.json`

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

### Configuration via Environment Variable

```bash
OTEL_EXPORTER_OTLP_ENDPOINT="http://otel-collector:4317"
```

When either configuration setting is supplied, `AddFrogFrameOpenTelemetry` automatically registers the OTLP trace and metric exporters. When omitted, telemetry activities and meters remain active for in-process collectors with zero network overhead.

---

## 3. Administrative Interfaces

FrogFramework provides dedicated operational diagnostic interfaces for system administrators:

### 3.1 Background Jobs (`/admin/jobs`)
Implemented in `ScheduledJobsAdmin.razor`:
- **Job Catalog & Schedules**: Displays all registered background jobs and their cron schedules.
- **Execution History**: Shows last run timestamp and execution status (`Success`, `Failed`, `SkippedLocked`).
- **Manual Trigger**: Provides an on-demand "Run Now" trigger scoped per tenant.

### 3.2 Centralized Error Logs (`/admin/error-logs`)
Implemented in `ErrorLogAdmin.razor`:
- **Tenant Isolation**: Displays unhandled exceptions isolated by tenant from `sys_error_logs` (`020_error_logs.sql`).
- **Diagnostic Drawer**: Full exception inspection including message, stack trace, HTTP method, URL, and user ID.
- **Triage Actions**: Supports tenant-scoped "Mark as Seen" acknowledging issues and "Clear All" log pruning.

---

## 4. Structured Logging & Security Scrubbing

FrogFramework uses structured logging with persistent database storage for critical diagnostics:
- **Error Log Persistence**: Database sink writing directly to `sys_error_logs`.
- **Sensitive Data Redaction**: Automatic scrubbing of passwords, authorization headers, and secret keys before log persistence.

