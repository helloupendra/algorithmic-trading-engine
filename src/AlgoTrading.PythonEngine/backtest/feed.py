"""
backtest/feed.py

Historical candles for the replay, read through the platform API:

  - index candles for the driver resolution and every strategy-required
    resolution (`GET /api/MarketData/history/local`, canonical resolution
    codes, DateOnly bounds), exposed as cumulative BarFrame lists via
    `bars_upto(resolution, t)`;
  - option candles per contract, loaded on first use for the run range at the
    driver resolution; when the store has none and the broker is linked, one
    `POST /api/MarketData/history/sync` (30-day chunks) is attempted, then the
    symbol is marked `no_data` and never retried.

The API is duck-typed: `get_local_history(symbol, resolution, from, to)` and
`sync_history(symbol, resolution, from, to)`.
"""

from __future__ import annotations

from bisect import bisect_right
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from typing import Any, Callable, Dict, List, Optional, Set

from core.resolutions import is_daily, minutes_of, to_candle_resolution, to_strategy_resolution
from strategies.base_strategy import BarFrame

from backtest.run_spec import BacktestRun
from backtest.timeutil import (
    IST,
    SESSION_CLOSE_IST,
    date_range_chunks,
    in_session,
    iso_utc,
    ist_date,
    parse_utc,
)

SYNC_CHUNK_DAYS = 30


def _bar_end(start: datetime, resolution_code: str) -> datetime:
    """When a bar of this resolution is complete (daily bars: session close IST)."""
    if is_daily(resolution_code):
        local = start.astimezone(IST)
        return datetime.combine(local.date(), SESSION_CLOSE_IST, tzinfo=IST).astimezone(start.tzinfo)
    return start + timedelta(minutes=minutes_of(resolution_code))


def row_to_frame(row: Dict[str, Any], symbol: str, resolution_code: str) -> BarFrame:
    """API candle row -> BarFrame (timestamp with "Z", strategy-facing resolution)."""
    ts = parse_utc(row.get("timestampUtc") or row.get("timeStampUtc") or row.get("timestamp"))
    return BarFrame(
        symbol=str(row.get("symbol") or symbol),
        resolution=to_strategy_resolution(resolution_code),
        timestamp_utc=iso_utc(ts),
        open=float(row.get("open") or 0.0),
        high=float(row.get("high") or 0.0),
        low=float(row.get("low") or 0.0),
        close=float(row.get("close") or 0.0),
        volume=float(row.get("volume") or 0.0),
    )


@dataclass
class CandleSeries:
    """Sorted, de-duplicated candles of one (symbol, resolution)."""
    symbol: str
    resolution_code: str
    starts: List[datetime] = field(default_factory=list)
    ends: List[datetime] = field(default_factory=list)
    frames: List[BarFrame] = field(default_factory=list)

    @classmethod
    def from_rows(cls, symbol: str, resolution_code: str, rows: List[Dict[str, Any]]) -> "CandleSeries":
        by_start: Dict[datetime, BarFrame] = {}
        for row in rows or []:
            frame = row_to_frame(row, symbol, resolution_code)
            by_start[parse_utc(frame.timestamp_utc)] = frame
        series = cls(symbol=symbol, resolution_code=resolution_code)
        for start in sorted(by_start):
            series.starts.append(start)
            series.ends.append(_bar_end(start, resolution_code))
            series.frames.append(by_start[start])
        return series

    def __len__(self) -> int:
        return len(self.frames)

    @property
    def first(self) -> Optional[datetime]:
        return self.starts[0] if self.starts else None

    @property
    def last(self) -> Optional[datetime]:
        return self.starts[-1] if self.starts else None

    def close_at(self, t: datetime) -> Optional[float]:
        """Close of the bar starting at t, else the last close earlier the same IST day, else None."""
        idx = bisect_right(self.starts, t) - 1
        if idx < 0:
            return None
        start = self.starts[idx]
        if start == t or ist_date(start) == ist_date(t):
            return self.frames[idx].close
        return None

    def last_close_on_or_before(self, t: datetime) -> Optional[float]:
        idx = bisect_right(self.starts, t) - 1
        return self.frames[idx].close if idx >= 0 else None

    def frames_completed_by(self, t_end: datetime) -> List[BarFrame]:
        idx = bisect_right(self.ends, t_end)
        return self.frames[:idx]


