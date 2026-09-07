#!/usr/bin/env bash
# One screen: is the desk running, and is everything it looks after alive?
# Safe to run any time, from anywhere on this machine. Reads logs/desk.status
# (written by scripts/desk.sh every 30 s) plus live checks.
#
# Usage: ./scripts/status.sh          # once
#        watch -n 10 ./scripts/status.sh   # keep it on screen

cd "$(dirname "$0")/.."
API="${API_BASE_URL:-http://localhost:5025}"
PUBLIC="${PUBLIC_URL:-https://console.snehatra.com}"
PIDFILE="$HOME/Library/Application Support/algotrading/desk.pid"
G='\033[32m'; R='\033[31m'; Y='\033[33m'; D='\033[2m'; N='\033[0m'
ok()   { printf "  ${G}●${N} %-28s %s\n" "$1" "$2"; }
bad()  { printf "  ${R}●${N} %-28s %s\n" "$1" "$2"; }
meh()  { printf "  ${Y}●${N} %-28s %s\n" "$1" "$2"; }

printf "\n  ${D}%s${N}\n\n" "$(date '+%A %d %b %Y, %H:%M:%S IST')"

# --- the desk itself ---------------------------------------------------------
if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
  age=""
  if [ -f logs/desk.status ]; then
    upd="$(grep '^updated=' logs/desk.status | cut -d= -f2-)"
    secs=$(( $(date +%s) - $(date -j -f '%Y-%m-%d %H:%M:%S' "$upd" +%s 2>/dev/null || echo 0) ))
    age="(last loop ${secs}s ago)"
    [ "$secs" -gt 120 ] && age="${age} ${Y}— stale, the loop may be stuck${N}"
  fi
  host="in the background"; [ "$(ps -o tty= -p "$(cat "$PIDFILE")" 2>/dev/null | tr -d ' ')" != "??" ] && host="in a Terminal window"
  ok "desk supervisor" "running $host, pid $(cat "$PIDFILE") $age"
else
  bad "desk supervisor" "NOT running — start: ./scripts/desk.sh --headless  (or wait ≤10 min for the keepalive)"
fi

# --- services -----------------------------------------------------------------
launchctl list 2>/dev/null | grep -q com.algotrading.tunnel \
  && ok  "cloudflare tunnel service" "registered$(tail -50 logs/tunnel.log 2>/dev/null | grep -q 'Registered tunnel' && echo ', connected')" \
  || bad "cloudflare tunnel service" "not installed (scripts/install-tunnel.sh)"

code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "$API/health" 2>/dev/null)"
[ "$code" = "200" ] && ok "API (local :5025)" "healthy" || bad "API (local :5025)" "health returned '${code:-no response}'"

code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 12 "$PUBLIC/" 2>/dev/null)"
[ "$code" = "200" ] && ok "public $PUBLIC" "200" || bad "public $PUBLIC" "'${code:-no response}' — tunnel or API"

running="$(docker compose ps --status running --format '{{.Service}}' 2>/dev/null | tr '\n' ' ')"
case "$running" in *timescaledb*redis*|*redis*timescaledb*) ok "docker infra" "$running";; *) bad "docker infra" "${running:-nothing running} — need timescaledb + redis";; esac

# --- daemons and runs ----------------------------------------------------------
pgrep -f fyers_streamer >/dev/null       && ok  "tick ingestor"  "running" || meh "tick ingestor"  "not running (normal outside market hours)"
pgrep -f option_chain_poller >/dev/null  && ok  "chain poller"   "running" || meh "chain poller"   "not running (normal outside market hours)"
n="$(pgrep -f execution_runner | wc -l | tr -d ' ')"
[ "$n" -gt 0 ] && ok "strategy runners" "$n live" || meh "strategy runners" "none live"

# --- broker ----------------------------------------------------------------------
if [ -f .env ]; then
  set -a; . ./.env 2>/dev/null; set +a
  tok="$(curl -fsS --max-time 6 -X POST "$API/api/UserAuth/login" -H 'Content-Type: application/json' \
        -d "{\"userNameOrEmail\":\"${ADMIN_USERNAME:-}\",\"password\":\"${ADMIN_PASSWORD:-}\"}" 2>/dev/null | grep -o '"accessToken":"[^"]*' | grep -o '[^"]*$')"
  if [ -n "$tok" ]; then
    sess="$(curl -fsS --max-time 6 "$API/api/auth/session" -H "Authorization: Bearer $tok" 2>/dev/null)"
    if printf '%s' "$sess" | grep -q '"isAuthenticated":true'; then
      exp="$(printf '%s' "$sess" | grep -o '"accessToken":"[^"]*' | cut -d'"' -f4 | cut -d. -f2 | { read p; p="$p$(printf '%*s' $(( (4 - ${#p} % 4) % 4 )) '' | tr ' ' '=')"; echo "$p" | base64 -D 2>/dev/null; } | grep -o '"exp":[0-9]*' | cut -d: -f2)"
      when="$( [ -n "$exp" ] && date -r "$exp" '+%a %H:%M' )"
      if [ -n "$exp" ] && [ "$exp" -gt "$(date +%s)" ]; then ok "FYERS session" "valid until $when"; else bad "FYERS session" "EXPIRED at $when — sign in at $PUBLIC/login"; fi
    else
      bad "FYERS session" "not connected — sign in at $PUBLIC/login → Connectors"
    fi
  else
    meh "FYERS session" "could not sign in to the API to check"
  fi
fi

# --- deploy + market open ----------------------------------------------------------
if [ -f logs/desk.status ]; then
  printf "\n"
  printf "  ${D}commit           %s${N}\n" "$(grep '^commit=' logs/desk.status | cut -d= -f2-)"
  printf "  ${D}last deploy      %s${N}\n" "$(grep '^last_deploy=' logs/desk.status | cut -d= -f2-)"
  printf "  ${D}market-open ran  %s${N}\n" "$(grep '^market_open_ran_on=' logs/desk.status | cut -d= -f2-)"
fi
# --- what the desk did last -------------------------------------------------------
if [ -f logs/desk.log ]; then
  printf "\n  ${D}desk.log, last lines (tail -f logs/desk.log to follow):${N}\n"
  grep -vE '^\s|^ Container|Warning\(s\)|Error\(s\)|Time Elapsed|^$' logs/desk.log | tail -8 | sed 's/^/    /'
fi
printf "\n  ${D}logs: logs/desk.log · logs/market-open-$(date +%F).log · logs/tunnel.log · logs/api.log${N}\n\n"
