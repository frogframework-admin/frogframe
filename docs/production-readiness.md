# FrogFramework — Production Readiness Checklist & Operational Roadmap

> **Status Reference**: This document serves as the master production readiness checklist, operational gap analysis, and deployment hardening roadmap for **FrogFramework**. It tracks prerequisites required before enterprise production deployments, scaling phases, and Day-2 operations.

---

## 1. Executive Summary

| Phase | Category | Focus | Target Readiness |
|---|---|---|---|
| **Baseline** | Completed Foundation | Architecture, Testing, CI, OWASP Baseline | **100% In Place** |
| **P0** | Pre-Production Hardening | Secret Management, Config Decoupling, Network Isolation, Migration Safety | **Action Required** |
| **P1** | Scaling & Automation | Release CD Pipeline, Observability End-to-End, Production Smoke Tests | **Next Sprint** |
| **P2** | Operations & Governance | Repository Hygiene, Admin Runbooks, SLOs, Maintenance Discipline | **Operational Backlog** |

---

## 2. Completed Foundation (Verified in Repository)

The following baseline pillars are already implemented, tested, and passing:

- [x] **CI Pipeline (`.github/workflows/ci.yml`)**:
  - Automated build and test on .NET 10 SDK with Release configuration.
  - Integration testing with PostgreSQL 17 alpine service container and reporting read-only role setup.
  - Automated dependency vulnerability checks (`dotnet list package --vulnerable --include-transitive`).
  - Unit and integration test suite passing (250 In-Memory tests passed; 34 PostgreSQL integration tests passed in CI).
- [x] **Documentation & Site Deployment (`.github/workflows/deploy-docs.yml`)**:
  - Automated DocFX build and deployment to GitHub Pages.
- [x] **Modular Enterprise Architecture**:
  - Clean separation across `Framework.Core`, `Framework.Scripting`, `Framework.Persistence.PostgreSql`, `Framework.Web`, and `Apps.Erp`.
  - Zero external third-party dependencies for OpenXML reporting and SVG chart rendering.
- [x] **Security Baseline & OWASP Controls**:
  - XML data protection keys persisted in PostgreSQL (`019_data_protection_keys.sql`).
  - Security headers middleware (CSP, HSTS, X-Frame-Options, X-Content-Type-Options).
  - Anti-tamper form protection, IDOR mitigation, SQL injection prevention, and tenant isolation checks.
  - Two-factor authentication (authenticator app, recovery codes, optional per-role requirement) and Google / Entra ID single sign-on (`docs/guides/two-factor-and-sso.md`). Before go-live, decide which roles must use 2FA (`FROGFRAME_2FA_REQUIRED_ROLES`).
  - Database role segregation: requests run as the data-only role `frogframe_app`, migrations and other DDL as the owner (`docs/guides/database-roles.md`). Before upgrading, add `FROGFRAME_APP_PASSWORD` to `.env`.
- [x] **Distributed Concurrency & Locking**:
  - PostgreSQL advisory lock implementation (`PostgresDistributedLock`) for multi-tenant background cron tasks.
  - Optimistic concurrency control (`version = @expected`) on document updates.
- [x] **Observability Primitives**:
  - OpenTelemetry Tracing spans and .NET BCL Meter activity metrics (`FrogFrameworkTelemetry`).
  - Centralized database error logging (`sys_error_logs`) with web drawer inspection.
- [x] **Health Check Probes**:
  - Health check endpoints (`/health/live`, `/health/ready`, `/healthz`) integrated with document store connectivity.
- [x] **Performance Baseline (Grafana k6)**:
  - Multi-stage load testing script (`tests/load/k6-baseline.js`) verifying 100 Virtual Users concurrent load.

---

## 3. Production Readiness Checklist

### P0 — Pre-Production Critical (Must Complete Before Production Go-Live)

- [x] **Decouple Demo Credentials & Production Configuration**:
  - `docker-compose.yml` defaults to Development during the development phase (demo mode on). Setting `ASPNETCORE_ENVIRONMENT=Production` turns demo mode off by itself: the `FROGFRAME_DEMO_*` switches default to `false` and there is no default demo password outside Development. `docker-compose.demo.yml` additionally publishes the PostgreSQL/S3 ports on localhost.
  - Outside Development the application refuses to start while demo mode is on, unless `Demo:AllowOutsideDevelopment=true` with a non-default password of 12+ characters (dedicated demo servers). See `docs/guides/deployment-checks.md`.
- [x] **Secret Management & External Secret Stores**:
  - Compose reads every secret from `.env` (not tracked by git; required variables fail fast). The app also reads secret files from `/run/secrets` (Docker/Kubernetes secrets, Key Vault or Vault CSI mounts).
  - Outside Development the app refuses to start while a secret in use is a `change-me` placeholder, a repository/common default, or (passwords) shorter than 12 characters. Rotation steps: `docs/guides/secrets.md`.
- [x] **PostgreSQL Network Isolation**:
  - `docker-compose.yml` publishes no PostgreSQL (or S3) port; the database is only reachable on the compose network. `docker-compose.demo.yml` publishes them on `127.0.0.1` for local development only.
- [x] **Real Backup & Restore Drills**:
  - `deploy/scripts/restore-drill.sh` restores into a throwaway database. It compares per-table row counts and content checksums with the source (taken in the same snapshot as the dump), checks the migrations, measures restore time against the RTO, and writes an optional JSON report. It can also verify an existing backup file. See `docs/backup-disaster-recovery.md`.
