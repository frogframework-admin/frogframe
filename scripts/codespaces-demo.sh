#!/usr/bin/env bash
# Runs the FrogFrame demo (sample data, demo accounts on the login page) with Docker Compose, in GitHub Codespaces or
# on any machine with Docker and the .NET 10 SDK.
#
#   scripts/codespaces-demo.sh            # prepare .env, publish, build and start; waits until the app is ready
#   scripts/codespaces-demo.sh prepare    # only create/update .env
#   scripts/codespaces-demo.sh logs       # follow the app log
#   scripts/codespaces-demo.sh down       # stop the demo (data is kept in Docker volumes)
#   scripts/codespaces-demo.sh reset      # stop and delete the demo data
#
# .env: created from .env.example on the first run, with a random value for every "change-me-..." secret. An existing
# .env is kept; only the public URL and allowed hosts are set for the Codespace, and missing settings are added.
# Demo mode only; for production see docs/guides/deployment-checks.md and docs/guides/release.md.
set -euo pipefail

cd "$(dirname "$0")/.."
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.demo.yml)
log() { echo "[demo] $*"; }

# Sets KEY=value in .env (replaces the line, or appends it).
env_set() {
  local key=$1 value=$2
  if grep -q "^${key}=" .env; then
    # | as sed delimiter: values are URLs and host lists, never containing |
    sed -i "s|^${key}=.*|${key}=${value}|" .env
  else
    printf '%s=%s\n' "$key" "$value" >> .env
  fi
}

env_get() { grep -E "^$1=" .env | tail -n 1 | cut -d= -f2- || true; }

random_secret() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }

prepare_env() {
  if [[ ! -f .env ]]; then
    [[ -f .env.example ]] || { echo "[demo] .env.example not found" >&2; exit 1; }
    log "Creating .env from .env.example (random secrets)"
    cp .env.example .env
    # Every change-me placeholder gets its own random value.
    while grep -q '=change-me-' .env; do
      local key
      key=$(grep -m1 '=change-me-' .env | cut -d= -f1)
      env_set "$key" "$(random_secret)"
    done
  fi

  local port url hosts
  port=$(env_get FROGFRAME_HTTP_PORT); port=${port:-5080}
  if [[ -n "${CODESPACE_NAME:-}" && -n "${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN:-}" ]]; then
    url="https://${CODESPACE_NAME}-${port}.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}"
    hosts="localhost;*.${GITHUB_CODESPACES_PORT_FORWARDING_DOMAIN}"
  else
    url="http://localhost:${port}"
    hosts="localhost"
  fi
  env_set FROGFRAME_PUBLIC_BASE_URL "$url"
  env_set FROGFRAME_ALLOWED_HOSTS "$hosts"
  [[ -n "$(env_get ASPNETCORE_ENVIRONMENT)" ]] || env_set ASPNETCORE_ENVIRONMENT Development

  # Settings required by docker-compose.yml that an older .env may lack.
  for key in POSTGRES_PASSWORD FROGFRAME_RO_PASSWORD FROGFRAME_APP_PASSWORD FROGFRAME_ADMIN_PASSWORD; do
    [[ -n "$(env_get "$key")" ]] || { log "Adding $key to .env"; env_set "$key" "$(random_secret)"; }
  done

  if [[ "$(env_get ASPNETCORE_ENVIRONMENT)" != "Development" ]]; then
    log "Warning: ASPNETCORE_ENVIRONMENT is $(env_get ASPNETCORE_ENVIRONMENT) in .env; the demo data and accounts need Development."
  fi
  log "Public URL: $url"
}

require() { command -v "$1" >/dev/null 2>&1 || { echo "[demo] '$1' is not installed. $2" >&2; exit 1; }; }

up() {
  require docker "In Codespaces it comes with the dev container (Docker-in-Docker)."
  require dotnet "Install the .NET 10 SDK (included in the Codespaces dev container)."
  docker info >/dev/null 2>&1 || { echo "[demo] Docker is not running. In Codespaces, wait a moment after the container starts and try again." >&2; exit 1; }
  prepare_env

  log "Publishing the application (dotnet publish)…"
  dotnet publish src/Apps.Erp -c Release -o publish --nologo -v quiet

  log "Building and starting the containers (first run takes a few minutes)…"
  "${COMPOSE[@]}" up -d --build

  local port; port=$(env_get FROGFRAME_HTTP_PORT); port=${port:-5080}
  log "Waiting for the application (migrations and sample data on the first run)…"
  local deadline=$((SECONDS + ${DEMO_WAIT_SECONDS:-600}))
  until curl -fsS -o /dev/null "http://localhost:${port}/health/ready"; do
    if (( SECONDS >= deadline )); then
      echo "[demo] The application is not ready yet. See: scripts/codespaces-demo.sh logs" >&2
      exit 1
    fi
    sleep 5
  done

  local demo_password; demo_password=$(env_get FROGFRAME_DEMO_PASSWORD)
  cat <<EOF

[demo] FrogFrame is ready: $(env_get FROGFRAME_PUBLIC_BASE_URL)
       (in Codespaces: tab "Ports", port ${port})

       Demo accounts are listed on the login page, password: ${demo_password:-FrogFrame#2026}
       Administrator: $(env_get FROGFRAME_ADMIN_USER || true) / password FROGFRAME_ADMIN_PASSWORD in .env

       Port ${port} is private in Codespaces (only you). To share it: Ports tab → right-click → Port Visibility → Public.
       After changing code, run this script again.
EOF
}

case "${1:-up}" in
  up) up ;;
  prepare) prepare_env ;;
  logs) "${COMPOSE[@]}" logs -f app ;;
  down) "${COMPOSE[@]}" down ;;
  reset) "${COMPOSE[@]}" down -v ;;
  *) echo "Usage: $0 [up|prepare|logs|down|reset]" >&2; exit 2 ;;
esac
