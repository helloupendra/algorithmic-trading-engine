#!/usr/bin/env bash
# The Dhan feed read-path load test: baseline (the production tree) against
# the fix (this tree), on two pinned vCPUs of Docker Desktop's Linux VM.
#
#   tools/feed_loadtest/matrix.sh [CASE ...]        # all cases when none named
#
#   CASES     L2 L18 FISO G R C   (see below)
#   OUT       where results go (default: a new folder under $TMPDIR)
#   BASE_REF  the baseline commit (default ee27219, what production runs)
#   TRIALS    trials per case (default 3)
#   QUIET_LOAD, QUIET_PROBE_MS, QUIET_WAIT   the quiet-window gate (below)
#
# The quiet-window gate. Docker Desktop's CPUs are the host's: on a Mac busy
# with other work a trial measures the Mac, not the feed. Before each trial
# the script waits until the host's 1-minute load average is at most
# QUIET_LOAD (default 10) and a 2M-iteration Python loop pinned to CPU 6 takes
# at most QUIET_PROBE_MS (default 400), checking every 20 s. After QUIET_WAIT
# seconds (default 1200) it runs anyway. Either way matrix.log says which.
#
# Run it on a development Mac with Docker Desktop (8 CPUs in its VM), never on
# the live server. LOCAL ONLY: the "Dhan" is a fake on 127.0.0.1 inside the
# container; nothing here connects to Dhan, FYERS, Angel or TrueData.
#
# CPUs: the container gets 5,6,7. The feed, Redis and its proxy, the burners
# and the runner emulators share 6 and 7 (LOAD_CLIENT_CPUS) — the 2-vCPU
# server; the fake Dhan and the sampler get 5 (LOAD_SERVER_CPUS).
#
#   L2    2 burners + 23 runner emulators, 1000/2000/3000 frames/s, both trees
#   L18   18 burners + 23 emulators, 2000/s, both trees
#   FISO  the feed fix alone with today's runners (unfiltered), 3000/s
#   G     L2 + a thread in the feed busy 100% of the time, 3000/s; must pass
#         at FEED_GIL_SWITCH_MS=1, recorded at 0 (Python's 5 ms)
#   R     L2 with redis-server stopped (SIGSTOP) for 10 s at t=20 s, 2000/s
#   C     L2 with the inferred Dhan cutoff (no pong for 40 s), 180 s, both trees
#   GC    the fix at L2 3000/s with FEED_GC_FREEZE=0, to set against L2-3000-fix
#
# The baseline's runners read every entry (today's runner); the fix's read only
# their spot, in batches, at nice 10 (the fix's runner).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(git -C "$HERE" rev-parse --show-toplevel)"
BASE_REF="${BASE_REF:-ee27219}"
TRIALS="${TRIALS:-3}"
OUT="${OUT:-${TMPDIR:-/tmp}/feed-loadtest-$(date +%Y%m%d-%H%M%S)}"
IMAGE="feedload:py312"
CASES=("$@")
if [ "${#CASES[@]}" -eq 0 ]; then CASES=(L2 L18 FISO G R C GC); fi

mkdir -p "$OUT"
BASE_TREE="$OUT/tree-$BASE_REF"
if [ ! -d "$BASE_TREE/src" ]; then
  mkdir -p "$BASE_TREE"
  # The baseline's files only, straight from the object store: no worktree.
  git -C "$REPO" archive "$BASE_REF" src/AlgoTrading.PythonEngine src/AlgoTrading.Api/SeedData | tar -x -C "$BASE_TREE"
fi

docker build -q -t "$IMAGE" "$HERE" >/dev/null

QUIET_LOAD="${QUIET_LOAD:-10}"
QUIET_PROBE_MS="${QUIET_PROBE_MS:-400}"
QUIET_WAIT="${QUIET_WAIT:-1200}"

host_load() {
  # The 1-minute load average: macOS first, then Linux.
  if sysctl -n vm.loadavg >/dev/null 2>&1; then
    sysctl -n vm.loadavg | tr -d '{}' | awk '{print $1}'
  else
    cut -d' ' -f1 /proc/loadavg
  fi
}

