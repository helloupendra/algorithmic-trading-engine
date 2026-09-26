#!/usr/bin/env bash
# How the morning job reads a plan line for one account (scripts/market-open.sh,
# parse_plan_line). Run: bash scripts/tests/market-open-plan.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.."

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}
eval "$(sed -n '/^# >>> plan-line/,/^# <<< plan-line/p' scripts/market-open.sh)"
p() { parse_plan_line "$1" "$2" 2 20; }

check "a plain line runs in every account"          "GhostTangentCrossings BANKNIFTY,NIFTY,SENSEX 2 20" "$(p 'GhostTangentCrossings  BANKNIFTY,NIFTY,SENSEX  2' coderforchange)"
check "no leg target given takes the default"       "ChainFlowBuy NIFTY 2 20"              "$(p 'ChainFlowBuy NIFTY 2' admin)"
check "'-' means no leg target"                     "CrudeMomentum CRUDEOIL 2 -"           "$(p 'CrudeMomentum CRUDEOIL 2 -' admin)"
check "@admin runs for admin"                       "Fulcrum BANKNIFTY,NIFTY,SENSEX 2 -"   "$(p 'Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - @admin' admin)"
check "@admin is skipped for coderforchange"        ""                                     "$(p 'Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - @admin' coderforchange)"
check "@ before the target is read the same"        "Fulcrum NIFTY 2 -"                    "$(p 'Fulcrum NIFTY 2 @admin -' admin)"
check "@-lists hold several accounts"               "Ghost NIFTY 3 40"                     "$(p 'Ghost NIFTY 3 40 @admin,coderforchange' coderforchange)"
check "account names match whole, not by prefix"    ""                                     "$(p 'Ghost NIFTY 2 @administrator' admin)"
check "account names ignore case"                   "Ghost NIFTY 2 20"                     "$(p 'Ghost NIFTY 2 @Admin' admin)"
check "a missing lots column takes the default"     "Ghost NIFTY 2 20"                     "$(p 'Ghost NIFTY' admin)"
check "comments are skipped"                        ""                                     "$(p '# Ghost NIFTY 2' admin)"
check "blank lines are skipped"                     ""                                     "$(p '   ' admin)"

echo "the real plan file, per account"
PLAN="$(grep -v '^[[:space:]]*#' config/morning-plan.txt | grep -v '^[[:space:]]*$' | grep -vi '^[[:space:]]*accounts:')"
count() {  # account -> runs the plan starts for it
  local n=0 line parsed
  while IFS= read -r line; do
    parsed="$(parse_plan_line "$line" "$1" 2 20)"; [ -n "$parsed" ] || continue
    set -- "$1" $parsed; n=$((n + $(printf '%s' "$3" | tr ',' '\n' | grep -c .)))
  done <<<"$PLAN"
  echo "$n"
}
check "admin gets 13 runs (Fulcrum included)"       "13" "$(count admin)"
check "coderforchange gets 10 runs (no Fulcrum)"    "10" "$(count coderforchange)"

echo "the tally's expected runs (expected_runs), from the same parser"
eval "$(sed -n '/^expected_runs() {/,/^}/p' scripts/market-open.sh)"
ACCOUNTS="admin coderforchange" LOTS_DEFAULT=2 LEG_TARGET_PTS=20
EXPECTED="$(expected_runs)"
check "23 planned runs in all"                      "23" "$(printf '%s\n' "$EXPECTED" | grep -c .)"
check "one line per account, strategy, underlying"  "0"  "$(printf '%s\n' "$EXPECTED" | grep -vc '^[^|]*|[^|]*|[^|]*$')"
check "no Fulcrum for coderforchange"               "0"  "$(printf '%s\n' "$EXPECTED" | grep -c '^coderforchange|Fulcrum|')"
check "Fulcrum on three indices for admin"          "3"  "$(printf '%s\n' "$EXPECTED" | grep -c '^admin|Fulcrum|')"
check "crude in both accounts"                      "2"  "$(printf '%s\n' "$EXPECTED" | grep -c '|CrudeMomentum|CRUDEOIL$')"

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
