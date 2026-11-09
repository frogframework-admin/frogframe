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
- [x] **Clean 4-Project Architecture**:
  - `Framework.Core`, `Framework.Persistence.PostgreSql`, `Framework.Web`, `Apps.Sample`.
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

- [ ] **Decouple Demo Configuration from Production**:
  - Separate `docker-compose.yml` (production-ready) from `docker-compose.demo.yml` (demo seeds).
  - Ensure `Demo:SeedData` and `Demo:ShowAccounts` are disabled by default in production.
- [ ] **Secret Management & External Store Integration**:
  - Move hardcoded database passwords and bootstrap admin credentials to `.env` or external secret stores (Docker secrets, Key Vault, HashiCorp Vault).
  - Provide `.env.example` template with production guidelines.
- [ ] **PostgreSQL Network Isolation**:
  - Remove public host port mapping (`5432:5432`) in production Compose configuration.
- [ ] **Automated Backup & Restore Drill**:
  - Build and verify an automated restoration verification script to prove end-to-end recovery time and data integrity.
- [ ] **Database Migration Deployment Strategy**:
  - Separate migration execution from application startup to support multi-replica rolling deployments without race conditions.
  - Implement pre-migration database snapshot backup hook.
- [ ] **Write Performance Optimization & Target Alignment**:
  - Address p95 document write latency under 100 VU spikes (~1,020 ms vs target 400 ms) in `tests/load/README.md`.

---

## 🟡 P1 — Scale-Up & Enterprise Hardening (Before Scaling Workloads)

- [ ] **Release CD Pipeline (GitHub Actions)**:
  - Add workflow for container build, publish to registry (GHCR/Docker Hub), staging deploy, automated smoke tests, and production approval gate.
- [ ] **Fail-Fast Startup Configuration Validation**:
  - Add runtime validation on boot: abort startup immediately if production environment runs with demo seeds or default credentials.
- [ ] **End-to-End Observability & Alerting**:
  - Configure OTLP exporters for Prometheus/Grafana/Jaeger; establish alerting rules for error rates, latency spikes, and DB deadlocks.
- [ ] **Automated Post-Deployment Smoke Tests**:
  - Automate post-deployment health, login, CRUD, workflow, and tenant boundary verification.
- [ ] **Tiered Endpoint Rate Limiting**:
  - Transition from single global rate limit (`Api:RateLimitPerMinute=10000`) to route-specific policies (stricter for auth/write, higher for query APIs).
- [ ] **Automated Dependency Management**:
  - Add Dependabot configuration (`.github/dependabot.yml`) for NuGet and GitHub Actions.

---

## 🔵 P2 — Operational Governance & Maintenance Hygiene

- [ ] **Repository Hygiene**:
  - Add `.github/CODEOWNERS`, pull request template (`.github/pull_request_template.md`), and issue templates.
  - Maintain `CHANGELOG.md` with semantic versioning tags.
- [ ] **Administrator & User Onboarding Guides**:
  - Complete Tenant Provisioning, Role Management, and Operational Troubleshooting Runbooks.
- [ ] **Formalized Service Level Objectives (SLOs)**:
  - Document availability targets, p95 latency SLOs, and quarterly DR drill schedules.
- [ ] **Test Badge & Documentation Alignment**:
  - Synchronize README test badges with latest CI test counts (142 In-Memory + 34 PostgreSQL = 176 total).

---

## 🚀 Application Feature Backlog & Operational Pilot Roadmap

### Priority 1: Pilot Daily Operations & Essential Workflows (P1)

- [ ] **P1-5 — Export List & Bulk Actions** (High Urgency):
  - Add Export to Excel (`.xlsx`) and CSV on the `ListPage.razor` toolbar respecting permissions and active filters.
  - Implement bulk operations: Bulk Submit, Bulk Cancel, and Bulk Edit Field (extending existing Bulk Delete).
- [ ] **P1-4 — Comments & @mention Timeline** (High Urgency):
  - Embed internal collaboration thread below `FormPage.razor` interleaved with audit trail history.
  - Deliver automated in-app notifications when a user is tagged with `@username`.
- [ ] **P1-8 — Saved Filters / Custom Views** (Medium Urgency):
  - Persist and load user-defined named search filter combinations per DocType via `IUserPreferenceStore`.
- [ ] **P1-7 — Assign To / ToDo & "My Tasks" Widget** (Medium Urgency):
  - Implement document delegation to users, due date tracking, and a "My Tasks" dashboard widget for pending assignments.

### Priority 2: Platform Scalability & Extensibility (P2)

- [ ] **Custom Fields / Customize Form per Tenant**:
  - Add or reorder DocType fields dynamically from web UI per tenant without modifying base JSON schemas.
- [ ] **Webhooks Dispatcher**:
  - Outbox-driven document event payload dispatcher to external webhook URLs with HMAC signatures and auto-retries.
- [ ] **Ad-Hoc Report Builder**:
  - Self-service builder for non-technical users to select columns, sort, filter, and group data into custom saved reports.
- [ ] **Dynamic Row-Level Security (User Permissions per Record)**:
  - Granular per-document access sharing to specific users or roles directly from the interface.
- [ ] **Advanced Authentication (SSO & 2FA)**:
  - Google and Microsoft Entra ID (OIDC) SSO integration, plus TOTP app-based two-factor authentication.
- [ ] **Generic Document Rename**:
  - Rename document keys with automated cascading updates across all foreign Link references in parent and child tables.
- [ ] **Ad-Hoc Background Jobs (`frappe.enqueue`)**:
  - Asynchronous heavy task queue with live progress bar feedback in the web UI.
- [ ] **Dynamic Visual Workflows**:
  - Visual state machine transition editor with conditional approval branches.
- [ ] **Database Role Segregation**:
  - Separation of migration owner role from runtime application connection role.

### Priority 3: Specialized Views & Value-Add Enhancements (P3)

- [ ] **Alternate Views**:
  - Kanban board (workflow state columns with drag-and-drop progression).
  - Calendar view (date-based document schedules and due dates).
  - Tree view (hierarchical structure navigation for Chart of Accounts and Cost Centers).
- [ ] **Visual Print Format Builder & Letterhead UI**:
  - Interactive corporate letterhead manager and visual print template layout editor.
- [ ] **Social Features**:
  - Color-coded document tags, personal bookmarks/favorites, and Follow Document subscriptions.
- [ ] **Dynamic Multi-Language (i18n)**:
  - Runtime UI localization toggle between English and Indonesian.


