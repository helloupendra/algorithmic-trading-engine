#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2319  # variables are read by the functions under test; "$(cond; echo $?)" is how check() takes a result
# The morning's instrument master refresh (scripts/market-open.sh, step 3b2):
#   - the reading of GET /api/Instruments/masters (morning_checks.py masters):
#     running until the job is over, then done or failed, a line per master;
#   - the step itself, loaded from the script between its ">>> masters-refresh"
#     and "<<< masters-refresh" markers with the API, the clock and sleep
#     stubbed: it waits for the refresh, logs each master, and on a failure, a
#     refusal or a timeout it warns and carries on. It never stops the open.
# Run: bash scripts/tests/market-open-masters.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO_ROOT="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}
same() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}

SINCE="2026-10-15T03:15:00Z"   # 08:45 IST
FRESH="2026-10-15T03:15:20Z"
OLD="2026-09-09T02:00:00Z"

master() {  # name, startedUtc, ok, [error]
  printf '{"name":"%s","lastRefresh":{"name":"%s","startedUtc":"%s","finishedUtc":"%s","ok":%s,"error":%s,"inserted":1234,"updated":56789,"skipped":0}}' \
    "$1" "$1" "$2" "$2" "$3" "$( [ -n "${4:-}" ] && printf '"%s"' "$4" || echo null)"
}
body() {  # isRunning, masters JSON...
  local running="$1"; shift
  local IFS=,
  printf '{"directory":"/srv/data/instruments","masters":[%s],"job":{"isRunning":%s,"current":%s}}' \
    "$*" "$running" "$( [ "$running" = true ] && echo '"NSE_FO"' || echo null)"
}
verdict() {  # body, [since] -> the report on one line, "|" between lines
  printf '%s' "$1" | python3 scripts/lib/morning_checks.py masters --since "${2-$SINCE}" | paste -sd'|' -
}

echo "Reading the masters"
same "a job still going is running" "running|refreshing NSE_FO" \
  "$(verdict "$(body true "$(master NSE_CM "$FRESH" true)")")"
same "every master refreshed since the call: done, with the counts" \
  "done|NSE_CM: 1,234 inserted, 56,789 updated, 0 skipped|NSE_FO: 1,234 inserted, 56,789 updated, 0 skipped" \
  "$(verdict "$(body false "$(master NSE_CM "$FRESH" true)" "$(master NSE_FO "$FRESH" true)")")"
same "one that failed: failed, with its error (not FAILED, which Sentinel reads as the job failing)" \
  "failed|NSE_CM: 1,234 inserted, 56,789 updated, 0 skipped|NSE_FO: refresh failed — 503 Service Unavailable" \
  "$(verdict "$(body false "$(master NSE_CM "$FRESH" true)" "$(master NSE_FO "$FRESH" false "503 Service Unavailable")")")"
same "one the job did not reach: failed" \
  "failed|NSE_CM: 1,234 inserted, 56,789 updated, 0 skipped|NSE_FO: not refreshed by this run (its last refresh began 2026-09-09 02:00 UTC)" \
  "$(verdict "$(body false "$(master NSE_CM "$FRESH" true)" "$(master NSE_FO "$OLD" true)")")"
same "without --since (a refresh someone else started) any finished refresh counts" \
  "done|NSE_FO: 1,234 inserted, 56,789 updated, 0 skipped" \
  "$(verdict "$(body false "$(master NSE_FO "$OLD" true)")" "")"
same "an answer that does not parse: unknown" "unknown|the API's answer about the masters could not be read" \
  "$(verdict '<html>502</html>')"

