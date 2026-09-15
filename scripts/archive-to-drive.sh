#!/usr/bin/env bash
# Nightly: copies each finished trading day of ticks, option chain snapshots and
# 1-minute bars to Google Drive, verified (scripts/archive_to_drive.py).
#
# Run by cron at 06:00 IST, after the UTC day has ended and before the 08:45
# morning job (see docs/modules/data_archive.md for the crontab line).
# ARCHIVE_DROP_OLDER_THAN_DAYS in .env, when set, also frees the server's disk
# of verified days older than that; unset, nothing is ever deleted.
set -uo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/archive-$(date +%F).log"
. scripts/lib/desk-common.sh
load_env >/dev/null 2>&1 || true
# The manifest lives where the desk keeps its state.
export DESK_STATE_DIR

args=()
[ -n "${ARCHIVE_DROP_OLDER_THAN_DAYS:-}" ] && args+=(--drop-older-than "$ARCHIVE_DROP_OLDER_THAN_DAYS")

before="$(df -h / | awk 'NR==2 {print $4}')"
if python3 scripts/archive_to_drive.py "${args[@]}" >>"$LOG" 2>&1; then
  archived="$(grep -c -- '-> VERIFIED$' "$LOG" 2>/dev/null || echo 0)"
  say "archive to Drive: ok, ${archived} file(s) verified; disk free ${before} -> $(df -h / | awk 'NR==2 {print $4}')"
  [ "${archived:-0}" -gt 0 ] && notify "AlgoTrading" "Archive to Drive: ${archived} file(s) copied and verified. Disk free: $(df -h / | awk 'NR==2 {print $4}')."
else
  warn "archive to Drive FAILED — see $LOG"
  notify "AlgoTrading" "Archive to Drive FAILED at $(date '+%H:%M'). Nothing was deleted. Log: logs/archive-$(date +%F).log"
fi
