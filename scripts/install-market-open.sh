#!/usr/bin/env bash
# Installs (or removes) the launchd job that runs scripts/market-open.sh on
# trading mornings.
#
# launchd rather than a cron job inside a Claude session: this has to fire
# whether or not a terminal is open, and survive the machine being asleep —
# StartCalendarInterval runs the job at the next wake when the Mac slept
# through its time.
#
# Usage:
#   ./scripts/install-market-open.sh            # install, weekdays 09:05
#   ./scripts/install-market-open.sh --at 8 55  # a different hour/minute
#   ./scripts/install-market-open.sh --remove

set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"

LABEL="com.algotrading.market-open"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
HOUR=9
MIN=5

while [ $# -gt 0 ]; do
  case "$1" in
    --remove)
      launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || launchctl unload "$PLIST" 2>/dev/null || true
      rm -f "$PLIST"
      echo "Removed $LABEL."
      exit 0 ;;
    --at) HOUR="$2"; MIN="$3"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

mkdir -p "$HOME/Library/LaunchAgents" "$REPO_ROOT/logs"

# Weekday 1-5 is Monday-Friday. The script re-checks the day itself, so a
# holiday costs nothing but a log line.
{
  printf '<?xml version="1.0" encoding="UTF-8"?>\n'
  printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
  printf '<plist version="1.0">\n<dict>\n'
  printf '  <key>Label</key><string>%s</string>\n' "$LABEL"
  printf '  <key>ProgramArguments</key>\n  <array>\n'
  printf '    <string>/bin/bash</string>\n'
  printf '    <string>%s/scripts/market-open.sh</string>\n' "$REPO_ROOT"
  printf '  </array>\n'
  printf '  <key>WorkingDirectory</key><string>%s</string>\n' "$REPO_ROOT"
  printf '  <key>StartCalendarInterval</key>\n  <array>\n'
  for d in 1 2 3 4 5; do
    printf '    <dict><key>Weekday</key><integer>%s</integer>' "$d"
    printf '<key>Hour</key><integer>%s</integer>' "$HOUR"
    printf '<key>Minute</key><integer>%s</integer></dict>\n' "$MIN"
  done
  printf '  </array>\n'
  printf '  <key>StandardOutPath</key><string>%s/logs/launchd-market-open.log</string>\n' "$REPO_ROOT"
  printf '  <key>StandardErrorPath</key><string>%s/logs/launchd-market-open.log</string>\n' "$REPO_ROOT"
  # docker, dotnet and node are not on launchd's bare PATH.
  printf '  <key>EnvironmentVariables</key>\n  <dict>\n'
  printf '    <key>PATH</key><string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:%s/.dotnet/tools</string>\n' "$HOME"
  printf '  </dict>\n'
  printf '  <key>RunAtLoad</key><false/>\n'
  printf '</dict>\n</plist>\n'
} > "$PLIST"

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"

printf 'Installed %s — weekdays at %02d:%02d.\n' "$LABEL" "$HOUR" "$MIN"
printf '  report:  %s/logs/market-open-YYYY-MM-DD.log\n' "$REPO_ROOT"
printf '  remove:  ./scripts/install-market-open.sh --remove\n'
printf '\nThe Mac must be awake at that time. System Settings -> Battery ->\n'
printf 'Options -> "Wake for network access", or leave it plugged in and awake.\n'
