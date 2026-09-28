#!/usr/bin/env python3
"""
Paper activity for the local dev stack (scripts/dev/local-live.sh), booked
through its API, so Positions, the Desk and a live run's page have legs whose
P&L moves with scripts/dev/replay-ticks.py.

WHAT IT BOOKS
-------------
--setup (local-live.sh up runs it): the trader account and its module grants,
    the synthetic instrument master (three indices, NIFTY options at five
    strikes for the next Tuesday expiry, the near CRUDEOIL future) imported
    through POST /api/Instruments/import-local, and those symbols on the
    watchlist.

default: whichever of the two halves below has no open legs (after a restart
    of the dev API that is the strategy run); --force books both again.
    - the admin's manual book: BUY 2 lots of the at-the-money NIFTY call, SELL
      1 lot of the put 100 below, BUY 1 lot of the crude future;
    - the trader's manual book: SELL 1 lot of the call 100 above;
    - a "LivePaper" strategy run in the trader's account holding a short
      straddle (SELL the at-the-money call and put), booked with the same
      OPEN_GROUP signal a runner posts.
    Manual orders go through POST /api/ManualOrders and are carried forward,
    so the manual book's square-off at the exchange close leaves them alone.

--fill    one more manual order (a random NIFTY option, a random side, 1 lot).
--close   squares off the newest open leg, or --position ID, through
          POST /api/Strategy/runs/{runId}/positions/close.

THE STRATEGY RUN, AND WHY ONE SQL STATEMENT
-------------------------------------------
A live run is normally created by POST /api/Strategy/{id}/start, which also
launches its Python runner. In the dev stack Python is refused
(refuse-python.sh), so that path ends with a failed run. Instead the run is
created with POST /api/Simulator/runs (Mode LivePaper, status Pending, no
process), switched to Running with one UPDATE in the throwaway database
(docker exec into openfno_dev_db, checked by its dev-live label), and given
its legs with POST /api/Simulator/signals. It has no runner and no pid, so
nothing can ever be stopped or killed on its account. It is closed as
orphaned if the dev API restarts, like any run whose runner is gone.

USAGE
-----
    python3 scripts/dev/seed-positions.py --setup
    python3 scripts/dev/seed-positions.py [--force]
    python3 scripts/dev/seed-positions.py --fill [--user trader]
    python3 scripts/dev/seed-positions.py --close [--position 12] [--user trader]
"""

from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
import urllib.parse
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import _devlive as dev

TRADER_MODULES = ["strategies", "backtesting", "market-data"]
STRATEGY_FALLBACK_NAME = "DevReplayDemo"


# --------------------------------------------------------------------- setup --

def ensure_trader(admin: dev.DevApi, state: dict[str, str]) -> None:
    name = state["TRADER_USER"]
    users = admin.get("/api/Users") or []
    user = next((u for u in users if str(u.get("userName", "")).lower() == name.lower()), None)
    if user is None:
        created = admin.post("/api/UserAuth/register", {
            "userName": name, "email": f"{name}@localhost", "password": state["TRADER_PASSWORD"]})
        user_id = created["user"]["id"]
        print(f"  created trader {name} (id {user_id})")
    else:
        user_id = user["id"]
        print(f"  trader {name} exists (id {user_id})")
    admin.put(f"/api/Users/{user_id}/grants", {"moduleKeys": TRADER_MODULES})
    print(f"  granted {', '.join(TRADER_MODULES)}")


def setup(state: dict[str, str], spots: dict[str, float]) -> None:
    admin = dev.DevApi.as_role(state, "admin")
    admin.login()
    print("Setting up the dev stack's accounts, instruments and watchlist")
    ensure_trader(admin, state)

    # The same contracts on every `up` while they are live, so positions
    # booked yesterday keep their quotes; a new set once they expire.
    universe = dev.load_universe() if dev.UNIVERSE_FILE.is_file() and not spots else None
    if universe is None or any(x.expiry_date and x.expiry_date <= dev.today_ist() for x in universe):
        universe = dev.build_universe(dev.today_ist(), spots)
    for path in dev.write_masters(universe, Path(state["INSTRUMENTS_DIR"])):
        result = admin.post("/api/Instruments/import-local?filePath=" + urllib.parse.quote(str(path)))
        print(f"  imported {path.name}: {result.get('inserted', 0)} new, {result.get('updated', 0)} updated")
    dev.save_universe(universe)

    for x in universe:
        admin.post("/api/LiveData/watchlist",
                   {"symbol": x.symbol, "dataType": "symbolUpdate", "isActive": True, "priority": 10})
    expiry = next(x.expiry for x in universe if x.kind == "option")
    print(f"  watchlist: {len(universe)} symbols; the NIFTY options expire {expiry}")


