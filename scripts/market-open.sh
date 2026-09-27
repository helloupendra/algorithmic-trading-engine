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

# Space-separated: the underlyings the feed and the chain poller cover. The
# chain poller covers the same set (CHAIN_UNDERLYINGS, set for the API by the
# library). Commodities are not in here: the chain poller does not follow them.
UNDERLYINGS="${MARKET_OPEN_UNDERLYINGS:-BANKNIFTY NIFTY SENSEX}"
export CHAIN_UNDERLYINGS="$(printf '%s' "$UNDERLYINGS" | tr ' ' ',')"

# The morning's plan: what to run, on what, at how many lots, and — where it
# should differ from the default — that strategy's own leg target in premium
# points.
#
#   Strategy  UNDERLYING[,UNDERLYING...]  lots  [legTargetPoints]
#
# It lives in config/morning-plan.txt (overridable with MARKET_OPEN_PLAN_FILE, or
# MARKET_OPEN_PLAN for the lines themselves).
#
# The fourth column matters: the 20-point default leg target was written for
# Ghost, which buys a single option. A straddle's legs and a crude option's
# premium are not the same animal, so a strategy that wants a different number
# says so here rather than inheriting one that happens to be there. An empty
# fourth column means "no leg target at all" — write `-` for that, and leave it
# off to take the default.
#
# Every account in MARKET_OPEN_ACCOUNTS gets the whole plan, so the number of
# runners started is accounts x lines x underlyings. Each runner is its own
# Python process; the API refuses to go past StrategyRunner:MaxConcurrentProcesses.
PLAN_FILE="${MARKET_OPEN_PLAN_FILE:-$REPO_ROOT/config/morning-plan.txt}"
if [ -z "${MARKET_OPEN_PLAN:-}" ] && [ -f "$PLAN_FILE" ]; then
  # The plan and its accounts come from one file, which Sentinel also reads to
  # check afterwards that everything asked for is actually running.
  PLAN="$(grep -v '^[[:space:]]*#' "$PLAN_FILE" | grep -v '^[[:space:]]*$' | grep -vi '^[[:space:]]*accounts:')"
  PLAN_ACCOUNTS="$(grep -i '^[[:space:]]*accounts:' "$PLAN_FILE" | head -1 | cut -d: -f2-)"
else
  PLAN="${MARKET_OPEN_PLAN:-}"
  PLAN_ACCOUNTS=""
fi
# A missing plan is a morning with no runs. That is said on the phone, not
# left as one line in desk.log for the evening.
if [ -z "$PLAN" ]; then
  echo "market-open: no plan — $PLAN_FILE is missing or empty and MARKET_OPEN_PLAN is unset" >&2
  notify "AlgoTrading" "Morning run FAILED: no plan ($PLAN_FILE is missing or empty). Nothing was started."
  exit 1
fi

# Platform user names whose accounts the plan is deployed into. The script signs
# in as the admin and names the owner on each start, so no trader's password is
# held anywhere. A name that does not exist is reported and the rest still run.
# No accounts at all is refused rather than quietly read as "admin only".
ACCOUNTS="${MARKET_OPEN_ACCOUNTS:-$PLAN_ACCOUNTS}"
if [ -z "$(printf '%s' "$ACCOUNTS" | tr -d '[:space:]')" ]; then
  echo "market-open: the plan names no accounts — add an 'accounts:' line to $PLAN_FILE" >&2
  notify "AlgoTrading" "Morning run FAILED: the plan names no accounts. Nothing was started."
  exit 1
fi

# Lots for a plan line that leaves them out.
LOTS_DEFAULT="${MARKET_OPEN_LOTS:-2}"
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

# A failed morning is said on the phone, not only in a log nobody reads until
# the evening: on 11 and 14 Sep "no FYERS sign-in by 1430" was one WARN line.
fail() {
  say "FAILED: $1"
  say "--- stopping here; nothing further was started ---"
  notify "AlgoTrading" "Morning run FAILED: $1"
  exit 1
}

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

