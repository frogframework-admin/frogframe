# DocFrame Observability & Telemetry Guide

Panduan observabilitas terpadu pada DocFrame: OpenTelemetry traces, metrics, structured logging, dan antarmuka administrasi.

---

## 1. Arsitektur Telemetri

DocFrame mengimplementasikan instrumentasi telemetri standar .NET BCL (`System.Diagnostics.DiagnosticSource` & `System.Diagnostics.Metrics`) di `Framework.Core` dengan **zero runtime dependencies**:
- **Source / Meter Name**: `DocFrame`
- **Version**: `1.0.0`

### 1.1 Metrik yang Tersedia (`Meter`)

| Nama Metrik | Tipe | Deskripsi | Tags / Dimensi |
|---|---|---|---|
| `docframe.http.requests` | Counter | Total permintaan HTTP masuk | `method`, `scope` |
| `docframe.http.errors` | Counter | Total unhandled exception pada pipeline HTTP/API | - |
| `docframe.documents.operations` | Counter | Total operasi dokumen (insert, update, delete) | `doctype`, `operation` |
| `docframe.scheduled_jobs.runs` | Counter | Total eksekusi scheduled job | `job`, `status` |
| `docframe.outbox.processed` | Counter | Total event transactional outbox berhasil | `type` |
| `docframe.outbox.failed` | Counter | Total event transactional outbox gagal/retry | `type` |

### 1.2 Distributed Tracing (`ActivitySource` Spans)

| Nama Span | Komponen | Atribut Utama |
|---|---|---|
| `docframe.document.insert` | DocumentService | `docframe.doctype`, `docframe.tenant`, `docframe.user`, `docframe.name` |
| `docframe.document.update` | DocumentService | `docframe.doctype`, `docframe.name`, `docframe.tenant`, `docframe.user` |
| `docframe.document.delete` | DocumentService | `docframe.doctype`, `docframe.name`, `docframe.tenant`, `docframe.user` |
| `docframe.document.action` | DocumentService | `docframe.doctype`, `docframe.name`, `docframe.action`, `docframe.tenant`, `docframe.user` |
| `docframe.scheduler.job` | Scheduler | `docframe.job_name`, `docframe.tenant`, `docframe.trigger` (`manual` / `scheduled`) |
| `docframe.outbox.process` | OutboxProcessor | `docframe.event_type`, `docframe.event_id`, `docframe.tenant` |

---

## 2. Mengaktifkan OpenTelemetry OTLP Exporter

DocFrame mendukung ekspor data trace dan metrik langsung ke kolektor OTLP (OpenTelemetry Collector, Jaeger, Prometheus, Grafana Tempo/Mimir, Datadog) melalui konfigurasi:

### appsettings.json
```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

### Environment Variable
```bash
OTEL_EXPORTER_OTLP_ENDPOINT="http://otel-collector:4317"
```

Bila konfigurasi ini diisi, `AddDocFrameOpenTelemetry` secara otomatis mendaftarkan OTLP trace & metric exporters. Bila tidak diisi, telemetri tetap terekam secara lokal tanpa overhead jaringan.

---

## 3. Antarmuka Administrasi

DocFrame menyediakan 2 halaman admin terpadu untuk tim operasional:
1. **/admin/jobs** (`ScheduledJobsAdmin.razor`):
   - Daftar job yang terdaftar dan jadwal cron.
   - Waktu run terakhir dan status (Success / Failed / SkippedLocked).
   - Tombol manual **"Run Now"** per tenant.
2. **/admin/error-logs** (`ErrorLogAdmin.razor`):
   - Riwayat unhandled exception per tenant.
   - Detail stack trace, HTTP method, URL, dan user ID.
   - Aksi **"Mark as Seen"** (terisolasi per tenant) dan **"Clear All"**.

