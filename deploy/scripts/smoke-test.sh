#!/usr/bin/env bash
# ==============================================================================
# FrogFrame smoke test: checks a running instance after a deployment.
#
#   smoke-test.sh <base-url> [user] [password]
#
#  1. GET /health/live answers 200 (waits up to SMOKE_TIMEOUT seconds, default 300, for the app to start)
#  2. GET /health/ready answers 200 (database and storage reachable, migrations applied)
#  3. With user and password: POST /api/auth/login returns an access token, and GET /api/auth/me answers 200 with it
#  4. GET /login answers 200 (the UI is served)
# Exit code 0 = all checks passed. Needs curl (and jq or python3 to read the token).
# ==============================================================================
set -euo pipefail

BASE_URL="${1:?Usage: smoke-test.sh <base-url> [user] [password]}"
BASE_URL="${BASE_URL%/}"
USER_NAME="${2:-}"
PASSWORD="${3:-}"
TIMEOUT="${SMOKE_TIMEOUT:-300}"
log() { echo "[smoke] $*"; }
fail() { echo "[smoke] FAILED: $*" >&2; exit 1; }
status() { local code; code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$@") || true; echo "${code:-000}"; }

wait_for() {
  local path="$1" deadline=$((SECONDS + TIMEOUT)) code
  while true; do
    code=$(status "$BASE_URL$path")
    [ "$code" = 200 ] && { log "$path: 200"; return 0; }
    [ $SECONDS -ge $deadline ] && fail "$path answered $code for ${TIMEOUT}s"
    sleep 3
  done
}

wait_for /health/live
wait_for /health/ready

if [ -n "$USER_NAME" ]; then
  body=$(curl -s --max-time 15 -X POST "$BASE_URL/api/auth/login" -H 'Content-Type: application/json' \
    -d "$(printf '{"userName":%s,"password":%s}' "$(printf '%s' "$USER_NAME" | python3 -c 'import json,sys;print(json.dumps(sys.stdin.read()))')" \
                                                "$(printf '%s' "$PASSWORD" | python3 -c 'import json,sys;print(json.dumps(sys.stdin.read()))')")") \
    || fail "login request failed"
  token=$(printf '%s' "$body" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("accessToken",""))
except Exception: print("")')
  [ -n "$token" ] || fail "login did not return an access token (response: ${body:0:200})"
  log "login: token received"
  code=$(status -H "Authorization: Bearer $token" "$BASE_URL/api/auth/me")
  [ "$code" = 200 ] || fail "/api/auth/me answered $code"
  log "/api/auth/me: 200"
fi

code=$(status "$BASE_URL/login")
[ "$code" = 200 ] || fail "/login answered $code"
log "/login: 200"
log "all checks passed for $BASE_URL"