# --- 1b. not a second time under live runs ------------------------------------
# This job restarts the API and the feeds. At 08:45 nothing is running; once
# the session is under way, a second run would restart them under the day's
# runners, the failure of 22 and 24 Sep. It is exactly what a short morning
# tally, or a Sentinel incident, tempts someone to do, so it is refused here;
# a missing run is started from Strategies > Live runner instead.
if [ "$DRY_RUN" != 1 ] && [ "$((10#$(date +%H%M)))" -ge 915 ] && [ -z "${MARKET_OPEN_FORCE:-}" ]; then
  LIVE_NOW="$(live_runs)"
  if [ "${LIVE_NOW:--1}" -gt 0 ] 2>/dev/null; then
    say "$LIVE_NOW strategy run(s) are live — not running the morning job again under them"
    say "start a missing run from Strategies > Live runner; MARKET_OPEN_FORCE=1 overrides (restarts the API and feeds)"
    notify "AlgoTrading" "Morning job not re-run: $LIVE_NOW runs are live and it would restart the API and feeds under them. Start missing runs from Strategies > Live runner."
    exit 0
  fi
fi

# --- 2. infrastructure -------------------------------------------------------
if [ "$DRY_RUN" = 1 ]; then
  # A dry run starts nothing, and that includes the API: on 23 Sep five dry
  # runs restarted it five times in ten minutes.
  say "dry run: would start infra, rebuild the console and restart the API"
else
say "starting infra (TimescaleDB, Redis) ..."
docker compose up -d --wait timescaledb redis >>"$LOG" 2>&1 \
  || fail "docker compose could not start the database or Redis."
say "  infra up"

# --- 3. the API --------------------------------------------------------------
# Restarted rather than reused, so today's CHAIN_UNDERLYINGS reach the poller
# it spawns and today's console bundle is what the domain serves.
web_build || true
api_restart || fail "the API did not come up."
fi

# Proves the credentials work now; every later call re-mints through
# auth_token() rather than carrying this one. The admin JWT lives 60 minutes
# and the sign-in wait below can run past 14:30 — see desk-common.sh.
auth_token >/dev/null || fail "could not sign in to the API as $ADMIN_USERNAME."

# --- 3b. is the exchange open today? -----------------------------------------
# Weekends are caught above without the API; holidays need the exchanges' own
# calendar, which the API holds (System > Market calendar). On 2026-09-14,
# Ganesh Chaturthi, this script could not tell: it restarted everything and
# waited for a FYERS sign-in until 14:30 on a day NSE never opened.
SESSION_JSON="$(api_get "/api/MarketSession/check?exchange=NSE&segment=CM" 2>/dev/null || true)"
read_session() {  # field -> value, empty when the answer is missing or unreadable
  printf '%s' "$SESSION_JSON" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
except Exception:
    sys.exit(0)
field = sys.argv[1]
if field == "holiday":
    print((d.get("holidayName") or "a non-trading day") if d.get("isTradingDay") is False else "")
else:
    print(d.get(field) or "")
' "$1" 2>/dev/null
}

if [ -z "$SESSION_JSON" ]; then
  warn "could not ask the API whether today is a trading day — carrying on as an ordinary weekday"
else
  CALENDAR_WARNING="$(read_session calendarWarning)"
  if [ -n "$CALENDAR_WARNING" ]; then
    warn "$CALENDAR_WARNING"
    notify "AlgoTrading" "Holiday calendar: $CALENDAR_WARNING Add it under System > Market calendar."
  fi

  HOLIDAY="$(read_session holiday)"
  if [ -n "$HOLIDAY" ]; then
    say "Exchange holiday — NSE is closed today ($HOLIDAY). Nothing to start."
    notify "AlgoTrading" "Market holiday today: $HOLIDAY. NSE is closed; the desk is not starting feeds or strategies."
    exit 0
  fi
fi

start_daemon() {
  label="$1"; path="$2"
  body="${3:-{\}}"
  say "starting $label ..."
  out="$(api_post "$path" "$body" || true)"
  say "  $label: $(printf '%s' "$out" | head -c 160)"
}

stop_daemon() {  # label, path
  out="$(api_post "$2" '{}' 2>/dev/null || true)"
  case "$out" in *'"wasRunning":true'*) say "  $1 from before the open stopped — restarting it on today's token";; esac
}

