#!/usr/bin/env bash
# The local dev stack's walls (scripts/dev/local-live.sh): the committed
# override file keeps every broker credential empty and every broker address on
# the closed port, the socket check tells this stack's own connections from
# anything else, and a process that breaks the rule is stopped.
# Run: bash scripts/tests/local-live.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected $2, got $3)"; FAILS=$((FAILS + 1)); fi
}

SCRIPT=scripts/dev/local-live.sh
TMP="$(mktemp -d)"
cleanup() {
  [ -n "${FAKE_API:-}" ] && kill "$FAKE_API" 2>/dev/null
  [ -n "${PEER:-}" ] && kill "$PEER" 2>/dev/null
  rm -rf "$TMP"
}
trap cleanup EXIT

validate() {  # file -> exit code of the override validation
  LOCAL_LIVE_OVERRIDES="$1" bash "$SCRIPT" _validate >/dev/null 2>&1
  echo $?
}

tampered() {  # sed expression -> path of a changed copy of local-live.env
  local out="$TMP/overrides.$RANDOM"
  sed "$1" scripts/dev/local-live.env > "$out"
  echo "$out"
}

echo "Override file"
check "the committed local-live.env is accepted" 0 "$(validate scripts/dev/local-live.env)"
check "a Dhan PIN with a value is refused" 1 "$(validate "$(tampered 's/^DHAN_PIN=$/DHAN_PIN=123456/')")"
check "a Dhan TOTP secret in the section key is refused" 1 "$(validate "$(tampered 's/^Dhan__TotpSecret=$/Dhan__TotpSecret=ABCD/')")"
check "a FYERS app id with a value is refused" 1 "$(validate "$(tampered 's/^Fyers__ClientId=$/Fyers__ClientId=XY1234-100/')")"
check "a real broker address is refused" 1 "$(validate "$(tampered 's#^Dhan__ApiBaseUrl=.*#Dhan__ApiBaseUrl=https://api.dhan.co/v2#')")"
check "a deleted credential line is refused" 1 "$(validate "$(tampered '/^Dhan__Pin=$/d')")"
check "the automatic Dhan sign-in switched on is refused" 1 "$(validate "$(tampered 's/^Dhan__AutoSignIn__Enabled=false$/Dhan__AutoSignIn__Enabled=true/')")"
check "a proxy that goes somewhere is refused" 1 "$(validate "$(tampered 's#^HTTPS_PROXY=.*#HTTPS_PROXY=http://10.0.0.1:3128#')")"

classify() {  # lsof -F text on stdin -> exit code of the classifier
  DEV_API_PORT=5125 DEV_DB_PORT=5544 DEV_REDIS_PORT=6390 LOCAL_LIVE_DIR="$TMP/none" \
    bash "$SCRIPT" _classify >/dev/null 2>&1
  echo $?
}

listen='p100
f10
PTCP
n127.0.0.1:5125
TST=LISTEN'

echo "Socket check"
check "listening on the API port, talking to Postgres and Redis, one client: clean" 0 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n127.0.0.1:50001->127.0.0.1:5544' 'TST=ESTABLISHED' \
  'f12' 'PTCP' 'n127.0.0.1:50002->127.0.0.1:6390' 'TST=ESTABLISHED' \
  'f13' 'PTCP' 'n127.0.0.1:5125->127.0.0.1:50003' 'TST=ESTABLISHED' | classify)"
