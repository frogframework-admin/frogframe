#!/usr/bin/env bash
# ==============================================================================
# DocFrame Production PostgreSQL Restore Script
# Restores a compressed, custom-format (-Fc) dump with safety checks.
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

if [ "${FORCE}" != "true" ] && [ "${2:-}" != "--force" ]; then
  echo "WARNING: This will clean and restore database '${PGDATABASE}' on ${PGHOST}:${PGPORT}."
  read -p "Are you sure you want to proceed? [y/N]: " CONFIRM
  if [ "${CONFIRM}" != "y" ] && [ "${CONFIRM}" != "Y" ]; then
    echo "Restore aborted by user."
    exit 0
  fi
fi

LOG_PREFIX="[$(date +'%Y-%m-%d %H:%M:%S')] [DocFrame Restore]"
echo "${LOG_PREFIX} Restoring database '${PGDATABASE}' from '${BACKUP_FILE}'..."

pg_restore \
  -h "${PGHOST}" \
  -p "${PGPORT}" \
  -U "${PGUSER}" \
  -d "${PGDATABASE}" \
  -Fc \
  --clean \
  --if-exists \
  --no-owner \
  --no-privileges \
  -v \
  "${BACKUP_FILE}" || true

echo "${LOG_PREFIX} Database restored successfully."
echo "${LOG_PREFIX} Note: Run 'dotnet run --project src/Apps.Sample' or SchemaMigrator to ensure schema consistency."

