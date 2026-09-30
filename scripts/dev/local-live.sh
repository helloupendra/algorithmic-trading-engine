#!/usr/bin/env bash
# A throwaway local copy of the platform, for trying the console against moving
# prices: its own Postgres and Redis in docker, the API built from this
# checkout, the web dev server, synthetic ticks and a few paper positions.
# Nothing in it can reach a broker.
#
# Usage: scripts/dev/local-live.sh up [--no-web] [--no-replay] [--no-seed]
#        scripts/dev/local-live.sh status
#        scripts/dev/local-live.sh check     # the isolation check on its own
#        scripts/dev/local-live.sh down      # stop everything, remove the containers
#
# Ports (a fresh `up` reads these; a running stack keeps its own):
#   DEV_API_PORT=5125  DEV_WEB_PORT=5180  DEV_DB_PORT=5544  DEV_REDIS_PORT=6390
#
# WHY IT IS BUILT LIKE THIS
# Production trades live on the owner's Dhan and FYERS accounts. A second API
# that signs in to Dhan with the PIN and TOTP ends the server's token; a second
# Dhan websocket disconnects the first; FYERS, Angel and TrueData behave the
# same way. So this stack cannot reach a broker by construction, and proves it
# while it runs:
#   1. No credential reaches the API. It starts from an EMPTY environment
#      (env -i) holding scripts/dev/local-live.env plus the values computed
#      here; its content root is .dev-live/content/api, so the
#      src/AlgoTrading.Api/appsettings.Local.json that holds the real keys is
#      never read (and the copy the build drops next to the dll is deleted);
#      its database is new; its Data Protection key ring is its own (HOME is
#      .dev-live/home).
#   2. Every automatic sign-in, feed, recorder and failover is switched off.
#   3. Every broker and vendor URL is http://127.0.0.1:9, a closed port, and
#      HTTP(S)_PROXY points there too, so any other outbound call fails fast.
#   4. Python never runs: StrategyRunner:PythonExecutable is refuse-python.sh,
#      so no feed, runner, backtest, alerter or notifier can start.
#   5. Its own containers, openfno_dev_db and openfno_dev_redis, on their own
#      ports, never the Mac's database on 5433 or the default containers,
#      which may hold old broker tokens.
#   6. Every socket of the API and its children is checked with lsof while it
#      starts, after it starts, on every `status` and `check`, and every 5 s by
#      a background watchdog: loopback only, and only this stack's own ports
#      (the API's, Postgres's, Redis's). A loopback port is not enough on its
#      own: an SSH tunnel to the server listens on loopback too. Anything else,
#      or sockets that cannot be read three times running, stops the API.
# Before any of that, `up` refuses to start if local-live.env gives a
# credential a value or points a URL anywhere but 127.0.0.1:9.
#
# Everything it writes is under .dev-live/ (git-ignored): the generated
# passwords (state.env), the API's environment (api.env), logs, pids and the
# build. `down` removes all of it except logs/ and build/.

set -uo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd -P)" || exit 1
SELF="$REPO/scripts/dev/local-live.sh"
# LOCAL_LIVE_DIR moves all of it elsewhere; scripts/tests/local-live.test.sh uses that.
DEV="${LOCAL_LIVE_DIR:-$REPO/.dev-live}"
STATE="$DEV/state.env"
API_ENV="$DEV/api.env"
CONTENT="$DEV/content/api"
BUILD="$DEV/build/api"
LOGS="$DEV/logs"
RUN="$DEV/run"
OVERRIDES="${LOCAL_LIVE_OVERRIDES:-$REPO/scripts/dev/local-live.env}"

DB_CONTAINER=openfno_dev_db
REDIS_CONTAINER=openfno_dev_redis
LABEL_KEY=com.openfno.dev-live
DB_IMAGE=timescale/timescaledb:latest-pg15
REDIS_IMAGE=redis:7-alpine
DB_NAME=openfno_dev
DB_USER=postgres
WALL_PORT=9
API_DLL="$BUILD/AlgoTrading.Api.dll"

G='\033[32m'; R='\033[31m'; Y='\033[33m'; D='\033[2m'; B='\033[1m'; N='\033[0m'
ok()   { printf '  %b●%b %-22s %s\n' "$G" "$N" "$1" "$2"; }
bad()  { printf '  %b●%b %-22s %s\n' "$R" "$N" "$1" "$2"; }
meh()  { printf '  %b●%b %-22s %s\n' "$Y" "$N" "$1" "$2"; }
step() { printf '\n%b%s%b\n' "$B" "$1" "$N"; }
die()  { printf '\n  %b%s%b\n\n' "$R" "$1" "$N" >&2; exit 1; }

# The running stack's own value for KEY, from .dev-live/state.env.
state_get() { [ -f "$STATE" ] && awk -F= -v k="$1" '$1 == k { sub(/^[^=]*=/, ""); print; exit }' "$STATE"; }

