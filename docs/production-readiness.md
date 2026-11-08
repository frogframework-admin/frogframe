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
  - Unit and integration test suite passing (142 In-Memory tests passed; 34 PostgreSQL integration tests passed in CI).
- [x] **Documentation & Site Deployment (`.github/workflows/deploy-docs.yml`)**:
  - Automated DocFX build and deployment to GitHub Pages.
- [x] **Modular Enterprise Architecture**:
  - Clean separation across `Framework.Core`, `Framework.Persistence.PostgreSql`, `Framework.Web`, and `Apps.Sample`.
  - Zero external third-party dependencies for OpenXML reporting and SVG chart rendering.
- [x] **Security Baseline & OWASP Controls**:
  - XML data protection keys persisted in PostgreSQL (`019_data_protection_keys.sql`).
  - Security headers middleware (CSP, HSTS, X-Frame-Options, X-Content-Type-Options).
  - Anti-tamper form protection, IDOR mitigation, SQL injection prevention, and tenant isolation checks.
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

- [ ] **Decouple Demo Credentials & Production Configuration**:
  - `docker-compose.yml` currently runs with `ASPNETCORE_ENVIRONMENT=Production` while hardcoding `Demo__SeedData=true`, `Demo__ShowAccounts=true`, and default passwords.
  - **Action**: Split compose files into `docker-compose.yml` (production baseline) and `docker-compose.demo.yml` (demo overrides). Ensure demo seeding and account display are strictly disabled in production.
- [ ] **Secret Management & External Secret Stores**:
  - Database credentials (`frogframe`, `frogframe_ro`), bootstrap admin passwords, and API encryption keys are present in compose and sample files.
  - **Action**: Migrate all sensitive values to `.env` files (excluded from git), Docker Secrets, Azure Key Vault, or HashiCorp Vault.
- [ ] **PostgreSQL Network Isolation**:
  - PostgreSQL port `5432:5432` is mapped directly to host in `docker-compose.yml`.
  - **Action**: In production deployments, remove public port mapping so the database is only accessible via the private Docker bridge network (`frogframe_default`) or secure internal VPC.
- [ ] **Real Backup & Restore Drills**:
  - `docs/backup-disaster-recovery.md` outlines procedures, but restore drills must be validated with automation.
  - **Action**: Create an automated test script to execute a dump, drop, restore into an isolated instance, and verify data checksums and application readiness.
- [ ] **Database Migration Deployment Strategy**:
  - `Program.cs` currently executes migrations on startup. In multi-instance / replica environments, simultaneous startup can cause race conditions or unhandled failures.
  - **Action**: Establish an isolated migration runner step (pre-deploy job / Kubernetes init container / standalone CLI tool) with advisory lock protection and automatic pre-migration backup snapshots.
- [ ] **Review & Optimize Write Performance Budget**:
  - Under 100 VU concurrent spike in `tests/load/README.md`, p95 write latency reached ~1,020 ms against the documented threshold of 400 ms.
  - **Action**: Tune transactional outbox batching, optimize multi-table child inserts, review PostgreSQL connection pool settings, or update performance budget thresholds to match realistic production expectations.

---

### P1 — Scale-Up & Enterprise Hardening (Before Scaling Users & Workloads)

- [ ] **Automated Release & CD Pipeline**:
  - Current CI tests code but does not publish release artifacts.
  - **Action**: Add GitHub Actions workflow for building Docker images, publishing to container registries (GHCR / Docker Hub), deploying to Staging, executing smoke tests, manual production approval gate, and automatic rollback on health probe failure.
- [ ] **Fail-Fast Startup Configuration Validation**:
  - **Action**: Add startup validation in `Hosting.cs` to verify that when `ASPNETCORE_ENVIRONMENT=Production`:
    - `Demo:SeedData` is `false`.
    - `Bootstrap:AdminPassword` is not empty and not the default fallback.
    - Connection strings point to valid, non-default credentials.
    - Halts application execution immediately with a clear diagnostic error if invariants fail.
- [ ] **End-to-End Observability & Alerting**:
  - OpenTelemetry instrumentation is implemented, but operational backend connectivity and alerting need standardization.
  - **Action**: Configure standard OTLP exporters (Prometheus, Grafana, Jaeger, or Azure Monitor). Define alert thresholds for:
    - HTTP error rate > 1%
    - p95 latency > 500 ms
    - Deadlock occurrences & connection pool exhaustion
    - Health check probe failures
- [ ] **Automated Post-Deployment Smoke Tests**:
  - **Action**: Create an automated end-to-end smoke test script validating authentication, basic DocType CRUD, workflow state transition, and tenant boundary enforcement after deployment.
- [ ] **Granular Tiered Rate Limiting**:
  - `Api__RateLimitPerMinute=10000` is currently a single flat threshold.
  - **Action**: Differentiate rate limit policies per endpoint type (e.g. strict rate limiting for login/auth endpoints, moderate for document writes, higher for cached read queries).
- [ ] **Automated Dependency Management**:
  - **Action**: Add `.github/dependabot.yml` to automatically monitor and open pull requests for NuGet dependencies and GitHub Actions updates.

---

### P2 — Operational Governance & Maintenance Hygiene

- [ ] **Repository Hygiene & Maintainer Assets**:
  - Add `.github/CODEOWNERS` for code review routing.
  - Add `.github/pull_request_template.md` and issue templates (`.github/ISSUE_TEMPLATE/`).
  - Add `CHANGELOG.md` and semantic release tagging guidelines.
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

