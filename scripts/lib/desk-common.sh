#!/usr/bin/env bash
# Shared by scripts/desk.sh and scripts/market-open.sh: one definition of how
# the API is started, stopped and judged healthy, and of how the console is
# built. Two copies of "start the API" had already drifted once (one restarted
# it, one did not; one pinned the port, one fell back to 5000).
#
# Source it from the repo root:  . scripts/lib/desk-common.sh

REPO_ROOT="${REPO_ROOT:-$PWD}"
API="${API_BASE_URL:-http://localhost:5025}"
LOG="${LOG:-$REPO_ROOT/logs/desk.log}"
mkdir -p "$REPO_ROOT/logs"

# Every line goes to the log; it is echoed to the screen too unless
# DESK_LOG_ONLY is set (the headless desk's stdout IS the log, and a tee
# there would write each line twice).
say() {
  if [ -n "${DESK_LOG_ONLY:-}" ]; then printf '%s  %s\n' "$(date '+%H:%M:%S')" "$1" >>"$LOG"
  else printf '%s  %s\n' "$(date '+%H:%M:%S')" "$1" | tee -a "$LOG"; fi
}
warn() { say "WARN: $1"; }

# --- environment the API and its daemons run with --------------------------
# Underlyings the chain poller covers. The poller is spawned by the API and
# reads this from the environment it inherits, so it is exported before the
# API starts and the API is the only thing that ever starts the poller.
export CHAIN_UNDERLYINGS="${CHAIN_UNDERLYINGS:-BANKNIFTY,NIFTY,SENSEX}"
# Production: Development exposes Swagger and puts stack traces in error JSON,
# and this API is on a public domain. Every real setting is in
# appsettings.Local.json, which loads in both.
export ASPNETCORE_ENVIRONMENT=Production
# The launch profile is skipped (it forces Development), and it was also what
# set the port; without this Kestrel falls back to 5000.
export ASPNETCORE_URLS="$API"

api_healthy() { curl -sf -o /dev/null --max-time 4 "$API/health"; }

# --- the database and Redis -------------------------------------------------
# Docker Desktop is an app: after a reboot it is only running if it is a login
# item, and the first morning after a shutdown found it closed — no database,
# so the API died on every start and the domain showed 502. Start it if the
# daemon is not answering, then bring the containers up.
infra_up() {
  if ! docker info >/dev/null 2>&1; then
    say "Docker daemon is not running — starting Docker Desktop"
    open -g -a Docker 2>/dev/null || { warn "could not open Docker Desktop"; return 1; }
    local i; for i in $(seq 1 40); do sleep 3; docker info >/dev/null 2>&1 && break; done
    docker info >/dev/null 2>&1 || { warn "Docker daemon did not come up in two minutes"; return 1; }
  fi
  ( cd "$REPO_ROOT" && docker compose up -d --wait timescaledb redis >>"$LOG" 2>&1 ) && say "  infra up" || { warn "docker compose failed (see desk.log)"; return 1; }
}

api_pids() { pgrep -f "AlgoTrading.Api" 2>/dev/null || true; }

api_stop() {
  local pids; pids="$(api_pids)"
  [ -n "$pids" ] || return 0
  say "stopping the API (pid $(printf '%s' "$pids" | tr '\n' ' '))"
  # SIGTERM first: Kestrel drains in-flight requests and the daemons it
  # supervises get their stdio closed cleanly.
  kill $pids 2>/dev/null || true
  for _ in $(seq 1 15); do sleep 1; [ -z "$(api_pids)" ] && return 0; done
  kill -9 $(api_pids) 2>/dev/null || true
  sleep 1
}

api_start() {
  if api_healthy; then say "API already healthy"; return 0; fi
  api_stop
  say "starting the API (Production, $API, chain: $CHAIN_UNDERLYINGS)"
  # One log per API lifetime, kept under the time it ended, seven deep. Left
  # to append forever, api.log reached 2 GB in two days (EF Core was logging
  # every SQL statement; see appsettings.json); kept as a single .prev, the
  # market-hours log of 2026-09-08 was gone by the evening's second restart.
  if [ -s "$REPO_ROOT/logs/api.log" ]; then
    mv -f "$REPO_ROOT/logs/api.log" "$REPO_ROOT/logs/api-until-$(date '+%Y%m%d-%H%M%S').log"
    ls -t "$REPO_ROOT"/logs/api-until-*.log 2>/dev/null | tail -n +8 | xargs rm -f 2>/dev/null || true
  fi
  ( cd "$REPO_ROOT" && nohup dotnet run --project src/AlgoTrading.Api --no-launch-profile >>"$REPO_ROOT/logs/api.log" 2>&1 & )
  for _ in $(seq 1 60); do sleep 2; api_healthy && { say "  API up"; return 0; }; done
  warn "the API did not come up within two minutes (see logs/api.log)"
  return 1
}

api_restart() { api_stop; api_start; }

# --- the console bundle ------------------------------------------------------
# The API serves the console from wwwroot on the domain, so a frontend change
# is live the moment this runs — no restart.
web_build() {
  say "building the web console into wwwroot ..."
  if ( cd "$REPO_ROOT/web" && VITE_API_BASE_URL='' npx vite build >>"$LOG" 2>&1 ); then
    rm -rf "$REPO_ROOT/src/AlgoTrading.Api/wwwroot"
    mkdir -p "$REPO_ROOT/src/AlgoTrading.Api/wwwroot"
    cp -R "$REPO_ROOT/web/dist/." "$REPO_ROOT/src/AlgoTrading.Api/wwwroot/"
    say "  console built"
  else
    warn "web build FAILED — the previous bundle is still being served"
    return 1
  fi
}

# --- talking to the API as the admin -----------------------------------------
load_env() {
  [ -f "$REPO_ROOT/.env" ] || { warn ".env is missing"; return 1; }
  set -a; . "$REPO_ROOT/.env"; set +a
  [ -n "${ADMIN_USERNAME:-}" ] && [ -n "${ADMIN_PASSWORD:-}" ]
}

admin_token() {
  load_env || return 1
  curl -fsS --max-time 10 -X POST "$API/api/UserAuth/login" -H 'Content-Type: application/json' \
    -d "{\"userNameOrEmail\":\"$ADMIN_USERNAME\",\"password\":\"$ADMIN_PASSWORD\"}" \
    | grep -o '"accessToken":"[^"]*' | grep -o '[^"]*$'
}

# Number of strategy runs currently Running; -1 when the API cannot be asked
# (treated as "something may be live" by callers that care).
live_runs() {
  local tok body
  tok="$(admin_token 2>/dev/null)" || { echo -1; return; }
  body="$(curl -fsS --max-time 15 "$API/api/Strategy/runs?status=Running" -H "Authorization: Bearer $tok" 2>/dev/null)" || { echo -1; return; }
  # Counted from the captured body, not through a pipeline: under pipefail a
  # grep that matches nothing fails the pipe, and the `|| echo -1` fallback
  # then printed "0" AND "-1" — zero live runs read as "cannot tell".
  local n; n="$(printf '%s' "$body" | grep -o '"runId"' | wc -l)"
  echo "${n// /}"
}
