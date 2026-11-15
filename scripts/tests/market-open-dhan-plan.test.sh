#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2016  # STATUS_JSON is read by the api_get stub; the grep patterns are literal script text
# The morning's Dhan check (scripts/market-open.sh, step 3c) must tell "the data plan has ended"
# apart from "not signed in": a lapsed plan can't be fixed by a sign-in or a wait, so the morning
# goes straight to FYERS (16 Nov 2026). dhan_state is loaded from the script itself, between its
# ">>> dhan-state" and "<<< dhan-state" markers, with the API stubbed.
# Run: bash scripts/tests/market-open-dhan-plan.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
expect() {  # description, status JSON, expected first field
  STATUS_JSON="$2"
  local got
  got="$(dhan_state)"
  if [ "${got%%|*}" = "$3" ]; then echo "  ok   $1 ($got)"; else echo "  FAIL $1: wanted $3, got $got"; FAILS=$((FAILS + 1)); fi
}

api_get() { printf '%s' "$STATUS_JSON"; }
# eval, not source <(…): macOS's bash 3.2 can't source a process substitution.
eval "$(sed -n '/^# >>> dhan-state/,/^# <<< dhan-state/p' scripts/market-open.sh)"
type dhan_state >/dev/null 2>&1 || { echo "FAIL dhan_state was not found between the markers"; exit 1; }

future="$(TZ=Asia/Kolkata date -v+1d '+%Y-%m-%d %H:%M:%S' 2>/dev/null || TZ=Asia/Kolkata date -d tomorrow '+%Y-%m-%d %H:%M:%S')"
past="$(TZ=Asia/Kolkata date -v-1d '+%Y-%m-%d %H:%M:%S' 2>/dev/null || TZ=Asia/Kolkata date -d yesterday '+%Y-%m-%d %H:%M:%S')"
ends="$(date -u -v+20H '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+20 hours' '+%Y-%m-%dT%H:%M:%SZ')"

echo "dhan_state"
expect "signed in, plan active" "{\"ok\":true,\"signInExpiresUtc\":\"$ends\",\"dataPlan\":\"Active\",\"dataValidity\":\"$future.0\"}" ok
expect "signed in, plan not active" "{\"ok\":true,\"signInExpiresUtc\":\"$ends\",\"dataPlan\":\"Deactive\"}" plan
expect "signed in, plan validity passed" "{\"ok\":true,\"signInExpiresUtc\":\"$ends\",\"dataPlan\":\"Active\",\"dataValidity\":\"$past.0\"}" plan
expect "Dhan answered 806 (not subscribed)" '{"ok":false,"notSubscribed":true,"error":"Data APIs not Subscribed"}' plan
expect "not signed in" '{"ok":false,"authFailure":true,"error":"Invalid token"}' no
expect "no plan fields at all: judged by the token only" "{\"ok\":true,\"signInExpiresUtc\":\"$ends\"}" ok
expect "the API did not answer" 'not json' no

echo "the step 3c branches"
grep -qF 'if [ "${DHAN%%|*}" = plan ]; then' scripts/market-open.sh; c1=$?
grep -qF 'while [ "${DHAN%%|*}" = no ]' scripts/market-open.sh; c2=$?
if [ "$c1" = 0 ] && [ "$c2" = 0 ]; then echo "  ok   a lapsed plan skips the sign-in and the wait for Dhan"; else echo "  FAIL step 3c does not branch on plan"; FAILS=$((FAILS + 1)); fi

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