- [x] **Database Migration Deployment Strategy**:
  - `--migrate-only` / `--migration-status` run migrations as their own step (advisory lock, one transaction per migration). With `Database:MigrateOnStartup=false` the application waits for that step instead of migrating.
  - Docker Compose runs `db-backup` (verified `pg_dump` snapshot) → `migrate` → `app`. See `docs/guides/database-migrations.md`.
- [ ] **Review & Optimize Write Performance Budget** (tuned; confirm with k6 on the target environment):
  - Under 100 VU concurrent spike in `tests/load/README.md`, p95 write latency reached ~1,020 ms against the documented threshold of 400 ms.
  - **Action**: Tune transactional outbox batching, optimize multi-table child inserts, review PostgreSQL connection pool settings, or update performance budget thresholds to match realistic production expectations.
  - **Done**:
    - bounded connection pool (30 per instance unless configured);
    - read cache per save (7 → 3 database reads per Purchase Request);
    - batched lock, schema check, audit and outbox in the transaction.
    - Write benchmark (`--bench-write`, 100 simultaneous users): p95 1,299 ms with 14 errors → 292 ms with no errors, and 131 → 409 saves/s. See `docs/guides/performance.md`.
  - **Remaining**: re-run the k6 test (payload updated to the current metadata) on the production-like environment.

---

### P1 — Scale-Up & Enterprise Hardening (Before Scaling Users & Workloads)

- [x] **Automated Release & CD Pipeline**:
  - `release.yml` builds the image, smoke-tests it in Production mode with the real compose file, and publishes it to GHCR. It then deploys to staging and, after approval by the required reviewers of the `production` environment, to production.
  - `deploy.yml` runs `deploy/scripts/deploy.sh` (db-backup → migrate → app) and `deploy/scripts/smoke-test.sh`. When the smoke test fails, it rolls back to the previous image automatically. See `docs/guides/release.md`.
- [x] **Fail-Fast Startup Configuration Validation**:
  - Outside Development, `DeploymentChecks` stops the application before migrating or seeding when demo seeding is on, a secret (database, `Bootstrap:AdminPassword`, S3, AI) is a placeholder, a repository default or too short, `App:PublicBaseUrl` is missing, or `AllowedHosts` is `*`. See `docs/guides/deployment-checks.md`.
- [ ] **End-to-End Observability & Alerting**:
  - OpenTelemetry instrumentation and the OTLP exporter (`OpenTelemetry:OtlpEndpoint`) are implemented; alert rules and a reference dashboard are still open.
  - **Action**: Configure standard OTLP exporters (Prometheus, Grafana, Jaeger, or Azure Monitor). Define alert thresholds for:
    - HTTP error rate > 1%
    - p95 latency > 500 ms
    - Deadlock occurrences & connection pool exhaustion
    - Health check probe failures
- [ ] **Automated Post-Deployment Smoke Tests**:
  - Done: `deploy/scripts/smoke-test.sh` runs after every deploy (health, readiness, API login, login page) and triggers automatic rollback.
  - **Action** (open): extend it with DocType CRUD, a workflow state transition and tenant boundary enforcement.
- [x] **Granular Tiered Rate Limiting**:
  - Tiers with separate budgets: sign-in (1200/min per IP, including the browser login and 2FA forms), password reset (20/min per IP), and per user or API key heavy (60), write (120) and read (300). `429` with `Retry-After`; rejections are logged. See `docs/guides/rate-limiting.md`.
- [x] **Automated Dependency Management**:
  - `.github/dependabot.yml` opens weekly grouped pull requests for NuGet, the .NET SDK, GitHub Actions, the Dockerfile and the compose images.

---

### P2 — Operational Governance & Maintenance Hygiene

- [x] **Repository Hygiene & Maintainer Assets**:
  - `.github/CODEOWNERS`, `.github/pull_request_template.md`, `.github/ISSUE_TEMPLATE/` and `SECURITY.md`.
  - `CHANGELOG.md` with semantic versioning and tagging guidelines.
- [ ] **Administrator & User Onboarding Documentation**:
  - Write an Administrator Guide covering tenant provisioning, role assignments, dynamic menu configuration, and user lifecycle management.
  - Publish an Operational Troubleshooting Runbook (handling deadlocks, outbox retry exhaustion, orphan cleanup).
- [ ] **Documented Service Level Objectives (SLOs)**:
  - Formally document availability target (e.g., 99.9%), latency SLOs, RPO (≤ 24 hours) / RTO (< 30 minutes), backup retention schedules, and quarterly disaster recovery drill cadences.
- [ ] **Documentation & Badge Synchronization**:
  - Keep test badges and roadmap counts synchronized with actual test execution (142 In-Memory + 34 PostgreSQL = 176 total tests).

---

## 4. Recommended Execution Roadmap

```text
Phase 1: Security & Config Decoupling (P0)
├── 1. Separate demo compose from production compose
├── 2. Externalize secrets to .env / secret managers
├── 3. Close external PostgreSQL port 5432 in production
└── 4. Implement fail-fast startup configuration validation

Phase 2: Database & Deployment Protection (P0 & P1)
├── 5. Isolate database migration runner & pre-deploy backup hook
├── 6. Build and verify automated backup-restore drill script
├── 7. Optimize write latency under high VU concurrency
└── 8. Add GitHub Actions CD pipeline (image publish + staging deploy)

Phase 3: Operations & Repository Hygiene (P1 & P2)
├── 9. Configure OTLP exporters, Grafana dashboard & alerting
├── 10. Implement post-deploy automated smoke tests
├── 11. Add Dependabot & repository templates (CODEOWNERS, PR/Issue)
└── 12. Publish Admin Onboarding & Troubleshooting Guides
```