API_PORT="$(state_get API_PORT)";     API_PORT="${API_PORT:-${DEV_API_PORT:-5125}}"
WEB_PORT="$(state_get WEB_PORT)";     WEB_PORT="${WEB_PORT:-${DEV_WEB_PORT:-5180}}"
DB_PORT="$(state_get DB_PORT)";       DB_PORT="${DB_PORT:-${DEV_DB_PORT:-5544}}"
REDIS_PORT="$(state_get REDIS_PORT)"; REDIS_PORT="${REDIS_PORT:-${DEV_REDIS_PORT:-6390}}"
API_URL="http://127.0.0.1:$API_PORT"
WEB_URL="http://127.0.0.1:$WEB_PORT"

# What each process's command line must contain before it is signalled.
API_MARKER="$API_DLL"
WEB_MARKER="vite.js --host 127.0.0.1 --port $WEB_PORT"
REPLAY_MARKER="$REPO/scripts/dev/replay-ticks.py"
WATCHDOG_MARKER="$SELF _watchdog"

# ------------------------------------------------------------------ overrides --

# Credentials every connector reads. Each must be in local-live.env, empty: a
# line deleted by accident would let a value in from somewhere else.
REQUIRED_EMPTY="Fyers__ClientId Fyers__SecretKey
Dhan__ClientId Dhan__ApiKey Dhan__ApiSecret Dhan__AccessToken Dhan__Pin Dhan__TotpSecret
DHAN_CLIENT_ID DHAN_API_KEY DHAN_API_SECRET DHAN_ACCESS_TOKEN DHAN_PIN DHAN_TOTP_SECRET
TrueData__Username TrueData__Password TRUEDATA_USERNAME TRUEDATA_PASSWORD
Angel__ApiKey Angel__ClientCode Angel__Pin Angel__TotpSecret ANGEL_API_KEY ANGEL_CLIENT_CODE ANGEL_PIN ANGEL_TOTP_SECRET
SimBroker__AdminKey SimBroker__ClientId SimBroker__AppId SimBroker__AppSecret SimBroker__TotpSecret
SIMBROKER_ADMIN_KEY SIMBROKER_CLIENT_ID SIMBROKER_APP_ID SIMBROKER_APP_SECRET SIMBROKER_TOTP_SECRET
Telegram__BotToken TELEGRAM_BOT_TOKEN"

# Switches and walls that must be exactly so.
REQUIRED_EXACT="Dhan__AutoSignIn__Enabled=false Dhan__ChainPoller__Enabled=false DHAN_CHAIN_POLLER_ENABLED=false
FeedFailover__Enabled=false MarketFactors__SyncEnabled=false Forecasts__SchedulerEnabled=false PatternAlerts__Enabled=false
MarketIntelligence__NewsEnabled=false MarketIntelligence__AnnouncementsEnabled=false
MarketIntelligence__QuoteSnapshotsEnabled=false MarketIntelligence__GlobalDailyEnabled=false
MarketIntelligence__BreadthEnabled=false MarketIntelligence__BackfillEnabled=false MarketIntelligence__NewsScoringEnabled=false
Dhan__ApiBaseUrl=http://127.0.0.1:9 Dhan__AuthBaseUrl=http://127.0.0.1:9 Dhan__FeedUrl=ws://127.0.0.1:9
Fyers__DataApiBaseUrl=http://127.0.0.1:9 Angel__RootUrl=http://127.0.0.1:9 SimBroker__BaseUrl=http://127.0.0.1:9
TrueData__AuthBaseUrl=http://127.0.0.1:9 TrueData__StreamHost=127.0.0.1 TrueData__RealTimePort=9
HTTP_PROXY=http://127.0.0.1:9 HTTPS_PROXY=http://127.0.0.1:9 http_proxy=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9"

is_credential_key() {
  case "$1" in
    *ClientId|*SecretKey|*ApiKey|*ApiSecret|*AccessToken|*Pin|*TotpSecret|*Username|*Password|*ClientCode|\
    *AppId|*AppSecret|*AdminKey|*BotToken|*ChatId|*StaticIp) return 0 ;;
    *_CLIENT_ID|*_SECRET_KEY|*_APP_ID|*_API_KEY|*_API_SECRET|*_ACCESS_TOKEN|*_PIN|*_TOTP_SECRET|*_USERNAME|\
    *_PASSWORD|*_CLIENT_CODE|*_APP_SECRET|*_ADMIN_KEY|*_BOT_TOKEN|*_CHAT_ID|*_STATIC_IP) return 0 ;;
  esac
  return 1
}