# --- 3c. Dhan: the day's market data -----------------------------------------
# Dhan is the primary feed when it is signed in: ticks with five-level depth,
# OI and volume for the indices, near futures and at-the-money options, and its
# option chain (OI, IV, greeks) recorded every minute. FYERS stays the backup.
#
# Only ONE live feed runs. Bars and latest quotes are kept per symbol, not per
# vendor, so two feeds on the same contract would build one bar out of two
# vendors' volume counters. When Dhan is up the FYERS feed and its chain poller
# are not started; when Dhan cannot start, they are, exactly as before.
#
# Dhan's token lasts 24 hours from the sign-in (Connectors > Dhan > Connect).
# This runs before the FYERS check, so a missing FYERS sign-in never costs the
# day's data, and when Dhan is up it does not hold the strategies back either
# (step 4).
DHAN_PRIMARY=0
DHAN_WAIT_UNTIL="${MARKET_OPEN_DHAN_UNTIL:-0912}"

dhan_state() {  # prints "ok|<hours of token left, or ?>" or "no|<reason>"
  api_get /api/Dhan/status 2>/dev/null | python3 -c '
import json, sys
from datetime import datetime, timedelta, timezone
IST = timezone(timedelta(hours=5, minutes=30))
try:
    d = json.load(sys.stdin)
except Exception:
    print("no|the API did not answer"); sys.exit(0)
if not d.get("ok"):
    print("no|" + (d.get("error") or "not signed in")); sys.exit(0)
until = None
if d.get("signInExpiresUtc"):
    try:
        until = datetime.fromisoformat(d["signInExpiresUtc"].replace("Z", "+00:00"))
        if until.tzinfo is None: until = until.replace(tzinfo=timezone.utc)
    except Exception:
        until = None
if until is None and d.get("tokenValidity"):
    for fmt in ("%d/%m/%Y %H:%M", "%d/%m/%Y %H:%M:%S", "%Y-%m-%d %H:%M:%S"):
        try:
            until = datetime.strptime(d["tokenValidity"], fmt).replace(tzinfo=IST); break
        except Exception:
            pass
if until is None:
    print("ok|?")
else:
    print("ok|%.1f|%s" % ((until - datetime.now(timezone.utc)).total_seconds() / 3600, until.astimezone(IST).strftime("%H:%M")))
' 2>/dev/null || echo "no|could not read the answer"
}

start_dhan_primary() {  # makes Dhan the day's feed: today's ids, a fresh feed, the chain recorder
  # Today's contracts (a new weekly expiry, new strikes) get their Dhan ids.
  say "  mapping today's instruments to Dhan ids (about a minute) ..."
  IMPORT="$(API_MAX_TIME=240 api_post /api/Dhan/instruments/import '{}' 2>/dev/null || true)"
  say "  $(printf '%s' "$IMPORT" | head -c 200)"

  stop_daemon "Dhan feed" "/api/Feeds/dhan/stop"
  start_daemon "Dhan feed" "/api/Feeds/dhan/start"
  start_daemon "Dhan chain recorder" "/api/Dhan/chain-poller/start"
  DHAN_PRIMARY=1
}

# The automatic PIN + TOTP sign-in (DhanAutoSignIn.cs). The API's own worker
# takes the token between 08:00 and 08:40; these are for a morning it could not
# (the API restarted at 08:45, or a token that was fine at 08:40 and is not now).
dhan_auto_configured() {  # set up AND switched on (Dhan:AutoSignIn:Enabled)
  local status
  status="$(api_get /api/Dhan/auto-sign-in 2>/dev/null)" || return 1
  case "$status" in *'"configured":true'*) ;; *) return 1 ;; esac
  case "$status" in *'"enabled":true'*) return 0 ;; *) return 1 ;; esac
}

dhan_auto_sign_in() {  # prints the API's one-line answer; exit 0 when Dhan is signed in
  local out rc
  out="$(api_post '/api/Dhan/auto-sign-in?trigger=morning%20job' '{}' 2>/dev/null)"; rc=$?
  printf '%s' "$out" | python3 -c '
import json, sys
try:
    print(json.load(sys.stdin).get("message") or "no message")
except Exception:
    print("the API did not answer")'
  return $rc
}

