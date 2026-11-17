#!/usr/bin/env bash
# Keeps the morning plan trading until the end of the session.
#
# The owner, 17 Nov 2026: until Dhan's data plan is back, NIFTY must not stop
# under any condition. Two failures could stop a planned run for the rest of a
# day, with nothing to bring it back:
#   - the feed going silent or its process dying. Only the pager said so.
#   - the runner exiting. It was closed "Runner exited" and stayed closed.
#
# scripts/market-open.sh starts this, detached, once the broker sign-in is
# through. It does nothing while the morning job is still running
# (KEEPER_WAIT_PID). After that, once a minute until KEEPER_UNTIL, it asks
# scripts/lib/session_keeper.py what to do and does it:
#   - feed: whichever live feed is running (FYERS, or Angel One once someone
#     has switched to it) is stopped and started when a spot the plan trades
#     has had no price for 5 minutes. With none running for two checks in a
#     row, the last one seen running is started (KEEPER_FEED, FYERS at first).
#     The two checks leave time for a switch made by hand. Angel One still
#     silent after two restarts hands over to FYERS (scripts/switch-feed.sh,
#     which keeps Angel running if FYERS cannot price either). At most
#     KEEPER_FEED_MAX times a day, KEEPER_FEED_GAP_SECONDS apart.
#   - runs: a planned run is not live, and its last run today ended by itself
#     (the runner exited, or an API restart lost it), or it never started.
#     That run alone is started again with market-open.sh --redeploy-only and
#     MARKET_OPEN_ONLY. At most KEEPER_RUN_MAX times a day, KEEPER_RUN_GAP_SECONDS
#     apart, and not after KEEPER_RUNS_UNTIL.
#     A run that a person stopped, the risk guard closed or the market close
#     ended is left as it is.
# Every action goes to the phone; the log is logs/session-keeper.log.
#
#   scripts/session-keeper.sh     (by hand it reads the plan itself)
#   KEEPER_DRY=1 KEEPER_FROM=0000 KEEPER_UNTIL=<HHMM> scripts/session-keeper.sh
#                                 says what it would do and does nothing

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/session-keeper.log"
mkdir -p "$REPO_ROOT/logs"
# shellcheck source=scripts/lib/desk-common.sh
. scripts/lib/desk-common.sh
DESK_LOG_ONLY=1   # detached: say() writes to the log alone

FROM="${KEEPER_FROM:-0917}"              # HHMM IST: the open's first prices are in
UNTIL="${KEEPER_UNTIL:-1525}"            # HHMM IST: stop watching
RUNS_UNTIL="${KEEPER_RUNS_UNTIL:-1500}"  # HHMM IST: no run is started again after this
EVERY="${KEEPER_EVERY_SECONDS:-60}"
FEED_MAX="${KEEPER_FEED_MAX:-6}"
FEED_GAP="${KEEPER_FEED_GAP_SECONDS:-300}"
RUN_MAX="${KEEPER_RUN_MAX:-4}"
RUN_GAP="${KEEPER_RUN_GAP_SECONDS:-180}"
WAIT_PID="${KEEPER_WAIT_PID:-}"
DAY_STATE="${KEEPER_DAY_STATE:-unknown}"
FYERS_FEED="${KEEPER_FYERS_FEED:-1}"     # 0 when Dhan is the day's feed: no feed is touched
LAST_FEED="${KEEPER_FEED:-fyers}"        # started when none runs; follows whichever is seen running
EXPECTED_TEXT="${KEEPER_EXPECTED:-}"
DRY="${KEEPER_DRY:-0}"                   # 1: say what would be done, do nothing

load_env || { say "session keeper: .env has no admin credentials — not watching"; exit 1; }

# By hand there is no morning job to hand the plan over: read it the same way.
if [ -z "$EXPECTED_TEXT" ]; then
  eval "$(sed -n '/^# >>> plan-line/,/^# <<< plan-line/p' scripts/market-open.sh)"
  PLAN="$(grep -v '^[[:space:]]*#' config/morning-plan.txt | grep -v '^[[:space:]]*$' | grep -vi '^[[:space:]]*accounts:')"
  ACCOUNTS="$(grep -i '^[[:space:]]*accounts:' config/morning-plan.txt | head -1 | cut -d: -f2-)"
  EXPECTED_TEXT="$(plan_runs "$ACCOUNTS" 2 20 <<<"$PLAN" | cut -d'|' -f1-3)"
fi
[ -n "$EXPECTED_TEXT" ] || { say "session keeper: the plan has no runs — nothing to keep"; exit 0; }

