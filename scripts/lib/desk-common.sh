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

# The same scripts run the desk on the Mac and on the Linux server (AWS): the
# few places that differ are behind these two.
IS_MAC=false; [ "$(uname -s)" = "Darwin" ] && IS_MAC=true
if $IS_MAC; then DESK_STATE_DIR="$HOME/Library/Application Support/algotrading"; else DESK_STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/algotrading"; fi
mkdir -p "$DESK_STATE_DIR"; chmod 700 "$DESK_STATE_DIR" 2>/dev/null || true

# How the desk reaches the operator. On the Mac that is a desktop notification;
# on the server there is no desktop, so it is Telegram.
#
# This used to be Mac-only, with the log as the server's "notification". The
# log is not a notification: on 2026-09-11 the morning run waited for a FYERS
# sign-in from 08:46, nudged at the open and every ten minutes after it, and
# every one of those nudges went nowhere because the desk had moved to Linux.
# Telegram is called directly rather than through the API, because notify() is
# needed most exactly when the API is the thing that is wrong.
# notify goes to the system channel (TELEGRAM_SYSTEM_CHAT_ID, else the one
# chat): deploys, sign-ins, a failed morning. notify_trades goes to the trades
# channel, for what a trader reads: the morning plan's tally.
notify() { _notify_to system "$@"; }
notify_trades() { _notify_to trades "$@"; }

_notify_to() {  # system|trades, title, message
  local channel="$1" chat; shift
  if $IS_MAC; then
    osascript -e "display notification \"$2\" with title \"$1\"" 2>/dev/null || true
    return 0
  fi
  # The alert path must not depend on the caller having loaded .env. desk.sh
  # reaches load_env only inside command substitutions, so those exports never
  # reach its own shell: without this, every deploy notice was a silent no-op —
  # the same "it looked fine because nothing said otherwise" failure this
  # function exists to end. Read straight from the file, quietly.
  if [ -z "${TELEGRAM_BOT_TOKEN:-}" ] || [ -z "${TELEGRAM_CHAT_ID:-}" ]; then
    if [ -f "$REPO_ROOT/.env" ]; then set -a; . "$REPO_ROOT/.env"; set +a; fi
  fi
  [ -n "${TELEGRAM_BOT_TOKEN:-}" ] && [ -n "${TELEGRAM_CHAT_ID:-}" ] || return 0
  # Chosen after .env is read, so the system chat is known; without one the
  # message goes to the one chat the desk has.
  chat="$TELEGRAM_CHAT_ID"
  [ "$channel" = system ] && [ -n "${TELEGRAM_SYSTEM_CHAT_ID:-}" ] && chat="$TELEGRAM_SYSTEM_CHAT_ID"
  # -o /dev/null: the URL carries the bot token, so nothing from this call is
  # ever echoed or logged.
  curl -fsS --max-time 10 -o /dev/null \
    "https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/sendMessage" \
    --data-urlencode "chat_id=${chat}" \
    --data-urlencode "text=$1 — $2" 2>/dev/null || true
}