# --------------------------------------------------------------------- quotes --

def freshen_quotes(state: dict[str, str], universe: list[dev.Instrument]) -> None:
    """One tick per symbol, stamped now, so every fill below has a live price.

    While the market is open a paper fill refuses a quote older than 60 s
    (PaperFills:MaxQuoteAgeSeconds). With the replay running this changes
    nothing; without it, it is what makes the seed work on its own.
    """
    service = dev.DevApi.as_role(state, "service")
    market = dev.Market(universe)
    latest = {q["symbol"]: q.get("lastTradedPrice") for q in service.get("/api/LiveData/latest/all") or []}
    market.start_from({k: v for k, v in latest.items() if v})
    now = datetime.now(timezone.utc)
    service.post("/api/LiveData/ticks/upsert-batch", [market.tick(x, now) for x in universe])


# ------------------------------------------------------------------- booking --

def manual_order(api: dev.DevApi, symbol: str, side: str, lots: int) -> dict[str, Any]:
    answer = api.post("/api/ManualOrders", {
        "symbol": symbol, "side": side, "quantity": lots, "carryForward": True})
    print(f"  {api.username}: {answer.get('message')}")
    return answer


def strategy_name(admin: dev.DevApi) -> str:
    """A name from the catalog, so the run reads like a real one (the catalog is the file scan here).

    ShortStraddle when it is there, since the legs booked below are one.
    """
    try:
        names = [e.get("name") for e in admin.get("/api/Strategy") or [] if e.get("name") not in (None, "Manual")]
    except dev.ApiError as error:
        print(f"  strategy catalog unavailable ({error.status}); using {STRATEGY_FALLBACK_NAME}")
        return STRATEGY_FALLBACK_NAME
    if "ShortStraddle" in names:
        return "ShortStraddle"
    return names[0] if names else STRATEGY_FALLBACK_NAME


def mark_running(state: dict[str, str], run_id: int) -> None:
    """Pending -> Running in the throwaway database, and nowhere else."""
    container = state["DB_CONTAINER"]
    label = subprocess.run(
        ["docker", "inspect", "-f", '{{index .Config.Labels "com.openfno.dev-live"}}', container],
        capture_output=True, text=True, check=False).stdout.strip()
    if label != "1":
        raise SystemExit(f"Refusing: container {container} is not the dev stack's database (no dev-live label).")
    sql = ('UPDATE simulation_runs SET "Status" = \'Running\', "StartedUtc" = now(), '
           '"StartedByName" = \'scripts/dev/seed-positions.py\' '
           f'WHERE "Id" = {int(run_id)} AND "Status" = \'Pending\' AND "Mode" = \'LivePaper\';')
    result = subprocess.run(
        ["docker", "exec", container, "psql", "-U", state["DB_USER"], "-d", state["DB_NAME"],
         "-v", "ON_ERROR_STOP=1", "-tAc", sql],
        capture_output=True, text=True, check=False)
    if result.returncode != 0 or "UPDATE 1" not in result.stdout:
        raise SystemExit(f"Could not mark run {run_id} Running: {result.stdout.strip()} {result.stderr.strip()}")


def seed_strategy_run(state: dict[str, str], admin: dev.DevApi, trader: dev.DevApi,
                      universe: list[dev.Instrument]) -> None:
    name = strategy_name(admin)
    run = trader.post("/api/Simulator/runs", {
        "mode": "LivePaper",
        "symbol": "NSE:NIFTY50-INDEX",
        "resolution": "1m",
        "strategyName": name,
        "parametersJson": json.dumps({"underlying": "NIFTY", "lots": 1, "seededBy": "scripts/dev/seed-positions.py"}),
        "initialCapital": 500000,
    })
    run_id = int(run["id"])
    mark_running(state, run_id)

    atm = dev.atm_strike(universe)
    call, put = dev.find_option(universe, atm, "CE"), dev.find_option(universe, atm, "PE")
    admin.post("/api/Simulator/signals", {
        "simulationRunId": run_id,
        "strategyName": name,
        "signalType": "OPEN_GROUP",
        "timestampUtc": dev.utc_stamp(datetime.now(timezone.utc)),
        "symbol": call.symbol,
        "groupId": f"DEV-STRADDLE-{run_id}",
        "metadataJson": json.dumps({"seededBy": "scripts/dev/seed-positions.py"}),
        "clientSignalId": f"dev-seed-{run_id}-open",
        "legs": [
            {"symbol": call.symbol, "side": "SELL", "quantity": 1},
            {"symbol": put.symbol, "side": "SELL", "quantity": 1},
        ],
    })
    print(f"  {trader.username}: strategy run #{run_id} ({name}, LivePaper, no runner) "
          f"short straddle {call.symbol} + {put.symbol}")


