#!/usr/bin/env bash
# Runs Sentinel, the desk's watchman (src/AlgoTrading.PythonEngine/sentinel),
# as its own systemd service on the Ubuntu server: algotrading-sentinel.
#
# Its own service, not something desk.sh starts, on purpose: the watchman must
# not die with what it watches. "The desk supervisor is not running" is one of
# the things Sentinel reports, and a Sentinel that was the desk's child would
# go quiet at exactly that moment. systemd keeps it up on its own: it restarts
# it 15 s after any exit, including the clean exit Sentinel makes when a deploy
# changes its code (sentinel/reload.py).
#
# It runs as the desk's user, from the engine directory, on the same
# interpreter the API gives the strategy runners — the repo's .venv, which
# scripts/setup.sh fills from requirements.txt (redis, requests,
# psycopg2-binary: everything Sentinel imports). Output is appended to
# logs/sentinel.log.
#
# Usage: ./scripts/install-sentinel.sh            install or update, and start
#        ./scripts/install-sentinel.sh --remove   stop and uninstall
#
# Idempotent: an unchanged unit is left alone and the running service is not
# restarted; a changed one is rewritten and the service restarted onto it.

set -euo pipefail
cd "$(dirname "$0")/.."
REPO_DIR="$PWD"
NAME="algotrading-sentinel"
UNIT_FILE="/etc/systemd/system/$NAME.service"
PY="$REPO_DIR/.venv/bin/python"

die() { printf 'FAILED: %s\n' "$1" >&2; exit 1; }

command -v systemctl >/dev/null 2>&1 \
  || die "no systemd here — this installs the server's service. Anywhere else: cd src/AlgoTrading.PythonEngine && python3 -m sentinel"
[ "$(id -u)" -ne 0 ] || die "run as the desk's user (ubuntu), not root — sudo is used where needed"

case "${1:-}" in
  --remove)
    sudo systemctl disable --now "$NAME" >/dev/null 2>&1 || true
    sudo rm -f "$UNIT_FILE"
    sudo systemctl daemon-reload
    echo "Removed $NAME. Its incidents stay in the database; the console will call its heartbeat stale."
    exit 0 ;;
  "") ;;
  *) die "unknown option: $1 (usage: $0 [--remove])" ;;
esac

[ -x "$PY" ] || die "no virtualenv at $REPO_DIR/.venv — run scripts/setup.sh first"
"$PY" -c 'import psycopg2, redis, requests' 2>/dev/null \
  || die "the .venv lacks what Sentinel imports — $PY -m pip install -r src/AlgoTrading.PythonEngine/requirements.txt"
[ -f .env ] || die ".env not found in $REPO_DIR — Sentinel reads the API, database and Telegram settings from it"
mkdir -p logs

# The unit, written by a function rather than captured in $(...): bash 3.2
# (the Mac's) misreads an apostrophe in a here-document inside $(...).
unit() {
  cat <<UNIT
[Unit]
Description=AlgoTrading Sentinel: watches the desk, opens incidents, changes nothing
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$REPO_DIR/src/AlgoTrading.PythonEngine
Environment=HOME=$HOME
Environment=PYTHONUNBUFFERED=1
# The weekly dependency scan runs dotnet and npm (read-only listings). dotnet
# must not leave an MSBuild worker behind inside this service's memory limit.
Environment=DOTNET_ROOT=/usr/share/dotnet
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=MSBUILDDISABLENODEREUSE=1
Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/share/dotnet:$HOME/.dotnet/tools
ExecStart=$PY -m sentinel
StandardOutput=append:$REPO_DIR/logs/sentinel.log
StandardError=append:$REPO_DIR/logs/sentinel.log
Restart=always
RestartSec=15
# Never in the way of trading on an 8 GB box that also carries the API, the
# database and two dozen runners (15 Sep: one research job froze it). Past
# these limits Sentinel is slowed, or killed and restarted, alone — the kernel
# looks for a victim inside this service, not among the runners. Sentinel
# itself peaks near 50 MB; the weekly scan's dotnet near 170 MB. A scan killed
# at the limit is a failed scan, retried later, not a stopped watchman.
Nice=10
IOSchedulingClass=best-effort
IOSchedulingPriority=7
CPUQuota=25%
MemoryHigh=250M
MemoryMax=300M
OOMPolicy=continue
# "It changes nothing" made a little harder to break: it cannot gain privileges
# (it never uses sudo), and /usr, /boot and /etc are read-only to it.
NoNewPrivileges=true
ProtectSystem=full

[Install]
WantedBy=multi-user.target
UNIT
}

was_running=0
systemctl is-active --quiet "$NAME" 2>/dev/null && was_running=1
changed=0
if ! unit | cmp -s - "$UNIT_FILE" 2>/dev/null; then
  unit | sudo tee "$UNIT_FILE" >/dev/null
  sudo systemctl daemon-reload
  changed=1
fi
sudo systemctl enable --now "$NAME" >/dev/null 2>&1 || die "systemctl enable --now $NAME failed — see: journalctl -u $NAME -n 50"
if [ "$changed" = 1 ] && [ "$was_running" = 1 ]; then
  sudo systemctl restart "$NAME"   # a running service keeps its old unit until restarted
fi

echo "$([ "$changed" = 1 ] && echo Installed || echo Unchanged): $UNIT_FILE"
sleep 3
systemctl status "$NAME" --no-pager --lines=0 2>/dev/null | sed -n '1,4p' || true
echo
echo "Its log:  tail -f logs/sentinel.log     Stop: sudo systemctl stop $NAME     Remove: $0 --remove"
