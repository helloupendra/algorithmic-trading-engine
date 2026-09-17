"""
strategies/indicators.py

The shared indicator and candle-pattern maths, kept in one place so a strategy,
a signal filter and a backtest all compute the same number from the same bars.

Everything here is a pure function over a list of BarFrame (oldest first, the
order every caller in this codebase already uses). Nothing reads the clock, the
database or a config file, so each one can be tested against a handful of
hand-written bars.

Two rules the whole file obeys:

  - Not enough bars returns None, never a partial answer. An EMA seeded on three
    closes is not a 20-EMA, and a filter that treats it as one is worse than a
    filter that stands aside.
  - The CURRENT bar is included. Callers that must not see the forming bar pass
    `bars[:-1]` themselves - the decision of what has closed belongs to them,
    not here, and the live runner and the replay disagree about it.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Sequence

# Indian exchanges stamp everything in IST; bars carry UTC. One definition,
# because a session boundary computed two ways is two different sessions.
IST = timezone(timedelta(hours=5, minutes=30))


# ----------------------------------------------------------------- helpers --

def parse_utc(timestamp: Any) -> Optional[datetime]:
    """
    A bar's timestamp as an aware UTC datetime.

    Bar timestamps reach us from several places - warmup writes an explicit "Z",
    the live path passes the API's JSON through untouched, the replay builds its
    own - so neither the suffix nor the presence of an offset can be assumed.
    """
    if isinstance(timestamp, datetime):
        return timestamp if timestamp.tzinfo else timestamp.replace(tzinfo=timezone.utc)

    text = str(timestamp or "").strip()
    if not text:
        return None
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    return parsed.replace(tzinfo=timezone.utc) if parsed.tzinfo is None else parsed


def ist_of(bar: Any) -> Optional[datetime]:
    """The bar's start in IST, or None when it carries no readable stamp."""
    moment = parse_utc(getattr(bar, "timestamp_utc", None))
    return moment.astimezone(IST) if moment else None


def session_date(bar: Any) -> Optional[str]:
    """
    The IST calendar date of a bar, which identifies its session.

    True for NSE/BSE (09:15-15:30) and for MCX (09:00-23:30/23:55): both run
    within one IST day, so the date is a complete session key.
    """
    moment = ist_of(bar)
    return moment.strftime("%Y-%m-%d") if moment else None


def session_bars(bars: Sequence[Any], upto_index: int = -1) -> List[Any]:
    """Bars of the same session as `bars[upto_index]`, up to and including it."""
    if not bars:
        return []
    anchor = bars[upto_index]
    day = session_date(anchor)
    if day is None:
        return []
    end = len(bars) + upto_index + 1 if upto_index < 0 else upto_index + 1
    return [b for b in bars[:end] if session_date(b) == day]


def closes(bars: Sequence[Any]) -> List[float]:
    return [float(b.close) for b in bars]


# -------------------------------------------------------------- indicators --

def sma(values: Sequence[float], period: int) -> Optional[float]:
    """Simple moving average of the last `period` values."""
    if period < 1 or len(values) < period:
        return None
    window = values[-period:]
    return sum(window) / float(period)


def ema(values: Sequence[float], period: int) -> Optional[float]:
    """
    EMA at the last value, seeded with the SMA of the first `period` values.

    Seeding matters: starting from the first value alone makes the early series
    depend on where the data happens to begin, so two runs over different
    history lengths disagree about today's EMA.
    """
    if period < 1 or len(values) < period:
        return None
    multiplier = 2.0 / (period + 1.0)
    value = sum(values[:period]) / float(period)
    for v in values[period:]:
        value = v * multiplier + value * (1.0 - multiplier)
    return value


def vwap(bars: Sequence[Any]) -> Optional[float]:
    """
    Volume-weighted average of the typical price over the bars given.

    Anchoring is the caller's job: pass `session_bars(...)` for the session VWAP
    every intraday trader means by the word. Returns None when nothing traded,
    which is the honest answer for an index (no volume) and for a dead strike.
    """
    volume_sum = 0.0
    weighted = 0.0
    for bar in bars:
        volume = float(getattr(bar, "volume", 0.0) or 0.0)
        if volume <= 0:
            continue
        typical = (float(bar.high) + float(bar.low) + float(bar.close)) / 3.0
        weighted += typical * volume
        volume_sum += volume
    return weighted / volume_sum if volume_sum > 0 else None


