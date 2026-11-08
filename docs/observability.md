# DocFrame Observability & Telemetry Guide

Unified observability guide for DocFrame: OpenTelemetry traces, metrics, structured logging, and administrative diagnostics.

---

## 1. Telemetry Architecture

DocFrame implements standard .NET BCL telemetry instrumentation (`System.Diagnostics.DiagnosticSource` & `System.Diagnostics.Metrics`) within `Framework.Core` with **zero runtime external dependencies**:
- **Source / Meter Name**: `DocFrame`
- **Version**: `1.0.0`

### 1.1 Available Metrics (`Meter`)

| Metric Name | Type | Description | Dimensions / Tags |
|---|---|---|---|
| `docframe.http.requests` | Counter | Total incoming HTTP requests | `method`, `scope` |
| `docframe.http.errors` | Counter | Total unhandled exceptions on HTTP/API pipeline | - |
| `docframe.documents.operations` | Counter | Total document operations (insert, update, delete) | `doctype`, `operation` |
| `docframe.scheduled_jobs.runs` | Counter | Total executions of background scheduled jobs | `job`, `status` |
| `docframe.outbox.processed` | Counter | Total transactional outbox events processed | `type` |
| `docframe.outbox.failed` | Counter | Total transactional outbox events failed/retried | `type` |

### 1.2 Distributed Tracing (`ActivitySource` Spans)

| Span Name | Component | Core Attributes |
|---|---|---|
| `docframe.document.insert` | DocumentService | `docframe.doctype`, `docframe.tenant`, `docframe.user`, `docframe.name` |
| `docframe.document.update` | DocumentService | `docframe.doctype`, `docframe.name`, `docframe.tenant`, `docframe.user` |
| `docframe.document.delete` | DocumentService | `docframe.doctype`, `docframe.name`, `docframe.tenant`, `docframe.user` |
| `docframe.outbox.dispatch` | OutboxWorker | `docframe.event_type`, `docframe.tenant` |
| `docframe.job.execute` | JobScheduler | `docframe.job_name`, `docframe.tenant` |

---

## 2. Structured Logging & Error Handling

DocFrame uses structured Serilog/Microsoft.Extensions.Logging with persistent database sinks for critical issues:
- **Error Log Table**: `docframe_error_logs` (`020_error_logs.sql`).
- **Log Viewer**: Available to administrators at `/admin/error-logs`.
- **Log Scrubbing**: Passwords, authentication tokens, and sensitive headers are redacted before persistence.

---

## 3. Production Exporters

OpenTelemetry exporters can be configured in `appsettings.json` or environment variables:
- **Prometheus**: Scraped via standard `/metrics` endpoint.
- **OTLP (gRPC / HTTP)**: Direct telemetry streaming to collectors (Jaeger, Grafana Tempo, Datadog, New Relic).
