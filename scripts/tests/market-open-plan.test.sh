#!/usr/bin/env bash
# shellcheck disable=SC2034  # variables here are read by the functions eval'd from market-open.sh
# How the morning job reads a plan line for one account (scripts/market-open.sh,
# parse_plan_line). Run: bash scripts/tests/market-open-plan.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}
eval "$(sed -n '/^# >>> plan-line/,/^# <<< plan-line/p' scripts/market-open.sh)"
p() { parse_plan_line "$1" "$2" 2 20; }

check "a plain line runs in every account"          "GhostTangentCrossings BANKNIFTY,NIFTY,SENSEX 2 20 -" "$(p 'GhostTangentCrossings  BANKNIFTY,NIFTY,SENSEX  2' coderforchange)"
check "no leg target given takes the default"       "ChainFlowBuy NIFTY 2 20 -"            "$(p 'ChainFlowBuy NIFTY 2' admin)"
check "'-' means no leg target"                     "CrudeMomentum CRUDEOIL 2 - -"         "$(p 'CrudeMomentum CRUDEOIL 2 -' admin)"
check "@admin runs for admin"                       "Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - -" "$(p 'Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - @admin' admin)"
check "@admin is skipped for coderforchange"        ""                                     "$(p 'Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - @admin' coderforchange)"
check "@ before the target is read the same"        "Fulcrum NIFTY 2 - -"                  "$(p 'Fulcrum NIFTY 2 @admin -' admin)"
check "@-lists hold several accounts"               "Ghost NIFTY 3 40 -"                   "$(p 'Ghost NIFTY 3 40 @admin,coderforchange' coderforchange)"
check "account names match whole, not by prefix"    ""                                     "$(p 'Ghost NIFTY 2 @administrator' admin)"
check "account names ignore case"                   "Ghost NIFTY 2 20 -"                   "$(p 'Ghost NIFTY 2 @Admin' admin)"
check "a missing lots column takes the default"     "Ghost NIFTY 2 20 -"                   "$(p 'Ghost NIFTY' admin)"
check "comments are skipped"                        ""                                     "$(p '# Ghost NIFTY 2' admin)"
check "blank lines are skipped"                     ""                                     "$(p '   ' admin)"
check "key=value fields are the run's settings"     "Ghost NIFTY 2 50 timeframe=60m"       "$(p 'Ghost NIFTY 2 50 timeframe=60m' admin)"
check "several settings, any order with the rest"   "TR NIFTY 2 - day_filters=off;itm_steps=1" "$(p 'TR NIFTY 2 day_filters=off - @admin itm_steps=1' admin)"
check "a setting is never read as the leg target"   "TR NIFTY 2 20 day_filters=off"        "$(p 'TR NIFTY 2 day_filters=off' admin)"

echo "the settings as the start's parameters (settings_json)"
eval "$(sed -n '/^# >>> settings-json/,/^# <<< settings-json/p' scripts/market-open.sh)"
check "no settings: an empty object"                '{}'                                    "$(settings_json -)"
check "numbers, true/false and words are typed"     '{"timeframe": "60m", "day_filters": "off", "itm_steps": 1, "x": 0.5, "on": true}' \
  "$(settings_json 'timeframe=60m;day_filters=off;itm_steps=1;x=0.5;on=true')"
check "a key that is not a plain name is dropped"   '{"ok": 1}'                             "$(settings_json 'Bad-Key=1;ok=1')"
check "day_stop and day_lock are not settings"      '{"timeframe": "60m"}'                  "$(settings_json 'timeframe=60m;day_stop=4000;day_lock=5000:3000')"

echo "the run's risk (risk_json): leg target, day stop and profit lock"
eval "$(sed -n '/^# >>> risk-json/,/^# <<< risk-json/p' scripts/market-open.sh)"
check "a leg target alone"                          '{"leg": {"targetPoints": 50.0}}'       "$(risk_json 50)"
check "no target, no settings: no rules"            '{}'                                    "$(risk_json -)"
check "day stop and the owner's ladder"             '{"overall": {"stopLoss": 4000.0, "lockSteps": [{"at": 5000.0, "keep": 3000.0}, {"at": 10000.0, "keep": 7000.0}, {"at": 20000.0, "keep": 15000.0}], "scope": "day"}}' \
  "$(risk_json - 'day_filters=off;day_stop=4000;day_lock=5000:3000,10000:7000,20000:15000')"
check "with a leg target beside them"               '{"leg": {"targetPoints": 50.0}, "overall": {"stopLoss": 4000.0, "scope": "day"}}' \
  "$(risk_json 50 'timeframe=60m;day_stop=4000')"

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
# Fulcrum came off the plan on 10 Oct: the owner stopped it on 1 Oct, and its line kept deploying it.
# coderforchange came off on 15 Nov: its runs loaded the box's CPU; admin alone trades the plan.
check "the plan's accounts are admin alone"         "admin" "$(grep -i '^[[:space:]]*accounts:' config/morning-plan.txt | head -1 | cut -d: -f2- | xargs)"
check "admin gets 5 runs"                           "5"  "$(count admin)"

