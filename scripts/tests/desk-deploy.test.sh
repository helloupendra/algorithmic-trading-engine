#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2154,SC2319  # variables are shared with the eval'd code under test; "$(cond; echo $?)" is how check() takes a result
# The desk's deploy step (scripts/desk.sh, ">>> deploy-step") against a real
# git checkout with an origin, both in a temp folder, the build and the restart
# stubbed (2 Nov review):
#   - F2: a commit the gate holds is fetched, never pulled: the checkout stays
#     the deployed commit through the session, so a restart builds only that;
#     a docker-compose change is held the same way;
#   - F1: a commit whose API does not build is not recorded as deployed, the
#     checkout goes back, nothing of it goes out, and it is not built again at
#     every check (deploy-failed-sha); the next commit, or deploy-now, is;
#   - the morning job goes on with the running API when its build fails
#     (scripts/market-open.sh, ">>> api-restart").
# Run: bash scripts/tests/desk-deploy.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
OUT="$WORK/out"          # what the desk said, notified and called
RECORDS="$WORK/records"  # what reached the Deployments page
: >"$OUT"; : >"$RECORDS"

# --- a GitHub, a laptop that pushes to it, and the server's checkout --------------
G() { git -c user.name=t -c user.email=t@t -c init.defaultBranch=main -c advice.detachedHead=false "$@"; }
G init -q --bare "$WORK/origin.git"
G clone -q "$WORK/origin.git" "$WORK/laptop" 2>/dev/null
LAPTOP="$WORK/laptop"
commit() {  # message path... — changes each path, commits, pushes; prints the commit
  local msg="$1" p; shift
  for p in "$@"; do mkdir -p "$LAPTOP/$(dirname "$p")"; echo "$msg" >>"$LAPTOP/$p"; done
  G -C "$LAPTOP" add -A && G -C "$LAPTOP" commit -q -m "$msg" && G -C "$LAPTOP" push -q origin HEAD:main 2>/dev/null
  G -C "$LAPTOP" rev-parse HEAD
}
commit first src/AlgoTrading.Api/A.cs web/src/a.ts scripts/x.sh docker-compose.yml src/AlgoTrading.PythonEngine/e.py >/dev/null
G clone -q "$WORK/origin.git" "$WORK/desk" 2>/dev/null
DESK="$WORK/desk"
short() { G -C "$DESK" rev-parse --short "$1"; }
head_sha() { G -C "$DESK" rev-parse HEAD; }

# --- the desk, its clock, its API and its builds --------------------------------
DESK_STATE_DIR="$WORK/state"; mkdir -p "$DESK_STATE_DIR"
LOG="$WORK/desk.log"
say() { echo "say: $*" >>"$OUT"; }
warn() { echo "warn: $*" >>"$OUT"; }
notify() { echo "notify: $1 — $2" >>"$OUT"; }
DOW=1; HHMM=1100; LIVE=3; BUILD_OK=1
date() {
  case "${1:-}" in
    +%u) echo "$DOW" ;;
    +%H%M) echo "$HHMM" ;;
    +%F) echo 2026-11-02 ;;
    *) command date "$@" ;;
  esac
}
live_runs() { echo "$LIVE"; }
api_build() { echo "api_build of $(git rev-parse --short HEAD)" >>"$OUT"; [ "$BUILD_OK" = 1 ]; }
api_restart() { echo "api_restart into $(git rev-parse --short HEAD)" >>"$OUT"; }
web_build() { echo "web_build of $(git rev-parse --short HEAD)" >>"$OUT"; }
npm() { :; }
python3() {
  if [ "${1:-}" = scripts/lib/deploy-record.py ]; then shift; printf '%s\n' "$*" >>"$RECORDS"; return 0; fi
  command python3 "$@"
}
eval "$(sed -n '/^# --- when a deploy may build and restart/,/^live_runs() {/p' scripts/lib/desk-common.sh | sed '$d')"
eval "$(sed -n '/^# >>> deployed-sha/,/^# <<< deployed-sha/p' scripts/desk.sh)"
eval "$(sed -n '/^# >>> deploy-step/,/^# <<< deploy-step/p' scripts/desk.sh)"

