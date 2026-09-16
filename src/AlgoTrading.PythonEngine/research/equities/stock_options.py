"""
research/equities/stock_options.py

Dhan option history for stocks (data set D3: the options of the day's movers).

Facts this relies on, checked against the live API on 2026-09-16:
  * POST https://api.dhan.co/v2/charts/rollingoption with exchangeSegment
    "NSE_FNO", instrument "OPTSTK", securityId = the stock's NSE_EQ security id,
    expiryFlag "MONTH", expiryCode 1 (the nearest monthly expiry; stock options
    have no weekly), strike "ATM", "ATM+3", "ATM-3" ... answers per-bar arrays
    open/high/low/close/iv/volume/oi/strike/spot for CALL or PUT. RELIANCE puts,
    March 2025, ATM-3 and ATM+3: 1,425 five-minute bars each.
  * The series rolls: `strike` is the contract that was ATM+offset at each bar,
    so a held contract is followed by its strike across offsets.
  * One request covers a calendar month (the endpoint's cap is 30 days per
    request for index options; a month of 5-minute bars answered in one call).
  * Far strikes of thinly traded stocks answer with gaps (PATANJALI ATM+10:
    243 bars in a month), which is why the plan stops at +/-3.

Files: `<root>/raw/<SYMBOL>/<YYYY-MM>/<CE|PE>_<offset>.csv.gz`, every request in
`<root>/manifest.jsonl`; a re-run fetches only series that are not finished.
Credentials come from the environment and are never logged.
"""

from __future__ import annotations

import csv
import gzip
import json
import os
import time
from dataclasses import asdict, dataclass
from datetime import date, datetime, timedelta, timezone
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple

from research.equities import intraday
from research.equities.intraday import AuthError, IST, Pacer, Poster

URL = "https://api.dhan.co/v2/charts/rollingoption"
DEFAULT_ROOT = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "equities",
                            "options-5m")
COLUMNS = ("bar_start_ist", "open", "high", "low", "close", "iv", "volume", "oi", "strike", "spot")
REQUIRED = ["open", "high", "low", "close", "iv", "volume", "strike", "oi", "spot"]
OFFSETS = tuple(range(-3, 4))
TYPES = ("CE", "PE")


@dataclass(frozen=True)
class Series:
    symbol: str
    security_id: str
    month: str          # YYYY-MM
    option_type: str    # CE | PE
    offset: int

    @property
    def key(self) -> Tuple[str, str, str, int]:
        return (self.symbol, self.month, self.option_type, self.offset)


def month_bounds(month: str) -> Tuple[date, date]:
    first = date.fromisoformat(month + "-01")
    nxt = date(first.year + (first.month == 12), first.month % 12 + 1, 1)
    return first, nxt - timedelta(days=1)


def strike_argument(offset: int) -> str:
    return "ATM" if offset == 0 else (f"ATM+{offset}" if offset > 0 else f"ATM{offset}")


def request_body(series: Series, interval: str = "5") -> Dict[str, object]:
    lo, hi = month_bounds(series.month)
    return {"exchangeSegment": "NSE_FNO", "interval": interval, "securityId": int(series.security_id),
            "instrument": "OPTSTK", "expiryFlag": "MONTH", "expiryCode": 1, "strike": strike_argument(series.offset),
            "drvOptionType": "CALL" if series.option_type == "CE" else "PUT", "requiredData": REQUIRED,
            "fromDate": lo.isoformat(), "toDate": hi.isoformat()}


def parse(payload: dict, series: Series) -> List[Dict[str, object]]:
    """The CE or PE block -> in-session rows (09:15-15:29 IST) inside the series' month."""
    data = (payload or {}).get("data") or {}
    side = data.get("ce" if series.option_type == "CE" else "pe") or {}
    stamps = side.get("timestamp") or []
    lo, hi = month_bounds(series.month)
    rows = []
    for i, stamp in enumerate(stamps):
        start = datetime.fromtimestamp(float(stamp), tz=timezone.utc).astimezone(IST)
        minute = start.hour * 60 + start.minute
        if not (lo <= start.date() <= hi) or not (555 <= minute <= 929):
            continue
        row = {"bar_start_ist": start.strftime("%Y-%m-%d %H:%M")}
        for col in COLUMNS[1:]:
            values = side.get(col) or []
            row[col] = values[i] if i < len(values) else None
        rows.append(row)
    return rows


def dhan_poster(timeout: float = 60.0) -> Poster:
    """A poster bound to the rolling option endpoint (intraday.dhan_poster's default is the stock bars one)."""
    return intraday.dhan_poster(timeout=timeout, url=URL)


def series_path(root: str, series: Series) -> str:
    return os.path.join(root, "raw", series.symbol, series.month, f"{series.option_type}_{series.offset:+d}.csv.gz")


