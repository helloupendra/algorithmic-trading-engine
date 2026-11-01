#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2319  # variables are read by the functions under test; "$(cond; echo $?)" is how check() takes a result
# How the API is built and started (scripts/lib/desk-common.sh: api_build,
# api_start, api_restart), with and without API_BUILD_CONFIG=Release; what a
# build that fails does (the running API kept, else the last good build
# started); and the host vitals the status screen and the morning tally show
# (scripts/lib/host_vitals.py). dotnet and nohup are stubs that record what
# they were asked to run, and where.
# Run: bash scripts/tests/api-build.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1
REPO="$PWD"

FAILS=0
check() {  # description, condition result (0 = pass)
  if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1"; FAILS=$((FAILS + 1)); fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

start_api() {  # config (Release or empty), build ok (1/0), how (start|restart), [API up before (1/0)] -> the recorded calls, then rc=
  local calls="$WORK/calls"
  : >"$calls"
  rm -rf "$WORK/repo"
  mkdir -p "$WORK/repo/logs" "$WORK/repo/src/AlgoTrading.Api" "$WORK/state"
  printf '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n' \
    >"$WORK/repo/src/AlgoTrading.Api/AlgoTrading.Api.csproj"
  # What a build that passed earlier left in bin/: a failed build does not remove it.
  mkdir -p "$WORK/repo/src/AlgoTrading.Api/bin/Release/net10.0" "$WORK/repo/src/AlgoTrading.Api/bin/Debug/net10.0"
  echo old >"$WORK/repo/src/AlgoTrading.Api/bin/Release/net10.0/AlgoTrading.Api.dll"
  echo old >"$WORK/repo/src/AlgoTrading.Api/bin/Debug/net10.0/AlgoTrading.Api.dll"
  (
    DESK_STATE_DIR="$WORK/state" REPO_ROOT="$WORK/repo" LOG="$WORK/desk.log" API=http://127.0.0.1:1 CHAIN_UNDERLYINGS=NIFTY
    DESK_API_CONFIG="$1" BUILD_OK="$2"
    say() { echo "say: $*" >>"$calls"; }
    warn() { echo "warn: $*" >>"$calls"; }
    notify_at_most_hourly() { echo "notify: $2 — $3" >>"$calls"; }
    eval "$(sed -n '/^# --- an API restart in progress/,/^_api_restarted()/p' "$REPO/scripts/lib/desk-common.sh")"
    eval "$(sed -n '/^api_pids()/,/^# --- the console bundle/p' "$REPO/scripts/lib/desk-common.sh")"
    UP="${4:-0}"
    api_healthy() { [ "$UP" = 1 ]; }
    api_stop() { echo "stop" >>"$calls"; UP=0; }
    sleep() { UP=1; }
    dotnet() {
      echo "dotnet $* (in ${PWD#"$WORK/repo"})" >>"$calls"
      if [ "$1" = build ] && [ "$BUILD_OK" = 1 ]; then
        local cfg=Debug; case " $* " in *" -c Release "*) cfg=Release ;; esac
        echo "new $cfg" >"$REPO_ROOT/src/AlgoTrading.Api/bin/$cfg/net10.0/AlgoTrading.Api.dll"
      fi
      [ "$1" != build ] || [ "$BUILD_OK" = 1 ]
    }
    nohup() { echo "launch: $* (in ${PWD#"$WORK/repo"})" >>"$calls"; }
    rc=0
    if [ "$3" = restart ]; then api_restart || rc=$?; else api_start || rc=$?; fi
    echo "rc=$rc" >>"$calls"
  )
  cat "$calls"
}
GOOD="$WORK/state/api-last-good"
builds() { grep -c '^dotnet build' <<<"$1"; }

echo "Default (API_BUILD_CONFIG unset): a Debug build, then dotnet run"
rm -rf "$WORK/state"
out="$(start_api "" 1 start)"
check "builds Debug first, on its own" "$(grep -qx 'dotnet build src/AlgoTrading.Api -v q --nologo (in )' <<<"$out"; echo $?)"
check "then dotnet run from the repo root, without building again" \
  "$(grep -qx 'launch: dotnet run --no-build --project src/AlgoTrading.Api --no-launch-profile (in )' <<<"$out"; echo $?)"
