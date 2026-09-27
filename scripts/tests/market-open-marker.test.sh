#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2319  # variables are read by the functions under test; "$(cond; echo $?)" is how check() takes a result
# The morning job runs once a day, even across desk restarts: the day's marker
# file decides, not the desk's memory (scripts/lib/desk-common.sh, daily_job on
# the desk's side, job_marker_attach on the job's). The desk's functions are
# loaded from the library between ">>> daily-job" and "<<< daily-job", with
# say/warn/notify replaced by stubs; each desk_call is a freshly started desk.
# The job is a stand-in that speaks the job's side of the protocol, plus one
# real run of scripts/market-open.sh.
# Run: bash scripts/tests/market-open-marker.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'touch "$WORK/release" 2>/dev/null; rm -rf "$WORK"' EXIT
mkdir -p "$WORK/state" "$WORK/repo/logs" "$WORK/bin"

# flock is util-linux: on a Mac without it, a stand-in with the same -n -o -E
# behaviour (a real fcntl lock, not held by the command it runs).
if ! command -v flock >/dev/null 2>&1; then
  cat >"$WORK/bin/flock" <<'PY'
#!/usr/bin/env python3
import fcntl, os, subprocess, sys
args, conflict, flags = sys.argv[1:], 1, fcntl.LOCK_EX
while args and args[0].startswith("-"):
    opt = args.pop(0)
    if opt == "-n":
        flags |= fcntl.LOCK_NB
    elif opt == "-E":
        conflict = int(args.pop(0))
fd = os.open(args[0], os.O_RDWR | os.O_CREAT, 0o644)
try:
    fcntl.flock(fd, flags)
except BlockingIOError:
    sys.exit(conflict)
sys.exit(subprocess.call(args[1:]))   # close_fds: the command does not hold the lock (-o)
PY
  chmod +x "$WORK/bin/flock"
fi
# Notifications on a Mac go through osascript: never on the screen from a test.
printf '#!/bin/sh\nexit 0\n' >"$WORK/bin/osascript"; chmod +x "$WORK/bin/osascript"
export PATH="$WORK/bin:$PATH"

# Stands in for scripts/market-open.sh: the job's side of the marker, then
# whatever the case asks for — hold until released, die to a signal, or exit.
JOB="$WORK/fake-market-open.sh"
cat >"$JOB" <<EOF
#!/usr/bin/env bash
eval "\$(sed -n '/^# >>> job-marker/,/^# <<< job-marker/p' "$REPO/scripts/lib/desk-common.sh")"
job_marker_attach
echo ran >>"$WORK/runs"
if [ -n "\${FAKE_HOLD:-}" ]; then while [ ! -f "$WORK/release" ]; do sleep 0.1; done; fi
if [ -n "\${FAKE_DIE:-}" ]; then kill -TERM \$\$; sleep 5; fi
exit "\${FAKE_RC:-0}"
EOF
chmod +x "$JOB"

TODAY="$(date +%F)"
YESTERDAY="$(date -v-1d +%F 2>/dev/null || date -d yesterday +%F)"
MARKER="$WORK/state/market-open-$TODAY"

