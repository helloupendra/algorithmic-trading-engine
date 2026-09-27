#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2319  # variables are read by the functions under test; "$(cond; echo $?)" is how check() takes a result
# The morning job's gates, on fixtures of the API's answers:
#   - the feed check (scripts/lib/morning_checks.py feed): every spot the plan
#     trades priced since today's open, 09:15 on NSE/BSE and 09:00 on MCX;
#   - the calendar (morning_checks.py session): a calendarWarning or no answer
#     is "unknown", never "trading";
#   - the order the plan step starts runs in (plan_runs in scripts/market-open.sh):
#     interleaved per strategy and underlying across the accounts.
# Run: bash scripts/tests/market-open-gates.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}
same() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}

NOW="2026-09-28T09:17:30+05:30"
PLAN_SPOTS="BANKNIFTY,NIFTY,SENSEX,CRUDEOIL"

feed() {  # quotes JSON -> the report; its exit status is appended as "rc=N"
  local out rc
  out="$(printf '%s' "$1" | python3 scripts/lib/morning_checks.py feed --underlyings "${2:-$PLAN_SPOTS}" --now "$NOW")"; rc=$?
  printf '%s\nrc=%s\n' "$out" "$rc"
}

quote() {  # symbol exchangeTimestampUtc [updatedUtc] -> one quote object
  printf '{"symbol":"%s","lastTradedPrice":100.5,"exchangeTimestampUtc":%s,"updatedUtc":"%s"}' \
    "$1" "$( [ "$2" = null ] && echo null || echo "\"$2\"")" "${3:-2026-09-28T03:47:29Z}"
}

TODAY_0917="2026-09-28T03:47:20Z"        # 09:17:20 IST
TODAY_0905="2026-09-28T03:35:00Z"        # 09:05:00 IST: after the MCX open, before NSE's
PREOPEN_0907="2026-09-28T03:37:59Z"      # 09:07:59 IST: NSE's pre-open equilibrium
FRIDAY_CLOSE="2026-09-25T09:59:59Z"      # 15:29:59 IST last Friday

echo "The feed check"
ALL_FRESH="[$(quote NSE:NIFTY50-INDEX $TODAY_0917),$(quote NSE:NIFTYBANK-INDEX $TODAY_0917),$(quote BSE:SENSEX-INDEX $TODAY_0917),$(quote MCX:CRUDEOIL26OCTFUT $TODAY_0917),$(quote MCX:CRUDEOILM26OCTFUT $FRIDAY_CLOSE)]"
out="$(feed "$ALL_FRESH")"
check "all spots fresh: pass" "$(test "$(head -1 <<<"$out")" = pass; echo $?)"
check "and exits 0" "$(grep -q '^rc=0$' <<<"$out"; echo $?)"
check "four of four" "$(grep -q '^4 of 4 spot price' <<<"$out"; echo $?)"

MCX_ONLY="[$(quote NSE:NIFTY50-INDEX $PREOPEN_0907),$(quote NSE:NIFTYBANK-INDEX $FRIDAY_CLOSE),$(quote BSE:SENSEX-INDEX $FRIDAY_CLOSE),$(quote MCX:CRUDEOIL26OCTFUT $TODAY_0917)]"
out="$(feed "$MCX_ONLY")"
check "MCX fresh, NSE stale: fail (the old check passed on this)" "$(test "$(head -1 <<<"$out")" = fail; echo $?)"
check "and exits 2" "$(grep -q '^rc=2$' <<<"$out"; echo $?)"
check "a pre-open print is not the session" "$(grep -q '^  missing: NIFTY (NSE:NIFTY50-INDEX) — nothing since 09:15 IST; last price 09:07:59 IST' <<<"$out"; echo $?)"
check "names last Friday's price for what it is" "$(grep -q 'missing: SENSEX (BSE:SENSEX-INDEX) — nothing since 09:15 IST; last price 25 Sep 15:29:59 IST' <<<"$out"; echo $?)"
check "crude is fresh" "$(grep -q '^  fresh:   CRUDEOIL (MCX:CRUDEOIL futures)' <<<"$out"; echo $?)"

NO_SENSEX="[$(quote NSE:NIFTY50-INDEX $TODAY_0917),$(quote NSE:NIFTYBANK-INDEX $TODAY_0917),$(quote MCX:CRUDEOIL26OCTFUT $TODAY_0917)]"
out="$(feed "$NO_SENSEX")"
check "one index missing, the session flowing: partial" "$(test "$(head -1 <<<"$out")" = partial; echo $?)"
check "names it" "$(grep -q '^  missing: SENSEX (BSE:SENSEX-INDEX) — nothing since 09:15 IST; no price at all' <<<"$out"; echo $?)"
check "exits 1" "$(grep -q '^rc=1$' <<<"$out"; echo $?)"

out="$(feed "[$(quote NSE:NIFTY50-INDEX $TODAY_0905),$(quote MCX:CRUDEOIL26OCTFUT $TODAY_0905)]" NIFTY,CRUDEOIL)"
check "09:05 is fresh on MCX (opens 09:00) and not on NSE (09:15)" "$(grep -q 'fresh:   CRUDEOIL' <<<"$out" && grep -q 'missing: NIFTY' <<<"$out"; echo $?)"

