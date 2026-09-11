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
# Risk rules every run is deployed with. Per leg, in premium points from its
# own entry: the risk guard closes that leg alone once it has made
# LEG_TARGET_PTS (and, if set, once it has lost LEG_STOP_PTS). Nothing at the
# day level unless DAY_TARGET / DAY_STOP_LOSS (rupees on the day's total P&L)
# are given. The owner's brief for Ghost (2026-09-09): "target = entry + 20
# points, leave the stop-loss empty". Rules can still be edited on a live run.
LEG_TARGET_PTS="${MARKET_OPEN_LEG_TARGET_PTS:-20}"
LEG_STOP_PTS="${MARKET_OPEN_LEG_STOP_PTS:-}"
DAY_TARGET="${MARKET_OPEN_DAY_TARGET:-}"
DAY_STOP_LOSS="${MARKET_OPEN_DAY_STOP_LOSS:-}"
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

# Proves the credentials work now; every later call re-mints through
# auth_token() rather than carrying this one. The admin JWT lives 60 minutes
# and the sign-in wait below can run past 14:30 — see desk-common.sh.
auth_token >/dev/null || fail "could not sign in to the API as $ADMIN_USERNAME."

# --- 4. the broker token, which expires daily --------------------------------
connected() {
  api_get /api/auth/session 2>/dev/null | grep -q '"isAuthenticated":true'
}

if connected; then
  say "FYERS session already valid"
else
  # Try the refresh anyway: it costs one call, and it starts working the day
  # FYERS re-enables the API. Today it answers "disabled to comply with SEBI
  # regulations" — the daily sign-in is a regulator's requirement, not a gap
  # in this platform, and no amount of code removes it.
  say "FYERS token expired — trying a refresh ..."
  REFRESH="$(api_post /api/auth/refresh-token '{}' 2>/dev/null || true)"

  if connected; then
    say "  token renewed"
  else
    say "  refresh unavailable: $(printf '%s' "$REFRESH" | head -c 140)"
    say "  waiting for the FYERS sign-in — will keep watching until ${LOGIN_WAIT_UNTIL} IST"
    say "  (the token expired at 06:00; FYERS fixes that hour and has disabled refresh)"

    # Put the login in front of the operator rather than in a log they have to
    # go looking for: raise a notification, and on the Mac open the page too.
    if $IS_MAC; then open "$CONSOLE/admin/data/connectors" 2>/dev/null || true; fi
    notify "AlgoTrading" "Sign in to FYERS at $CONSOLE — the morning run is waiting."

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
        notify "AlgoTrading" "Market is OPEN and FYERS is not signed in — nothing is trading."
        NUDGED_OPEN=1
        LAST_NUDGE=$(date +%s)
      elif [ $(( $(date +%s) - LAST_NUDGE )) -ge 600 ]; then
        notify "AlgoTrading" "Still waiting for the FYERS sign-in."
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
  out="$(api_post "$path" "$body" || true)"
  say "  $label: $(printf '%s' "$out" | head -c 160)"
}

if [ "$DRY_RUN" = 1 ]; then
  say "dry run: would start the ingestor, the chain poller, and $STRATEGY on:$(printf ' %s' $UNDERLYINGS) at $LOTS lot(s)"
  exit 0
fi

# Fresh daemons, never "already running": a feed that lived through the night
# (the MCX session runs to 23:30) holds a token that expired at 06:00 and
# would sit deaf all day while the API reports it healthy. Stop first — a
# stop with nothing running is a no-op — then start with today's token.
stop_daemon() {  # label, path
  out="$(api_post "$2" '{}' 2>/dev/null || true)"
  case "$out" in *'"wasRunning":true'*) say "  $1 from before the open stopped — restarting it on today's token";; esac
}
stop_daemon "tick ingestor" "/api/Ingestor/stop"
stop_daemon "chain poller" "/api/OptionChain/poller/stop"

# Last week's weekly options are still on the recording list, and FYERS answers
# a subscribe that contains them with -300 "Please provide a valid symbol",
# naming them. On 2026-09-11 that was all 36 SENSEX strikes of the 09-10
# expiry: the rest of the batch still subscribed, but SENSEX options recorded
# nothing all day and the error hid in the log. Only rows the API itself calls
# "expired" (its instrument expiry is behind today's IST date) are removed —
# never a "silent" one, which before the feed is up would mean every symbol.
prune_expired_watchlist() {
  local stale parsed count body
  stale="$(api_get /api/LiveData/watchlist/stale 2>/dev/null)" || {
    warn "could not read the recording list — leaving it as it is"
    return 0
  }
  # Two lines out: how many rows, then the request body naming exactly those.
  parsed="$(printf '%s' "$stale" | python3 -c '
import json, sys
try:
    rows = (json.load(sys.stdin) or {}).get("items") or []
except Exception:
    rows = []
ids = [r["id"] for r in rows if r.get("reason") == "expired" and r.get("id")]
print(len(ids))
print(json.dumps({"ids": ids}))
' 2>/dev/null)" || parsed=""
  count="$(printf '%s\n' "$parsed" | sed -n 1p)"
  body="$(printf '%s\n' "$parsed" | sed -n 2p)"
  if [ -z "$count" ] || [ "$count" = 0 ]; then
    say "  recording list: no expired contracts"
    return 0
  fi
  if api_post /api/LiveData/watchlist/prune "$body" >/dev/null 2>&1; then
    say "  recording list: dropped $count expired contract(s)"
  else
    warn "could not drop $count expired contract(s) from the recording list"
  fi
}
say "checking the recording list for expired contracts ..."
prune_expired_watchlist

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