reset() { rm -f "$WORK"/state/* "$WORK/runs" "$WORK/out" "$WORK/release"; : >"$WORK/out"; }
runs() { if [ -f "$WORK/runs" ]; then grep -c . "$WORK/runs"; else echo 0; fi; }

desk_call() {  # [day] [fg|bg] [command] -> one look by a freshly started desk, as desk.sh's loop does it
  (
    DESK_STATE_DIR="$WORK/state" REPO_ROOT="$WORK/repo" LOG="$WORK/desk.log"
    say() { echo "say: $*" >>"$WORK/out"; }
    warn() { echo "warn: $*" >>"$WORK/out"; }
    notify() { echo "notify: $2" >>"$WORK/out"; }
    eval "$(sed -n '/^# >>> daily-job/,/^# <<< daily-job/p' "$REPO/scripts/lib/desk-common.sh")"
    daily_job market-open "${1:-$TODAY}" "${2:-fg}" "=== 0845 — running market-open.sh ===" "${3:-$JOB}"
    echo "result: $?" >>"$WORK/out"
  )
}

wait_for() {  # file pattern -> waits up to 10 s for the pattern to appear in the file
  local i
  for i in $(seq 1 100); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.1; done
  return 1
}

echo "No marker yet: the job runs, once, and records itself"
reset
desk_call
check "the job ran" "$(test "$(runs)" = 1; echo $?)"
check "the desk wrote started=" "$(grep -q '^started=' "$MARKER"; echo $?)"
check "the job wrote its pid" "$(grep -q '^pid=[0-9][0-9]*$' "$MARKER"; echo $?)"
check "the job wrote done= with its exit status" "$(grep -q '^done=.* exit=0$' "$MARKER"; echo $?)"
check "the desk says it finished" "$(grep -q 'say: market-open.sh finished' "$WORK/out"; echo $?)"
check "the banner was said" "$(grep -q 'say: === 0845 — running market-open.sh ===' "$WORK/out"; echo $?)"
check "settled for the day (0)" "$(grep -q 'result: 0' "$WORK/out"; echo $?)"

echo "A job that exits non-zero is reported as before"
reset
FAKE_RC=2 desk_call
check "done= carries exit=2" "$(grep -q '^done=.* exit=2$' "$MARKER"; echo $?)"
check "warns 'market-open.sh exited non-zero' (Sentinel's rule)" "$(grep -q 'warn: market-open.sh exited non-zero (see logs/market-open-' "$WORK/out"; echo $?)"

echo "Today's marker present (the desk restarted after the job): skipped"
reset
printf 'started=%s 08:45:03\npid=1\ndone=%s 09:21:07 exit=0\n' "$TODAY" "$TODAY" >"$MARKER"
desk_call
check "the job did not run again" "$(test "$(runs)" = 0; echo $?)"
check "says 'already ran today'" "$(grep -q 'say: market-open already ran today' "$WORK/out"; echo $?)"
check "no notification" "$(! grep -q '^notify' "$WORK/out"; echo $?)"

echo "Marker with a dead pid and no done line (killed mid-morning): one notice, no rerun"
reset
true & dead=$!; wait "$dead"
printf 'started=%s 08:45:03\npid=%s\n' "$TODAY" "$dead" >"$MARKER"
desk_call
desk_call   # and the desk restarts again
check "the job was not rerun" "$(test "$(runs)" = 0; echo $?)"
check "exactly one notification" "$(test "$(grep -c '^notify' "$WORK/out")" = 1; echo $?)"
check "it says how to rerun by hand" "$(grep -q 'notify: Today.s morning job was interrupted at [0-9][0-9]:[0-9][0-9] — rerun by hand with scripts/market-open.sh --redeploy-only' "$WORK/out"; echo $?)"
check "the notice is recorded in the marker" "$(grep -q '^notified=' "$MARKER"; echo $?)"
check "the second look says it was already reported" "$(grep -q 'was interrupted and has been reported' "$WORK/out"; echo $?)"

echo "A job killed by a signal while the desk waits for it"
reset
FAKE_DIE=1 desk_call
check "no done line" "$(! grep -q '^done=' "$MARKER"; echo $?)"
check "says it ended without finishing" "$(grep -q '^ended=' "$MARKER"; echo $?)"
check "reported as interrupted, once" "$(test "$(grep -c '^notify: Today.s morning job was interrupted' "$WORK/out")" = 1; echo $?)"

echo "A job that cannot even start is a failure at once, not an interruption later"
reset
desk_call "$TODAY" fg "$WORK/no-such-market-open.sh"
check "done= records its exit status" "$(grep -q '^done=.* exit=[1-9][0-9]* it ended before it recorded itself' "$MARKER"; echo $?)"
check "warns 'exited non-zero' now" "$(grep -q 'warn: market-open.sh exited non-zero' "$WORK/out"; echo $?)"
check "no 'interrupted' notice" "$(! grep -q '^notify' "$WORK/out"; echo $?)"

echo "A marker dated yesterday is ignored"
reset
printf 'started=%s 08:45:03\npid=1\ndone=%s 09:21:07 exit=0\n' "$YESTERDAY" "$YESTERDAY" >"$WORK/state/market-open-$YESTERDAY"
desk_call
check "today's job ran" "$(test "$(runs)" = 1; echo $?)"
check "today's marker was written" "$(grep -q '^done=' "$MARKER"; echo $?)"

echo "No pid yet, a moment after the desk wrote the marker: looked at again, not reported"
reset
printf 'started=%s 08:45:03\n' "$TODAY" >"$MARKER"
desk_call
check "not settled (1): the next loop looks again" "$(grep -q 'result: 1' "$WORK/out"; echo $?)"
check "no notification, no run" "$(! grep -q '^notify' "$WORK/out" && test "$(runs)" = 0; echo $?)"

echo "The desk restarts while the job is still running"
reset
FAKE_HOLD=1 desk_call "$TODAY" bg
wait_for "$MARKER" '^pid=' || echo "  (the background job never wrote its pid)"
desk_call
check "only one copy ran" "$(test "$(runs)" = 1; echo $?)"
check "says it is still running" "$(grep -q 'say: market-open for .* is still running (pid [0-9]*' "$WORK/out"; echo $?)"
check "not settled while it runs (1)" "$(test "$(grep -c 'result: 1' "$WORK/out")" = 2; echo $?)"
touch "$WORK/release"
wait_for "$MARKER" '^done=' || echo "  (the background job never finished)"
desk_call
check "once done, the next look settles it" "$(tail -1 "$WORK/out" | grep -q 'result: 0'; echo $?)"

echo "Two flock invocations at once (a marker removed by hand): the second is refused by the lock"
reset
FAKE_HOLD=1 desk_call "$TODAY" bg
wait_for "$MARKER" '^pid=' || echo "  (the background job never wrote its pid)"
desk_call "$YESTERDAY"   # a different day's marker, so only the lock stands in the way
check "the second copy did not run" "$(test "$(runs)" = 1; echo $?)"
check "logs 'another market-open is running'" "$(grep -q 'say: another market-open is running' "$WORK/out"; echo $?)"
check "and records why in its marker" "$(grep -q '^done=.* exit=75 not started' "$WORK/state/market-open-$YESTERDAY"; echo $?)"
check "no 'exited non-zero' for it" "$(! grep -q 'exited non-zero' "$WORK/out"; echo $?)"
touch "$WORK/release"
wait_for "$MARKER" '^done=' || echo "  (the background job never finished)"

echo "The real scripts/market-open.sh"
reset
printf 'started=%s 08:45:03\n' "$TODAY" >"$MARKER"
DESK_JOB_MARKER="$MARKER" HOME="$WORK/home" XDG_STATE_HOME="$WORK/home/state" MARKET_OPEN_PLAN='' \
  MARKET_OPEN_PLAN_FILE="$WORK/no-plan.txt" ./scripts/market-open.sh >/dev/null 2>&1
check "records its pid and exit status (no plan: exit 1)" "$(grep -q '^pid=' "$MARKER" && grep -q '^done=.* exit=1$' "$MARKER"; echo $?)"
HOME="$WORK/home" ./scripts/market-open.sh --dry-rn >/dev/null 2>&1; rc=$?
check "refuses an unknown argument (exit 2) instead of running the live morning" "$(test "$rc" = 2; echo $?)"
check "market-close.sh records itself too" "$(grep -q '^\[ "\$DRY_RUN" = 1 \] || job_marker_attach' scripts/market-close.sh; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
