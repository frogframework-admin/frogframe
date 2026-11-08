# FrogFrame Observability & Telemetry Guide

Unified observability guide for FrogFrame: OpenTelemetry traces, metrics, structured logging, and administrative diagnostics.

---

## 1. Telemetry Architecture

FrogFrame implements standard .NET BCL telemetry instrumentation (`System.Diagnostics.DiagnosticSource` & `System.Diagnostics.Metrics`) within `Framework.Core` with **zero runtime external dependencies**:
- **Source / Meter Name**: `FrogFrame`
- **Version**: `1.0.0`

### 1.1 Available Metrics (`Meter`)

| Metric Name | Type | Description | Dimensions / Tags |
|---|---|---|---|
| `FrogFrame.http.requests` | Counter | Total incoming HTTP requests | `method`, `scope` |
| `FrogFrame.http.errors` | Counter | Total unhandled exceptions on HTTP/API pipeline | - |
| `FrogFrame.documents.operations` | Counter | Total document operations (insert, update, delete) | `doctype`, `operation` |
| `FrogFrame.scheduled_jobs.runs` | Counter | Total executions of background scheduled jobs | `job`, `status` |
| `FrogFrame.outbox.processed` | Counter | Total transactional outbox events processed | `type` |
| `FrogFrame.outbox.failed` | Counter | Total transactional outbox events failed/retried | `type` |

### 1.2 Distributed Tracing (`ActivitySource` Spans)

| Span Name | Component | Core Attributes |
|---|---|---|
| `FrogFrame.document.insert` | DocumentService | `FrogFrame.doctype`, `FrogFrame.tenant`, `FrogFrame.user`, `FrogFrame.name` |
| `FrogFrame.document.update` | DocumentService | `FrogFrame.doctype`, `FrogFrame.name`, `FrogFrame.tenant`, `FrogFrame.user` |
| `FrogFrame.document.delete` | DocumentService | `FrogFrame.doctype`, `FrogFrame.name`, `FrogFrame.tenant`, `FrogFrame.user` |
| `FrogFrame.outbox.dispatch` | OutboxWorker | `FrogFrame.event_type`, `FrogFrame.tenant` |
| `FrogFrame.job.execute` | JobScheduler | `FrogFrame.job_name`, `FrogFrame.tenant` |

---

## 2. Structured Logging & Error Handling

FrogFrame uses structured Serilog/Microsoft.Extensions.Logging with persistent database sinks for critical issues:
- **Error Log Table**: `FrogFrame_error_logs` (`020_error_logs.sql`).
- **Log Viewer**: Available to administrators at `/admin/error-logs`.
- **Log Scrubbing**: Passwords, authentication tokens, and sensitive headers are redacted before persistence.

---

## 3. Production Exporters

OpenTelemetry exporters can be configured in `appsettings.json` or environment variables:
- **Prometheus**: Scraped via standard `/metrics` endpoint.
- **OTLP (gRPC / HTTP)**: Direct telemetry streaming to collectors (Jaeger, Grafana Tempo, Datadog, New Relic).
