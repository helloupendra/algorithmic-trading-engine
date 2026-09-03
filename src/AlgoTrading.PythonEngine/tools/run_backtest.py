"""
tools/run_backtest.py

Terminal front-end for the Backtesting module. Creates a run through the API
(POST /api/Backtest/runs - the same path the web dialog uses, so the API
validates coverage, spawns tools/backtest_runner.py and persists the results),
tails GET /api/Backtest/runs/{id} until it finishes and prints the ledger as a
text report.

    python tools/run_backtest.py --strategy GhostTangentCrossings --underlying BANKNIFTY \
        --resolution 5m --from 2026-08-19 --to 2026-09-02 --lots 1 --sl 5000 --target 8000

Nothing is hard-coded here: symbols, lot sizes and expiries come from the
platform, and the strategy is any catalog entry (see tools/list_strategies.py).

Starting a run is admin-only (POST /api/Backtest/runs) and the engine service
account is a Trader, so the wrapper signs in as an admin: pass --user/--password,
or set BACKTEST_USERNAME / BACKTEST_PASSWORD (falling back to ADMIN_USERNAME /
ADMIN_PASSWORD from the repo-root .env).
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import datetime
from typing import Any, Dict, List, Optional

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from core.api_client import PlatformApiClient  # noqa: E402
from core.config import API_BASE_URL, VERIFY_SSL  # noqa: E402
from core.resolutions import to_candle_resolution, to_strategy_resolution  # noqa: E402
from backtest.timeutil import format_ist, parse_utc  # noqa: E402

FINISHED = {"Completed", "Failed", "Stopped"}
LINE = "=" * 100
RULE = "-" * 100


def money(value: Optional[float]) -> str:
    if value is None:
        return "-"
    sign = "-" if value < 0 else ("+" if value > 0 else "")
    return f"{sign}Rs. {abs(value):,.2f}"


def when(value: Optional[str], fmt: str = "%d %b %H:%M") -> str:
    if not value:
        return "-"
    try:
        return format_ist(parse_utc(value), fmt)
    except ValueError:
        return str(value)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Backtest a catalog strategy over stored history.")
    parser.add_argument("--strategy", required=True, help="Catalog strategy name (tools/list_strategies.py)")
    parser.add_argument("--underlying", required=True, help="e.g. BANKNIFTY, NIFTY, SENSEX")
    parser.add_argument("--resolution", default="5m", help="Bar resolution: 1m, 5m, 15m or 1D (default 5m)")
    parser.add_argument("--from", dest="from_date", required=True, help="First IST day, yyyy-MM-dd")
    parser.add_argument("--to", dest="to_date", required=True, help="Last IST day, yyyy-MM-dd (inclusive)")
    parser.add_argument("--lots", type=int, default=None, help="Lots per leg (default: the catalog default)")
    parser.add_argument("--sl", type=float, default=None, help="Stop-loss in rupees on total P&L")
    parser.add_argument("--target", type=float, default=None, help="Target in rupees on total P&L")
    parser.add_argument("--eod", default="15:15", help="EOD square-off time IST, HH:MM; empty for none")
    parser.add_argument("--charges", type=float, default=0.0, help="Flat charges per lot per fill (rupees)")
    parser.add_argument("--capital", type=float, default=None, help="Initial capital (default 1,000,000)")
    parser.add_argument("--params", default=None, help="Strategy parameter overrides as a JSON object")
    parser.add_argument("--poll", type=float, default=2.0, help="Seconds between status polls (default 2)")
    parser.add_argument("--json", action="store_true", help="Print the final run view as JSON instead of the report")
    parser.add_argument("--user", default=None,
                        help="Admin user name to sign in as (default: BACKTEST_USERNAME, then ADMIN_USERNAME from .env)")
    parser.add_argument("--password", default=None,
                        help="Password for --user (default: BACKTEST_PASSWORD, then ADMIN_PASSWORD from .env)")
    return parser.parse_args()


def resolve_credentials(args: argparse.Namespace) -> tuple[Optional[str], Optional[str]]:
    """
    The admin credentials the wrapper signs in with. None/None means "use the
    engine service account", which cannot start runs (it is a Trader) but can
    still tail one.
    """
    username = (args.user or os.getenv("BACKTEST_USERNAME") or os.getenv("ADMIN_USERNAME") or "").strip()
    password = args.password or os.getenv("BACKTEST_PASSWORD") or os.getenv("ADMIN_PASSWORD") or ""
    if username and password:
        return username, password
    return None, None


def resolve_strategy_id(api: PlatformApiClient, name: str) -> int:
    catalog = api.get_strategy_catalog()
    for entry in catalog:
        if str(entry.get("name", "")).lower() == name.lower():
            return int(entry["id"])
    names = ", ".join(sorted(str(e.get("name")) for e in catalog))
    raise SystemExit(f"Strategy '{name}' is not in the catalog. Available: {names}")


def build_request(args: argparse.Namespace, strategy_id: int) -> Dict[str, Any]:
    for label, value in (("--from", args.from_date), ("--to", args.to_date)):
        try:
            datetime.strptime(value, "%Y-%m-%d")
        except ValueError:
            raise SystemExit(f"{label} must be yyyy-MM-dd, got {value!r}")

    payload: Dict[str, Any] = {
        "strategyId": strategy_id,
        "underlying": args.underlying.strip().upper(),
        "resolution": to_candle_resolution(args.resolution),
        "fromDate": args.from_date,
        "toDate": args.to_date,
        "eodSquareOffIst": args.eod.strip(),
        "chargesPerLot": max(0.0, args.charges),
    }
    if args.lots is not None:
        payload["lots"] = max(1, args.lots)
    if args.sl is not None:
        payload["stopLoss"] = args.sl
    if args.target is not None:
        payload["target"] = args.target
    if args.capital is not None:
        payload["initialCapital"] = args.capital
    if args.params:
        try:
            overrides = json.loads(args.params)
        except ValueError as ex:
            raise SystemExit(f"--params must be a JSON object: {ex}")
        if not isinstance(overrides, dict):
            raise SystemExit("--params must be a JSON object")
        payload["parameters"] = overrides
    return payload


def wait_for_run(api: PlatformApiClient, run_id: int, poll: float) -> Dict[str, Any]:
    last_line = ""
    while True:
        view = api.get_backtest_run(run_id)
        status = str(view.get("status") or "")
        if status in FINISHED:
            if last_line:
                print()
            return view
        progress = view.get("progress") or {}
        line = (
            f"  {status:<8} {float(progress.get('percent') or 0):5.1f}%  "
            f"bars {progress.get('barsProcessed') or 0}/{progress.get('totalBars') or 0}  "
            f"trades {progress.get('trades') or 0}  {when(progress.get('currentUtc'))}"
        )
        if line != last_line:
            print(f"\r{line:<100}", end="", flush=True)
            last_line = line
        time.sleep(max(0.5, poll))


def contract_label(position: Dict[str, Any]) -> str:
    contract = position.get("contract") or {}
    return str(contract.get("label") or position.get("symbol") or "")


def print_report(view: Dict[str, Any], logs: List[str]) -> None:
    lot_size = view.get("lotSize") or 1
    print("\n" + LINE)
    print(f"BACKTEST REPORT: {view.get('strategyName')}  (run #{view.get('runId')})")
    print(LINE)
    print(f"Underlying   : {view.get('underlying')} ({view.get('spotSymbol')})  lot size {lot_size} ({view.get('lotSizeSource')})")
    print(f"Range        : {view.get('fromDate')} -> {view.get('toDate')}  @ {to_strategy_resolution(str(view.get('resolution') or '5'))}")
    print(f"Lots         : {view.get('lots')}   SL {money(view.get('stopLoss')) if view.get('stopLoss') else 'none'}   "
          f"target {money(view.get('target')) if view.get('target') else 'none'}   "
          f"EOD {view.get('eodSquareOffIst') or 'none'} IST   charges/lot {view.get('chargesPerLot') or 0}")
    print(f"Status       : {view.get('status')}{'  - ' + str(view.get('stopReason')) if view.get('stopReason') else ''}")
    if view.get("lastError"):
        print(f"Error        : {view.get('lastError')}")

    positions = view.get("positions") or []
    print(RULE)
    if not positions:
        print("No positions were opened during this period.")
    for i, pos in enumerate(positions, 1):
        qty = pos.get("quantity")
        if qty is None:
            qty = (pos.get("lots") or 0) * (pos.get("lotSize") or lot_size)
        exit_price = pos.get("exitPrice")
        exit_text = f"{exit_price:.2f} ({when(pos.get('closedUtc'), '%H:%M')})" if exit_price is not None else "open"
        print(
            f"Trade {i:03d}: {when(pos.get('openedUtc'), '%m-%d %H:%M')} | {str(pos.get('side') or ''):<4} "
            f"{pos.get('lots')}x{pos.get('lotSize') or lot_size} {contract_label(pos)} | "
            f"Entry: {float(pos.get('entryPrice') or 0):.2f} | Exit: {exit_text} | "
            f"PnL: {money(pos.get('pnl'))} | {pos.get('exitReason') or pos.get('status')}"
        )

    pnl = view.get("pnl") or {}
    metrics = view.get("metrics") or {}
    print(RULE)
    print(f"Closed positions    : {metrics.get('closedPositions', len([p for p in positions if p.get('status') == 'Closed']))}")
    if metrics:
        print(f"Win rate            : {float(metrics.get('winRatePercent') or 0):.1f}% "
              f"({metrics.get('winning', 0)}W - {metrics.get('losing', 0)}L)")
        print(f"Gross profit / loss : {money(metrics.get('grossProfit'))} / {money(metrics.get('grossLoss'))}")
        print(f"Profit factor       : {metrics.get('profitFactor')}")
        print(f"Avg win / loss      : {money(metrics.get('averageWin'))} / {money(metrics.get('averageLoss'))}")
        print(f"Largest win / loss  : {money(metrics.get('largestWin'))} / {money(metrics.get('largestLoss'))}")
        print(f"Max drawdown        : {money(metrics.get('maxDrawdownAmount'))} ({float(metrics.get('maxDrawdownPercent') or 0):.2f}%)")
        print(f"Profitable days     : {metrics.get('profitableDays', 0)} of {metrics.get('tradingDays', 0)}")
    print(f"Realized / unreal.  : {money(pnl.get('realized'))} / {money(pnl.get('unrealized'))}")
    print(f"Charges             : {money(pnl.get('charges'))}")
    print(f"Net PnL             : {money(pnl.get('total'))} ({float(pnl.get('returnPercent') or 0):.2f}%)")

    daily = view.get("daily") or []
    if daily:
        print(RULE)
        print("Daily P&L (IST):")
        for row in daily:
            print(f"  {row.get('date')}  {money(row.get('pnl')):>18}  trades {row.get('trades', 0)}")

    notes = view.get("dataNotes") or []
    if notes:
        print(RULE)
        print("Data notes:")
        for note in notes:
            print(f"  - {note}")

    if view.get("status") == "Failed" and logs:
        print(RULE)
        print("Last runner output:")
        for line in logs[-20:]:
            print(f"  {line}")
    print(LINE + "\n")


def main() -> int:
    args = parse_args()
    username, password = resolve_credentials(args)
    if username is None:
        print(
            "No admin credentials given (--user/--password, BACKTEST_USERNAME/BACKTEST_PASSWORD or "
            "ADMIN_USERNAME/ADMIN_PASSWORD in .env); signing in as the engine service account, "
            "which is not allowed to start backtests.",
            file=sys.stderr,
        )
    else:
        print(f"Signing in as {username}")
    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL, username=username, password=password)

    strategy_id = resolve_strategy_id(api, args.strategy)
    payload = build_request(args, strategy_id)
    print(f"Starting backtest: {json.dumps(payload)}")
    try:
        started = api.start_backtest(payload)
    except Exception as ex:
        print(f"Could not start the backtest: {ex}", file=sys.stderr)
        if str(ex).startswith("403"):
            print("POST /api/Backtest/runs is admin-only: pass --user/--password for an admin account "
                  "(or set ADMIN_USERNAME/ADMIN_PASSWORD in .env).", file=sys.stderr)
        return 1
    run_id = int(started["runId"])
    print(f"Run #{run_id}: {started.get('message') or 'started'}")

    try:
        view = wait_for_run(api, run_id, args.poll)
    except KeyboardInterrupt:
        print(f"\nStill running as run #{run_id}; stop it from the Backtesting page or POST /api/Backtest/runs/{run_id}/stop")
        return 130

    if args.json:
        print(json.dumps(view, indent=2, default=str))
        return 0 if view.get("status") == "Completed" else 1

    logs: List[str] = []
    if view.get("status") == "Failed":
        try:
            logs = api.get_backtest_logs(run_id, take=50)
        except Exception:
            logs = []
    print_report(view, logs)
    return 0 if view.get("status") in ("Completed", "Stopped") else 1


if __name__ == "__main__":
    sys.exit(main())
