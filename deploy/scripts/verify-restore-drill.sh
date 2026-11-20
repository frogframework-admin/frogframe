#!/usr/bin/env bash
# FrogFrame Automated Backup & Restore Verification Drill (Bash)
# Executes a snapshot restore into a throwaway database, verifies migrations,
# table row counts, and core content checksums, measures RTO, and cleans up.
set -euo pipefail

BACKUP_FILE="${1:-}"
PGHOST="${PGHOST:-127.0.0.1}"
PGPORT="${PGPORT:-5432}"
PGDATABASE="${PGDATABASE:-frogframe}"
PGUSER="${PGUSER:-frogframe}"
CONTAINER="${FROGFRAME_PG_CONTAINER:-frogframe-postgres}"

if [ -z "${PGPASSWORD:-}" ] && [ -f ".env" ]; then
  PGPASSWORD="$(grep -E '^POSTGRES_PASSWORD=' .env | head -n1 | cut -d= -f2- | tr -d '"' | tr -d "'")"
  export PGPASSWORD
fi

if [ -z "${PGPASSWORD:-}" ]; then
  echo "ERROR: Set PGPASSWORD or define POSTGRES_PASSWORD in .env." >&2
  exit 1
fi

USE_DOCKER=0
if ! command -v pg_dump >/dev/null 2>&1 || ! command -v psql >/dev/null 2>&1; then
  USE_DOCKER=1
elif ! psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -c "SELECT 1" >/dev/null 2>&1; then
  USE_DOCKER=1
fi

run_psql() {
  local target_db="$1"
  local sql="$2"
  if [ "$USE_DOCKER" -eq 0 ]; then
    psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$target_db" -t -A -v ON_ERROR_STOP=1 -c "$sql"
  else
    docker exec -e PGPASSWORD="$PGPASSWORD" "$CONTAINER" psql -U "$PGUSER" -d "$target_db" -t -A -v ON_ERROR_STOP=1 -c "$sql"
  fi
}

DRILL_DB="frogframe_drill_$(date +%Y%m%d_%H%M%S)"
CREATED_DUMP=0
DRILL_CREATED=0

cleanup() {
  echo "[Drill 5/5] Cleaning up throwaway database and temp files..."
  if [ "$DRILL_CREATED" -eq 1 ]; then
    run_psql "postgres" "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DRILL_DB' AND pid <> pg_backend_pid();" >/dev/null 2>&1 || true
    run_psql "postgres" "DROP DATABASE IF EXISTS \"$DRILL_DB\";" >/dev/null 2>&1 || true
  fi
  if [ "$CREATED_DUMP" -eq 1 ] && [ -n "$BACKUP_FILE" ] && [ -f "$BACKUP_FILE" ]; then
    rm -f "$BACKUP_FILE"
  fi
}
trap cleanup EXIT

if [ -z "$BACKUP_FILE" ]; then
  BACKUP_FILE="/tmp/${DRILL_DB}.dump"
  CREATED_DUMP=1
  echo "[Drill 1/5] Creating snapshot of '$PGDATABASE'..."
  if [ "$USE_DOCKER" -eq 0 ]; then
    pg_dump -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" -Fc -f "$BACKUP_FILE"
  else
    docker exec -e PGPASSWORD="$PGPASSWORD" "$CONTAINER" pg_dump -U "$PGUSER" -d "$PGDATABASE" -Fc -f /tmp/drill.dump
    docker cp "${CONTAINER}:/tmp/drill.dump" "$BACKUP_FILE"
    docker exec "$CONTAINER" rm -f /tmp/drill.dump >/dev/null
  fi
fi

echo "[Drill 2/5] Creating throwaway database '$DRILL_DB'..."
run_psql "postgres" "CREATE DATABASE \"$DRILL_DB\" OWNER \"$PGUSER\";" >/dev/null
DRILL_CREATED=1

