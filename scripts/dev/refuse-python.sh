#!/usr/bin/env bash
# Stands in for the Python interpreter inside the local dev stack
# (scripts/dev/local-live.sh sets StrategyRunner:PythonExecutable to this file).
#
# Every Python process the API can start is one that could reach a broker: the
# Dhan and FYERS feeds, the chain poller, the strategy runners, the alerter,
# the Telegram notifier, the forecasts. And the engine loads the repo-root .env
# on import, which on the owner's Mac holds the real keys. So in the dev stack
# none of them runs: the API "launches" this instead, it writes one line saying
# what was asked for, and exits. The strategy catalog falls back to its own
# scan of the strategy files, which needs no Python.
#
# Exit code 3, so a caller that checks it sees a failure and says so.

log="${DEV_LIVE_REFUSED_LOG:-/dev/null}"
printf '%s refused: python %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >>"$log" 2>/dev/null
echo "Python is switched off in the local dev stack (scripts/dev/local-live.sh): nothing that could reach a broker runs here." >&2
exit 3
