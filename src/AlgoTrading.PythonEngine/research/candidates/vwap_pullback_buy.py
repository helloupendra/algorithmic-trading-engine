"""
vwap_pullback_buy - buy the resumption after a pullback to VWAP in a trend.

In a trend, price leaves VWAP, comes back to test it, and goes again. Buying
the test's resumption enters with the trend at a nearer invalidation point than
chasing an extended bar. The pullback must reach VWAP's neighbourhood without
closing through it, and the entry bar must resume: close beyond the previous
bar's extreme, on the trend's side of VWAP.
"""

from __future__ import annotations

from typing import Any, Mapping, Optional

from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Signal
from research.regime import TREND_DOWN, TREND_UP

#: How many bars before the entry bar may hold the VWAP touch.
LOOKBACK_BARS = 3


def entry(ctx: BarContext) -> Optional[Signal]:
    r = ctx.regime
    if r.label not in (TREND_UP, TREND_DOWN) or not r.atr:
        return None
    previous = ctx.bar_back(1)
    if previous is None or ctx.session_bar < LOOKBACK_BARS:
        return None
    band = float(ctx.params["touch_atr"]) * r.atr
    close = float(ctx.bar.close)
    touched = False
    for back in range(1, LOOKBACK_BARS + 1):
        bar, reading = ctx.bar_back(back), ctx.regime_back(back)
        if bar is None or reading is None or reading.session != r.session:
            break
        if r.label == TREND_UP:
            if float(bar.close) < reading.vwap - band:
                return None  # closed through VWAP: a breakdown, not a pullback
            if float(bar.low) <= reading.vwap + band:
                touched = True
        else:
            if float(bar.close) > reading.vwap + band:
                return None
            if float(bar.high) >= reading.vwap - band:
                touched = True
    if not touched:
        return None
    if r.label == TREND_UP and close > float(previous.high) and close > r.vwap:
        return Signal(CE, f"TREND_UP pullback to VWAP {r.vwap:.1f}, resumed above {float(previous.high):.1f}")
    if r.label == TREND_DOWN and close < float(previous.low) and close < r.vwap:
        return Signal(PE, f"TREND_DOWN pullback to VWAP {r.vwap:.1f}, resumed below {float(previous.low):.1f}")
    return None


def exits(params: Mapping[str, Any]) -> ExitPolicy:
    return ExitPolicy(
        stop_premium_pct=float(params["stop_premium_pct"]),
        target_premium_pct=float(params["target_premium_pct"]),
        time_stop_bars=int(params["time_stop_bars"]),
        exit_on_regime_flip=True,
    )


CANDIDATE = Candidate(
    name="vwap_pullback_buy",
    title="VWAP pullback in a trend, bought on resumption",
    rules=(f"Regime TREND_UP: one of the previous {LOOKBACK_BARS} bars reached within `touch_atr` ATRs of the "
           "session VWAP without closing more than that below it, and this bar closes above the previous "
           "bar's high and above VWAP -> CE. Mirror for TREND_DOWN -> PE. Exit on a `stop_premium_pct` stop, "
           "a `target_premium_pct` target, `time_stop_bars`, a regime flip, or 15:15."),
    defaults={"touch_atr": 0.25, "stop_premium_pct": 25.0, "target_premium_pct": 60.0, "time_stop_bars": 18},
    parameter_notes={
        "touch_atr": "0.25 ATR: close enough to VWAP to be a test of it, loose enough that index bars "
                     "(which rarely print the exact level) can qualify.",
        "stop_premium_pct": "25%: the invalidation (a close through VWAP) is near, so the stop can be tighter "
                            "than a breakout's.",
        "target_premium_pct": "60%: a resumed trend leg on NIFTY moves an ATM premium this much when it works; "
                              "a target lets the trade bank it before theta and the afternoon.",
        "time_stop_bars": "18 bars (90 min): a resumption that has not paid in an hour and a half is "
                          "paying theta instead.",
    },
    entry=entry,
    exits=exits,
    strike_offset=0,
    grid={"touch_atr": [0.15, 0.35], "target_premium_pct": [40.0, 80.0]},
)