out="$(feed "[$(quote NSE:NIFTY50-INDEX null 2026-09-28T03:47:10Z)]" NIFTY)"
check "no exchange stamp: the arrival time stands in" "$(test "$(head -1 <<<"$out")" = pass; echo $?)"
out="$(feed "[$(quote NSE:NIFTY50-INDEX 2026-09-27T18:30:00Z 2026-09-28T03:47:10Z)]" NIFTY)"
check "a date-only stamp (00:00 IST): the arrival time stands in" "$(test "$(head -1 <<<"$out")" = pass; echo $?)"
out="$(feed '{"items":[{"symbol":"NSE:NIFTY50-INDEX","lastTradedPrice":null,"exchangeTimestampUtc":"2026-09-28T03:47:20Z"}]}' NIFTY)"
check "a row without a price is no price (wrapped answer read too)" "$(test "$(head -1 <<<"$out")" = fail; echo $?)"
out="$(feed '<html>502 Bad Gateway</html>')"
check "an answer that does not parse: fail, not pass" "$(test "$(head -1 <<<"$out")" = fail; echo $?)"
out="$(feed "[$(quote MCX:CRUDEOILM26OCTFUT $TODAY_0917)]" CRUDEOIL)"
check "crude mini's price is not crude's" "$(test "$(head -1 <<<"$out")" = fail; echo $?)"

echo "The calendar"
session() {  # answered body [today] -> the three lines, joined by |
  printf '%s' "$2" | python3 scripts/lib/morning_checks.py session --answered "$1" --today "${3:-2026-09-28}" | paste -sd'|' -
}
same "an ordinary trading day" "trading||" "$(session yes '{"isTradingDay":true,"calendarWarning":null}')"
same "a holiday, by name" "holiday|Mahatma Gandhi Jayanti|" \
  "$(session yes '{"isTradingDay":false,"isHoliday":true,"holidayName":"Mahatma Gandhi Jayanti"}')"
# The bodies are built outside $(...): bash 3.2 mangles \" inside "$(...)".
warned() { printf '{"isTradingDay":true,"calendarWarning":"%s"}' "$1"; }
W="No NSE holiday calendar is loaded for 2026; only weekends are known to be closed."
BODY="$(warned "$W")"
same "calendarWarning: unknown, with the warning" "unknown|$W|$W" "$(session yes "$BODY")"
W="The holiday calendar could not be loaded; only weekends are known to be closed."
BODY="$(warned "$W")"
same "a calendar that did not load: unknown" "unknown|$W|$W" "$(session yes "$BODY")"
W="The NSE holiday calendar for 2027 is not loaded yet."
BODY="$(warned "$W")"
same "December's warning about NEXT year: today is still known" "trading||$W" "$(session yes "$BODY" 2026-12-01)"
same "a non-2xx answer: unknown" "unknown|the API did not answer whether today is a trading day|" \
  "$(session no '{"title":"Unauthorized"}')"
same "an answer that does not parse: unknown" "unknown|the API did not answer whether today is a trading day|" \
  "$(session yes '<html>502</html>')"

echo "The order the plan starts in"
eval "$(sed -n '/^# >>> plan-line/,/^# <<< plan-line/p' scripts/market-open.sh)"
PLAN_FIXTURE='Ghost  BANKNIFTY,NIFTY  2
Fulcrum NIFTY 2 - @admin
Crude  CRUDEOIL  1  -'
ORDER="$(plan_runs "admin coderforchange" 2 20 <<<"$PLAN_FIXTURE" | tr '\n' ' ')"
same "interleaved per strategy and underlying, accounts inner" \
  "admin|Ghost|BANKNIFTY|2|20 coderforchange|Ghost|BANKNIFTY|2|20 admin|Ghost|NIFTY|2|20 coderforchange|Ghost|NIFTY|2|20 admin|Fulcrum|NIFTY|2|- admin|Crude|CRUDEOIL|1|- coderforchange|Crude|CRUDEOIL|1|- " \
  "$ORDER"
REAL="$(grep -v '^[[:space:]]*#' config/morning-plan.txt | grep -v '^[[:space:]]*$' | grep -vi '^[[:space:]]*accounts:')"
FIRST_TWO="$(plan_runs "admin coderforchange" 2 20 <<<"$REAL" | head -2 | cut -d'|' -f1-3 | tr '\n' ' ')"
same "the real plan: both accounts' first run back to back" \
  "admin|GhostTangentCrossings|BANKNIFTY coderforchange|GhostTangentCrossings|BANKNIFTY " "$FIRST_TWO"
same "the feed check covers what the real plan trades" "BANKNIFTY,NIFTY,SENSEX,CRUDEOIL" \
  "$(plan_runs "admin coderforchange" 2 20 <<<"$REAL" | cut -d'|' -f3 | awk '!seen[$0]++' | paste -sd, -)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