@dataclass
class Attempt:
    symbol: str
    security_id: str
    month: str
    option_type: str
    offset: int
    status: str          # ok | empty | error
    http: Optional[int]
    rows: int
    detail: str
    fetched_utc: str


class Manifest:
    def __init__(self, root: str) -> None:
        self.path = os.path.join(root, "manifest.jsonl")
        self.latest: Dict[Tuple[str, str, str, int], Attempt] = {}
        if os.path.exists(self.path):
            with open(self.path) as fh:
                for line in fh:
                    if line.strip():
                        a = Attempt(**json.loads(line))
                        self.latest[(a.symbol, a.month, a.option_type, a.offset)] = a

    def add(self, attempt: Attempt) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "a") as fh:
            fh.write(json.dumps(asdict(attempt)) + "\n")
        self.latest[(attempt.symbol, attempt.month, attempt.option_type, attempt.offset)] = attempt

    def done(self, series: Series, root: str) -> bool:
        a = self.latest.get(series.key)
        if a is None or a.security_id != str(series.security_id):
            return False
        return a.status == "empty" or (a.status == "ok" and os.path.exists(series_path(root, series)))


def plan(stock_months: Iterable[Tuple[str, str, str]], offsets: Sequence[int] = OFFSETS,
         types: Sequence[str] = TYPES) -> List[Series]:
    """(symbol, security id, YYYY-MM) -> every series, ordered month by month so an interrupted run is usable."""
    seen, out = set(), []
    for symbol, security_id, month in sorted(set(stock_months), key=lambda x: (x[2], x[0])):
        for option_type in types:
            for offset in offsets:
                s = Series(symbol, str(security_id), month, option_type, offset)
                if s.key not in seen:
                    seen.add(s.key)
                    out.append(s)
    return out


def fetch(root: str, manifest: Manifest, series: Series, post: Poster, pacer: Pacer, attempts: int = 4,
          sleep: Callable[[float], None] = time.sleep) -> Attempt:
    detail, status_code, payload = "", None, None
    for n in range(attempts):
        pacer.wait()
        try:
            status_code, payload = post(request_body(series))
        except Exception as ex:
            status_code, payload, detail = None, None, type(ex).__name__
        if status_code in (401, 403):
            raise AuthError(f"Dhan answered {status_code}; the access token has probably expired")
        if status_code == 200 or (status_code is not None and 400 <= status_code < 500 and status_code != 429):
            break
        sleep(2 ** n)
    now = datetime.now(timezone.utc).isoformat(timespec="seconds")
    base = dict(symbol=series.symbol, security_id=str(series.security_id), month=series.month,
                option_type=series.option_type, offset=series.offset, fetched_utc=now)
    if status_code == 200 and isinstance(payload, dict):
        rows = parse(payload, series)
        if rows:
            path = series_path(root, series)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            tmp = path + ".part"
            with gzip.open(tmp, "wt", newline="") as fh:
                writer = csv.DictWriter(fh, fieldnames=COLUMNS)
                writer.writeheader()
                writer.writerows(rows)
            os.replace(tmp, path)
            attempt = Attempt(status="ok", http=200, rows=len(rows), detail="", **base)
        else:
            attempt = Attempt(status="empty", http=200, rows=0, detail="no bars in the month", **base)
    else:
        text = json.dumps(payload)[:200] if isinstance(payload, (dict, list)) else str(payload)[:200]
        attempt = Attempt(status="error", http=status_code, rows=0, detail=detail or text, **base)
    manifest.add(attempt)
    return attempt


def download(root: str, series_list: Sequence[Series], post: Poster, pacer: Optional[Pacer] = None,
             stop_at: Optional[Callable[[], bool]] = None, log: Callable[[str], None] = print) -> Dict[str, int]:
    """Every series not yet finished. Stops at an authentication failure, or when `stop_at()` says so."""
    pacer = pacer or Pacer(spacing=0.5)
    manifest = Manifest(root)
    counts = {"ok": 0, "empty": 0, "error": 0, "skipped": 0}
    for k, s in enumerate(series_list, 1):
        if stop_at is not None and stop_at():
            log(f"stopped by the time limit after {k - 1}/{len(series_list)} series")
            counts["stopped"] = 1
            break
        if manifest.done(s, root):
            counts["skipped"] += 1
        else:
            a = fetch(root, manifest, s, post, pacer)
            counts[a.status] += 1
            if a.status == "error":
                log(f"{s.symbol} {s.month} {s.option_type}{s.offset:+d} error http={a.http} {a.detail}")
        if k % 200 == 0 or k == len(series_list):
            log(f"{k}/{len(series_list)} series · " + " ".join(f"{n}={v}" for n, v in counts.items()))
    return counts
