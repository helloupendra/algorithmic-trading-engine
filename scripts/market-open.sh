#!/usr/bin/env bash
# Brings the platform up for a trading day, unattended.
#
# Run by launchd before the 09:15 IST open (see scripts/install-market-open.sh).
# Everything it does is paper: this platform has no order-placement path at all
# — the only run modes are LivePaper and OfflineReplay — so an unattended start
# risks no money, only a wasted morning.
#
# It writes one report per day to logs/market-open-YYYY-MM-DD.log and prints the
# same lines, so the overnight result is readable in one place.
#
# Usage: ./scripts/market-open.sh [--dry-run]

set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/market-open-$(date +%F).log"
. scripts/lib/desk-common.sh

DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

# Space-separated: one live run is started per underlying. The chain poller
# covers the same set (CHAIN_UNDERLYINGS, set for the API by the library).
UNDERLYINGS="${MARKET_OPEN_UNDERLYINGS:-BANKNIFTY NIFTY SENSEX}"
export CHAIN_UNDERLYINGS="$(printf '%s' "$UNDERLYINGS" | tr ' ' ',')"
STRATEGY="${MARKET_OPEN_STRATEGY:-GhostTangentCrossings}"
LOTS="${MARKET_OPEN_LOTS:-2}"
# Clock time (HHMM, IST) to stop waiting for the morning FYERS sign-in.
#
# Late in the session rather than a short window: FYERS expires its token at
# 06:00 sharp and disables refresh to comply with SEBI, so the sign-in cannot
# happen the night before and cannot be automated. The one thing this script
# CAN do is be ready the instant it happens — whether that is 08:50 or 11:30 —
# instead of giving up and leaving the day unrun.
LOGIN_WAIT_UNTIL="${MARKET_OPEN_LOGIN_UNTIL:-1430}"
# Where the operator actually looks. The Vite dev server when it is up, else the
# API, which serves the built console from wwwroot after scripts/go-live.sh.
CONSOLE="${CONSOLE_URL:-}"
if [ -z "$CONSOLE" ]; then
  if curl -sf -o /dev/null http://localhost:5173 2>/dev/null; then CONSOLE=http://localhost:5173; else CONSOLE="$API"; fi
fi

fail() { say "FAILED: $1"; say "--- stopping here; nothing further was started ---"; exit 1; }

say "=== market-open: $(date '+%A %d %B %Y') ==="
[ "$DRY_RUN" = 1 ] && say "(dry run — nothing will be started)"

# --- 0. is it even a trading day? -------------------------------------------
DOW="$(date +%u)"   # 1=Mon .. 7=Sun
if [ "$DOW" -ge 6 ]; then
  say "Weekend — the market is closed. Nothing to do."
  exit 0
fi

# --- 1. credentials ----------------------------------------------------------
load_env || fail ".env is missing or has no ADMIN_USERNAME / ADMIN_PASSWORD."

# --- 2. infrastructure -------------------------------------------------------
say "starting infra (TimescaleDB, Redis) ..."
docker compose up -d --wait timescaledb redis >>"$LOG" 2>&1 \
  || fail "docker compose could not start the database or Redis."
say "  infra up"

# --- 3. the API --------------------------------------------------------------
# Restarted rather than reused, so today's CHAIN_UNDERLYINGS reach the poller
# it spawns and today's console bundle is what the domain serves.
web_build || true
api_restart || fail "the API did not come up."

TOKEN="$(admin_token)" || true
[ -n "$TOKEN" ] || fail "could not sign in to the API as $ADMIN_USERNAME."
AUTH="Authorization: Bearer $TOKEN"

# --- 4. the broker token, which expires daily --------------------------------
connected() {
  curl -fsS "$API/api/auth/session" -H "$AUTH" 2>/dev/null | grep -q '"isAuthenticated":true'
}

if connected; then
  say "FYERS session already valid"
