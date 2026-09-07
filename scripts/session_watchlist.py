#!/usr/bin/env python
"""
Two watchlists, one feed: park the equity/F&O symbols when NSE closes.

WHY
---
The broker socket carries every subscribed symbol all day, but NSE and BSE stop
producing after 15:30 while MCX runs to 23:30. Left alone the ingestor keeps
holding subscriptions that cannot tick for eight hours - work the box does not
need to do, and noise in every "stale quote" view.

So the watchlist is treated as two lists, split by the only thing that actually
decides the session: the exchange on the symbol.

    MCX:*            -> commodity   (stays subscribed into the evening)
    NSE:* / BSE:*    -> equity/F&O  (parked at 15:30, restored next morning)

Nothing is deleted. Parking sets IsActive=false on the row, which is the exact
flag the ingestor filters on (fyers_streamer.get_active_watchlist), so its next
watchlist sync drops those subscriptions and the morning run puts them back.

WHAT IT WILL NOT PARK
---------------------
Any contract a live run is holding an open position in. The risk guard prices
stop-loss and target off those quotes; unsubscribing one would leave a run
exposed with a mark that never moves again. If a run is still holding NIFTY
options past 15:30, those stay on the wire and the alert says so.

Which symbols this process parked is written to a state file, and only those are
restored. Re-activating "every inactive NSE row" would silently switch on things
an operator had deliberately turned off.

USAGE
-----
    python scripts/session_watchlist.py --status
    python scripts/session_watchlist.py --park      # after the equity close
    python scripts/session_watchlist.py --restore   # before the equity open
    python scripts/session_watchlist.py --install   # schedule both
    ... --dry-run   on any of them
"""

from __future__ import annotations

import argparse
import json
import logging
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

import requests

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts"))

# Reuse the notifier's config loading, alert publishing and formatting rather
# than growing a second, slightly different copy of all three.
from telegram_notifier import (  # noqa: E402
    IST,
    AlertPublisher,
    ApiClient,
    Config,
    esc,
    load_env,
)

import redis  # noqa: E402

STATE_FILE = REPO_ROOT / "data" / "session-watchlist-state.json"
COMMODITY_PREFIXES = ("MCX:",)

log = logging.getLogger("session-watchlist")


def is_commodity(symbol: str) -> bool:
    return symbol.upper().startswith(COMMODITY_PREFIXES)


def read_state() -> dict[str, Any]:
    try:
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"parked": []}


def write_state(state: dict[str, Any]) -> None:
    STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")


def protected_symbols(api: ApiClient) -> set[str]:
    """
    Contracts a live run is holding. These are never parked.

    On any doubt this returns everything it managed to find rather than an empty
    set: over-protecting costs a few idle subscriptions, under-protecting costs a
    run its stop-loss.
    """
    held: set[str] = set()
    runs = api.get("/api/Strategy/runs")
    if not isinstance(runs, list):
        log.warning("could not read runs; protecting nothing is unsafe, so parking is skipped")
        raise RuntimeError("run list unavailable")

    for run in runs:
        if not run.get("isActive"):
            continue
        live = api.get(f"/api/Strategy/runs/{run['runId']}/live")
        if not isinstance(live, dict):
            raise RuntimeError(f"live view unavailable for run {run.get('runId')}")
        for position in live.get("positions") or []:
            if str(position.get("status") or "").lower() == "open" and position.get("symbol"):
                held.add(position["symbol"])
    return held


def set_active(api: ApiClient, item: dict[str, Any], active: bool) -> bool:
    """Upsert the row with a new IsActive. Returns True on success."""
    try:
        response = api._session.post(  # noqa: SLF001 - same package, same contract
            f"{api._config.api_base}/api/LiveData/watchlist",  # noqa: SLF001
            headers={"Authorization": f"Bearer {api._token}"},  # noqa: SLF001
            json={
                "symbol": item["symbol"],
                "dataType": item.get("dataType") or "symbolUpdate",
                "isActive": active,
                "priority": item.get("priority") or 0,
            },
            timeout=20,
        )
        return response.ok
    except requests.RequestException as exc:
        log.warning("could not update %s: %s", item.get("symbol"), exc)
        return False


def now_ist() -> str:
    return datetime.now(timezone.utc).astimezone(IST).strftime("%d %b, %H:%M IST")


def announce(publisher: AlertPublisher, title: str, lines: list[str], severity: str) -> None:
    publisher.publish(
        title=title,
        message="\n".join(lines),
        source="process",
        severity=severity,
        underlying="FEED",
    )


