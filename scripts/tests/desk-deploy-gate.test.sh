#!/usr/bin/env bash
# shellcheck disable=SC2034  # variables here are read by the functions under test
# The desk's deploy gate (scripts/lib/desk-common.sh, deploy_allowed): a build
# and API restart only on a quiet desk. Run: bash scripts/tests/desk-deploy-gate.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, expected (0 allowed / 1 refused), actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected $2, got $3)"; FAILS=$((FAILS + 1)); fi
}

STATE="$(mktemp -d)"
trap 'rm -rf "$STATE"' EXIT
DESK_STATE_DIR="$STATE"
# Load only the gate, with the state dir pointed at a scratch folder.
eval "$(sed -n '/^# --- when a deploy may build and restart/,/^live_runs() {/p' scripts/lib/desk-common.sh | sed '$d')"
DEPLOY_NOW_FILE="$STATE/deploy-now"

gate() { deploy_allowed "$@"; echo $?; }

echo "deploy_allowed"
check "weekend, runs live: allowed (no market)"            0 "$(gate 6 1100 0 5)"
check "Sunday night: allowed"                               0 "$(gate 7 2300 0 0)"
check "weekday 08:00, nothing live: allowed"                0 "$(gate 1 0800 0 0)"
check "weekday 08:45, the morning job is starting: refused" 1 "$(gate 1 0845 0 0)"
check "weekday 11:28, 13 live runs: refused (24 Sep)"       1 "$(gate 4 1128 0 13)"
check "weekday 14:58, 10 live runs: refused (22 Sep)"       1 "$(gate 2 1458 0 10)"
check "weekday 16:00, crude still live: refused"            1 "$(gate 3 1600 0 2)"
check "weekday 16:00, nothing live, not closed: refused"    1 "$(gate 3 1600 0 0)"
check "weekday after the close, nothing live: allowed"      0 "$(gate 3 2340 1 0)"
check "weekday after the close, a run still live: refused"  1 "$(gate 3 2340 1 1)"
check "live count unknown (-1) counts as live"              1 "$(gate 3 0800 0 -1)"
check "a leading-zero clock is read as decimal (0859)"      1 "$(gate 1 0859 0 0)"
touch "$DEPLOY_NOW_FILE"
check "deploy-now overrides, even mid-session with runs"    0 "$(gate 4 1128 0 13)"
touch -t "$(date -v-2H +%Y%m%d%H%M 2>/dev/null || date -d '2 hours ago' +%Y%m%d%H%M)" "$DEPLOY_NOW_FILE"
check "a deploy-now two hours old is not honoured"          1 "$(gate 4 1128 0 13)"
rm -f "$DEPLOY_NOW_FILE"

echo "deploy_clock_allows (asked before the API is)"
clock() { deploy_clock_allows "$@"; echo $?; }
check "weekend"                                             0 "$(clock 7 1100 0)"
check "weekday before 08:40"                                0 "$(clock 1 0830 0)"
check "weekday in the session"                              1 "$(clock 1 1128 0)"
check "weekday after the close"                             0 "$(clock 1 2340 1)"

echo "deploy_block_reason"
r="$(deploy_block_reason 4 1128 0 13)"; case "$r" in *"13 live run"*) check "names the live runs" 0 0 ;; *) check "names the live runs: $r" 0 1 ;; esac
r="$(deploy_block_reason 4 1128 0 -1)"; case "$r" in *unknown*) check "says when the count is unknown" 0 0 ;; *) check "says when the count is unknown: $r" 0 1 ;; esac
r="$(deploy_block_reason 3 2253 0 0)"; case "$r" in *"at 23:58"*) check "names the time the close job opens the gate" 0 0 ;; *) check "names the time the close job opens the gate: $r" 0 1 ;; esac
r="$(CLOSE_AT=2340 deploy_block_reason 3 2253 0 0)"; case "$r" in *"at 23:40"*) check "follows a moved close time" 0 0 ;; *) check "follows a moved close time: $r" 0 1 ;; esac

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
