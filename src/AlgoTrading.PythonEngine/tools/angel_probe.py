"""
tools/angel_probe.py

Try Angel One SmartAPI from this machine, without touching the live desk.

Nothing here writes to the database, starts a feed or changes a running
session: it reads. Use it to find out what the app can actually do from the
machine it is registered to.

Usage (from src/AlgoTrading.PythonEngine):
    python tools/angel_probe.py master                      # scrip master: download, count, map our symbols
    python tools/angel_probe.py check                       # what is configured, what is missing
    python tools/angel_probe.py login                       # log in and print the profile
    python tools/angel_probe.py quote NSE:RELIANCE-EQ NSE:NIFTY50-INDEX
    python tools/angel_probe.py candles NSE:NIFTY50-INDEX --resolution 5m --days 5
    python tools/angel_probe.py greeks NIFTY --expiry 2026-09-22
    python tools/angel_probe.py scanners                    # gainers/losers and PCR

The app is bound to the static IP it was registered with, so a call from a
laptop will be refused even when every credential is right; `check` says so.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import date, datetime, timedelta

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

import core.config  # noqa: F401,E402  — loads the repo-root .env

from market_data.angel.client import AngelClient, AngelError  # noqa: E402
from market_data.angel.instruments import AngelInstruments  # noqa: E402
from market_data.angel.session import AngelAuthError, AngelCredentials, AngelSession  # noqa: E402

INDEX_SUFFIX = "-INDEX"


def resolve(master: AngelInstruments, platform_symbol: str):
    """A platform symbol -> the Angel row, index or equity."""
    if platform_symbol.upper().endswith(INDEX_SUFFIX):
        return master.index(platform_symbol)
    return master.equity(platform_symbol)


def cmd_master(args) -> int:
    master = AngelInstruments.load(cache_dir=args.cache_dir)
    print(f"scrip master: {len(master):,} instruments")
    by_exchange = {}
    for row in master.rows:
        by_exchange[row.exchange] = by_exchange.get(row.exchange, 0) + 1
    print("  " + ", ".join(f"{k} {v:,}" for k, v in sorted(by_exchange.items())))
    print("\nour symbols in Angel's master:")
    for symbol in args.symbols or ["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX",
                                   "NSE:RELIANCE-EQ", "NSE:HDFCBANK-EQ"]:
        row = resolve(master, symbol)
        print(f"  {symbol:24} {'token ' + row.token + '  ' + row.symbol if row else 'NOT FOUND'}")
    return 0


def cmd_check(args) -> int:
    creds = AngelCredentials.from_env()
    print(f"root URL     : {creds.root_url}")
    print(f"API key      : {'set' if creds.api_key else 'MISSING'}")
    for name in ("ANGEL_CLIENT_CODE", "ANGEL_PIN", "ANGEL_TOTP_SECRET"):
        print(f"{name:13}: {'MISSING — add it to .env' if name in creds.missing else 'set'}")
    if creds.can_login:
        print("\nready to log in. Remember the app only answers from its registered static IP.")
    else:
        print("\nnot ready: fill the missing values in .env, then run `login`.")
    return 0


def cmd_login(args) -> int:
    session = AngelSession()
    session.login()
    print("logged in; the session carries a token (not printed).")
    client = AngelClient(session=session)
    profile = client.get("/rest/secure/angelbroking/user/v1/getProfile")
    if isinstance(profile, dict):
        for key in ("clientcode", "name", "email", "exchanges", "products", "broker"):
            if key in profile:
                print(f"  {key}: {profile[key]}")
    return 0


def cmd_quote(args) -> int:
    master = AngelInstruments.load(cache_dir=args.cache_dir)
    wanted, missing = {}, []
    for symbol in args.symbols:
        row = resolve(master, symbol)
        if row is None:
            missing.append(symbol)
            continue
        wanted.setdefault(row.exchange, []).append(row.token)
    for symbol in missing:
        print(f"  {symbol}: not in Angel's master")
    if not wanted:
        return 1
    data = AngelClient().quotes(wanted, mode=args.mode)
    print(json.dumps(data, indent=2)[:4000])
    return 0


def cmd_candles(args) -> int:
    master = AngelInstruments.load(cache_dir=args.cache_dir)
    row = resolve(master, args.symbol)
    if row is None:
        print(f"{args.symbol}: not in Angel's master")
        return 1
    end = date.today()
    start = end - timedelta(days=args.days)
    bars = AngelClient().history(row.exchange, row.token, args.resolution, start, end)
    print(f"{args.symbol} ({row.exchange} {row.token}): {len(bars)} bars at {args.resolution}")
    for bar in bars[:3] + bars[-3:]:
        print(f"  {bar.timestamp:%Y-%m-%d %H:%M}  O {bar.open} H {bar.high} L {bar.low} C {bar.close} V {bar.volume:g}")
    return 0


def cmd_greeks(args) -> int:
    expiry = datetime.strptime(args.expiry, "%Y-%m-%d").date()
    data = AngelClient().option_greeks(args.underlying, expiry)
    rows = data if isinstance(data, list) else []
    print(f"{args.underlying} {expiry}: {len(rows)} strikes")
    for row in rows[:6]:
        print("  " + json.dumps(row))
    return 0


def cmd_scanners(args) -> int:
    client = AngelClient()
    for kind in ("PercPriceGainers", "PercPriceLosers", "PercOIGainers", "PercOILosers"):
        try:
            data = client.gainers_losers(kind)
            print(f"{kind}: {len(data) if isinstance(data, list) else 'n/a'}")
            for row in (data or [])[:3]:
                print("  " + json.dumps(row))
        except AngelError as ex:
            print(f"{kind}: {ex}")
    try:
        pcr = client.put_call_ratio()
        print(f"putCallRatio: {len(pcr) if isinstance(pcr, list) else 'n/a'}")
    except AngelError as ex:
        print(f"putCallRatio: {ex}")
    return 0


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cache-dir", default=None, help="where the scrip master is kept")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("master"); p.add_argument("symbols", nargs="*"); p.set_defaults(func=cmd_master)
    sub.add_parser("check").set_defaults(func=cmd_check)
    sub.add_parser("login").set_defaults(func=cmd_login)
    p = sub.add_parser("quote"); p.add_argument("symbols", nargs="+"); p.add_argument("--mode", default="FULL")
    p.set_defaults(func=cmd_quote)
    p = sub.add_parser("candles"); p.add_argument("symbol"); p.add_argument("--resolution", default="5m")
    p.add_argument("--days", type=int, default=5); p.set_defaults(func=cmd_candles)
    p = sub.add_parser("greeks"); p.add_argument("underlying"); p.add_argument("--expiry", required=True)
    p.set_defaults(func=cmd_greeks)
    sub.add_parser("scanners").set_defaults(func=cmd_scanners)

    args = parser.parse_args(argv)
    if args.cache_dir is None:
        from market_data.angel.instruments import DEFAULT_CACHE_DIR
        args.cache_dir = DEFAULT_CACHE_DIR
    try:
        return args.func(args)
    except (AngelAuthError, AngelError) as ex:
        print(f"stopped: {ex}")
        return 2


if __name__ == "__main__":
    sys.exit(main())