# The step, with the API answering from a scenario: POST's status and body,
# then GET's body for each poll (the last one repeats).
run_step() {  # post_rc, post_body, clock HHMM, bodies...
  (
    OUT="$(mktemp)"
    trap 'cat "$OUT"; rm -f "$OUT"' EXIT
    POST_RC="$1"; POST_BODY="$2"; FAKE_HHMM="$3"; shift 3
    BODIES=("$@")
    SLEEPS=0
    say() { echo "say: $*" >>"$OUT"; }
    warn() { echo "warn: $*" >>"$OUT"; }
    api_post() { echo "api_post $1" >>"$OUT"; printf '%s' "$POST_BODY"; return "$POST_RC"; }
    api_get() {
      local i=$SLEEPS
      [ "$i" -lt "${#BODIES[@]}" ] || i=$(( ${#BODIES[@]} - 1 ))
      printf '%s' "${BODIES[$i]}"
    }
    sleep() { SLEEPS=$((SLEEPS + 1)); echo "sleep" >>"$OUT"; [ "$SLEEPS" -lt 100 ] || { echo "runaway" >>"$OUT"; exit 4; }; }
    date() {
      case "$*" in
        "-u +%Y-%m-%dT%H:%M:%SZ") echo "$SINCE" ;;
        +%H%M) echo "$FAKE_HHMM" ;;
        *) command date "$@" ;;
      esac
    }
    MARKET_OPEN_MASTERS_WAIT=60
    eval "$(sed -n '/^# >>> masters-refresh/,/^# <<< masters-refresh/p' scripts/market-open.sh)"
    refresh_masters
    echo "exit: $?" >>"$OUT"
  )
}

RUNNING="$(body true "$(master NSE_FO "$OLD" true)")"
DONE="$(body false "$(master NSE_CM "$FRESH" true)" "$(master NSE_FO "$FRESH" true)")"
HALF="$(body false "$(master NSE_CM "$FRESH" true)" "$(master NSE_FO "$FRESH" false "Response status code does not indicate success: 503")")"

echo "The step: started, running for two polls, then done"
out="$(run_step 0 '{"message":"Refresh started."}' 0846 "$RUNNING" "$RUNNING" "$DONE")"
check "asks for the refresh" "$(grep -q 'api_post /api/Instruments/masters/refresh' <<<"$out"; echo $?)"
same "polls until the job is over" 2 "$(grep -c '^sleep' <<<"$out")"
check "one line per master, with its counts" \
  "$(grep -q 'say:   NSE_CM: 1,234 inserted, 56,789 updated, 0 skipped' <<<"$out" \
     && grep -q 'say:   NSE_FO: 1,234 inserted' <<<"$out"; echo $?)"
check "no warning" "$(! grep -q '^warn' <<<"$out"; echo $?)"
check "carries on" "$(grep -q 'exit: 0' <<<"$out"; echo $?)"

echo "The step: a master fails"
out="$(run_step 0 '{"message":"Refresh started."}' 0846 "$HALF")"
check "warns with the master and its error" \
  "$(grep -q 'warn: instrument master NSE_FO: refresh failed — Response status code' <<<"$out"; echo $?)"
check "still logs the one that worked" "$(grep -q 'say:   NSE_CM: 1,234 inserted' <<<"$out"; echo $?)"
check "carries on" "$(grep -q 'exit: 0' <<<"$out"; echo $?)"

echo "The step: one already running (started from the console)"
out="$(run_step 1 '{"message":"A masters refresh is already running."}' 0846 "$RUNNING" "$(body false "$(master NSE_FO "$OLD" true)")")"
check "says so and waits for it" "$(grep -q 'already running; waiting for it' <<<"$out" && grep -q '^sleep' <<<"$out"; echo $?)"
check "its result counts, though it began before the call" "$(grep -q 'say:   NSE_FO: 1,234 inserted' <<<"$out"; echo $?)"
check "no warning" "$(! grep -q '^warn' <<<"$out"; echo $?)"

echo "The step: the API refuses or does not answer"
out="$(run_step 1 '' 0846 "$DONE")"
check "warns and does not wait" \
  "$(grep -q 'warn: the instrument masters could not be refreshed (the API did not answer)' <<<"$out" \
     && ! grep -q '^sleep' <<<"$out"; echo $?)"
check "carries on" "$(grep -q 'exit: 0' <<<"$out"; echo $?)"

echo "The step: still running when the wait is up"
out="$(run_step 0 '{"message":"Refresh started."}' 0846 "$RUNNING")"
same "waits MARKET_OPEN_MASTERS_WAIT (60 s) in 10 s polls" 6 "$(grep -c '^sleep' <<<"$out")"
check "warns that it carries on" "$(grep -q 'warn: the instrument master refresh had not finished after 60s' <<<"$out"; echo $?)"
check "carries on" "$(grep -q 'exit: 0' <<<"$out"; echo $?)"
out="$(run_step 0 '{"message":"Refresh started."}' 0912 "$RUNNING")"
check "never past 09:12, whatever the wait" \
  "$(! grep -q '^sleep' <<<"$out" && grep -q 'had not finished after 0s' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
