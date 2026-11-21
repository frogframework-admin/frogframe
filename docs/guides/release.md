# Release Pipeline

Releases are built and deployed by GitHub Actions:

| Workflow | Does |
|---|---|
| `ci.yml` | Every push and pull request on `main`: build, tests (in-memory and PostgreSQL), vulnerable package check. |
| `release.yml` | Version tags and manual runs: image build, image smoke test, publish to GHCR, staging, production. |
| `deploy.yml` | Deploys one image to one environment, smoke-tests it, rolls back when the smoke test fails. Called by `release.yml`, or run by hand to redeploy an image. |

## Releasing a version

```bash
git tag v1.4.0 && git push origin v1.4.0
```

1. **Image**: restore, build and the in-memory tests; `dotnet publish`; Docker image. The image is started with the real `docker-compose.yml` in Production mode with generated secrets and a fresh database, and `deploy/scripts/smoke-test.sh` checks it. Only then is it pushed to `ghcr.io/<owner>/<repo>` with the tags `1.4.0`, `1.4`, `latest` and `sha-<commit>`.
2. **Staging** (when `DEPLOY_STAGING=true`): `deploy.yml` with the `sha-<commit>` image.
3. **Production** (when `DEPLOY_PRODUCTION=true`, after staging succeeded): the same image. Deployment waits for a required reviewer to approve it in GitHub.

Update `CHANGELOG.md` before tagging (rename *Unreleased* to the version).

A manual run (Actions → Release → Run workflow) builds and publishes `sha-<commit>` and the branch name, and deploys to staging only if *Deploy the image to staging* is ticked.

## A deployment

On the host, `deploy/scripts/deploy.sh deploy <image>`:

1. remembers the running image as `FROGFRAME_PREVIOUS_IMAGE` in `.env` and sets `FROGFRAME_IMAGE`;
2. `docker compose pull app migrate`;
3. `docker compose up -d --no-build`, which runs `db-backup` (snapshot), `migrate` (pending migrations) and then the app.

The runner then checks the URL with the smoke test:

1. `/health/live` and `/health/ready` answer 200 (waits up to 5 minutes);
2. with `SMOKE_USER` / `SMOKE_PASSWORD`: `/api/auth/login` returns a token and `/api/auth/me` works with it;
3. `/login` answers 200.

**Automatic rollback**: if the smoke test fails, `deploy.sh rollback` switches the app back to the previous image, the smoke test runs again, and the workflow fails with a message. Database migrations are not reversed. They are written to work with the previous release (see [Database migrations](database-migrations.md)), and the `db-backup` snapshot taken just before is available if data must be restored.

Roll back by hand on the host:

```bash
cd /srv/frogframe && bash deploy/scripts/deploy.sh rollback
```

Or deploy any earlier version: Actions → Deploy → Run workflow, with the environment and the image (for example `ghcr.io/acme/frogframe:1.3.2`).

## Setting up

### Repository (Settings → Secrets and variables → Actions → Variables)

| Variable | Value |
|---|---|
| `DEPLOY_STAGING` | `true` once the staging environment is ready |
| `DEPLOY_PRODUCTION` | `true` once the production environment is ready |

### Environments (Settings → Environments): `staging` and `production`

Same names in both:

| Kind | Name | Value |
|---|---|---|
| variable | `DEPLOY_HOST` | host name or IP for SSH |
| variable | `DEPLOY_SSH_USER` | SSH user (member of the `docker` group) |
| variable | `DEPLOY_PATH` | directory on the host, e.g. `/srv/frogframe` |
| variable | `DEPLOY_URL` | public URL, e.g. `https://erp-staging.example.com` |
| variable | `GHCR_USER` | only for private images: the user of `GHCR_TOKEN` |
| secret | `DEPLOY_SSH_KEY` | private key of a deploy key (ed25519) authorised for `DEPLOY_SSH_USER` |
| secret | `DEPLOY_KNOWN_HOSTS` | the host's line(s) from `ssh-keyscan <host>`; host keys are checked |
| secret | `GHCR_TOKEN` | only for private images: a token with `read:packages` |
| secret | `SMOKE_USER`, `SMOKE_PASSWORD` | optional: an account the smoke test signs in with (a user with no roles is enough) |

On **production**, add *Required reviewers*: they approve each production deployment.

### Host

- Docker with the Compose plugin.
- `DEPLOY_PATH` containing a `.env` with the secrets (`cp .env.example .env`, fill it in, `chmod 600 .env`; see [Secrets](secrets.md)) and `ASPNETCORE_ENVIRONMENT=Production`.
- The workflow copies `docker-compose.yml` and `deploy/` there on every deployment and keeps `FROGFRAME_IMAGE` / `FROGFRAME_PREVIOUS_IMAGE` in `.env`.
- A reverse proxy with TLS in front of the app port (`FROGFRAME_HTTP_PORT`), and `FROGFRAME_TRUSTED_PROXY_CIDR` set.

### Images

The package `ghcr.io/<owner>/<repo>` is created by the first release. It is private when the repository is private: give hosts the `GHCR_TOKEN`, or make the package public in its settings.

## Smoke test by hand

```bash
deploy/scripts/smoke-test.sh https://erp.example.com            # health and login page
deploy/scripts/smoke-test.sh https://erp.example.com user secret # plus sign-in
```
