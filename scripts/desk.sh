#!/usr/bin/env bash
# The trading desk: one long-running loop that keeps the platform up.
#
#   - keeps the API healthy: after three failed health checks, 30 s apart, it
#     checks the database and Redis, then restarts the API;
#   - every two minutes checks GitHub. When origin/main has moved it pulls at
#     once, but builds and restarts only on a quiet desk: weekends, weekdays
#     before 08:40 or after the evening close, and no live run (deploy_allowed
#     in lib/desk-common.sh; `touch "$DESK_STATE_DIR/deploy-now"` overrides it
#     for an hour). The commit last built is kept in DESK_STATE_DIR/deployed-sha,
#     so a restart between a pull and its build still builds it;
#   - at 08:45 on weekdays runs scripts/market-open.sh, once a day even across
#     desk restarts: a marker file per day decides (daily_job in
#     lib/desk-common.sh), and a job that was interrupted is reported, never
#     rerun by itself;
#   - at 23:58 on weekdays, after the MCX close (23:30 or 23:55), runs
#     scripts/market-close.sh the same way — after midnight if it was missed —
#     so no feed, recorder or run is left holding a session overnight;
#   - writes logs/desk.status every loop so scripts/status.sh can answer
#     "is it running?" without guessing.
#
# Where it runs. On the server (Linux, AWS since 2026-09-17) it is the systemd
# unit algotrading-desk (scripts/aws/bootstrap.sh). Restart=always brings it
# back 15 s after it exits, so there --headless and --stop only print the
# systemctl command. KillMode=process: stopping or restarting the unit ends
# this loop alone — the API and a morning job it started keep running.
#
# On the Mac it is born in a Terminal window, not as a launchd service: macOS
# privacy protection refuses a launchd-spawned bash, git or dotnet any file
# under ~/Documents, where this repo lives — the first scheduled morning failed
# exactly that way. Terminal holds the permission and passes it on, so
# launchd's only job (install-desk.sh) is to open the launcher in Terminal,
# which runs this script with --headless: the loop detaches into the background
# with its output in logs/desk.log, and the window closes itself a second
# later. The permission stays with the process (verified 2026-09-08 after
# Terminal.app was quit); should a future macOS take it away, the loop
# notices — it can no longer read the repo — exits, and the keepalive reopens
# it through Terminal within ten minutes.
#
# A deploy never kills a live run: runners are separate processes that survive
# an API restart and re-register with the new API, and the Python engine keeps
# the code a run started with until that run is restarted by hand.
#
# Switches, from the environment or .env, read when the desk starts:
#   DESK_BACKGROUND_OPEN=1   run the morning job in the background and keep
#                            checking the API meanwhile (default: foreground)
#   API_BUILD_CONFIG=Release build the API Release and run its output
#                            directly (default: `dotnet run`, a Debug build).
#                            It applies from the next API restart; to switch
#                            at once, restart the desk, then --restart-api.
#
# Usage: ./scripts/desk.sh               here, in this window; Ctrl+C stops it
#        ./scripts/desk.sh --headless    (Mac) in the background; watch logs/desk.log
#        ./scripts/desk.sh --stop        (Mac) stop the background loop; the API stays up
#        ./scripts/desk.sh --restart-api restart the API once, as the desk would
#                                        (refused while runs are live)
#        (--daemon is the detached copy: the launcher, --headless and systemd run it)

set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/desk.log"
. scripts/lib/desk-common.sh

STATUS="$REPO_ROOT/logs/desk.status"
PIDFILE="$DESK_STATE_DIR/desk.pid"

HEALTH_EVERY=30          # seconds between health checks
DEPLOY_EVERY=120         # seconds between git checks
OPEN_AT="${MARKET_OPEN_AT:-0845}"   # HHMM, weekdays
# HHMM, weekdays: after the MCX close — 23:30 in the US summer, 23:55 in its
# winter — stop every feed, recorder and run (market_close_due in
# desk-common.sh has why 23:58, and the catch-up after midnight). Any value set
# here must fall after 23:55 and before midnight. A feed left alive overnight
# wakes up with a dead token and reconnects in a loop — on 2026-09-16 that got
# the Dhan account blocked (scripts/market-close.sh).
CLOSE_AT="${MARKET_CLOSE_AT:-$MARKET_CLOSE_AT_DEFAULT}"

