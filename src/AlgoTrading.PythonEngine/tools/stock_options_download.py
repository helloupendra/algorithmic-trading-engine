"""
tools/stock_options_download.py

Dhan option history for the stocks in a picks list (data set D3), 5-minute bars,
nearest monthly expiry, ATM-3..ATM+3, calls and puts, one calendar month per series.

Usage (from src/AlgoTrading.PythonEngine; credentials from --env-file or the
environment, run after market hours):
    python tools/stock_options_download.py --picks picks.csv [--stop-at 08:30] [--spacing 0.5] [--limit 20]

picks.csv needs `symbol` and `day` (YYYY-MM-DD); every (symbol, month) with a
pick is fetched. Dhan ids come from the intraday data set's symbols.csv and
symbols-delisted.csv. The run stops by itself at --stop-at (IST, the next such
time), before the production server's market-open job uses the same account.
"""

from __future__ import annotations

import argparse
import csv
import os
import socket
import sys
from datetime import datetime, timedelta, timezone

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

from research.equities import intraday, stock_options as so  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))
BARS = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "equities", "intraday-5m")


def dhan_ids() -> dict:
    ids = {}
    for name in ("symbols.csv", "symbols-delisted.csv"):
        path = os.path.join(BARS, name)
        if os.path.exists(path):
            with open(path) as fh:
                for r in csv.DictReader(fh):
                    if r.get("dhan_id"):
                        ids.setdefault(r["symbol"], r["dhan_id"])
    return ids


def deadline(hhmm: str, now: datetime) -> datetime:
    h, m = (int(x) for x in hhmm.split(":"))
    at = now.replace(hour=h, minute=m, second=0, microsecond=0)
    return at if at > now else at + timedelta(days=1)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--picks", required=True)
    ap.add_argument("--root", default=so.DEFAULT_ROOT)
    ap.add_argument("--stop-at", default="08:30")
    ap.add_argument("--spacing", type=float, default=0.5)
    ap.add_argument("--limit", type=int)
    ap.add_argument("--env-file", default=os.path.expanduser("~/.config/openfno/dhan.env"))
    args = ap.parse_args(argv)

    import urllib3.util.connection as connection
    connection.allowed_gai_family = lambda: socket.AF_INET   # this network's IPv6 route is dead (84 s a call)
    if os.path.exists(args.env_file):
        with open(args.env_file) as fh:
            for line in fh:
                if "=" in line:
                    k, v = line.strip().split("=", 1)
                    os.environ.setdefault(k, v)

    ids = dhan_ids()
    with open(args.picks) as fh:
        picks = list(csv.DictReader(fh))
    months, missing = set(), set()
    for p in picks:
        sid = ids.get(p["symbol"])
        if sid is None:
            missing.add(p["symbol"])
            continue
        months.add((p["symbol"], sid, p["day"][:7]))
    series = so.plan(months)
    if args.limit:
        series = series[:args.limit]
    if missing:
        print(f"no Dhan id for {len(missing)} symbols, skipped: {', '.join(sorted(missing))}", flush=True)
    stop = deadline(args.stop_at, datetime.now(IST))
    print(f"{len(months)} stock-months, {len(series)} series; stops at {stop:%Y-%m-%d %H:%M} IST", flush=True)
    try:
        counts = so.download(args.root, series, so.dhan_poster(), intraday.Pacer(spacing=args.spacing),
                             stop_at=lambda: datetime.now(IST) >= stop, log=lambda line: print(line, flush=True))
    except intraday.AuthError as ex:
        print(f"stopped: {ex}", flush=True)
        return 2
    print("done " + " ".join(f"{k}={v}" for k, v in counts.items()), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
