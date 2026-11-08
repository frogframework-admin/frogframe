# DocFrame — Metadata-Driven .NET Business Application Framework

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL_3.0-blue.svg)](LICENSE)
[![Build & Test](https://img.shields.io/badge/Tests-152%20Passing-brightgreen.svg)]()
[![Documentation](https://img.shields.io/badge/Docs-DocFX-informational.svg)](docs/index.md)

**DocFrame** is an extensible, metadata-driven business application framework built on **.NET 10**, **ASP.NET Core**, **Blazor Server (Interactive)**, and **[Blazor Blueprint UI](https://blazorblueprintui.com)** (shadcn/ui-styled components for Blazor).

Inspired by modern, metadata-oriented architectures (such as Frappe Framework), DocFrame provides a cohesive set of foundational primitives for rapid enterprise software development: **DocType metadata modeling**, **document lifecycle & validation pipeline**, **declarative role-based permissions**, **workflow state machines**, **generic UI views and REST APIs**, and an **extensible plugin/hook architecture**.

Storage can be seamlessly toggled via configuration: **in-memory** (for development, demos, and testing) or **PostgreSQL** (production-grade persistence with advisory locks and analytical read replicas). Both backends pass the same test suites.

---

## Current Status (v19)

### Completed and Verified
- **Data Import**: Multi-step Excel/CSV import engine with validation simulations and failure reporting.
- **Reporting Engine**: OpenXML template generation for Excel and Word documents (`.xlsx`, `.docx`) without external third-party dependencies.
- **API Extensibility**: Integration API Keys, custom `IApiMethod` endpoints, and Saved Query APIs.
- **Master Roles & Permissions**: Single-source-of-truth master roles, strict tenant isolation, and field-level permission hierarchies (`permLevel`).
- **Module Hierarchies**: Mandatory module categorization per DocType with uppercase immutable module codes and navigation menus.
- **Global Search**: Unified Awesomebar (`Ctrl+K` / `Alt+Q`) indexing DocTypes, pages, actions, and modules.
- **Security & Reliability**: Startup metadata audits, orphan file cleanup, and PostgreSQL advisory locking for distributed cron jobs.

### In Progress & Planned Roadmap
- Static link filters from metadata (e.g., leaf accounts only in General Ledger).
- Singleton DocType per tenant for application-wide settings.
- Separation of database migration owner role from runtime application role.
- Excel/CSV import upsert modes (Update and Add-or-Update).

---

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (v10.0.100 or later).
- [Docker & Docker Compose](https://www.docker.com/) (optional, for PostgreSQL mode).

### Running Locally (In-Memory Mode)

```bash
# Run the host sample application (opens on http://localhost:5080)
dotnet run --project src/Apps.Sample

# Run the automated test suite
dotnet test
```

Demo seed data is generated automatically upon startup. Seeding executes through the standard `DocumentService` pipeline, ensuring that validations and workflows are verified during application boot.

### Running with PostgreSQL

```bash
# 1. Start PostgreSQL 17 container
docker compose up -d

# 2. Run the application with PostgreSQL provider
dotnet run --project src/Apps.Sample --Persistence:Provider=PostgreSql
```

---

## Architecture & Subsystems

DocFrame is divided into four cleanly decoupled projects:

```text
DocFrame/
├── src/
│   ├── Framework.Core/               # Pure domain & metadata compiler (no web dependencies)
│   ├── Framework.Persistence.PostgreSql/ # Production storage, index sync & analytics
│   ├── Framework.Web/                # Blazor UI, Identity, REST endpoints & Adapters
│   └── Apps.Sample/                  # Sample application host & Procurement/HR pilot
├── tests/
│   └── Framework.Tests/              # Unit, integration, and benchmark test suite
└── docs/                             # Technical documentation & guides (DocFX)
```

### 1. Pure Domain Engine (`Framework.Core`)
- **Expressions Engine**: Custom lexer, AST parser, and sandboxed interpreter with resource limits and an allowlist of built-in functions (`empty`, `len`, `sum`, `round`, `abs`, `coalesce`, `today`, `has_role`).
- **Metadata Compiler**: Strongly-typed definitions for DocTypes, fields, permissions, list views, and workflows. Performs topological sorting on computed fields to detect circular dependencies.
- **Document Service**: The unified, canonical save pipeline: authorize → load & verify version → normalize → reject mass assignment → run hooks → compute formula fields → validate rules → commit atomically (document + audit + outbox).
- **Security**: Granular permission resolution (`all`, `owner`, `match:field=attribute`) and multi-tenant scoping.

### 2. UI System (`Framework.Web`)
Built using Blazor Blueprint components styled with OKLCH theme variables:
- Dynamic schema-driven document form rendering (`BbTabs`, `BbCard`, `BbField`, `BbInput`, `BbSelect`, `BbCombobox`).
- Server-side searchable Link dropdowns.
- Real-time workflow state badges and status stamp components.
- Generic organizational tree visualizations (`OrgTree`).

### 3. PostgreSQL Persistence & Analytics (`Framework.Persistence.PostgreSql`)
- **JSONB Document Store**: Unified `docframe_documents` table with GIN index acceleration and optimistic concurrency checks (`version = @expected`).
- **Metadata Index Synchronizer**: Automatically creates and drops B-tree and GIN trigram indexes based on `listView` configurations concurrently.
- **Reporting Schema Views**: Dynamic `reporting.<doctype>` views populated for fast analytical queries and SQL dashboards.
- **Transactional Outbox**: Guaranteed event delivery with worker lease locks (`FOR UPDATE SKIP LOCKED`).

---

## Authentication & User Accounts

Authentication is powered by **ASP.NET Core Identity** (cookie-based for browsers, Bearer tokens for REST APIs). Claims map immutably to `UserContext`:

| Claim Type | Content | `UserContext` Property |
|---|---|---|
| `nameidentifier` | Unique Account ID | `Id` (Document owner) |
| `docframe:tenant` | Tenant ID | `TenantId` |
| `docframe:full_name` | Full Name | `FullName` |
| `role` | Assigned Roles | `Roles` |
| `docframe:department` | Department Code | `Attributes["department"]` |
| `docframe:employee` | Linked Employee ID | `Attributes["employee"]` |

Claims missing a tenant are rejected immediately (fail-closed). Accounts lock automatically for 15 minutes after 5 consecutive failed attempts.

---

## SQL Dashboards & Saved Query APIs

DocFrame allows administrators to build live dashboards and publish read-only SQL queries as secure REST endpoints:

- **Isolated Read-Only User**: Analytical queries execute under a dedicated `docframe_ro` role with restricted privileges and per-connection session isolation.
- **Visual Chart Widgets**: Bar, Line, Pie, Doughnut, Single Value KPI, and Data Grid widgets rendered using lightweight, pure SVG.
- **Saved Query APIs**: Published at `/api/query/{slug}` with strict parameter allowlists, CSV/JSON outputs, and response caching.

```bash
# Querying a published SQL endpoint
curl -H "Authorization: ApiKey dfk_k1a2b3....<secret>" \
     "http://localhost:5080/api/query/procurement-by-dept?Dept=IT"
```

---

## Extensibility: Hooks, Form Actions & Scheduler

Extend business logic cleanly without monkey-patching:

1. **Document Lifecycle Hooks**: Implement `IDocumentHook` for stages such as `BeforeValidate`, `BeforeSave`, `AfterSave`, `BeforeSubmit`, `BeforeDelete`, etc.
2. **Custom Form Actions**: Register toolbar actions using `IFormAction<TDoc>`.
3. **Background Scheduler**: Register periodic jobs using `AddScheduledJob<TJob>()`, guaranteed to run with distributed advisory locks across nodes.

```csharp
// Example: Registering a periodic background sync job
builder.Services.AddScheduledJob<DailySyncJob>();

// Example: Registering a custom lifecycle hook
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestApprovalHook>();
```

For detailed architecture and code examples, see [Architecture Guide: Hooks, Actions & Scheduler](docs/hooks-formactions-scheduler.md).

---

## REST API Overview

DocFrame provides auto-generated REST endpoints for every API-enabled DocType:

```bash
# 1. Obtain an access token
TOKEN=$(curl -s -X POST -H "Content-Type: application/json" \
  -d '{"userName":"andi","password":"DocFrame#2026"}' \
  http://localhost:5080/api/auth/login | jq -r .accessToken)

# 2. Query documents with filters and sorting
curl -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5080/api/data/PurchaseRequest?filters=[[\"priority\",\"=\",\"High\"]]&sort=total&dir=desc"

# 3. Transition document workflow
curl -X POST -H "Authorization: Bearer $TOKEN" -H "If-Match: 1" \
  -H "Content-Type: application/json" -d '{}' \
  "http://localhost:5080/api/data/PurchaseRequest/PR-2026-00001/actions/Submit"
```

All errors conform to RFC 7807 `application/problem+json`.

---

## Documentation & Website

The complete technical documentation, architecture specifications, and .NET API references are built with **DocFX**:

- **Local Preview**:
  ```powershell
  dotnet docfx --serve
  ```
  Visit `http://localhost:8080` to browse guides, architecture blueprints, and API documentation.
- **Guides Index**: [docs/index.md](docs/index.md)
- **Project Structure Blueprint**: [docs/PROJECT_STRUCTURE.md](docs/PROJECT_STRUCTURE.md)
- **Security Baseline (OWASP Top 10)**: [docs/security-baseline.md](docs/security-baseline.md)
- **Observability Guide**: [docs/observability.md](docs/observability.md)
- **Backup & Disaster Recovery Runbook**: [docs/backup-disaster-recovery.md](docs/backup-disaster-recovery.md)

---

## License

FrogFrame is licensed under the [GNU Affero General Public License v3.0](LICENSE).
