#!/usr/bin/env bash
# Which Telegram chat scripts/lib/desk-common.sh's notify and notify_trades use.
# Run: bash scripts/tests/notify-channels.test.sh
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 1

FAILS=0
check() {  # description, expected, actual
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1 (expected '$2', got '$3')"; FAILS=$((FAILS + 1)); fi
}

FUNCS="$(mktemp)"
trap 'rm -f "$FUNCS"' EXIT
# notify, notify_trades and _notify_to, which run from "notify() {" to the
# first line that is a lone "}".
sed -n '/^notify() {/,/^}/p' scripts/lib/desk-common.sh >"$FUNCS"

chat_of() {  # function (notify|notify_trades), system chat -> the chat_id curl was given
  FUNCS="$FUNCS" FN="$1" TELEGRAM_SYSTEM_CHAT_ID="$2" bash -c '
    . "$FUNCS"
    IS_MAC=false REPO_ROOT=/nonexistent TELEGRAM_BOT_TOKEN=123:abc TELEGRAM_CHAT_ID=-100111
    curl() { for a in "$@"; do case "$a" in chat_id=*) echo "${a#chat_id=}";; esac; done; }
    "$FN" "AlgoTrading" "hello"'
}

echo "notify and notify_trades"
check "a system notice goes to the system chat"        "-100222" "$(chat_of notify -100222)"
check "the morning tally goes to the trades chat"      "-100111" "$(chat_of notify_trades -100222)"
check "no system chat: the one chat still gets it"     "-100111" "$(chat_of notify '')"

[ "$FAILS" -eq 0 ] && echo "all passed" || { echo "$FAILS failed"; exit 1; }