dhan_token_short() {  # hours left -> exit 0 when the token will not last to the MCX close
  # 15.5 h from about 08:45 is past 23:55, the MCX close on US standard time
  # (from 2 Nov); crude now trades to the MCX close, not to 15:30.
  case "$1" in '?'|'') return 1 ;; esac
  python3 -c "import sys; sys.exit(0 if float('$1') < 15.5 else 1)" 2>/dev/null
}

if [ "$DRY_RUN" = 1 ]; then
  say "dry run: would check Dhan ($(dhan_state)), map instruments, start the Dhan feed and chain recorder"
  dhan_auto_configured && say "dry run: the automatic PIN + TOTP sign-in is set up" \
    || say "dry run: the automatic PIN + TOTP sign-in is not set up (DHAN_PIN / DHAN_TOTP_SECRET)"
else
  say "checking Dhan ..."
  DHAN="$(dhan_state)"
  if [ "${DHAN%%|*}" != ok ] && dhan_auto_configured; then
    say "  Dhan is not usable (${DHAN#*|}); signing in with the PIN and a TOTP code ..."
    say "  $(dhan_auto_sign_in)"
    DHAN="$(dhan_state)"
  fi
  if [ "${DHAN%%|*}" != ok ]; then
    say "  Dhan is not usable: ${DHAN#*|}"
    notify "AlgoTrading" "Dhan is not signed in — press Connect on Connectors > Dhan before 09:12. FYERS is the fallback."
    while [ "${DHAN%%|*}" != ok ] && [ "$(date +%H%M)" -lt "$DHAN_WAIT_UNTIL" ]; do
      sleep 20
      DHAN="$(dhan_state)"
    done
  fi

  if [ "${DHAN%%|*}" = ok ]; then
    HOURS_LEFT="$(printf '%s' "$DHAN" | cut -d'|' -f2)"
    ENDS_AT="$(printf '%s' "$DHAN" | cut -d'|' -f3)"
    say "  Dhan signed in${ENDS_AT:+; token valid until $ENDS_AT IST}"
    # The MCX session runs to 23:30 (23:55 in winter), up to 15.5 hours after this check. Nothing
    # streams on Dhan yet, so this is the one moment a new token cuts nothing off.
    if dhan_token_short "$HOURS_LEFT" && dhan_auto_configured; then
      say "  that is before the evening close; taking a fresh token with the PIN and a TOTP code ..."
      say "  $(dhan_auto_sign_in)"
      DHAN_AFTER="$(dhan_state)"
      if [ "${DHAN_AFTER%%|*}" = ok ]; then
        HOURS_LEFT="$(printf '%s' "$DHAN_AFTER" | cut -d'|' -f2)"
        ENDS_AT="$(printf '%s' "$DHAN_AFTER" | cut -d'|' -f3)"
      fi
    fi
    if dhan_token_short "$HOURS_LEFT"; then
      warn "the Dhan token ends at $ENDS_AT IST, before the evening session closes"
      notify "AlgoTrading" "Dhan token ends at $ENDS_AT IST. Press Connect on Connectors > Dhan for a full day; the feed picks the new token up by itself."
    fi

    start_dhan_primary
  else
    warn "no Dhan sign-in by $DHAN_WAIT_UNTIL IST — FYERS will be the feed today"
  fi
fi

# --- 4. the broker token, which expires daily --------------------------------
# >>> fyers-gate (these functions are also loaded by scripts/tests/market-open-fyers-gate.test.sh)
connected() {
  api_get /api/auth/session 2>/dev/null | grep -q '"isAuthenticated":true'
}

fyers_try_refresh() {  # exit 0 when the token is (now) valid
  # Try the refresh anyway: it costs one call, and it starts working the day
  # FYERS re-enables the API. Today it answers "disabled to comply with SEBI
  # regulations" — the daily sign-in is a regulator's requirement, not a gap
  # in this platform, and no amount of code removes it.
  say "FYERS token expired — trying a refresh ..."
  REFRESH="$(api_post /api/auth/refresh-token '{}' 2>/dev/null || true)"
  if connected; then
    say "  token renewed"
    return 0
  fi
  say "  refresh unavailable: $(printf '%s' "$REFRESH" | head -c 140)"
  return 1
}