check "IPv6 loopback is loopback" 0 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n[::1]:50001->[::1]:5544' 'TST=ESTABLISHED' | classify)"
check "a connection to the internet is a violation" 1 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n192.168.1.5:50001->13.235.10.20:443' 'TST=ESTABLISHED' | classify)"
check "a half-open connection out is a violation" 1 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n192.168.1.5:50001->13.235.10.20:443' 'TST=SYN_SENT' | classify)"
check "a loopback port that is not this stack's (an SSH tunnel) is a violation" 1 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n127.0.0.1:50001->127.0.0.1:52215' 'TST=ESTABLISHED' | classify)"
check "the Mac's own database on 5433 is a violation" 1 "$(printf '%s\n' "$listen" 'f11' 'PTCP' 'n127.0.0.1:50001->127.0.0.1:5433' 'TST=ESTABLISHED' | classify)"
check "listening on every interface is a violation" 1 "$(printf '%s\n' 'p100' 'f10' 'PTCP' 'n*:5125' 'TST=LISTEN' | classify)"
check "any UDP is a violation" 1 "$(printf '%s\n' "$listen" 'f11' 'PUDP' 'n192.168.1.5:5353->8.8.8.8:53' | classify)"
check "no listening socket in the output: cannot tell" 2 "$(printf '%s\n' 'p100' 'f11' 'PTCP' 'n127.0.0.1:50001->127.0.0.1:5544' 'TST=ESTABLISHED' | classify)"
check "empty output: cannot tell" 2 "$(printf '' | classify)"

echo "Stop path, against a fake API"
if ! command -v lsof >/dev/null || ! command -v pgrep >/dev/null || ! command -v python3 >/dev/null; then
  echo "  skip (needs lsof, pgrep and python3)"
else
  free_port() { python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1])'; }
  API_PORT="$(free_port)"; PEER_PORT="$(free_port)"; OTHER_PORT="$(free_port)"
  mkdir -p "$TMP/dev/run" "$TMP/dev/build/api" "$TMP/dev/content/api"
  MARKER="$TMP/dev/build/api/AlgoTrading.Api.dll"

  # Both helpers are started in a subshell that exits at once, so they belong
  # to init rather than to this script: a stopped one is reaped straight away
  # instead of lingering as this shell's zombie (which kill -0 still sees).

  # Something on loopback for the fake API to talk to.
  ( python3 -c '
import socket, sys, time
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(5)
conns = []
while True:
    c, _ = s.accept(); conns.append(c)
' "$PEER_PORT" </dev/null >/dev/null 2>&1 & echo $! > "$TMP/peer.pid" )
  PEER="$(cat "$TMP/peer.pid")"

  # A process whose command line carries the API marker, listening on the API
  # port and holding one connection to the peer.
  start_fake_api() {
    ( python3 -c '
import socket, sys, time
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[2]))); s.listen(5)
for _ in range(50):
    try:
        c = socket.create_connection(("127.0.0.1", int(sys.argv[3]))); break
    except OSError:
        time.sleep(0.1)
time.sleep(120)
' "$MARKER" "$API_PORT" "$PEER_PORT" </dev/null >/dev/null 2>&1 & echo $! > "$TMP/dev/run/api.pid" )
    FAKE_API="$(cat "$TMP/dev/run/api.pid")"
    sleep 1
  }

  run_check() {  # db port -> exit code of `local-live.sh check`
    LOCAL_LIVE_DIR="$TMP/dev" DEV_API_PORT="$API_PORT" DEV_DB_PORT="$1" DEV_REDIS_PORT="$OTHER_PORT" \
      bash "$SCRIPT" check >"$TMP/check.out" 2>&1
    echo $?
  }

  start_fake_api
  check "talking only to its own database port: clean" 0 "$(run_check "$PEER_PORT")"
  check "  and it is left running" 0 "$(kill -0 "$FAKE_API" 2>/dev/null; echo $?)"

  check "talking to a port that is not this stack's: violation" 1 "$(run_check "$OTHER_PORT")"
  sleep 1
  check "  and it is stopped" 1 "$(kill -0 "$FAKE_API" 2>/dev/null; echo $?)"

  start_fake_api
  touch "$TMP/dev/content/api/appsettings.Local.json"
  check "an appsettings.Local.json in the content root: violation" 1 "$(run_check "$PEER_PORT")"
  sleep 1
  check "  and it is stopped" 1 "$(kill -0 "$FAKE_API" 2>/dev/null; echo $?)"
fi

echo
if [ "$FAILS" -eq 0 ]; then echo "All passed."; else echo "$FAILS failed."; exit 1; fi