# Every line goes to the log; it is echoed to the screen too unless
# DESK_LOG_ONLY is set (the headless desk's stdout IS the log, and a tee
# there would write each line twice).
#
# Stamped with the date as well as the time: desk.log runs for weeks, and a
# bare 11:27 could be any day's. Sentinel reads both this stamp and the older
# time-only one (sentinel/agents/logs.py, agents/health.py, pack.py).
say() {
  if [ -n "${DESK_LOG_ONLY:-}" ]; then printf '%s  %s\n' "$(date '+%F %T')" "$1" >>"$LOG"
  else printf '%s  %s\n' "$(date '+%F %T')" "$1" | tee -a "$LOG"; fi
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
    if $IS_MAC; then
      say "Docker daemon is not running — starting Docker Desktop"
      open -g -a Docker 2>/dev/null || { warn "could not open Docker Desktop"; return 1; }
    else
      say "Docker daemon is not running — starting the docker service"
      sudo -n systemctl start docker 2>>"$LOG" || { warn "could not start docker (systemctl)"; return 1; }
    fi
    for _ in $(seq 1 40); do sleep 3; docker info >/dev/null 2>&1 && break; done
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
  # shellcheck disable=SC2046  # one PID per word, on purpose
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
  # Every API gets .env in its environment, whichever script starts it. The
  # desk never loaded it, so an API it restarted (a deploy, a health restart)
  # had no DHAN_CLIENT_ID: on 22 and 24 Sep that was "Dhan has no client id"
  # and a feed on a dead token. market-open.sh loads .env itself, which is why
  # mornings worked. The two settings above are put back after it, so nothing
  # in .env can move the API off Production or its port.
  ( cd "$REPO_ROOT" \
    && if [ -f .env ]; then set -a; . ./.env; set +a; fi \
    && export ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="$API" \
    && nohup dotnet run --project src/AlgoTrading.Api --no-launch-profile >>"$REPO_ROOT/logs/api.log" 2>&1 & )
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

# --- authenticated calls that outlive a long morning -------------------------
# The admin JWT lives 60 minutes (Jwt:AccessTokenMinutes). A script that waits
# for the FYERS sign-in can run for hours, so a token minted once at the start
# is long dead by the time it is used: on 2026-09-11 market-open polled the
# session endpoint with a token that had expired at 09:46, could not see the
# 10:17 sign-in, and so started no feed and no strategy for the whole day.
#
# Every authenticated call goes through these instead. They mint on first use,
# reuse the token while it is comfortably fresh, and mint a new one on a 401 —
# staleness is the one failure a long-running script can always repair itself.
# The cache is a file, not a variable. Every call site reads the result
# through $(...), which is a subshell, so a variable cache is thrown away the
# moment the call returns: the morning wait would sign in three times a minute,
# fill the activity log with hundreds of "admin signed in" rows, and sit one
# busy console away from the 10-per-minute limit. The file survives subshells.
# It holds a 60-minute bearer token, so it is written 0600 in a 0700 dir.
_TOKEN_CACHE="$DESK_STATE_DIR/admin-token"
# Half the token's 60-minute life: a call can never race the expiry.
ADMIN_TOKEN_TTL="${ADMIN_TOKEN_TTL:-1800}"

# Drops the cached token, so the next call signs in again. Called on a 401.
auth_token_forget() { rm -f "$_TOKEN_CACHE" 2>/dev/null || true; }

auth_token() {
  local now minted cached fresh deadline
  now="$(date +%s)"
  if [ -r "$_TOKEN_CACHE" ]; then
    minted="$(sed -n 1p "$_TOKEN_CACHE" 2>/dev/null)"
    cached="$(sed -n 2p "$_TOKEN_CACHE" 2>/dev/null)"
    case "$minted" in ''|*[!0-9]*) minted=0 ;; esac
    if [ -n "$cached" ] && [ "$(( now - minted ))" -lt "$ADMIN_TOKEN_TTL" ]; then
      printf '%s' "$cached"
      return 0
    fi
  fi
  # Keep asking across the whole sign-in window and a little past it. The API
  # allows 10 sign-ins per minute per IP on a 1-minute sliding window
  # (Program.cs), so a burst locks the door for up to a minute. Giving up
  # inside that minute would report a transient as "the credentials are wrong"
  # and cost the morning, which is the failure this file exists to end.
  deadline=$(( now + ${ADMIN_TOKEN_WAIT:-90} ))
  while :; do
    fresh="$(admin_token 2>/dev/null)" || fresh=""
    if [ -n "$fresh" ]; then
      ( umask 077; printf '%s\n%s\n' "$(date +%s)" "$fresh" > "$_TOKEN_CACHE" )
      printf '%s' "$fresh"
      return 0
    fi
    [ "$(date +%s)" -ge "$deadline" ] && return 1
    sleep "${ADMIN_TOKEN_RETRY_WAIT:-15}"
  done
}

