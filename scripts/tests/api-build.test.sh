#!/usr/bin/env bash
# shellcheck disable=SC2034,SC2319  # variables are read by the functions under test; "$(cond; echo $?)" is how check() takes a result
# How the API is built and started (scripts/lib/desk-common.sh: api_build,
# api_start, api_restart), with and without API_BUILD_CONFIG=Release, and the
# host vitals the status screen and the morning tally show
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

start_api() {  # config (Release or empty), build ok (1/0), how (start|restart) -> the recorded calls
  local calls="$WORK/calls"
  : >"$calls"
  rm -rf "$WORK/repo"
  mkdir -p "$WORK/repo/logs" "$WORK/repo/src/AlgoTrading.Api" "$WORK/state"
  printf '<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n' \
    >"$WORK/repo/src/AlgoTrading.Api/AlgoTrading.Api.csproj"
  (
    DESK_STATE_DIR="$WORK/state" REPO_ROOT="$WORK/repo" LOG="$WORK/desk.log" API=http://127.0.0.1:1 CHAIN_UNDERLYINGS=NIFTY
    DESK_API_CONFIG="$1" BUILD_OK="$2"
    say() { echo "say: $*" >>"$calls"; }
    warn() { echo "warn: $*" >>"$calls"; }
    eval "$(sed -n '/^# --- an API restart in progress/,/^_api_restarted()/p' "$REPO/scripts/lib/desk-common.sh")"
    eval "$(sed -n '/^api_pids()/,/^# --- the console bundle/p' "$REPO/scripts/lib/desk-common.sh")"
    UP=0
    api_healthy() { [ "$UP" = 1 ]; }
    api_stop() { echo "stop" >>"$calls"; }
    sleep() { UP=1; }
    dotnet() {
      echo "dotnet $* (in ${PWD#"$WORK/repo"})" >>"$calls"
      if [ "$1" = build ] && [ "$BUILD_OK" = 1 ]; then
        mkdir -p "$REPO_ROOT/src/AlgoTrading.Api/bin/Release/net10.0"
        touch "$REPO_ROOT/src/AlgoTrading.Api/bin/Release/net10.0/AlgoTrading.Api.dll"
      fi
      [ "$1" != build ] || [ "$BUILD_OK" = 1 ]
    }
    nohup() { echo "launch: $* (in ${PWD#"$WORK/repo"})" >>"$calls"; }
    if [ "$3" = restart ]; then api_restart; else api_start; fi
  )
  cat "$calls"
}

echo "Default (API_BUILD_CONFIG unset): dotnet run, exactly as before"
out="$(start_api "" 1 start)"
check "started with dotnet run from the repo root" \
  "$(grep -qx 'launch: dotnet run --project src/AlgoTrading.Api --no-launch-profile (in )' <<<"$out"; echo $?)"
check "no separate build (dotnet run builds)" "$(! grep -q '^dotnet build' <<<"$out"; echo $?)"
check "the start line is unchanged" "$(grep -qx 'say: starting the API (Production, http://127.0.0.1:1, chain: NIFTY)' <<<"$out"; echo $?)"
out="$(start_api "" 1 restart)"
check "api_restart does not build either" "$(! grep -q '^dotnet build' <<<"$out"; echo $?)"

echo "API_BUILD_CONFIG=Release"
out="$(start_api Release 1 start)"
check "builds Release" "$(grep -qx 'dotnet build src/AlgoTrading.Api -c Release -v q --nologo (in )' <<<"$out"; echo $?)"
check "starts the build output directly, from the project directory (the content root)" \
  "$(grep -qx 'launch: dotnet bin/Release/net10.0/AlgoTrading.Api.dll (in /src/AlgoTrading.Api)' <<<"$out"; echo $?)"
check "no dotnet run" "$(! grep -q 'dotnet run' <<<"$out"; echo $?)"
check "says which build" "$(grep -q 'say: starting the API (.*, Release build)' <<<"$out"; echo $?)"
out="$(start_api Release 1 restart)"
check "api_restart builds while the old API still serves, then stops it" \
  "$(test "$(grep -E '^(dotnet build|stop)' <<<"$out" | head -2 | tr '\n' '|')" = "dotnet build src/AlgoTrading.Api -c Release -v q --nologo (in )|stop|"; echo $?)"
out="$(start_api Release 0 start)"
check "a failed Release build falls back to dotnet run" \
  "$(grep -q 'warn: the Release build failed' <<<"$out" && grep -qx 'launch: dotnet run --project src/AlgoTrading.Api --no-launch-profile (in )' <<<"$out"; echo $?)"

echo "api_build, which the deploy step uses"
build_args() {  # config -> the dotnet build command api_build runs
  ( REPO_ROOT="$WORK/repo" DESK_API_CONFIG="$1"
    eval "$(sed -n '/^api_build() {/,/^}/p' "$REPO/scripts/lib/desk-common.sh")"
    dotnet() { echo "dotnet $*"; }
    api_build )
}
check "default: the Debug build dotnet run uses" "$(test "$(build_args '')" = 'dotnet build src/AlgoTrading.Api -v q --nologo'; echo $?)"
check "Release: -c Release" "$(test "$(build_args Release)" = 'dotnet build src/AlgoTrading.Api -c Release -v q --nologo'; echo $?)"
check "the deploy step builds through api_build" "$(grep -q 'if api_build >>"\$LOG" 2>&1; then' scripts/desk.sh; echo $?)"

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
