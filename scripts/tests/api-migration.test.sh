#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2154,SC2319  # variables are shared with the eval'd code under test; "$(cond; echo $?)" is how check() takes a result
# A database migration under way (scripts/lib/desk-common.sh, 2 Nov review, F4):
#   - an API applying migrations is not killed, however long past the old two
#     minutes, whether it was just started, a desk restarted beside it, or a
#     restart was asked for meanwhile;
#   - past the limit it is restarted, and Telegram is told;
#   - a real failure is still caught: the process gone, or a pid that is no
#     longer the API;
#   - with no migration the rules are unchanged.
# The clock, sleep, /health, the build and the launch are stubs: each sleep moves
# the clock, and what the API does at a given second is what sim() says; what it
# does when launched is on_launch(). The API process is a real sleep, because
# kill -0 is the check.
# Run: bash scripts/tests/api-migration.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
CALLS="$WORK/calls"
T0=1793000000

# scenario NAME: runs the shell function NAME in a subshell with the desk's API
# code and its stubs loaded, after the caller's setup(). Prints what it called and
# said, then rc= and the seconds it took on the stubbed clock.
scenario() {
  : >"$CALLS"
  rm -rf "$WORK/repo" "$WORK/state"
  mkdir -p "$WORK/repo/logs" "$WORK/repo/src/AlgoTrading.Api/bin/Debug/net10.0" "$WORK/state"
  echo built >"$WORK/repo/src/AlgoTrading.Api/bin/Debug/net10.0/AlgoTrading.Api.dll"
  (
    DESK_STATE_DIR="$WORK/state" REPO_ROOT="$WORK/repo" LOG="$WORK/desk.log" API=http://127.0.0.1:1 CHAIN_UNDERLYINGS=NIFTY
    DESK_API_CONFIG="" DESK_BG_OPEN=0
    NOW="$T0"; UP=0; API_PROC=""
    say() { echo "say: $*" >>"$CALLS"; }
    warn() { echo "warn: $*" >>"$CALLS"; }
    notify_at_most_hourly() { echo "notify: $1: $3" >>"$CALLS"; }
    eval "$(sed -n '/^# --- an API restart in progress/,/^_api_restarted()/p' "$REPO/scripts/lib/desk-common.sh")"
    eval "$(sed -n '/^api_pids()/,/^# --- the console bundle/p' "$REPO/scripts/lib/desk-common.sh")"
    eval "$(sed -n '/^# >>> health-step/,/^# <<< health-step/p' "$REPO/scripts/desk.sh")"
    api_healthy() { [ "$UP" = 1 ]; }
    api_keep_last_good() { :; }
    infra_up() { echo "infra_up" >>"$CALLS"; }
    ps() { echo "${PS_ARGS:-dotnet bin/Release/net10.0/AlgoTrading.Api.dll}"; }
    date() { if [ "${1:-}" = "+%s" ]; then echo "$NOW"; else command date "$@"; fi; }
    sleep() { NOW=$((NOW + ${1%.*})); sim $((NOW - T0)); }
    dotnet() { echo "dotnet $1" >>"$CALLS"; [ "$1" != build ] || [ "${BUILD_OK:-1}" = 1 ]; }
    _api_launch() { echo "launch" >>"$CALLS"; on_launch; }
    api_stop() {
      echo "stop" >>"$CALLS"; UP=0
      if [ -n "$API_PROC" ]; then kill "$API_PROC" 2>/dev/null; wait "$API_PROC" 2>/dev/null; API_PROC=""; fi
    }
    # The API process, and the marker it writes (StartupMigrationMarker).
    api_begins_migrating() {
      command sleep 300 & API_PROC=$!
      printf 'pid=%s\nstarted=%s\npending=2\n' "$API_PROC" "$NOW" >"$API_MIGRATING_FILE"
    }
    api_finishes_migrating() { rm -f "$API_MIGRATING_FILE"; }
    # The process ends with the marker left behind: a throw, or a kill.
    api_dies() { kill "$API_PROC" 2>/dev/null; wait "$API_PROC" 2>/dev/null; API_PROC=""; }
    sim() { :; }
    on_launch() { UP=1; }
    setup
    rc=0; "$1" || rc=$?
    [ -z "$API_PROC" ] || { kill "$API_PROC" 2>/dev/null; wait "$API_PROC" 2>/dev/null; }
    echo "rc=$rc seconds=$((NOW - T0))" >>"$CALLS"
  ) 2>/dev/null
  cat "$CALLS"
}
setup() { :; }
says() { grep -q "$2" <<<"$1"; }
stopped_at_all() { grep -qx stop <<<"$1"; }

