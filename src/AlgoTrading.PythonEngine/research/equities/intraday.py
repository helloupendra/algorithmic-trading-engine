"""
research/equities/intraday.py

Dhan intraday bars for stock-picker research (data set D2 of the blueprint).

Facts this relies on, checked against the live API on 2026-09-15:
  * POST https://api.dhan.co/v2/charts/intraday with securityId,
    exchangeSegment "NSE_EQ", instrument "EQUITY", interval "5", fromDate,
    toDate answers parallel arrays open/high/low/close/volume/timestamp.
  * Timestamps are epoch seconds of each bar's START. A bar starting exactly on
    fromDate is left out, so a window asks from 09:00 of its first day.
  * 5-minute stock history reaches back to at least January 2020.
  * At most 90 days per request; data APIs allow 5 requests a second, per
    account (the production server shares the budget, so bulk downloads run
    after market hours).

Bars are kept per stock and window under `<root>/raw/<SYMBOL>/<from>_<to>.csv.gz`
and every request is appended to `<root>/manifest.jsonl`; a re-run fetches only
windows that are not ok yet. Credentials come from the environment
(DHAN_CLIENT_ID, DHAN_ACCESS_TOKEN) and are never logged.
"""

from __future__ import annotations

import csv
import gzip
import json
import os
import time
from dataclasses import asdict, dataclass
from datetime import date, datetime, timedelta, timezone
from typing import Callable, Dict, Iterable, List, Optional, Tuple

DEFAULT_ROOT = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "equities",
                            "intraday-5m")
URL = "https://api.dhan.co/v2/charts/intraday"
IST = timezone(timedelta(hours=5, minutes=30))
WINDOW_DAYS = 90
SPACING_SECONDS = 0.23       # a little under 5 requests a second
COLUMNS = ("bar_start_ist", "open", "high", "low", "close", "volume")


class AuthError(RuntimeError):
    """Dhan refused the credentials; nothing more can be fetched in this run."""


def windows(start: date, end: date, days: int = WINDOW_DAYS) -> List[Tuple[date, date]]:
    """[start, end] cut into inclusive windows of at most `days` calendar days."""
    out = []
    lo = start
    while lo <= end:
        hi = min(lo + timedelta(days=days - 1), end)
        out.append((lo, hi))
        lo = hi + timedelta(days=1)
    return out


def request_body(security_id: str, lo: date, hi: date, interval: str = "5") -> Dict[str, object]:
    return {"securityId": str(security_id), "exchangeSegment": "NSE_EQ", "instrument": "EQUITY",
            "interval": interval, "oi": False, "fromDate": f"{lo:%Y-%m-%d} 09:00:00",
            "toDate": f"{hi:%Y-%m-%d} 15:30:00"}


def parse_bars(payload: Dict[str, list], lo: date, hi: date) -> List[Dict[str, object]]:
    """Dhan's parallel arrays -> in-session rows (bar start 09:15..15:29 IST) whose date lies in [lo, hi]."""
    stamps = payload.get("timestamp") or []
    rows = []
    for i, stamp in enumerate(stamps):
        start = datetime.fromtimestamp(float(stamp), tz=timezone.utc).astimezone(IST)
        minute = start.hour * 60 + start.minute
        if not (lo <= start.date() <= hi) or not (555 <= minute <= 929):
            continue
        rows.append({"bar_start_ist": start.strftime("%Y-%m-%d %H:%M"), "open": payload["open"][i],
                     "high": payload["high"][i], "low": payload["low"][i], "close": payload["close"][i],
                     "volume": payload["volume"][i]})
    return rows


@dataclass
class WindowAttempt:
    symbol: str
    security_id: str
    window_from: str
    window_to: str
    status: str          # ok | empty | error
    http: Optional[int]
    rows: int
    detail: str
    fetched_utc: str


class Manifest:
    def __init__(self, root: str) -> None:
        self.path = os.path.join(root, "manifest.jsonl")
        self.latest: Dict[Tuple[str, str], WindowAttempt] = {}
        if os.path.exists(self.path):
            with open(self.path) as fh:
                for line in fh:
                    if line.strip():
                        a = WindowAttempt(**json.loads(line))
                        self.latest[(a.symbol, a.window_from)] = a

    def add(self, attempt: WindowAttempt) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "a") as fh:
            fh.write(json.dumps(asdict(attempt)) + "\n")
        self.latest[(attempt.symbol, attempt.window_from)] = attempt

    def done(self, symbol: str, lo: date, security_id: str) -> bool:
        """Finished for this symbol AND this Dhan id: a corrected mapping fetches the window again."""
        a = self.latest.get((symbol, lo.isoformat()))
        return a is not None and a.status in ("ok", "empty") and a.security_id == str(security_id)


Poster = Callable[[Dict[str, object]], Tuple[int, object]]