# api_get PATH   /   api_post PATH [JSON-BODY]
# The response body is printed; the return code is 0 only for a 2xx, so
# `if api_get ...` reads as "the API answered properly". A 401 is retried once
# with a fresh token; every other status is the answer and is handed back.
# API_MAX_TIME (seconds, default 30) is for the few calls that legitimately
# take longer, such as a vendor's instrument import.
api_get()  { _api_call GET  "$1" ''; }
api_post() { _api_call POST "$1" "${2-'{}'}"; }

_api_call() {
  local method="$1" path="$2" body="$3" attempt tok raw code out
  for attempt in 1 2; do
    tok="$(auth_token)" || return 1
    if [ "$method" = GET ]; then
      raw="$(curl -sS --max-time "${API_MAX_TIME:-30}" -w '\n%{http_code}' "$API$path" \
        -H "Authorization: Bearer $tok" 2>/dev/null)" || return 1
    else
      raw="$(curl -sS --max-time "${API_MAX_TIME:-30}" -w '\n%{http_code}' -X POST "$API$path" \
        -H "Authorization: Bearer $tok" -H 'Content-Type: application/json' \
        -d "$body" 2>/dev/null)" || return 1
    fi
    code="${raw##*$'\n'}"
    out="${raw%$'\n'*}"
    if [ "$code" = 401 ] && [ "$attempt" = 1 ]; then auth_token_forget; continue; fi
    printf '%s' "$out"
    case "$code" in 2??) return 0 ;; *) return 1 ;; esac
  done
  return 1
}

# Number of strategy runs currently Running; -1 when the API cannot be asked
# (treated as "something may be live" by callers that care).
# --- when a deploy may build and restart --------------------------------------
# A deploy rebuilds and restarts the API. During a session that is not free:
# on 22 Sep (14:58, 10 live runs) and 24 Sep (11:28, 13 live runs) the Dhan
# socket went silent during the build and never came back, the restarted API
# came up without its chain recorder, and each day lost NSE ticks and hours of
# option-chain snapshots. A 2-vCPU box already near 100% with 26 runners takes
# twice as long to build as it does at night.
#
# So the build and restart wait for a quiet desk: at weekends, or on weekdays
# before the morning job and after the evening close — and only with no live
# run. "Unknown" (-1, the API did not say) counts as live. A real hotfix can
# still go out at once: `touch "$DESK_STATE_DIR/deploy-now"`, which one deploy
# consumes.
#
# deploy_allowed DOW HHMM CLOSED_TODAY LIVE_RUNS  ->  exit 0 when it may build now.
#   DOW 1..7 (date +%u), HHMM local IST, CLOSED_TODAY 1 once market-close ran today.
DEPLOY_NOW_FILE="$DESK_STATE_DIR/deploy-now"
DEPLOY_MORNING_END="${DEPLOY_MORNING_END:-0840}"

# deploy-now is honoured for an hour: touched when nothing was waiting, it
# used to linger until the next web or API commit, days later perhaps, which
# then went out in the middle of a session.
DEPLOY_NOW_MINUTES="${DEPLOY_NOW_MINUTES:-60}"

deploy_now_requested() {
  [ -f "$DEPLOY_NOW_FILE" ] && [ -n "$(find "$DEPLOY_NOW_FILE" -mmin "-$DEPLOY_NOW_MINUTES" 2>/dev/null)" ]
}

# Whether the clock alone lets a deploy through, before anyone asks the API
# how many runs are live: in the session the answer is no whatever the count.
deploy_clock_allows() {
  local dow="$1" hhmm="$2" closed="$3"
  [ "$dow" -ge 6 ] || [ "$((10#$hhmm))" -lt "$((10#$DEPLOY_MORNING_END))" ] || [ "$closed" = "1" ]
}

deploy_allowed() {
  local dow="$1" hhmm="$2" closed="$3" live="$4"
  deploy_now_requested && return 0
  [ "$dow" -ge 6 ] && return 0
  [ "$live" = "0" ] || return 1
  if [ "$((10#$hhmm))" -lt "$((10#$DEPLOY_MORNING_END))" ] || [ "$closed" = "1" ]; then
    return 0
  fi
  return 1
}