# 0 when local-live.env is safe to start an API with; otherwise says why.
validate_overrides() {
  local line key value fails=0
  [ -f "$OVERRIDES" ] || { echo "  missing $OVERRIDES"; return 1; }
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    key="${line%%=*}"; value="${line#*=}"
    if [ "$key" = "$line" ] || [ -z "$key" ]; then echo "  not KEY=VALUE: $line"; fails=1; continue; fi
    if is_credential_key "$key" && [ -n "$value" ]; then
      echo "  $key has a value: every credential in the dev stack must be empty"; fails=1
    fi
    case "$value" in
      *://*)
        case "$value" in
          http://127.0.0.1:9|http://127.0.0.1:9/*|ws://127.0.0.1:9|ws://127.0.0.1:9/*) ;;
          *) echo "  $key points at $value: every address must be the closed port 127.0.0.1:9"; fails=1 ;;
        esac ;;
    esac
  done < "$OVERRIDES"
  for key in $REQUIRED_EMPTY; do
    grep -qx -- "$key=" "$OVERRIDES" || { echo "  $key= is missing (it must be there, empty)"; fails=1; }
  done
  for line in $REQUIRED_EXACT; do
    grep -qx -- "$line" "$OVERRIDES" || { echo "  $line is missing"; fails=1; }
  done
  return "$fails"
}

# ---------------------------------------------------------------- processes --

pid_of() { [ -f "$RUN/$1.pid" ] && tr -dc '0-9' < "$RUN/$1.pid"; }

# The pid recorded for NAME, when it is alive AND its command line has MARKER.
# A pid is only ever signalled after this: a recycled pid is someone else's.
alive_as() {
  local pid
  pid="$(pid_of "$1")"
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null || return 1
  ps -p "$pid" -o command= 2>/dev/null | grep -qF -- "$2" || return 1
  echo "$pid"
}

api_pid()      { alive_as api "$API_MARKER"; }
web_pid()      { alive_as web "$WEB_MARKER"; }
replay_pid()   { alive_as replay "$REPLAY_MARKER"; }
watchdog_pid() { alive_as watchdog "$WATCHDOG_MARKER"; }

# Runs a command in its own session (so its whole process group can be
# stopped, and closing this terminal does not stop it), output appended to LOG.
# Prints the pid, which is the command's own: perl and env both exec.
detach() {
  local log="$1"; shift
  perl -MPOSIX -e 'POSIX::setsid() != -1 or die "setsid: $!\n"; exec { $ARGV[0] } @ARGV or die "exec: $!\n"' -- "$@" \
    </dev/null >>"$log" 2>&1 &
  echo "$!"
}

# Stops NAME's process group: TERM, then KILL after 15 s. MARKER as alive_as.
stop_named() {
  local name="$1" marker="$2" pid i
  pid="$(alive_as "$name" "$marker")"
  if [ -z "$pid" ]; then
    # No pid file (or a stale one): the command line is unique to this checkout.
    pid="$(pgrep -f -- "$marker" 2>/dev/null | head -n 1)"
  fi
  if [ -n "$pid" ]; then
    kill -TERM -- "-$pid" 2>/dev/null || kill -TERM "$pid" 2>/dev/null
    for i in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
    if kill -0 "$pid" 2>/dev/null; then
      kill -KILL -- "-$pid" 2>/dev/null || kill -KILL "$pid" 2>/dev/null
      meh "$name" "did not stop on TERM; killed (pid $pid)"
    else
      ok "$name" "stopped (pid $pid, after ${i}x0.5 s)"
    fi
  fi
  rm -f "$RUN/$name.pid"
}

port_listener() { lsof -nP -iTCP:"$1" -sTCP:LISTEN -t 2>/dev/null | head -n 1; }

# ------------------------------------------------------------ isolation check --

# The API's pid and all its descendants, one per line.
tree_pids() {
  local kid
  echo "$1"
  for kid in $(pgrep -P "$1" 2>/dev/null); do tree_pids "$kid"; done
}

