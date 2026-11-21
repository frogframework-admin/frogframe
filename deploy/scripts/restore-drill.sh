#!/bin/sh
# ==============================================================================
# FrogFrame backup & restore drill
#
# Proves that a backup can be restored: restores a dump into a throwaway database, compares every table's row count
# and content checksum with the source, and measures the restore time against the RTO target. The source database is
# only read; the throwaway database is dropped at the end.
#
#   restore-drill.sh                 dump the source now (consistent snapshot) and verify that dump exactly
#   restore-drill.sh --dump FILE     verify an existing backup file (e.g. last night's): restore, tables, migrations
#
# Environment (libpq): PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE = the source database. The user needs CREATEDB.
#   DRILL_DB            throwaway database name (default <PGDATABASE>_restore_drill_<timestamp>); must not exist
#   KEEP_DRILL_DB       true = keep the throwaway database for inspection (default false)
#   JOBS                parallel pg_restore jobs (default 2)
#   RTO_TARGET_SECONDS  fail when restore + verification takes longer (default 1800 = 30 minutes)
#   WORK_DIR            where the drill dump is written (default a temporary folder, removed at the end)
#   REPORT_FILE         also write the summary as JSON to this file
#
# Exit code 0 = drill passed, 1 = failed. POSIX sh: runs in postgres:*-alpine (busybox) and on Linux/macOS hosts.
# pg_dump/pg_restore must be the same major version as the server or newer. See docs/backup-disaster-recovery.md.
# ==============================================================================
set -eu

DUMP_FILE=""
case "${1:-}" in
  --dump) DUMP_FILE="${2:?Usage: restore-drill.sh [--dump FILE]}" ;;
  "") ;;
  -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
  *) echo "Usage: restore-drill.sh [--dump FILE]" >&2; exit 1 ;;
esac

: "${PGDATABASE:?Set PGDATABASE (and PGHOST, PGPORT, PGUSER, PGPASSWORD) to the source database}"
SOURCE_DB="$PGDATABASE"
STAMP=$(date +%Y%m%d_%H%M%S)
DRILL_DB="${DRILL_DB:-${SOURCE_DB}_restore_drill_${STAMP}}"
KEEP_DRILL_DB="${KEEP_DRILL_DB:-false}"
JOBS="${JOBS:-2}"
RTO_TARGET_SECONDS="${RTO_TARGET_SECONDS:-1800}"
REPORT_FILE="${REPORT_FILE:-}"
case "$JOBS$RTO_TARGET_SECONDS" in *[!0-9]*) echo "JOBS and RTO_TARGET_SECONDS must be numbers." >&2; exit 1 ;; esac
case "$DRILL_DB" in *[!A-Za-z0-9_]*) echo "DRILL_DB may only contain letters, digits and '_'." >&2; exit 1 ;; esac
[ "$DRILL_DB" != "$SOURCE_DB" ] || { echo "DRILL_DB must differ from the source database." >&2; exit 1; }

OWN_WORK=false
if [ -z "${WORK_DIR:-}" ]; then WORK_DIR=$(mktemp -d); OWN_WORK=true; fi
mkdir -p "$WORK_DIR"
log() { echo "[$(date +'%Y-%m-%d %H:%M:%S')] [restore drill] $*"; }
now() { date +%s; }
psql_at() { psql -X -q -At -v ON_ERROR_STOP=1 "$@"; }

SNAP_PID=""
DRILL_CREATED=false
cleanup() {
  rc=$?
  if [ -n "$SNAP_PID" ]; then exec 3>&- 2>/dev/null || true; kill "$SNAP_PID" 2>/dev/null || true; fi
  if [ "$DRILL_CREATED" = true ] && [ "$KEEP_DRILL_DB" != true ]; then
    psql_at -d postgres -c "DROP DATABASE IF EXISTS \"$DRILL_DB\" WITH (FORCE)" >/dev/null 2>&1 \
      || psql_at -d postgres -c "DROP DATABASE IF EXISTS \"$DRILL_DB\"" >/dev/null 2>&1 \
      || log "WARNING: could not drop throwaway database $DRILL_DB; drop it by hand."
  fi
  if [ "$OWN_WORK" = true ]; then rm -rf "$WORK_DIR"; fi
  exit $rc
}
trap cleanup EXIT
trap 'exit 1' INT TERM

# Per-table row count and an order-independent checksum of the rows' text (sum of 64-bit row hashes), for every table
# outside the system schemas. Read-only: works inside the exported snapshot's read-only transaction.
FINGERPRINT_SQL="SELECT n.nspname || '.' || c.relname
  || '|' || (xpath('/row/c/text()', x))[1]::text || '|' || (xpath('/row/h/text()', x))[1]::text
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
CROSS JOIN LATERAL query_to_xml(format(
  'SELECT count(*) AS c, coalesce(sum(hashtextextended(t::text, 0)), 0) AS h FROM %I.%I t', n.nspname, c.relname),
  false, true, '') AS x
WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
  AND n.nspname NOT LIKE 'pg_temp%'
ORDER BY 1;"
MIGRATION_SQL="SELECT CASE WHEN to_regclass('frogframe_schema_migrations') IS NULL THEN 'none'
  ELSE (SELECT coalesce(max(version)::text, 'none') || ' (' || count(*) || ' applied)' FROM frogframe_schema_migrations) END;"

START=$(now)
MODE=exact
if [ -z "$DUMP_FILE" ]; then
  # 1. Fresh dump. A read-only REPEATABLE READ transaction exports its snapshot; pg_dump and the source fingerprint
  #    both read that snapshot, so they see exactly the same data while the application keeps writing.
  DUMP_FILE="$WORK_DIR/${SOURCE_DB}_drill_${STAMP}.dump"
  mkfifo "$WORK_DIR/snap.in" "$WORK_DIR/snap.out"
  psql -X -q -At -v ON_ERROR_STOP=1 <"$WORK_DIR/snap.in" >"$WORK_DIR/snap.out" 2>"$WORK_DIR/snap.err" &
  SNAP_PID=$!
  exec 3>"$WORK_DIR/snap.in" 4<"$WORK_DIR/snap.out"
  echo "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY; SELECT pg_export_snapshot();" >&3
  if ! read -r SNAPSHOT <&4 || [ -z "$SNAPSHOT" ]; then
    log "FAILED: cannot open a snapshot on '$SOURCE_DB': $(cat "$WORK_DIR/snap.err")"; exit 1
  fi
  log "Dumping '$SOURCE_DB' on ${PGHOST:-local}:${PGPORT:-5432} (snapshot $SNAPSHOT)..."
  T=$(now)
  pg_dump -Fc --snapshot="$SNAPSHOT" -f "$DUMP_FILE"
  DUMP_SECONDS=$(( $(now) - T ))
  log "Fingerprinting the source in the same snapshot..."
  printf '%s\n%s\nSELECT '"'"'__END__'"'"';\n' "$FINGERPRINT_SQL" "$MIGRATION_SQL" >&3
  : >"$WORK_DIR/source.txt"
  while read -r line <&4; do
    [ "$line" = "__END__" ] && break
    echo "$line" >>"$WORK_DIR/source.txt"
  done
  echo "COMMIT;" >&3
  exec 3>&- 4<&-
  wait "$SNAP_PID" || { log "FAILED: source fingerprint: $(cat "$WORK_DIR/snap.err")"; SNAP_PID=""; exit 1; }
  SNAP_PID=""
  SOURCE_MIGRATIONS=$(tail -n 1 "$WORK_DIR/source.txt")
  sed '$d' "$WORK_DIR/source.txt" >"$WORK_DIR/source.tables"
else
  # 1. Existing backup. The source has changed since, so contents are not compared; the check is that the backup
  #    restores completely and holds every table its archive lists.
  MODE=existing
  [ -f "$DUMP_FILE" ] || { log "FAILED: backup file '$DUMP_FILE' not found."; exit 1; }
  DUMP_SECONDS=0
  SOURCE_MIGRATIONS=$(psql_at -c "$MIGRATION_SQL" 2>/dev/null || echo "unknown")
fi

log "Checking the archive..."
pg_restore --list "$DUMP_FILE" >"$WORK_DIR/toc.txt" || { log "FAILED: '$DUMP_FILE' is not a readable pg_dump custom-format archive."; exit 1; }
DUMP_BYTES=$(wc -c <"$DUMP_FILE" | tr -d ' ')

# 2. Restore into the throwaway database: this is the timed recovery.
log "Restoring into throwaway database '$DRILL_DB' ($JOBS jobs)..."
RESTORE_START=$(now)
psql_at -d postgres -c "CREATE DATABASE \"$DRILL_DB\"" >/dev/null
DRILL_CREATED=true
if ! pg_restore --exit-on-error --no-owner --no-privileges -j "$JOBS" -d "$DRILL_DB" "$DUMP_FILE" 2>"$WORK_DIR/restore.err"; then
  log "FAILED: pg_restore: $(tail -n 5 "$WORK_DIR/restore.err")"; exit 1
fi
RESTORE_SECONDS=$(( $(now) - RESTORE_START ))

# 3. Verify.
log "Verifying..."
psql_at -d "$DRILL_DB" -c "$FINGERPRINT_SQL" >"$WORK_DIR/restored.tables"
RESTORED_MIGRATIONS=$(psql_at -d "$DRILL_DB" -c "$MIGRATION_SQL")
FAILURES=0
fail() { log "MISMATCH: $*"; FAILURES=$((FAILURES + 1)); }

TABLES=$(wc -l <"$WORK_DIR/restored.tables" | tr -d ' ')
[ "$TABLES" -gt 0 ] || fail "the restored database has no tables"
ROWS=$(awk -F'|' '{ s += $2 } END { print s + 0 }' "$WORK_DIR/restored.tables")

