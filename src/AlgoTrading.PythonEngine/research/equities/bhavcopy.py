"""
research/equities/bhavcopy.py

Download, verify and normalise the exchanges' daily bhavcopies: one file per
session with every listed security's OHLC, previous close, volume, turnover
and trades.

Sources (all checked against the live archives on 2026-09-15):

  nse_udiff     nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_YYYYMMDD_F_0000.csv.zip
                NSE's UDiFF format, published from July 2024.
  nse_legacy    archives.nseindia.com/content/historical/EQUITIES/YYYY/MON/cmDDMONYYYYbhav.csv.zip
                The older format; the last file is 05 Jul 2024.
  nse_delivery  nsearchives.nseindia.com/products/content/sec_bhavdata_full_DDMMYYYY.csv
                Delivery quantity and delivery % per symbol and series.
  bse_udiff     www.bseindia.com/download/BhavCopy/Equity/BhavCopy_BSE_CM_0_0_0_YYYYMMDD_F_0000.CSV
  bse_legacy    www.bseindia.com/download/BhavCopy/Equity/EQ_ISINCODE_DDMMYY.zip

A date with no file (a holiday, a weekend, or a file the archive does not
have) answers 404 at NSE, and 200 with an HTML page at BSE; both count as
missing. The history of holidays before 2026 is not
in the database, so a session is inferred: when neither NSE nor BSE has a file
the day was closed; when only one has it, that is a data gap and the coverage
report lists it.

Raw files are kept exactly as downloaded (they are the source of truth) under
`<root>/raw/<source>/<year>/`, and every attempt is appended to
`<root>/manifest.jsonl`. Normalised month files are rebuilt from the raw files.
"""

from __future__ import annotations

import csv
import gzip
import hashlib
import io
import json
import os
import time
import zipfile
from dataclasses import asdict, dataclass
from datetime import date, datetime, timedelta, timezone
from typing import Callable, Dict, Iterable, Iterator, List, Optional, Sequence, Tuple

DEFAULT_ROOT = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "equities", "eod")

#: First session published in the UDiFF format by both exchanges is taken as
#: this date; before it the legacy file is tried first, after it the UDiFF one.
UDIFF_FROM = date(2024, 7, 8)

#: A bhavcopy with fewer rows than this is not a real session file (BSE's
#: UDiFF file for 01 Sep 2023 exists but holds a handful of rows).
MIN_ROWS = {"nse": 500, "bse": 1000}

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) "
                  "Chrome/124.0 Safari/537.36",
    "Referer": "https://www.nseindia.com/",
    "Accept": "*/*",
}

MONTHS = ("JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC")

#: Normalised columns, in file order.
COLUMNS = ("trade_date", "exchange", "symbol", "series", "isin", "security_id", "name", "open", "high", "low",
           "close", "last", "prev_close", "volume", "turnover", "trades", "delivery_qty", "delivery_pct")


# ------------------------------------------------------------------ urls --

def url_for(source: str, day: date) -> str:
    if source == "nse_udiff":
        return f"https://nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_{day:%Y%m%d}_F_0000.csv.zip"
    if source == "nse_legacy":
        mon = MONTHS[day.month - 1]
        return (f"https://archives.nseindia.com/content/historical/EQUITIES/{day.year}/{mon}/"
                f"cm{day.day:02d}{mon}{day.year}bhav.csv.zip")
    if source == "nse_delivery":
        return f"https://nsearchives.nseindia.com/products/content/sec_bhavdata_full_{day:%d%m%Y}.csv"
    if source == "bse_udiff":
        return f"https://www.bseindia.com/download/BhavCopy/Equity/BhavCopy_BSE_CM_0_0_0_{day:%Y%m%d}_F_0000.CSV"
    if source == "bse_legacy":
        return f"https://www.bseindia.com/download/BhavCopy/Equity/EQ_ISINCODE_{day:%d%m%y}.zip"
    raise ValueError(f"unknown source {source!r}")


def bhavcopy_sources(exchange: str, day: date) -> Tuple[str, str]:
    """The two bhavcopy sources for an exchange, the likelier one first."""
    ex = exchange.lower()
    if ex not in ("nse", "bse"):
        raise ValueError(f"unknown exchange {exchange!r}")
    udiff, legacy = f"{ex}_udiff", f"{ex}_legacy"
    return (udiff, legacy) if day >= UDIFF_FROM else (legacy, udiff)