desk_pid() { [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null && cat "$PIDFILE"; }

# Starts a command in the background in its own session: no controlling
# terminal, so Terminal does not count it as "a running process" when it
# decides whether a window may close, and closing that window cannot signal
# it. The subshell execs straight into it — a plain `f … &` would leave a copy
# of this script sitting there as the command's parent until it exits.
spawn_detached() { ( exec python3 -c 'import os,sys; os.setsid(); os.execv(sys.argv[1], sys.argv[1:])' "$@" ) & }

# Closes the Terminal window this shell is running in, after this shell has
# exited — so the window holds no process and Terminal closes it without asking.
close_own_window() {
  local tty_dev win
  tty_dev="$(tty 2>/dev/null)" || return 0
  win="$(osascript -e "tell application \"Terminal\" to id of first window whose tty is \"$tty_dev\"" 2>/dev/null)" || return 0
  [ -n "$win" ] || return 0
  spawn_detached /bin/bash -c "sleep 1; osascript -e 'tell application \"Terminal\" to close window id $win' >/dev/null 2>&1" </dev/null >/dev/null 2>&1
}

# On the server the desk belongs to systemd (Restart=always): a copy started
# beside it, or a kill it undoes in 15 s, would only confuse the two.
SYSTEMD_UNIT=algotrading-desk

case "${1:-}" in
  --headless)
    if ! $IS_MAC; then
      echo "On this server the desk is the systemd unit $SYSTEMD_UNIT. Start it with:"
      echo "  sudo systemctl start $SYSTEMD_UNIT      (then: scripts/status.sh; its log is logs/desk.log)"
      exit 0
    fi
    if pid="$(desk_pid)"; then
      echo "desk is already running in the background (pid $pid) — logs/desk.log"
    else
      spawn_detached /bin/bash "$REPO_ROOT/scripts/desk.sh" --daemon </dev/null >>"$LOG" 2>&1
      sleep 1
      echo "desk started in the background — log: logs/desk.log, status: scripts/status.sh, stop: scripts/desk.sh --stop"
    fi
    # Only the launcher sets this: a window opened just for the desk closes
    # itself; a window the owner typed into is left alone.
    [ "${DESK_CLOSE_WINDOW:-}" = "1" ] && close_own_window
    exit 0;;
  --stop)
    if ! $IS_MAC; then
      echo "On this server the desk is the systemd unit $SYSTEMD_UNIT (Restart=always: a kill is undone in 15 s). Stop it with:"
      echo "  sudo systemctl stop $SYSTEMD_UNIT      (the API, and a morning job under way, keep running: KillMode=process)"
      exit 0
    fi
    if pid="$(desk_pid)"; then kill "$pid" && echo "desk (pid $pid) stopped; the API keeps running"; else echo "desk is not running"; fi
    exit 0;;
  --restart-api)
    # One API restart, built and run the way the desk would (API_BUILD_CONFIG):
    # how a switch of that setting is tried out at once, or rolled back. Not
    # under live runs, like a deploy; DESK_RESTART_API_FORCE=1 overrides.
    live="$(live_runs)"
    if [ "$live" != 0 ] && [ -z "${DESK_RESTART_API_FORCE:-}" ]; then
      echo "not restarting the API: $( [ "$live" = -1 ] && echo 'the live-run count is unknown' || echo "$live run(s) are live" ) (DESK_RESTART_API_FORCE=1 overrides)"
      exit 1
    fi
    api_restart
    exit $?;;
  --daemon)
    # stdout is already the log: say() must not tee into it a second time.
    export DESK_LOG_ONLY=1;;
  "") ;;
  *) echo "usage: scripts/desk.sh [--headless | --stop | --restart-api]"; exit 2;;
