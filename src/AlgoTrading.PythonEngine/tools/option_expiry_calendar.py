"""
tools/option_expiry_calendar.py

The index option expiry calendar, taken from the exchanges' daily derivative
bhavcopies rather than from weekday rules, so holiday shifts, weekday changes and
one-off revisions are exactly what the exchange recorded.

A date E is an expiry of underlying U when the bhavcopy of trading day E lists an
index option of U expiring on E. A listed expiry that the exchange moved later never
passes that test, so it is left out.

  NSE  NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY
       old format until 5 Jul 2024: content/historical/DERIVATIVES/YYYY/MON/foDDMONYYYYbhav.csv.zip
       UDiFF from 8 Jul 2024:        content/fo/BhavCopy_NSE_FO_0_0_0_YYYYMMDD_F_0000.csv.zip
  BSE  SENSEX, BANKEX
       old format:  download/Bhavcopy/Derivative/bhavcopyDD-MM-YY.zip
       UDiFF:       download/BhavCopy/Derivative/BhavCopy_BSE_FO_0_0_0_YYYYMMDD_F_0000.CSV

Trading days come from the index candle files of data set D0 (NIFTY for NSE, SENSEX
for BSE). Each day's parsed expiries are cached, so a rerun only fetches new days.

Usage (from src/AlgoTrading.PythonEngine):
    python tools/option_expiry_calendar.py [--exchanges NSE,BSE] [--from 2020-08-01] [--to 2026-09-17]
                                           [--out ~/OpenFNO-data/reference/index-option-expiries.json]
"""

from __future__ import annotations

import argparse
import csv
import gzip
import io
import json
import os
import re
import sys
import time
import zipfile
from datetime import date, datetime, timezone
from typing import Callable, Dict, Iterable, List, Optional, Set, Tuple

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
# First, always: the tools folder is sys.path[0] when a tool is run as a script,
# and `tools/research.py` would otherwise shadow the `research` package.
sys.path.insert(0, ENGINE_DIR)

DATA_DIR = os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data"))
UDIFF_FROM = date(2024, 7, 8)
NSE_UNDERLYINGS = ("NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY")
BSE_UNDERLYINGS = ("SENSEX", "BANKEX")
USER_AGENT = "Mozilla/5.0"

Expiry = Tuple[str, date]          # (underlying, expiry date)
Fetch = Callable[[str], Tuple[int, bytes]]


# ------------------------------------------------------------------ urls --

MONTHS = ("JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC")


def nse_urls(day: date) -> List[str]:
    mon = MONTHS[day.month - 1]
    old = (f"https://nsearchives.nseindia.com/content/historical/DERIVATIVES/{day:%Y}/{mon}/"
           f"fo{day:%d}{mon}{day:%Y}bhav.csv.zip")
    new = f"https://nsearchives.nseindia.com/content/fo/BhavCopy_NSE_FO_0_0_0_{day:%Y%m%d}_F_0000.csv.zip"
    return [old, new] if day < UDIFF_FROM else [new, old]


def bse_urls(day: date) -> List[str]:
    old = f"https://www.bseindia.com/download/Bhavcopy/Derivative/bhavcopy{day:%d-%m-%y}.zip"
    new = f"https://www.bseindia.com/download/BhavCopy/Derivative/BhavCopy_BSE_FO_0_0_0_{day:%Y%m%d}_F_0000.CSV"
    return [old, new] if day < UDIFF_FROM else [new, old]


# --------------------------------------------------------------- parsing --

def _text(body: bytes) -> str:
    if body[:2] == b"PK":
        with zipfile.ZipFile(io.BytesIO(body)) as z:
            name = next(n for n in z.namelist() if n.lower().endswith(".csv"))
            body = z.read(name)
    # Some old BSE files end their lines with a bare carriage return.
    return body.decode("utf-8-sig", errors="replace").replace("\r\n", "\n").replace("\r", "\n")