else
  # Try the refresh anyway: it costs one call, and it starts working the day
  # FYERS re-enables the API. Today it answers "disabled to comply with SEBI
  # regulations" — the daily sign-in is a regulator's requirement, not a gap
  # in this platform, and no amount of code removes it.
  say "FYERS token expired — trying a refresh ..."
  REFRESH="$(curl -sS -X POST "$API/api/auth/refresh-token" -H "$AUTH" -H 'Content-Type: application/json' -d '{}' 2>/dev/null)"

  if connected; then
    say "  token renewed"
  else
    say "  refresh unavailable: $(printf '%s' "$REFRESH" | head -c 140)"
    say "  waiting for the FYERS sign-in — will keep watching until ${LOGIN_WAIT_UNTIL} IST"
    say "  (the token expired at 06:00; FYERS fixes that hour and has disabled refresh)"

    # Put the login in front of the operator rather than in a log they have to
    # go looking for: open the console and raise a notification.
    open "$CONSOLE/admin/data/connectors" 2>/dev/null || true
    osascript -e 'display notification "Sign in to FYERS — the morning run is waiting." with title "AlgoTrading" sound name "Submarine"' 2>/dev/null || true

    NUDGED_OPEN=0
    LAST_NUDGE=$(date +%s)
    while ! connected; do
      NOW="$(date +%H%M)"
      if [ "$NOW" -ge "$LOGIN_WAIT_UNTIL" ]; then
        fail "no FYERS sign-in by ${LOGIN_WAIT_UNTIL} IST; nothing was started."
      fi

      # A louder reminder at the open itself, then one every ten minutes: the
      # first notification is easy to sleep through, and every minute waited is
      # a minute of the session gone.
      if [ "$NUDGED_OPEN" = 0 ] && [ "$NOW" -ge 0915 ]; then
        osascript -e 'display notification "Market is OPEN and FYERS is not signed in — nothing is trading." with title "AlgoTrading" sound name "Sosumi"' 2>/dev/null || true
        NUDGED_OPEN=1
        LAST_NUDGE=$(date +%s)
      elif [ $(( $(date +%s) - LAST_NUDGE )) -ge 600 ]; then
        osascript -e 'display notification "Still waiting for the FYERS sign-in." with title "AlgoTrading" sound name "Submarine"' 2>/dev/null || true
        LAST_NUDGE=$(date +%s)
      fi

      sleep 20
    done
    say "  signed in at $(date '+%H:%M') — carrying on"
  fi
fi

# --- 5. market data ----------------------------------------------------------
start_daemon() {
  label="$1"; path="$2"
  body="${3:-{\}}"
  say "starting $label ..."
  out="$(curl -sS -X POST "$API$path" -H "$AUTH" -H 'Content-Type: application/json' -d "$body")"
  say "  $label: $(printf '%s' "$out" | head -c 160)"
}

if [ "$DRY_RUN" = 1 ]; then
  say "dry run: would start the ingestor, the chain poller, and $STRATEGY on:$(printf ' %s' $UNDERLYINGS) at $LOTS lot(s)"
  exit 0
fi

start_daemon "tick ingestor" "/api/Ingestor/start"

# The poller start endpoint takes no body: it reads CHAIN_UNDERLYINGS from the
# environment the API handed it, which was exported above.
start_daemon "chain poller ($CHAIN_UNDERLYINGS)" "/api/OptionChain/poller/start"

# --- 6. wait for the open, then prove the feed is live -----------------------
if [ "$(date +%H%M)" -lt 0916 ]; then
  say "waiting for the 09:15 open ..."
  while [ "$(date +%H%M)" -lt 0916 ]; do sleep 20; done
  say "market open — checking the feed"
else
  # A late sign-in: the session is already under way, so there is nothing to
  # wait for. Said plainly, because the log is the record of what happened.
  say "signed in after the open — starting into a session already running"
fi

# Long enough for the ingestor to have subscribed and the first ticks to land.
sleep 90

TICKS="$(curl -fsS "$API/api/LiveData/latest/all" -H "$AUTH" 2>/dev/null \
  | tr ',' '\n' | grep -c 'lastTradedPrice' || true)"
say "  symbols carrying a price: ${TICKS:-0}"

if [ "${TICKS:-0}" -lt 1 ]; then
  fail "no live prices after the ingestor started — the feed is not flowing."
fi

# --- 7. the strategy ---------------------------------------------------------
SID="$(curl -fsS "$API/api/Strategy" -H "$AUTH" \
  | tr '{' '\n' | grep "\"name\":\"$STRATEGY\"" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)"

if [ -z "$SID" ]; then
  fail "strategy '$STRATEGY' is not in the catalogue."
fi

# Already-running underlyings are left alone: a second launchd fire, or a hand
# re-run after a stumble, must not double the position.
RUNNING="$(curl -fsS "$API/api/Strategy/runs?status=Running" -H "$AUTH" 2>/dev/null || echo '')"

# One run per underlying. A failure on one is reported and the others still go:
# losing SENSEX should not cost the BANKNIFTY session too.
STARTED=0
SKIPPED=0
for U in $UNDERLYINGS; do
  if printf '%s' "$RUNNING" | grep -q "\"underlying\":\"$U\""; then
    say "$U already has a running $STRATEGY — leaving it alone"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  say "deploying $STRATEGY (id $SID) on $U, $LOTS lot(s) — paper"
  RUN="$(curl -sS -X POST "$API/api/Strategy/$SID/start" -H "$AUTH" -H 'Content-Type: application/json' \
    -d "{\"underlying\":\"$U\",\"lots\":$LOTS}")"
  say "  $(printf '%s' "$RUN" | head -c 220)"
  case "$RUN" in *'"runId"'*) STARTED=$((STARTED + 1)) ;; esac
  sleep 2
done

say "=== $STARTED started, $SKIPPED already running, of $(printf '%s' "$UNDERLYINGS" | wc -w | tr -d ' ') ==="
say "Watch them at $CONSOLE/admin/strategies/live — or read this file."