def open_positions(api: dev.DevApi) -> list[dict[str, Any]]:
    return (api.get("/api/Positions/open") or {}).get("positions") or []


def seed(state: dict[str, str], force: bool) -> None:
    admin = dev.DevApi.as_role(state, "admin")
    trader = dev.DevApi.as_role(state, "trader")
    universe = dev.load_universe()

    # Each half on its own: after a restart of the dev API the strategy run is
    # closed as orphaned while the manual books are still open.
    existing = open_positions(admin)
    need_manual = force or not any(p.get("isManualBook") for p in existing)
    need_run = force or not any(not p.get("isManualBook") for p in existing)
    if not need_manual and not need_run:
        print(f"Already seeded: {len(existing)} open leg(s). --force books another set, "
              "--fill one more order, --close squares one off.")
        return

    print("Booking paper positions")
    freshen_quotes(state, universe)
    atm = dev.atm_strike(universe)
    future = next(x for x in universe if x.kind == "future")

    if need_manual:
        manual_order(admin, dev.find_option(universe, atm, "CE").symbol, "BUY", 2)
        manual_order(admin, dev.find_option(universe, atm - 100, "PE").symbol, "SELL", 1)
        manual_order(admin, future.symbol, "BUY", 1)
        manual_order(trader, dev.find_option(universe, atm + 100, "CE").symbol, "SELL", 1)
    if need_run:
        seed_strategy_run(state, admin, trader, universe)

    print(f"{len(open_positions(admin))} open legs; their P&L moves while replay-ticks.py runs "
          "(local-live.sh up starts it).")


def fill(state: dict[str, str], user: str) -> None:
    api = dev.DevApi.as_role(state, user)
    universe = dev.load_universe()
    freshen_quotes(state, universe)
    option = random.choice([x for x in universe if x.kind == "option"])
    manual_order(api, option.symbol, random.choice(["BUY", "SELL"]), 1)


def close(state: dict[str, str], user: str, position_id: int | None) -> None:
    api = dev.DevApi.as_role(state, user)
    positions = open_positions(api)
    if not positions:
        raise SystemExit("No open legs to close.")
    if position_id is None:
        target = max(positions, key=lambda p: p["positionId"])
    else:
        target = next((p for p in positions if p["positionId"] == position_id), None)
        if target is None:
            raise SystemExit(f"Position {position_id} is not open (open: "
                             f"{', '.join(str(p['positionId']) for p in positions)}).")
    freshen_quotes(state, dev.load_universe())
    answer = api.post(f"/api/Strategy/runs/{target['runId']}/positions/close", {
        "positionIds": [target["positionId"]], "reason": "scripts/dev/seed-positions.py --close"})
    print(f"  {api.username}: closed position {target['positionId']} ({target['symbol']}, "
          f"{target.get('strategyName')} run {target['runId']}): {answer.get('message')}")


def parse_spots(values: list[str]) -> dict[str, float]:
    spots: dict[str, float] = {}
    for value in values:
        name, _, level = value.partition("=")
        if name.upper() not in dev.DEFAULT_SPOTS or not level:
            raise SystemExit(f"--spot takes NAME=LEVEL with NAME one of {', '.join(dev.DEFAULT_SPOTS)}.")
        spots[name.upper()] = float(level)
    return spots


def main() -> int:
    parser = argparse.ArgumentParser(description="Paper positions for the local dev stack.")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--setup", action="store_true", help="trader account, instrument master, watchlist")
    action.add_argument("--fill", action="store_true", help="book one more manual order")
    action.add_argument("--close", action="store_true", help="square off the newest open leg (or --position)")
    parser.add_argument("--force", action="store_true", help="seed again even when legs are open")
    parser.add_argument("--position", type=int, help="with --close: the position id to square off")
    parser.add_argument("--user", choices=["admin", "trader"], default="admin", help="with --fill/--close")
    parser.add_argument("--spot", action="append", default=[],
                        help="with --setup: a starting level, e.g. --spot NIFTY=24000 (repeatable)")
    args = parser.parse_args()

    state = dev.load_state()
    if not dev.api_is_up(state["API_URL"]):
        raise SystemExit(f"The dev API is not answering at {state['API_URL']}. scripts/dev/local-live.sh status")

    try:
        if args.setup:
            setup(state, parse_spots(args.spot))
        elif args.fill:
            fill(state, args.user)
        elif args.close:
            close(state, args.user, args.position)
        else:
            seed(state, args.force)
    except dev.ApiError as error:
        print(f"API refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
