#!/usr/bin/env bash
# Puts the console on a real domain, for free, from this machine.
#
# A Cloudflare *named* tunnel: cloudflared runs here as a launchd service and
# keeps an outbound connection to Cloudflare, which routes https://<host> to
# the API on localhost:5025. No port is opened on the home router, TLS is
# Cloudflare's, and the URL is stable — unlike the quick tunnel go-live.sh
# used, which changed on every start.
#
# Why this and not a cloud VM: every "free" host that can run long-lived
# daemons wants a card, and the ones that do not put the server to sleep after
# fifteen idle minutes, which kills the broker websocket. This Mac already
# stays awake, wakes itself before the open and runs the whole stack; the only
# thing it lacked was a way in.
#
# Usage:
#   ./scripts/install-tunnel.sh <tunnel-token> openfno.com
#   ./scripts/install-tunnel.sh --remove
#
# The token comes from Cloudflare → Zero Trust → Networks → Tunnels → Create.
# In that same screen add a public hostname: <host> → HTTP → localhost:5025.

set -euo pipefail
cd "$(dirname "$0")/.."
REPO_ROOT="$PWD"
LABEL="com.algotrading.tunnel"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

if [ "${1:-}" = "--remove" ]; then
  launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
  rm -f "$PLIST"
  echo "Removed $LABEL. The domain will stop resolving to this machine."
  exit 0
fi

TOKEN="${1:-}"; HOST="${2:-}"
[ -n "$TOKEN" ] && [ -n "$HOST" ] || { echo "usage: $0 <tunnel-token> <public-hostname>" >&2; exit 1; }

CF="$(command -v cloudflared || true)"
if [ -z "$CF" ]; then
  echo "==> installing cloudflared"
  brew install cloudflared
  CF="$(command -v cloudflared)"
fi

mkdir -p "$HOME/Library/LaunchAgents" logs

# The token is written into the plist rather than a shell profile: launchd
# reads it directly, and it never sits in a tracked file (the plist lives in
# ~/Library). KeepAlive restarts cloudflared if it dies; RunAtLoad brings it up
# at login, before the market-open job needs it.
{
  printf '<?xml version="1.0" encoding="UTF-8"?>\n'
  printf '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n'
  printf '<plist version="1.0">\n<dict>\n'
  printf '  <key>Label</key><string>%s</string>\n' "$LABEL"
  printf '  <key>ProgramArguments</key>\n  <array>\n'
  printf '    <string>%s</string>\n' "$CF"
  printf '    <string>tunnel</string>\n    <string>run</string>\n'
  printf '    <string>--token</string>\n    <string>%s</string>\n' "$TOKEN"
  printf '  </array>\n'
  printf '  <key>RunAtLoad</key><true/>\n'
  printf '  <key>KeepAlive</key><true/>\n'
  printf '  <key>StandardOutPath</key><string>%s/logs/tunnel.log</string>\n' "$REPO_ROOT"
  printf '  <key>StandardErrorPath</key><string>%s/logs/tunnel.log</string>\n' "$REPO_ROOT"
  printf '</dict>\n</plist>\n'
} > "$PLIST"
chmod 600 "$PLIST"

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$(id -u)" "$PLIST"

# The browser will be on the new origin; the API must accept it. Appended to
# .env (git-ignored) and regenerated into appsettings.Local.json the way
# setup.sh does, so the origin survives the next regeneration.
ORIGIN="https://$HOST"
if [ -f .env ]; then
  if grep -q '^CORS_ALLOWED_ORIGINS=' .env; then
    grep -q "$ORIGIN" .env || sed -i '' "s|^CORS_ALLOWED_ORIGINS=\(.*\)|CORS_ALLOWED_ORIGINS=\1,$ORIGIN|" .env
  else
    printf 'CORS_ALLOWED_ORIGINS=http://localhost:5173,%s\n' "$ORIGIN" >> .env
  fi
  if [ -f scripts/_gen_local_settings.py ]; then
    .venv/bin/python scripts/_gen_local_settings.py >/dev/null 2>&1 || python3 scripts/_gen_local_settings.py >/dev/null 2>&1 || true
  fi
fi

sleep 3
echo
echo "Tunnel service installed: $LABEL"
echo "  log:     $REPO_ROOT/logs/tunnel.log"
echo "  status:  launchctl list | grep $LABEL"
echo "  remove:  $0 --remove"
echo
echo "Still to do, once each:"
echo "  1. Cloudflare → Zero Trust → Tunnels → this tunnel → Public hostname:"
echo "       $HOST  →  HTTP  →  localhost:5025"
echo "  2. FYERS app (myapi.fyers.in) → Redirect URI → $ORIGIN/api/auth/callback"
echo "     then Console → Connectors → Broker page → same redirect URI → save."
echo "  3. Restart the API so the new CORS origin loads (market-open.sh does this daily)."
echo
tail -5 logs/tunnel.log 2>/dev/null || true