probe_ms() {
  docker run --rm --cpuset-cpus 6 "$IMAGE" python -c \
    'import time
t = time.perf_counter(); x = 0
for i in range(2_000_000): x += i * i
print(round((time.perf_counter() - t) * 1000))'
}

wait_for_quiet() {
  local waited=0 load probe
  while true; do
    load="$(host_load)"
    probe="$(probe_ms)"
    if awk -v l="$load" -v p="$probe" -v L="$QUIET_LOAD" -v P="$QUIET_PROBE_MS" 'BEGIN { exit !(l <= L && p <= P) }'; then
      QUIET_NOTE="quiet: host load $load, CPU probe ${probe} ms, waited ${waited}s"
      return
    fi
    if [ "$waited" -ge "$QUIET_WAIT" ]; then
      QUIET_NOTE="NOT QUIET after ${waited}s: host load $load, CPU probe ${probe} ms"
      return
    fi
    sleep 20
    waited=$((waited + 20))
  done
}

run() {
  local label="$1" tree="$2"
  shift 2
  wait_for_quiet
  echo "=== $label ($QUIET_NOTE)" | tee -a "$OUT/matrix.log"
  docker run --rm --cpuset-cpus 5,6,7 -e LOAD_CLIENT_CPUS=6,7 -e LOAD_SERVER_CPUS=5 \
    -v "$REPO":/trees/fix:ro -v "$BASE_TREE":/trees/base:ro -v "$OUT":/out "$IMAGE" \
    python /trees/fix/src/AlgoTrading.PythonEngine/tools/feed_loadtest/run_load.py \
    --tree "/trees/$tree" --label "$label" --out /out "$@" 2>&1 | tee -a "$OUT/matrix.log" || true
}

for case in "${CASES[@]}"; do
  for trial in $(seq 1 "$TRIALS"); do
    case "$case" in
      L2)
        for rate in 1000 2000 3000; do
          run "L2-${rate}-base-$trial" base --rate "$rate" --burners 2 --runners 23 --emulators unfiltered
          run "L2-${rate}-fix-$trial" fix --rate "$rate" --burners 2 --runners 23 --emulators filtered
        done ;;
      L18)
        run "L18-2000-base-$trial" base --rate 2000 --burners 18 --runners 23 --emulators unfiltered
        run "L18-2000-fix-$trial" fix --rate 2000 --burners 18 --runners 23 --emulators filtered ;;
      FISO)
        run "FISO-3000-fix-$trial" fix --rate 3000 --burners 2 --runners 23 --emulators unfiltered ;;
      G)
        run "G-3000-fix-gil1-$trial" fix --rate 3000 --burners 2 --runners 23 --emulators filtered --spinner --gil-ms 1
        run "G-3000-fix-gil0-$trial" fix --rate 3000 --burners 2 --runners 23 --emulators filtered --spinner --gil-ms 0 ;;
      R)
        run "R-2000-fix-$trial" fix --rate 2000 --burners 2 --runners 23 --emulators filtered \
          --redis-stop-at 20 --redis-stop-for 10 ;;
      C)
        run "C-2000-base-$trial" base --rate 2000 --burners 2 --runners 23 --emulators unfiltered \
          --cut-on-pong-late 40 --seconds 180
        run "C-2000-fix-$trial" fix --rate 2000 --burners 2 --runners 23 --emulators filtered \
          --cut-on-pong-late 40 --seconds 180 ;;
      GC)
        run "GC-3000-fix-nofreeze-$trial" fix --rate 3000 --burners 2 --runners 23 --emulators filtered \
          --env FEED_GC_FREEZE=0 ;;
      *) echo "unknown case $case" >&2; exit 2 ;;
    esac
  done
done

python3 "$HERE/summarize.py" "$OUT" | tee "$OUT/summary.txt"
echo "results: $OUT"