# One keeper at a time: the morning job run twice must not start a second one.
mkdir -p "$DESK_STATE_DIR"
LOCK="$DESK_STATE_DIR/session-keeper.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  OTHER="$(cat "$LOCK/pid" 2>/dev/null || true)"
  if [ -n "$OTHER" ] && kill -0 "$OTHER" 2>/dev/null; then
    say "session keeper: another one (pid $OTHER) is watching — leaving it"
    exit 0
  fi
  rm -rf "$LOCK"
  mkdir "$LOCK" || exit 1
fi
echo "$$" >"$LOCK/pid"
WORK="$(mktemp -d)"
trap 'rm -rf "$LOCK" "$WORK"' EXIT

printf '%s\n' "$EXPECTED_TEXT" >"$WORK/expected.txt"
hhmm() { echo "$((10#$(date +%H%M)))"; }

fetch() {  # API path, file: the answer, or no file when the API did not give one
  if api_get "$1" >"$WORK/$2.tmp" 2>/dev/null; then mv "$WORK/$2.tmp" "$WORK/$2"; else rm -f "$WORK/$2" "$WORK/$2.tmp"; fi
}

# A standing state is logged when it starts, not every minute: one memory each for the feed and the runs.
FEED_SAID="" RUNS_SAID=""
feed_once() { [ "$1" = "$FEED_SAID" ] || { FEED_SAID="$1"; say "$1"; }; }
runs_once() { [ "$1" = "$RUNS_SAID" ] || { RUNS_SAID="$1"; say "$1"; }; }

say "session keeper started (pid $$): $(grep -c . "$WORK/expected.txt") planned run(s), $(paste -sd, "$WORK/expected.txt"); watching until $UNTIL IST; feeds $([ "$FYERS_FEED" = 1 ] && echo "watched ($LAST_FEED first)" || echo 'not touched (Dhan is the feed)')"
fetch /api/Users users.json