def dhan_poster(client_id: Optional[str] = None, token: Optional[str] = None, timeout: float = 30.0,
                url: str = URL) -> Poster:
    import requests

    client_id = client_id or os.environ.get("DHAN_CLIENT_ID")
    token = token or os.environ.get("DHAN_ACCESS_TOKEN")
    if not client_id or not token:
        raise AuthError("DHAN_CLIENT_ID and DHAN_ACCESS_TOKEN must be set in the environment")
    session = requests.Session()
    session.headers.update({"access-token": token, "client-id": client_id, "Content-Type": "application/json",
                            "Accept": "application/json"})

    def post(body: Dict[str, object]) -> Tuple[int, object]:
        response = session.post(url, json=body, timeout=timeout)
        try:
            return response.status_code, response.json()
        except ValueError:
            return response.status_code, response.text[:200]

    return post


def window_path(root: str, symbol: str, lo: date, hi: date) -> str:
    return os.path.join(root, "raw", symbol, f"{lo:%Y%m%d}_{hi:%Y%m%d}.csv.gz")


class Pacer:
    def __init__(self, spacing: float = SPACING_SECONDS, clock=time.monotonic, sleep=time.sleep) -> None:
        self.spacing, self.clock, self.sleep, self.last = spacing, clock, sleep, None

    def wait(self) -> None:
        if self.last is not None:
            gap = self.spacing - (self.clock() - self.last)
            if gap > 0:
                self.sleep(gap)
        self.last = self.clock()


def fetch_window(root: str, manifest: Manifest, symbol: str, security_id: str, lo: date, hi: date, post: Poster,
                 pacer: Pacer, attempts: int = 4, sleep=time.sleep) -> WindowAttempt:
    detail, status_code, payload = "", None, None
    for n in range(attempts):
        pacer.wait()
        try:
            status_code, payload = post(request_body(security_id, lo, hi))
        except Exception as ex:
            status_code, payload, detail = None, None, f"{type(ex).__name__}"
        if status_code in (401, 403):
            raise AuthError(f"Dhan answered {status_code}; the access token has probably expired")
        if status_code == 200 or (status_code is not None and 400 <= status_code < 500 and status_code != 429):
            break
        sleep(2 ** n)   # 429, 5xx or a network error: back off and retry
    now = datetime.now(timezone.utc).isoformat(timespec="seconds")
    if status_code == 200 and isinstance(payload, dict):
        rows = parse_bars(payload, lo, hi)
        if rows:
            path = window_path(root, symbol, lo, hi)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            tmp = path + ".part"
            with gzip.open(tmp, "wt", newline="") as fh:
                writer = csv.DictWriter(fh, fieldnames=COLUMNS)
                writer.writeheader()
                writer.writerows(rows)
            os.replace(tmp, path)
            attempt = WindowAttempt(symbol, str(security_id), lo.isoformat(), hi.isoformat(), "ok", 200, len(rows),
                                    "", now)
        else:
            attempt = WindowAttempt(symbol, str(security_id), lo.isoformat(), hi.isoformat(), "empty", 200, 0,
                                    "no bars in the window", now)
    else:
        text = json.dumps(payload)[:200] if isinstance(payload, (dict, list)) else str(payload)[:200]
        attempt = WindowAttempt(symbol, str(security_id), lo.isoformat(), hi.isoformat(), "error", status_code, 0,
                                detail or text, now)
    manifest.add(attempt)
    return attempt


def download(root: str, stocks: Iterable[Tuple[str, str]], start: date, end: date, post: Optional[Poster] = None,
             log: Callable[[str], None] = print, pacer: Optional[Pacer] = None,
             stop_at: Optional[Callable[[], bool]] = None) -> Dict[str, int]:
    """
    Every (symbol, Dhan security id) over [start, end]. Stops at the first
    authentication failure, or before the next request once `stop_at()` is true
    (a night run must end before the production server uses the same account).
    """
    post = post or dhan_poster()
    pacer = pacer or Pacer()
    manifest = Manifest(root)
    counts: Dict[str, int] = {"ok": 0, "empty": 0, "error": 0, "skipped": 0}
    stocks = list(stocks)
    for k, (symbol, security_id) in enumerate(stocks, 1):
        for lo, hi in windows(start, end):
            if manifest.done(symbol, lo, security_id) and (manifest.latest[(symbol, lo.isoformat())].status == "empty"
                                              or os.path.exists(window_path(root, symbol, lo, hi))):
                counts["skipped"] += 1
                continue
            if stop_at is not None and stop_at():
                log(f"stopped by the time limit at {symbol} {lo}")
                counts["stopped"] = 1
                return counts
            attempt = fetch_window(root, manifest, symbol, security_id, lo, hi, post, pacer)
            counts[attempt.status] += 1
            if attempt.status == "error":
                log(f"{symbol} {lo}..{hi} error http={attempt.http} {attempt.detail}")
        if k % 50 == 0 or k == len(stocks):
            log(f"{k}/{len(stocks)} stocks · " + " ".join(f"{s}={n}" for s, n in counts.items()))
    return counts