def raw_path(root: str, source: str, day: date) -> str:
    name = url_for(source, day).rsplit("/", 1)[1]
    return os.path.join(root, "raw", source, str(day.year), name)


# --------------------------------------------------------------- parsing --

class NotABhavcopy(ValueError):
    """The bytes are not the file the source should serve (an HTML error page, a bad zip)."""


def _text(content: bytes, source: str) -> str:
    if content[:2] == b"PK":
        with zipfile.ZipFile(io.BytesIO(content)) as z:
            names = [n for n in z.namelist() if not n.endswith("/")]
            if not names:
                raise NotABhavcopy(f"{source}: empty zip")
            content = z.read(names[0])
    elif url_for(source, date(2024, 1, 1)).endswith(".zip"):
        raise NotABhavcopy(f"{source}: expected a zip, got {content[:15]!r}")
    text = content.decode("utf-8-sig", errors="replace")
    if text.lstrip()[:1] == "<":
        raise NotABhavcopy(f"{source}: got an HTML page, not a CSV")
    return text


def _rows(text: str) -> Iterator[Dict[str, str]]:
    reader = csv.reader(io.StringIO(text))
    header = None
    for raw in reader:
        if not raw or not any(cell.strip() for cell in raw):
            continue
        cells = [c.strip() for c in raw]
        if header is None:
            header = cells
            continue
        yield dict(zip(header, cells))


def _num(value: Optional[str]) -> Optional[float]:
    if value is None:
        return None
    v = value.strip().replace(",", "")
    if v in ("", "-", "NA", "nan"):
        return None
    try:
        return float(v)
    except ValueError:
        return None


def _day(value: str) -> date:
    v = value.strip()
    for fmt in ("%Y-%m-%d", "%d-%b-%Y", "%d-%b-%y", "%d-%m-%Y"):
        try:
            return datetime.strptime(v.title() if "%b" in fmt else v, fmt).date()
        except ValueError:
            continue
    raise ValueError(f"unrecognised date {value!r}")


def _row(**kw) -> Dict[str, object]:
    return {c: kw.get(c) for c in COLUMNS}


def parse_udiff(text: str, exchange: str) -> List[Dict[str, object]]:
    """
    NSE or BSE UDiFF bhavcopy -> normalised rows. Every cash-market row is
    kept: both exchanges tag stocks, ETFs, bonds and G-secs alike as STK, so
    the series (NSE) or group (BSE) is what tells them apart downstream.
    """
    out = []
    for r in _rows(text):
        if "TradDt" not in r:
            raise NotABhavcopy("udiff: no TradDt column")
        if r.get("Sgmt", "CM") != "CM":
            continue
        out.append(_row(
            trade_date=_day(r["TradDt"]).isoformat(), exchange=exchange.upper(), symbol=r.get("TckrSymb", ""),
            series=r.get("SctySrs", ""), isin=r.get("ISIN", ""), security_id=r.get("FinInstrmId", ""),
            name=r.get("FinInstrmNm", ""), open=_num(r.get("OpnPric")), high=_num(r.get("HghPric")),
            low=_num(r.get("LwPric")), close=_num(r.get("ClsPric")), last=_num(r.get("LastPric")),
            prev_close=_num(r.get("PrvsClsgPric")), volume=_num(r.get("TtlTradgVol")),
            turnover=_num(r.get("TtlTrfVal")), trades=_num(r.get("TtlNbOfTxsExctd")),
        ))
    return out


def parse_nse_legacy(text: str) -> List[Dict[str, object]]:
    out = []
    for r in _rows(text):
        if "SYMBOL" not in r:
            raise NotABhavcopy("nse_legacy: no SYMBOL column")
        out.append(_row(
            trade_date=_day(r["TIMESTAMP"]).isoformat(), exchange="NSE", symbol=r["SYMBOL"], series=r["SERIES"],
            isin=r.get("ISIN", ""), security_id="", name="", open=_num(r["OPEN"]), high=_num(r["HIGH"]),
            low=_num(r["LOW"]), close=_num(r["CLOSE"]), last=_num(r.get("LAST")), prev_close=_num(r["PREVCLOSE"]),
            volume=_num(r["TOTTRDQTY"]), turnover=_num(r["TOTTRDVAL"]), trades=_num(r.get("TOTALTRADES")),
        ))
    return out


