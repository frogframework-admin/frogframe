# Production Deployment & Operations Guide

This guide provides step-by-step procedures, hardening standards, and operational tips for deploying and operating **FrogFrame** in a production environment.

---

## 1. Pre-Deployment Hardening Checklist

FrogFrame includes automatic **Fail-Fast Startup Checks** that prevent the application from starting in an insecure or misconfigured state outside the Development environment.

| Requirement | Setting in `.env` | Rule / Description |
|---|---|---|
| **Environment Mode** | `ASPNETCORE_ENVIRONMENT=Production` | Automatically disables demo personas, test seeding, and clears default demo passwords. |
| **Public Base URL** | `FROGFRAME_PUBLIC_BASE_URL` | Must be a valid absolute HTTPS URL (e.g. `https://erp.example.com`). Used for email links and password reset tokens. |
| **Allowed Hosts** | `FROGFRAME_ALLOWED_HOSTS` | Explicit domain name whitelist (e.g. `erp.example.com`). Wildcard `*` is strictly blocked. |
| **Database Passwords** | `POSTGRES_PASSWORD`, `FROGFRAME_RO_PASSWORD`, `FROGFRAME_APP_PASSWORD` | Minimum 12 characters, non-default, no placeholder terms like `change-me` or `postgres`. |
| **Object Storage Keys** | `FROGFRAME_S3_ACCESS_KEY`, `FROGFRAME_S3_SECRET_KEY` | Strong access and secret credentials for S3 / SeaweedFS storage. |
| **Admin Password** | `FROGFRAME_ADMIN_PASSWORD` | Initial password for the bootstrap `admin` (System Manager) account. |
| **Mandatory 2FA** | `FROGFRAME_2FA_REQUIRED_ROLES` | Comma-separated list of critical roles required to use TOTP (e.g. `System Manager,HR-Admin,Finance-Admin`). |

---

## 2. Standard Production Deployment Workflow

Production stacks should be launched using the hardened compose override [docker-compose.prod.yml](file:///d:/Side%20Projects/DocFrame/docker-compose.prod.yml):

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

### Deployment Pipeline Stages (Automated)

1. **Stateful Services Ready**: `postgres` and `seaweedfs` start and pass health checks.
2. **Pre-Start Migrator**: Container `frogframe-migrator` runs `dotnet Apps.Erp.dll --migrate-only`:
   - Takes a database snapshot backup before applying changes.
   - Acquires PostgreSQL advisory locks to ensure safe rolling updates across instances.
   - Applies pending SQL schema migrations.
3. **Application Online**: Container `frogframe-app` starts with read/write runtime permissions (`frogframe_app` role), connecting once migrations are verified.

---

## 3. Production Architecture & Infrastructure Best Practices

### 3.1 Reverse Proxy & SSL Termination
Never expose port `8080` (or `5080`) directly to the public internet:
- Place an enterprise reverse proxy (Nginx, Traefik, Caddy, or Cloudflare Tunnel) in front of the application.
- Terminate SSL/TLS with modern cipher suites and enforce HTTPS redirection.
- Forward proxy headers properly:
  ```nginx
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  proxy_set_header X-Forwarded-Proto $scheme;
  proxy_set_header X-Forwarded-Host $host;
  ```
  This ensures FrogFrame's tiered rate limiters accurately identify client IPs.

### 3.2 Operating System & Host Environment
- **Run on Native Linux Servers**: Use Ubuntu LTS, Debian, or Rocky Linux in production. Avoid running production workloads inside Windows WSL2 or Docker Desktop due to virtual disk (`ext4.vhdx`) performance overhead and named pipe instability.
- **Decouple Mounts & Storage**:
  - Mount `/var/lib/postgresql/data` onto dedicated high-speed SSD/NVMe volumes (or use managed cloud databases such as AWS RDS / Google Cloud SQL).
  - Store uploaded files on cloud object storage (S3 / Cloudflare R2 / GCS) or distributed SeaweedFS clusters with RAID protection.

### 3.3 Zero-Downtime Backup & Restore Drills
- **Automated Backups**: Schedule nightly `pg_dump` runs using consistent snapshots (`REPEATABLE READ READ ONLY`).
- **Automated Restore Drills**:
  Test your backups automatically on a weekly schedule using:
  ```bash
  ./deploy/scripts/restore-drill.sh --dump /backups/nightly-backup.dump
  ```
  This script restores the dump into a temporary throwaway database, checks row counts, validates content checksums against the RTO target, and drops the temporary database without impacting active user sessions.

### 3.4 Proactive Health Monitoring & Alerting
- Configure external uptime monitors (Uptime Kuma, Datadog, Prometheus Blackbox) targeting `/health/live` and `/health/ready`.
- Alert on:
  - 3 consecutive failed health probe responses.
  - HTTP 5xx error rate > 1%.
  - Database connection pool saturation > 85%.
  - Host disk I/O errors or S.M.A.R.T reallocated sector warnings.