# Dhan signed in after 09:12, while the morning waits for FYERS: that is
# enough to trade on, so it ends the wait. On 23 Sep the sign-in came at 10:36
# and the desk kept waiting for FYERS regardless.
dhan_came_up() {
  [ "${DHAN_PRIMARY:-0}" = 0 ] || return 1
  [ "${DHAN_FAILED_TODAY:-0}" = 0 ] || return 1
  [ "$(dhan_state 2>/dev/null | cut -d'|' -f1)" = ok ]
}

wait_for_fyers() {  # blocks until FYERS (or, late, Dhan) is signed in; fails the run at LOGIN_WAIT_UNTIL
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
    if dhan_came_up; then
      say "  Dhan came up at $(date '+%H:%M') — using Dhan as today's feed; FYERS stays the backup"
      notify "AlgoTrading" "Dhan signed in at $(date '+%H:%M') — the morning run carries on with Dhan."
      start_dhan_primary
      return 0
    fi
    if [ "$NOW" -ge "$LOGIN_WAIT_UNTIL" ]; then
      fail "no FYERS or Dhan sign-in by ${LOGIN_WAIT_UNTIL} IST; nothing was started."
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
}

# FYERS matters only as the backup feed. Every run this script starts is paper,
# filled at the live feed's quotes, and warms up from the platform's own
# candles, so when Dhan is the day's feed the morning does not wait for the
# FYERS sign-in. On 2026-09-17 that wait held Ghost back from 09:15 until the
# sign-in at 09:19 (deployed 09:21) while Dhan had been streaming since 08:45.
# The wait still happens when FYERS has to feed the day: Dhan not signed in, or
# Dhan silent after the open (step 6).
fyers_gate() {  # $1 = 1 when Dhan is the day's feed
  if connected; then
    say "FYERS session already valid"
    return 0
  fi
  fyers_try_refresh && return 0
  if [ "${1:-0}" = 1 ]; then
    say "  not waiting for FYERS: Dhan is today's feed and every run is paper, so FYERS is only the backup"
    notify "AlgoTrading" "FYERS is not signed in. Strategies start on Dhan without it; sign in when you can so the backup feed is ready."
    return 0
  fi
  wait_for_fyers
}
# <<< fyers-gate

if [ "$DRY_RUN" = 1 ] && ! connected; then
  if [ "$DHAN_PRIMARY" = 1 ]; then
    say "dry run: FYERS is not signed in; would carry on without it (Dhan is the feed)"
  else
    say "dry run: FYERS is not signed in; would wait for the sign-in until ${LOGIN_WAIT_UNTIL} IST"
  fi
else
  fyers_gate "$DHAN_PRIMARY"
fi

# --- 5. market data ----------------------------------------------------------

# A dry run skips every daemon but still walks the plan below: what it is for
# is seeing which strategies would be deployed, into which accounts, before a
# morning proves it the hard way.
if [ "$DRY_RUN" = 1 ]; then
  say "dry run: would start the ingestor and the chain poller; the plan follows"
else

# Fresh daemons, never "already running": a feed that lived through the night
# (the MCX session runs to 23:30) holds a token that expired at 06:00 and
# would sit deaf all day while the API reports it healthy. Stop first — a
# stop with nothing running is a no-op — then start with today's token.
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

start_fyers_feed() {
  start_daemon "tick ingestor" "/api/Ingestor/start"
  # The poller start endpoint takes no body: it reads CHAIN_UNDERLYINGS from the
  # environment the API handed it, which was exported above.
  start_daemon "chain poller ($CHAIN_UNDERLYINGS)" "/api/OptionChain/poller/start"
}

if [ "$DHAN_PRIMARY" = 1 ]; then
  say "Dhan is today's feed; the FYERS feed and chain poller stay off as the backup"
else
  start_fyers_feed
fi

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
FRESH_PRICES_PY='
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
'
TICKS="$(api_get /api/LiveData/latest/all 2>/dev/null | python3 -c "$FRESH_PRICES_PY" 2>/dev/null || echo 0)"
say "  symbols with a price in the last 5 minutes: ${TICKS:-0}"