# Why deploy_allowed said no, in words for the log and the Deployments page.
deploy_block_reason() {
  local dow="$1" hhmm="$2" closed="$3" live="$4"
  if [ "$live" = "-1" ]; then
    echo "the live-run count is unknown (API did not answer), so runs are assumed live"
  elif [ "$live" != "0" ]; then
    echo "$live live run(s)"
  else
    echo "market day, $((10#$hhmm / 100)):$(printf '%02d' $((10#$hhmm % 100))) — waits until the evening close"
  fi
}

live_runs() {
  # Strategy runs with a runner behind them. Not every Running row: the manual
  # order book is Running by design with no runner, and holds positions across
  # days, so counting it kept the pre-open deploy window shut; a row whose
  # runner is gone (isActive false) is not a run a restart could disturb.
  # Through api_get, whose token is cached: a fresh admin sign-in on every
  # two-minute check put ~430 "signed in" rows a day in the activity log.
  local body
  body="$(api_get '/api/Strategy/runs?status=Running&take=500' 2>/dev/null)" || { echo -1; return; }
  # Counted from the captured body, not through a grep pipeline: under
  # pipefail a grep that matches nothing fails the pipe, and the `|| echo -1`
  # fallback then printed "0" AND "-1" — zero live runs read as "cannot tell".
  BODY="$body" python3 -c '
import json, os
try:
    rows = json.loads(os.environ["BODY"])
except Exception:
    print(-1)
    raise SystemExit
if isinstance(rows, dict):
    rows = rows.get("items") or rows.get("runs") or []
print(sum(1 for r in rows if isinstance(r, dict)
          and r.get("isActive") is not False
          and str(r.get("strategyName") or "") != "Manual"))' 2>/dev/null || echo -1
}

# --- when the evening close runs ----------------------------------------------
# market-close.sh stops every run, feed and recorder, so it must run after the
# LAST market of the day has closed: MCX, at 23:30 IST while the US is on
# daylight saving and 23:55 while it is not (MarketSessionService). Until
# 27 Sep it ran at a fixed 23:35: after the summer close, but from 1 Nov twenty
# minutes before the winter one — squaring off crude runs that the owner
# decided that day should trade the MCX evening to its close. The API stops
# each run itself at its own market's close (MarketHoursService); this is the
# net under it.
#
# 23:58 is after both closes, and leaves the API's once-a-minute sweep time to
# stop the crude runs first, with its own "MCX closed (23:55 IST)". It also
# leaves two minutes before midnight, so a close the desk missed — the loop
# held up, the desk restarted — is caught up after midnight, until 06:00, for
# the day before. A desk started in that window runs it once more for that
# day, which is harmless: stopping what is already stopped does nothing.
#
# market_close_due DOW HHMM TODAY YESTERDAY CLOSED_ON [CLOSE_AT]
#   -> prints the trading day whose close is due and exits 0; exits 1 when none is.
#   DOW 1..7 (date +%u) of today, HHMM local IST, CLOSED_ON the day it last ran for.
MARKET_CLOSE_AT_DEFAULT=2358
MARKET_CLOSE_CATCH_UP_UNTIL="${MARKET_CLOSE_CATCH_UP_UNTIL:-0600}"

market_close_due() {
  local dow="$1" hhmm="$2" today="$3" yesterday="$4" closed_on="$5" close_at="${6:-$MARKET_CLOSE_AT_DEFAULT}"
  local ydow=$(( dow == 1 ? 7 : dow - 1 ))
  if [ "$dow" -le 5 ] && [ "$((10#$hhmm))" -ge "$((10#$close_at))" ] && [ "$closed_on" != "$today" ]; then
    echo "$today"; return 0
  fi
  if [ "$ydow" -le 5 ] && [ "$((10#$hhmm))" -lt "$((10#$MARKET_CLOSE_CATCH_UP_UNTIL))" ] && [ "$closed_on" != "$yesterday" ]; then
    echo "$yesterday"; return 0
  fi
  return 1
}
