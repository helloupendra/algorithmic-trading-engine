#!/usr/bin/env bash
# Shuts the trading day down: every live run flat and stopped, every feed and
# recorder stopped, nothing left holding a session overnight.
#
# Run after the LAST market of the day closes — MCX, at 23:30 IST — by the desk
# supervisor (scripts/desk.sh, CLOSE_AT). Nothing here is destructive: stopping
# something that is already stopped is a no-op, so it is safe to run twice or
# by hand.
#
# Why it exists: on 2026-09-16 the Dhan feed from the previous evening was
# still running at the open. Its token had died overnight, so it connected,
# subscribed, was dropped, and reconnected every 20 seconds until Dhan blocked
# the account — while the platform still reported the feed as running. A feed
# that is stopped at the end of the day cannot do that.
#
# It writes one report per day to logs/market-close-YYYY-MM-DD.log.
#
# Usage: ./scripts/market-close.sh [--dry-run] [--force]
#   --dry-run  say what would be stopped, stop nothing
#   --force    also kill feed processes the API does not own (adopted or
#              orphaned ones it cannot stop)

set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/market-close-$(date +%F).log"
. scripts/lib/desk-common.sh

DRY_RUN=0
FORCE=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --force)   FORCE=1 ;;
    *) say "unknown argument: $arg"; exit 2 ;;
  esac
done

say "=== market-close: $(date '+%A %d %B %Y %H:%M') ==="
load_env

API_UP=1
if ! api_healthy; then
  # Without the API nothing can be stopped politely, and every call would sit
  # through its own timeout first. Skip straight to the processes, which is
  # also the only thing --force needs.
  API_UP=0
  warn "the API is not answering — only stray processes can be dealt with"
fi

stopped=0
failed=0

# --- 1. live runs ------------------------------------------------------------
# Equity and index runs square off at 15:30 on their own; a commodity run holds
# until MCX closes. Stopping with flatten=true asks the platform to close what
# is still open, through the same path the console's Stop button uses.
runs=""
[ "$API_UP" = 1 ] && runs="$(api_get "/api/Strategy/runs?status=Running" 2>/dev/null || true)"
run_ids="$(printf '%s' "$runs" | grep -o '"runId":[0-9]*' | cut -d: -f2 | tr '\n' ' ')"
if [ -z "${run_ids// /}" ]; then
  say "live runs: none open"
else
  say "live runs still open:${run_ids% }"
  for id in $run_ids; do
    if [ "$DRY_RUN" = 1 ]; then
      say "  dry run: would stop run $id (flatten)"
      continue
    fi
    if api_post "/api/Strategy/runs/$id/stop" '{"flatten":true}' >/dev/null 2>&1; then
      say "  run $id stopped"
      stopped=$((stopped + 1))
    else
      warn "  run $id did not stop — stop it from the console"
      failed=$((failed + 1))
    fi
  done
fi

# --- 2. recorders ------------------------------------------------------------
# Before the feeds: a chain poller left running would keep asking a vendor for
# snapshots with no feed behind it, and log a failure a minute all night.
for entry in "chain poller|/api/OptionChain/poller/stop" "Dhan chain recorder|/api/Dhan/chain-poller/stop"; do
  label="${entry%%|*}"; path="${entry##*|}"
  [ "$API_UP" = 0 ] && continue
  if [ "$DRY_RUN" = 1 ]; then say "dry run: would stop $label"; continue; fi
  out="$(api_post "$path" '{}' 2>/dev/null || true)"
  case "$out" in
    *'"wasRunning":true'*) say "$label stopped"; stopped=$((stopped + 1)) ;;
    *)                     say "$label was not running" ;;
  esac
done

# --- 3. feeds ----------------------------------------------------------------
# Every vendor the platform knows, not a hard-coded list: a connector added
# later must be stopped at the end of the day too.
feeds=""
[ "$API_UP" = 1 ] && feeds="$(api_get "/api/Feeds" 2>/dev/null || true)"
keys="$(printf '%s' "$feeds" | grep -o '"key":"[a-zA-Z0-9_-]*"' | cut -d'"' -f4 | tr '\n' ' ')"
[ -z "${keys// /}" ] && keys="fyers dhan truedata"
for key in $keys; do
  [ "$API_UP" = 0 ] && continue
  if [ "$DRY_RUN" = 1 ]; then say "dry run: would stop the $key feed"; continue; fi
  out="$(api_post "/api/Feeds/$key/stop" '{}' 2>/dev/null || true)"
  case "$out" in
    *'"wasRunning":true'*) say "$key feed stopped"; stopped=$((stopped + 1)) ;;
    *)                     say "$key feed was not running" ;;
  esac
done
# The FYERS feed also answers to the older ingestor route, which is what the
# morning job starts it with.
if [ "$DRY_RUN" = 0 ] && [ "$API_UP" = 1 ]; then
  out="$(api_post "/api/Ingestor/stop" '{}' 2>/dev/null || true)"
  case "$out" in *'"wasRunning":true'*) say "tick ingestor stopped"; stopped=$((stopped + 1)) ;; esac
fi

# --- 4. what is still alive --------------------------------------------------
# The API can only stop what it started or has adopted. Anything else is named
# here, because a process nobody owns is exactly what caused this script to
# exist.
strays="$(pgrep -fa "run_feed.py|live_ingestor.py" 2>/dev/null | grep -v market-close || true)"
if [ -n "$strays" ]; then
  say "still running after the stops:"
  printf '%s\n' "$strays" | while IFS= read -r line; do say "  $line"; done
  if [ "$FORCE" = 1 ] && [ "$DRY_RUN" = 0 ]; then
    printf '%s\n' "$strays" | awk '{print $1}' | while IFS= read -r pid; do
      kill "$pid" 2>/dev/null && say "  killed $pid" || warn "  could not kill $pid"
    done
    sleep 2
    leftover="$(pgrep -f "run_feed.py|live_ingestor.py" 2>/dev/null | tr '\n' ' ' || true)"
    [ -n "${leftover// /}" ] && warn "still alive after kill: $leftover"
  elif [ "$FORCE" = 0 ]; then
    say "  (run with --force to end these)"
  fi
else
  say "no feed processes left running"
fi

if [ "$DRY_RUN" = 1 ]; then
  say "=== dry run: nothing was stopped ==="
  exit 0
fi

summary="stopped $stopped thing(s)"
[ "$failed" -gt 0 ] && summary="$summary, $failed could not be stopped"
say "=== market-close done — $summary ==="
notify "Market close" "$summary. Feeds and recorders are down for the night."
[ "$failed" -gt 0 ] && exit 1
exit 0
