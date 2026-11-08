# Backup & Disaster Recovery Runbook

Operational guide for backing up and restoring FrogFrame environments in production.

---

## 1. Recovery Objectives

| Metric | Target | Notes |
|---|---|---|
| **RPO** (Recovery Point Objective) | ≤ 24 hours | Achieved with daily `pg_dump`. Lower RPO requires WAL archiving / point-in-time recovery (PITR). |
| **RTO** (Recovery Time Objective) | < 30 minutes | Baseline test: 1.1s for 0.08 MB dump (development data). Validate against production volumes. |

---

## 2. Backup Scope

All persistent state in FrogFrame resides in PostgreSQL. A single `pg_dump` covers:
- **Documents & Audit**: `frogframe_documents`, `FrogFrame_audit`
- **Uploaded Files / Images**: `FrogFrame_files`
- **Data Protection Keys (Session Cookies)**: `FrogFrame_data_protection_keys`
- **Metadata, Workflows, Menus, Roles, Users**: `FrogFrame_metadata_versions`, `FrogFrame_workflow_versions`, `FrogFrame_navigation_*`, `frogframe_roles`, `frogframe_users`
- **Outbox, Job History, Error Logs**: `FrogFrame_outbox`, `FrogFrame_scheduled_job_runs`, `FrogFrame_error_logs`

> [!CAUTION]
> Data Protection keys stored in the database are sensitive. Restrict backup file permissions and encrypt storage buckets (e.g., AWS S3 SSE, GCP Cloud Storage default encryption).

> [!NOTE]
> The `frogframe_ro` role (read-only analytical user for dashboards) is excluded when restoring with `--no-owner --no-privileges`. Re-run `deploy/sql/reporting-readonly-user.sql` after restoration.

---

## 3. Automated Backup Procedure

Execute the backup script via cron or scheduled task:

```bash
pg_dump -Fc -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -f "/backups/FrogFrame_$(date +%Y%m%d_%H%M%S).dump"
```

Retention policy:
- Keep daily backups for 30 days.
- Retain monthly snapshots for 12 months.

---

## 4. Disaster Recovery & Restoration Steps

1. Stop application containers to prevent active mutations:
   ```bash
   docker compose down
   ```
2. Re-create the database:
   ```bash
   dropdb -h "$DB_HOST" -U "$DB_USER" "$DB_NAME"
   createdb -h "$DB_HOST" -U "$DB_USER" "$DB_NAME"
   ```
3. Restore from backup dump:
   ```bash
   pg_restore --clean --if-exists --no-owner --no-privileges \
     -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" \
     "/backups/<backup_file>.dump"
   ```
4. Re-grant read-only permissions for reporting:
   ```bash
   psql -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -f deploy/sql/reporting-readonly-user.sql
   ```
5. Restart the application service:
   ```bash
   docker compose up -d
   ```
6. Verify service health via `/healthz`.