if [ "$MODE" = exact ]; then
  # Same tables, same row counts, same checksums.
  if ! diff "$WORK_DIR/source.tables" "$WORK_DIR/restored.tables" >"$WORK_DIR/diff.txt"; then
    awk -F'|' 'NR == FNR { src[$1] = $2 "|" $3; next } { dst[$1] = $2 "|" $3 }
      END {
        for (t in src) { if (!(t in dst)) print "table " t " missing after restore";
          else if (src[t] != dst[t]) { split(src[t], a, "|"); split(dst[t], b, "|");
            print "table " t ": source " a[1] " rows, restored " b[1] " rows" (a[1] == b[1] ? " (contents differ)" : "") } }
        for (t in dst) if (!(t in src)) print "table " t " only in the restored database" }' \
      "$WORK_DIR/source.tables" "$WORK_DIR/restored.tables" | sort >"$WORK_DIR/mismatch.txt"
    while read -r m; do fail "$m"; done <"$WORK_DIR/mismatch.txt"
  fi
  [ "$SOURCE_MIGRATIONS" = "$RESTORED_MIGRATIONS" ] || fail "migrations: source $SOURCE_MIGRATIONS, restored $RESTORED_MIGRATIONS"
else
  # Every table whose data the archive lists exists after the restore.
  # TOC line: "<id>; <oid> <oid> TABLE DATA <schema> <table name, may contain spaces> <owner>"
  awk '/^[0-9]+; [0-9]+ [0-9]+ TABLE DATA / { name = $7; for (i = 8; i < NF; i++) name = name " " $i; print $6 "." name }' \
    "$WORK_DIR/toc.txt" | sort -u >"$WORK_DIR/toc.tables"
  cut -d'|' -f1 "$WORK_DIR/restored.tables" | sort -u >"$WORK_DIR/restored.names"
  comm -23 "$WORK_DIR/toc.tables" "$WORK_DIR/restored.names" >"$WORK_DIR/missing.txt"
  while read -r t; do fail "table $t is in the archive but missing after restore"; done <"$WORK_DIR/missing.txt"
  [ "$RESTORED_MIGRATIONS" != none ] || log "NOTE: the backup has no frogframe_schema_migrations table (not a FrogFrame database?)"
fi

VERIFY_SECONDS=$(( $(now) - RESTORE_START - RESTORE_SECONDS ))
RTO_SECONDS=$(( RESTORE_SECONDS + VERIFY_SECONDS ))
TOTAL_SECONDS=$(( $(now) - START ))
[ "$RTO_SECONDS" -le "$RTO_TARGET_SECONDS" ] || fail "restore + verification took ${RTO_SECONDS}s, RTO target ${RTO_TARGET_SECONDS}s"

RESULT=PASSED; [ "$FAILURES" -eq 0 ] || RESULT=FAILED
echo
echo "Restore drill $RESULT"
echo "  Mode:              $([ "$MODE" = exact ] && echo "fresh dump, contents compared" || echo "existing backup, restore and tables checked")"
echo "  Source:            $SOURCE_DB on ${PGHOST:-local}:${PGPORT:-5432}"
echo "  Dump:              $DUMP_FILE ($DUMP_BYTES bytes)"
echo "  Tables restored:   $TABLES ($ROWS rows)"
echo "  Migrations:        source $SOURCE_MIGRATIONS, restored $RESTORED_MIGRATIONS"
echo "  Dump time:         ${DUMP_SECONDS}s"
echo "  Restore time:      ${RESTORE_SECONDS}s"
echo "  Verification time: ${VERIFY_SECONDS}s"
echo "  RTO (restore+verify): ${RTO_SECONDS}s of ${RTO_TARGET_SECONDS}s target"
echo "  Total:             ${TOTAL_SECONDS}s"
echo "  Mismatches:        $FAILURES"
[ "$KEEP_DRILL_DB" = true ] && echo "  Kept database:     $DRILL_DB"

if [ -n "$REPORT_FILE" ]; then
  cat >"$REPORT_FILE" <<JSON
{"result":"$RESULT","mode":"$MODE","source":"$SOURCE_DB","drillDatabase":"$DRILL_DB","dumpBytes":$DUMP_BYTES,"tables":$TABLES,"rows":$ROWS,"sourceMigrations":"$SOURCE_MIGRATIONS","restoredMigrations":"$RESTORED_MIGRATIONS","dumpSeconds":$DUMP_SECONDS,"restoreSeconds":$RESTORE_SECONDS,"verifySeconds":$VERIFY_SECONDS,"rtoSeconds":$RTO_SECONDS,"rtoTargetSeconds":$RTO_TARGET_SECONDS,"mismatches":$FAILURES,"finishedAt":"$(date -u +%Y-%m-%dT%H:%M:%SZ)"}
JSON
fi
[ "$FAILURES" -eq 0 ]
