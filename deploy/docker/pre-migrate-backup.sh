#!/bin/sh
# Snapshot of the FrogFrame database before migrations (docker compose service "db-backup").
# Environment: PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD, BACKUP_DIR (default /backups),
# BACKUP_KEEP (newest dumps to keep, default 5), BACKUP_ENABLED (default true).
# Exits non-zero when the dump fails, so the migrate step (and the app) do not start on an unsaved database.
set -eu

BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_KEEP="${BACKUP_KEEP:-5}"
log() { echo "[$(date +'%Y-%m-%d %H:%M:%S')] [pre-migrate backup] $*"; }

if [ "${BACKUP_ENABLED:-true}" != "true" ]; then
  log "Skipped (FROGFRAME_PRE_MIGRATE_BACKUP=${BACKUP_ENABLED:-})."
  exit 0
fi
case "$BACKUP_KEEP" in ''|*[!0-9]*) log "BACKUP_KEEP must be a number, got '$BACKUP_KEEP'."; exit 1 ;; esac

# A new database (never migrated) has nothing to save.
if ! MIGRATED=$(psql -X -At -v ON_ERROR_STOP=1 -c "SELECT to_regclass('frogframe_schema_migrations') IS NOT NULL"); then
  log "Cannot query database '$PGDATABASE' on ${PGHOST}:${PGPORT:-5432}; not migrating without a snapshot."
  exit 1
fi
if [ "$MIGRATED" != "t" ]; then
  log "New database (no frogframe_schema_migrations table): nothing to back up."
  exit 0
fi

mkdir -p "$BACKUP_DIR"
STAMP=$(date +%Y%m%d_%H%M%S)
FILE="$BACKUP_DIR/pre-migrate_${PGDATABASE}_${STAMP}.dump"
TMP="$FILE.partial"
# shellcheck disable=SC2154 # rc is assigned inside the trap
trap 'rc=$?; rm -f "$TMP"; [ $rc -ne 0 ] && log "FAILED (exit $rc); no partial file left."; exit $rc' EXIT

log "Dumping '$PGDATABASE' on ${PGHOST}:${PGPORT:-5432}..."
pg_dump -Fc -f "$TMP"
pg_restore --list "$TMP" > /dev/null
mv "$TMP" "$FILE"
log "Saved $FILE ($(du -h "$FILE" | cut -f1))."

# Keep the newest BACKUP_KEEP pre-migrate dumps of this database.
# shellcheck disable=SC2012 # file names are created by this script
ls -1t "$BACKUP_DIR"/pre-migrate_"${PGDATABASE}"_*.dump 2>/dev/null | tail -n +"$((BACKUP_KEEP + 1))" | while read -r old; do
  rm -f "$old" && log "Removed old snapshot $old"
done
