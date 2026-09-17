"""
tools/index_history_import.py

Load the index history files (data set D0, written by tools/index_history_download.py)
into the database, so backtests and research read five years instead of two.

  candles  index candles -> `candles`: "1" and "5" straight from the files, "15" and
           "D" rolled up from the 1-minute bars. SourceKey "dhan".
  options  1-minute option premiums -> `option_history_bars`: expiry flag WEEK, code 1,
           ExpiryDate left empty, exactly as the API's Dhan importer stores them.
           SourceKey "dhan".

Rows already in a table win. Every insert is ON CONFLICT DO NOTHING, so broker
candles (FYERS, live) are never overwritten and a rerun adds nothing twice.

Option bars are loaded only before the first bar the table already holds for that
underlying (or before --until). The later weeks were filled by the API importer and
sit in compressed chunks, and checking conflicts there would decompress them.

Usage (from src/AlgoTrading.PythonEngine; database settings from the repo-root .env):
    python tools/index_history_import.py candles [--names NIFTY,BANKNIFTY,SENSEX,FINNIFTY,MIDCPNIFTY,INDIAVIX]
    python tools/index_history_import.py options [--names NIFTY,BANKNIFTY,SENSEX] [--offsets=-5:5]
                                                 [--until 2024-09-02] [--dry-run]

Run it after market hours. Each file (candles) or month (options) is one transaction.
"""

from __future__ import annotations

import argparse
import csv
import gzip
import io
import os
import re
import sys
from collections import OrderedDict
from datetime import date, datetime, timedelta, timezone
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

from research import index_history as ih  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))
SESSION_OPEN_MINUTE = 9 * 60 + 15
SOURCE_KEY = "dhan"

#: The platform's spellings of the index symbols (the FYERS ones the backtest reads).
INDEX_SYMBOLS: Dict[str, str] = {
    "NIFTY": "NSE:NIFTY50-INDEX",
    "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
    "FINNIFTY": "NSE:FINNIFTY-INDEX",
    "MIDCPNIFTY": "NSE:MIDCPNIFTY-INDEX",
    "SENSEX": "BSE:SENSEX-INDEX",
    "INDIAVIX": "NSE:INDIAVIX-INDEX",
}

OPTION_FILE = re.compile(r"^(CE|PE)_([+-]\d+)\.csv\.gz$")

Candle = Tuple[str, str, datetime, float, float, float, float, int, str]
OptionBar = Tuple[datetime, str, str, int, None, int, float, str, str,
                  float, float, float, float, Optional[int], Optional[int], Optional[float], Optional[float], str]


# ------------------------------------------------------------------ pure parts --

def ist_to_utc(text: str) -> datetime:
    """'2021-03-01 09:15' (a naive IST bar start) -> the same instant in UTC."""
    return datetime.strptime(text, "%Y-%m-%d %H:%M").replace(tzinfo=IST).astimezone(timezone.utc)


def _num(value: Optional[str]) -> Optional[float]:
    return None if value in (None, "") else float(value)


def _int(value: Optional[str]) -> Optional[int]:
    return None if value in (None, "") else int(round(float(value)))


def read_csv_gz(path: str) -> List[Dict[str, str]]:
    with gzip.open(path, "rt", newline="") as handle:
        return list(csv.DictReader(handle))


def candle_rows(symbol: str, resolution: str, rows: Iterable[Dict[str, str]]) -> List[Candle]:
    """File rows -> `candles` tuples; volume 0 where the vendor gives none (Dhan's index candles)."""
    out = []
    for r in rows:
        out.append((symbol, resolution, ist_to_utc(r["bar_start_ist"]), float(r["open"]), float(r["high"]),
                    float(r["low"]), float(r["close"]), _int(r.get("volume")) or 0, SOURCE_KEY))
    return out


