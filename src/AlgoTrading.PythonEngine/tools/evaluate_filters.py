"""
tools/evaluate_filters.py

Runs the shared indicators and the market-context gate over real bars and
prints the result as JSON, so the console can show what they actually compute
before either is trusted with an order.

It imports `strategies.indicators` and `strategies.signal_filters` directly —
the same modules the live runner and the replay use. A lab that reimplemented
the maths would test a copy, and the copy is exactly what would drift.

Request arrives on stdin as JSON, because `filters` is a nested object and a
command line is a poor place for one:

    {"symbol": "NSE:NIFTYBANK-INDEX", "resolution": "5m", "take": 200,
     "direction": "bullish", "bars": 25, "filters": {...}}

The reply carries three things:
  - the indicator values on the last CLOSED bar;
  - the gate's verdict for a signal in `direction` right now;
  - the same verdict replayed over the last `bars` candles, which is what
    answers "how often would this actually let a trade through, and why not".
"""

from __future__ import annotations

import json
import sys
from datetime import date, datetime, time as dtime, timedelta
import traceback
from typing import Any, Dict, List

from core.api_client import PlatformApiClient
from core.config import API_BASE_URL, VERIFY_SSL
from strategies import indicators, signal_filters
from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal


def bars_from_rows(rows: List[Dict[str, Any]], symbol: str, resolution: str) -> List[BarFrame]:
    """Recent-bar rows (newest first, as the API returns them) -> oldest-first frames."""
    return [
        BarFrame(
            symbol=row.get("symbol", symbol),
            resolution=row.get("resolution", resolution),
            timestamp_utc=str(row.get("barStartUtc", "")),
            open=float(row.get("open", 0.0)),
            high=float(row.get("high", 0.0)),
            low=float(row.get("low", 0.0)),
            close=float(row.get("close", 0.0)),
            volume=float(row.get("volumeDelta", 0.0)),
        )
        for row in reversed(rows or [])
    ]


def bars_from_history(rows: List[Dict[str, Any]], symbol: str, resolution: str) -> List[BarFrame]:
    """
    Stored candles -> oldest-first frames.

    A different shape from the live path on purpose, because it IS a different
    endpoint: stored rows carry `timestampUtc` and a total `volume`, live rows
    carry `barStartUtc` and a `volumeDelta`. Mapping one with the other's key
    names yields silent zeros, which an indicator then averages.
    """
    frames = [
        BarFrame(
            symbol=row.get("symbol", symbol),
            resolution=resolution,
            timestamp_utc=str(row.get("timestampUtc", "")),
            open=float(row.get("open", 0.0)),
            high=float(row.get("high", 0.0)),
            low=float(row.get("low", 0.0)),
            close=float(row.get("close", 0.0)),
            volume=float(row.get("volume", 0.0)),
        )
        for row in rows or []
    ]
    frames.sort(key=lambda b: b.timestamp_utc)
    return frames


def canonical_resolution(resolution: str) -> str:
    """"5m" -> "5" — what the stored-candle endpoint keys its rows by."""
    text = (resolution or "5m").strip().lower()
    return text[:-1] if text.endswith("m") else text


def probe_signal(direction: str) -> StrategySignal:
    """
    A stand-in opening signal in the requested direction.

    The gate reads only the direction and the type, so a one-leg signal is
    enough to ask "would a bullish entry be allowed here?" without a strategy
    having to produce one.
    """
    return StrategySignal(
        strategy_name="FilterLab",
        signal_type="OPEN_GROUP",
        timestamp_utc="",
        reason="lab probe",
        legs=[],
        metadata={"direction": "SELL" if direction == "bearish" else "BUY"},
    )


def snapshot(bars: List[BarFrame], config) -> Dict[str, Any]:
    """Every indicator the gate can use, on the last CLOSED bar."""
    if not bars:
        return {}

    last = bars[-1]
    session = indicators.session_bars(bars)
    closes = indicators.closes(bars)
    ema_period = (config.ema_period if config and config.ema_period else 20)

    ist = indicators.ist_of(last)
    return {
        "barIst": ist.strftime("%Y-%m-%d %H:%M") if ist else None,
        "open": last.open,
        "high": last.high,
        "low": last.low,
        "close": last.close,
        "volume": last.volume,
        "sessionBars": len(session),
        "vwap": indicators.vwap(session),
        "ema": indicators.ema(closes, ema_period),
        "emaPeriod": ema_period,
        "sma20": indicators.sma(closes, 20),
        "rsi14": indicators.rsi(closes, 14),
        "atr14": indicators.atr(bars, 14),
        "atrPercent": indicators.atr_percent(bars, 14),
        "volumeZScore": indicators.volume_zscore(bars, config.volume_lookback if config else 20),
        # Every pattern, not just the ones a filter asked for: the point of the
        # lab is to see what the detectors actually fire on.
        "patterns": [name for name, fn in indicators.PATTERNS.items() if fn(bars)],
    }


def verdict_dict(verdict) -> Dict[str, Any]:
    return {
        "allowed": verdict.allowed,
        "blockedBy": verdict.blocked_by,
        "reason": verdict.reason,
        "details": verdict.details,
    }