check "the start line is unchanged" "$(grep -qx 'say: starting the API (Production, http://127.0.0.1:1, chain: NIFTY)' <<<"$out"; echo $?)"
check "the build that passed is kept as the last good one" \
  "$(grep -qx 'new Debug' "$GOOD/AlgoTrading.Api.dll" && grep -qx 'config=Debug' "$GOOD/desk-build.txt"; echo $?)"
check "one copy only: no half-made copy left beside it" "$(test ! -e "$GOOD.new"; echo $?)"
out="$(start_api "" 1 restart 1)"
check "api_restart builds while the old API still serves, then stops it" \
  "$(test "$(grep -E '^(dotnet build|stop)' <<<"$out" | head -2 | tr '\n' '|')" = "dotnet build src/AlgoTrading.Api -v q --nologo (in )|stop|"; echo $?)"
check "and builds once: api_start does not build again" "$(test "$(builds "$out")" = 1 && grep -q 'launch: dotnet run --no-build' <<<"$out" && grep -qx 'rc=0' <<<"$out"; echo $?)"

echo "API_BUILD_CONFIG=Release"
out="$(start_api Release 1 start)"
check "builds Release" "$(grep -qx 'dotnet build src/AlgoTrading.Api -c Release -v q --nologo (in )' <<<"$out"; echo $?)"
check "starts the build output directly, from the project directory (the content root)" \
  "$(grep -qx 'launch: dotnet bin/Release/net10.0/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)' <<<"$out"; echo $?)"
check "no dotnet run" "$(! grep -q 'dotnet run' <<<"$out"; echo $?)"
check "says which build" "$(grep -q 'say: starting the API (.*, Release build)' <<<"$out"; echo $?)"
check "the Release build replaces the last good copy" \
  "$(grep -qx 'new Release' "$GOOD/AlgoTrading.Api.dll" && grep -qx 'config=Release' "$GOOD/desk-build.txt"; echo $?)"
out="$(start_api Release 1 restart 1)"
check "api_restart builds while the old API still serves, then stops it" \
  "$(test "$(grep -E '^(dotnet build|stop)' <<<"$out" | head -2 | tr '\n' '|')" = "dotnet build src/AlgoTrading.Api -c Release -v q --nologo (in )|stop|"; echo $?)"
check "and builds once" "$(test "$(builds "$out")" = 1; echo $?)"

echo "A build that fails (2 Nov review, F1)"
out="$(start_api Release 0 restart 1)"
check "api_restart leaves a healthy API alone: nothing stopped, nothing launched" \
  "$(! grep -qE '^(stop|launch:)' <<<"$out"; echo $?)"
check "returns 2 (kept), not 0 or 1" "$(grep -qx 'rc=2' <<<"$out"; echo $?)"
check "warns in the words Sentinel reads (build FAILED, dotnet build failed)" \
  "$(grep -q '^warn: the API build FAILED — the running API is kept and was not restarted (dotnet build failed' <<<"$out"; echo $?)"
check "and tells the owner" "$(grep -q '^notify: AlgoTrading API — The API build failed, so the running API was kept' <<<"$out"; echo $?)"
check "the failed build leaves the last good copy as it was" "$(grep -qx 'new Release' "$GOOD/AlgoTrading.Api.dll"; echo $?)"
out="$(start_api Release 0 restart 0)"
check "the API down and the build failing: started from the last good build, from the project directory" \
  "$(grep -qx "launch: dotnet $GOOD/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)" <<<"$out"; echo $?)"
check "without compiling the same tree a second time" "$(test "$(builds "$out")" = 1; echo $?)"
check "never dotnet run on the broken tree" "$(! grep -q 'dotnet run' <<<"$out"; echo $?)"
check "says so, naming the build it started" \
  "$(grep -q '^warn: the API build FAILED — starting the last good build instead (commit .*, Release, built ' <<<"$out" && grep -q '^notify: AlgoTrading API — The API build failed, so the API was started from the last good build' <<<"$out"; echo $?)"
check "and comes up (rc 0)" "$(grep -qx 'rc=0' <<<"$out"; echo $?)"
out="$(start_api "" 0 restart 0)"
check "Debug too: the last good build, not dotnet run" \
  "$(grep -qx "launch: dotnet $GOOD/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)" <<<"$out" && ! grep -q 'dotnet run' <<<"$out"; echo $?)"
