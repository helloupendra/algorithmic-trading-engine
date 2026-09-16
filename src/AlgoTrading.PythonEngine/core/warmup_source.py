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
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any, Callable, List, Optional, Sequence

from core.data_models import BarData
from core.warmup_retry import WARMUP_AUTH_RETRY_ATTEMPTS, fetch_warmup_bars_with_retry, is_auth_failure

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
        return WarmupBars(stored, "local store", f"{len(stored)} bars from the platform store")

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
