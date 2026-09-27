#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2154,SC2319  # variables are shared with the eval'd code under test; "$(cond; echo $?)" is how check() takes a result
# The desk's own housekeeping (27 Sep audit, item 23):
#   - the commit it last built survives a restart (desk.sh, ">>> deployed-sha"),
#     and a deploy cut off halfway is said and redone;
#   - an API restart in progress is visible to the desk (api-restarting), and
#     stops counting after five minutes;
#   - switches come from the environment, else .env (desk_setting);
#   - on Linux, --stop and --headless print the systemctl command, not act.
# Run: bash scripts/tests/desk-hygiene.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/state" "$WORK/bin"
OUT="$WORK/out"
say() { echo "say: $*" >>"$OUT"; }
warn() { echo "warn: $*" >>"$OUT"; }

echo "The commit last built, across a desk restart"
G="$WORK/git"
git init -q "$G"
git -C "$G" -c user.name=t -c user.email=t@t commit -q --allow-empty -m one
FIRST="$(git -C "$G" rev-parse HEAD)"
git -C "$G" -c user.name=t -c user.email=t@t commit -q --allow-empty -m two
SECOND="$(git -C "$G" rev-parse HEAD)"
DESK_STATE_DIR="$WORK/state"
BLOCK="$(sed -n '/^# >>> deployed-sha/,/^# <<< deployed-sha/p' scripts/desk.sh)"
start_desk() { : >"$OUT"; ( cd "$G" && eval "$BLOCK" && load_deployed_sha && echo "$deployed_sha" ); }

check "a first start takes HEAD as built" "$(test "$(start_desk)" = "$SECOND"; echo $?)"
check "and records it" "$(test "$(cat "$WORK/state/deployed-sha")" = "$SECOND"; echo $?)"
printf '%s\n' "$FIRST" >"$WORK/state/deployed-sha"
check "restarted after a pull, before its build: the older commit is still the built one" \
  "$(test "$(start_desk)" = "$FIRST"; echo $?)"
printf 'nonsense\n' >"$WORK/state/deployed-sha"
check "a recorded sha that is not a commit here: HEAD, said" \
  "$(test "$(start_desk)" = "$SECOND" && grep -q 'warn: the recorded deployed commit nonsense is not in this checkout' "$OUT"; echo $?)"
printf 'pulling abc1234 -> def5678 at 2026-09-28 11:20:02\n' >"$WORK/state/deploy-pending"
start_desk >/dev/null
check "a deploy cut off halfway: 'previous deploy was interrupted — redoing'" \
  "$(grep -q 'warn: previous deploy was interrupted (pulling abc1234 -> def5678 at 2026-09-28 11:20:02) — redoing' "$OUT"; echo $?)"
check "and the pending marker is cleared" "$(test ! -f "$WORK/state/deploy-pending"; echo $?)"
start_desk >/dev/null
check "said once, not at every start" "$(! grep -q 'previous deploy' "$OUT"; echo $?)"

echo "An API restart in progress"
eval "$(sed -n '/^# --- an API restart in progress/,/^_api_restarted()/p' scripts/lib/desk-common.sh)"
check "nothing under way" "$(! api_restart_in_progress; echo $?)"
_api_restarting
check "under way while a script restarts it" "$(api_restart_in_progress; echo $?)"
touch -t "$(date -v-10M +%Y%m%d%H%M 2>/dev/null || date -d '10 minutes ago' +%Y%m%d%H%M)" "$API_RESTARTING_FILE"
check "ten minutes old: no longer counts (a script killed mid-restart)" "$(! api_restart_in_progress; echo $?)"
_api_restarted
check "cleared when the restart ends" "$(test ! -f "$API_RESTARTING_FILE"; echo $?)"

echo "api_start and api_restart mark the restart"
(
  eval "$(sed -n '/^# --- an API restart in progress/,/^_api_restarted()/p' scripts/lib/desk-common.sh)"
  eval "$(sed -n '/^api_pids()/,/^# --- the console bundle/p' scripts/lib/desk-common.sh)"
  REPO_ROOT="$WORK/repo"; mkdir -p "$REPO_ROOT/logs"; API=http://127.0.0.1:1 CHAIN_UNDERLYINGS=NIFTY DESK_API_CONFIG=""
  UP=0
  api_healthy() { [ "$UP" = 1 ]; }
  api_pids() { :; }
  nohup() { :; }
  seen=""
  sleep() { [ -f "$API_RESTARTING_FILE" ] && seen=1; UP=1; }
  api_restart >/dev/null
  echo "seen=$seen left=$([ -f "$API_RESTARTING_FILE" ] && echo yes || echo no)"
) >"$WORK/restart.txt" 2>&1
check "the marker is there while the API comes up" "$(grep -q 'seen=1' "$WORK/restart.txt"; echo $?)"
check "and gone once it is up" "$(grep -q 'left=no' "$WORK/restart.txt"; echo $?)"

echo "Switches: the environment, else .env"
eval "$(sed -n '/^desk_setting() {/,/^}/p' scripts/lib/desk-common.sh)"
REPO_ROOT="$WORK/envrepo"; mkdir -p "$REPO_ROOT"
printf 'A_SWITCH=1\nQUOTED="Release"\nSPACED = 2\n# COMMENTED=1\n' >"$REPO_ROOT/.env"
check "read from .env" "$(test "$(desk_setting A_SWITCH)" = 1; echo $?)"
check "quotes are dropped" "$(test "$(desk_setting QUOTED)" = Release; echo $?)"
check "the environment wins" "$(test "$(A_SWITCH=0 desk_setting A_SWITCH)" = 0; echo $?)"
check "a commented line is not a setting" "$(test -z "$(desk_setting COMMENTED)"; echo $?)"
check "absent: nothing" "$(test -z "$(desk_setting NOT_THERE)"; echo $?)"

echo "On Linux, --stop and --headless point at systemd"
printf '#!/bin/sh\necho Linux\n' >"$WORK/bin/uname"; chmod +x "$WORK/bin/uname"
for arg in --stop --headless; do
  out="$(PATH="$WORK/bin:$PATH" HOME="$WORK/home" XDG_STATE_HOME="$WORK/home/state" bash scripts/desk.sh "$arg" 2>&1)"; rc=$?
  check "$arg prints the systemctl command" "$(grep -q "sudo systemctl ${arg#--}" <<<"$out" || grep -q 'sudo systemctl start' <<<"$out"; echo $?)"
  check "$arg acts on nothing (exit 0, no desk started)" "$(test "$rc" = 0 && ! grep -q 'desk started' <<<"$out"; echo $?)"
done
out="$(PATH="$WORK/bin:$PATH" HOME="$WORK/home" XDG_STATE_HOME="$WORK/home/state" bash scripts/desk.sh --stop 2>&1)"
check "--stop says the API keeps running (KillMode=process)" "$(grep -q 'sudo systemctl stop algotrading-desk' <<<"$out" && grep -q 'KillMode=process' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
