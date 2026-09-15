"""
orb_breakout_buy - opening-range breakout, only in the regime's direction.

The first 15 minutes set a range. A close beyond it in the direction the
regime classifier has called (TREND_UP -> above the high, TREND_DOWN -> below
the low) is the entry; a break against the regime is ignored. One trade per
session, and only in the morning, because an opening-range break at 14:00 is
not an opening-range break.
"""

from __future__ import annotations

from datetime import time
from typing import Any, Mapping, Optional

from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Signal
from research.regime import TREND_DOWN, TREND_UP

TRAIL_ARM_PCT = 20.0
TRAIL_GIVEBACK_PCT = 15.0


def _clock(value: Any) -> time:
    if isinstance(value, time):
        return value
    hour, minute = str(value).split(":")
    return time(int(hour), int(minute))


def entry(ctx: BarContext) -> Optional[Signal]:
    if ctx.state.get("orb_taken"):
        return None
    if ctx.ist.time() >= _clock(ctx.params["last_entry_ist"]):
        return None
    r = ctx.regime
    if r.or_high is None or r.or_low is None or not r.atr:
        return None
    buffer = float(ctx.params["buffer_atr"]) * r.atr
    close = float(ctx.bar.close)
    if r.label == TREND_UP and close > r.or_high + buffer:
        ctx.state["orb_taken"] = True
        return Signal(CE, f"close {close:.1f} above opening-range high {r.or_high:.1f} in TREND_UP")
    if r.label == TREND_DOWN and close < r.or_low - buffer:
        ctx.state["orb_taken"] = True
        return Signal(PE, f"close {close:.1f} below opening-range low {r.or_low:.1f} in TREND_DOWN")
    return None


def exits(params: Mapping[str, Any]) -> ExitPolicy:
    return ExitPolicy(
        stop_premium_pct=float(params["stop_premium_pct"]),
        trail_arm_pct=TRAIL_ARM_PCT,
        trail_giveback_pct=TRAIL_GIVEBACK_PCT,
        exit_on_regime_flip=True,
    )


CANDIDATE = Candidate(
    name="orb_breakout_buy",
    title="Opening-range breakout in the regime's direction",
    rules=("After the 09:15-09:30 range is set: CE when the regime is TREND_UP and a 5m close is "
           "`buffer_atr` ATRs above the range high; PE when TREND_DOWN and a close is that far below the "
           "low. Once per session, signals before `last_entry_ist`. Exit on a `stop_premium_pct` premium "
           f"stop, a trail (arms at +{TRAIL_ARM_PCT:.0f}%, gives back {TRAIL_GIVEBACK_PCT:.0f}% of entry), "
           "a regime flip, or 15:15."),
    defaults={"buffer_atr": 0.1, "last_entry_ist": "11:30", "stop_premium_pct": 30.0},
    parameter_notes={
        "buffer_atr": "0.1 ATR beyond the range: a close a hair over the high is noise; a tenth of an ATR "
                      "keeps the rule close to the textbook break.",
        "last_entry_ist": "11:30: a morning break still has the day to run; later breaks meet lunch and theta.",
        "stop_premium_pct": "30%: an ATM weekly premium usually loses 30% only when the break has failed, "
                            "not on an ordinary retest.",
    },
    entry=entry,
    exits=exits,
    strike_offset=0,
    grid={"buffer_atr": [0.0, 0.25], "stop_premium_pct": [25.0, 35.0]},
)