esac

# One desk at a time. A second copy would double every restart and deploy.
if pid="$(desk_pid)"; then
  echo "desk is already running (pid $pid). Use scripts/status.sh to look at it."
  exit 0
fi
echo $$ > "$PIDFILE"
trap 'rm -f "$PIDFILE"; say "desk stopped (the API is left running)"; exit 0' INT TERM

say "=== desk started (pid $$, $( [ -n "${DESK_LOG_ONLY:-}" ] && echo background || echo 'this window' )) — API $API, chain $CHAIN_UNDERLYINGS, open at $OPEN_AT ==="
[ "$DESK_BG_OPEN" = 1 ] && say "DESK_BACKGROUND_OPEN=1: the morning job runs in the background; the loop keeps checking the API meanwhile"
[ "$DESK_API_CONFIG" = Release ] && say "API_BUILD_CONFIG=Release: the API is built Release and run from its output, from its next restart on"

# --- infra, then the API --------------------------------------------------------
say "infra ..."
infra_up || true
api_start || true

# --- what was last built ----------------------------------------------------------
# Two ways a commit reaches this machine: pulled from GitHub, or made right here
# and pushed. Both must be built, so the desk remembers the commit it last built
# (deployed_sha) and builds whatever HEAD has moved past it — the pull is only
# the first half. It is kept in a file, written after each deploy: taken from
# HEAD at startup, as it was until 28 Sep, a desk restarted between a pull and
# its build (a deferred deploy, a crash, a systemd restart) counted the pulled
# commit as built and never built it.
# >>> deployed-sha (also loaded by scripts/tests/desk-hygiene.test.sh)
DEPLOYED_SHA_FILE="$DESK_STATE_DIR/deployed-sha"
# Written before a pull and removed when the deploy step returns: found at
# startup, the last deploy was cut off halfway.
DEPLOY_PENDING_FILE="$DESK_STATE_DIR/deploy-pending"
save_deployed_sha() { printf '%s\n' "$1" >"$DEPLOYED_SHA_FILE.tmp" && mv -f "$DEPLOYED_SHA_FILE.tmp" "$DEPLOYED_SHA_FILE"; }
deploy_pending() { printf '%s at %s\n' "$1" "$(date '+%F %T')" >"$DEPLOY_PENDING_FILE"; }

# Sets deployed_sha as the desk starts: the recorded commit while it is one in
# this checkout, else HEAD (and records that).
load_deployed_sha() {
  local sha
  sha="$(head -1 "$DEPLOYED_SHA_FILE" 2>/dev/null || true)"
  if [ -z "$sha" ] || ! git cat-file -e "$sha^{commit}" 2>/dev/null; then
    [ -z "$sha" ] || warn "the recorded deployed commit $sha is not in this checkout — taking HEAD as built"
    sha="$(git rev-parse HEAD 2>/dev/null || echo '')"
    [ -z "$sha" ] || save_deployed_sha "$sha"
  fi
  if [ -f "$DEPLOY_PENDING_FILE" ]; then
    warn "previous deploy was interrupted ($(head -1 "$DEPLOY_PENDING_FILE")) — redoing: everything since $(git rev-parse --short "$sha" 2>/dev/null) is built at the next check the deploy gate allows"
    rm -f "$DEPLOY_PENDING_FILE"
  fi
  deployed_sha="$sha"
}
# <<< deployed-sha
load_deployed_sha

fails=0
last_deploy_check=0
opened_on=""
closed_on=""
deferred_sha=""   # the commit whose deferral was already announced (once per commit)
deploy_held=0     # 1 while deploy checks wait for a background morning job
last_commit="$(git rev-parse --short "$deployed_sha" 2>/dev/null || echo '?')"
last_deploy_note="none since desk started"