# Reads `lsof -F pfPnT` output and judges every socket. Exit 0 all local and
# expected, 1 a violation, 2 the API's own listening socket was not in the
# output (so lsof did not really see its sockets: unknown, not clean).
classify() {
  awk -v api="$API_PORT" -v db="$DB_PORT" -v redis="$REDIS_PORT" '
    function hostof(s) { sub(/:[^:]*$/, "", s); return s }
    function portof(s) { sub(/^.*:/, "", s); return s }
    function loop(h) { return h == "127.0.0.1" || h == "[::1]" || h == "localhost" || h == "[::ffff:127.0.0.1]" }
    function judge(   at, l, r, verdict, why) {
      if (name == "") return
      verdict = "VIOLATION"
      at = index(name, "->")
      if (at > 0) {
        l = substr(name, 1, at - 1); r = substr(name, at + 2)
        if (!loop(hostof(r)))                         why = "the other end is not this machine"
        else if (proto != "TCP")                      why = proto " to a loopback port"
        else if (portof(l) == api)                    { verdict = "ok"; why = "a client of the API" }
        else if (portof(r) == db)                     { verdict = "ok"; why = "the dev Postgres" }
        else if (portof(r) == redis)                  { verdict = "ok"; why = "the dev Redis" }
        else                                          why = "loopback, but not a port of this stack"
      } else if (proto == "TCP" && tcpstate == "LISTEN") {
        if (loop(hostof(name)) && portof(name) == api) { verdict = "ok"; why = "the API, listening"; listening = 1 }
        else                                          why = "listening beyond the API port or on every interface"
      } else if (proto == "TCP") {
        verdict = "ok"; why = "no peer"
      } else {
        why = proto " socket"
      }
      if (verdict != "ok") bad++
      printf "    %-9s pid %-6s %-4s %-46s %-11s %s\n", verdict, pid, proto, name, tcpstate, why
      name = ""; proto = ""; tcpstate = ""
    }
    /^p/    { judge(); pid = substr($0, 2); next }
    /^f/    { judge(); next }
    # A file starts at its f line on macOS, but Linux lsof prints none unless
    # asked; P comes before n in every file, so it also closes the one before.
    # Without it only the last socket of a process was judged on Linux.
    /^P/    { judge(); proto = substr($0, 2); next }
    /^n/    { name = substr($0, 2); next }
    /^TST=/ { tcpstate = substr($0, 5); next }
    END {
      judge()
      if (bad > 0) exit 1
      if (!listening) { print "    UNKNOWN   the API listening socket was not in lsof output"; exit 2 }
      exit 0
    }'
}

# Prints the check. 0 clean, 1 violation (and the API is stopped), 2 cannot
# tell this time, 3 the API is not running. MODE: empty prints everything,
# "quiet" only trouble, "starting" only a violation (until the API listens,
# its listening socket cannot be seen, so "cannot tell" is expected then).
isolation_check() {
  local mode="${1:-}" pid pids raw report rc
  pid="$(api_pid)" || { [ -n "$mode" ] || meh "isolation check" "API not running"; return 3; }
  pids="$(tree_pids "$pid" | paste -s -d, -)"
  # lsof exits 1 when a short-lived child is already gone; only the verdict matters.
  raw="$(lsof -nP -a -p "$pids" -i -F pfPnT 2>/dev/null)"
  report="$(printf '%s\n' "$raw" | classify)"; rc=$?
  if [ -e "$CONTENT/appsettings.Local.json" ]; then
    report="$report
    VIOLATION $CONTENT/appsettings.Local.json exists: the API would load it"
    rc=1
  fi
  case "$rc" in
    0) [ -n "$mode" ] || { ok "isolation check" "clean: pids $pids, loopback only, ports $API_PORT/$DB_PORT/$REDIS_PORT only"; printf '%s\n' "$report"; } ;;
    2) [ "$mode" = starting ] || { meh "isolation check" "could not read the API's sockets this time (pids $pids)"; printf '%s\n' "$report"; } ;;
    *) bad "isolation check" "FAILED: the API is not isolated (VIOLATION below). Stopping it."
       printf '%s\n' "$report"
       stop_named api "$API_MARKER"
       rc=1 ;;
  esac
  return "$rc"
}

# The check until it can tell: clean, or the API stopped. Three unreadable
# answers in a row stop it as well (fail closed).
verify_isolation() {
  local attempt rc
  for attempt in 1 2 3; do
    isolation_check; rc=$?
    case "$rc" in
      0) return 0 ;;
      2) sleep 1 ;;
      *) return "$rc" ;;
    esac
  done
  bad "isolation check" "the API's sockets could not be read $attempt times running: stopping it (fail closed)"
  stop_named api "$API_MARKER"
  return 1
}

# Runs in the background for as long as the API does (started by `up`).
watchdog() {
  local rc unknown=0 beats=0
  echo "$(date '+%F %T') watchdog started for $API_URL"
  while api_pid >/dev/null; do
    isolation_check quiet; rc=$?
    case "$rc" in
      0) unknown=0 ;;
      1) echo "$(date '+%F %T') VIOLATION: the API was stopped (see above)"; exit 1 ;;
      2) unknown=$((unknown + 1))
         if [ "$unknown" -ge 3 ]; then
           echo "$(date '+%F %T') the API's sockets could not be read 3 times running: stopping it (fail closed)"
           stop_named api "$API_MARKER"; exit 1
         fi ;;
      3) break ;;
    esac
    beats=$((beats + 1))
    [ $((beats % 60)) -eq 0 ] && echo "$(date '+%F %T') clean for the last 5 minutes"
    sleep 5
  done
  echo "$(date '+%F %T') API gone; watchdog exits"
}

# --------------------------------------------------------------------- state --

gen_secret() { openssl rand -hex "$1"; }

