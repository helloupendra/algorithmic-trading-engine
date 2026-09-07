#!/usr/bin/env bash
# The trading desk: one long-running loop that keeps the platform up.
#
#   - keeps the API healthy (restarts it after three failed health checks);
#   - every two minutes checks GitHub, and when origin/main has moved, pulls
#     and deploys — console rebuilt in place, API rebuilt and restarted when
#     its code changed;
#   - at 08:45 on weekdays runs scripts/market-open.sh once;
#   - writes logs/desk.status every loop so scripts/status.sh can answer
#     "is it running?" without guessing.
#
# Why one loop in a Terminal window rather than a set of launchd services:
# macOS privacy protection refuses a launchd-spawned bash, git or dotnet any
# file under ~/Documents, where this repo lives — the first scheduled morning
# failed exactly that way. Terminal holds the permission, so everything that
# touches the repo runs under it. launchd's only job (install-desk.sh) is to
# open this script in Terminal at login and reopen it if it is gone.
#
# Deploy policy (mirrors scripts/auto-deploy.ps1 on the Windows box, with one
# difference the owner asked for): the API IS restarted while runs are live.
# Runners are separate processes that survive the ~15 s restart and re-register
# with the new API; the Python engine, however, keeps the code it started with
# until a run is restarted by hand — a live run is never killed by a deploy.
#
# Usage: ./scripts/desk.sh      (Ctrl+C stops the loop; the API keeps running)

set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/desk.log"
. scripts/lib/desk-common.sh

STATUS="$REPO_ROOT/logs/desk.status"
PIDFILE="$HOME/Library/Application Support/algotrading/desk.pid"
mkdir -p "$(dirname "$PIDFILE")"

HEALTH_EVERY=30          # seconds between health checks
DEPLOY_EVERY=120         # seconds between git checks
OPEN_AT="${MARKET_OPEN_AT:-0845}"   # HHMM, weekdays

# One desk at a time. A second copy would double every restart and deploy.
if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
  echo "desk is already running (pid $(cat "$PIDFILE")). Use scripts/status.sh to look at it."
  exit 0
fi
echo $$ > "$PIDFILE"
trap 'rm -f "$PIDFILE"; say "desk stopped (the API is left running)"; exit 0' INT TERM

say "=== desk started (pid $$) — API $API, chain $CHAIN_UNDERLYINGS, open at $OPEN_AT ==="

# --- infra once ---------------------------------------------------------------
say "infra ..."
docker compose up -d --wait timescaledb redis >>"$LOG" 2>&1 && say "  infra up" || warn "docker compose failed — is Docker Desktop running?"

api_start || true

fails=0
last_deploy_check=0
opened_on=""
last_commit="$(git rev-parse --short HEAD 2>/dev/null || echo '?')"
last_deploy_note="none since desk started"

write_status() {
  {
    printf 'desk_pid=%s\n' "$$"
    printf 'updated=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')"
    printf 'api=%s\n' "$( api_healthy && echo up || echo DOWN )"
    printf 'commit=%s\n' "$last_commit"
    printf 'last_deploy=%s\n' "$last_deploy_note"
    printf 'market_open_ran_on=%s\n' "${opened_on:-not yet today}"
  } > "$STATUS.tmp" && mv "$STATUS.tmp" "$STATUS"
}

