"""
tools/backtest_runner.py

Runs one OfflineReplay SimulationRun to completion. Launched by the API as

    backtest_runner.py --run-id <id>

with cwd / PYTHONPATH = the engine directory and PYTHONUNBUFFERED=1. The run
row (strategy name, spot symbol, resolution, UTC range, parametersJson) is
loaded from GET /api/Simulator/runs/{id}; the strategy factory is resolved
through strategies/registry.py by the run's strategyName; the replay itself is
backtest/engine.run_backtest, which posts every fill, mark, equity point and
the completion summary back to the Simulator.

Exit codes: 0 completed (or stopped with SIGTERM - the API marks the run
Stopped), 1 failed (the cause is printed on stderr and posted via /complete).
"""

from __future__ import annotations

import argparse
import os
import signal
import sys
from typing import Any

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core.api_client import PlatformApiClient  # noqa: E402
from core.config import API_BASE_URL, VERIFY_SSL  # noqa: E402


def log(text: str) -> None:
    print(text, flush=True)


def install_signal_handlers() -> None:
    """SIGTERM/SIGINT end the replay with exit code 0; the API marks the run Stopped."""
    def _handler(signum: int, _frame: Any) -> None:
        try:
            name = signal.Signals(signum).name
        except ValueError:
            name = str(signum)
        log(f"[RUNNER] stopping: {name}")
        raise SystemExit(0)

    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            signal.signal(sig, _handler)
        except (ValueError, OSError):
            pass


def fail(api: PlatformApiClient, run_id: int, message: str) -> int:
    log(f"[ERROR] {message}")
    print(message, file=sys.stderr, flush=True)
    try:
        api.complete_run(run_id, "Failed", {"dataNotes": [message], "skippedEntries": [], "trades": 0}, message)
    except Exception as ex:
        print(f"could not mark run {run_id} failed: {ex}", file=sys.stderr, flush=True)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description="Replay one OfflineReplay simulation run.")
    parser.add_argument("--run-id", type=int, required=True, help="SimulationRun id (Mode OfflineReplay)")
    args = parser.parse_args()

    install_signal_handlers()
    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)

    try:
        run_row = api.get_simulation_run(args.run_id)
    except Exception as ex:
        message = f"Could not load run {args.run_id} from {API_BASE_URL}: {ex}"
        print(message, file=sys.stderr, flush=True)
        return 1

    strategy_name = str(run_row.get("strategyName") or "").strip()
    from strategies.registry import load_strategy_factories

    factories = load_strategy_factories()
    factory = factories.get(strategy_name)
    if factory is None:
        return fail(api, args.run_id,
                    f"Strategy '{strategy_name}' is not in the catalog. Available: {', '.join(sorted(factories))}")

    log(f"[RUNNER] run {args.run_id}: {strategy_name} ({run_row.get('mode')}) on {run_row.get('symbol')} @ {run_row.get('resolution')}")

    def on_progress(progress: dict) -> None:
        api.post_progress(args.run_id, progress)

    from backtest.engine import run_backtest

    outcome = run_backtest(api, run_row, factory, on_progress, log=log)
    if outcome.status != "Completed":
        print(outcome.error or "Backtest failed", file=sys.stderr, flush=True)
        return 1
    log(f"[RUNNER] run {args.run_id} completed: {outcome.summary.get('trades', 0)} trades"
        f"{' - ' + outcome.stop_reason if outcome.stop_reason else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