# Only quotes updated in the last few minutes count. The table keeps every
# symbol's LAST price forever, so on 2026-09-10 it showed 127 "prices" from
# the previous evening while the ingestor sat on an expired token and
# nothing was flowing. The date-time filter is done in python on purpose:
# the JSON is one line and grep cannot tell today's stamp from yesterday's.
TICKS="$(api_get /api/LiveData/latest/all 2>/dev/null | python3 -c '
import json, sys
from datetime import datetime, timedelta, timezone
try:
    rows = json.load(sys.stdin)
except Exception:
    rows = []
if isinstance(rows, dict):
    rows = rows.get("items") or rows.get("quotes") or rows.get("data") or []
cutoff = datetime.now(timezone.utc) - timedelta(minutes=5)
fresh = 0
for r in rows:
    stamp = r.get("updatedUtc") or r.get("receivedUtc") or ""
    try:
        at = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
        if at.tzinfo is None:
            at = at.replace(tzinfo=timezone.utc)
    except Exception:
        continue
    if at >= cutoff and r.get("lastTradedPrice") is not None:
        fresh += 1
print(fresh)
' 2>/dev/null || echo 0)"
say "  symbols with a price in the last 5 minutes: ${TICKS:-0}"

if [ "${TICKS:-0}" -lt 1 ]; then
  fail "no fresh prices after the ingestor started — the feed is not flowing (check the broker token: FYERS expires it at 06:00 IST)."
fi

# --- 7. the strategy ---------------------------------------------------------
SID="$(api_get /api/Strategy \
  | tr '{' '\n' | grep "\"name\":\"$STRATEGY\"" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)"

if [ -z "$SID" ]; then
  fail "strategy '$STRATEGY' is not in the catalogue."
fi

# Already-running underlyings are left alone: a second launchd fire, or a hand
# re-run after a stumble, must not double the position.
RUNNING="$(api_get '/api/Strategy/runs?status=Running' 2>/dev/null || echo '')"

# One run per underlying. A failure on one is reported and the others still go:
# losing SENSEX should not cost the BANKNIFTY session too.
STARTED=0
SKIPPED=0
# The rules as the API's RiskRulesDto (camelCase; leg rules in premium
# points, day rules in rupees with scope "day"), and a sentence for the log.
RISK_JSON="$(LEG_TARGET_PTS="$LEG_TARGET_PTS" LEG_STOP_PTS="$LEG_STOP_PTS" DAY_TARGET="$DAY_TARGET" DAY_STOP_LOSS="$DAY_STOP_LOSS" python3 - <<'PYEOF'
import json, os
def num(k):
    v = os.environ.get(k, "").strip()
    return float(v) if v else None
leg = {k: v for k, v in {"targetPoints": num("LEG_TARGET_PTS"), "stopLossPoints": num("LEG_STOP_PTS")}.items() if v}
day = {k: v for k, v in {"target": num("DAY_TARGET"), "stopLoss": num("DAY_STOP_LOSS")}.items() if v}
risk = {}
if leg: risk["leg"] = leg
if day: risk["overall"] = {**day, "scope": "day"}
print(json.dumps(risk))
PYEOF
)"
RISK_TEXT="leg target ${LEG_TARGET_PTS:-none} pts / leg SL ${LEG_STOP_PTS:-none}${DAY_TARGET:+, day target ₹$DAY_TARGET}${DAY_STOP_LOSS:+, day SL ₹$DAY_STOP_LOSS}"
say "risk rules for every run: $RISK_TEXT  ($RISK_JSON)"

for U in $UNDERLYINGS; do
  if printf '%s' "$RUNNING" | grep -q "\"underlying\":\"$U\""; then
    say "$U already has a running $STRATEGY — leaving it alone"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  say "deploying $STRATEGY (id $SID) on $U, $LOTS lot(s), $RISK_TEXT — paper"
  RUN="$(api_post "/api/Strategy/$SID/start" \
    "{\"underlying\":\"$U\",\"lots\":$LOTS,\"risk\":$RISK_JSON}" || true)"
  say "  $(printf '%s' "$RUN" | head -c 220)"
  case "$RUN" in *'"runId"'*) STARTED=$((STARTED + 1)) ;; esac
  sleep 2
done

say "=== $STARTED started, $SKIPPED already running, of $(printf '%s' "$UNDERLYINGS" | wc -w | tr -d ' ') ==="
say "Watch them at $CONSOLE/admin/strategies/live — or read this file."