# --- deploy ---------------------------------------------------------------------
deploy_if_behind() {
  git fetch origin --quiet 2>>"$LOG" || { warn "git fetch failed; leaving everything alone"; return; }
  local local_sha remote_sha
  local_sha="$(git rev-parse HEAD)"; remote_sha="$(git rev-parse origin/main)"
  [ "$local_sha" != "$remote_sha" ] || return 0

  say "origin/main moved: $(git rev-parse --short "$local_sha") -> $(git rev-parse --short "$remote_sha")"
  local started; started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  local from_short to_short; from_short="$(git rev-parse --short "$local_sha")"; to_short="$(git rev-parse --short "$remote_sha")"
  record() {  # outcome summary [step ...]  — writes the record the Deployments page shows
    local outcome="$1" summary="$2"; shift 2
    local args=(--outcome "$outcome" --summary "$summary" --from "$from_short" --to "$to_short" --started "$started")
    for st in "$@"; do args+=(--step "$st"); done
    if [ -n "${changed:-}" ]; then args+=(--files "$(printf '%s\n' "$changed" | wc -l | tr -d ' ')"); fi
    while IFS= read -r line; do [ -n "$line" ] && args+=(--commit "$line"); done < <(git log --oneline "$local_sha..$remote_sha" 2>/dev/null | head -20)
    python3 scripts/lib/deploy-record.py "${args[@]}" >>"$LOG" 2>&1 || true
  }

  # Refuse to destroy local work. A dirty tree here means someone is editing
  # on this machine; the deploy waits for them to commit or stash.
  if [ -n "$(git status --porcelain)" ]; then
    warn "working tree has uncommitted changes — NOT pulling. Commit or stash them and the next check will deploy."
    last_deploy_note="blocked by uncommitted local changes at $(date '+%H:%M')"
    record skipped "Uncommitted changes on this machine" "Pulled from GitHub|skipped|$(git status --porcelain | wc -l | tr -d ' ') uncommitted file(s) on this machine"
    return
  fi
  if ! git merge-base --is-ancestor "$local_sha" "$remote_sha"; then
    warn "local HEAD is not an ancestor of origin/main (diverged) — NOT pulling. Resolve by hand."
    last_deploy_note="blocked: local history diverged at $(date '+%H:%M')"
    record failed "Not a fast-forward — the branches have diverged" "Pulled from GitHub|failed|Not a fast-forward - resolve by hand"
    return
  fi

  git pull --ff-only --quiet origin main >>"$LOG" 2>&1 || { warn "git pull failed (see desk.log)"; record failed "git pull failed" "Pulled from GitHub|failed|see logs/desk.log"; return; }
  local changed; changed="$(git diff --name-only "$local_sha" "$remote_sha")"
  last_commit="$(git rev-parse --short HEAD)"
  say "pulled to $last_commit: $(printf '%s\n' "$changed" | wc -l | tr -d ' ') file(s)"

  local web_changed api_changed engine_changed
  web_changed="$(printf '%s\n' "$changed" | grep -c '^web/' || true)"
  api_changed="$(printf '%s\n' "$changed" | grep -cE '^src/AlgoTrading\.(Api|Application|Domain|Infrastructure|Contracts)/|^tests/' || true)"
  engine_changed="$(printf '%s\n' "$changed" | grep -c '^src/AlgoTrading.PythonEngine/' || true)"
  local notes=() steps=("Pulled from GitHub|ok|$(git log --oneline "$local_sha..$remote_sha" | wc -l | tr -d ' ') commit(s), $(printf '%s\n' "$changed" | wc -l | tr -d ' ') file(s)")

  if [ "$web_changed" -gt 0 ]; then
    if ( cd web && npm ci --silent >>"$LOG" 2>&1 || true ) && web_build; then notes+=("console rebuilt"); steps+=("Console rebuilt|ok|New bundle is being served - no restart needed"); else notes+=("console build FAILED"); steps+=("Console rebuilt|failed|Build failed - the old bundle is still being served"); fi
  fi

  if [ "$api_changed" -gt 0 ]; then
    # Build BEFORE stopping the old API, so a broken build costs nothing but
    # a log line and the running API stays up.
    say "API code changed — building ..."
    if dotnet build src/AlgoTrading.Api -v q --nologo >>"$LOG" 2>&1; then
      local live; live="$(live_runs)"
      if [ "$live" != "0" ]; then
        say "  restarting the API with $live live run(s) — runners survive and re-register (owner's policy)"
      fi
      if api_restart; then notes+=("API rebuilt and restarted"); steps+=("API rebuilt and restarted|ok|Backend is live on the new build ($live live run(s) kept)"); else notes+=("API restart FAILED"); steps+=("API rebuilt and restarted|failed|See logs/api.log"); fi
    else
      notes+=("API build FAILED — old API still running")
      steps+=("API rebuilt and restarted|failed|dotnet build failed - the old API keeps running")
      warn "dotnet build failed; the old API keeps running. See desk.log."
    fi
  fi

  if [ "$engine_changed" -gt 0 ]; then
    notes+=("engine changed: live runs keep the old code until restarted; new runs use the new")
    steps+=("Python engine|ok|Pulled; live runs keep the code they started with, new runs use the new")
  fi

  [ ${#notes[@]} -gt 0 ] || notes+=("nothing to rebuild (docs/scripts only)")
  last_deploy_note="$last_commit at $(date '+%H:%M') — $(IFS='; '; echo "${notes[*]}")"
  say "deploy: $last_deploy_note"
  local outcome=ok; case "$last_deploy_note" in *FAILED*) outcome=failed;; esac
  record "$outcome" "$(IFS='; '; echo "${notes[*]}")" "${steps[@]}"
  osascript -e "display notification \"$last_deploy_note\" with title \"AlgoTrading deploy\"" 2>/dev/null || true
}

# --- the loop -------------------------------------------------------------------
while true; do
  now=$(date +%s)

  # 1. health
  if api_healthy; then
    fails=0
  else
    fails=$((fails + 1))
    warn "API health check failed ($fails/3)"
    if [ "$fails" -ge 3 ]; then
      say "API is down — restarting"
      api_restart || true
      fails=0
    fi
  fi

  # 2. deploy
  if [ $((now - last_deploy_check)) -ge "$DEPLOY_EVERY" ]; then
    deploy_if_behind
    last_deploy_check=$now
  fi

  # 3. market open, once per weekday
  today="$(date +%F)"; dow="$(date +%u)"; hhmm="$(date +%H%M)"
  if [ "$dow" -le 5 ] && [ "$hhmm" -ge "$OPEN_AT" ] && [ "$hhmm" -lt 1500 ] && [ "$opened_on" != "$today" ]; then
    say "=== $OPEN_AT — running market-open.sh ==="
    ./scripts/market-open.sh >>"$LOG" 2>&1 || warn "market-open.sh exited non-zero (see logs/market-open-$today.log)"
    opened_on="$today"
  fi

  write_status
  # Backgrounded and waited on, not run in the foreground: bash delivers a
  # signal only after the foreground command returns, so a plain `sleep 30`
  # made Ctrl+C and the keepalive's checks wait out the whole interval.
  sleep "$HEALTH_EVERY" & wait $!
done
