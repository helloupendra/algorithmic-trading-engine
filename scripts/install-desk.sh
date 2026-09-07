#!/usr/bin/env bash
# Makes scripts/desk.sh start at login and come back if it is closed.
#
# Two launchd jobs, both of which only ever call /usr/bin/open — the one thing
# a launchd process may do to a script under ~/Documents (privacy protection
# denies it bash, git and dotnet there; Terminal.app is allowed):
#   com.algotrading.desk           at login: open desk.sh in Terminal
#   com.algotrading.desk-keepalive every 10 min: if the desk pid is gone, reopen
# The keepalive's own script lives under ~/Library/Application Support, outside
# the protected folder, so launchd can run it directly.
#
# Replaces com.algotrading.market-open: the desk runs market-open.sh itself at
# 08:45, and two schedulers would start everything twice.
#
# Usage: ./scripts/install-desk.sh            ./scripts/install-desk.sh --remove

set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
AGENTS="$HOME/Library/LaunchAgents"
SUPPORT="$HOME/Library/Application Support/algotrading"
UID_="$(id -u)"

if [ "${1:-}" = "--remove" ]; then
  for l in com.algotrading.desk com.algotrading.desk-keepalive; do
    launchctl bootout "gui/$UID_/$l" 2>/dev/null || true; rm -f "$AGENTS/$l.plist"
  done
  echo "Removed the desk jobs. The desk keeps running until you close its window."
  exit 0
fi

mkdir -p "$AGENTS" "$SUPPORT" logs

# The keepalive, outside ~/Documents.
cat > "$SUPPORT/desk-keepalive.sh" <<EOF
#!/bin/bash
PID="\$(cat "$SUPPORT/desk.pid" 2>/dev/null)"
if [ -n "\$PID" ] && kill -0 "\$PID" 2>/dev/null; then exit 0; fi
/usr/bin/open -a Terminal "$REPO_ROOT/scripts/desk.sh"
EOF
chmod +x "$SUPPORT/desk-keepalive.sh"

plist() {  # label, then the rest of the dict body
  local label="$1"; shift
  {
    printf '<?xml version="1.0" encoding="UTF-8"?>\n'
    printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
    printf '<plist version="1.0">\n<dict>\n  <key>Label</key><string>%s</string>\n' "$label"
    printf '%s\n' "$@"
    printf '</dict>\n</plist>\n'
  } > "$AGENTS/$label.plist"
  launchctl bootout "gui/$UID_/$label" 2>/dev/null || true
  launchctl bootstrap "gui/$UID_" "$AGENTS/$label.plist"
}

plist com.algotrading.desk \
  '  <key>ProgramArguments</key><array><string>/usr/bin/open</string><string>-a</string><string>Terminal</string>' \
  "    <string>$REPO_ROOT/scripts/desk.sh</string></array>" \
  '  <key>RunAtLoad</key><true/>'

plist com.algotrading.desk-keepalive \
  "  <key>ProgramArguments</key><array><string>$SUPPORT/desk-keepalive.sh</string></array>" \
  '  <key>StartInterval</key><integer>600</integer>' \
  '  <key>RunAtLoad</key><false/>'

# The standalone market-open job is superseded by the desk.
launchctl bootout "gui/$UID_/com.algotrading.market-open" 2>/dev/null || true
rm -f "$AGENTS/com.algotrading.market-open.plist"

echo "Installed:"
echo "  com.algotrading.desk            opens the desk in Terminal at login"
echo "  com.algotrading.desk-keepalive  reopens it within 10 min if it is closed"
echo "  (com.algotrading.market-open removed — the desk runs it at 08:45)"
echo
echo "Start it now without logging out:   ./scripts/desk.sh   (or wait ≤10 min)"
echo "See it any time:                    ./scripts/status.sh"
