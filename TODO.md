# FrogFramework Project TODO & Production Readiness Tracking

Master TODO list tracking completed foundations, pre-production requirements, scaling enhancements, and operational items.

For full architectural context and runbooks, refer to [Production Readiness Guide](docs/production-readiness.md) and [Project Status Roadmap](docs/project-status-roadmap.md).

---

## 🟢 Completed & Verified Foundations

- [x] **Automated CI Workflow** (`.github/workflows/ci.yml`):
  - .NET 10 solution build & test suite execution.
  - PostgreSQL 17 test database integration with read-only reporting role.
  - NuGet package vulnerability scanning.
- [x] **Automated Documentation Pipeline** (`.github/workflows/deploy-docs.yml`):
  - DocFX generation and deployment to GitHub Pages.
- [x] **Clean Multi-Project Architecture**:
  - `Framework.Core`, `Framework.Scripting`, `Framework.Persistence.PostgreSql`, `Framework.Web`, `Apps.Erp`.
- [x] **OWASP Top 10 Security Hardening**:
  - Data protection key persistence in PostgreSQL, anti-tamper forms, IDOR mitigation, SQL injection prevention.
- [x] **Distributed Locking & Concurrency Control**:
  - PostgreSQL advisory lock (`PostgresDistributedLock`) and optimistic document version concurrency.
- [x] **Health Check Endpoints**:
  - `/health/live`, `/health/ready`, `/healthz`.
- [x] **Observability Primitives**:
  - OpenTelemetry Tracing spans, activity meters, and centralized database error logs (`sys_error_logs`).
- [x] **Load Testing Baseline**:
  - Grafana k6 ramped load test verifying up to 100 concurrent VUs (`tests/load/k6-baseline.js`).
- [x] **Backup & Disaster Recovery Runbook**:
  - PostgreSQL snapshot and recovery guide with RPO/RTO targets (`docs/backup-disaster-recovery.md`).

---

## 🔴 P0 — Pre-Production Critical (Must Complete Before Production Go-Live)

- [x] **Decouple Demo Configuration from Production**:
  - `docker-compose.yml` defaults to Development (demo on) during development; `ASPNETCORE_ENVIRONMENT=Production` turns demo mode off by itself.
  - `Demo:SeedData` and `Demo:ShowAccounts` are off by default, and the app refuses to start outside Development while they are on (unless explicitly allowed for a demo server).
- [x] **Secret Management & External Store Integration**:
  - Secrets come from `.env` or secret files (`/run/secrets`: Docker secrets, Kubernetes, Key Vault/Vault CSI); `.env.example` has production guidance.
  - Outside Development the app refuses to start with placeholder, default or short secrets. See `docs/guides/secrets.md`.
- [x] **PostgreSQL Network Isolation**:
  - `docker-compose.yml` publishes no database port (only `docker-compose.demo.yml`, on `127.0.0.1`).
- [x] **Automated Backup & Restore Drill**:
  - `deploy/scripts/restore-drill.sh`: restore into a throwaway database, row counts + checksums vs. the source snapshot, RTO check, JSON report.
- [x] **Database Migration Deployment Strategy**:
  - `--migrate-only` step separate from startup (`Database:MigrateOnStartup=false`); Compose: `db-backup` → `migrate` → `app`.
  - Pre-migration snapshot (`deploy/docker/pre-migrate-backup.sh`). See `docs/guides/database-migrations.md`.
- [ ] **Write Performance Optimization & Target Alignment**:
  - Address p95 document write latency under 100 VU spikes (~1,020 ms vs target 400 ms) in `tests/load/README.md`.
  - Tuned (bounded pool, read cache per save, batched commit): write benchmark p95 1,299 → 292 ms at 100 simultaneous users, no more "too many clients" errors (`docs/guides/performance.md`). Remaining: re-run k6 on the target environment.

---

## 🟡 P1 — Scale-Up & Enterprise Hardening (Before Scaling Workloads)

- [x] **Release CD Pipeline (GitHub Actions)**:
  - `release.yml`: image build, image smoke test, GHCR publish, staging, production behind required reviewers.
  - `deploy.yml`: deploy, smoke test, automatic rollback. See `docs/guides/release.md`.
