"""
regime_momentum_buy - a strong candle in the regime's direction, ADX-filtered.

When the classifier has called a trend and ADX confirms real directional
strength, a large-bodied bar that closes near its extreme in the trend's
direction is momentum resuming. The option buyer needs exactly that - a fast
move - so the time stop is short: momentum that stalls is theta.
"""

from __future__ import annotations

from typing import Any, Mapping, Optional

from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Signal
from research.regime import TREND_DOWN, TREND_UP
from strategies.indicators import candle

#: The close must sit in the outer quarter of the bar's range.
CLOSE_LOCATION = 0.75
#: Bars after a signal before the next one may fire.
COOLDOWN_BARS = 6
TRAIL_ARM_PCT = 20.0
TRAIL_GIVEBACK_PCT = 15.0


def entry(ctx: BarContext) -> Optional[Signal]:
    r = ctx.regime
    if r.label not in (TREND_UP, TREND_DOWN) or not r.atr or r.adx is None:
        return None
    if r.adx < float(ctx.params["adx_min"]):
        return None
    last = ctx.state.get("last_signal_i")
    if last is not None and ctx.i - last <= COOLDOWN_BARS:
        return None
    c = candle(ctx.bar)
    if c.span <= 0 or c.body < float(ctx.params["body_atr"]) * r.atr:
        return None
    location = (c.close - c.low) / c.span
    if r.label == TREND_UP and c.bullish and location >= CLOSE_LOCATION and c.close > r.vwap:
        ctx.state["last_signal_i"] = ctx.i
        return Signal(CE, f"bull bar body {c.body:.1f} ({c.body / r.atr:.2f} ATR), ADX {r.adx:.0f}, TREND_UP")
    if r.label == TREND_DOWN and c.bearish and location <= 1 - CLOSE_LOCATION and c.close < r.vwap:
        ctx.state["last_signal_i"] = ctx.i
        return Signal(PE, f"bear bar body {c.body:.1f} ({c.body / r.atr:.2f} ATR), ADX {r.adx:.0f}, TREND_DOWN")
    return None


def exits(params: Mapping[str, Any]) -> ExitPolicy:
    return ExitPolicy(
        stop_premium_pct=float(params["stop_premium_pct"]),
        trail_arm_pct=TRAIL_ARM_PCT,
        trail_giveback_pct=TRAIL_GIVEBACK_PCT,
        time_stop_bars=int(params["time_stop_bars"]),
        exit_on_regime_flip=True,
    )


CANDIDATE = Candidate(
    name="regime_momentum_buy",
    title="Momentum candle in the regime's direction with an ADX filter",
    rules=("Regime TREND_UP and ADX >= `adx_min`: a bullish 5m bar whose body is at least `body_atr` ATRs, "
           f"closing in the top {int((1 - CLOSE_LOCATION) * 100)}% of its range and above VWAP -> CE. Mirror "
           f"for TREND_DOWN -> PE. {COOLDOWN_BARS}-bar cooldown between signals. Exit on a `stop_premium_pct` "
           f"stop, a trail (arms at +{TRAIL_ARM_PCT:.0f}%, gives back {TRAIL_GIVEBACK_PCT:.0f}% of entry), "
           "`time_stop_bars`, a regime flip, or 15:15."),
    defaults={"adx_min": 25.0, "body_atr": 0.6, "stop_premium_pct": 25.0, "time_stop_bars": 12},
    parameter_notes={
        "adx_min": "25: the conventional 'strong trend' line, stricter than the classifier's 20 so the "
                   "candidate only acts on the clearer half of trend bars.",
        "body_atr": "0.6 ATR of body: well above an ordinary 5m body (roughly a third of ATR) without "
                    "demanding a news bar.",
        "stop_premium_pct": "25%: momentum entries are wrong quickly when wrong.",
        "time_stop_bars": "12 bars (60 min): momentum that has not moved the premium in an hour is not "
                          "momentum.",
    },
    entry=entry,
    exits=exits,
    strike_offset=0,
    grid={"adx_min": [20.0, 30.0], "body_atr": [0.5, 0.8]},
)
