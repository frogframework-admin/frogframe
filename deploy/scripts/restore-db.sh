#!/usr/bin/env bash
# ==============================================================================
# DocFrame Production PostgreSQL Restore Script
# Restores a compressed, custom-format (-Fc) dump with safety checks.
# Exits non-zero if pg_restore reports any error.
# ==============================================================================
set -euo pipefail

BACKUP_FILE="${1:-}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGDATABASE="${PGDATABASE:-docframe}"
PGUSER="${PGUSER:-docframe}"
FORCE="${FORCE:-false}"

if [ -z "${BACKUP_FILE}" ]; then
  echo "Usage: $0 <path-to-dump-file> [--force]"
  exit 1
fi

if [ ! -f "${BACKUP_FILE}" ]; then
  echo "Error: Backup file '${BACKUP_FILE}' does not exist."
  exit 1
fi

# Validate the archive before touching the target database.
if ! pg_restore --list "${BACKUP_FILE}" > /dev/null; then
  echo "Error: '${BACKUP_FILE}' is not a valid pg_dump custom-format archive."
  exit 1
fi

if [ "${FORCE}" != "true" ] && [ "${2:-}" != "--force" ]; then
  echo "WARNING: This will drop and recreate objects in database '${PGDATABASE}' on ${PGHOST}:${PGPORT}."
  read -r -p "Are you sure you want to proceed? [y/N]: " CONFIRM
  if [ "${CONFIRM}" != "y" ] && [ "${CONFIRM}" != "Y" ]; then
    echo "Restore aborted by user."
    exit 0
  fi
fi

LOG_PREFIX="[$(date +'%Y-%m-%d %H:%M:%S')] [DocFrame Restore]"
echo "${LOG_PREFIX} Restoring database '${PGDATABASE}' from '${BACKUP_FILE}'..."

# --single-transaction: all-or-nothing; a failure leaves the database unchanged.
# --exit-on-error: stop at the first error instead of continuing silently.
if ! pg_restore \
  -h "${PGHOST}" \
  -p "${PGPORT}" \
  -U "${PGUSER}" \
  -d "${PGDATABASE}" \
  --clean \
  --if-exists \
  --no-owner \
  --no-privileges \
  --single-transaction \
  --exit-on-error \
  "${BACKUP_FILE}"; then
  echo "${LOG_PREFIX} ERROR: pg_restore failed. The transaction was rolled back; the database was not changed." >&2
  exit 1
fi

echo "${LOG_PREFIX} Database restored successfully."
echo "${LOG_PREFIX} Next: re-apply deploy/sql/reporting-readonly-user.sql and start the app so SchemaMigrator runs."
