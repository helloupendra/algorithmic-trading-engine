#!/usr/bin/env bash
# Daily: what a new server needs besides the database and the code, encrypted, to Google Drive.
#
# The database dump (backup-db-to-drive.sh) and the git repositories cover the data
# and the code. A server that is lost — an AWS account closed overnight — also takes
# the files that exist nowhere else: .env, the API's appsettings.Local.json, the
# rclone config that reaches Drive, the Cloudflare tunnel token, the systemd units
# and the crontab, the pager (not in git), and the simulated broker's .env and
# database. This packs them into one tar, encrypts it with CONFIG_BACKUP_KEY from
# .env (AES-256-CBC, PBKDF2), uploads it to CONFIG_BACKUP_REMOTE (default
# openfno-new:OpenFNO/config), compares the Drive copy's MD5, and keeps the newest
# CONFIG_BACKUP_KEEP (default 30).
#
# The key has to live off this server as well — on the Mac and in a password
# manager — because the bundle is useless without it and a lost server cannot give
# it back. To open a bundle:
#   openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 \
#     -in config-<stamp>.tar.enc -out config.tar -pass file:<key file>
# The restore steps are in scripts/aws/README.md ("Moving to a new AWS account").
#
# Run every morning by archive-to-drive.sh after the database backup; by hand with
# no arguments.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
REPO_ROOT="$PWD"
LOG="$REPO_ROOT/logs/config-backup-$(date +%F).log"
mkdir -p "$REPO_ROOT/logs"
. scripts/lib/desk-common.sh
load_env >/dev/null 2>&1 || true

REMOTE="${CONFIG_BACKUP_REMOTE:-openfno-new:OpenFNO/config}"
KEEP="${CONFIG_BACKUP_KEEP:-30}"
BROKER_DIR="${BROKER_DIR:-$HOME/openfno-broker}"
PAGER_DIR="${PAGER_DIR:-$HOME/openfno-pager}"
BROKER_DB_CONTAINER="${BROKER_DB_CONTAINER:-openfno-broker-postgres-1}"

fail() {
  warn "config backup to Drive FAILED: $1 — see $LOG"
  notify "AlgoTrading" "Config backup to Drive FAILED at $(date '+%H:%M'): $1. Log: logs/config-backup-$(date +%F).log"
  exit 1
}

[ -n "${CONFIG_BACKUP_KEY:-}" ] || fail "CONFIG_BACKUP_KEY is not set in .env"
echo "== $(date '+%F %T') config backup -> $REMOTE" >>"$LOG"
rclone lsf "$REMOTE" >/dev/null 2>>"$LOG" || rclone mkdir "$REMOTE" >>"$LOG" 2>&1 || fail "Drive cannot be reached (token?)"

umask 077
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
stage="$work/config"
mkdir -p "$stage"

# Copies one file into the bundle under the given path; a file that is not there is
# listed as missing rather than failing the backup, so one absent piece never costs
# the rest. sudo only for the root-owned files.
take() {  # source, path in the bundle, [sudo]
  local src="$1" dest="$stage/$2"
  mkdir -p "$(dirname "$dest")"
  if [ "${3:-}" = sudo ]; then
    # sudo reads the root-owned file; the redirect writes it as this user, into the
    # private staging folder — which is the point.
    # shellcheck disable=SC2024
    sudo -n test -f "$src" 2>/dev/null && sudo -n cat "$src" >"$dest" 2>>"$LOG" && { echo "  $2" >>"$work/manifest"; return; }
  elif [ -f "$src" ]; then
    cp "$src" "$dest" && { echo "  $2" >>"$work/manifest"; return; }
  fi
  rm -f "$dest"
  echo "  MISSING $src" >>"$work/manifest"
}

take "$REPO_ROOT/.env" engine/.env
take "$REPO_ROOT/src/AlgoTrading.Api/appsettings.Local.json" engine/appsettings.Local.json
take "$HOME/.config/rclone/rclone.conf" home/rclone.conf
take /etc/cloudflared/token etc/cloudflared/token sudo
for unit in algotrading-desk.service algotrading-sentinel.service openfno-pager.service \
            cloudflared.service cloudflared-update.service cloudflared-update.timer; do
  take "/etc/systemd/system/$unit" "etc/systemd/$unit" sudo
done
crontab -l >"$stage/crontab.txt" 2>/dev/null && echo "  crontab.txt" >>"$work/manifest"
take "$BROKER_DIR/.env" broker/.env

# The pager is not in any repository, so its code and state travel whole.
if [ -d "$PAGER_DIR" ]; then
  tar -C "$(dirname "$PAGER_DIR")" -cf "$stage/openfno-pager.tar" \
      --exclude='__pycache__' --exclude='*.log' "$(basename "$PAGER_DIR")" 2>>"$LOG" \
    && echo "  openfno-pager.tar" >>"$work/manifest"
fi

# The broker's database is small (a few MB) and not part of the main dump.
if docker ps --format '{{.Names}}' | grep -qx "$BROKER_DB_CONTAINER"; then
  db_user="$(docker exec "$BROKER_DB_CONTAINER" printenv POSTGRES_USER 2>/dev/null)"
  db_name="$(docker exec "$BROKER_DB_CONTAINER" printenv POSTGRES_DB 2>/dev/null)"
  if docker exec "$BROKER_DB_CONTAINER" pg_dump -U "$db_user" -d "$db_name" -Fc </dev/null >"$stage/broker-db.dump" 2>>"$LOG"; then
    echo "  broker-db.dump ($db_name)" >>"$work/manifest"
  else
    rm -f "$stage/broker-db.dump"; echo "  MISSING broker database dump" >>"$work/manifest"
  fi
fi

{
  echo "OpenFNO server config bundle"
  echo "host: $(hostname)   made: $(date '+%F %T %Z')"
  echo "engine commit: $(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null)"
  echo "broker commit: $(git -C "$BROKER_DIR" rev-parse --short HEAD 2>/dev/null)"
  echo "files:"; cat "$work/manifest"
} >"$stage/MANIFEST.txt"
cat "$stage/MANIFEST.txt" >>"$LOG"

name="config-$(date +%Y%m%d-%H%M).tar.enc"
tar -C "$work" -czf - config 2>>"$LOG" \
  | openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -md sha256 -salt -pass env:CONFIG_BACKUP_KEY -out "$work/$name" 2>>"$LOG" \
  || fail "packing or encrypting the bundle failed"

rclone copy "$work/$name" "$REMOTE/" --retries 2 >>"$LOG" 2>&1 || fail "the upload of $name did not finish"
local_md5="$(md5sum "$work/$name" | cut -d' ' -f1)"
drive_md5="$(rclone md5sum "$REMOTE/$name" 2>>"$LOG" | cut -d' ' -f1)"
[ -n "$drive_md5" ] && [ "$local_md5" = "$drive_md5" ] \
  || fail "the Drive copy of $name does not match (server $local_md5, Drive ${drive_md5:-none})"

old="$(rclone lsf "$REMOTE" --include 'config-*.tar.enc' 2>>"$LOG" | sort -r | tail -n +"$((KEEP + 1))")"
for f in $old; do
  rclone deletefile "$REMOTE/$f" >>"$LOG" 2>&1 && echo "removed old bundle $f" >>"$LOG"
done

missing="$(grep -c MISSING "$work/manifest" || true)"
say "config backup to Drive: ok, $name verified (${missing} missing); keeping the newest $KEEP"
[ "$missing" = 0 ] || notify "AlgoTrading" "Config backup: $name is on Drive, but ${missing} file(s) were missing — see logs/config-backup-$(date +%F).log"