cd "$DESK" || exit 1
load_deployed_sha
closed_on=""; deferred_sha=""; last_deploy_note=""; last_commit=""
check_now() { : >"$OUT"; : >"$RECORDS"; deploy_if_behind; }
said() { grep -q -- "$1" "$OUT"; }

echo "Scripts and the Python engine alone go out at once, even in the session"
S="$(commit "runner fix" scripts/x.sh src/AlgoTrading.PythonEngine/e.py)"
check_now
check "pulled at 11:00 with runs live" "$(test "$(head_sha)" = "$S"; echo $?)"
check "recorded as deployed, nothing built" "$(test "$(cat "$DESK_STATE_DIR/deployed-sha")" = "$S" && ! said api_build && ! said web_build; echo $?)"

echo "An API change in the session is fetched, not pulled (F2)"
B="$(commit "api change" src/AlgoTrading.Api/A.cs)"
check_now
check "deferred, and said why" "$(said "say: deploy of $(short "$B") deferred — market day, 11:00 — waits for the evening close job at 23:58.*; nothing is pulled until"; echo $?)"
check "the checkout stays the deployed commit" "$(test "$(head_sha)" = "$S"; echo $?)"
check "so a restart now builds what the API runs" "$(test "$(head_sha)" = "$(cat "$DESK_STATE_DIR/deployed-sha")"; echo $?)"
check "nothing built, nothing restarted" "$(! said api_build && ! said api_restart; echo $?)"
check "the Deployments page says fetched, not pulled" \
  "$(grep -q 'Fetched from GitHub|ok|1 commit(s), 1 file(s) - not pulled' "$RECORDS" && grep -q -- '--outcome skipped' "$RECORDS"; echo $?)"
check "no deploy left pending" "$(test ! -f "$DESK_STATE_DIR/deploy-pending"; echo $?)"
check_now
check "the next check is quiet: the deferral is said once" "$(! said deferred && test "$(head_sha)" = "$S"; echo $?)"
C="$(commit "script after it" scripts/x.sh)"
check_now
check "a script commit on top of a held one waits with it (no fast-forward past it)" \
  "$(test "$(head_sha)" = "$S" && said "deploy of $(short "$C") deferred"; echo $?)"

echo "After the evening close the held commits go out"
DOW=1; HHMM=2359; LIVE=0; closed_on=2026-11-02
check_now
check "pulled to origin/main" "$(test "$(head_sha)" = "$C"; echo $?)"
check "the API built from the pulled tree, then restarted" \
  "$(said "api_build of $(short "$C")" && said "api_restart into $(short "$C")" && test "$(grep -n api_build "$OUT" | cut -d: -f1)" -lt "$(grep -n api_restart "$OUT" | cut -d: -f1)"; echo $?)"
check "recorded as deployed" "$(test "$(cat "$DESK_STATE_DIR/deployed-sha")" = "$C" && grep -q -- '--outcome ok' "$RECORDS"; echo $?)"

echo "A docker-compose change is held the same way"
DOW=2; HHMM=1100; LIVE=2; closed_on=""
D="$(commit "redis setting" docker-compose.yml)"
check_now
check "in the session: fetched, not pulled" "$(test "$(head_sha)" = "$C" && said "deploy of $(short "$D") deferred"; echo $?)"
HHMM=0805; LIVE=0
check_now
check "before 08:40 with nothing live: pulled" "$(test "$(head_sha)" = "$D"; echo $?)"
check "nothing rebuilt; the note says when compose applies it" \
  "$(! said api_build && said 'docker-compose changed: applied at the next infra start'; echo $?)"

echo "A commit whose API does not build (F1)"
DOW=3; HHMM=0010; LIVE=0; closed_on=""; BUILD_OK=0
E="$(commit "broken api" src/AlgoTrading.Api/A.cs web/src/a.ts)"
check_now
check "the build was tried on the pulled tree" "$(said "api_build of $(short "$E")"; echo $?)"
check "warned in the words Sentinel reads" \
  "$(said "warn: dotnet build failed for $(short "$E") — NOT deployed; the API keeps running $(short "$D")"; echo $?)"
