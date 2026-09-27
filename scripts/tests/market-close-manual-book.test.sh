#!/usr/bin/env bash
# The nightly close (scripts/market-close.sh, step 1) stops every live run but
# the manual book: hand-placed positions are carried overnight (27 Sep). The
# step's functions are loaded from the script itself, between its
# ">>> live-runs" and "<<< live-runs" markers, with the API replaced by stubs
# that record every stop asked for. Run: bash scripts/tests/market-close-manual-book.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.."

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

run_case() {  # runs JSON, [dry run 0/1], [stop answers ok 0/1]
  (
    OUT="$(mktemp)"
    trap 'cat "$OUT"; rm -f "$OUT"' EXIT
    RUNS_JSON="$1"
    DRY_RUN="${2:-0}"
    STOP_OK="${3:-1}"
    API_UP=1
    stopped=0
    failed=0
    say() { echo "say: $*" >>"$OUT"; }
    warn() { echo "warn: $*" >>"$OUT"; }
    api_get() { echo "api_get $1" >>"$OUT"; printf '%s' "$RUNS_JSON"; }
    api_post() {
      echo "api_post $1 $2" >>"$OUT"
      [ "$STOP_OK" = 1 ] && { echo '{"wasRunning":true}'; return 0; }
      return 1
    }
    eval "$(sed -n '/^# >>> live-runs/,/^# <<< live-runs/p' scripts/market-close.sh)"
    stop_live_runs
    echo "stopped=$stopped failed=$failed" >>"$OUT"
  )
}

BOOK_AND_TWO_RUNS='[
  {"runId":41,"strategyName":"Ghost","underlying":"NIFTY","isActive":true},
  {"runId":7,"strategyName":"Manual","underlying":"MANUAL","isActive":true},
  {"runId":42,"strategyName":"CrudeMomentum","underlying":"CRUDEOIL","isActive":true}
]'

echo "Two strategy runs and a manual book are Running at the close:"
out="$(run_case "$BOOK_AND_TWO_RUNS")"
check "asks for Running runs" "$(grep -q 'api_get /api/Strategy/runs?status=Running' <<<"$out"; echo $?)"
check "stops run 41 with flatten" "$(grep -q 'api_post /api/Strategy/runs/41/stop {"flatten":true}' <<<"$out"; echo $?)"
check "stops run 42 with flatten" "$(grep -q 'api_post /api/Strategy/runs/42/stop {"flatten":true}' <<<"$out"; echo $?)"
check "never stops the manual book" "$(! grep -q 'runs/7/stop' <<<"$out"; echo $?)"
check "never flattens anything but the two runs" "$(test "$(grep -c '^api_post' <<<"$out")" = 2; echo $?)"
check "says the book was left open" "$(grep -q 'manual book(s) left open — hand-placed positions carry overnight: 7' <<<"$out"; echo $?)"
check "counts two stops" "$(grep -q 'stopped=2 failed=0' <<<"$out"; echo $?)"

echo "Only the manual book is Running:"
out="$(run_case '[{"runId":7,"strategyName":"Manual","isActive":true}]')"
check "stops nothing" "$(! grep -q '^api_post' <<<"$out"; echo $?)"
check "reports no live runs" "$(grep -q 'say: live runs: none open' <<<"$out"; echo $?)"
check "still names the book it kept" "$(grep -q 'left open.*: 7' <<<"$out"; echo $?)"

echo "The runs list comes back wrapped ({\"items\": [...]}):"
out="$(run_case '{"items":[{"runId":9,"strategyName":"Manual"},{"runId":10,"strategyName":"Fulcrum"}]}')"
check "stops the strategy run" "$(grep -q 'runs/10/stop' <<<"$out"; echo $?)"
check "keeps the book" "$(! grep -q 'runs/9/stop' <<<"$out"; echo $?)"

echo "Dry run:"
out="$(run_case "$BOOK_AND_TWO_RUNS" 1)"
check "stops nothing" "$(! grep -q '^api_post' <<<"$out"; echo $?)"
check "would stop the two runs only" "$(grep -q 'would stop run 41' <<<"$out" && grep -q 'would stop run 42' <<<"$out" && ! grep -q 'would stop run 7' <<<"$out"; echo $?)"

echo "A stop the API refuses:"
out="$(run_case '[{"runId":41,"strategyName":"Ghost"}]' 0 0)"
check "is counted as failed and said" "$(grep -q 'stopped=0 failed=1' <<<"$out" && grep -q 'warn:   run 41 did not stop' <<<"$out"; echo $?)"

echo "A runs body that does not parse (an error page, a cut-off response):"
out="$(run_case '<html>502 Bad Gateway</html>')"
check "stops nothing rather than guessing" "$(! grep -q '^api_post' <<<"$out"; echo $?)"
check "and says so, not \"none open\"" "$(grep -q 'could not read the running list' <<<"$out" && ! grep -q 'none open' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
