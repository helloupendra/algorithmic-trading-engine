"""
tools/equities_eod.py

Exchange bhavcopies for stock-picker research: download them, rebuild the
normalised month files, and report coverage. Research data lives outside the
repository (default ~/OpenFNO-data/equities/eod, or $OPENFNO_DATA_DIR) and is
never deleted by this tool.

Usage (from src/AlgoTrading.PythonEngine):
    python tools/equities_eod.py download  --from 2023-09-01 [--to 2026-09-15]
    python tools/equities_eod.py normalise --from 2023-09-01 [--to 2026-09-15]
    python tools/equities_eod.py coverage  --from 2023-09-01 [--to 2026-09-15]

Run it on a workstation, not on the production server.
"""

from __future__ import annotations

import argparse
import os
import sys
from collections import Counter
from datetime import date, datetime, timedelta, timezone

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

from research.equities import bhavcopy  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("download", "normalise", "coverage"))
    parser.add_argument("--from", dest="date_from", required=True)
    parser.add_argument("--to", dest="date_to")
    parser.add_argument("--root", default=bhavcopy.DEFAULT_ROOT)
    args = parser.parse_args(argv)
    import socket
    import urllib3.util.connection as connection
    # This workstation's IPv6 route is dead: requests tried IPv6 first and each new connection waited ~80 s.
    connection.allowed_gai_family = lambda: socket.AF_INET

    start = date.fromisoformat(args.date_from)
    end = date.fromisoformat(args.date_to) if args.date_to else datetime.now(IST).date()
    if args.command == "download":
        bhavcopy.download(args.root, start, end, log=lambda line: print(line, flush=True))
        args.command = "coverage"
    if args.command == "normalise":
        bhavcopy.normalise(args.root, start, end)
        return 0

    days = bhavcopy.coverage(args.root, start, end)
    verdicts = Counter(d.verdict.split(" (")[0] for d in days)
    print(f"{start} to {end}: " + ", ".join(f"{k} {v}" for k, v in sorted(verdicts.items())))
    for d in days:
        if d.verdict.startswith("gap") or d.verdict == "not tried":
            print(f"  {d.day} {date.fromisoformat(d.day):%a}  {d.verdict}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
