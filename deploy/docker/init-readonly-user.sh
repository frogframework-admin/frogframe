#!/bin/sh
# Run by the postgres image when the database is first created (docker-entrypoint-initdb.d).
# Creates the read-only user for SQL Dashboards from deploy/sql/reporting-readonly-user.sql,
# then replaces its built-in password with FROGFRAME_RO_PASSWORD.
# Note: the postgres entrypoint may "source" this script, so it avoids "set -e" and "exit 0".

: "${FROGFRAME_RO_PASSWORD:?FROGFRAME_RO_PASSWORD is required}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -f /opt/frogframe/reporting-readonly-user.sql || exit 1

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v ro_password="$FROGFRAME_RO_PASSWORD" <<'EOSQL' || exit 1
ALTER ROLE frogframe_ro PASSWORD :'ro_password';
EOSQL
