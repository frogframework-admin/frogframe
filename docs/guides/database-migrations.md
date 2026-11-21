# Database Migrations

The PostgreSQL schema is created and updated by numbered migrations (`src/Framework.Persistence.PostgreSql/Migrations/NNN_name.sql`, plus migrations registered by applications). Each migration runs in its own transaction and is recorded in `frogframe_schema_migrations`. A PostgreSQL advisory lock makes sure only one process applies migrations at a time.

## Two ways to run them

| Setting | Behaviour | Use for |
|---|---|---|
| `Database:MigrateOnStartup=true` (default) | the application applies pending migrations when it starts | `dotnet run`, a single instance |
| `Database:MigrateOnStartup=false` | the application does not touch the schema; it waits until a separate migrate step has applied every migration, showing the wait on the startup page, and fails after `Database:WaitForMigrationsSeconds` (default 600) | Docker Compose (default there), several instances, rolling updates |

Running migrations as their own step means a deployment migrates once, before any new instance starts, and a failed migration stops the deployment instead of leaving instances half started.

## Command line

The application binary has two migration commands. Neither starts the web server.

```bash
dotnet Apps.Erp.dll --migration-status   # lists pending migrations; exit code 0 = up to date, 2 = pending
dotnet Apps.Erp.dll --migrate-only       # applies pending migrations; exit code 0 = up to date, 1 = failed
```

Both use the normal configuration (`Persistence:Provider=PostgreSql`, `ConnectionStrings:FrogFrame`, or `ConnectionStrings:FrogFrameSchema` when the schema owner is a separate role; after migrating, `--migrate-only` also creates or updates the runtime role `Database:AppRole` and its grants, see [Database roles](database-roles.md)), and the same startup checks as the application. While PostgreSQL is still starting they wait for it; a configuration error such as a wrong password or an unknown database fails at once.

## Docker Compose

`docker-compose.yml` starts the services in this order:

1. `postgres`
2. `db-backup`: snapshot before migrating (below)
3. `migrate`: same image and settings as the app, runs `--migrate-only` and exits
4. `app`: `Database__MigrateOnStartup=false`, starts only after `migrate` succeeded

```bash
docker compose up -d --build          # deploy: backup, migrate, then start the app
docker compose run --rm migrate       # migrate by hand
docker compose run --rm migrate --migration-status
docker compose logs db-backup migrate # what the last deployment did
```

If `db-backup` or `migrate` fails, the app is not started; the previous containers keep running when you deploy with `docker compose up -d`.

### Snapshot before migrating

On every `docker compose up` of an existing database, `db-backup` writes a `pg_dump` custom-format file to the `frogframe-backups` volume (`pre-migrate_frogframe_<timestamp>.dump`), checks it with `pg_restore --list`, and keeps the newest `FROGFRAME_PRE_MIGRATE_BACKUP_KEEP` files (default 5). A new, never-migrated database is skipped. When the dump or the connection fails, the service exits with an error and nothing is migrated.

| `.env` | Default | |
|---|---|---|
| `FROGFRAME_PRE_MIGRATE_BACKUP` | `true` | `false` skips the snapshot (for example on a very large database with its own backup before deployments) |
| `FROGFRAME_PRE_MIGRATE_BACKUP_KEEP` | `5` | number of snapshots kept |

The snapshot is for rolling back a deployment. It does not replace scheduled backups and off-site copies (see [Backup & disaster recovery](../backup-disaster-recovery.md)). To restore one:

```bash
docker compose stop app
# Replace the database with the snapshot (the frogframe user is the superuser of the bundled PostgreSQL).
docker compose run --rm --no-deps --entrypoint sh db-backup -c '
  psql -X -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE frogframe WITH (FORCE)" -c "CREATE DATABASE frogframe" &&
  pg_restore --exit-on-error -d frogframe /backups/pre-migrate_frogframe_<timestamp>.dump'
# then start the previous application version
```

Restore into an empty database as above rather than with `pg_restore --clean`: `--clean` leaves tables that a newer migration created, which would make the next migrate step fail.

## Kubernetes and other orchestrators

Run `--migrate-only` as a pre-deployment job (for example a Helm `pre-upgrade` hook or a Job that the pipeline waits for) with the same image and configuration, and set `Database__MigrateOnStartup=false` on the application. Take a backup before the job (`deploy/scripts/backup-db.sh`). Do not use an init container per pod: every pod would try to migrate; the lock keeps that safe but slows down every start.

## Writing migrations for rolling updates

During a rolling update the previous version keeps serving requests on the new schema until it is replaced. Make each migration work with both versions:

- add tables, nullable columns and indexes; do not drop or rename in the same release that stops using them;
- remove a column or table in a later release, once no running version reads it;
- create indexes on large tables with `CREATE INDEX CONCURRENTLY` outside a migration (metadata indexes are already built in the background by the application).
