# Service Level Objectives (SLOs) & Reliability Framework

This document defines the formal Service Level Indicators (SLIs), Service Level Objectives (SLOs), error budget policies, and disaster recovery drill cadences for FrogFramework in production operations.

---

## 1. Scope & Taxonomy

| Term | Definition |
|---|---|
| **SLI (Service Level Indicator)** | A quantifiable metric measured in real time (e.g. request latency, error rate, outbox lag). |
| **SLO (Service Level Objective)** | A target reliability objective agreed upon between engineering and business stakeholders (e.g. 99.9% availability). |
| **SLA (Service Level Agreement)** | A contractual commitment with business consequences (e.g. service credits) if SLO thresholds are breached. |
| **Error Budget** | The allowed margin of unreliability ($100\% - \text{SLO}$), spent during deployments, experiments, and transient failures. |

---

## 2. Availability SLOs

### 2.1 Target Availability

FrogFramework targets **99.9% availability** ("three nines") per rolling 30-day window across core operational workloads.

$$\text{Availability} = \frac{\text{Successful Requests (HTTP status } < 500\text{)}}{\text{Total Valid HTTP Requests}} \times 100\% \ge 99.9\%$$

### 2.2 Downtime Budget

| Window | Allowed Downtime (at 99.9%) | Allowed Failed Requests (per 1M reqs) |
|---|---|---|
| **Daily** | 1 minute 26 seconds | 1,000 |
| **Weekly** | 10 minutes 4 seconds | 7,000 |
| **Monthly (30 Days)** | 43 minutes 12 seconds | 30,000 |
| **Quarterly** | 2 hours 9 minutes | 90,000 |

### 2.3 Exclusions from Availability Calculation
The following events are excluded from availability calculations:
- Scheduled maintenance windows communicated at least 48 hours in advance (conducted outside business hours).
- Force majeure events, upstream network ISP outages, or cloud provider regional unavailability.
- Client-side errors (HTTP 4xx series: bad requests, unauthenticated attempts, permission denials, rate limits).

---

## 3. Latency & Performance SLOs

All performance objectives are evaluated against real-time OpenTelemetry metrics and verified via regular k6 load test baselines (`tests/load/k6-baseline.js`).

| Service Tier | SLI Metric | Target Threshold | Measuring Endpoint / Scope |
|---|---|---|---|
| **Health Probes** | p95 Latency | $< 50\text{ ms}$ | `GET /health/live`, `GET /health/ready` |
| **Document Reads** | p95 Latency | $< 200\text{ ms}$ | `GET /api/data/{doctype}` (Indexed queries) |
| **Transactional Writes (Steady)** | p95 Latency | $< 400\text{ ms}$ | `POST/PUT /api/data/{doctype}` (under normal load $\le 50\text{ VU}$) |
| **Transactional Writes (Peak Spike)** | p95 Latency | $< 1.8\text{ s}$ | Multi-table transactions during concurrent 100 VU bursts |
| **Analytical / Saved Queries** | p95 Latency | $< 1.5\text{ s}$ | `/api/query/*`, `/reporting/*` against read-replica |
| **Template & PDF Generation** | p95 Latency | $< 2.5\text{ s}$ | `/api/print/*`, OpenXML `.xlsx` / `.docx` report generation |
| **Asynchronous Outbox Dispatch** | End-to-End Latency | $< 60\text{ s}$ | Elapsed duration from document commit to outbox delivery |

---

## 4. Disaster Recovery (DR) & Data Protection Objectives

Data durability and recovery capabilities are guaranteed through PostgreSQL write-ahead logging (WAL), automated snapshots, and isolated S3 object storage backups.

### 4.1 RPO & RTO Targets

| Metric | Target Objective | Implementation Mechanism |
|---|---|---|
| **RPO (Recovery Point Objective)** | $\le 24\text{ hours}$ (Standard)<br/>$\le 15\text{ minutes}$ (Continuous) | Daily automated `pg_dump` snapshot.<br/>Continuous WAL archiving to offsite storage bucket. |
| **RTO (Recovery Time Objective)** | $< 30\text{ minutes}$ | Automated restore verification script (`deploy/scripts/restore-drill.sh`). Baseline measured: $< 2\text{ minutes}$ for standard database volumes. |

### 4.2 Backup Retention Schedule

| Backup Tier | Frequency | Retention Window | Storage Target |
|---|---|---|---|
| **Pre-Migration Snapshot** | Before every deployment | 5 newest retained | Local volume `frogframe-backups` (`db-backup` service) |
| **Daily Full Dump** | Daily at 02:00 UTC | 30 days | Encrypted offsite S3 / Cloud Storage bucket |
| **Weekly Snapshot** | Sundays | 12 weeks | Encrypted offsite S3 / Cloud Storage bucket |
| **Monthly Snapshot** | 1st of each month | 12 months | Immutable / WORM cloud archive storage |

---

## 5. Quarterly Disaster Recovery Drill Schedule

To prove that backup archives are valid, uncorrupted, and restorable within the documented RTO target, the engineering team executes automated disaster recovery drills every calendar quarter.

### 5.1 Drill Schedule

| Quarter | Drill Window | Scope | Verification Tool |
|---|---|---|---|
| **Q1** | Last week of March | Database restore, checksum parity & migration check | `deploy/scripts/restore-drill.sh` |
| **Q2** | Last week of June | Full snapshot restore + S3 attachment blob verification | `restore-drill.sh` + S3 bucket sync check |
| **Q3** | Last week of September | Standalone failover drill to secondary staging host | Cold container standup from backup |
| **Q4** | Last week of December | Annual comprehensive business continuity audit | Full disaster recovery simulation |

### 5.2 Drill Execution Procedure

Run the automated drill script against the production backup archive:

```bash
# Execute drill on throwaway database (verifies restore time, schema, row counts, and checksums)
./deploy/scripts/restore-drill.sh --dump /backups/FrogFramework_latest.dump --report /backups/dr_report_Q1.json
```

### 5.3 Drill Acceptance Criteria
1. **Zero Data Loss**: Content checksums and row counts match 100% against the source snapshot.
2. **Migration Consistency**: All registered PostgreSQL migrations (`001` through `045+`) are applied and verified.
3. **RTO Compliance**: Total restore and verification duration does not exceed `RTO_TARGET_SECONDS` (1,800 seconds / 30 minutes).
4. **Audit Trail**: JSON report generated and signed off by Lead SRE and System Administrator.

---

## 6. Error Budget Management & Escalation Policies

Error budgets prevent over-engineering while protecting end-user reliability:

```
[Monthly Error Budget = 0.1%]
       │
       ├─► Budget Consumed < 50%  ──► Green: Standard release cycles and feature deployments.
       │
       ├─► Budget Consumed 50-80% ──► Yellow: Review recent deployments; require approval for high-risk changes.
       │
       └─► Budget Consumed > 80%  ──► Red: Freeze non-critical feature releases.
                                            Prioritize bug fixes, reliability tasks, and performance tuning.
```

### 6.1 Burn Rate Alerting Thresholds

Alerts fire when the error budget is consumed at an accelerated rate:

- **14.4x Burn Rate (2% consumed in 1 hour)**: Critical Page (P0 alert). Immediate investigation by on-call engineer.
- **6x Burn Rate (5% consumed in 6 hours)**: High Priority (P1 alert). Investigation within 30 minutes.
- **1x Burn Rate (10% consumed in 3 days)**: Warning (P2 alert). Review during weekly sprint triage.