def true_range(bar: Any, previous: Optional[Any]) -> float:
    """The classic true range: today's span, widened by any gap from yesterday."""
    high, low = float(bar.high), float(bar.low)
    if previous is None:
        return high - low
    prev_close = float(previous.close)
    return max(high - low, abs(high - prev_close), abs(low - prev_close))


def atr(bars: Sequence[Any], period: int = 14) -> Optional[float]:
    """Average true range over the last `period` bars (simple mean of TR)."""
    if period < 1 or len(bars) < period + 1:
        return None
    ranges = [true_range(bars[i], bars[i - 1]) for i in range(len(bars) - period, len(bars))]
    return sum(ranges) / float(period)


def atr_percent(bars: Sequence[Any], period: int = 14) -> Optional[float]:
    """
    ATR as a percentage of the last close - the comparable form.

    An ATR of 40 means something different on BANKNIFTY at 57,000 than on NIFTY
    at 24,000, so a threshold in points cannot be shared between underlyings and
    a threshold in percent can.
    """
    value = atr(bars, period)
    if value is None:
        return None
    last = float(bars[-1].close)
    return (value / last) * 100.0 if last else None


def wilder_atr(bars: Sequence[Any], period: int = 14) -> Optional[float]:
    """
    Wilder's smoothed average true range, which is what Supertrend and ADX are
    defined on. `atr` above is the simple mean and stays as it is: changing it
    would move every threshold already chosen against it.
    """
    if period < 1 or len(bars) < period + 1:
        return None
    ranges = [true_range(bars[i], bars[i - 1]) for i in range(1, len(bars))]
    value = sum(ranges[:period]) / float(period)
    for tr in ranges[period:]:
        value = (value * (period - 1) + tr) / float(period)
    return value


def supertrend(bars: Sequence[Any], period: int = 10, multiple: float = 3.0) -> Optional[Dict[str, Any]]:
    """
    Supertrend over the bars given: {"direction": "bullish"|"bearish", "level"}.

    The bands are the median price plus and minus `multiple` ATRs; each band
    only tightens while the trend holds, and price closing through the active
    band flips the direction. None until there are enough bars to define it.
    """
    if period < 1 or len(bars) < period + 2:
        return None
    ranges = [true_range(bars[i], bars[i - 1]) for i in range(1, len(bars))]
    atr_value = sum(ranges[:period]) / float(period)
    direction = "bullish"
    upper = lower = None
    for index in range(period, len(bars)):
        bar = bars[index]
        if index > period:
            atr_value = (atr_value * (period - 1) + ranges[index - 1]) / float(period)
        median = (float(bar.high) + float(bar.low)) / 2.0
        band_up, band_down = median + multiple * atr_value, median - multiple * atr_value
        close, previous_close = float(bar.close), float(bars[index - 1].close)
        upper = band_up if upper is None or band_up < upper or previous_close > upper else upper
        lower = band_down if lower is None or band_down > lower or previous_close < lower else lower
        if direction == "bullish" and close < lower:
            direction, upper = "bearish", band_up
        elif direction == "bearish" and close > upper:
            direction, lower = "bullish", band_down
    return {"direction": direction, "level": round(lower if direction == "bullish" else upper, 2)}


