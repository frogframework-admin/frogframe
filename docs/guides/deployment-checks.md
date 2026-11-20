# Deployment Checks

Outside the Development environment, FrogFrame checks its configuration when it starts, before migrating or seeding anything. A failed check stops the application with a message that names the setting to change. This prevents it from starting in an unsafe state.

| Check | Fails when | Fix |
|---|---|---|
| Public URL | `App:PublicBaseUrl` is empty or not an absolute http/https URL | Set `FROGFRAME_PUBLIC_BASE_URL` |
| Allowed hosts | `AllowedHosts` is empty or `*` | Set `FROGFRAME_ALLOWED_HOSTS` (e.g. `erp.example.com`) |
| Demo mode | `Demo:SeedData` or `Demo:ShowAccounts` is `true` | Set `FROGFRAME_DEMO_SEED_DATA=false` and `FROGFRAME_DEMO_SHOW_ACCOUNTS=false` |
| Demo server password | demo mode is explicitly allowed but `Demo:Password` is the public default or shorter than 12 characters | Set `FROGFRAME_DEMO_PASSWORD` |

## Why demo mode is blocked

Demo seeding creates accounts such as `admin` (System Manager), `hr02` and `finance01`, all with one shared password that the login page displays. The default password is in the repository. Running that outside a local machine would give anyone administrator access.

## Compose files

- `docker-compose.yml` defaults to `ASPNETCORE_ENVIRONMENT=Development` during the development phase. Development is always demo mode (demo accounts seeded and listed on the login page), and no database or object storage port is published to the host.
- **Going live** needs one change in `.env`: `ASPNETCORE_ENVIRONMENT=Production`. Demo mode then turns off by itself, because the `FROGFRAME_DEMO_*` switches default to `false` and only apply outside Development. If one of them is still `true`, the application stops at startup and names the values to change.
- `docker-compose.demo.yml` adds the PostgreSQL and S3 ports on `127.0.0.1`, for running `dotnet run` against the containers:

  ```bash
  docker compose -f docker-compose.yml -f docker-compose.demo.yml up -d
  ```

## Dedicated demo server

To run demo mode on a server (for example a public trial), opt in explicitly and use your own password:

```env
FROGFRAME_DEMO_SEED_DATA=true
FROGFRAME_DEMO_SHOW_ACCOUNTS=true
FROGFRAME_DEMO_ALLOW_OUTSIDE_DEVELOPMENT=true
FROGFRAME_DEMO_PASSWORD=<at least 12 characters, not the default>
```

Never point a demo server at a production database. On every start, demo seeding restores the names and roles of accounts with the demo ids (for example `admin` becomes a System Manager again) and deletes accounts with the ids of older demo personas (`andi`, `budi`, `citra` and others).