ensure_state() {
  mkdir -p "$DEV" "$LOGS" "$RUN" "$DEV/home" "$DEV/tmp" "$DEV/instruments"
  chmod 700 "$DEV"
  [ -f "$STATE" ] && return 0
  # A new state means new passwords: containers from an older one cannot be
  # signed in to, and they are throwaway by definition.
  remove_container "$DB_CONTAINER"; remove_container "$REDIS_CONTAINER"
  umask 077
  cat > "$STATE" <<EOF
# Written by scripts/dev/local-live.sh up. Local, throwaway, git-ignored.
API_URL=$API_URL
API_PORT=$API_PORT
WEB_URL=$WEB_URL
WEB_PORT=$WEB_PORT
DB_PORT=$DB_PORT
REDIS_PORT=$REDIS_PORT
DB_CONTAINER=$DB_CONTAINER
REDIS_CONTAINER=$REDIS_CONTAINER
DB_NAME=$DB_NAME
DB_USER=$DB_USER
DB_PASSWORD=$(gen_secret 16)
JWT_KEY=$(gen_secret 32)
ADMIN_USER=devadmin
ADMIN_PASSWORD=Dev-$(gen_secret 8)
TRADER_USER=devtrader
TRADER_PASSWORD=Dev-$(gen_secret 8)
SERVICE_USER=dev-engine-service
SERVICE_PASSWORD=Dev-$(gen_secret 12)
INSTRUMENTS_DIR=$DEV/instruments
EOF
  umask 022
}

# ---------------------------------------------------------------- containers --

# "<status> <label>" for a container, empty when there is none.
container_state() { docker inspect -f "{{.State.Status}} {{index .Config.Labels \"$LABEL_KEY\"}}" "$1" 2>/dev/null; }

remove_container() {
  local st
  st="$(container_state "$1")"
  [ -z "$st" ] && return 0
  case "$st" in
    *" 1") docker rm -f -v "$1" >/dev/null && ok "$1" "removed" ;;
    *) meh "$1" "exists but has no $LABEL_KEY label, so it is not this stack's; left alone" ;;
  esac
}

start_container() {  # name, then the docker run arguments after the name
  local name="$1" st; shift
  st="$(container_state "$name")"
  case "$st" in
    "running 1") ok "$name" "running" ;;
    "exited 1"|"created 1") docker start "$name" >/dev/null && ok "$name" "started again" ;;
    "") docker run -d --name "$name" --label "$LABEL_KEY=1" "$@" >/dev/null || die "docker run $name failed"
        ok "$name" "created" ;;
    *) die "A container named $name exists that is not this stack's ($st). Remove or rename it by hand." ;;
  esac
}

start_containers() {
  local db_password i
  db_password="$(state_get DB_PASSWORD)"
  start_container "$DB_CONTAINER" -p "127.0.0.1:$DB_PORT:5432" \
    -e POSTGRES_USER="$DB_USER" -e POSTGRES_PASSWORD="$db_password" -e POSTGRES_DB="$DB_NAME" "$DB_IMAGE"
  start_container "$REDIS_CONTAINER" -p "127.0.0.1:$REDIS_PORT:6379" "$REDIS_IMAGE" \
    redis-server --save "" --appendonly no
  # Over TCP inside the container: during its first-run setup Postgres
  # listens on the socket only, so a socket check would pass too early.
  for i in $(seq 1 90); do
    docker exec "$DB_CONTAINER" pg_isready -q -h 127.0.0.1 -U "$DB_USER" -d "$DB_NAME" 2>/dev/null && break
    [ "$i" -eq 90 ] && die "Postgres ($DB_CONTAINER) did not become ready; docker logs $DB_CONTAINER"
    sleep 1
  done
  for i in $(seq 1 30); do
    [ "$(docker exec "$REDIS_CONTAINER" redis-cli ping 2>/dev/null)" = "PONG" ] && break
    [ "$i" -eq 30 ] && die "Redis ($REDIS_CONTAINER) did not answer; docker logs $REDIS_CONTAINER"
    sleep 1
  done
  ok "database" "Postgres on 127.0.0.1:$DB_PORT ($DB_NAME), Redis on 127.0.0.1:$REDIS_PORT"
}

# One statement against the throwaway database, and only when the container
# is running with this stack's label.
dev_sql() {
  [ "$(container_state "$DB_CONTAINER")" = "running 1" ] || die "$DB_CONTAINER is not this stack's running database."
  docker exec "$DB_CONTAINER" psql -q -U "$DB_USER" -d "$DB_NAME" -v ON_ERROR_STOP=1 -tAc "$1"
}