write_status() {
  {
    printf 'desk_pid=%s\n' "$$"
    printf 'updated=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')"
    printf 'api=%s\n' "$( api_healthy && echo up || echo DOWN )"
    printf 'commit=%s\n' "$last_commit"
    printf 'last_deploy=%s\n' "$last_deploy_note"
    printf 'market_open_ran_on=%s\n' "${opened_on:-not yet today}"
    printf 'market_open_today=%s\n' "$(job_state "$(job_marker market-open "$(date +%F)")" market-open)"
  } > "$STATUS.tmp" && mv "$STATUS.tmp" "$STATUS"
}

# --- deploy ---------------------------------------------------------------------
# What was built last is deployed_sha (above). deploy-pending lives only while
# this runs; a desk that finds it at startup redoes the deploy.
deploy_if_behind() {
  _deploy_if_behind
  rm -f "$DEPLOY_PENDING_FILE"
}

_deploy_if_behind() {
  git fetch origin --quiet 2>>"$LOG" || { warn "git fetch failed; leaving everything alone"; return; }
  local local_sha remote_sha
  local_sha="$(git rev-parse HEAD)"; remote_sha="$(git rev-parse origin/main)"
  local started; started="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  local from_short to_short; from_short="$(git rev-parse --short "$deployed_sha")"; to_short="$(git rev-parse --short "$remote_sha")"
  record() {  # outcome summary [step ...]  — writes the record the Deployments page shows
    local outcome="$1" summary="$2"; shift 2
    local args=(--outcome "$outcome" --summary "$summary" --from "$from_short" --to "$to_short" --started "$started")
    for st in "$@"; do args+=(--step "$st"); done
    if [ -n "${changed:-}" ]; then args+=(--files "$(printf '%s\n' "$changed" | wc -l | tr -d ' ')"); fi
    while IFS= read -r line; do [ -n "$line" ] && args+=(--commit "$line"); done < <(git log --oneline "$deployed_sha..$to_short" 2>/dev/null | head -20)
    python3 scripts/lib/deploy-record.py "${args[@]}" >>"$LOG" 2>&1 || true
  }

  if [ "$local_sha" != "$remote_sha" ]; then
    say "origin/main moved: $(git rev-parse --short "$local_sha") -> $to_short"
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
    deploy_pending "pulling $(git rev-parse --short "$local_sha") -> $to_short"
    git pull --ff-only --quiet origin main >>"$LOG" 2>&1 || { warn "git pull failed (see desk.log)"; record failed "git pull failed" "Pulled from GitHub|failed|see logs/desk.log"; return; }
  fi

  local head; head="$(git rev-parse HEAD)"
  [ "$head" != "$deployed_sha" ] || return 0
  to_short="$(git rev-parse --short "$head")"
  local changed; changed="$(git diff --name-only "$deployed_sha" "$head")"
  local how="pulled"; [ "$local_sha" = "$head" ] && how="committed here"
  say "building $from_short -> $to_short ($how): $(printf '%s\n' "$changed" | wc -l | tr -d ' ') file(s)"

  local web_changed api_changed engine_changed
  web_changed="$(printf '%s\n' "$changed" | console_build_inputs)"
  api_changed="$(printf '%s\n' "$changed" | grep -cE '^src/AlgoTrading\.(Api|Application|Domain|Infrastructure|Contracts)/' || true)"
  engine_changed="$(printf '%s\n' "$changed" | grep -c '^src/AlgoTrading.PythonEngine/' || true)"
  local notes=() steps=()
  if [ "$how" = "pulled" ]; then steps+=("Pulled from GitHub|ok|$(git log --oneline "$deployed_sha..$head" | wc -l | tr -d ' ') commit(s), $(printf '%s\n' "$changed" | wc -l | tr -d ' ') file(s)")
  else steps+=("Committed on this machine|ok|$(git log --oneline "$deployed_sha..$head" | wc -l | tr -d ' ') commit(s), $(printf '%s\n' "$changed" | wc -l | tr -d ' ') file(s) - nothing to pull"); fi

  # Build and restart only on a quiet desk (see deploy_allowed). The pull above
  # already happened, so scripts and the Python engine are current for anything
  # started from now on; what waits is the console build and the API restart.
  # deployed_sha is left alone, so every check tries again and the deploy goes
  # out by itself once the desk is quiet.
  if [ "$web_changed" -gt 0 ] || [ "$api_changed" -gt 0 ]; then
    local g_dow g_hhmm g_closed g_live
    g_dow="$(date +%u)"; g_hhmm="$(date +%H%M)"
    g_closed=0; [ "$closed_on" = "$(date +%F)" ] && g_closed=1
    # The API is asked only when the clock would allow a deploy; in the
    # session the answer is no whatever the count.
    g_live=0
    if deploy_clock_allows "$g_dow" "$g_hhmm" "$g_closed" && ! deploy_now_requested; then g_live="$(live_runs)"; fi
    if ! deploy_allowed "$g_dow" "$g_hhmm" "$g_closed" "$g_live"; then
      if [ "$deferred_sha" != "$head" ]; then
        local why; why="$(deploy_block_reason "$g_dow" "$g_hhmm" "$g_closed" "$g_live")"
        say "deploy of $to_short deferred — $why; it goes out by itself when the desk is quiet (or: touch \"$DEPLOY_NOW_FILE\")"
        record skipped "Deferred: $why" "${steps[@]}" "Build and restart|deferred|$why - retried every check"
        notify "AlgoTrading deploy deferred" "$to_short waits: $why"
        deferred_sha="$head"
      fi
      return
    fi
    if deploy_now_requested; then
      say "deploy-now requested — building $to_short despite the desk not being quiet"
    fi
    rm -f "$DEPLOY_NOW_FILE"
  fi
  deploy_pending "building $from_short -> $to_short"

  if [ "$web_changed" -gt 0 ]; then
    if ( cd web && npm ci --silent >>"$LOG" 2>&1 || true ) && web_build; then notes+=("console rebuilt"); steps+=("Console rebuilt|ok|New bundle is being served - no restart needed"); else notes+=("console build FAILED"); steps+=("Console rebuilt|failed|Build failed - the old bundle is still being served"); fi
  fi

  # The API reads its secrets from appsettings.Local.json, which is generated
  # from .env. A value added to .env by hand therefore does nothing until that
  # file is written again — on 2026-09-21 a broker key sat in .env for an hour
  # while the console said "not configured". Regenerating on every deploy costs
  # nothing and makes .env the single place an operator has to edit.
  if [ -f scripts/_gen_local_settings.py ]; then
    if python3 scripts/_gen_local_settings.py --quiet >>"$LOG" 2>&1; then
      say "  settings regenerated from .env"
    else
      warn "could not regenerate appsettings.Local.json from .env (see desk.log)"
    fi
  fi

  if [ "$api_changed" -gt 0 ]; then
    # Build BEFORE stopping the old API, so a broken build costs nothing but
    # a log line and the running API stays up. The build is the one api_start
    # runs: Debug, or Release with API_BUILD_CONFIG=Release.
    say "API code changed — building${DESK_API_CONFIG:+ ($DESK_API_CONFIG)} ..."
    if api_build >>"$LOG" 2>&1; then
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
  last_commit="$to_short"
  last_deploy_note="$last_commit at $(date '+%H:%M') — $(IFS='; '; echo "${notes[*]}")"
  say "deploy: $last_deploy_note"
  local outcome=ok; case "$last_deploy_note" in *FAILED*) outcome=failed;; esac
  record "$outcome" "$(IFS='; '; echo "${notes[*]}")" "${steps[@]}"
  # One attempt per commit, pass or fail — a broken build is not retried every
  # two minutes; the next commit gets its own attempt. Kept across restarts.
  deployed_sha="$head"
  save_deployed_sha "$head" || warn "could not record $to_short in $DEPLOYED_SHA_FILE"
  notify "AlgoTrading deploy" "$last_deploy_note"
}