def _date(text: str) -> Optional[date]:
    text = text.strip()
    for fmt in ("%Y-%m-%d", "%d-%b-%Y", "%d %b %Y", "%d-%m-%Y"):
        try:
            return datetime.strptime(text, fmt).date()
        except ValueError:
            continue
    return None


BSE_SYMBOL = re.compile(r"^(SENSEX50|SENSEX|BANKEX)\d")


def parse_expiries(body: bytes, exchange: str) -> Set[Expiry]:
    """Every (underlying, expiry) of an index option in one bhavcopy, whatever its format."""
    rows = csv.DictReader(io.StringIO(_text(body)))
    wanted = NSE_UNDERLYINGS if exchange == "NSE" else BSE_UNDERLYINGS
    out: Set[Expiry] = set()
    for r in rows:
        r = {(k or "").strip(): (v or "").strip() for k, v in r.items()}
        if "FinInstrmTp" in r:                                  # UDiFF, both exchanges
            if r["FinInstrmTp"] != "IDO":
                continue
            underlying, expiry = r.get("TckrSymb", ""), _date(r.get("XpryDt", ""))
        elif "INSTRUMENT" in r:                                 # NSE old format
            if r["INSTRUMENT"] != "OPTIDX":
                continue
            underlying, expiry = r.get("SYMBOL", ""), _date(r.get("EXPIRY_DT", ""))
        elif "Contract Type" in r:                              # BSE old format
            if r["Contract Type"] != "IO":
                continue
            m = BSE_SYMBOL.match(r.get("Symbol", ""))
            underlying, expiry = (m.group(1) if m else ""), _date(r.get("Expiry", ""))
        else:
            raise ValueError(f"unknown bhavcopy layout: {list(r)[:6]}")
        if underlying in wanted and expiry is not None:
            out.add((underlying, expiry))
    return out


def calendar(days: Dict[date, Set[Expiry]]) -> Dict[str, List[date]]:
    """An expiry counts when the bhavcopy of that very day lists it."""
    out: Dict[str, Set[date]] = {}
    for day, expiries in days.items():
        for underlying, expiry in expiries:
            if expiry == day:
                out.setdefault(underlying, set()).add(expiry)
    return {u: sorted(d) for u, d in sorted(out.items())}


def unconfirmed(days: Dict[date, Set[Expiry]], confirmed: Dict[str, List[date]], last_day: date) -> Dict[str, List[date]]:
    """Listed expiries on or before the last day read that never became an expiry (revised by the exchange)."""
    seen: Dict[str, Set[date]] = {}
    for expiries in days.values():
        for underlying, expiry in expiries:
            if expiry <= last_day and expiry not in set(confirmed.get(underlying, [])):
                seen.setdefault(underlying, set()).add(expiry)
    return {u: sorted(d) for u, d in sorted(seen.items())}


# -------------------------------------------------------------- trading days --

def trading_days(index: str, start: date, end: date) -> List[date]:
    """IST session dates in the D0 5-minute index candle files."""
    folder = os.path.join(DATA_DIR, "index", "candles-5m", "raw", index)
    days: Set[date] = set()
    for file in sorted(os.listdir(folder)):
        if not file.endswith(".csv.gz"):
            continue
        with gzip.open(os.path.join(folder, file), "rt") as handle:
            for row in csv.DictReader(handle):
                d = date.fromisoformat(row["bar_start_ist"][:10])
                if start <= d <= end:
                    days.add(d)
    return sorted(days)


# ------------------------------------------------------------------- fetch --

def http_fetch(url: str) -> Tuple[int, bytes]:
    import requests

    referer = "https://www.bseindia.com/" if "bseindia" in url else "https://www.nseindia.com/"
    response = requests.get(url, headers={"User-Agent": USER_AGENT, "Referer": referer}, timeout=60)
    return response.status_code, response.content


