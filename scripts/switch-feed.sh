#!/usr/bin/env bash
# Moves the live ticks to one feed: angel or fyers (18 Nov 2026).
#
# The new feed is started first and the old one stopped only once the new one's
# own log shows it subscribed and pricing (in market hours NIFTY must also be
# fresh), so the strategies never go without prices in between: for a few
# seconds both feed the same symbols, which costs nothing. A switch that shows
# no prices in 60 s leaves the old feed running.
# The session keeper follows whichever feed is running.
#
#   scripts/switch-feed.sh angel     # Angel One: signs in by itself, 1,000 symbols
#   scripts/switch-feed.sh fyers     # back to FYERS (needs the day's FYERS sign-in)

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/switch-feed.log"
mkdir -p "$REPO_ROOT/logs"
# shellcheck source=scripts/lib/desk-common.sh
. scripts/lib/desk-common.sh

TO="$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]')"
case "$TO" in
  angel|fyers) ;;
  *) echo "usage: scripts/switch-feed.sh angel|fyers" >&2; exit 2 ;;
esac
load_env || { echo "switch-feed: .env has no admin credentials" >&2; exit 1; }

running_feeds() {  # the keys of the live feeds running now, one per line
  api_get /api/Feeds 2>/dev/null | python3 -c '
import json, sys
try:
    rows = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for row in rows if isinstance(rows, list) else []:
    if row.get("isRunning") is True:
        print(str(row.get("key") or "").lower())
'
}

nifty_age() {  # seconds since NIFTY 50's last price reached the API, or empty
  api_get /api/LiveData/latest/all 2>/dev/null | python3 -c '
import json, sys
from datetime import datetime, timezone
try:
    rows = json.load(sys.stdin)
except Exception:
    sys.exit(0)
for row in rows if isinstance(rows, list) else []:
    if row.get("symbol") == "NSE:NIFTY50-INDEX" and row.get("updatedUtc"):
        at = datetime.fromisoformat(str(row["updatedUtc"]).replace("Z", "+00:00"))
        print(int((datetime.now(timezone.utc) - at).total_seconds()))
'
}

BEFORE="$(running_feeds | paste -sd' ' -)"
say "switch-feed: to $TO (running now: ${BEFORE:-none})"
if ! printf '%s\n' "$BEFORE" | tr ' ' '\n' | grep -qx "$TO"; then
  say "  start $TO: $(api_post "/api/Feeds/$TO/start" '{}' 2>&1 | head -c 200)"
fi

delivers() {  # key -> exit 0 once that feed's own log shows it took symbols and is pricing them
  local logs
  logs="$(api_get "/api/Feeds/$1/logs?take=400" 2>/dev/null)" || return 1
  printf '%s' "$logs" | grep -Eq 'SUBSCRIBED: [1-9][0-9]* symbol' || return 1
  # Angel says so itself; FYERS's subscribe is its proof.
  [ "$1" != angel ] || printf '%s' "$logs" | grep -q 'first prices arriving'
}
session_open() {  # NSE's hours on a weekday: only then must NIFTY's price be fresh
  local now; now="$((10#$(date +%H%M)))"
  [ "$(date +%u)" -le 5 ] && [ "$now" -ge 915 ] && [ "$now" -lt 1530 ]
}

PRICED=0
for _ in $(seq 1 12); do
  sleep 5
  AGE="$(nifty_age)"
  delivers "$TO" || continue
  if session_open && { [ -z "$AGE" ] || [ "$AGE" -gt 10 ]; }; then continue; fi
  PRICED=1; break
done
if [ "$PRICED" != 1 ]; then
  say "  $TO shows no prices after 60 s (NIFTY's last price ${AGE:-?} s old) — the old feed is left running"
  say "  its output: /api/Feeds/$TO/logs, or logs/engine/ on the server"
  exit 1
fi

for OTHER in $(running_feeds); do
  [ "$OTHER" = "$TO" ] && continue
  say "  stop $OTHER: $(api_post "/api/Feeds/$OTHER/stop" '{}' 2>&1 | head -c 200)"
done
say "  done: $TO is the feed; NIFTY's last price ${AGE:-?} s old"
notify "AlgoTrading" "Live feed switched to $TO. NIFTY is pricing from it."