echo "the tally's expected runs (expected_runs), from the same parser"
eval "$(sed -n '/^expected_runs() {/,/^}/p' scripts/market-open.sh)"
ACCOUNTS="$(grep -i '^[[:space:]]*accounts:' config/morning-plan.txt | head -1 | cut -d: -f2-)" LOTS_DEFAULT=2 LEG_TARGET_PTS=20
EXPECTED="$(expected_runs)"
check "5 planned runs in all"                      "5"  "$(printf '%s\n' "$EXPECTED" | grep -c .)"
check "one line per account, strategy, underlying"  "0"  "$(printf '%s\n' "$EXPECTED" | grep -vc '^[^|]*|[^|]*|[^|]*$')"
check "no Fulcrum in any account (stopped 1 Oct)"   "0"  "$(printf '%s\n' "$EXPECTED" | grep -c '|Fulcrum|')"
check "no run for coderforchange (off 15 Nov)"      "0"  "$(printf '%s\n' "$EXPECTED" | grep -c '^coderforchange|')"
# 17 Nov evening, the owner: from 18 Nov only TrendRider V2 on NIFTY; 18 Nov night: V2BE beside it; 20 Nov: V2OB;
# 22 Nov night: all four versions (TrendRider back on NIFTY). 25 Nov evening: Ghost V2 on 1-hour candles beside them.
check "crude is off (from 18 Nov)"                  "0"  "$(printf '%s\n' "$EXPECTED" | grep -c '|CrudeMomentum|')"
check "only the strategies the owner named"         "GhostTangentCrossingsV2 TrendRider TrendRiderV2 TrendRiderV2BE TrendRiderV2OB" \
  "$(printf '%s\n' "$EXPECTED" | cut -d'|' -f2 | sort -u | xargs)"
check "everything runs on NIFTY alone"             "admin|TrendRider|NIFTY admin|TrendRiderV2|NIFTY admin|TrendRiderV2BE|NIFTY admin|TrendRiderV2OB|NIFTY admin|GhostTangentCrossingsV2|NIFTY" \
  "$(printf '%s\n' "$EXPECTED" | xargs)"
check "TrendRider has no leg target"              "-" "$(parse_plan_line "$(grep '^TrendRider ' config/morning-plan.txt)" admin 2 20 | awk '{print $4}')"
check "TrendRiderV2 has no leg target"              "-" "$(parse_plan_line "$(grep '^TrendRiderV2 ' config/morning-plan.txt)" admin 2 20 | awk '{print $4}')"
check "TrendRiderV2BE has no leg target"            "-" "$(parse_plan_line "$(grep '^TrendRiderV2BE ' config/morning-plan.txt)" admin 2 20 | awk '{print $4}')"
check "TrendRiderV2OB has no leg target"            "-" "$(parse_plan_line "$(grep '^TrendRiderV2OB ' config/morning-plan.txt)" admin 2 20 | awk '{print $4}')"
for v in TrendRider TrendRiderV2 TrendRiderV2BE TrendRiderV2OB; do
  check "$v trades with its day rules off"         "day_filters=off" "$(parse_plan_line "$(grep "^$v " config/morning-plan.txt)" admin 2 20 | awk '{split($5, s, ";"); print s[1]}')"
done
check "Ghost V2: 1-hour candles, 50-point leg target" "2 50 timeframe=60m" "$(parse_plan_line "$(grep '^GhostTangentCrossingsV2 ' config/morning-plan.txt)" admin 2 20 | awk '{split($5, s, ";"); print $3, $4, s[1]}')"
# 25 Nov evening, the owner: every script ends its day at -4,000 and locks 5k->3k, 10k->7k, 20k->15k, each on its own.
for v in TrendRider TrendRiderV2 TrendRiderV2BE TrendRiderV2OB GhostTangentCrossingsV2; do
  check "$v: day stop 4,000 and the owner's ladder" "day_stop=4000;day_lock=5000:3000,10000:7000,20000:15000" \
    "$(parse_plan_line "$(grep "^$v " config/morning-plan.txt)" admin 2 20 | awk '{n = split($5, s, ";"); print s[n-1] ";" s[n]}')"
done

echo "a redeploy of some runs only (MARKET_OPEN_ONLY, as scripts/session-keeper.sh passes it)"
selected() { only_selected "$1" && echo yes || echo no; }
check "no list selects every run"                   "yes" "$(MARKET_OPEN_ONLY='' selected 'admin|Ghost|NIFTY')"
check "a named run is selected, ignoring case"      "yes" "$(MARKET_OPEN_ONLY='admin|trendriderv2|nifty' selected 'admin|TrendRiderV2|NIFTY')"
check "a run not named is not"                      "no"  "$(MARKET_OPEN_ONLY='admin|TrendRiderV2|NIFTY' selected 'admin|TrendRider|NIFTY')"
check "names match whole, not by prefix"            "no"  "$(MARKET_OPEN_ONLY='admin|TrendRider|NIFTY' selected 'admin|TrendRiderV2|NIFTY')"
check "a comma-separated list"                      "yes" "$(MARKET_OPEN_ONLY='admin|Ghost|NIFTY,admin|TrendRiderV2|NIFTY' selected 'admin|TrendRiderV2|NIFTY')"
check "the tally expects only the named runs"       "admin|TrendRiderV2|NIFTY" "$(MARKET_OPEN_ONLY='admin|TrendRiderV2|NIFTY' expected_runs | xargs)"
check "a name outside the plan selects nothing"     ""    "$(MARKET_OPEN_ONLY='admin|Ghost|NIFTY' expected_runs | xargs)"

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
