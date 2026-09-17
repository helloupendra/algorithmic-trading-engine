#!/usr/bin/env bash
# The morning's FYERS gate (scripts/market-open.sh, step 4): when Dhan is the
# day's feed the strategies must not wait for the FYERS sign-in; when it is not,
# they must. The gate's functions are loaded from the script itself, between its
# ">>> fyers-gate" and "<<< fyers-gate" markers, with the API, the clock and the
# notifications replaced by stubs. Run: bash scripts/tests/market-open-fyers-gate.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.."

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

run_case() {  # name, dhan_primary, connected_after_sleeps (-1 = never, 0 = already), clock HHMM
  (
    OUT="$(mktemp)"
    trap 'cat "$OUT"' EXIT   # fail() exits the case; its lines must still reach the checks
    SLEEPS=0
    CONNECT_AFTER="$3"
    FAKE_HHMM="$4"
    LOGIN_WAIT_UNTIL=1430
    CONSOLE=http://console
    IS_MAC=false
    say() { echo "say: $*" >>"$OUT"; }
    warn() { echo "warn: $*" >>"$OUT"; }
    notify() { echo "notify: $2" >>"$OUT"; }
    fail() { echo "fail: $*" >>"$OUT"; exit 3; }
    api_post() { echo "api_post $1" >>"$OUT"; echo '{"connected":false}'; }
    api_get() {
      if [ "$CONNECT_AFTER" -ge 0 ] && [ "$SLEEPS" -ge "$CONNECT_AFTER" ]; then
        echo '{"isAuthenticated":true}'
      else
        echo '{"isAuthenticated":false}'
      fi
    }
    sleep() { SLEEPS=$((SLEEPS + 1)); echo "sleep" >>"$OUT"; [ "$SLEEPS" -lt 50 ] || { echo "runaway" >>"$OUT"; exit 4; }; }
    date() {
      case "${1:-}" in
        +%H%M) echo "$FAKE_HHMM" ;;
        +%s) echo 1000 ;;
        *) echo "09:20" ;;
      esac
    }
    eval "$(sed -n '/^# >>> fyers-gate/,/^# <<< fyers-gate/p' scripts/market-open.sh)"
    fyers_gate "$2"
    echo "exit: $?" >>"$OUT"
  )
}

echo "Dhan is the feed, FYERS not signed in, never signs in:"
out="$(run_case dhan 1 -1 0900)"
check "carries on without waiting" "$(grep -q 'exit: 0' <<<"$out"; echo $?)"
check "does not sleep" "$(! grep -q '^sleep' <<<"$out"; echo $?)"
check "says why" "$(grep -q 'not waiting for FYERS: Dhan is today' <<<"$out"; echo $?)"
check "still asks for the sign-in" "$(grep -q 'notify: FYERS is not signed in' <<<"$out"; echo $?)"
check "tried the refresh first" "$(grep -q 'api_post /api/auth/refresh-token' <<<"$out"; echo $?)"

echo "FYERS is the feed (no Dhan), sign-in arrives after two polls:"
out="$(run_case fyers 0 2 0905)"
check "waits for the sign-in" "$(test "$(grep -c '^sleep' <<<"$out")" = 2; echo $?)"
check "then carries on" "$(grep -q 'signed in at' <<<"$out" && grep -q 'exit: 0' <<<"$out"; echo $?)"

echo "FYERS already signed in:"
out="$(run_case already 0 0 0900)"
check "no refresh, no wait" "$(grep -q 'already valid' <<<"$out" && ! grep -q 'api_post' <<<"$out" && ! grep -q '^sleep' <<<"$out"; echo $?)"

echo "FYERS is the feed and nobody signs in by 14:30:"
out="$(run_case late 0 -1 1430)"
check "fails the run" "$(grep -q 'fail: no FYERS sign-in by 1430' <<<"$out"; echo $?)"

echo "Dhan went silent after the open (step 6) and FYERS is not signed in:"
out="$(run_case fallback 0 1 0930)"
check "waits for FYERS before its feed starts" "$(grep -q '^sleep' <<<"$out" && grep -q 'signed in at' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
