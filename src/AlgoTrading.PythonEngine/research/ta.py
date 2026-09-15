"""
research/ta.py

Causal indicator series for research: one forward pass over the bars, one value
per bar, each value computed only from that bar and the bars before it.

`strategies.indicators` answers "what is the value at the last bar" and is the
reference maths; calling it on every growing prefix is O(n^2), which is too
slow for a walk-forward over thousands of bars and a parameter grid. These
series reproduce the same numbers in O(n) and the tests check them against
`strategies.indicators` bar by bar where the definitions coincide.

Two deliberate differences from `strategies.indicators`, both documented where
they apply:

  - ADX/+DI/-DI are not in `strategies.indicators`; Wilder's definitions are
    implemented here.
  - `wilder_atr_series` smooths true range with Wilder's recursion (the ADX
    family's ATR) instead of the simple mean `indicators.atr` uses, and it can
    ignore the overnight gap (see `session_keys`).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, List, Optional, Sequence

from strategies.indicators import true_range


def ema_series(values: Sequence[float], period: int) -> List[Optional[float]]:
    """
    EMA at every index, identical to `indicators.ema(values[:i + 1], period)`:
    None until `period` values exist, seeded with their SMA, then recursive.
    """
    out: List[Optional[float]] = [None] * len(values)
    if period < 1 or len(values) < period:
        return out
    multiplier = 2.0 / (period + 1.0)
    value = sum(values[:period]) / float(period)
    out[period - 1] = value
    for i in range(period, len(values)):
        value = values[i] * multiplier + value * (1.0 - multiplier)
        out[i] = value
    return out


@dataclass(frozen=True)
class DirectionalPoint:
    """Wilder's directional movement numbers at one bar."""
    atr: Optional[float]
    plus_di: Optional[float]
    minus_di: Optional[float]
    adx: Optional[float]


def directional_series(
    bars: Sequence[Any],
    period: int = 14,
    session_keys: Optional[Sequence[Optional[str]]] = None,
) -> List[DirectionalPoint]:
    """
    Wilder ATR, +DI, -DI and ADX at every bar.

    Definitions (J. Welles Wilder, 1978):
      TR  = true range; +DM = up-move if it beats the down-move and is > 0;
      -DM = down-move likewise. TR14/+DM14/-DM14 start as the sum of the first
      `period` values, then X14 = X14 - X14/period + X.
      ATR = TR14 / period; +DI = 100 * +DM14 / TR14; -DI likewise;
      DX = 100 * |+DI - -DI| / (+DI + -DI); ADX starts as the mean of the first
      `period` DX values, then ADX = (ADX * (period - 1) + DX) / period.

    Readiness: ATR/DI from bar `period - 1`, ADX from bar `2 * period - 2`
    (the very first bar has no predecessor and contributes TR = high - low and
    no directional movement).

    `session_keys`: when given (one key per bar, e.g. the IST date), the first
    bar of each session is treated as having no predecessor - its TR is its
    own range and its DM is zero. The overnight gap is then not read as
    fourteen bars of volatility and direction; the smoothing itself still
    carries across sessions, so the series is warm at the open.
    """
    n = len(bars)
    out: List[DirectionalPoint] = [DirectionalPoint(None, None, None, None)] * n
    if period < 1 or n == 0:
        return out

    tr_sum = plus_sum = minus_sum = 0.0
    dx_values: List[float] = []
    adx: Optional[float] = None

    for i in range(n):
        bar = bars[i]
        previous = bars[i - 1] if i > 0 else None
        if previous is not None and session_keys is not None and session_keys[i] != session_keys[i - 1]:
            previous = None

        tr = true_range(bar, previous)
        if previous is None:
            plus_dm = minus_dm = 0.0
        else:
            up = float(bar.high) - float(previous.high)
            down = float(previous.low) - float(bar.low)
            plus_dm = up if (up > down and up > 0) else 0.0
            minus_dm = down if (down > up and down > 0) else 0.0

        if i < period:
            tr_sum += tr
            plus_sum += plus_dm
            minus_sum += minus_dm
            if i < period - 1:
                continue
        else:
            tr_sum = tr_sum - tr_sum / period + tr
            plus_sum = plus_sum - plus_sum / period + plus_dm
            minus_sum = minus_sum - minus_sum / period + minus_dm

        atr = tr_sum / period
        if tr_sum > 0:
            plus_di = 100.0 * plus_sum / tr_sum
            minus_di = 100.0 * minus_sum / tr_sum
        else:
            plus_di = minus_di = 0.0
        di_total = plus_di + minus_di
        dx = 100.0 * abs(plus_di - minus_di) / di_total if di_total > 0 else 0.0

        if adx is None:
            dx_values.append(dx)
            if len(dx_values) == period:
                adx = sum(dx_values) / period
        else:
            adx = (adx * (period - 1) + dx) / period

        out[i] = DirectionalPoint(atr=atr, plus_di=plus_di, minus_di=minus_di, adx=adx)
    return out


def median(values: Sequence[float]) -> Optional[float]:
    """Median of a non-empty sequence, None when empty."""
    if not values:
        return None
    ordered = sorted(values)
    mid = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[mid]
    return (ordered[mid - 1] + ordered[mid]) / 2.0