# The nightly candle archive (NightlyArchiveService) asks a broker for the
# day's candles. There is no broker here and no day worth archiving, so it is
# told the archive is done until 2099. It has no switch, and it waits 90 s
# after start before its first look, which is time enough to set this.
quiet_nightly_archive() {
  if dev_sql "INSERT INTO system_settings (\"Key\", \"Value\", \"UpdatedBy\", \"Reason\", \"CreatedUtc\", \"UpdatedUtc\")
              VALUES ('archive.candles.lastDay', '2099-12-31', 'local-live.sh', 'dev stack: no nightly archive', now(), now())
              ON CONFLICT (\"Key\") DO UPDATE SET \"Value\" = EXCLUDED.\"Value\", \"UpdatedUtc\" = now();" >/dev/null; then
    ok "nightly archive" "marked done until 2099 (it would ask a broker for candles)"
  else
    meh "nightly archive" "could not be marked done; it finds no credentials and no broker regardless"
  fi
}

# ----------------------------------------------------------------------- api --

build_api() {
  echo "  building the API into .dev-live/build/api (dotnet build, a minute or two the first time)"
  dotnet build "$REPO/src/AlgoTrading.Api/AlgoTrading.Api.csproj" -c Debug -o "$BUILD" -nologo -v quiet \
    >"$LOGS/build.log" 2>&1 || { tail -n 30 "$LOGS/build.log"; die "The API did not build (log: .dev-live/logs/build.log)"; }
  # The build copies every appsettings*.json next to the dll, the real
  # appsettings.Local.json included when this checkout has one. The content
  # root is elsewhere so it would not be read; it is deleted all the same.
  rm -f "$BUILD"/appsettings*.Local.json
  [ -e "$BUILD/appsettings.Local.json" ] && die "Could not remove $BUILD/appsettings.Local.json"
  ok "build" "done"
}

prepare_content() {
  mkdir -p "$CONTENT"
  # Committed defaults only: lot sizes, paper-fill rules, logging.
  cp "$REPO/src/AlgoTrading.Api/appsettings.json" "$CONTENT/appsettings.json"
  ln -sfn "$REPO/src/AlgoTrading.Api/SeedData" "$CONTENT/SeedData"
  # Strategy specs are read from <content root>/../../docs/strategies.
  ln -sfn "$REPO/docs" "$DEV/docs"
  for f in "$CONTENT"/appsettings*.Local.json; do
    [ -e "$f" ] && die "$f exists. The dev API must never load a Local.json; remove it and run up again."
  done
}

write_api_env() {
  local problems
  problems="$(validate_overrides)" || die "scripts/dev/local-live.env is not safe to start an API with:
$problems"
  umask 077
  {
    grep -v -e '^[[:space:]]*#' -e '^[[:space:]]*$' "$OVERRIDES"
    echo "PATH=/usr/bin:/bin:/usr/sbin:/sbin"
    echo "HOME=$DEV/home"
    echo "TMPDIR=$DEV/tmp/"
    echo "LANG=en_US.UTF-8"
    echo "DOTNET_NOLOGO=1"
    echo "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "ASPNETCORE_ENVIRONMENT=Development"
    echo "ConnectionStrings__TradingDb=Host=127.0.0.1;Port=$DB_PORT;Database=$DB_NAME;Username=$DB_USER;Password=$(state_get DB_PASSWORD)"
    echo "ConnectionStrings__Redis=127.0.0.1:$REDIS_PORT"
    echo "Jwt__SecretKey=$(state_get JWT_KEY)"
    echo "Bootstrap__AdminUserName=$(state_get ADMIN_USER)"
    echo "Bootstrap__AdminEmail=$(state_get ADMIN_USER)@localhost"
    echo "Bootstrap__AdminPassword=$(state_get ADMIN_PASSWORD)"
    echo "Bootstrap__ServiceUserName=$(state_get SERVICE_USER)"
    echo "Bootstrap__ServicePassword=$(state_get SERVICE_PASSWORD)"
    echo "Cors__AllowedOrigins__0=$WEB_URL"
    echo "Cors__AllowedOrigins__1=http://localhost:$WEB_PORT"
    echo "Frontend__BaseUrl=$WEB_URL"
    echo "StrategyRunner__PythonExecutable=$REPO/scripts/dev/refuse-python.sh"
    echo "StrategyRunner__EngineDirectory=$REPO/src/AlgoTrading.PythonEngine"
    echo "Instruments__MasterDirectory=$DEV/instruments"
    echo "Desk__PlanFile=$REPO/config/morning-plan.txt"
    echo "DEV_LIVE_REFUSED_LOG=$LOGS/refused-python.log"
  } > "$API_ENV"
  umask 022
}

start_api() {
  local dotnet line pid i
  local -a env_args=()
  dotnet="$(command -v dotnet)"; dotnet="$(cd "$(dirname "$dotnet")" && pwd -P)/$(basename "$dotnet")"
  [ -n "$(port_listener "$API_PORT")" ] && die "Port $API_PORT is taken by something else (lsof -nP -iTCP:$API_PORT)."
  while IFS= read -r line; do env_args+=("$line"); done < "$API_ENV"
  pid="$(cd "$CONTENT" && detach "$LOGS/api.log" env -i "${env_args[@]}" "$dotnet" "$API_DLL" \
          --contentRoot "$CONTENT" --urls "$API_URL")"
  echo "$pid" > "$RUN/api.pid"
  echo "  API starting (pid $pid, migrations run first; log .dev-live/logs/api.log)"
  for i in $(seq 1 240); do
    if ! kill -0 "$pid" 2>/dev/null; then
      tail -n 40 "$LOGS/api.log"; die "The API exited while starting (log above)."
    fi
    # Checked while it starts, too: a leak at startup must not wait for the end.
    if api_pid >/dev/null; then isolation_check starting; [ $? -eq 1 ] && die "Stopped: see the isolation check above."; fi
    [ "$(curl -s -o /dev/null -w '%{http_code}' --noproxy '*' --max-time 2 "$API_URL/api/Backend/status")" = "200" ] && break
    [ "$i" -eq 240 ] && die "The API did not answer in 4 minutes (log: .dev-live/logs/api.log)."
    sleep 1
  done
  ok "api" "$API_URL (pid $pid, ready in ${i} s)"
}

# ----------------------------------------------------------------------- web --

start_web() {
  local node pid i
  node="$(command -v node)" || die "node is not installed"
  if [ ! -d "$REPO/web/node_modules" ]; then
    echo "  installing the web dependencies (npm ci)"
    (cd "$REPO/web" && npm ci --no-audit --no-fund >"$LOGS/npm.log" 2>&1) || die "npm ci failed (log: .dev-live/logs/npm.log)"
  fi
  [ -n "$(port_listener "$WEB_PORT")" ] && die "Port $WEB_PORT is taken by something else (lsof -nP -iTCP:$WEB_PORT)."
  pid="$(cd "$REPO/web" && detach "$LOGS/web.log" env "VITE_API_BASE_URL=$API_URL" \
          "$node" node_modules/vite/bin/vite.js --host 127.0.0.1 --port "$WEB_PORT" --strictPort)"
  echo "$pid" > "$RUN/web.pid"
  for i in $(seq 1 60); do
    kill -0 "$pid" 2>/dev/null || { tail -n 20 "$LOGS/web.log"; die "The web dev server exited."; }
    [ "$(curl -s -o /dev/null -w '%{http_code}' --noproxy '*' --max-time 2 "$WEB_URL/")" = "200" ] && break
    [ "$i" -eq 60 ] && die "The web dev server did not answer (log: .dev-live/logs/web.log)."
    sleep 1
  done
  ok "web" "$WEB_URL (vite, pid $pid)"
}

# ------------------------------------------------------------------ commands --

preflight() {
  local cmd
  for cmd in docker dotnet node python3 lsof perl curl openssl pgrep awk; do
    command -v "$cmd" >/dev/null 2>&1 || die "$cmd is needed and not installed."
  done
  docker info >/dev/null 2>&1 || die "Docker is not running."
  for p in "$API_PORT" "$WEB_PORT" "$DB_PORT" "$REDIS_PORT"; do
    case "$p" in ''|*[!0-9]*) die "Port '$p' is not a number." ;; esac
    # The Mac's own stack: its API, its databases and caches, its dashboards.
    case "$p" in 5025|5173|5432|5433|6379|6380|3000|9090) die "Port $p belongs to the Mac's own stack; pick another (DEV_*_PORT)." ;; esac
  done
  [ -n "$(port_listener "$WALL_PORT")" ] && die "Something listens on 127.0.0.1:$WALL_PORT, the port every broker address points at. It must stay closed."
  return 0
}

