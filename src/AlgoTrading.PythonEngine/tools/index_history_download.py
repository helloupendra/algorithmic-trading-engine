"""
tools/index_history_download.py

Long index history as files (data set D0): index candles (1 and 5 minutes) for
NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX and INDIA VIX, then 1-minute
option premiums (nearest expiry, ATM-5..ATM+5, CE and PE) for NIFTY, BANKNIFTY
and SENSEX, month by month. See research/index_history.py.

Usage (from src/AlgoTrading.PythonEngine, after market hours):
    python tools/index_history_download.py [--what all|candles|options] [--from 2020-08-01] [--to 2026-09-16]
                                           [--stop-at 08:30] [--spacing 0.5] [--limit 10]
"""

from __future__ import annotations

import argparse
import os
import socket
import sys
from datetime import date, datetime, timedelta, timezone

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from research import index_history as ih  # noqa: E402
from research.equities.intraday import AuthError, Pacer  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--what", choices=("all", "candles", "options"), default="all")
    ap.add_argument("--from", dest="start", default="2020-08-01")
    ap.add_argument("--to", dest="end")
    ap.add_argument("--root", default=ih.ROOT)
    ap.add_argument("--stop-at", default="08:30")
    ap.add_argument("--spacing", type=float, default=0.5)
    ap.add_argument("--limit", type=int)
    ap.add_argument("--env-file", default=os.path.expanduser("~/.config/openfno/dhan.env"))
    args = ap.parse_args(argv)

    import urllib3.util.connection as connection
    connection.allowed_gai_family = lambda: socket.AF_INET     # this network's IPv6 route is dead
    if os.path.exists(args.env_file):
        with open(args.env_file) as fh:
            for line in fh:
                if "=" in line:
                    k, v = line.strip().split("=", 1)
                    os.environ.setdefault(k, v)

    now = datetime.now(IST)
    start = date.fromisoformat(args.start)
    end = date.fromisoformat(args.end) if args.end else now.date()
    jobs = []
    if args.what in ("all", "candles"):
        jobs += ih.candle_jobs(list(ih.INDICES), ["5", "1"], start, end)
    if args.what in ("all", "options"):
        jobs += ih.option_jobs(["NIFTY", "BANKNIFTY", "SENSEX"], start, end)
    if args.limit:
        jobs = jobs[:args.limit]
    h, m = (int(x) for x in args.stop_at.split(":"))
    stop = now.replace(hour=h, minute=m, second=0, microsecond=0)
    if stop <= now:
        stop += timedelta(days=1)
    print(f"{len(jobs)} requests ({start} → {end}); stops at {stop:%Y-%m-%d %H:%M} IST", flush=True)
    try:
        counts = ih.run(args.root, jobs, ih.dhan_post(), Pacer(spacing=args.spacing),
                        stop_at=lambda: datetime.now(IST) >= stop, log=lambda line: print(line, flush=True))
    except AuthError as ex:
        print(f"stopped: {ex}", flush=True)
        return 2
    print("done " + " ".join(f"{k}={v}" for k, v in counts.items()), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