def do_park(api: ApiClient, publisher: AlertPublisher, dry: bool) -> int:
    watchlist = api.get("/api/LiveData/watchlist") or []
    held = protected_symbols(api)

    candidates = [
        w for w in watchlist
        if w.get("isActive") and not is_commodity(w.get("symbol", ""))
    ]
    parkable = [w for w in candidates if w["symbol"] not in held]
    skipped = [w for w in candidates if w["symbol"] in held]
    commodity = [w for w in watchlist if w.get("isActive") and is_commodity(w.get("symbol", ""))]

    log.info("equity/F&O active: %d, of which parkable: %d, held by a live run: %d",
             len(candidates), len(parkable), len(skipped))
    log.info("commodity staying on the feed: %d", len(commodity))

    if dry:
        for w in parkable:
            log.info("  would park %s", w["symbol"])
        for w in skipped:
            log.info("  would KEEP %s (live run holds it)", w["symbol"])
        return 0

    parked: list[str] = []
    for item in parkable:
        if set_active(api, item, False):
            parked.append(item["symbol"])

    state = read_state()
    state["parked"] = sorted(set(state.get("parked", [])) | set(parked))
    state["parkedAt"] = now_ist()
    write_state(state)

    lines = [
        "🌙 <b>Equity/F&amp;O feed parked</b>",
        "",
        f"NSE and BSE are closed; {len(parked)} symbol(s) unsubscribed to take the",
        "load off the socket. They come back automatically before tomorrow's open.",
        "",
        f"Still live: <b>{len(commodity)} MCX contract(s)</b> - commodity trades to 23:30 IST.",
    ]
    if skipped:
        lines += [
            "",
            f"<b>Kept subscribed</b> ({len(skipped)}) - a live run holds these and the",
            "risk guard prices its stop-loss off them:",
        ]
        lines += [f"  {esc(w['symbol'])}" for w in skipped[:10]]
    lines += ["", now_ist()]

    announce(publisher, "Equity/F&O feed parked", lines, "info")
    log.info("parked %d symbol(s)", len(parked))
    return 0


def do_restore(api: ApiClient, publisher: AlertPublisher, dry: bool) -> int:
    state = read_state()
    wanted = set(state.get("parked", []))
    if not wanted:
        log.info("nothing recorded as parked; nothing to restore")
        return 0

    watchlist = api.get("/api/LiveData/watchlist") or []
    by_symbol = {w["symbol"]: w for w in watchlist if w.get("symbol")}
    to_restore = [by_symbol[s] for s in sorted(wanted) if s in by_symbol and not by_symbol[s].get("isActive")]

    log.info("recorded as parked: %d, still present and inactive: %d", len(wanted), len(to_restore))

    if dry:
        for w in to_restore:
            log.info("  would restore %s", w["symbol"])
        return 0

    restored: list[str] = []
    for item in to_restore:
        if set_active(api, item, True):
            restored.append(item["symbol"])

    state["parked"] = sorted(wanted - set(restored))
    state["restoredAt"] = now_ist()
    write_state(state)

    announce(
        publisher,
        "Equity/F&O feed restored",
        [
            "☀️ <b>Equity/F&amp;O feed restored</b>",
            "",
            f"{len(restored)} symbol(s) re-subscribed ahead of the equity open.",
            "",
            now_ist(),
        ],
        "success",
    )
    log.info("restored %d symbol(s)", len(restored))
    return 0


def do_status(api: ApiClient) -> int:
    watchlist = api.get("/api/LiveData/watchlist") or []
    active = [w for w in watchlist if w.get("isActive")]
    commodity = [w for w in active if is_commodity(w["symbol"])]
    equity = [w for w in active if not is_commodity(w["symbol"])]
    parked = read_state().get("parked", [])

    print(f"  commodity (MCX)      active: {len(commodity)}")
    print(f"  equity / F&O         active: {len(equity)}")
    print(f"  parked by this script      : {len(parked)}")
    for s in parked[:20]:
        print(f"      {s}")
    return 0


def do_install() -> int:
    """
    Two scheduled tasks: park just after the equity close, restore just before
    the open. Both run every weekday; the script itself is harmless on a holiday
    because there is nothing to park that is not already parked.
    """
    python = sys.executable
    script = str(Path(__file__).resolve())
    for name, when, flag in (
        ("AlgoTrading park equity feed", "15:35", "--park"),
        ("AlgoTrading restore equity feed", "09:05", "--restore"),
    ):
        cmd = [
            "schtasks", "/Create", "/F", "/TN", name,
            "/TR", f'"{python}" "{script}" {flag}',
            "/SC", "WEEKLY", "/D", "MON,TUE,WED,THU,FRI",
            "/ST", when,
        ]
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode == 0:
            log.info("scheduled '%s' at %s", name, when)
        else:
            log.error("could not schedule '%s': %s", name, (result.stderr or result.stdout).strip())
            return 1
    log.info("remove with: schtasks /Delete /TN \"AlgoTrading park equity feed\" /F")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--park", action="store_true", help="deactivate equity/F&O after the close")
    group.add_argument("--restore", action="store_true", help="reactivate what this script parked")
    group.add_argument("--status", action="store_true", help="show the two lists")
    group.add_argument("--install", action="store_true", help="schedule park and restore")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO, format="%(asctime)s  %(levelname)-7s %(message)s",
                        datefmt="%H:%M:%S")
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:  # noqa: BLE001
            pass

    if args.install:
        return do_install()

    config = Config(load_env(REPO_ROOT / ".env"))
    api = ApiClient(config)
    try:
        api.login()
    except Exception as exc:  # noqa: BLE001
        log.error("cannot sign in to the API at %s - %s", config.api_base, exc)
        return 2

    if args.status:
        return do_status(api)

    publisher = AlertPublisher(
        redis.Redis(
            host=config.redis_host, port=config.redis_port, db=config.redis_db,
            password=config.redis_password, decode_responses=True,
        )
    )

    try:
        if args.park:
            return do_park(api, publisher, args.dry_run)
        return do_restore(api, publisher, args.dry_run)
    except RuntimeError as exc:
        log.error("aborted: %s", exc)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