- [x] **Fail-Fast Startup Configuration Validation**:
  - Outside Development the app stops at startup on demo seeding, placeholder/default/short secrets (database, bootstrap admin, S3, AI), a missing public URL or `AllowedHosts=*`. See `docs/guides/deployment-checks.md`.
- [ ] **End-to-End Observability & Alerting** (partly done):
  - Done: OpenTelemetry traces and metrics with an OTLP exporter (`OpenTelemetry:OtlpEndpoint` / `OTEL_EXPORTER_OTLP_ENDPOINT`).
  - Open: alerting rules (error rate, p95 latency, deadlocks, pool exhaustion, failing health probes) and a reference dashboard.
- [ ] **Automated Post-Deployment Smoke Tests** (partly done):
  - Done: `deploy/scripts/smoke-test.sh` after every deploy (health, readiness, API login, `/login`), with automatic rollback.
  - Open: CRUD, workflow transition and tenant-boundary checks against the deployed instance.
- [x] **Tiered Endpoint Rate Limiting**:
  - Sign-in and password reset per client IP (also the browser login form); API read, write and heavy (reports, saved queries, print) per user or API key, each with its own budget. See `docs/guides/rate-limiting.md`.
- [x] **Automated Dependency Management**:
  - `.github/dependabot.yml`: weekly grouped updates for NuGet, the .NET SDK (`global.json`), GitHub Actions, the Dockerfile and the compose images (no PostgreSQL major upgrades).

---

## 🔵 P2 — Operational Governance & Maintenance Hygiene

- [x] **Repository Hygiene**:
  - `.github/CODEOWNERS`, pull request template, issue templates (bug, feature, private security reporting) and `SECURITY.md`.
  - `CHANGELOG.md` (Keep a Changelog, `vMAJOR.MINOR.PATCH` tags).
- [ ] **Administrator & User Onboarding Guides**:
  - Complete Tenant Provisioning, Role Management, and Operational Troubleshooting Runbooks.
- [ ] **Formalized Service Level Objectives (SLOs)**:
  - Document availability targets, p95 latency SLOs, and quarterly DR drill schedules.
- [x] **Test Badge & Documentation Alignment**:
  - Synchronize README test badges with latest CI test counts (268 In-Memory + 34 PostgreSQL = 302 total).

---

## 🚀 Application Feature Backlog & Operational Pilot Roadmap

### Priority 1: Pilot Daily Operations & Essential Workflows (P1)

- [x] **P1-5 — Export List & Bulk Actions** (High Urgency):
  - Add Export to Excel (`.xlsx`) and CSV on the `ListPage.razor` toolbar respecting permissions and active filters.
  - Implement bulk operations: Bulk Submit, Bulk Cancel, and Bulk Edit Field (extending existing Bulk Delete).
- [x] **P1-4 — Comments & @mention Timeline** (High Urgency):
  - Embed internal collaboration thread below `FormPage.razor` interleaved with audit trail history.
  - Mention parser supporting `@username` and `@everyone`, with strict tenant isolation, document read permission, and row-level scope enforcement.
  - Real-time in-app notifications delivered via SignalR hub (`/hubs/notifications`) and outbox wake-up signal.
  - Dedicated `/mentions` inbox page with live unread badge on sidebar navigation.
  - Configurable pagination via `appsettings.json` (`Ui:MentionsPageSize` = 20, `Ui:HistoryCommentsPageSize` = 10) with Load More support.
  - Real-time search filters in both `/mentions` and the Form **History & Comments** tab.
  - Interactive comment counter (`0 / 5000`) with auto-disable and red limit highlight.
  - Custom activity icons on `AuditTimeline` replacing empty circle markers (`message-square`, `pencil`, `plus`, `check`, `x`, etc.).
  - Document Send Email drawer enhanced with `BbRichTextEditor`, automatic Document Print PDF attachment, multi-attachment upload, and filename editing.
  - Live document presence indicator deduplication per user and exclusion of current user.
