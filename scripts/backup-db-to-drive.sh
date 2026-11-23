#!/usr/bin/env bash
# Weekly: the whole database to Google Drive, verified.
#
# scripts/aws/migrate-db.sh dump writes algotrading-<stamp>.tar: every table
# except the raw ticks, plus the ASP.NET data-protection keys that decrypt the
# stored broker credentials and the trading PIN. This uploads it to
# DB_BACKUP_REMOTE (default openfno-new:OpenFNO/database), compares the Drive
# copy's MD5 with the server's, removes the server copy only when they match,
# and keeps the newest DB_BACKUP_KEEP (default 8) dumps on Drive.
#
# The raw ticks reach Drive every morning through archive-to-drive.sh, so the
# two jobs together cover what the database holds. Secrets in .env are not
# copied. Restore: ./scripts/aws/migrate-db.sh restore <file.tar>
#
# Run by cron on Sunday at 02:30 IST, when no market is open; by hand with no
# arguments. Setup notes are kept outside the public repo.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/db-backup-$(date +%F).log"
mkdir -p "$REPO_ROOT/logs"
. scripts/lib/desk-common.sh
load_env >/dev/null 2>&1 || true

REMOTE="${DB_BACKUP_REMOTE:-openfno-new:OpenFNO/database}"
KEEP="${DB_BACKUP_KEEP:-8}"

fail() {
  warn "database backup to Drive FAILED: $1 — see $LOG"
  notify "AlgoTrading" "Database backup to Drive FAILED at $(date '+%H:%M'): $1. Log: logs/db-backup-$(date +%F).log"
  exit 1
}

{
  echo "== $(date '+%F %T') database backup -> $REMOTE"
  rclone lsf "$REMOTE" >/dev/null 2>&1 || { echo "cannot reach $REMOTE"; exit 3; }
} >>"$LOG" 2>&1 || fail "Drive cannot be reached (token?)"

# The dump reads the database through docker; nice and ionice keep the live
# desk ahead of it. </dev/null so docker exec -i never reads this script's stdin.
nice -n 10 ionice -c3 ./scripts/aws/migrate-db.sh dump </dev/null >>"$LOG" 2>&1 || fail "the dump did not finish"
file="$(ls -t algotrading-*.tar 2>/dev/null | head -1)"
[ -n "$file" ] || fail "no dump file was written"
size="$(du -h "$file" | cut -f1)"

{
  echo "uploading $file ($size)"
  rclone copy "$file" "$REMOTE/" --drive-chunk-size 64M
} >>"$LOG" 2>&1 || fail "the upload of $file did not finish (the server copy is kept)"

local_md5="$(md5sum "$file" | cut -d' ' -f1)"
drive_md5="$(rclone md5sum "$REMOTE/$file" 2>>"$LOG" | cut -d' ' -f1)"
if [ -z "$drive_md5" ] || [ "$local_md5" != "$drive_md5" ]; then
  fail "the Drive copy of $file does not match (server $local_md5, Drive ${drive_md5:-none}; the server copy is kept)"
fi
rm -f "$file"
echo "verified $file (md5 $local_md5); the server copy was removed" >>"$LOG"

# Keep the newest KEEP dumps; the names sort by date (algotrading-YYYYMMDD-HHMM.tar).
old="$(rclone lsf "$REMOTE" --include 'algotrading-*.tar' 2>>"$LOG" | sort -r | tail -n +"$((KEEP + 1))")"
for f in $old; do
  rclone deletefile "$REMOTE/$f" >>"$LOG" 2>&1 && echo "removed old dump $f" >>"$LOG"
done

say "database backup to Drive: ok, $file ($size) verified; keeping the newest $KEEP"
notify "AlgoTrading" "Database backup to Drive: $file ($size) copied and verified."
