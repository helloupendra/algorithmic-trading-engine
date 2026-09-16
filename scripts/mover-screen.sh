#!/usr/bin/env bash
# Mover screen for the whole stock universe, on this workstation.
#
#   scripts/mover-screen.sh            prepare (skips what is already done), then
#                                      run until 15:30 and open the page
#   scripts/mover-screen.sh prepare    only build the baseline (after ~19:00, when
#                                      the exchange has published the bhavcopy)
#
# Dhan credentials come from ~/.config/openfno/dhan.env. When Dhan refuses the
# token, the command in $DHAN_TOKEN_COMMAND is run to write a fresh one; if it
# is unset and private/scripts/dhan-token-from-server.sh exists, that is used.
# Output: ~/OpenFNO-data/equities/screen/live.html and <date>.jsonl.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PY="$REPO/.venv/bin/python"
TOOL="$REPO/src/AlgoTrading.PythonEngine/tools/mover_screen.py"
TOKEN_CMD="${DHAN_TOKEN_COMMAND:-}"
if [ -z "$TOKEN_CMD" ] && [ -x "$REPO/private/scripts/dhan-token-from-server.sh" ]; then
  TOKEN_CMD="$REPO/private/scripts/dhan-token-from-server.sh"
fi

"$PY" "$TOOL" prepare
[ "${1:-}" = "prepare" ] && exit 0
if [ -n "$TOKEN_CMD" ]; then
  exec "$PY" "$TOOL" run --open --token-command "$TOKEN_CMD"
fi
exec "$PY" "$TOOL" run --open
