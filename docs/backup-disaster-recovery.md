# Backup & Disaster Recovery Runbook

Operational guide for backing up and restoring FrogFramework environments in production.

---

## 1. Recovery Objectives

| Metric | Target | Notes |
|---|---|---|
| **RPO** (Recovery Point Objective) | ≤ 24 hours | Achieved with daily `pg_dump`. Lower RPO requires WAL archiving / point-in-time recovery (PITR). |
| **RTO** (Recovery Time Objective) | < 30 minutes | Baseline test: 1.1s for 0.08 MB dump (development data). Validate against production volumes. |

---

## 2. Backup Scope

All persistent state in FrogFramework resides in PostgreSQL, **except file contents when `Storage:Provider = S3`**
(the docker compose default): those are in the object store (bundled SeaweedFS volume `frogframe-seaweedfs`) and must be
backed up together with the database (see `docs/guides/file-and-image-storage.md` §4). A single `pg_dump` covers:
- **Documents & Audit**: `frogframe_documents`, `frogframe_audit`
- **Uploaded Files / Images**: `frogframe_files`
- **Data Protection Keys (Session Cookies)**: `frogframe_data_protection_keys`
- **Metadata, Workflows, Menus, Roles, Users**: `frogframe_metadata_versions`, `frogframe_workflow_versions`, `frogframe_navigation_*`, `frogframe_roles`, `frogframe_users`
- **Outbox, Job History, Error Logs**: `frogframe_outbox`, `frogframe_scheduled_job_runs`, `frogframe_error_logs`

> [!CAUTION]
> Data Protection keys stored in the database are sensitive. Restrict backup file permissions and encrypt storage buckets (e.g., AWS S3 SSE, GCP Cloud Storage default encryption).

> [!NOTE]
> The `frogframe_ro` role (read-only analytical user for dashboards) is excluded when restoring with `--no-owner --no-privileges`. Re-run `deploy/sql/reporting-readonly-user.sql` after restoration.

---

## 3. Automated Backup Procedure

Execute the backup script via cron or scheduled task:

```bash
pg_dump -Fc -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -f "/backups/FrogFramework_$(date +%Y%m%d_%H%M%S).dump"
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
