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

In Docker Compose, every deployment also takes a verified snapshot before migrating (service `db-backup`, volume `frogframe-backups`, newest 5 kept). It is meant for rolling back a deployment and does not replace the scheduled backups above. See [Database migrations](guides/database-migrations.md).

### Restore drill

A backup only counts once it has been restored. `deploy/scripts/restore-drill.sh` proves it automatically. It restores into a throwaway database, verifies the result, measures the time, and drops the throwaway database again. The source is only read, and exit code 0 means the drill passed.

| Mode | Command | What is checked |
|---|---|---|
| Fresh dump (default) | `restore-drill.sh` | Dumps the source from a consistent snapshot and fingerprints the source **in the same snapshot**. Then every table's row count and content checksum must match after the restore, and so must the applied migrations. This stays exact while the application keeps writing. |
| Existing backup | `restore-drill.sh --dump FILE` | Verifies a backup taken earlier (e.g. last night's): it must restore without errors, and every table the archive lists must exist. Contents are not compared, because the source has changed since. |

Both modes fail when restore plus verification takes longer than `RTO_TARGET_SECONDS` (default 1800, the RTO above).

With Docker Compose the script is mounted in the `db-backup` service, which has the matching PostgreSQL client and the connection settings. This works the same from PowerShell on Windows:

```bash
docker compose run --rm --no-deps --entrypoint sh db-backup /opt/frogframe/restore-drill.sh
docker compose run --rm --no-deps --entrypoint sh db-backup /opt/frogframe/restore-drill.sh \
  --dump /backups/pre-migrate_frogframe_<timestamp>.dump
```

On a host with the PostgreSQL client (same major version as the server or newer):

```bash
PGHOST=db.internal PGUSER=frogframe PGDATABASE=frogframe PGPASSWORD=... deploy/scripts/restore-drill.sh
```

Options are set as environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `RTO_TARGET_SECONDS` | `1800` | Fail when restore plus verification takes longer than this. |
| `JOBS` | `2` | Parallel `pg_restore` jobs. |
| `KEEP_DRILL_DB` | `false` | Set `true` to keep the throwaway database for inspection. |
| `DRILL_DB` | `<db>_restore_drill_<timestamp>` | Name of the throwaway database. |
| `WORK_DIR` | a temporary folder | Where the fresh dump is written. |
| `REPORT_FILE` | (none) | Also write the summary as JSON to this file, e.g. for monitoring. |

The user needs `CREATEDB` (the bundled `frogframe` user has it). Run the drill monthly and after major upgrades, and keep the reports. For production-sized data, run it on a separate instance restored from the backup storage, not on the production server. The drill covers PostgreSQL only: with `Storage:Provider=S3`, check the object-store backup separately.

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