def parse_bse_legacy(text: str) -> List[Dict[str, object]]:
    out = []
    for r in _rows(text):
        if "SC_CODE" not in r:
            raise NotABhavcopy("bse_legacy: no SC_CODE column")
        out.append(_row(
            trade_date=_day(r["TRADING_DATE"]).isoformat(), exchange="BSE", symbol="", series=r["SC_GROUP"],
            isin=r.get("ISIN_CODE", ""), security_id=r["SC_CODE"], name=r.get("SC_NAME", ""), open=_num(r["OPEN"]),
            high=_num(r["HIGH"]), low=_num(r["LOW"]), close=_num(r["CLOSE"]), last=_num(r.get("LAST")),
            prev_close=_num(r["PREVCLOSE"]), volume=_num(r["NO_OF_SHRS"]), turnover=_num(r["NET_TURNOV"]),
            trades=_num(r.get("NO_TRADES")),
        ))
    return out


def parse_nse_delivery(text: str) -> Dict[Tuple[str, str], Tuple[Optional[float], Optional[float]]]:
    """(symbol, series) -> (delivery quantity, delivery %). Non-EQ series carry '-' and map to None."""
    return {key: (qty, pct) for key, (_, qty, pct) in _delivery_rows(text).items()}


def _delivery_rows(text: str) -> Dict[Tuple[str, str], Tuple[str, Optional[float], Optional[float]]]:
    out = {}
    for r in _rows(text):
        if "SYMBOL" not in r or "DELIV_PER" not in r:
            raise NotABhavcopy("nse_delivery: no SYMBOL/DELIV_PER column")
        out[(r["SYMBOL"], r["SERIES"])] = (_day(r["DATE1"]).isoformat(), _num(r.get("DELIV_QTY")),
                                           _num(r.get("DELIV_PER")))
    return out


def parse(source: str, content: bytes):
    text = _text(content, source)
    if source == "nse_udiff":
        return parse_udiff(text, "NSE")
    if source == "bse_udiff":
        return parse_udiff(text, "BSE")
    if source == "nse_legacy":
        return parse_nse_legacy(text)
    if source == "bse_legacy":
        return parse_bse_legacy(text)
    if source == "nse_delivery":
        return parse_nse_delivery(text)
    raise ValueError(f"unknown source {source!r}")


def check(source: str, content: bytes, day: date) -> int:
    """Row count of a verified file; raises NotABhavcopy when it is not a real session file for `day`."""
    if source == "nse_delivery":
        rows = _delivery_rows(_text(content, source))
        dates = {d for d, _, _ in rows.values()}
        minimum = MIN_ROWS["nse"]
    else:
        parsed = parse(source, content)
        rows = parsed
        dates = {r["trade_date"] for r in parsed}
        minimum = MIN_ROWS[source.split("_")[0]]
    if len(rows) < minimum:
        raise NotABhavcopy(f"{source}: only {len(rows)} rows for {day}")
    if dates != {day.isoformat()}:
        raise NotABhavcopy(f"{source}: file is dated {sorted(dates)[:3]}, asked for {day}")
    return len(rows)


# -------------------------------------------------------------- download --

@dataclass
class Attempt:
    day: str
    source: str
    status: str            # ok | missing | invalid | error
    http: Optional[int]
    rows: int
    bytes: int
    sha256: str
    detail: str
    fetched_utc: str


class Manifest:
    """Append-only record of every attempt; the latest line per (day, source) wins."""

    def __init__(self, root: str) -> None:
        self.path = os.path.join(root, "manifest.jsonl")
        self.latest: Dict[Tuple[str, str], Attempt] = {}
        if os.path.exists(self.path):
            with open(self.path) as fh:
                for line in fh:
                    if line.strip():
                        a = Attempt(**json.loads(line))
                        self.latest[(a.day, a.source)] = a

    def add(self, attempt: Attempt) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "a") as fh:
            fh.write(json.dumps(asdict(attempt)) + "\n")
        self.latest[(attempt.day, attempt.source)] = attempt

    def status(self, day: date, source: str) -> Optional[str]:
        a = self.latest.get((day.isoformat(), source))
        return a.status if a else None


Fetcher = Callable[[str], Tuple[int, bytes]]


def http_fetcher(timeout: float = 30.0) -> Fetcher:
    import requests

    session = requests.Session()
    session.headers.update(HEADERS)

    def fetch(url: str) -> Tuple[int, bytes]:
        response = session.get(url, timeout=timeout)
        return response.status_code, response.content

    return fetch


