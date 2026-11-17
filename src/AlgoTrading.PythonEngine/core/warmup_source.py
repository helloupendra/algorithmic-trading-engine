"""
core/warmup_source.py

Where a live runner's warm-up bars come from.

Until 2026-09-16 warm-up went straight to the broker's history API (FYERS via
DataEngine). That made every warming strategy depend on a FYERS token even
when another connector was feeding the platform: on 16 Sep the Dhan feed
carried the market from 09:15 and wrote ticks, bars and quotes, yet Ghost sat
in "broker rejected the token — retry 1/39" until the owner signed in to FYERS
at 09:36. Twenty-one minutes of a live session, with the data already on disk.

So the platform's own candle store is asked first. It is vendor-agnostic:
whatever connector backfilled it (`/api/MarketData/history/local`). The broker
is the fallback, and its failure no longer blocks a start — the runner warms
up on whatever local bars exist and says which source it used.

The broker retry loop still has its place: with NO local bars, waiting for a
sign-in is better than starting blind (see core/warmup_retry.py). With local
bars in hand there is nothing to wait for, so the broker gets one try.

A store with plenty of bars can still be missing most of one day. On
2026-11-17 TrendRider V2 warmed up at 09:18 while the store held 16 Nov only
from 13:49 (the live recorder had missed the morning; FYERS history filled it
later that day). V2 read yesterday's high as 25,291.1; the day's high was
25,337.6. So every earlier session is checked, and a short one (under 90% of
its candles) is filled from the broker, once, before the strategy sees it.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from typing import Any, Callable, List, Optional, Sequence, Tuple

from core.data_models import BarData
from core.resolutions import is_daily, minutes_of
from core.warmup_retry import WARMUP_AUTH_RETRY_ATTEMPTS, fetch_warmup_bars_with_retry, is_auth_failure

#: Calendar days of history a warm-up loads when the strategy names no need.
WARMUP_DAYS = 15
#: Bars a live warm-up replays when the strategy names no need: 500 5-minute
#: bars are six and a half sessions.
LIVE_WARMUP_BARS = 500
#: One NSE session, 09:15-15:30.
SESSION_MINUTES = 375


def required_warmup_bars(strategy: Any) -> Optional[int]:
    """The bars a strategy says it needs before it evaluates (`warmup_bars`), or None."""
    bars = getattr(strategy, "warmup_bars", None)
    if callable(bars):
        bars = bars()
    return bars if isinstance(bars, int) and bars > 0 else None


def warmup_days_for(strategy: Any, resolution_code: str, default: int = WARMUP_DAYS) -> int:
    """
    Calendar days of index history to load before the range so the strategy
    has the bars it says it needs (`warmup_bars`) at the run's resolution.
    Fifteen days hold ~1,100 5-minute bars but only ~11 daily ones, which left
    a daily run of a 51-bar strategy with nothing to evaluate: the window has
    to follow the bar length, not stay fixed. Never less than `default`.
    The backtest and the live runner both size their warm-up with it.
    """
    bars = required_warmup_bars(strategy)
    if bars is None:
        return default
    if is_daily(resolution_code):
        sessions = bars
    else:
        sessions = math.ceil(bars / max(1, SESSION_MINUTES // max(1, minutes_of(resolution_code))))
    # Sessions to calendar days: five sessions a week, plus room for holidays.
    return max(default, math.ceil(sessions * 7 / 5 * 1.1) + 5)


def live_warmup_count(strategy: Any) -> int:
    """
    How many of the newest warm-up bars a live runner replays: LIVE_WARMUP_BARS,
    or more when the strategy needs more. TrendRider needs ten full sessions
    before today (900 5-minute bars); cut at 500 it saw six and a half and
    skipped every day for want of history.
    """
    return max(LIVE_WARMUP_BARS, required_warmup_bars(strategy) or 0)


#: India Standard Time, where the session's 09:15-15:30 is.
IST = timezone(timedelta(hours=5, minutes=30))
SESSION_OPEN_MIN = 9 * 60 + 15
SESSION_CLOSE_MIN = 15 * 60 + 30
#: A session holding fewer of its candles than this share counts as short.
COMPLETE_SHARE = 0.9

#: Fewer bars than this and the broker is still worth asking: most warming
#: strategies need tens of bars (Ghost's pivots, an EMA's span) before they
#: evaluate anything.
MIN_LOCAL_BARS = 60


@dataclass(frozen=True)
class WarmupBars:
    """The bars, where they came from, and what to print about it."""
    bars: List[BarData]
    source: str                  # "local store" | "broker history" | "none"
    detail: str = ""

    def __bool__(self) -> bool:
        return bool(self.bars)


def canonical_resolution(resolution: str) -> str:
    """"5m" and "5" are the same candle; the local store keys them as "5"/"D"."""
    text = (resolution or "").strip()
    if text.lower().endswith("m") and text[:-1].isdigit():
        return text[:-1]
    return text.upper() if text.lower() in ("d", "1d") else text


def _bar_from_row(symbol: str, resolution: str, row: dict) -> Optional[BarData]:
    stamp = row.get("timestampUtc") or row.get("timestamp_utc") or row.get("timeStampUtc")
    if not stamp:
        return None
    text = str(stamp)
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        when = datetime.fromisoformat(text)
    except ValueError:
        return None
    if when.tzinfo is None:
        when = when.replace(tzinfo=timezone.utc)
    try:
        return BarData(
            symbol=row.get("symbol") or symbol,
            resolution=resolution,
            timestamp_start=when,
            open=float(row["open"]), high=float(row["high"]), low=float(row["low"]), close=float(row["close"]),
            volume=int(float(row.get("volume") or 0)),
        )
    except (KeyError, TypeError, ValueError):
        return None


def local_bars(api, symbol: str, resolution: str, start_date: str, end_date: str) -> List[BarData]:
    """Stored candles for the window, oldest first. Never raises: an empty list is the answer."""
    try:
        rows = api.get_local_history(symbol, canonical_resolution(resolution), start_date, end_date)
    except Exception:  # noqa: BLE001 — the caller falls back to the broker
        return []
    bars = [b for b in (_bar_from_row(symbol, resolution, r) for r in rows or []) if b is not None]
    bars.sort(key=lambda b: b.timestamp_start)
    return bars


def short_sessions(bars: Sequence[BarData], resolution: str, before: date) -> List[Tuple[date, int, int]]:
    """
    The sessions before `before` (an IST date) holding under COMPLETE_SHARE of
    their candles: (day, candles held, candles a full session has). Daily bars
    have no session to count and are never short.
    """
    if is_daily(resolution):
        return []
    minutes = max(1, minutes_of(resolution))
    full = SESSION_MINUTES // minutes
    held: dict = {}
    for b in bars:
        ist = b.timestamp_start.astimezone(IST)
        minute = ist.hour * 60 + ist.minute
        if minute < SESSION_OPEN_MIN or minute >= SESSION_CLOSE_MIN or ist.date() >= before:
            continue
        held.setdefault(ist.date(), set()).add((minute - SESSION_OPEN_MIN) // minutes)
    return [(d, len(slots), full) for d, slots in sorted(held.items()) if len(slots) < full * COMPLETE_SHARE]


def fill_sessions(stored: Sequence[BarData], extra: Sequence[BarData], days: Sequence[date]) -> List[BarData]:
    """`stored` plus the bars of `extra` that fall on `days` at a start `stored` lacks; oldest first."""
    have = {b.timestamp_start for b in stored}
    wanted = set(days)
    added = [b for b in extra
             if b.timestamp_start.astimezone(IST).date() in wanted and b.timestamp_start not in have]
    return sorted([*stored, *added], key=lambda b: b.timestamp_start)


def _describe(gaps: Sequence[Tuple[date, int, int]]) -> str:
    return ", ".join(f"{d:%d %b} {n}/{full}" for d, n, full in gaps)


def _fill_short_sessions(stored: List[BarData], *, make_engine: Callable, symbol: str, resolution: str,
                         start_date: str, end_date: str, label: str, log: Callable[[str], None],
                         sleep=None) -> WarmupBars:
    """A full-looking store with short earlier sessions: one broker try to fill them, never fatal."""
    try:
        before = date.fromisoformat(end_date[:10])
        gaps = short_sessions(stored, resolution, before)
    except ValueError:
        return WarmupBars(stored, "local store", f"{len(stored)} bars from the platform store")
    if not gaps:
        return WarmupBars(stored, "local store", f"{len(stored)} bars from the platform store")

    log(f"[{label}] Warmup: short session(s) in the platform store ({_describe(gaps)} candles); "
        f"asking the broker once to fill them.")
    try:
        kwargs = {"sleep": sleep} if sleep is not None else {}
        broker = fetch_warmup_bars_with_retry(
            make_engine=make_engine, symbol=symbol, resolution=resolution,
            start_date=start_date, end_date=end_date, label=label, attempts=1, **kwargs) or []
        why = "the broker returned nothing"
    except Exception as ex:  # noqa: BLE001 — reported, never fatal
        broker = []
        why = "the broker rejected the token" if is_auth_failure(ex) else f"the broker failed ({ex})"

    merged = fill_sessions(stored, broker, [d for d, _, _ in gaps])
    added = len(merged) - len(stored)
    if added == 0:
        return WarmupBars(stored, "local store",
                          f"{len(stored)} stored bars; short session(s) {_describe(gaps)} not filled ({why}). "
                          "Levels read from those days (yesterday's high/low, the average range) may be wrong.")
    still = short_sessions(merged, resolution, before)
    detail = f"{len(stored)} stored bars + {added} from the broker for {_describe(gaps)}"
    if still:
        detail += f"; still short: {_describe(still)} (a short trading session, or the broker lacks it too)"
    return WarmupBars(merged, "local store + broker", detail)


def load_warmup_bars(
    *,
    api,
    make_engine: Callable,
    symbol: str,
    resolution: str,
    start_date: str,
    end_date: str,
    label: str,
    min_local_bars: int = MIN_LOCAL_BARS,
    log: Callable[[str], None] = print,
    sleep=None,
) -> WarmupBars:
    """
    Warm-up bars from the platform's store, else from the broker.

    The broker is retried while someone signs in ONLY when there is nothing
    local to fall back on; otherwise it gets a single attempt and the local
    bars are used if it refuses.
    """
    stored = local_bars(api, symbol, resolution, start_date, end_date)
    if len(stored) >= min_local_bars:
        return _fill_short_sessions(stored, make_engine=make_engine, symbol=symbol, resolution=resolution,
                                    start_date=start_date, end_date=end_date, label=label, log=log, sleep=sleep)

    attempts = 1 if stored else WARMUP_AUTH_RETRY_ATTEMPTS
    if stored:
        log(f"[{label}] Warmup: only {len(stored)} stored bar(s); asking the broker once before using them.")
    try:
        kwargs = {"sleep": sleep} if sleep is not None else {}
        bars = fetch_warmup_bars_with_retry(
            make_engine=make_engine, symbol=symbol, resolution=resolution,
            start_date=start_date, end_date=end_date, label=label, attempts=attempts, **kwargs)
        if bars:
            return WarmupBars(list(bars), "broker history", f"{len(bars)} bars from the broker")
        if stored:
            return WarmupBars(stored, "local store", f"broker returned nothing; {len(stored)} stored bars")
        return WarmupBars([], "none", "broker returned no bars")
    except Exception as ex:  # noqa: BLE001 — reported, never fatal
        why = "the broker rejected the token" if is_auth_failure(ex) else f"the broker failed ({ex})"
        if stored:
            return WarmupBars(stored, "local store", f"{why}; used {len(stored)} stored bars instead")
        return WarmupBars([], "none", f"{why}, and the platform store has no bars for this window")