class HistoricalFeed:
    """Index and option candles for one backtest run (see module docstring)."""

    def __init__(self, api: Any, run: BacktestRun, broker_linked: bool = True,
                 warmup_days: int = 15, log: Callable[[str], None] = print) -> None:
        self.api = api
        self.run = run
        self.broker_linked = bool(broker_linked)
        self.warmup_days = max(0, int(warmup_days))
        self.log = log

        self._index: Dict[str, CandleSeries] = {}            # resolution code -> series
        self._visible: Dict[str, List[BarFrame]] = {}         # cumulative frames handed to strategies
        self._cursor: Dict[str, int] = {}
        self._last_t: Dict[str, datetime] = {}

        self._options: Dict[tuple, CandleSeries] = {}        # (symbol, resolution code) -> series
        self.no_data: Set[str] = set()                        # "symbol@resolution" with nothing stored
        self.synced: List[str] = []
        self.sync_failures: List[str] = []
        self._sync_enabled = self.broker_linked
        self.local_reads = 0

    # --- index candles ------------------------------------------------------

    @property
    def index_from_date(self) -> date:
        return self.run.from_date - timedelta(days=self.warmup_days)

    def _read_local(self, symbol: str, resolution_code: str, from_date: date, to_date: date) -> List[Dict[str, Any]]:
        self.local_reads += 1
        return self.api.get_local_history(symbol, resolution_code, from_date.isoformat(), to_date.isoformat()) or []

    def ensure_index(self, resolution: str) -> CandleSeries:
        """Load (once) the spot symbol's candles at this resolution, warm-up window included."""
        code = to_candle_resolution(resolution)
        series = self._index.get(code)
        if series is None:
            rows = self._read_local(self.run.spot_symbol, code, self.index_from_date, self.run.to_date)
            series = CandleSeries.from_rows(self.run.spot_symbol, code, rows)
            self._index[code] = series
            self._visible[code] = []
            self._cursor[code] = 0
            self.log(
                f"[FEED] {self.run.spot_symbol} {to_strategy_resolution(code)}: {len(series)} candles "
                f"{self.index_from_date.isoformat()}..{self.run.to_date.isoformat()}"
            )
        return series

    def load(self, resolutions: List[str]) -> None:
        for resolution in [self.run.resolution] + list(resolutions or []):
            self.ensure_index(resolution)

    def driver_bars(self) -> List[BarFrame]:
        """Index candles at the run resolution inside [from, to], session bars only."""
        series = self.ensure_index(self.run.resolution_code)
        daily = is_daily(self.run.resolution_code)
        bars: List[BarFrame] = []
        for start, frame in zip(series.starts, series.frames):
            if start < self.run.from_utc or start > self.run.to_utc:
                continue
            if not daily and not in_session(start):
                continue
            bars.append(frame)
        return bars

    def warmup_bars(self, resolution: str) -> List[BarFrame]:
        """Index candles of this resolution strictly before the range start."""
        series = self.ensure_index(resolution)
        return [f for s, f in zip(series.starts, series.frames) if s < self.run.from_utc]

    def bars_upto(self, resolution: str, t: datetime) -> List[BarFrame]:
        """
        Every candle of this resolution that is complete by the end of the
        driver bar starting at t (warm-up candles included). `t` must not
        move backwards between calls; the returned list is shared and grows
        in place - treat it as read-only.
        """
        code = to_candle_resolution(resolution)
        series = self.ensure_index(code)
        t = parse_utc(t)
        if self._last_t.get(code) is not None and t < self._last_t[code]:
            # Time went backwards (should not happen in a replay): rebuild the window.
            self._visible[code] = []
            self._cursor[code] = 0
        self._last_t[code] = t
        t_end = _bar_end(t, self.run.resolution_code)
        visible, cursor = self._visible[code], self._cursor[code]
        while cursor < len(series.ends) and series.ends[cursor] <= t_end:
            visible.append(series.frames[cursor])
            cursor += 1
        self._cursor[code] = cursor
        return visible

    # --- option candles -----------------------------------------------------

    def _option_series(self, symbol: str, resolution_code: str) -> Optional[CandleSeries]:
        key = (symbol, resolution_code)
        if key in self._options:
            return self._options[key]
        tag = f"{symbol}@{resolution_code}"
        if tag in self.no_data:
            return None

        rows = self._read_local(symbol, resolution_code, self.run.from_date, self.run.to_date)
        if not rows and self._sync_enabled:
            rows = self._sync_then_read(symbol, resolution_code)

        if not rows:
            self.no_data.add(tag)
            self.log(f"[FEED] {symbol} {to_strategy_resolution(resolution_code)}: no premium history in range")
            return None

        series = CandleSeries.from_rows(symbol, resolution_code, rows)
        self._options[key] = series
        self.log(
            f"[FEED] {symbol} {to_strategy_resolution(resolution_code)}: {len(series)} candles "
            f"{iso_utc(series.first)}..{iso_utc(series.last)}"
        )
        return series

    # Failures that mean the broker itself is unusable for the rest of the run
    # (no session / rejected token / the API cannot be reached). Anything else
    # — typically FYERS answering "Invalid symbol" for an expired contract —
    # is a fact about THAT contract and must not stop the next contract from
    # being fetched (the spec's rule is per symbol, not per run).
    _BROKER_DOWN_MARKERS = ("no valid fyers session", "401", "unauthorized", "authenticate", "broker session")
    _TRANSPORT_ERRORS = ("ConnectionError", "ConnectTimeout", "ReadTimeout", "Timeout")

    def _sync_then_read(self, symbol: str, resolution_code: str) -> List[Dict[str, Any]]:
        fetched = 0
        for chunk_from, chunk_to in date_range_chunks(self.run.from_date, self.run.to_date, SYNC_CHUNK_DAYS):
            try:
                candles = self.api.sync_history(symbol, resolution_code, chunk_from.isoformat(), chunk_to.isoformat())
                fetched += len(candles or [])
            except Exception as ex:
                text = str(ex)
                lowered = text.lower()
                if "invalid symbol" in lowered:
                    # FYERS keeps history only for contracts that still exist.
                    self.log(f"[FEED] {symbol}: broker has no history (contract expired or unknown)")
                    return []
                message = f"{symbol}: history sync failed ({text[:200]})"
                self.sync_failures.append(message)
                transport = type(ex).__name__ in self._TRANSPORT_ERRORS
                if transport or any(marker in lowered for marker in self._BROKER_DOWN_MARKERS):
                    self.log(f"[FEED] WARN: {message}; broker unusable — no further broker syncs this run")
                    self._sync_enabled = False
                else:
                    self.log(f"[FEED] WARN: {message}; skipping this contract")
                return []
        self.synced.append(symbol)
        self.log(f"[FEED] {symbol}: synced {fetched} candles from the broker")
        if fetched == 0:
            return []
        return self._read_local(symbol, resolution_code, self.run.from_date, self.run.to_date)

    def option_close_at(self, symbol: str, t: datetime, resolution: Optional[str] = None) -> Optional[float]:
        """
        Fill/mark price of an option at driver bar t: the close of its candle
        starting at t, else the last close earlier the same IST day, else None.
        """
        code = to_candle_resolution(resolution or self.run.resolution_code)
        series = self._option_series(symbol, code)
        if series is None:
            return None
        return series.close_at(parse_utc(t))

    def option_last_close(self, symbol: str, t: datetime, resolution: Optional[str] = None) -> Optional[float]:
        """Last known close on or before t (any day); None when nothing is stored."""
        code = to_candle_resolution(resolution or self.run.resolution_code)
        series = self._option_series(symbol, code)
        if series is None:
            return None
        return series.last_close_on_or_before(parse_utc(t))

    def option_bars_upto(self, symbol: str, resolution: str, t: datetime) -> List[BarFrame]:
        """Option candles of this resolution complete by the end of driver bar t (copy)."""
        code = to_candle_resolution(resolution)
        series = self._option_series(symbol, code)
        if series is None:
            return []
        return series.frames_completed_by(_bar_end(parse_utc(t), self.run.resolution_code))

    def has_option_data(self, symbol: str, resolution: Optional[str] = None) -> bool:
        code = to_candle_resolution(resolution or self.run.resolution_code)
        return self._option_series(symbol, code) is not None

    @property
    def no_data_symbols(self) -> List[str]:
        return sorted({tag.split("@", 1)[0] for tag in self.no_data})
