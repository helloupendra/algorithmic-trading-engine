"""
tools/equities_intraday.py

Dhan 5-minute bars for the stocks in a symbol list (research data set D2).

Usage (from src/AlgoTrading.PythonEngine, with DHAN_CLIENT_ID and
DHAN_ACCESS_TOKEN in the environment):
    python tools/equities_intraday.py download --symbols symbols.csv --from 2024-09-01 [--to 2026-09-15]
                                               [--limit 3]

symbols.csv needs the columns `symbol` and `dhan_id`; rows without a Dhan id
are listed and skipped. Run it on a workstation, after market hours: the
account's request budget is shared with the production server.
"""

from __future__ import annotations

import argparse
import csv
import os
import sys
from datetime import date, datetime, timedelta, timezone

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

from research.equities import intraday  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("download",))
    parser.add_argument("--symbols", required=True)
    parser.add_argument("--from", dest="date_from", required=True)
    parser.add_argument("--to", dest="date_to")
    parser.add_argument("--root", default=intraday.DEFAULT_ROOT)
    parser.add_argument("--limit", type=int)
    parser.add_argument("--spacing", type=float, default=intraday.SPACING_SECONDS,
                        help="seconds between requests (default %(default)s); raise it after a DH-904 rate limit")
    parser.add_argument("--stop-at", help="HH:MM IST: stop before the next request at or after this time (next occurrence)")
    args = parser.parse_args(argv)
    import socket
    import urllib3.util.connection as connection
    connection.allowed_gai_family = lambda: socket.AF_INET   # this workstation's IPv6 route is dead (84 s a call)

    with open(args.symbols) as fh:
        rows = list(csv.DictReader(fh))
    stocks = [(r["symbol"], r["dhan_id"]) for r in rows if r.get("dhan_id")]
    unmapped = [r["symbol"] for r in rows if not r.get("dhan_id")]
    if unmapped:
        print(f"{len(unmapped)} symbols have no Dhan id and are skipped: {', '.join(unmapped)}", flush=True)
    if args.limit:
        stocks = stocks[:args.limit]
    start = date.fromisoformat(args.date_from)
    end = date.fromisoformat(args.date_to) if args.date_to else datetime.now(IST).date()
    stop_at = None
    if args.stop_at:
        now = datetime.now(IST)
        h, m = (int(x) for x in args.stop_at.split(":"))
        deadline = now.replace(hour=h, minute=m, second=0, microsecond=0)
        if deadline <= now:
            deadline += timedelta(days=1)
        print(f"stops at {deadline:%Y-%m-%d %H:%M} IST", flush=True)
        stop_at = lambda: datetime.now(IST) >= deadline  # noqa: E731
    try:
        counts = intraday.download(args.root, stocks, start, end, log=lambda line: print(line, flush=True),
                                   pacer=intraday.Pacer(spacing=args.spacing), stop_at=stop_at)
    except intraday.AuthError as ex:
        print(f"stopped: {ex}", flush=True)
        return 2
    print("done " + " ".join(f"{k}={v}" for k, v in counts.items()), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
