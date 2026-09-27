#!/usr/bin/env bash
# When the desk runs market-close.sh (scripts/lib/desk-common.sh,
# market_close_due): after the MCX close, which is 23:55 IST in the US winter,
# and after midnight for the day before when the evening's was missed.
# Run: bash scripts/tests/desk-close-schedule.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.."

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}

# Load only the schedule rule, not the rest of the desk's helpers.
eval "$(sed -n '/^# --- when the evening close runs/,$p' scripts/lib/desk-common.sh)"

# due DOW HHMM TODAY YESTERDAY CLOSED_ON [CLOSE_AT] -> the day it runs for, or "no"
due() { market_close_due "$@" || echo no; }

MON=2026-11-02; TUE=2026-11-03; FRI=2026-11-06; SAT=2026-11-07; SUN=2026-11-08
PREV_FRI=2026-10-30

echo "market_close_due"
check "23:35, the old time: not yet (MCX trades to 23:55 from 1 Nov)"  no   "$(due 1 2335 $MON $SUN $PREV_FRI)"
check "23:57: not yet"                                                  no   "$(due 1 2357 $MON $SUN $PREV_FRI)"
check "23:58 on a weekday: today's close"                               $MON "$(due 1 2358 $MON $SUN $PREV_FRI)"
check "23:59, already run today: nothing"                               no   "$(due 1 2359 $MON $SUN $MON)"
check "after midnight, yesterday's was missed: yesterday's close"       $MON "$(due 2 0010 $TUE $MON $PREV_FRI)"
check "after midnight, yesterday's ran: nothing"                        no   "$(due 2 0010 $TUE $MON $MON)"
check "a desk started at 02:00 knows nothing: runs yesterday's once"    $MON "$(due 2 0200 $TUE $MON '')"
check "the catch-up ends at 06:00"                                      no   "$(due 2 0600 $TUE $MON $PREV_FRI)"
check "a leading-zero clock is read as decimal (0059)"                  $MON "$(due 2 0059 $TUE $MON '')"
check "Monday after midnight: Sunday had no close"                      no   "$(due 1 0010 $MON $SUN $PREV_FRI)"
check "Saturday after midnight, Friday's was missed: Friday's close"    $FRI "$(due 6 0010 $SAT $FRI $PREV_FRI)"
check "Saturday 23:58: no close at a weekend"                           no   "$(due 6 2358 $SAT $FRI $FRI)"
check "MARKET_CLOSE_AT is honoured"                                     $MON "$(due 1 2350 $MON $SUN $PREV_FRI 2350)"
check "the default is 23:58"                                            2358 "$MARKET_CLOSE_AT_DEFAULT"

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
