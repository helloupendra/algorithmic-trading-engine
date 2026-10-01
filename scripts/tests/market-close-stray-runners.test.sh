#!/usr/bin/env bash
# shellcheck disable=SC2319  # "$(cond; echo $?)" is how check() takes a result
# The nightly close (scripts/market-close.sh, step 4) names every strategy
# runner still alive with no open run behind it. The step's function is loaded
# from the script itself, between its ">>> stray-runners" and "<<< stray-runners"
# markers. Run: bash scripts/tests/market-close-stray-runners.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

eval "$(sed -n '/^# >>> stray-runners/,/^# <<< stray-runners/p' scripts/market-close.sh)"

RUNNERS='4101 /srv/.venv/bin/python /srv/strategies/execution_runner.py --strategy Fulcrum --strategy-id 7 --run-id 41 --underlying NIFTY
4202 /srv/.venv/bin/python /srv/strategies/execution_runner.py --strategy Ghost --strategy-id 9 --run-id 42 --underlying SENSEX
4303 /srv/.venv/bin/python /srv/strategies/execution_runner.py --strategy Ghost --strategy-id 9 --run-id=43 --underlying NIFTY'

echo "Run 42 is still open; 41 and 43 are not:"
out="$(stray_runners "$RUNNERS" '[{"runId":42,"strategyName":"Ghost","status":"Running"}]')"
check "names the runner of run 41" "$(grep -q '^4101 ' <<<"$out"; echo $?)"
check "names the runner of run 43 (--run-id=43)" "$(grep -q '^4303 ' <<<"$out"; echo $?)"
check "leaves the runner of the open run alone" "$(! grep -q '^4202 ' <<<"$out"; echo $?)"

echo "The runs list comes back wrapped ({\"items\": [...]}) and every run is open:"
out="$(stray_runners "$RUNNERS" '{"items":[{"runId":41},{"runId":42},{"runId":43}]}')"
check "names nothing" "$(test -z "$out"; echo $?)"

echo "Run 43 is a market replay's recap run, listed only with recap=true:"
out="$(stray_runners "$RUNNERS" '[{"runId":42}]' '[{"runId":43}]')"
check "leaves the replay's runner alone" "$(! grep -q '^4303 ' <<<"$out"; echo $?)"
check "still names the runner of run 41" "$(grep -q '^4101 ' <<<"$out"; echo $?)"

echo "The recap list does not parse:"
out="$(stray_runners "$RUNNERS" '[{"runId":42}]' '<html>502</html>')"
check "judges by the open runs alone" "$(grep -q '^4303 ' <<<"$out"; echo $?)"

echo "A runner started by hand, with no --run-id:"
out="$(stray_runners '5000 python strategies/execution_runner.py --strategy Fulcrum' '[]')"
check "is named" "$(grep -q '^5000 ' <<<"$out"; echo $?)"

echo "A runs body that does not parse (the API down, an error page):"
out="$(stray_runners "$RUNNERS" '<html>502 Bad Gateway</html>')"
check "says so rather than calling every runner a stray" "$(test "$out" = unreadable; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