# The backup: Dhan was started but nothing is arriving. Its feed is stopped and
# the FYERS feed takes over, so the strategies are not left without prices.
if [ "${TICKS:-0}" -lt 1 ] && [ "$DHAN_PRIMARY" = 1 ]; then
  warn "the Dhan feed delivered no prices — switching to the FYERS feed"
  notify "AlgoTrading" "Dhan feed delivered no prices after the open. Switched to FYERS; Dhan's option chain keeps recording."
  stop_daemon "Dhan feed" "/api/Feeds/dhan/stop"
  DHAN_PRIMARY=0
  # Dhan is signed in but silent: the wait below must not read "Dhan is signed
  # in" as Dhan coming up, or it would hand the day straight back to the feed
  # that just failed.
  DHAN_FAILED_TODAY=1
  # The morning did not wait for FYERS while Dhan was the feed; now it is needed.
  if ! connected; then
    say "  the FYERS feed needs a FYERS sign-in first"
    fyers_try_refresh || wait_for_fyers
  fi
  start_fyers_feed
  sleep 90
  TICKS="$(api_get /api/LiveData/latest/all 2>/dev/null | python3 -c "$FRESH_PRICES_PY" 2>/dev/null || echo 0)"
  say "  symbols with a price in the last 5 minutes (FYERS): ${TICKS:-0}"
fi

if [ "${TICKS:-0}" -lt 1 ]; then
  fail "no fresh prices after the ingestor started — the feed is not flowing (check the broker token — FYERS expires it at 06:00 IST)."
fi

fi   # end of the live-only section a dry run skips

# --- 7. the morning's plan ---------------------------------------------------
# Every account in ACCOUNTS gets every line of PLAN. The script is signed in as
# the admin and names the owner on each start, so no trader's password is held
# anywhere and each run lands in that trader's own book.

CATALOGUE="$(api_get /api/Strategy)"
USERS="$(api_get /api/Users)"

strategy_id() {  # name -> id on stdout, empty when the catalogue has no such strategy
  printf '%s' "$CATALOGUE" | NAME="$1" python3 -c '
import json, os, sys
try:
    rows = json.load(sys.stdin)
except Exception:
    sys.exit(0)
if isinstance(rows, dict):
    rows = rows.get("items") or rows.get("strategies") or []
want = os.environ["NAME"].strip().lower()
for row in rows if isinstance(rows, list) else []:
    if str(row.get("name") or "").strip().lower() == want:
        print(row.get("id") or "")
        break
' 2>/dev/null
}

user_id() {  # user name -> id on stdout, empty when there is no such account
  printf '%s' "$USERS" | NAME="$1" python3 -c '
import json, os, sys
try:
    rows = json.load(sys.stdin)
except Exception:
    sys.exit(0)
want = os.environ["NAME"].strip().lower()
for row in rows if isinstance(rows, list) else []:
    if str(row.get("userName") or "").strip().lower() == want and row.get("isActive"):
        print(row.get("id") or "")
        break
' 2>/dev/null
}

# What is already running, per account. A second fire of this script, or a hand
# re-run after a stumble, must not double anyone's position — and two accounts
# running the same strategy on the same underlying is NOT a duplicate, it is
# the point: same signal, two books.
RUNNING="$(api_get '/api/Strategy/runs?status=Running&take=500' 2>/dev/null || echo '')"
already_running() {  # strategy underlying userId -> exit 0 when that account already runs it
  printf '%s' "$RUNNING" | STRATEGY="$1" UNDERLYING="$2" OWNER="$3" python3 -c '
import json, os, sys
try:
    runs = json.load(sys.stdin)
except Exception:
    sys.exit(1)
if isinstance(runs, dict):
    runs = runs.get("items") or runs.get("runs") or []
want_strategy = os.environ["STRATEGY"].strip().lower()
want_underlying = os.environ["UNDERLYING"].strip().upper()
want_owner = os.environ["OWNER"].strip()
for run in runs if isinstance(runs, list) else []:
    if str(run.get("strategyName") or "").strip().lower() == want_strategy \
            and str(run.get("underlying") or "").strip().upper() == want_underlying \
            and str(run.get("userId") or "").strip() == want_owner:
        sys.exit(0)
sys.exit(1)
' 2>/dev/null
}