out="$(start_api Release 0 start)"
check "api_start alone (the desk starting) falls back the same way" \
  "$(grep -qx "launch: dotnet $GOOD/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)" <<<"$out"; echo $?)"
rm -rf "$WORK/state"
out="$(start_api Release 0 start)"
check "no last good copy yet: the output a passing build left in bin/, not a compile" \
  "$(grep -qx 'launch: dotnet bin/Release/net10.0/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)' <<<"$out" && grep -q '^warn: the API build FAILED and there is no last good build yet' <<<"$out" && ! grep -q 'dotnet run' <<<"$out"; echo $?)"

echo "api_build, which the deploy step uses"
build_args() {  # config -> the dotnet build command api_build runs
  ( REPO_ROOT="$WORK/repo" DESK_API_CONFIG="$1"
    eval "$(sed -n '/^api_build() {/,/^}/p' "$REPO/scripts/lib/desk-common.sh")"
    dotnet() { echo "dotnet $*"; }
    api_build )
}
check "default: the Debug build dotnet run uses" "$(test "$(build_args '')" = 'dotnet build src/AlgoTrading.Api -v q --nologo'; echo $?)"
check "Release: -c Release" "$(test "$(build_args Release)" = 'dotnet build src/AlgoTrading.Api -c Release -v q --nologo'; echo $?)"
check "the deploy step builds through api_build" "$(grep -qE 'if (! )?api_build >>"\$LOG" 2>&1; then' scripts/desk.sh; echo $?)"

echo "Host vitals"
P="$WORK/proc"
mkdir -p "$P"
vitals() { HOST_VITALS_PROC="$P" python3 scripts/lib/host_vitals.py "$@"; echo "rc=$?"; }
printf 'processor\t: 0\nprocessor\t: 1\n' >"$P/cpuinfo"
printf 'MemTotal:        7864320 kB\nMemFree:          200000 kB\nMemAvailable:    2097152 kB\nSwapTotal:       2097152 kB\nSwapFree:        1992294 kB\n' >"$P/meminfo"
printf '1.20 0.98 0.75 3/512 4242\n' >"$P/loadavg"
printf 'pgfault 1\noom_kill 0\n' >"$P/vmstat"
printf '277200.5 500000.1\n' >"$P/uptime"
out="$(vitals)"
check "memory, swap and load in one line" \
  "$(grep -qx 'memory 2.0 GB available of 7.5 GB (26%) · swap 0.1 of 2.0 GB used · load 1.20 0.98 0.75 on 2 CPU(s)' <<<"$out"; echo $?)"
check "fine: exit 0" "$(grep -qx 'rc=0' <<<"$out"; echo $?)"
out="$(vitals --oom)"
check "no OOM kills since boot" "$(grep -qx 'none since boot (up 3 d 5 h)' <<<"$out" && grep -qx 'rc=0' <<<"$out"; echo $?)"
printf 'MemTotal:        7864320 kB\nMemAvailable:     300000 kB\nSwapTotal:       2097152 kB\nSwapFree:         500000 kB\n' >"$P/meminfo"
printf '5.10 4.00 3.00 3/512 4242\n' >"$P/loadavg"
printf 'oom_kill 3\n' >"$P/vmstat"
out="$(vitals)"
check "tight memory, swap and load are named, exit 1" \
  "$(grep -q ' — tight: memory, swap, load$' <<<"$out" && grep -qx 'rc=1' <<<"$out"; echo $?)"
out="$(vitals --oom)"
check "OOM kills are counted, exit 1" "$(grep -q '^3 process(es) killed for memory since boot' <<<"$out" && grep -qx 'rc=1' <<<"$out"; echo $?)"
out="$(HOST_VITALS_PROC="$WORK/nowhere" python3 scripts/lib/host_vitals.py; echo "rc=$?")"
check "no /proc (a Mac): load only, exit 2" "$(grep -q '^load .* (no /proc here: memory not shown)$' <<<"$out" && grep -qx 'rc=2' <<<"$out"; echo $?)"

if [ "$FAILS" = 0 ]; then echo "all passed"; else echo "$FAILS failed"; exit 1; fi