echo "A long migration after a deploy's restart (the review's scenario): five minutes, then seeding"
setup() {
  on_launch() { api_begins_migrating; }
  sim() { case "$1" in 300) api_finishes_migrating ;; 330) UP=1 ;; esac; }
}
out="$(scenario api_start)"
check "started once and never stopped under the migration" \
  "$(test "$(grep -cx launch <<<"$out")" = 1 && test "$(grep -cx stop <<<"$out")" = 1; echo $?)"   # the one stop precedes the launch
check "the stop came before the launch, not during the wait" "$(test "$(head -3 <<<"$out" | grep -c stop)" = 1; echo $?)"
check "it came up: rc 0, after 330 s, far past the old two minutes" "$(says "$out" 'rc=0 seconds=330'; echo $?)"
check "Telegram is told it started, with the limit" "$(says "$out" 'notify: api-migration-started: .*2 database migration.*up to 45 min'; echo $?)"
check "and that it finished" "$(says "$out" 'notify: api-migration-finished: .*finished'; echo $?)"
check "nothing reads as a failure" "$(! grep -q '^warn:' <<<"$out"; echo $?)"

echo "A desk started during a migration: left alone, not stopped"
setup() { api_begins_migrating; sim() { case "$1" in 600) api_finishes_migrating; UP=1 ;; esac; }; }
out="$(scenario api_start)"
check "no stop, no second launch" "$(! stopped_at_all "$out" && ! grep -qx launch <<<"$out"; echo $?)"
check "rc 0 once it answers" "$(says "$out" 'rc=0 seconds=600'; echo $?)"

echo "A restart asked for during a migration: waits for it first"
setup() { api_begins_migrating; sim() { case "$1" in 400) api_finishes_migrating; UP=1 ;; esac; }; }
out="$(scenario api_restart)"
done_at="$(grep -n 'say:   the database migration finished' <<<"$out" | cut -d: -f1)"
first="$(grep -nE '^(stop|dotnet build)' <<<"$out" | head -1 | cut -d: -f1)"
check "nothing is built or stopped until it finished" "$(test -n "$done_at" && test -n "$first" && test "$done_at" -lt "$first"; echo $?)"
check "then restarts as asked" "$(grep -qx stop <<<"$out" && grep -qx launch <<<"$out" && says "$out" '^rc=0'; echo $?)"

echo "Past the limit, after a start: given up on and said; the desk restarts it"
setup() { on_launch() { api_begins_migrating; }; }
out="$(scenario api_start)"
check "gives up at 45 minutes, not before and not long after" \
  "$(says "$out" 'warn: the database migration has run 45 min, past the 45 min limit' && s="$(sed -n 's/^rc=[0-9]* seconds=//p' <<<"$out")" && test "$s" -ge 2700 && test "$s" -le 2702; echo $?)"
check "Telegram is told, and where to read what to do" "$(says "$out" 'notify: api-migration-limit: .*applied by hand.*scripts/aws/README.md'; echo $?)"
check "the start reports failure: rc 1, which a deploy and the morning job act on" "$(says "$out" '^rc=1'; echo $?)"

echo "Past the limit, for a desk started beside it: stopped and started afresh"
setup() {
  api_begins_migrating
  on_launch() { UP=1; }
}
out="$(scenario api_start)"
check "stopped once the limit passed, then started" "$(test "$(grep -cx stop <<<"$out")" = 1 && test "$(grep -cx launch <<<"$out")" = 1 && says "$out" '^rc=0'; echo $?)"
started="$(grep -n 'api-migration-limit' <<<"$out" | cut -d: -f1)"
check "not before it" "$(test "$(grep -nx stop <<<"$out" | cut -d: -f1)" -gt "$started"; echo $?)"

