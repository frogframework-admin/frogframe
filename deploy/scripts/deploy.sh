#!/usr/bin/env bash
# ==============================================================================
# FrogFrame deployment on a Docker Compose host (run in the directory with docker-compose.yml and .env).
#
#   deploy.sh deploy <image>   switch to <image> (e.g. ghcr.io/acme/frogframe:1.4.0), remembering the current one
#   deploy.sh rollback         switch back to the image that was running before the last deploy
#
# A deploy runs, in this order: db-backup (snapshot), migrate (pending migrations), app. A rollback only switches the
# application image back; migrations are not reversed (they are written to stay compatible with the previous
# release; see docs/guides/database-migrations.md). The images are kept in .env: FROGFRAME_IMAGE and
# FROGFRAME_PREVIOUS_IMAGE.
# ==============================================================================
set -euo pipefail

log() { echo "[deploy] $*"; }
env_get() { sed -n "s/^$1=//p" .env | tail -n 1; }
env_set() {
  if grep -q "^$1=" .env; then sed -i "s#^$1=.*#$1=$2#" .env; else echo "$1=$2" >> .env; fi
}

[ -f docker-compose.yml ] || { echo "Run this in the directory with docker-compose.yml." >&2; exit 2; }
touch .env
# Stop before changing anything when .env lacks a required value (e.g. FROGFRAME_APP_PASSWORD, added in this
# release): docker compose reports the missing variable by name.
docker compose config -q || { echo "[deploy] docker-compose.yml cannot be loaded with this .env; nothing was changed." >&2; exit 1; }
case "${1:-}" in
  deploy)
    image="${2:?Usage: deploy.sh deploy <image>}"
    current="$(env_get FROGFRAME_IMAGE)"
    if [ -n "$current" ] && [ "$current" != "$image" ]; then env_set FROGFRAME_PREVIOUS_IMAGE "$current"; fi
    env_set FROGFRAME_IMAGE "$image"
    log "Pulling $image"
    docker compose pull app migrate
    log "Starting: db-backup -> migrate -> app"
    docker compose up -d --no-build
    log "Deployed $image (previous: ${current:-none})"
    ;;
  rollback)
    previous="$(env_get FROGFRAME_PREVIOUS_IMAGE)"
    current="$(env_get FROGFRAME_IMAGE)"
    [ -n "$previous" ] || { echo "[deploy] No previous image recorded; nothing to roll back to." >&2; exit 1; }
    env_set FROGFRAME_IMAGE "$previous"
    env_set FROGFRAME_PREVIOUS_IMAGE "$current"
    log "Rolling back $current -> $previous"
    docker compose pull app migrate
    docker compose up -d --no-build
    log "Rolled back to $previous"
    ;;
  *)
    echo "Usage: deploy.sh deploy <image> | deploy.sh rollback" >&2; exit 2 ;;
esac