def adx(bars: Sequence[Any], period: int = 14) -> Optional[Dict[str, float]]:
    """
    Wilder's ADX with both directional indicators: {"adx", "plus_di", "minus_di"}.

    ADX says how strongly the market is trending, the two DIs say which way. A
    filter that wants "a real trend, and it is against me" needs both numbers,
    so they are returned together.
    """
    if period < 1 or len(bars) < 2 * period + 1:
        return None
    plus_moves, minus_moves, ranges = [], [], []
    for index in range(1, len(bars)):
        bar, previous = bars[index], bars[index - 1]
        up = float(bar.high) - float(previous.high)
        down = float(previous.low) - float(bar.low)
        plus_moves.append(up if up > down and up > 0 else 0.0)
        minus_moves.append(down if down > up and down > 0 else 0.0)
        ranges.append(true_range(bar, previous))

    def smooth(values: Sequence[float]) -> List[float]:
        out = [sum(values[:period])]
        for value in values[period:]:
            out.append(out[-1] - out[-1] / period + value)
        return out

    smoothed_tr, smoothed_plus, smoothed_minus = smooth(ranges), smooth(plus_moves), smooth(minus_moves)
    dx: List[float] = []
    for tr, plus, minus in zip(smoothed_tr, smoothed_plus, smoothed_minus):
        if tr <= 0:
            continue
        plus_di, minus_di = 100.0 * plus / tr, 100.0 * minus / tr
        total = plus_di + minus_di
        dx.append(100.0 * abs(plus_di - minus_di) / total if total else 0.0)
    if len(dx) < period:
        return None
    value = sum(dx[:period]) / float(period)
    for item in dx[period:]:
        value = (value * (period - 1) + item) / float(period)
    last_tr = smoothed_tr[-1]
    return {
        "adx": value,
        "plus_di": 100.0 * smoothed_plus[-1] / last_tr if last_tr else 0.0,
        "minus_di": 100.0 * smoothed_minus[-1] / last_tr if last_tr else 0.0,
    }


def opening_range(bars: Sequence[Any], minutes: int = 15) -> Optional[Dict[str, float]]:
    """The session's first `minutes` as {"high", "low"}; None before it is complete."""
    session = session_bars(bars)
    if not session:
        return None
    first = ist_of(session[0])
    if first is None:
        return None
    end = first + timedelta(minutes=minutes)
    inside = [b for b in session if (ist_of(b) or first) < end]
    if not inside or (ist_of(session[-1]) or first) < end:
        return None
    return {"high": max(float(b.high) for b in inside), "low": min(float(b.low) for b in inside)}


def move_from_open_percent(bars: Sequence[Any]) -> Optional[float]:
    """How far the last close is from the session's open, in percent."""
    session = session_bars(bars)
    if not session:
        return None
    open_price = float(session[0].open)
    return None if open_price == 0 else 100.0 * (float(session[-1].close) - open_price) / open_price


def return_percent(bars: Sequence[Any], lookback: int) -> Optional[float]:
    """The percentage move over the last `lookback` bars."""
    if lookback < 1 or len(bars) < lookback + 1:
        return None
    before = float(bars[-1 - lookback].close)
    return None if before == 0 else 100.0 * (float(bars[-1].close) - before) / before


def gap_percent(bars: Sequence[Any]) -> Optional[float]:
    """The session's open against the previous session's last close, in percent."""
    session = session_bars(bars)
    if not session:
        return None
    first_index = len(bars) - len(session)
    if first_index <= 0:
        return None
    previous_close = float(bars[first_index - 1].close)
    return None if previous_close == 0 else 100.0 * (float(session[0].open) - previous_close) / previous_close


def rsi(values: Sequence[float], period: int = 14) -> Optional[float]:
    """Wilder's RSI of the last value."""
    if period < 1 or len(values) < period + 1:
        return None

    gains, losses = 0.0, 0.0
    for i in range(1, period + 1):
        change = values[i] - values[i - 1]
        gains += max(change, 0.0)
        losses += max(-change, 0.0)
    avg_gain, avg_loss = gains / period, losses / period

    for i in range(period + 1, len(values)):
        change = values[i] - values[i - 1]
        avg_gain = (avg_gain * (period - 1) + max(change, 0.0)) / period
        avg_loss = (avg_loss * (period - 1) + max(-change, 0.0)) / period

    if avg_loss == 0:
        return 100.0 if avg_gain > 0 else 50.0
    rs = avg_gain / avg_loss
    return 100.0 - (100.0 / (1.0 + rs))