# --- the loop -------------------------------------------------------------------
while true; do
  now=$(date +%s)

  # 0. can this process still read the repo? (see the header on macOS privacy
  #    protection). Exit and let the keepalive start a fresh copy through
  #    Terminal rather than fail every step.
  if ! cat "$REPO_ROOT/.git/HEAD" >/dev/null 2>&1; then
    warn "lost permission to read the repo (Terminal.app quit?) — exiting; the keepalive reopens the desk within 10 min"
    rm -f "$PIDFILE"; exit 3
  fi

  today="$(date +%F)"; dow="$(date +%u)"; hhmm="$(date +%H%M)"

  # 1. health
  if api_healthy; then
    fails=0
  else
    fails=$((fails + 1))
    warn "API health check failed ($fails/3)"
    if [ "$fails" -ge 3 ]; then
      if [ "$DESK_BG_OPEN" = 1 ] && api_restart_in_progress; then
        # A morning job running beside the loop is restarting it right now;
        # a second restart would kill the API it is bringing up.
        say "the API is down while another script restarts it ($API_RESTARTING_FILE) — leaving that restart alone"
      else
        # The database first: an API restarted against a stopped Docker just
        # dies again, two minutes at a time.
        say "API is down — checking infra, then restarting"
        infra_up || true
        api_restart || true
        fails=0
      fi
    fi
  fi

  # 2. deploy — held while a background morning job runs: it restarts the API
  #    and the feeds itself, and a deploy in the middle would do it again.
  if [ $((now - last_deploy_check)) -ge "$DEPLOY_EVERY" ]; then
    if [ "$DESK_BG_OPEN" = 1 ] && job_in_progress market-open "$today"; then
      [ "$deploy_held" = 1 ] || say "deploy checks held while the morning job runs"
      deploy_held=1
    else
      deploy_held=0
      deploy_if_behind
    fi
    last_deploy_check=$now
  fi

  # 3. market open, once per weekday — once per DAY, even across desk
  #    restarts: the day's marker file decides, not opened_on (daily_job in
  #    lib/desk-common.sh). opened_on only spares re-reading it every loop.
  #    With DESK_BACKGROUND_OPEN=1 the job runs beside the loop, which keeps
  #    checking the API and writing its status, and asks after the job each
  #    loop until it has finished.
  if [ "$dow" -le 5 ] && [ "$hhmm" -ge "$OPEN_AT" ] && [ "$hhmm" -lt 1500 ] && [ "$opened_on" != "$today" ]; then
    open_mode="fg"
    [ "$DESK_BG_OPEN" = 1 ] && open_mode="bg"
    if daily_job market-open "$today" "$open_mode" "=== $OPEN_AT — running market-open.sh ===" ./scripts/market-open.sh; then
      opened_on="$today"
    fi
  fi

  # 4. market close, once per weekday, after the MCX session — or, when that
  #    was missed, after midnight for the day before (market_close_due). Once
  #    per day across restarts too, by the same kind of marker.
  yesterday="$(date -v-1d +%F 2>/dev/null || date -d yesterday +%F)"
  if close_day="$(market_close_due "$dow" "$hhmm" "$today" "$yesterday" "$closed_on" "$CLOSE_AT")"; then
    if [ "$close_day" = "$today" ]; then
      close_banner="=== $CLOSE_AT — running market-close.sh ==="
    else
      close_banner="=== market-close.sh for $close_day did not run at $CLOSE_AT — running it now ==="
    fi
    if daily_job market-close "$close_day" fg "$close_banner" ./scripts/market-close.sh; then
      closed_on="$close_day"
    fi
  fi

  write_status
  # Backgrounded and waited on, not run in the foreground: bash delivers a
  # signal only after the foreground command returns, so a plain `sleep 30`
  # made Ctrl+C and the keepalive's checks wait out the whole interval.
  sleep "$HEALTH_EVERY" & wait $!
done