# >>> plan-line (also loaded by scripts/tests/market-open-plan.test.sh)
# One plan line, read for one account:
#   Strategy  UNDERLYING[,UNDERLYING...]  lots  [legTargetPoints | -]  [@account[,account]]
# Prints "NAME SYMBOLS LOTS TARGET" when the line applies to the account, and
# nothing when it does not (a comment, a blank, or an @-list without it).
parse_plan_line() {  # line account default_lots default_target
  local name symbols lots rest field target="" only=""
  read -r name symbols lots rest <<<"$1"
  [ -n "${name:-}" ] || return 0
  case "$name" in \#*) return 0 ;; esac
  for field in ${rest:-}; do
    case "$field" in
      @*) only="${field#@}" ;;
      *) target="$field" ;;
    esac
  done
  if [ -n "$only" ] && ! printf ',%s,' "$only" | tr '[:upper:]' '[:lower:]' \
      | grep -qF ",$(printf '%s' "$2" | tr '[:upper:]' '[:lower:]'),"; then
    return 0
  fi
  echo "$name ${symbols:-} ${lots:-$3} ${target:-$4}"
}
# <<< plan-line

# The rules as the API's RiskRulesDto (camelCase; leg rules in premium points,
# day rules in rupees with scope "day"). Built per plan line, because the leg
# target is the one rule a strategy may want its own.
risk_json() {  # legTargetPoints -> the run's RiskRulesDto on stdout
  LEG_TARGET_PTS="$1" LEG_STOP_PTS="$LEG_STOP_PTS" DAY_TARGET="$DAY_TARGET" DAY_STOP_LOSS="$DAY_STOP_LOSS" python3 - <<'PYEOF'
import json, os
def num(k):
    v = os.environ.get(k, "").strip()
    return float(v) if v and v != "-" else None
leg = {k: v for k, v in {"targetPoints": num("LEG_TARGET_PTS"), "stopLossPoints": num("LEG_STOP_PTS")}.items() if v}
day = {k: v for k, v in {"target": num("DAY_TARGET"), "stopLoss": num("DAY_STOP_LOSS")}.items() if v}
risk = {}
if leg: risk["leg"] = leg
if day: risk["overall"] = {**day, "scope": "day"}
print(json.dumps(risk))
PYEOF
}

say "day rules for every run: ${DAY_TARGET:+target ₹$DAY_TARGET }${DAY_STOP_LOSS:+SL ₹$DAY_STOP_LOSS}${DAY_TARGET:-${DAY_STOP_LOSS:-none}}; leg target is per strategy, below"

for ACCOUNT in $ACCOUNTS; do
  OWNER_ID="$(user_id "$ACCOUNT")"
  if [ -z "$OWNER_ID" ]; then
    say "no active account called '$ACCOUNT' — skipping it; the other accounts still run"
    continue
  fi

  say "--- $ACCOUNT (user $OWNER_ID) ---"

  printf '%s\n' "$PLAN" | while IFS= read -r LINE; do
    PARSED="$(parse_plan_line "$LINE" "$ACCOUNT" "$LOTS_DEFAULT" "$LEG_TARGET_PTS")"
    [ -n "$PARSED" ] || continue
    read -r NAME SYMBOLS PLAN_LOTS PLAN_TARGET <<<"$PARSED"
    RISK_JSON="$(risk_json "$PLAN_TARGET")"
    RISK_TEXT="leg target $([ "$PLAN_TARGET" = "-" ] && echo none || echo "$PLAN_TARGET pts")"

    SID="$(strategy_id "$NAME")"
    if [ -z "$SID" ]; then
      say "  '$NAME' is not in the catalogue — skipped"
      continue
    fi

    for U in $(printf '%s' "$SYMBOLS" | tr ',' ' '); do
      if already_running "$NAME" "$U" "$OWNER_ID"; then
        say "  $NAME on $U — already running in this account, left alone"
        continue
      fi

      if [ "$DRY_RUN" = 1 ]; then
        say "  dry run: would start $NAME (id $SID) on $U, $PLAN_LOTS lot(s) for $ACCOUNT"
        continue
      fi

      say "  deploying $NAME (id $SID) on $U, $PLAN_LOTS lot(s), $RISK_TEXT — paper, for $ACCOUNT"
      RUN="$(api_post "/api/Strategy/$SID/start" \
        "{\"underlying\":\"$U\",\"lots\":$PLAN_LOTS,\"ownerUserId\":$OWNER_ID,\"risk\":$RISK_JSON}" || true)"
      say "    $(printf '%s' "$RUN" | head -c 200)"
      sleep 2
    done
  done