def main() -> int:
    request = json.loads(sys.stdin.read() or "{}")

    symbol = str(request.get("symbol") or "").strip()
    resolution = str(request.get("resolution") or "5m")
    take = int(request.get("take") or 300)
    history = max(1, int(request.get("bars") or 25))
    raw_filters = request.get("filters") or {}
    from_date = str(request.get("fromDate") or "").strip()
    to_date = str(request.get("toDate") or "").strip()
    from_time = str(request.get("fromTime") or "").strip()
    to_time = str(request.get("toTime") or "").strip()

    if not symbol:
        print(json.dumps({"error": "symbol is required"}))
        return 2

    config = signal_filters.parse_filters({"filters": raw_filters})

    api = PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)

    if from_date:
        # Stored candles, so a window can reach back months rather than the day
        # and a half the live table holds.
        #
        # Fetched from a WEEK before the window: an EMA needs bars before the
        # first one it is asked about, and starting the series at the window's
        # own edge would answer "no reading yet" for exactly the candles being
        # studied.
        warmup_from = (date.fromisoformat(from_date) - timedelta(days=7)).isoformat()
        rows = api.get_local_history(symbol, canonical_resolution(resolution),
                                     warmup_from, to_date or from_date)
        bars = bars_from_history(rows, symbol, resolution)
        source = f"stored candles {from_date} to {to_date or from_date}"

        # Today's candles are only in the store if someone synced them, so a
        # window on today would otherwise come back empty and look like a bug.
        if len(bars) < 2:
            rows = api.get_recent_bars(symbol, resolution=resolution, take=take)
            bars = bars_from_rows(rows, symbol, resolution)
            source = "live bars (no stored candles for that range)"
    else:
        rows = api.get_recent_bars(symbol, resolution=resolution, take=take)
        bars = bars_from_rows(rows, symbol, resolution)
        source = "live bars"

    if len(bars) < 2:
        print(json.dumps({
            "symbol": symbol,
            "resolution": resolution,
            "error": f"only {len(bars)} bar(s) available for {symbol} at {resolution}",
            "barCount": len(bars),
        }))
        return 0

    # The newest bar is still forming, exactly as the live runner sees it, so
    # everything below judges the one before it.
    closed = bars[:-1]

    def evaluate_at(upto: List[BarFrame], sig: StrategySignal):
        inp = StrategyInput(
            mode="LivePaper",
            timestamp_utc=upto[-1].timestamp_utc,
            underlying=symbol,
            spot_price=float(upto[-1].close),
            bars={resolution: {"index": upto}},
            metadata={"source": "lab"},
        )
        # Already trimmed to closed bars, so nothing here is forming.
        return signal_filters.evaluate(config, sig, inp, newest_bar_is_forming=False)

    # BOTH sides, every time.
    #
    # Asking about one direction at a time hides the answer that matters: a
    # market the filters refuse a call in is usually one they would allow a put
    # in, and that is a trade, not a no-trade. Showing them side by side turns
    # "blocked" into "this side was open, that one was not".
    sides = {"bullish": probe_signal("bullish"), "bearish": probe_signal("bearish")}
    verdicts = {name: verdict_dict(evaluate_at(closed, sig)) for name, sig in sides.items()}

    # Which candles get asked about. With a window it is every closed candle
    # inside it — the warmup bars fetched before it build the indicators and
    # are never judged. Without one, the last `history` candles.
    def in_window(bar) -> bool:
        moment = indicators.ist_of(bar)
        if moment is None:
            return False
        if from_date and moment.date() < date.fromisoformat(from_date):
            return False
        if to_date and moment.date() > date.fromisoformat(to_date):
            return False
        for text, is_start in ((from_time, True), (to_time, False)):
            if not text:
                continue
            try:
                hh, mm = (int(x) for x in text.split(":"))
            except ValueError:
                continue
            limit = dtime(hh, mm)
            if is_start and moment.time() < limit:
                return False
            if not is_start and moment.time() > limit:
                return False
        return True

    if from_date or from_time or to_time:
        indexes = [i for i, b in enumerate(closed) if in_window(b) and i >= 1]
    else:
        indexes = list(range(max(2, len(closed) - history), len(closed) + 1))
        indexes = [i - 1 for i in indexes]

    timeline = []
    for idx in indexes:
        window = closed[: idx + 1]
        moment = indicators.ist_of(window[-1])
        row = {
            "barIst": moment.strftime("%d %b %H:%M") if moment else None,
            "close": window[-1].close,
        }
        for name, sig in sides.items():
            v = evaluate_at(window, sig)
            row[name] = {"allowed": v.allowed, "blockedBy": v.blocked_by, "reason": v.reason}
        timeline.append(row)

    def tally(name: str) -> Dict[str, Any]:
        blocked_by: Dict[str, int] = {}
        for row in timeline:
            if not row[name]["allowed"]:
                key = row[name]["blockedBy"] or "unknown"
                blocked_by[key] = blocked_by.get(key, 0) + 1
        return {
            "allowed": sum(1 for r in timeline if r[name]["allowed"]),
            "blocked": sum(1 for r in timeline if not r[name]["allowed"]),
            "blockedBy": blocked_by,
        }

    print(json.dumps({
        "symbol": symbol,
        "resolution": resolution,
        "barCount": len(bars),
        "closedBarCount": len(closed),
        "filtersConfigured": config is not None,
        "source": source,
        "indicators": snapshot(closed, config),
        "verdicts": verdicts,
        "timeline": timeline,
        "summary": {
            "evaluated": len(timeline),
            "bullish": tally("bullish"),
            "bearish": tally("bearish"),
        },
    }, default=str))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as ex:  # the console shows this instead of an empty panel
        print(json.dumps({"error": f"{type(ex).__name__}: {ex}",
                          "traceback": traceback.format_exc()}))
        sys.exit(1)
