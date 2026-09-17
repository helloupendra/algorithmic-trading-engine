"""
research/index_history.py

Long history for the indices, as files (data set D0): index candles and index
option premiums from Dhan, from August 2020 to today, so research is not limited
to one market regime.

Facts this relies on, checked against the live API on 2026-09-16:
  * Index candles: POST /v2/charts/intraday with exchangeSegment "IDX_I",
    instrument "INDEX", securityId NIFTY 13, BANKNIFTY 25, FINNIFTY 27,
    MIDCPNIFTY 442, SENSEX 51, INDIA VIX 21. NIFTY answered 5-minute (1,650 bars)
    and 1-minute (8,250 bars) for September 2020. SENSEX and INDIA VIX answered
    March 2025 but nothing for September 2020: their history starts later, and an
    empty window is recorded as such.
  * At most 90 days per intraday request.
  * Index options: POST /v2/charts/rollingoption, instrument "OPTIDX", securityId
    = the index id, exchangeSegment NSE_FNO (BSE_FNO for SENSEX), expiryFlag
    "WEEK", expiryCode 1 = the nearest expiry. NIFTY ATM CE, September 2020,
    1-minute: 8,249 bars. ATM-10..ATM+10 answer for indices; this data set keeps
    ATM-5..ATM+5, the width of the existing database copy, and NIFTY from Aug 2021
    also ATM-10..-6 and +6..+10 (added 17 Sep 2026 so hedged option spreads can be priced).

Files:
  <root>/candles-<n>m/raw/<INDEX>/<from>_<to>.csv.gz     (+ manifest.jsonl)
  <root>/options-1m/raw/<UNDERLYING>/<YYYY-MM>/<CE|PE>_<offset>.csv.gz (+ manifest.jsonl)
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

from research.equities.intraday import AuthError, IST, Pacer, Poster, windows

ROOT = os.path.join(os.path.expanduser(os.getenv("OPENFNO_DATA_DIR", "~/OpenFNO-data")), "index")
CANDLES_URL = "https://api.dhan.co/v2/charts/intraday"
OPTIONS_URL = "https://api.dhan.co/v2/charts/rollingoption"

INDICES = {                      # name -> (IDX_I security id, option segment or None)
    "NIFTY": ("13", "NSE_FNO"),
    "BANKNIFTY": ("25", "NSE_FNO"),
    "FINNIFTY": ("27", "NSE_FNO"),
    "MIDCPNIFTY": ("442", "NSE_FNO"),
    "SENSEX": ("51", "BSE_FNO"),
    "INDIAVIX": ("21", None),
}
CANDLE_COLUMNS = ("bar_start_ist", "open", "high", "low", "close", "volume")
OPTION_COLUMNS = ("bar_start_ist", "open", "high", "low", "close", "iv", "volume", "oi", "strike", "spot")
REQUIRED = ["open", "high", "low", "close", "iv", "volume", "strike", "oi", "spot"]


@dataclass(frozen=True)
class Job:
    kind: str            # "candles" | "options"
    name: str            # index / underlying
    security_id: str
    resolution: str      # "1" | "5"
    lo: date
    hi: date
    option_type: str = ""
    offset: int = 0

    @property
    def key(self) -> Tuple:
        return (self.kind, self.name, self.resolution, self.lo.isoformat(), self.option_type, self.offset)


def month_windows(start: date, end: date) -> List[Tuple[date, date]]:
    out, first = [], date(start.year, start.month, 1)
    while first <= end:
        nxt = date(first.year + (first.month == 12), first.month % 12 + 1, 1)
        out.append((max(first, start), min(nxt - timedelta(days=1), end)))
        first = nxt
    return out


def candle_jobs(names: Sequence[str], resolutions: Sequence[str], start: date, end: date) -> List[Job]:
    return [Job("candles", n, INDICES[n][0], r, lo, hi) for n in names for r in resolutions for lo, hi in windows(start, end)]


def parse_offsets(text: str) -> List[int]:
    """'-10:-6,6:10' -> [-10, -9, -8, -7, -6, 6, 7, 8, 9, 10]; ranges are inclusive. Dhan answers ATM-10..ATM+10."""
    out: List[int] = []
    for part in (p.strip() for p in text.split(",") if p.strip()):
        lo, _, hi = part.partition(":")
        a, b = int(lo), int(hi or lo)
        out += range(min(a, b), max(a, b) + 1)
    if any(abs(o) > 10 for o in out):
        raise ValueError(f"offsets must lie within ATM-10..ATM+10: {text}")
    return sorted(set(out))


def option_jobs(names: Sequence[str], start: date, end: date, offsets: Sequence[int] = tuple(range(-5, 6))) -> List[Job]:
    jobs = []
    for lo, hi in month_windows(start, end):              # month by month, so a stopped run is usable
        for n in names:
            for t in ("CE", "PE"):
                for o in offsets:
                    jobs.append(Job("options", n, INDICES[n][0], "1", lo, hi, t, o))
    return jobs


def request(job: Job) -> Tuple[str, Dict[str, object]]:
    if job.kind == "candles":
        return CANDLES_URL, {"securityId": job.security_id, "exchangeSegment": "IDX_I", "instrument": "INDEX",
                             "interval": job.resolution, "oi": False, "fromDate": f"{job.lo:%Y-%m-%d} 09:00:00",
                             "toDate": f"{job.hi:%Y-%m-%d} 15:30:00"}
    segment = INDICES[job.name][1]
    if segment is None:
        raise ValueError(f"{job.name} has no options")
    strike = "ATM" if job.offset == 0 else (f"ATM+{job.offset}" if job.offset > 0 else f"ATM{job.offset}")
    return OPTIONS_URL, {"exchangeSegment": segment, "interval": job.resolution, "securityId": int(job.security_id),
                         "instrument": "OPTIDX", "expiryFlag": "WEEK", "expiryCode": 1, "strike": strike,
                         "drvOptionType": "CALL" if job.option_type == "CE" else "PUT", "requiredData": REQUIRED,
                         "fromDate": job.lo.isoformat(), "toDate": job.hi.isoformat()}


def _in_session(stamp, lo: date, hi: date) -> Optional[datetime]:
    start = datetime.fromtimestamp(float(stamp), tz=timezone.utc).astimezone(IST)
    minute = start.hour * 60 + start.minute
    return start if (lo <= start.date() <= hi and 555 <= minute <= 929) else None


def parse(job: Job, payload: dict) -> List[Dict[str, object]]:
    if job.kind == "candles":
        block, columns = payload or {}, CANDLE_COLUMNS
    else:
        data = (payload or {}).get("data") or {}
        block, columns = data.get("ce" if job.option_type == "CE" else "pe") or {}, OPTION_COLUMNS
    rows = []
    for i, stamp in enumerate(block.get("timestamp") or []):
        start = _in_session(stamp, job.lo, job.hi)
        if start is None:
            continue
        row = {"bar_start_ist": start.strftime("%Y-%m-%d %H:%M")}
        for col in columns[1:]:
            values = block.get(col) or []
            row[col] = values[i] if i < len(values) else None
        rows.append(row)
    return rows


def path_of(root: str, job: Job) -> str:
    if job.kind == "candles":
        return os.path.join(root, f"candles-{job.resolution}m", "raw", job.name, f"{job.lo:%Y%m%d}_{job.hi:%Y%m%d}.csv.gz")
    return os.path.join(root, "options-1m", "raw", job.name, f"{job.lo:%Y-%m}", f"{job.option_type}_{job.offset:+d}.csv.gz")


def manifest_path(root: str, job: Job) -> str:
    folder = f"candles-{job.resolution}m" if job.kind == "candles" else "options-1m"
    return os.path.join(root, folder, "manifest.jsonl")


class Manifest:
    def __init__(self, root: str) -> None:
        self.root = root
        self.latest: Dict[Tuple, dict] = {}
        for path in _manifests(root):
            with open(path) as fh:
                for line in fh:
                    if line.strip():
                        r = json.loads(line)
                        self.latest[tuple(r["key"])] = r

    def done(self, job: Job) -> bool:
        r = self.latest.get(tuple(job.key))
        return r is not None and (r["status"] == "empty" or (r["status"] == "ok" and os.path.exists(path_of(self.root, job))))

    def add(self, job: Job, status: str, http: Optional[int], rows: int, detail: str) -> None:
        record = {"key": list(job.key), "status": status, "http": http, "rows": rows, "detail": detail,
                  "fetched_utc": datetime.now(timezone.utc).isoformat(timespec="seconds")}
        path = manifest_path(self.root, job)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "a") as fh:
            fh.write(json.dumps(record) + "\n")
        self.latest[tuple(job.key)] = record


def _manifests(root: str) -> List[str]:
    if not os.path.isdir(root):
        return []
    return [os.path.join(root, d, "manifest.jsonl") for d in os.listdir(root)
            if os.path.exists(os.path.join(root, d, "manifest.jsonl"))]


PostFor = Callable[[str, Dict[str, object]], Tuple[int, object]]


def fetch(root: str, manifest: Manifest, job: Job, post: PostFor, pacer: Pacer, attempts: int = 4,
          sleep: Callable[[float], None] = time.sleep) -> str:
    url, body = request(job)
    status_code, payload, detail = None, None, ""
    for n in range(attempts):
        pacer.wait()
        try:
            status_code, payload = post(url, body)
        except Exception as ex:
            status_code, payload, detail = None, None, type(ex).__name__
        if status_code in (401, 403):
            raise AuthError(f"Dhan answered {status_code}; the access token has probably expired")
        if status_code == 200 or (status_code is not None and 400 <= status_code < 500 and status_code != 429):
            break
        sleep(2 ** n)
    if status_code == 200 and isinstance(payload, dict):
        rows = parse(job, payload)
        if rows:
            path = path_of(root, job)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            columns = CANDLE_COLUMNS if job.kind == "candles" else OPTION_COLUMNS
            with gzip.open(path + ".part", "wt", newline="") as fh:
                w = csv.DictWriter(fh, fieldnames=columns)
                w.writeheader()
                w.writerows(rows)
            os.replace(path + ".part", path)
            manifest.add(job, "ok", 200, len(rows), "")
            return "ok"
        manifest.add(job, "empty", 200, 0, "no bars")
        return "empty"
    text = json.dumps(payload)[:200] if isinstance(payload, (dict, list)) else str(payload)[:200]
    manifest.add(job, "error", status_code, 0, detail or text)
    return "error"


def run(root: str, jobs: Sequence[Job], post: PostFor, pacer: Optional[Pacer] = None,
        stop_at: Optional[Callable[[], bool]] = None, log: Callable[[str], None] = print) -> Dict[str, int]:
    pacer = pacer or Pacer(spacing=0.5)
    manifest = Manifest(root)
    counts = {"ok": 0, "empty": 0, "error": 0, "skipped": 0}
    for k, job in enumerate(jobs, 1):
        if stop_at is not None and stop_at():
            log(f"stopped by the time limit after {k - 1}/{len(jobs)} requests")
            counts["stopped"] = 1
            break
        if manifest.done(job):
            counts["skipped"] += 1
        else:
            status = fetch(root, manifest, job, post, pacer)
            counts[status] += 1
            if status == "error":
                log(f"{job.kind} {job.name} {job.lo} {job.option_type}{job.offset:+d} error")
        if k % 250 == 0 or k == len(jobs):
            log(f"{k}/{len(jobs)} · " + " ".join(f"{n}={v}" for n, v in counts.items()))
    return counts


def dhan_post(timeout: float = 60.0) -> PostFor:
    import requests
    client_id, token = os.environ.get("DHAN_CLIENT_ID"), os.environ.get("DHAN_ACCESS_TOKEN")
    if not client_id or not token:
        raise AuthError("DHAN_CLIENT_ID and DHAN_ACCESS_TOKEN must be set in the environment")
    session = requests.Session()
    session.headers.update({"access-token": token, "client-id": client_id, "Content-Type": "application/json",
                            "Accept": "application/json"})

    def post(url: str, body: Dict[str, object]) -> Tuple[int, object]:
        response = session.post(url, json=body, timeout=timeout)
        try:
            return response.status_code, response.json()
        except ValueError:
            return response.status_code, response.text[:200]

    return post