done

# --- 8. the tally: the plan against what is actually live -------------------
# Until 27 Sep this printed the Running rows seconds after the last start and
# compared them with nothing. On 24 Sep that read "10 run(s) live" against 26
# planned — sixteen runners had died 1-5 s after starting — and the job exited
# 0. The expected runs come from the same parser the deploy loop used, so the
# two cannot disagree about what the plan says; scripts/lib/morning_tally.py
# does the matching (tested in the engine's tests/test_morning_tally.py).
expected_runs() {  # every planned run as "account|strategy|underlying"
  local account line parsed name symbols u
  for account in $ACCOUNTS; do
    while IFS= read -r line; do
      parsed="$(parse_plan_line "$line" "$account" "$LOTS_DEFAULT" "$LEG_TARGET_PTS")"
      [ -n "$parsed" ] || continue
      read -r name symbols _ _ <<<"$parsed"
      for u in $(printf '%s' "$symbols" | tr ',' ' '); do echo "$account|$name|$u"; done
    done <<<"$PLAN"
  done
}

EXPECTED="$(expected_runs)"
PLANNED_COUNT="$(printf '%s\n' "$EXPECTED" | grep -c . || true)"
TALLY_RC=0

if [ "$DRY_RUN" = 1 ]; then
  say "dry run: the tally would expect $PLANNED_COUNT run(s) live"
else
  # A runner that dies does so within seconds (24 Sep: 1-5 s), so the first
  # count waits a minute; a short count is taken again every 30 s, up to three
  # minutes in all, for a slow start to catch up.
  TALLY_FIRST="${MARKET_OPEN_TALLY_FIRST:-60}"
  TALLY_UNTIL=$(( $(date +%s) + ${MARKET_OPEN_TALLY_WAIT:-180} ))
  say "counting the live runs against the plan ($PLANNED_COUNT planned) in ${TALLY_FIRST} s ..."
  sleep "$TALLY_FIRST"
  while :; do
    FINAL="$(api_get '/api/Strategy/runs?status=Running&take=500' 2>/dev/null)" || FINAL=""
    TODAY_RUNS="$(api_get "/api/Strategy/runs?fromDate=$(date +%F)&take=500" 2>/dev/null)" || TODAY_RUNS=""
    REPORT="$(EXPECTED="$EXPECTED" USERS="$USERS" RUNNING="$FINAL" TODAY="$TODAY_RUNS" \
      python3 "$REPO_ROOT/scripts/lib/morning_tally.py")" && TALLY_RC=0 || TALLY_RC=$?
    [ "$TALLY_RC" = 0 ] && break
    [ "$(date +%s)" -ge "$TALLY_UNTIL" ] && break
    sleep 30
  done

  printf '%s\n' "$REPORT" | tail -n +2 | while IFS= read -r LINE; do say "$LINE"; done
  SUMMARY="$(printf '%s\n' "$REPORT" | head -1)"
  [ "$TALLY_RC" = 0 ] || warn "$SUMMARY"
  notify "AlgoTrading" "${SUMMARY:-Morning plan: the tally could not be taken}"

  # Who runs what, per account, as before: the tally says what is missing,
  # this says what is there.
  FINAL="$FINAL" python3 - <<'PYEOF' | while read -r LINE; do say "$LINE"; done
import json, os, sys
try:
    runs = json.loads(os.environ.get("FINAL") or "[]")
except Exception:
    sys.exit(0)
if isinstance(runs, dict):
    runs = runs.get("items") or runs.get("runs") or []
by_user = {}
for run in runs if isinstance(runs, list) else []:
    who = str(run.get("userName") or run.get("userId") or "?")
    by_user.setdefault(who, []).append(
        str(run.get("strategyName")) + " on " + str(run.get("underlying")))
for user, rows in sorted(by_user.items()):
    print("  " + user + ": " + str(len(rows)) + " - " + ", ".join(sorted(rows)))
PYEOF
fi

say "Watch them at $CONSOLE/admin/strategies/live — or read this file."

# Short (or unknown) is a failed morning for whoever reads the exit code: the
# desk's log, and the once-a-day marker that is coming (audit fix 9).
[ "$TALLY_RC" = 0 ] || exit 2
