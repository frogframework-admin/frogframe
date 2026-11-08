#!/usr/bin/env bash
# ==============================================================================
# FrogFrame Production PostgreSQL Backup Script
# Creates a compressed, custom-format (-Fc) dump with automated retention.
# ==============================================================================
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/var/backups/FrogFrame}"
PGHOST="${PGHOST:-localhost}"
PGPORT="${PGPORT:-5432}"
PGDATABASE="${PGDATABASE:-FrogFrame}"
PGUSER="${PGUSER:-FrogFrame}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"

mkdir -p "${BACKUP_DIR}"

TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="${BACKUP_DIR}/${PGDATABASE}_${TIMESTAMP}.dump"
LOG_PREFIX="[$(date +'%Y-%m-%d %H:%M:%S')] [FrogFrame Backup]"

echo "${LOG_PREFIX} Starting backup of '${PGDATABASE}' from ${PGHOST}:${PGPORT}..."

# Never leave a partial/corrupt file that looks like a valid backup.
trap 'rc=$?; if [ $rc -ne 0 ]; then rm -f "${BACKUP_FILE}"; echo "${LOG_PREFIX} ERROR: backup failed (exit $rc), partial file removed." >&2; fi' EXIT

# Execute pg_dump with custom format (-Fc), verbose, and compression
pg_dump \
  -h "${PGHOST}" \
  -p "${PGPORT}" \
  -U "${PGUSER}" \
  -d "${PGDATABASE}" \
  -Fc \
  -v \
  -f "${BACKUP_FILE}"

# Verify integrity of backup archive
echo "${LOG_PREFIX} Verifying archive integrity with pg_restore --list..."
pg_restore --list "${BACKUP_FILE}" > /dev/null
FILESIZE=$(ls -lh "${BACKUP_FILE}" | awk '{print $5}')
echo "${LOG_PREFIX} Backup successful: ${BACKUP_FILE} (${FILESIZE})"

# Prune older backups according to retention policy
echo "${LOG_PREFIX} Cleaning up backups older than ${RETENTION_DAYS} days..."
find "${BACKUP_DIR}" -type f -name "${PGDATABASE}_*.dump" -mtime +"${RETENTION_DAYS}" -exec rm -f {} +
echo "${LOG_PREFIX} Backup rotation complete."