up() {
  local no_web="" no_replay="" no_seed="" arg pid
  for arg in "$@"; do
    case "$arg" in
      --no-web) no_web=1 ;; --no-replay) no_replay=1 ;; --no-seed) no_seed=1 ;;
      *) die "Unknown option $arg (up takes --no-web, --no-replay, --no-seed)." ;;
    esac
  done
  preflight

  step "Throwaway database and cache"
  ensure_state
  start_containers

  step "API (isolated: no credentials, no Python, no broker addresses)"
  if pid="$(api_pid)"; then
    ok "api" "already running (pid $pid)"
  else
    build_api
    prepare_content
    write_api_env
    start_api
    quiet_nightly_archive
  fi
  verify_isolation || die "The API was stopped: see the isolation check above."
  if ! watchdog_pid >/dev/null; then
    pid="$(detach "$LOGS/watchdog.log" "$SELF" _watchdog)"
    echo "$pid" > "$RUN/watchdog.pid"
    ok "watchdog" "re-checks every 5 s (pid $pid, log .dev-live/logs/watchdog.log)"
  fi

  step "Accounts, instruments, watchlist"
  python3 "$REPO/scripts/dev/seed-positions.py" --setup || die "Setup failed (above)."

  if [ -z "$no_web" ]; then
    step "Console"
    if pid="$(web_pid)"; then ok "web" "already running (pid $pid)"; else start_web; fi
  fi

  if [ -z "$no_replay" ]; then
    step "Synthetic ticks"
    if pid="$(replay_pid)"; then
      ok "replay" "already running (pid $pid)"
    else
      pid="$(detach "$LOGS/replay.log" python3 "$REPLAY_MARKER" --quiet)"
      echo "$pid" > "$RUN/replay.pid"
      sleep 2
      kill -0 "$pid" 2>/dev/null || { tail -n 20 "$LOGS/replay.log"; die "The replay exited."; }
      ok "replay" "20 ticks/s (pid $pid, log .dev-live/logs/replay.log)"
    fi
  fi

  if [ -z "$no_seed" ]; then
    step "Paper positions"
    python3 "$REPO/scripts/dev/seed-positions.py" || meh "seed" "did not complete (above); the stack is up regardless"
  fi

  step "Isolation, once more"
  verify_isolation || die "The API was stopped: see the isolation check above."
  summary
}