check "the checkout went back to the deployed commit" "$(test "$(head_sha)" = "$D"; echo $?)"
check "NOT recorded as deployed" "$(test "$(cat "$DESK_STATE_DIR/deployed-sha")" = "$D"; echo $?)"
check "recorded as the failed one" "$(test "$(cat "$DESK_STATE_DIR/deploy-failed-sha")" = "$E"; echo $?)"
check "nothing of it went out: no console build, no restart" "$(! said web_build && ! said api_restart; echo $?)"
check "the owner is told on Telegram" "$(said "notify: AlgoTrading deploy FAILED — $(short "$E") did not build"; echo $?)"
check "a 'deploy: … FAILED' line, which does not clear Sentinel's finding" \
  "$(said "say: deploy: $(short "$E") at [0-9:]* — API build FAILED, not deployed"; echo $?)"
check "the Deployments page says failed" "$(grep -q -- '--outcome failed' "$RECORDS" && grep -q 'API build|failed|' "$RECORDS"; echo $?)"
check "no deploy left pending" "$(test ! -f "$DESK_STATE_DIR/deploy-pending"; echo $?)"
check_now
check "the next check does not build it again" "$(! said api_build && test "$(head_sha)" = "$D"; echo $?)"
( load_deployed_sha; echo "$failed_sha" ) >"$WORK/after-restart"
check "nor does a restarted desk (deploy-failed-sha is read at startup)" "$(test "$(cat "$WORK/after-restart")" = "$E"; echo $?)"
BUILD_OK=1
touch "$DEPLOY_NOW_FILE"
check_now
check "deploy-now tries it again" \
  "$(said "api_build of $(short "$E")" && test "$(head_sha)" = "$E" && test "$(cat "$DESK_STATE_DIR/deployed-sha")" = "$E"; echo $?)"
check "and is used up" "$(test ! -f "$DEPLOY_NOW_FILE"; echo $?)"
BUILD_OK=0
commit "broken again" src/AlgoTrading.Api/A.cs >/dev/null
check_now
BUILD_OK=1
F="$(commit "the fix" src/AlgoTrading.Api/A.cs)"
check_now
check "the next commit after a failed one gets its own attempt" \
  "$(said "api_build of $(short "$F")" && test "$(cat "$DESK_STATE_DIR/deployed-sha")" = "$F" && test "$(head_sha)" = "$F"; echo $?)"

echo "Uncommitted work on the server is never pulled over"
commit "another" src/AlgoTrading.Api/A.cs >/dev/null
echo local >"$DESK/scratch.txt"
check_now
check "not pulled, said" "$(test "$(head_sha)" = "$F" && said 'working tree has uncommitted changes — NOT pulling'; echo $?)"
rm -f "$DESK/scratch.txt"

echo "The morning job and an API build that fails (scripts/market-open.sh)"
cd "$REPO" || exit 1
morning() {  # api_restart's exit status -> what the morning did
  ( rc_wanted="$1"
    web_build() { :; }
    api_restart() { return "$rc_wanted"; }
    say() { echo "say: $*"; }
    fail() { echo "fail: $*"; exit 1; }
    eval "$(sed -n '/^# >>> api-restart/,/^# <<< api-restart/p' scripts/market-open.sh)"
    echo "went on" )
}
check "restarted: goes on" "$(test "$(morning 0)" = "went on"; echo $?)"
out="$(morning 2)"
check "build failed, the running API kept: goes on with it, and says so" \
  "$(grep -q '^say:   the API build failed, so the API already running was kept' <<<"$out" && grep -qx 'went on' <<<"$out"; echo $?)"
out="$(morning 1)"
check "the API did not come up: the morning fails, as before" "$(grep -qx 'fail: the API did not come up.' <<<"$out" && ! grep -q 'went on' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