FEED_DONE=0 FEED_LAST=0 RUN_DONE=0 RUN_LAST=0 NONE_RUNNING=0 ANGEL_RESTARTS=0
while :; do
  NOW="$(hhmm)"
  [ "$NOW" -ge "$((10#$UNTIL))" ] && break
  if [ -n "$WAIT_PID" ] && kill -0 "$WAIT_PID" 2>/dev/null; then sleep 20; continue; fi
  if [ "$NOW" -lt "$((10#$FROM))" ]; then sleep 20; continue; fi

  fetch /api/Feeds feeds.json
  fetch /api/LiveData/latest/all quotes.json
  fetch '/api/Strategy/runs?status=Running&take=500' running.json
  fetch "/api/Strategy/runs?fromDate=$(date +%F)&take=500" today.json
  DECISION="$(python3 scripts/lib/session_keeper.py --dir "$WORK" --expected "$WORK/expected.txt" --day-state "$DAY_STATE" --feed "$LAST_FEED" 2>&1)"
  FEED_LINE="$(printf '%s\n' "$DECISION" | sed -n 's/^feed //p')"
  RUNS_LINE="$(printf '%s\n' "$DECISION" | sed -n 's/^runs //p')"
  if [ -z "$FEED_LINE" ] || [ -z "$RUNS_LINE" ]; then
    feed_once "session keeper: no decision ($(printf '%s' "$DECISION" | tail -1 | head -c 160))"
    sleep "$EVERY"; continue
  fi
  FEED_ACT="${FEED_LINE%% *}"; REST="${FEED_LINE#* }"; FEED_KEY="${REST%% *}"; FEED_WHY="${REST#* }"
  if [ "$FEED_ACT" = start ]; then NONE_RUNNING=$((NONE_RUNNING + 1)); else NONE_RUNNING=0; fi
  [ "$FEED_ACT" = ok ] && ANGEL_RESTARTS=0
  case "$FEED_ACT" in ok|restart|wait) [ "$FEED_KEY" != - ] && LAST_FEED="$FEED_KEY" ;; esac
  RUNS_ACT="${RUNS_LINE%% *}"; REST="${RUNS_LINE#* }"; RUNS_KEYS="${REST%% *}"; RUNS_WHY="${REST#* }"
  EPOCH="$(date +%s)"

  case "$FEED_ACT" in
    start|restart)
      if [ "$FYERS_FEED" != 1 ]; then
        feed_once "feed: $FEED_WHY — Dhan is the feed today, no feed is touched"
      elif [ "$FEED_ACT" = start ] && [ "$NONE_RUNNING" -lt 2 ]; then
        feed_once "feed: $FEED_WHY — waiting one more check (a switch by hand may be under way)"
      elif [ "$FEED_DONE" -ge "$FEED_MAX" ]; then
        feed_once "feed: $FEED_WHY — already restarted $FEED_MAX times today; leaving it to a person"
        notify_at_most_hourly keeper-feed-cap "AlgoTrading" "Session keeper: $FEED_WHY. The feed was restarted $FEED_MAX times today; look at Data → Feeds."
      elif [ "$DRY" = 1 ]; then
        feed_once "dry run: feed: $FEED_WHY — would ${FEED_ACT} the $FEED_KEY feed"
      elif [ $((EPOCH - FEED_LAST)) -ge "$FEED_GAP" ] && [ "$FEED_ACT" = restart ] && [ "$FEED_KEY" = angel ] \
          && [ "$ANGEL_RESTARTS" -ge 2 ]; then
        FEED_DONE=$((FEED_DONE + 1)); FEED_LAST="$EPOCH"; FEED_SAID=""
        say "feed: $FEED_WHY — Angel One restarted $ANGEL_RESTARTS times and still silent; handing over to FYERS"
        notify "AlgoTrading" "Session keeper: Angel One gives no prices after $ANGEL_RESTARTS restarts. Switching to FYERS (needs today's FYERS sign-in)."
        ./scripts/switch-feed.sh fyers >>"$LOG" 2>&1 \
          || say "  the switch to FYERS did not take (is FYERS signed in?) — Angel One left running"
      elif [ $((EPOCH - FEED_LAST)) -ge "$FEED_GAP" ]; then
        FEED_DONE=$((FEED_DONE + 1)); FEED_LAST="$EPOCH"; FEED_SAID=""
        [ "$FEED_KEY" = angel ] && [ "$FEED_ACT" = restart ] && ANGEL_RESTARTS=$((ANGEL_RESTARTS + 1))
        say "feed: $FEED_WHY — ${FEED_ACT}ing the $FEED_KEY feed ($FEED_DONE of $FEED_MAX today)"
        if [ "$FEED_ACT" = restart ]; then
          say "  stop: $(api_post "/api/Feeds/$FEED_KEY/stop" '{}' 2>&1 | head -c 160)"
          sleep 3
        fi
        say "  start: $(api_post "/api/Feeds/$FEED_KEY/start" '{}' 2>&1 | head -c 160)"
        notify "AlgoTrading" "Session keeper: $FEED_WHY. The $FEED_KEY feed was ${FEED_ACT}ed ($FEED_DONE of $FEED_MAX today)."
      fi ;;
    *) feed_once "feed: $FEED_ACT — $FEED_WHY" ;;
  esac

  case "$RUNS_ACT" in
    redeploy)
      if [ "$NOW" -ge "$((10#$RUNS_UNTIL))" ]; then
        runs_once "runs: $RUNS_WHY — past $RUNS_UNTIL IST, not started again"
      elif [ "$RUN_DONE" -ge "$RUN_MAX" ]; then
        runs_once "runs: $RUNS_WHY — already started again $RUN_MAX times today; leaving it to a person"
        notify_at_most_hourly keeper-run-cap "AlgoTrading" "Session keeper: $RUNS_WHY. Started again $RUN_MAX times today; start it from Strategies → Live runner."
      elif [ "$DRY" = 1 ]; then
        runs_once "dry run: runs: $RUNS_WHY — would start again: $RUNS_KEYS"
      elif [ $((EPOCH - RUN_LAST)) -ge "$RUN_GAP" ]; then
        RUN_DONE=$((RUN_DONE + 1)); RUN_LAST="$EPOCH"; RUNS_SAID=""
        say "runs: $RUNS_WHY — starting again ($RUN_DONE of $RUN_MAX today): $RUNS_KEYS"
        notify "AlgoTrading" "Session keeper: $RUNS_WHY. Starting it again ($RUN_DONE of $RUN_MAX today)."
        MARKET_OPEN_ONLY="$RUNS_KEYS" ./scripts/market-open.sh --redeploy-only >>"$LOG" 2>&1 \
          || say "  the redeploy ended with exit $? (its tally is above)"
      fi ;;
    *) runs_once "runs: $RUNS_ACT — $RUNS_WHY" ;;
  esac
  sleep "$EVERY"
done
say "session keeper: $UNTIL IST — done for the day (feed restarts $FEED_DONE, runs started again $RUN_DONE)"