def roll_up(minute_bars: Sequence[Candle], resolution: str) -> List[Candle]:
    """
    1-minute candles -> "15" or "D" candles, the platform's way: 15-minute bars start
    at 09:15 IST (09:15, 09:30, ...); a daily bar is stamped 00:00 UTC of its IST date.
    """
    buckets: "OrderedDict[datetime, List[Candle]]" = OrderedDict()
    for bar in sorted(minute_bars, key=lambda b: b[2]):
        start_ist = bar[2].astimezone(IST)
        if resolution == "D":
            key = datetime(start_ist.year, start_ist.month, start_ist.day, tzinfo=timezone.utc)
        elif resolution == "15":
            minute = start_ist.hour * 60 + start_ist.minute
            floor = SESSION_OPEN_MINUTE + (minute - SESSION_OPEN_MINUTE) // 15 * 15
            key = start_ist.replace(hour=floor // 60, minute=floor % 60).astimezone(timezone.utc)
        else:
            raise ValueError(f"cannot roll 1-minute bars up to {resolution!r}")
        buckets.setdefault(key, []).append(bar)
    out = []
    for key, bars in buckets.items():
        first = bars[0]
        out.append((first[0], resolution, key, bars[0][3], max(b[4] for b in bars), min(b[5] for b in bars),
                    bars[-1][6], sum(b[7] for b in bars), SOURCE_KEY))
    return out


def option_file_series(name: str) -> Optional[Tuple[str, int]]:
    """'CE_+3.csv.gz' -> ('CE', 3); anything else -> None."""
    m = OPTION_FILE.match(name)
    return (m.group(1), int(m.group(2))) if m else None


def option_rows(underlying: str, option_type: str, offset: int, rows: Iterable[Dict[str, str]],
                before: Optional[datetime] = None) -> List[OptionBar]:
    """File rows -> `option_history_bars` tuples, keeping only bars that start before `before`."""
    out = []
    for r in rows:
        start = ist_to_utc(r["bar_start_ist"])
        if before is not None and start >= before:
            continue
        out.append((start, underlying, "WEEK", 1, None, offset, float(r["strike"]), option_type, "1m",
                    float(r["open"]), float(r["high"]), float(r["low"]), float(r["close"]),
                    _int(r.get("volume")), _int(r.get("oi")), _num(r.get("iv")), _num(r.get("spot")), SOURCE_KEY))
    return out


# ------------------------------------------------------------------- database --

CANDLE_COLUMNS = ('"Symbol"', '"Resolution"', '"TimeStampUtc"', '"Open"', '"High"', '"Low"', '"Close"',
                  '"Volume"', '"SourceKey"')
OPTION_COLUMNS = ('"BarStartUtc"', '"Underlying"', '"ExpiryFlag"', '"ExpiryCode"', '"ExpiryDate"',
                  '"StrikeOffset"', '"Strike"', '"OptionType"', '"Resolution"', '"Open"', '"High"', '"Low"',
                  '"Close"', '"Volume"', '"OpenInterest"', '"ImpliedVolatility"', '"SpotPrice"', '"SourceKey"')


def _copy_insert(conn, table: str, columns: Sequence[str], conflict: str, rows: Sequence[tuple]) -> int:
    """COPY rows into a temporary copy of the table's columns, then insert what is new."""
    if not rows:
        return 0
    buffer = io.StringIO()
    writer = csv.writer(buffer)
    for row in rows:
        writer.writerow(["" if v is None else (v.isoformat() if isinstance(v, datetime) else v) for v in row])
    buffer.seek(0)
    stage = f"stage_{table}"
    cols = ", ".join(columns)
    with conn.cursor() as cur:
        cur.execute(f"CREATE TEMP TABLE IF NOT EXISTS {stage} AS SELECT {cols} FROM {table} WITH NO DATA")
        cur.execute(f"TRUNCATE {stage}")
        cur.copy_expert(f"COPY {stage} ({cols}) FROM STDIN WITH (FORMAT csv)", buffer)
        cur.execute(f"INSERT INTO {table} ({cols}) SELECT {cols} FROM {stage} ON CONFLICT ({conflict}) DO NOTHING")
        inserted = cur.rowcount
    conn.commit()
    return inserted


def first_option_bar(conn, underlying: str) -> Optional[datetime]:
    with conn.cursor() as cur:
        cur.execute('SELECT "BarStartUtc" FROM option_history_bars WHERE "Underlying" = %s '
                    'ORDER BY "BarStartUtc" LIMIT 1', (underlying,))
        row = cur.fetchone()
    return row[0] if row else None


def import_candles(conn, root: str, names: Sequence[str], dry_run: bool) -> None:
    for name in names:
        symbol = INDEX_SYMBOLS[name]
        for minutes in ("1", "5"):
            folder = os.path.join(root, f"candles-{minutes}m", "raw", name)
            if not os.path.isdir(folder):
                print(f"{name} {minutes}m: no files at {folder}")
                continue
            total_read = total_new = 0
            for file in sorted(f for f in os.listdir(folder) if f.endswith(".csv.gz")):
                bars = candle_rows(symbol, minutes, read_csv_gz(os.path.join(folder, file)))
                batches = [bars]
                if minutes == "1":
                    batches += [roll_up(bars, "15"), roll_up(bars, "D")]
                for batch in batches:
                    total_read += len(batch)
                    if not dry_run:
                        total_new += _copy_insert(conn, "candles", CANDLE_COLUMNS,
                                                  '"Symbol", "Resolution", "TimeStampUtc"', batch)
            extra = " (+15m and D rolled up)" if minutes == "1" else ""
            print(f"{symbol} {minutes}m{extra}: {total_read:,} bars read, {total_new:,} new", flush=True)


def import_options(conn, root: str, names: Sequence[str], offsets: Sequence[int], until: Optional[date],
                   dry_run: bool) -> None:
    wanted = set(offsets)
    for name in names:
        folder = os.path.join(root, "options-1m", "raw", name)
        if not os.path.isdir(folder):
            print(f"{name}: no option files at {folder}")
            continue
        if until is not None:
            before = datetime(until.year, until.month, until.day, tzinfo=IST).astimezone(timezone.utc)
        else:
            before = first_option_bar(conn, name)
        print(f"{name}: loading bars before {before.astimezone(IST):%Y-%m-%d %H:%M} IST" if before
              else f"{name}: table has no bars yet, loading everything", flush=True)
        for month in sorted(os.listdir(folder)):
            month_dir = os.path.join(folder, month)
            if not os.path.isdir(month_dir):
                continue
            if before is not None and month > f"{before.astimezone(IST):%Y-%m}":
                continue
            rows: List[OptionBar] = []
            for file in sorted(os.listdir(month_dir)):
                series = option_file_series(file)
                if series is None or series[1] not in wanted:
                    continue
                rows += option_rows(name, series[0], series[1], read_csv_gz(os.path.join(month_dir, file)), before)
            new = 0 if dry_run else _copy_insert(
                conn, "option_history_bars", OPTION_COLUMNS,
                '"Underlying", "ExpiryFlag", "ExpiryCode", "StrikeOffset", "OptionType", "Resolution", "BarStartUtc"',
                rows)
            print(f"{name} {month}: {len(rows):,} bars read, {new:,} new", flush=True)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("what", choices=("candles", "options"))
    ap.add_argument("--names")
    ap.add_argument("--offsets", default="-5:5",
                    help="strikes from ATM as inclusive ranges; write --offsets=-10:-6,6:10 (with =)")
    ap.add_argument("--until", help="options: load bars before this IST date instead of before the table's first bar")
    ap.add_argument("--root", default=ih.ROOT)
    ap.add_argument("--dry-run", action="store_true", help="read and count the files, write nothing")
    args = ap.parse_args(argv)

    from research.data import connect

    conn = connect()
    try:
        if args.what == "candles":
            names = (args.names or ",".join(INDEX_SYMBOLS)).upper().split(",")
            import_candles(conn, args.root, names, args.dry_run)
        else:
            names = (args.names or "NIFTY,BANKNIFTY,SENSEX").upper().split(",")
            until = date.fromisoformat(args.until) if args.until else None
            import_options(conn, args.root, names, ih.parse_offsets(args.offsets), until, args.dry_run)
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