echo "A migration that fails: the process exits with the marker left behind"
setup() {
  on_launch() { api_begins_migrating; }
  sim() { case "$1" in 60) api_dies ;; esac; }
}
out="$(scenario api_start)"
check "caught when it happens, not after the limit" "$(says "$out" 'warn: the API stopped during a database migration'; echo $?)"
check "Telegram is told" "$(says "$out" 'notify: api-migration-failed:'; echo $?)"
check "the wait ends at once, with no two more minutes: rc 1 at 60 s" "$(says "$out" 'rc=1 seconds=60'; echo $?)"

echo "A marker from a process long gone, or a pid that is no longer the API"
setup() { printf 'pid=999999\nstarted=%s\npending=1\n' "$((NOW - 86400))" >"$API_MIGRATING_FILE"; }
out="$(scenario api_start)"
check "a dead pid: reported and started afresh" "$(says "$out" 'notify: api-migration-failed:' && grep -qx launch <<<"$out"; echo $?)"
setup() { PS_ARGS="/usr/sbin/sshd -D"; api_begins_migrating; }
out="$(scenario api_start)"
check "a reused pid does not count as a migration" "$(says "$out" 'warn: the API stopped during a database migration' && grep -qx launch <<<"$out"; echo $?)"
setup() { printf 'pid=999999\nstarted=1\npending=1\n' >"$API_MIGRATING_FILE"; UP=1; }
out="$(scenario api_start)"
check "a file left beside an API that answers: dropped, no alarm" "$(! grep -q 'api-migration-failed' <<<"$out"; echo $?)"

echo "With no migration, the rules are as they were"
setup() { on_launch() { :; }; }
out="$(scenario api_start)"
check "an API that does not come up is given up on after two minutes" "$(says "$out" 'warn: the API did not come up' && says "$out" 'rc=1 seconds=120'; echo $?)"
check "and no migration note" "$(! grep -q 'migration' <<<"$out"; echo $?)"

echo "The desk's health check (scripts/desk.sh, health_step)"
loop_checks() {  # the loop's checks, 30 s apart, as many as it would make in the given seconds
  local i
  fails=0
  for ((i = 0; i < $1; i += 30)); do health_step; echo "fails=$fails" >>"$CALLS"; sleep 30; done
}
twenty_checks() { loop_checks 600; }
setup() { api_begins_migrating; }
out="$(scenario twenty_checks)"
check "a migration: no failed checks and no restart in ten minutes" \
  "$(! grep -q 'health check failed' <<<"$out" && ! says "$out" 'API is down' && ! stopped_at_all "$out"; echo $?)"
check "the note goes out once, not at every check" "$(test "$(grep -c 'notify: api-migration-started' <<<"$out")" = 1; echo $?)"

over_the_limit() { fails=0; NOW=$((NOW + 2700)); health_step; }
setup() { api_begins_migrating; on_launch() { UP=1; }; }
out="$(scenario over_the_limit)"
check "past the limit: restarted at this check, not three later" "$(says "$out" 'say: API is down' && grep -qx stop <<<"$out"; echo $?)"

setup() {
  api_begins_migrating
  on_launch() { UP=1; }
  sim() { case "$1" in 90) api_dies ;; esac; }
}
out="$(scenario twenty_checks)"
check "a failed migration: told at once, restarted by the usual three checks" \
  "$(says "$out" 'notify: api-migration-failed:' && says "$out" 'health check failed (3/3)' && says "$out" 'say: API is down'; echo $?)"

setup() { on_launch() { UP=1; }; }
out="$(scenario twenty_checks)"
check "no migration, API down: the usual three checks, then a restart" \
  "$(says "$out" 'health check failed (3/3)' && says "$out" 'say: API is down' && grep -qx infra_up <<<"$out" && ! grep -q migration <<<"$out"; echo $?)"

setup() { UP=1; }
out="$(scenario twenty_checks)"
check "healthy: nothing to say" "$(! grep -qE '^(warn|notify|stop)' <<<"$out"; echo $?)"

echo
if [ "$FAILS" -gt 0 ]; then echo "$FAILS check(s) FAILED"; exit 1; fi
echo "all passed"