summary() {
  printf '\n%bLocal live stack is up%b  %b(nothing here can reach a broker)%b\n\n' "$B" "$N" "$D" "$N"
  if web_pid >/dev/null; then
    printf '  Console   %s\n' "$WEB_URL"
  else
    printf '  Console   %bnot started (run up without --no-web)%b\n' "$D" "$N"
  fi
  printf '  API       %s   %b(swagger: %s/swagger)%b\n' "$API_URL" "$D" "$API_URL" "$N"
  printf '  Sign in   %-18s %s   %badmin%b\n' "$(state_get ADMIN_USER)" "$(state_get ADMIN_PASSWORD)" "$D" "$N"
  printf '            %-18s %s   %btrader%b\n' "$(state_get TRADER_USER)" "$(state_get TRADER_PASSWORD)" "$D" "$N"
  printf '  Ticks     python3 scripts/dev/replay-ticks.py --help\n'
  printf '  Trades    python3 scripts/dev/seed-positions.py --fill   |   --close\n'
  printf '  Check     scripts/dev/local-live.sh status   %b(the watchdog also checks every 5 s)%b\n' "$D" "$N"
  printf '  Stop      scripts/dev/local-live.sh down\n\n'
}

status() {
  local pid st count
  step "Local live stack"
  for c in "$DB_CONTAINER" "$REDIS_CONTAINER"; do
    st="$(container_state "$c")"
    case "$st" in "running 1") ok "$c" "running" ;; "") bad "$c" "not there" ;; *) meh "$c" "$st" ;; esac
  done
  if pid="$(api_pid)"; then ok "api" "$API_URL (pid $pid)"; else bad "api" "not running"; fi
  if pid="$(web_pid)"; then ok "web" "$WEB_URL (pid $pid)"; else meh "web" "not running"; fi
  if pid="$(replay_pid)"; then ok "replay" "pid $pid — $(tail -n 1 "$LOGS/replay.log" 2>/dev/null)"; else meh "replay" "not running"; fi
  if pid="$(watchdog_pid)"; then ok "watchdog" "pid $pid"; else meh "watchdog" "not running"; fi
  if [ -f "$LOGS/refused-python.log" ]; then
    count="$(wc -l < "$LOGS/refused-python.log" | tr -d ' ')"
    meh "python refused" "$count launch(es) the API asked for and did not get (.dev-live/logs/refused-python.log)"
  fi
  if api_pid >/dev/null; then
    step "Isolation"
    isolation_check
    [ -f "$STATE" ] && summary
  fi
}

down() {
  step "Stopping the local live stack"
  stop_named replay "$REPLAY_MARKER"
  stop_named watchdog "$WATCHDOG_MARKER"
  stop_named web "$WEB_MARKER"
  stop_named api "$API_MARKER"
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    remove_container "$DB_CONTAINER"
    remove_container "$REDIS_CONTAINER"
  else
    meh "docker" "not running; containers (if any) left as they are"
  fi
  rm -rf "${RUN:?}" "${STATE:?}" "${API_ENV:?}" "${DEV:?}/home" "${DEV:?}/tmp" "${DEV:?}/instruments" \
         "${DEV:?}/universe.json" "${DEV:?}/content" "${DEV:?}/docs"
  ok "state" "removed (logs and the build are kept under .dev-live/)"
  echo
}

case "${1:-}" in
  up)        shift; up "$@" ;;
  down)      down ;;
  status)    status ;;
  check)     verify_isolation; rc=$?; [ "$rc" -eq 3 ] && exit 0; exit "$rc" ;;
  _watchdog) watchdog ;;
  _classify) classify ;;
  _validate) validate_overrides ;;
  *) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
