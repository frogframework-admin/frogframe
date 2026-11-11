# Documentation & Guides

<p align="center">
  <img src="images/logo.png" alt="FrogFramework Logo" width="120" />
</p>

Welcome to the **FrogFramework** developer guides, architectural blueprints, and technical specifications.

## Prerequisites

- **.NET 10 SDK** (v10.0.100 or later)
- **Docker & Docker Compose** (for running PostgreSQL and Redis)
- **Node.js is NOT required** — the entire documentation website and API reference are generated natively using .NET DocFX.

## Running Locally

```bash
# Run the host sample application (opens http://localhost:5080)
dotnet run --project src/Apps.Sample

# Run unit and integration tests
dotnet test
```

---

## Technical Guides

### Core Features & Workflows
- **[DocTypes & Visual Workflows](guides/doctype-and-workflows.md)**: Designing metadata schemas, dynamic UI forms, and state machine workflows.
- **[Outbound Webhooks](webhooks.md)**: HMAC-signed, SSRF-protected event dispatching with deduplicated delivery queue and exponential retries.
- **[Auto Repeat (Recurring Documents)](auto-repeat.md)**: Automated recurring document generation with catch-up logic and concurrency safeguards.
- **[C# Server Scripts (Roslyn Sandbox)](server-scripts.md)**: In-process sandboxed C# Roslyn compilation for DocEvents, Custom APIs, and Cron jobs.
- **[Inbound Email & Communication Timeline](communications.md)**: Bidirectional MailKit SMTP/IMAP, 4-stage thread matching, and unified document history timeline.
- **[SQL Dashboards & Widgets](guides/sql-dashboards.md)**: Building real-time SVG charts and analytical KPI cards from SQL queries.
- **[Saved Query REST APIs](guides/saved-query-apis.md)**: Publishing read-only SQL queries as secure REST endpoints with parameter allowlists.
- **[API Keys & Custom Methods](guides/api-integrations.md)**: Granular integration API keys and custom RPC `IApiMethod` endpoints.
- **[Excel & CSV Data Import](guides/excel-import.md)**: Multi-step bulk data import with dry-run validation simulations.
- **[Template Reporting (Excel & Word)](guides/template-reporting.md)**: High-performance OpenXML spreadsheet and document generation with zero external dependencies.
- **[PDF Printing & Server-Side Rendering](guides/pdf-printing.md)**: Letterhead configuration and headless Chromium PDF export.
- **[Generic Lookups & Hierarchies](guides/generic-lookup-and-hierarchy.md)**: Server-paginated lookup dialogs and circular reference prevention in self-referencing tree structures.
- **[File & Image Storage](guides/file-and-image-storage.md)**: Binary magic-byte sniffing, PostgreSQL BLOB storage, and orphan cleanup.
- **[Analytics Engine (World 2 OLAP)](guides/analytics-engine.md)**: The "Two Worlds" architecture, automatic metadata index synchronization, and performance benchmarks.

### Architecture & Production Operations
- **[Frequently Asked Questions (FAQ)](faq.md)**: Architectural philosophy, why Blazor, EF Core vs. ADO.NET, lean infrastructure, and roadmap vision.
- **[High-Priority Gaps & Frappe Parity](high-priority-gaps.md)**: Architectural comparison against Frappe Framework, Frappe parity analysis, and upcoming milestones.
- **[Project Structure Reference](PROJECT_STRUCTURE.md)**: Complete 4-project solution layout, dependency graphs, and subsystem designs.
- **[Roadmap & Feature Gap Analysis](project-status-roadmap.md)**: Current completion status and enterprise platform feature gap analysis.
- **[Production Readiness Checklist](production-readiness.md)**: Pre-production hardening (P0), scale-up readiness (P1), and Day-2 operations checklist (P2).
- **[Security Baseline (OWASP Top 10)](security-baseline.md)**: Mapping of application security controls to OWASP Top 10 standards with automated test evidence.
- **[Observability & Telemetry](observability.md)**: OpenTelemetry tracing spans, BCL metrics (`Meter`), and centralized error logs.
- **[Backup & Disaster Recovery Runbook](backup-disaster-recovery.md)**: RPO/RTO targets, PostgreSQL snapshot routines, and restoration procedures.
- **[Hooks, Form Actions & Scheduler](hooks-formactions-scheduler.md)**: Document lifecycle hooks, UI action buttons, and distributed cron job scheduling.
