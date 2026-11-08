# FrogFramework: Project Status Report & Feature Roadmap

This document summarizes the current development status of FrogFramework, including completed features, testing coverage, backlog items (gap analysis against modern enterprise platform capabilities), and the upcoming development roadmap.

*Last updated: November 2026*

---

## 1. Current Project Health

- **Compilation Status (`dotnet build`):** `0 Errors`, `0 Critical Warnings` across all projects (`Framework.Core`, `Framework.Persistence.PostgreSql`, `Framework.Web`, `Apps.Sample`, `Framework.Tests`).
- **Unit & Integration Test Status:** **142 Passed, 0 Failed** (34 tests decorated with `PgTest` are skipped when the local PostgreSQL server is inactive).
- **Database & Migration Status:** 23 PostgreSQL migrations registered and verified (`001_initial.sql` through `023_user_preferences.sql`).
- **Git Repository Status:** Clean working tree.

---

## 2. Completed Features

### A. Core Foundation & Production Hardening (P0 - 100% Complete)
- **Postgres Distributed Lock & Scheduler:** Atomic distributed locking based on PostgreSQL advisory locks to prevent duplicate execution of multi-tenant background cron jobs.
- **Data Protection & Encryption:** Persistent XML key ring storage in PostgreSQL database (`019_data_protection_keys.sql`).
- **Security & Pipeline Hardening:** Rate limiting protection, anti-tamper forms, IDOR mitigation, SQL injection sanitization, and security header middleware (CSP, HSTS, X-Frame-Options).
- **Relational Integrity (Link Checking):** Automatic referential integrity validation preventing deletion of parent documents still referenced by other documents or child tables.
- **Observability:** OpenTelemetry Tracing, activity metrics (Meters & Activities), and centralized Error Logging (`020_error_logs.sql`).
- **Backup & Disaster Recovery:** Comprehensive DR runbooks and k6 load testing baselines ([`backup-disaster-recovery.md`](backup-disaster-recovery.md)).
- **Automated CI & Documentation Pipeline:** GitHub Actions workflows for continuous integration (`ci.yml` - build, vulnerability check, in-memory & PostgreSQL test suite) and automated DocFX website publishing (`deploy-docs.yml`).

### B. Modern UI/UX & Design
- **Corporate Blue Theme & Full Dark Mode:** Seamless light and dark mode support with synchronized theme CSS variables.
- **Layout & Navigation:** Sticky topbar, role-based dynamic navigation, collapsible sidebar, breadcrumbs, row-range footer, and modern wave visual accents.
- **Admin Drawers:** Standard and double-width (`admin-drawer-2x`) drawer components for configuring complex forms without page transitions.

### C. Pilot Features & Productivity
1. **Multi-File Attachments per Document (P1-1):**
   - Multi-file uploads, MIME type inspection (magic byte sniffing), secure PostgreSQL BLOB storage, and ACL enforcement.
2. **Email Outbox & Template Engine (P1-2):**
   - Idempotent email outbox queue, retry backoff, dead-letter queue, and dynamic email templating.
3. **Forgot & Reset Password (P1-3):**
   - Self-service password reset workflow via secure, single-use, auto-expiring email token links.
4. **Awesomebar / Global Search (`Ctrl+K` / `Alt+Q`):**
   - Centralized search palette indexing DocTypes, administrative pages, action shortcuts, and workspace modules.
5. **Data Grid Column Preferences (P1-5):**
   - Per-user customizable table columns (visibility toggle, column reordering, width persistence in `023_user_preferences.sql`).
6. **Background Job Monitor & Error Log Dashboard:**
   - Real-time diagnostic viewer at `/admin/jobs` and `/admin/error-logs` with stack trace drawer inspection.
7. **Single DocType (Platform Feature):**
   - Singleton document schema per tenant for system and module configuration (e.g. `AccountingSettings`).
   - Dedicated APIs (`GetSingleAsync`, `SetSingleAsync`), compiler validation (workflow rejection), and UI adaptations (direct form routing, breadcrumbs, search quick action suppression).

---

## 3. Backlog & Gap Analysis (Enterprise Platform Parity)

| Feature / Capability | Priority | Current Status | Description |
|---|---|---|---|
| **Single DocType** | P1 | Completed | Singleton document per tenant for system/module global settings (`GetSingleAsync`/`SetSingleAsync`). |
| **Static Link Filters** | P1 | Completed | Metadata-driven query filter for Link fields (e.g., leaf accounts only in General Ledger). |
| **Import: Update & Upsert Modes** | P1 | Completed | Excel/CSV import supporting updating existing records by key or adding new records. |
| **Database Role Segregation** | P2 | Planned | Separation of database migration owner role from runtime application role for append-only audit enforcement. |
| **Dynamic Workflows** | P2 | Planned | Visual state machine transition editor with conditional approvals. |

---

## 4. Next Steps & Development Roadmap

1. **Production Readiness Hardening (P0/P1):** Complete the items identified in the [Production Readiness Checklist](production-readiness.md) (secret isolation, demo decoupling, automated backup verification, migration runner isolation).
2. **Database Role Segregation (P2):** Separation of database migration owner role from runtime application role for append-only audit enforcement.
3. **Dynamic Workflows (P2):** Visual state machine transition editor with conditional approvals.
