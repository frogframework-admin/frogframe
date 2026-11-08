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