def fetch_one(root: str, manifest: Manifest, source: str, day: date, fetch: Fetcher, attempts: int = 3,
              pause: float = 0.4, sleep: Callable[[float], None] = time.sleep) -> Attempt:
    """
    Download, verify and keep one file. A file already verified in the manifest
    and present on disk is not fetched again; a 404 is final (no retry); network
    errors and 5xx answers are retried with a growing wait.
    """
    path = raw_path(root, source, day)
    if manifest.status(day, source) == "ok" and os.path.exists(path):
        return manifest.latest[(day.isoformat(), source)]
    url = url_for(source, day)
    detail, http, content = "", None, b""
    for n in range(attempts):
        try:
            http, content = fetch(url)
        except Exception as ex:  # network trouble: retry
            detail, http, content = f"{type(ex).__name__}: {ex}"[:200], None, b""
        sleep(pause)
        if http is not None and http < 500 and http != 429:
            break
        sleep(pause * (4 ** (n + 1)))
    now = datetime.now(timezone.utc).isoformat(timespec="seconds")
    if http == 404:
        attempt = Attempt(day.isoformat(), source, "missing", http, 0, len(content), "", "404", now)
    elif http == 200 and content.lstrip()[:1] == b"<":
        # BSE answers a date with no file (a holiday) with 200 and an HTML page.
        attempt = Attempt(day.isoformat(), source, "missing", http, 0, len(content), "", "HTML page, no file", now)
    elif http != 200:
        attempt = Attempt(day.isoformat(), source, "error", http, 0, len(content), "", detail or f"HTTP {http}", now)
    else:
        try:
            rows = check(source, content, day)
        except (NotABhavcopy, ValueError, KeyError, zipfile.BadZipFile) as ex:
            attempt = Attempt(day.isoformat(), source, "invalid", http, 0, len(content), "", str(ex)[:200], now)
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            tmp = path + ".part"
            with open(tmp, "wb") as fh:
                fh.write(content)
            os.replace(tmp, path)
            attempt = Attempt(day.isoformat(), source, "ok", http, rows, len(content),
                              hashlib.sha256(content).hexdigest(), "", now)
    manifest.add(attempt)
    return attempt


def plan_day(day: date, results: Dict[str, str]) -> List[str]:
    """
    The next source to try for `day`, given the statuses so far (source ->
    status). An empty list means the day is finished.

      - NSE: the likelier bhavcopy format, then the other if the first is not
        ok. On a weekend a 404 from the likelier format ends it: weekend
        sessions (Muhurat, budget Saturdays) are rare and never at a format
        switch.
      - NSE delivery: only when an NSE bhavcopy is ok.
      - BSE: weekdays always (it is what tells a holiday from a gap); weekends
        only when NSE had a session.
    """
    weekend = day.weekday() >= 5
    for exchange in ("nse", "bse"):
        first, second = bhavcopy_sources(exchange, day)
        if exchange == "bse" and weekend and not _nse_ok(day, results):
            continue
        if results.get(first) == "ok" or results.get(second) == "ok":
            if exchange == "nse" and "nse_delivery" not in results:
                return ["nse_delivery"]
            continue
        if first not in results:
            return [first]
        if second not in results and not (weekend and results.get(first) == "missing"):
            return [second]
    return []


def _nse_ok(day: date, results: Dict[str, str]) -> bool:
    return any(results.get(s) == "ok" for s in bhavcopy_sources("nse", day))


def download(root: str, start: date, end: date, fetch: Optional[Fetcher] = None,
             log: Callable[[str], None] = print, retry_errors: bool = True,
             retry_missing_from: Optional[date] = None) -> Manifest:
    """
    Every calendar day in [start, end]: NSE bhavcopy + delivery, BSE bhavcopy.

    A 404 is normally final. `retry_missing_from` makes it provisional for days
    on or after that date: a daily job that runs before the exchange publishes
    the evening's file must be able to ask again the next time.
    """
    fetch = fetch or http_fetcher()
    manifest = Manifest(root)
    day = start
    while day <= end:
        results: Dict[str, str] = {}
        for source in ("nse_udiff", "nse_legacy", "nse_delivery", "bse_udiff", "bse_legacy"):
            status = manifest.status(day, source)
            # A failed or invalid attempt is tried again on the next run: both
            # can be transient (a timeout, a truncated file).
            if status == "missing" and retry_missing_from is not None and day >= retry_missing_from:
                continue
            if status in ("ok", "missing") or (status in ("error", "invalid") and not retry_errors):
                if status != "ok" or os.path.exists(raw_path(root, source, day)):
                    results[source] = status
        while True:
            todo = plan_day(day, results)
            if not todo:
                break
            source = todo[0]
            attempt = fetch_one(root, manifest, source, day, fetch)
            results[source] = attempt.status
            if attempt.status in ("ok", "invalid", "error"):
                log(f"{day} {source:12} {attempt.status:7} rows={attempt.rows} {attempt.detail}")
        day += timedelta(days=1)
    return manifest