def expiries_on(day: date, exchange: str, fetch: Fetch, pause: float) -> Tuple[Optional[Set[Expiry]], str]:
    urls = nse_urls(day) if exchange == "NSE" else bse_urls(day)
    notes = []
    for url in urls:
        for attempt in range(3):
            time.sleep(pause * (attempt + 1))
            try:
                code, body = fetch(url)
            except Exception as ex:
                notes.append(f"{type(ex).__name__}")
                continue
            if code == 200 and body and not body.lstrip()[:1] == b"<":
                try:
                    return parse_expiries(body, exchange), url
                except (ValueError, KeyError, StopIteration, zipfile.BadZipFile, csv.Error) as ex:
                    notes.append(f"unreadable ({type(ex).__name__})")
                    break
            notes.append(f"HTTP {code}")
            if code == 404:
                break
    return None, "; ".join(notes)


def run(exchange: str, start: date, end: date, cache_dir: str, fetch: Fetch = http_fetch, pause: float = 0.3,
        log=print) -> Dict[date, Set[Expiry]]:
    index = "NIFTY" if exchange == "NSE" else "SENSEX"
    cache_path = os.path.join(cache_dir, f"{exchange.lower()}-days.jsonl")
    cached: Dict[date, Set[Expiry]] = {}
    if os.path.exists(cache_path):
        for line in open(cache_path):
            rec = json.loads(line)
            cached[date.fromisoformat(rec["day"])] = {(u, date.fromisoformat(e)) for u, e in rec["expiries"]}
    days = trading_days(index, start, end)
    missing = [d for d in days if d not in cached]
    log(f"{exchange}: {len(days)} trading days, {len(cached)} cached, {len(missing)} to fetch")
    os.makedirs(cache_dir, exist_ok=True)
    with open(cache_path, "a") as out:
        for i, day in enumerate(missing, 1):
            expiries, source = expiries_on(day, exchange, fetch, pause)
            if expiries is None:
                log(f"  {day}: no bhavcopy ({source})")
                continue
            cached[day] = expiries
            out.write(json.dumps({"day": day.isoformat(), "source": source,
                                  "expiries": sorted([u, e.isoformat()] for u, e in expiries)}) + "\n")
            out.flush()
            if i % 100 == 0:
                log(f"  {exchange}: {i}/{len(missing)} fetched")
    return {d: e for d, e in cached.items() if start <= d <= end}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exchanges", default="NSE,BSE")
    ap.add_argument("--from", dest="start", default="2020-08-01")
    ap.add_argument("--to", dest="end", default=date.today().isoformat())
    ap.add_argument("--cache", default=os.path.join(DATA_DIR, "reference", "bhavcopy-expiries"))
    ap.add_argument("--out", default=os.path.join(DATA_DIR, "reference", "index-option-expiries.json"))
    args = ap.parse_args(argv)
    start, end = date.fromisoformat(args.start), date.fromisoformat(args.end)

    result: Dict[str, object] = {"generatedUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                                 "rule": "an expiry is a date whose own bhavcopy lists an index option expiring that day",
                                 "underlyings": {}, "unconfirmed": {}, "days": {}}
    for exchange in [x.strip().upper() for x in args.exchanges.split(",") if x.strip()]:
        days = run(exchange, start, end, args.cache)
        confirmed = calendar(days)
        last_day = max(days) if days else end
        result["days"][exchange] = {"first": min(days).isoformat() if days else None,
                                    "last": last_day.isoformat() if days else None, "count": len(days)}
        for underlying, dates in confirmed.items():
            result["underlyings"][underlying] = [d.isoformat() for d in dates]
            print(f"{underlying}: {len(dates)} expiries, {dates[0]} .. {dates[-1]}")
        for underlying, dates in unconfirmed(days, confirmed, last_day).items():
            result["unconfirmed"][underlying] = [d.isoformat() for d in dates]
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w") as handle:
        json.dump(result, handle, indent=1)
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
