"""
strategies/builder/conditions.py

The conditions a hand-built strategy is made of, written as short text so a run
carries its own rules and a report can print them back exactly as they were set.

    close>vwap            the bar closed above the session VWAP
    close<ema:20          …below the 20 EMA
    ema:9>ema:21          the fast EMA is above the slow one
    rsi>60 / rsi<40       RSI(14) level
    rsi_cross_up:60       RSI crossed that level on THIS bar (the momentum spike)
    rsi_cross_down:40     the mirror
    body>=0.5             the candle's body is at least half its range
    green / red           the candle closed up / down
    supertrend:bullish    Supertrend(10,3) direction
    adx>20                trend strength, whichever way it points
    break_high:15         closed above the first 15 minutes' high
    break_low:15          …below its low
    above_open / below_open   against the session's own open
    gap>0.5 / gap<0.5     the session's opening gap, in percent
    volume_z>1.5          volume z-score of the bar (indices carry none)

A condition whose input cannot be computed yet — an EMA before its period, VWAP
on an index with no volume — is FALSE, never true by default: a setup that
cannot be checked has not happened.

Pure functions over bar frames; no I/O, no state.
"""

from __future__ import annotations

import re
from typing import Any, Callable, List, Optional, Sequence, Tuple

from strategies import indicators

#: Supertrend's parameters when a condition does not name its own.
SUPERTREND = (10, 3.0)
RSI_PERIOD = 14
ADX_PERIOD = 14

_NUMBER = r"-?\d+(?:\.\d+)?"
_PATTERN = re.compile(
    rf"^(?P<left>[a-z_]+(?::{_NUMBER})?)\s*(?P<op>>=|<=|>|<|=|:)?\s*(?P<right>[a-z_]+(?::{_NUMBER})?|{_NUMBER})?$"
)


class ConditionError(ValueError):
    """A condition that cannot be read, named in the message."""


def parse_all(text: Any) -> List[str]:
    """A conditions parameter (list, or one comma/newline separated string) -> the list."""
    if text is None or text == "":
        return []
    if isinstance(text, (list, tuple)):
        items = [str(x) for x in text]
    else:
        items = re.split(r"[,\n]", str(text))
    return [item.strip().lower() for item in items if item.strip()]


def _value(name: str, bars: Sequence[Any]) -> Optional[float]:
    """A named quantity on the closed bars: 'close', 'vwap', 'ema:20', 'rsi', 'adx'…"""
    key, _, argument = name.partition(":")
    number = float(argument) if argument else None
    last = bars[-1]
    if key == "close":
        return float(last.close)
    if key == "open":
        return float(last.open)
    if key == "high":
        return float(last.high)
    if key == "low":
        return float(last.low)
    if key == "vwap":
        return indicators.vwap(indicators.session_bars(bars))
    if key == "ema":
        return None if number is None else indicators.ema(indicators.closes(bars), int(number))
    if key == "sma":
        return None if number is None else indicators.sma(indicators.closes(bars), int(number))
    if key == "rsi":
        return indicators.rsi(indicators.closes(bars), int(number or RSI_PERIOD))
    if key == "adx":
        reading = indicators.adx(bars, int(number or ADX_PERIOD))
        return None if reading is None else float(reading["adx"])
    if key == "atr_percent":
        return indicators.atr_percent(bars, int(number or 14))
    if key == "body":
        candle = indicators.candle(last)
        return None if candle.span <= 0 else candle.body / candle.span
    if key == "gap":
        return indicators.gap_percent(bars)
    if key == "volume_z":
        return indicators.volume_zscore(bars, int(number or 20))
    if key == "move_from_open":
        return indicators.move_from_open_percent(bars)
    if key == "session_open":
        session = indicators.session_bars(bars)
        return float(session[0].open) if session else None
    return None


def _compare(op: str, left: Optional[float], right: Optional[float]) -> bool:
    if left is None or right is None:
        return False
    if op == ">":
        return left > right
    if op == "<":
        return left < right
    if op == ">=":
        return left >= right
    if op == "<=":
        return left <= right
    return left == right


def evaluate(condition: str, bars: Sequence[Any]) -> bool:
    """Whether one condition holds on the last closed bar. Unreadable input raises."""
    text = condition.strip().lower()
    if not bars:
        return False
    if text in ("green", "red"):
        last = bars[-1]
        return float(last.close) > float(last.open) if text == "green" else float(last.close) < float(last.open)
    if text in ("above_open", "below_open"):
        move = indicators.move_from_open_percent(bars)
        return False if move is None else (move > 0 if text == "above_open" else move < 0)

    match = _PATTERN.match(text)
    if match is None:
        raise ConditionError(f"cannot read the condition {condition!r}")
    left, op, right = match.group("left"), match.group("op"), match.group("right")

    if left.startswith("rsi_cross_"):
        level = float(right) if right else (float(left.partition(":")[2]) if ":" in left else None)
        if level is None:
            raise ConditionError(f"{condition!r} needs a level, e.g. rsi_cross_up:60")
        closes = indicators.closes(bars)
        now, before = indicators.rsi(closes, RSI_PERIOD), indicators.rsi(closes[:-1], RSI_PERIOD)
        if now is None or before is None:
            return False
        return before <= level < now if left.startswith("rsi_cross_up") else before >= level > now

    if left.startswith("supertrend"):
        want = (right or "").strip()
        if want not in ("bullish", "bearish"):
            raise ConditionError(f"{condition!r} must say supertrend:bullish or supertrend:bearish")
        reading = indicators.supertrend(bars, *SUPERTREND)
        return reading is not None and reading["direction"] == want

    if left.startswith("break_"):
        minutes = int(float(right)) if right else 15
        window = indicators.opening_range(bars, minutes)
        if window is None:
            return False
        close = float(bars[-1].close)
        return close > window["high"] if left == "break_high" else close < window["low"]

    if op is None or right is None:
        raise ConditionError(f"the condition {condition!r} compares nothing")

    right_value = float(right) if re.fullmatch(_NUMBER, right) else _value(right, bars)
    return _compare(op, _value(left, bars), right_value)


def compile_all(conditions: Sequence[str]) -> Callable[[Sequence[Any]], bool]:
    """
    Every condition as one predicate: all of them must hold. An empty list never
    fires — a side with no conditions is a side the strategy does not trade.
    """
    items = list(conditions)
    for condition in items:                       # fail loudly at build time, not mid-run
        evaluate(condition, [])
    def holds(bars: Sequence[Any]) -> bool:
        return bool(items) and all(evaluate(condition, bars) for condition in items)
    return holds


def describe(conditions: Sequence[str]) -> str:
    return " and ".join(conditions) if conditions else "nothing"


def explain(conditions: Sequence[str], bars: Sequence[Any]) -> List[Tuple[str, bool]]:
    """Each condition with whether it held, for the line a run logs when it does not trade."""
    return [(condition, evaluate(condition, bars)) for condition in conditions]
