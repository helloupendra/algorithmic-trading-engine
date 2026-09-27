"""
Synthetic sessions, VIX and calendars for the analysis tests: deterministic,
weekday-only, and shaped like the real thing (a random-walk index whose range
clusters, a VIX that wanders around 15).
"""

import _bootstrap  # noqa: F401

import math
from datetime import date, timedelta
from typing import Dict, Iterable, List, Optional, Sequence

import numpy as np

from analysis.data import ExpiryCalendar, MarketData, SessionBar, SessionSeries, VIX_SYMBOL, BY_NAME


def weekdays(start: date, n: int, skip: Iterable[date] = ()) -> List[date]:
    skip = set(skip)
    out, day = [], start
    while len(out) < n:
        if day.weekday() < 5 and day not in skip:
            out.append(day)
        day += timedelta(days=1)
    return out


def sessions(days: Sequence[date], seed: int = 1, level: float = 20000.0) -> List[SessionBar]:
    """A random walk with clustered ranges (log-range is AR(1) around ln 1%)."""
    rng = np.random.default_rng(seed)
    out, close, log_range = [], level, math.log(1.0)
    for day in days:
        log_range = 0.7 * log_range + 0.3 * math.log(1.0) + rng.normal(0, 0.25)
        span = close * math.exp(log_range) / 100.0
        open_ = close * (1 + rng.normal(0, 0.002))
        new_close = open_ + rng.uniform(-0.45, 0.45) * span
        high = max(open_, new_close) + rng.uniform(0, 0.3) * span
        low = min(open_, new_close) - rng.uniform(0, 0.3) * span
        out.append(SessionBar(day, round(open_, 2), round(high, 2), round(low, 2), round(new_close, 2), 75,
                              "candles 5m"))
        close = new_close
    return out


def vix(days: Sequence[date], seed: int = 2) -> List[SessionBar]:
    rng = np.random.default_rng(seed)
    out, level = [], 15.0
    for day in days:
        level = max(9.0, level + 0.1 * (15.0 - level) + rng.normal(0, 0.6))
        out.append(SessionBar(day, level, level + 0.5, level - 0.5, round(level, 2), 75, "candles 5m"))
    return out


def tuesdays(days: Sequence[date]) -> frozenset:
    return frozenset(d for d in days if d.weekday() == 1)


def market(names: Sequence[str] = ("NIFTY",), n: int = 400, start: date = date(2023, 1, 2),
           holidays: Optional[Dict[str, frozenset]] = None, drop_vix: Iterable[date] = (),
           seed: int = 1) -> MarketData:
    holidays = holidays or {}
    skip = set().union(*holidays.values()) if holidays else set()
    days = weekdays(start, n, skip)
    series = {name: SessionSeries(BY_NAME[name].symbol, sessions(days, seed + k)) for k, name in enumerate(names)}
    drop = set(drop_vix)
    vix_series = SessionSeries(VIX_SYMBOL, [s for s in vix(days) if s.day not in drop])
    far = date(2099, 1, 1)
    calendar = ExpiryCalendar({name: tuesdays(days) for name in names}, {"NSE": far, "BSE": far}, {}, holidays)
    return MarketData(series, vix_series, calendar, holidays)


class FakeResponse:
    def __init__(self, status: int, body=None, text: str = ""):
        import json

        self.status_code = status
        self._body = body
        self.text = text or (json.dumps(body) if body is not None else "")
        self.content = self.text.encode()
        self.reason = "reason"

    def json(self):
        if self._body is None:
            raise ValueError("no json")
        return self._body


class FakeHttp:
    """Records every call; answers from `answers` (a list, consumed in order) or with `default`."""

    def __init__(self, answers=None, default=None):
        self.answers = list(answers or [])
        self.default = default or FakeResponse(201, {"id": 1, "issuedUtc": "2026-09-28T03:20:04Z"})
        self.calls = []

    def _answer(self):
        return self.answers.pop(0) if self.answers else self.default

    def post(self, url, json=None, **kw):
        self.calls.append(("POST", url, json, kw))
        return self._answer()

    def get(self, url, params=None, **kw):
        self.calls.append(("GET", url, params, kw))
        return self._answer()
