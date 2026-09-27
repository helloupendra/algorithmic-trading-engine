#!/usr/bin/env bash
# The Dhan section of the generated appsettings.Local.json (scripts/_gen_local_settings.py):
# every deploy rewrites that file, so what it leaves out, every restarted API lacks.
# Run: bash scripts/tests/gen-local-settings.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.."

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected $2, got $3)"; FAILS=$((FAILS + 1)); fi
}

dhan() {  # key=value ... -> the Dhan section as sorted JSON
  python3 - "$@" <<'PY'
import json, sys
sys.path.insert(0, "scripts")
from _gen_local_settings import build_api_settings
env = dict(arg.split("=", 1) for arg in sys.argv[1:])
print(json.dumps(build_api_settings(env).get("Dhan"), sort_keys=True))
PY
}

echo "Dhan section"
check "client id, key and secret are written" \
  '{"ApiKey": "k", "ApiSecret": "s", "ClientId": "1100"}' \
  "$(dhan DHAN_CLIENT_ID=1100 DHAN_API_KEY=k DHAN_API_SECRET=s)"
check "a pasted access token is never written" \
  '{"ClientId": "1100"}' \
  "$(dhan DHAN_CLIENT_ID=1100 DHAN_ACCESS_TOKEN=eyJdead)"
check "unset keys are left out, not written empty (they would hide the env fallback)" \
  '{}' \
  "$(dhan DHAN_CLIENT_ID= DHAN_PIN=)"
check "PIN and TOTP secret reach the automatic sign-in" \
  '{"ClientId": "1100", "Pin": "123456", "TotpSecret": "ABCD"}' \
  "$(dhan DHAN_CLIENT_ID=1100 DHAN_PIN=123456 DHAN_TOTP_SECRET=ABCD)"
check "the chain poller switch is a boolean" \
  '{"ChainPoller": {"Enabled": true}}' \
  "$(dhan DHAN_CHAIN_POLLER_ENABLED=True)"
check "anything else for the switch is ignored" \
  '{}' \
  "$(dhan DHAN_CHAIN_POLLER_ENABLED=yes)"

echo "Telegram section"
telegram() {  # key=value ... -> the Telegram section's chat ids
  python3 - "$@" <<'PY'
import json, sys
sys.path.insert(0, "scripts")
from _gen_local_settings import build_api_settings
env = dict(arg.split("=", 1) for arg in sys.argv[1:])
t = build_api_settings(env)["Telegram"]
print(t["ChatId"], t["SystemChatId"] or "-")
PY
}
check "the system chat is carried to the API"  "-100111 -100222" "$(telegram TELEGRAM_CHAT_ID=-100111 TELEGRAM_SYSTEM_CHAT_ID=-100222)"
check "and is empty when .env has none"        "-100111 -"       "$(telegram TELEGRAM_CHAT_ID=-100111)"

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
