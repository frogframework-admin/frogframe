# Secrets

FrogFrame reads every secret from configuration: environment variables (the `.env` file used by Docker Compose) or secret files. None is stored in the repository. The values that appear in `appsettings.json`, `appsettings.Development.json` and `.env.example` are development defaults and placeholders, and they are refused outside Development (see [Deployment checks](deployment-checks.md)).

## Secrets

| Setting | `.env` variable | Used for |
|---|---|---|
| `ConnectionStrings:FrogFrameSchema` (password) | `POSTGRES_PASSWORD` | schema owner `frogframe` (migrations, indexes, reporting views) |
| `ConnectionStrings:FrogFrame` (password), `Database:AppRolePassword` | `FROGFRAME_APP_PASSWORD` | runtime role `frogframe_app` that serves requests (see [Database roles](database-roles.md)) |
| `ConnectionStrings:FrogFrameReadOnly` (password) | `FROGFRAME_RO_PASSWORD` | read-only user `frogframe_ro` for SQL dashboards |
| `Bootstrap:AdminPassword` | `FROGFRAME_ADMIN_PASSWORD` | initial admin, created once if it does not exist |
| `Storage:S3:AccessKey`, `Storage:S3:SecretKey` | `FROGFRAME_S3_ACCESS_KEY`, `FROGFRAME_S3_SECRET_KEY` | object storage (bundled SeaweedFS or another S3 store) |
| `AI:ApiKey` | `FROGFRAME_AI_API_KEY` | AI designer, only with `AI:Auth=ApiKey` |
| `Demo:Password` | `FROGFRAME_DEMO_PASSWORD` | demo accounts, only on a dedicated demo server |

## Startup check

Outside Development the application refuses to start when a secret that is in use:

- still starts with `change-me` (the placeholders in `.env.example`);
- is a value from the repository or a common default (for example `frogframe`, `ganti-password-ini`, `FrogFrame#2026`, `password`, `admin`);
- is shorter than 12 characters. This applies to passwords and the S3 secret key; the S3 access key and the AI key are only checked for placeholders and defaults.

Settings that are not in use are skipped: the database password when `Persistence:Provider` is not `PostgreSql`, the S3 keys when `Storage:Provider` is not `S3`, an empty `Bootstrap:AdminPassword` (clear it after the first login) and an empty read-only connection string.

Generate values with, for example:

```bash
openssl rand -base64 24   # passwords
openssl rand -hex 20      # S3 access key
openssl rand -hex 32      # S3 secret key
```

## Docker Compose: the `.env` file

1. `cp .env.example .env` and replace every `change-me-...` value.
2. Keep `.env` out of git (it is in `.gitignore`) and readable only by the deploying user: `chmod 600 .env`.
3. `docker compose up -d`. Compose refuses to start when a required variable is missing.

## Secret files (Docker Swarm, Kubernetes, Key Vault / Vault)

The application also reads one file per setting from a folder, `/run/secrets` by default (change with `Secrets:Directory`). The file name is the setting with `__` for `:`, exactly like the environment variable, and the content is the value; a trailing newline is ignored. File values override environment variables, and a missing folder is ignored.

```text
/run/secrets/ConnectionStrings__FrogFrame        Host=postgres;Port=5432;Database=frogframe;Username=frogframe;Password=...
/run/secrets/ConnectionStrings__FrogFrameReadOnly
/run/secrets/Bootstrap__AdminPassword
/run/secrets/Storage__S3__SecretKey
```

This works with Docker Swarm secrets, Kubernetes secrets mounted as a volume, and the Azure Key Vault or HashiCorp Vault CSI drivers, which write secrets as files. Other files in the folder only add unused settings.

## Rotation

Rotate a secret when someone who knew it leaves, after a suspected leak, and on a fixed schedule (for example every 90 days).

| Secret | How to rotate |
|---|---|
| Database password | `ALTER ROLE frogframe PASSWORD '<new>';` (as a superuser), update `POSTGRES_PASSWORD` (or the secret file), restart the app. The `postgres` container keeps the old `POSTGRES_PASSWORD` value only for first-time initialisation, so changing `.env` alone does not change the role's password. |
| Application role password | Update `FROGFRAME_APP_PASSWORD` and run `docker compose up -d`: the migrate step sets the new password before the app restarts with it. |
| Read-only password | `ALTER ROLE frogframe_ro PASSWORD '<new>';`, update `FROGFRAME_RO_PASSWORD`, restart the app. |
| Admin password | Change it in the application (profile), then clear `FROGFRAME_ADMIN_PASSWORD`. The bootstrap value is only used when the admin account does not exist. |
| S3 keys (bundled SeaweedFS) | Update both `FROGFRAME_S3_*` values and recreate the containers (`docker compose up -d`); SeaweedFS writes its identity file from the environment at start. For an external store, add the new key there first, switch the app, then delete the old key. |
| AI key | Create a second key in the Azure portal, switch `FROGFRAME_AI_API_KEY`, restart, then revoke the old key. |

After a rotation, check the startup log and sign in once.