def volume_zscore(bars: Sequence[Any], lookback: int = 20) -> Optional[float]:
    """
    How unusual the last bar's volume is, in standard deviations of the previous
    `lookback` bars.

    This is the honest form of "volume spike". A fixed multiple of the average
    calls every bar a spike on a quiet day and none at all on a busy one,
    because it ignores how much volume normally varies.

    None when the instrument reports no volume - an index never does, so a
    volume filter must not be applied to index bars and expect signal.
    """
    if lookback < 2 or len(bars) < lookback + 1:
        return None

    history = [float(getattr(b, "volume", 0.0) or 0.0) for b in bars[-(lookback + 1):-1]]
    current = float(getattr(bars[-1], "volume", 0.0) or 0.0)
    if not any(history):
        return None

    mean = sum(history) / len(history)
    variance = sum((v - mean) ** 2 for v in history) / len(history)
    sd = variance ** 0.5
    if sd <= 0:
        # Perfectly flat history: "above average" is all that can be said.
        return 0.0 if current <= mean else float("inf")
    return (current - mean) / sd


# ---------------------------------------------------------- candle patterns --

@dataclass(frozen=True)
class Candle:
    """The shape of one bar, in the terms patterns are actually described in."""
    open: float
    high: float
    low: float
    close: float

    @property
    def body(self) -> float:
        return abs(self.close - self.open)

    @property
    def span(self) -> float:
        return self.high - self.low

    @property
    def upper_wick(self) -> float:
        return self.high - max(self.open, self.close)

    @property
    def lower_wick(self) -> float:
        return min(self.open, self.close) - self.low

    @property
    def bullish(self) -> bool:
        return self.close > self.open

    @property
    def bearish(self) -> bool:
        return self.close < self.open


def candle(bar: Any) -> Candle:
    return Candle(float(bar.open), float(bar.high), float(bar.low), float(bar.close))


def is_bullish_engulfing(bars: Sequence[Any]) -> bool:
    """Last bar is bullish and its body swallows the previous bearish body."""
    if len(bars) < 2:
        return False
    prev, last = candle(bars[-2]), candle(bars[-1])
    return (
        prev.bearish
        and last.bullish
        and last.close >= prev.open
        and last.open <= prev.close
        and last.body > prev.body
    )


def is_bearish_engulfing(bars: Sequence[Any]) -> bool:
    """Mirror of :func:`is_bullish_engulfing`."""
    if len(bars) < 2:
        return False
    prev, last = candle(bars[-2]), candle(bars[-1])
    return (
        prev.bullish
        and last.bearish
        and last.close <= prev.open
        and last.open >= prev.close
        and last.body > prev.body
    )


def is_hammer(bars: Sequence[Any], wick_ratio: float = 2.0) -> bool:
    """A small body at the top with a long lower wick - rejection of lower prices."""
    if not bars:
        return False
    c = candle(bars[-1])
    if c.span <= 0 or c.body <= 0:
        return False
    return c.lower_wick >= wick_ratio * c.body and c.upper_wick <= c.body


def is_shooting_star(bars: Sequence[Any], wick_ratio: float = 2.0) -> bool:
    """Mirror of :func:`is_hammer` - rejection of higher prices."""
    if not bars:
        return False
    c = candle(bars[-1])
    if c.span <= 0 or c.body <= 0:
        return False
    return c.upper_wick >= wick_ratio * c.body and c.lower_wick <= c.body


def is_marubozu(bars: Sequence[Any], body_ratio: float = 0.8) -> bool:
    """Body is most of the range: a bar that closed where it was going."""
    if not bars:
        return False
    c = candle(bars[-1])
    return c.span > 0 and (c.body / c.span) >= body_ratio


#: Pattern name -> predicate, so a filter can name one in configuration
#: without the config layer knowing any of this maths.
PATTERNS = {
    "bullish_engulfing": is_bullish_engulfing,
    "bearish_engulfing": is_bearish_engulfing,
    "hammer": is_hammer,
    "shooting_star": is_shooting_star,
    "marubozu": is_marubozu,
}

#: Which patterns argue for which direction, for a filter that only knows
#: "this signal is bullish".
BULLISH_PATTERNS = ("bullish_engulfing", "hammer", "marubozu")
BEARISH_PATTERNS = ("bearish_engulfing", "shooting_star", "marubozu")
