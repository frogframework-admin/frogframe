# FrogFramework Observability & Telemetry Guide

Unified observability guide for FrogFramework: OpenTelemetry traces, metrics, structured logging, and administrative diagnostics.

---

## 1. Telemetry Architecture

FrogFramework implements standard .NET BCL telemetry instrumentation (`System.Diagnostics.DiagnosticSource` & `System.Diagnostics.Metrics`) within `Framework.Core` with **zero runtime external dependencies**:
- **Source / Meter Name**: `FrogFramework`
- **Version**: `1.0.0`

### 1.1 Available Metrics (`Meter`)

| Metric Name | Type | Description | Dimensions / Tags |
|---|---|---|---|
| `FrogFramework.http.requests` | Counter | Total incoming HTTP requests | `method`, `scope` |
| `FrogFramework.http.errors` | Counter | Total unhandled exceptions on HTTP/API pipeline | - |
| `FrogFramework.documents.operations` | Counter | Total document operations (insert, update, delete) | `doctype`, `operation` |
| `FrogFramework.scheduled_jobs.runs` | Counter | Total executions of background scheduled jobs | `job`, `status` |
| `FrogFramework.outbox.processed` | Counter | Total transactional outbox events processed | `type` |
| `FrogFramework.outbox.failed` | Counter | Total transactional outbox events failed/retried | `type` |

### 1.2 Distributed Tracing (`ActivitySource` Spans)

| Span Name | Component | Core Attributes |
|---|---|---|
| `FrogFramework.document.insert` | DocumentService | `FrogFramework.doctype`, `FrogFramework.tenant`, `FrogFramework.user`, `FrogFramework.name` |
| `FrogFramework.document.update` | DocumentService | `FrogFramework.doctype`, `FrogFramework.name`, `FrogFramework.tenant`, `FrogFramework.user` |
| `FrogFramework.document.delete` | DocumentService | `FrogFramework.doctype`, `FrogFramework.name`, `FrogFramework.tenant`, `FrogFramework.user` |
| `FrogFramework.outbox.dispatch` | OutboxWorker | `FrogFramework.event_type`, `FrogFramework.tenant` |
| `FrogFramework.job.execute` | JobScheduler | `FrogFramework.job_name`, `FrogFramework.tenant` |

---

## 2. Structured Logging & Error Handling

FrogFramework uses structured Serilog/Microsoft.Extensions.Logging with persistent database sinks for critical issues:
- **Error Log Table**: `frogframe_error_logs` (`020_error_logs.sql`).
- **Log Viewer**: Available to administrators at `/admin/error-logs`.
- **Log Scrubbing**: Passwords, authentication tokens, and sensitive headers are redacted before persistence.

---

## 3. Production Exporters

OpenTelemetry exporters can be configured in `appsettings.json` or environment variables:
- **Prometheus**: Scraped via standard `/metrics` endpoint.
- **OTLP (gRPC / HTTP)**: Direct telemetry streaming to collectors (Jaeger, Grafana Tempo, Datadog, New Relic).
