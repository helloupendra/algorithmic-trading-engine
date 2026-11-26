"""
A live run's recent bars, without asking the API for 500 candles on every tick.

Until 26 Nov 2026 the runner fetched `take` (500) bars of every data requirement
on every spot tick. The API builds a 5-minute bar from five 1-minute rows and a
1-hour bar from sixty, so one tick of a 1-hour run read 30,000 rows and one tick
of a 5-minute run 2,500, about eight times a second each. With six runs on the
2-CPU server (Ghost V2 on 1-hour candles among them) the API and the database took
both CPUs, the runners fell 25-30 minutes behind the exchange (25 and 26 Nov,
from about 12:10 and 09:40), and their entries and exits were made on prices
that old.

The cache asks for the same bars, far less often:

- the full window (`take` bars) once, and again on the first tick of every new
  exchange minute, as before: the candle that has just closed is read whole on
  the same tick it was read on before;
- in between, at most once a second, only the newest few bars (TAIL_TAKE). The
  newest two of them (the candle still forming and the one before it) replace
  the cached ones, so the forming candle is never more than about a second old;
- otherwise the cached rows, which the strategy reads exactly as it read the
  API's: newest first, the same dicts.

A recap (replay) run does not use it: it bounds every read by its tick's stamp.
"""

from __future__ import annotations

import time
from datetime import datetime, timezone
from typing import Any, Callable, Dict, List, Optional, Tuple

#: Bars asked for between full reads. The API aggregates TAIL_TAKE x minutes
#: 1-minute rows and buckets them, so the oldest bucket it returns can be cut
#: short; the newest TAIL_KEEP never are (each holds at most `minutes` rows).
TAIL_TAKE = 3
TAIL_KEEP = 2

Fetch = Callable[[str, str, int], List[Dict[str, Any]]]


def _stamp(value: Any) -> Optional[datetime]:
    if value is None or value == "":
        return None
    if isinstance(value, datetime):
        moment = value
    else:
        try:
            moment = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        except ValueError:
            return None
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(timezone.utc)


def _minute_key(stamp: Any, wall: float) -> str:
    """The tick's exchange minute; the wall-clock minute when the stamp cannot be read."""
    moment = _stamp(stamp)
    if moment is None:
        return f"wall:{int(wall // 60)}"
    return moment.strftime("%Y-%m-%dT%H:%M")


def merge_tail(cached: List[Dict[str, Any]], tail: List[Dict[str, Any]], keep: int) -> List[Dict[str, Any]]:
    """
    The cached bars (newest first) with the newest `keep` of `tail` laid over them:
    a bar of the same start is replaced, a newer one is added in front. Every
    cached bar at or after the oldest kept tail bar is dropped, so nothing the API
    no longer reports survives beside its replacement.
    """
    fresh = [row for row in tail[:keep] if _stamp(row.get("barStartUtc")) is not None]
    if not fresh:
        return list(cached)
    oldest = min(_stamp(row.get("barStartUtc")) for row in fresh)
    older = [row for row in cached
             if (_stamp(row.get("barStartUtc")) or oldest) < oldest]
    fresh.sort(key=lambda row: _stamp(row.get("barStartUtc")), reverse=True)
    return fresh + older


class LiveBarCache:
    """Recent bars per (symbol, resolution) for one live runner. Not thread-safe; one runner, one loop."""

    def __init__(self, fetch: Fetch, min_interval: float = 1.0, clock: Callable[[], float] = time.monotonic,
                 wall: Callable[[], float] = time.time) -> None:
        self._fetch = fetch
        self._min_interval = float(min_interval)
        self._clock = clock
        self._wall = wall
        self._entries: Dict[Tuple[str, str], Dict[str, Any]] = {}
        self.full_reads = 0
        self.tail_reads = 0

    def get(self, symbol: str, resolution: str, stamp: Any, take: int = 500) -> List[Dict[str, Any]]:
        key = (symbol, resolution)
        minute = _minute_key(stamp, self._wall())
        now = self._clock()
        entry = self._entries.get(key)

        if entry is None or entry["minute"] != minute or entry["take"] != take:
            rows = list(self._fetch(symbol, resolution, take) or [])
            self.full_reads += 1
            self._entries[key] = {"rows": rows, "minute": minute, "at": now, "take": take}
            return rows

        if now - entry["at"] >= self._min_interval:
            tail = list(self._fetch(symbol, resolution, TAIL_TAKE) or [])
            self.tail_reads += 1
            entry["at"] = now
            if tail:
                entry["rows"] = merge_tail(entry["rows"], tail, TAIL_KEEP)[:take]
        return entry["rows"]