echo "[Drill 3/5] Restoring snapshot into '$DRILL_DB' and measuring RTO..."
START_TS=$(date +%s)
if [ "$USE_DOCKER" -eq 0 ]; then
  pg_restore -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$DRILL_DB" --no-owner --no-privileges --single-transaction --exit-on-error "$BACKUP_FILE"
else
  docker cp "$BACKUP_FILE" "${CONTAINER}:/tmp/drill_restore.dump"
  docker exec -e PGPASSWORD="$PGPASSWORD" "$CONTAINER" pg_restore -U "$PGUSER" -d "$DRILL_DB" --no-owner --no-privileges --single-transaction --exit-on-error /tmp/drill_restore.dump
  docker exec "$CONTAINER" rm -f /tmp/drill_restore.dump >/dev/null
fi
END_TS=$(date +%s)
RTO_SECONDS=$((END_TS - START_TS))

echo "[Drill 4/5] Verifying migrations, table row counts, and content checksums..."
SRC_MIG="$(run_psql "$PGDATABASE" "SELECT count(*) || ':' || coalesce(max(version), 0) FROM frogframe_schema_migrations;")"
DST_MIG="$(run_psql "$DRILL_DB" "SELECT count(*) || ':' || coalesce(max(version), 0) FROM frogframe_schema_migrations;")"
if [ "$SRC_MIG" != "$DST_MIG" ]; then
  echo "ERROR: Migration mismatch: source=$SRC_MIG vs restored=$DST_MIG" >&2
  exit 1
fi

SRC_TABLES="$(run_psql "$PGDATABASE" "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;")"
DST_TABLES="$(run_psql "$DRILL_DB" "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;")"
if [ "$SRC_TABLES" != "$DST_TABLES" ]; then
  echo "ERROR: Table list mismatch between source and restored database." >&2
  exit 1
fi

TABLE_COUNT=0
for tbl in $SRC_TABLES; do
  TABLE_COUNT=$((TABLE_COUNT + 1))
  SRC_CNT="$(run_psql "$PGDATABASE" "SELECT count(*) FROM public.\"$tbl\";")"
  DST_CNT="$(run_psql "$DRILL_DB" "SELECT count(*) FROM public.\"$tbl\";")"
  case "$tbl" in
    frogframe_scheduled_job_runs|frogframe_outbox|frogframe_error_logs)
      if [ "$DST_CNT" -gt "$SRC_CNT" ]; then
        echo "ERROR: Row count mismatch on live table '$tbl': restored ($DST_CNT) > source ($SRC_CNT)" >&2
        exit 1
      fi
      ;;
    *)
      if [ "$SRC_CNT" != "$DST_CNT" ]; then
        echo "ERROR: Row count mismatch on '$tbl': source=$SRC_CNT vs restored=$DST_CNT" >&2
        exit 1
      fi
      ;;
  esac
done

for tbl in frogframe_documents frogframe_audit frogframe_users frogframe_roles frogframe_metadata_versions; do
  if echo "$SRC_TABLES" | grep -qx "$tbl"; then
    SRC_HASH="$(run_psql "$PGDATABASE" "SELECT coalesce(sum(hashtext(t::text)::bigint), 0) FROM public.\"$tbl\" AS t;")"
    DST_HASH="$(run_psql "$DRILL_DB" "SELECT coalesce(sum(hashtext(t::text)::bigint), 0) FROM public.\"$tbl\" AS t;")"
    if [ "$SRC_HASH" != "$DST_HASH" ]; then
      echo "ERROR: Checksum mismatch on '$tbl': source=$SRC_HASH vs restored=$DST_HASH" >&2
      exit 1
    fi
  fi
done

echo ""
echo "=== RESTORE VERIFICATION DRILL PASSED ==="
echo "  Source Database : $PGDATABASE"
echo "  Measured RTO    : ${RTO_SECONDS}s (target: < 1800s)"
echo "  Migrations      : $DST_MIG"
echo "  Tables Verified : $TABLE_COUNT (row counts & core checksums matched 100%)"