# ------------------------------------------------------------- normalise --

def session_rows(root: str, manifest: Manifest, day: date) -> Dict[str, List[Dict[str, object]]]:
    """Normalised rows per exchange for one day, from the raw files the manifest marks ok."""
    out: Dict[str, List[Dict[str, object]]] = {}
    for exchange in ("nse", "bse"):
        for source in bhavcopy_sources(exchange, day):
            if manifest.status(day, source) != "ok":
                continue
            with open(raw_path(root, source, day), "rb") as fh:
                rows = parse(source, fh.read())
            if exchange == "nse" and manifest.status(day, "nse_delivery") == "ok":
                with open(raw_path(root, "nse_delivery", day), "rb") as fh:
                    delivery = parse("nse_delivery", fh.read())
                for r in rows:
                    qty, pct = delivery.get((r["symbol"], r["series"]), (None, None))
                    r["delivery_qty"], r["delivery_pct"] = qty, pct
            out[exchange] = rows
            break
    return out


def normalise(root: str, start: date, end: date, log: Callable[[str], None] = print) -> List[str]:
    """Rebuild `<root>/normalized/<exchange>/<YYYY-MM>.csv.gz` for every month touching [start, end]."""
    manifest = Manifest(root)
    written = []
    month = date(start.year, start.month, 1)
    while month <= end:
        nxt = date(month.year + (month.month == 12), month.month % 12 + 1, 1)
        buckets: Dict[str, List[Dict[str, object]]] = {"nse": [], "bse": []}
        day = month
        while day < nxt:
            for exchange, rows in session_rows(root, manifest, day).items():
                buckets[exchange].extend(rows)
            day += timedelta(days=1)
        for exchange, rows in buckets.items():
            if not rows:
                continue
            path = os.path.join(root, "normalized", exchange, f"{month:%Y-%m}.csv.gz")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            tmp = path + ".part"
            with gzip.open(tmp, "wt", newline="") as fh:
                writer = csv.DictWriter(fh, fieldnames=COLUMNS)
                writer.writeheader()
                writer.writerows(rows)
            os.replace(tmp, path)
            written.append(path)
            log(f"{path}: {len(rows):,} rows")
        month = nxt
    return written


# -------------------------------------------------------------- coverage --

@dataclass
class DayCoverage:
    day: str
    nse: bool
    bse: bool
    delivery: bool
    verdict: str   # session | closed | gap: <what> | not tried


def coverage(root: str, start: date, end: date) -> List[DayCoverage]:
    manifest = Manifest(root)
    out = []
    day = start
    while day <= end:
        out.append(day_coverage(day, {s: manifest.status(day, s) for s in
                                      ("nse_udiff", "nse_legacy", "nse_delivery", "bse_udiff", "bse_legacy")}))
        day += timedelta(days=1)
    return out


def day_coverage(day: date, statuses: Dict[str, Optional[str]]) -> DayCoverage:
    """
    The verdict for one day from its source statuses.

      session   NSE and BSE bhavcopies and NSE delivery are all ok
      closed    every bhavcopy tried answered 404 (holiday or weekend)
      gap: ...  something one would expect is absent or failed, named
    """
    nse = any(statuses.get(s) == "ok" for s in ("nse_udiff", "nse_legacy"))
    bse = any(statuses.get(s) == "ok" for s in ("bse_udiff", "bse_legacy"))
    delivery = statuses.get("nse_delivery") == "ok"
    failed = {s for s, v in statuses.items() if v in ("error", "invalid")}
    if not any(v is not None for v in statuses.values()):
        verdict = "not tried"
    elif nse and bse:
        verdict = "session" if delivery else "gap: nse_delivery"
    elif not nse and not bse:
        verdict = "gap: " + ",".join(sorted(failed)) if failed else "closed"
    else:
        missing = "bse" if nse else "nse"
        verdict = f"gap: {missing}" + (f" ({','.join(sorted(failed))})" if failed else "")
    return DayCoverage(day.isoformat(), nse, bse, delivery, verdict)