- [x] **P1-8 — Saved Filters / Custom Views** (Medium Urgency):
  - Persist and load user-defined named search filter combinations per DocType via `IUserPreferenceStore` (`SavedFilterClause`, `SavedCustomView`, `DocTypeCustomViewsPreference`).
  - View selector dropdown, interactive filter condition builder drawer, active filter chips bar with quick-remove, and Manage Saved Views drawer in `ListPage.razor`.
  - Seamless query filtering in `Docs.ListAsync` and consistent export filtering in `Docs.ExportListAsync`.
- [x] **P1-7 — Assign To / ToDo & "My Tasks" Widget** (Medium Urgency):
  - Document delegation to one or more users with due date, priority, and notes (`036_assignments.sql`, `AssignmentService`).
  - Real-time in-app notifications (`AssignmentNotifier`), daily due/overdue reminders (`AssignmentReminderJob`), and automatic cancellation on document deletion (`AssignmentDocumentCleanupHandler`).
  - FormPage **Assign** drawer & header assignee strip, `/tasks` page, sidebar live badge, Home **My Tasks** widget, and REST API endpoints.

### Priority 2: Platform Scalability & Extensibility (P2)

- [x] **Custom Fields / Customize Form**:
  - Add or reorder DocType fields dynamically from web UI without modifying base JSON schemas (`033_customizations.sql`).
- [x] **Webhooks Dispatcher**:
  - Outbox-driven document event payload dispatcher to external webhook URLs with HMAC signatures and auto-retries (`027_webhooks.sql`).
- [x] **Auto Repeat & C# Roslyn Server Scripting**:
  - Recurring documents (`028_auto_repeat.sql`), sandboxed C# Server & Client Scripts (`029_server_scripts.sql`, `034_client_scripts.sql`), and Inbound/Outbound Email Communications (`030_communications.sql`).
- [x] **Ad-Hoc Report Builder**:
  - Columns, sort, filter, group and saved reports. See `docs/guides/report-builder.md`.
- [x] **Dynamic Row-Level Security (User Permissions per Record)**:
  - Per-document shares to users or roles from the **Share** button (`037_doc_shares`). See `docs/guides/row-sharing.md`.
- [x] **Advanced Authentication (SSO & 2FA)**:
  - Authenticator-app 2FA with recovery codes, required per role if configured; Google and Microsoft Entra ID sign-in for existing accounts. See `docs/guides/two-factor-and-sso.md`.
- [ ] **Generic Document Rename** (postponed):
  - Rename document keys with automated cascading updates across all foreign Link references in parent and child tables.
- [x] **Ad-Hoc Background Jobs (`frappe.enqueue`)**:
  - `BackgroundTaskService.EnqueueAsync` + `IBackgroundTaskHandler`; imports and bulk actions; `/jobs` with live progress, cancel and run again. See `docs/guides/background-jobs.md`.
- [x] **Dynamic Visual Workflows**:
  - Flow designer canvas (drag states, draw transitions, conditions, roles, required comments; publish drafts). See `docs/guides/visual-workflow-editor.md`.
- [x] **Database Role Segregation**:
  - Requests run as `frogframe_app` (data only, no DDL, ledger append-only); the owner runs migrations, indexes and reporting views. The migrate step creates and grants the role; startup warns when the runtime role is privileged. See `docs/guides/database-roles.md`.

### Priority 3: Specialized Views & Value-Add Enhancements (P3)

- [x] **Alternate Views**: see `docs/guides/list-views-board-and-calendar.md`.
  - Kanban board (workflow state columns with drag-and-drop progression).
  - Calendar view (date-based document schedules and due dates).
  - Tree view (hierarchical structure navigation for Chart of Accounts and Cost Centers).
- [x] **Visual Print Format Builder & Letterhead UI**:
  - Letterheads and print formats per tenant with a live preview (`/admin/print-designs`, form-based, no drag-and-drop). See `docs/guides/print-designer.md`.
- [x] **Social Features**:
  - Colour-coded document tags (filterable in lists), personal bookmarks, and Follow with change notifications. See `docs/guides/tags-bookmarks-follow.md`.
- [x] **Dynamic Multi-Language (i18n)**:
  - Runtime UI localization toggle between English and Indonesian (`035_translations.sql`).


