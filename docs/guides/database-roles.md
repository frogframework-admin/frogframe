# Database Roles

FrogFrame connects to PostgreSQL with two roles:

| Role | Connection | Used for | Can |
|---|---|---|---|
| `frogframe` (owner) | `ConnectionStrings:FrogFrameSchema` | migrations, document indexes, reporting views | everything; owns the tables |
| `frogframe_app` (runtime) | `ConnectionStrings:FrogFrame` | every request, background job and scheduled task | `SELECT`, `INSERT`, `UPDATE`, `DELETE` on tables; sequences; temporary tables |

`frogframe_ro` (SQL dashboards, read only) is unchanged; see `reporting-readonly-user.sql`.

The runtime role **cannot**:
- create, alter or drop tables, indexes, views or functions;
- disable triggers;
- `TRUNCATE`;
- `COPY … TO/FROM PROGRAM` or read server files (it is not a superuser);
- create roles or databases;
- change or delete general-ledger rows (`frogframe_gl_entries`). It can only append them. This is a second safeguard next to the append-only trigger.

So a mistake or an injection in a query, a server script or a report runs with data rights only. It cannot change the schema, wipe a table with `TRUNCATE`, alter the ledger or run programs on the database server.

## Docker Compose

This is set up out of the box. Add the password to `.env`:

```bash
FROGFRAME_APP_PASSWORD=<long random value>   # e.g. openssl rand -hex 24
```

On `docker compose up`, the `migrate` step:
1. applies migrations as `frogframe`;
2. creates `frogframe_app` if it does not exist, or updates its password to `FROGFRAME_APP_PASSWORD`;
3. grants it data access on every table and sequence, including tables created by later migrations (default privileges);
4. revokes `UPDATE`, `DELETE` and `TRUNCATE` on the ledger.

The app then connects as `frogframe_app`. It also receives the owner connection, but uses it only for schema work: building document indexes and reporting views when metadata changes.

The settings, all in `docker-compose.yml`:

| Setting | Value |
|---|---|
| `ConnectionStrings__FrogFrame` | `…;Username=frogframe_app;Password=${FROGFRAME_APP_PASSWORD}` |
| `ConnectionStrings__FrogFrameSchema` | `…;Username=frogframe;Password=${POSTGRES_PASSWORD}` |
| `Database__AppRole` | `frogframe_app` |
| `Database__AppRolePassword` | `${FROGFRAME_APP_PASSWORD}` |

### Upgrading an existing installation

1. Add `FROGFRAME_APP_PASSWORD` to `.env`. Compose refuses to start without it.
2. Run `docker compose up -d` (or deploy the release as usual). The migrate step creates the role and the grants, and the app switches to it.

No data is moved, and ownership of the tables stays with `frogframe`.

### Changing the password

Change `FROGFRAME_APP_PASSWORD` in `.env` and run `docker compose up -d`. The migrate step sets the new password before the app restarts with it.

### After a restore

`deploy/scripts/restore-db.sh` and the restore drill restore without privileges (`--no-privileges`). Run the migrate step afterwards (`docker compose run --rm migrate`, or `docker compose up -d`) to give `frogframe_app` its grants again.

## Without Docker Compose

Configure the same four settings. The owner connection needs `CREATEROLE` (or superuser) the first time, so that it can create the role; afterwards it only grants. Alternatively, create the role yourself and leave `Database:AppRolePassword` empty: the grants are still applied on every migration.

`deploy/sql/gl-roles.sql` does the same by hand with `psql`. It is an option for databases managed outside FrogFrame.

Without `ConnectionStrings:FrogFrameSchema` and `Database:AppRole`, everything uses one connection, as before. That is fine for development.

## Startup check

After migrations, the app checks the role it serves requests with. If that role is a superuser, owns tables, or can create objects in `public`, the app logs this (a warning outside Development):

```
The application connects to PostgreSQL as frogframe (superuser: True, owns tables: True, can create objects: True). Use a separate data-only role in production: see docs/guides/database-roles.md.
```

The deployment checks also refuse placeholder or weak values for `Database:AppRolePassword` and for the passwords in both connection strings.

## Limits

- The owner credentials are still in the app's environment, because indexes and reporting views are rebuilt when an administrator changes metadata. This protects against SQL-level misuse of the request connection. It does not protect against someone who can run code in the app container.
- In the official PostgreSQL image, `frogframe` (`POSTGRES_USER`) is a superuser. Making the owner a non-superuser is a separate step and is not done here.
- The scheduler's occasional `REINDEX` of `frogframe_scheduled_job_runs` needs the owner. It is skipped silently with the runtime role, as before when it failed.

## Code

- `src/Framework.Persistence.PostgreSql/AppRoleProvisioner.cs`: `EnsureAsync` (role and grants) and `InspectRuntimeRoleAsync` (startup check).
- `src/Apps.Erp/Program.cs`: calls both after migrating.
- `tests/Framework.Tests/DatabaseRoleTests.cs`: tests on PostgreSQL. Data access works as the runtime role, including for tables created later; schema changes, ledger changes, `TRUNCATE`, `COPY TO PROGRAM` and `CREATE ROLE` are refused.
