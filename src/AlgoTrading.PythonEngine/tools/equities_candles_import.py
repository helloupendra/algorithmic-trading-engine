"""
tools/equities_candles_import.py

Load the stock 5-minute files (data set D2, `~/OpenFNO-data/equities/intraday-5m`)
into the `candles` table as `NSE:<SYMBOL>-EQ`, so a backtest can replay a stock the
same way it replays an index.

Daily bars are rolled up from the same minutes, because a strategy that asks for
"1D" must not need a second download to get them.

Rows already in the table win: every insert is ON CONFLICT DO NOTHING, so broker
candles are never overwritten and a rerun adds nothing twice.

Usage (from src/AlgoTrading.PythonEngine; database settings from the repo-root .env):
    python tools/equities_candles_import.py --symbols RELIANCE,HDFCBANK
    python tools/equities_candles_import.py --symbols-file nifty50.txt [--from 2021-01-01] [--dry-run]
    python tools/equities_candles_import.py --all            # every stock with files (large)

One stock is one transaction. Run it after market hours: a few million rows on a
live box is still a few million rows.
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import date
from typing import Dict, List, Optional, Sequence

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

# The file reading, roll-up and COPY are the index importer's; a stock file has
# the same columns, and two copies of an insert path is one copy too many.
from tools.index_history_import import (  # noqa: E402
    CANDLE_COLUMNS,
    _copy_insert,
    candle_rows,
    read_csv_gz,
    roll_up,
)

DATA_DIR = os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data"))
RAW_ROOT = os.path.join(DATA_DIR, "equities", "intraday-5m", "raw")
RESOLUTION = "5"


def equity_symbol(name: str) -> str:
    """'RELIANCE' -> 'NSE:RELIANCE-EQ', the spelling the platform prices stocks under."""
    text = name.strip().upper()
    if ":" in text:
        return text
    return f"NSE:{text}-EQ"


def stocks_with_files(root: str = RAW_ROOT) -> List[str]:
    return sorted(name for name in os.listdir(root) if os.path.isdir(os.path.join(root, name)))


def read_symbol_list(text: Optional[str], path: Optional[str]) -> List[str]:
    """--symbols and --symbols-file, in either order, de-duplicated and upper-cased."""
    names: List[str] = []
    if text:
        names += [part for part in text.replace("\n", ",").split(",")]
    if path:
        with open(os.path.expanduser(path)) as handle:
            for line in handle:
                names += line.split(",")
    seen, out = set(), []
    for name in names:
        key = name.strip().upper()
        if key and key not in seen:
            seen.add(key)
            out.append(key)
    return out


def import_stock(conn, name: str, root: str, start: Optional[date], dry_run: bool) -> Dict[str, int]:
    """One stock's files into `candles`; returns {read, new} across 5-minute and daily bars."""
    folder = os.path.join(root, name)
    symbol = equity_symbol(name)
    counts = {"read": 0, "new": 0}
    if not os.path.isdir(folder):
        return counts
    for file in sorted(f for f in os.listdir(folder) if f.endswith(".csv.gz")):
        rows = read_csv_gz(os.path.join(folder, file))
        if start is not None:
            rows = [r for r in rows if r["bar_start_ist"][:10] >= start.isoformat()]
        if not rows:
            continue
        bars = candle_rows(symbol, RESOLUTION, rows)
        for batch in (bars, roll_up(bars, "D")):
            counts["read"] += len(batch)
            if not dry_run:
                counts["new"] += _copy_insert(conn, "candles", CANDLE_COLUMNS,
                                              '"Symbol", "Resolution", "TimeStampUtc"', batch)
    return counts


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--symbols", help="comma-separated stock names, e.g. RELIANCE,HDFCBANK")
    ap.add_argument("--symbols-file", help="a file of names, one per line or comma separated")
    ap.add_argument("--all", action="store_true", help="every stock that has files (large)")
    ap.add_argument("--from", dest="start", help="skip bars before this IST date")
    ap.add_argument("--root", default=RAW_ROOT)
    ap.add_argument("--dry-run", action="store_true", help="read and count the files, write nothing")
    args = ap.parse_args(argv)

    names = read_symbol_list(args.symbols, args.symbols_file)
    if args.all:
        names = stocks_with_files(args.root)
    if not names:
        ap.error("name the stocks with --symbols, --symbols-file or --all")

    start = date.fromisoformat(args.start) if args.start else None
    from research.data import connect

    conn = connect()
    total = {"read": 0, "new": 0}
    missing: List[str] = []
    try:
        for index, name in enumerate(names, 1):
            counts = import_stock(conn, name, args.root, start, args.dry_run)
            if counts["read"] == 0:
                missing.append(name)
            total["read"] += counts["read"]
            total["new"] += counts["new"]
            print(f"[{index}/{len(names)}] {equity_symbol(name)}: {counts['read']:,} bars read, "
                  f"{counts['new']:,} new", flush=True)
    finally:
        conn.close()
    print(f"done: {total['read']:,} bars read, {total['new']:,} new, {len(missing)} stock(s) with no files")
    if missing:
        print("no files for: " + ", ".join(missing[:20]) + (" …" if len(missing) > 20 else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
