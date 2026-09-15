"""
baseline_ema_trend - the plain comparison every candidate has to beat.

Buy a CE when the index closes back above a rising EMA; buy a PE when it closes
below a falling one. No regime filter, no pattern, no chain data: if a
regime-aware idea cannot beat this after costs, the regime is not adding
anything. Exit on an index ATR stop or at 15:15, with a wide premium stop only
as a catastrophe guard.

Not tuned in the walk-forward (empty grid): a baseline that is optimised
stops being a baseline.
"""

from __future__ import annotations

from typing import Any, Mapping, Optional

from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Signal


def entry(ctx: BarContext) -> Optional[Signal]:
    period = int(ctx.params["ema_period"])
    now, before = ctx.ema(period), ctx.ema(period, back=1)
    previous = ctx.bar_back(1)
    if now is None or before is None or previous is None:
        return None
    close, prev_close = float(ctx.bar.close), float(previous.close)
    if prev_close <= before and close > now and now > before:
        return Signal(CE, f"close crossed above rising EMA({period})")
    if prev_close >= before and close < now and now < before:
        return Signal(PE, f"close crossed below falling EMA({period})")
    return None


def exits(params: Mapping[str, Any]) -> ExitPolicy:
    return ExitPolicy(
        stop_underlying_atr=float(params["atr_stop"]),
        stop_premium_pct=float(params["premium_stop_pct"]),
    )


CANDIDATE = Candidate(
    name="baseline_ema_trend",
    title="Baseline: EMA trend buy with an ATR stop",
    rules=("CE when the 5m close crosses above a rising EMA; PE when it crosses below a falling EMA. "
           "Exit when the index closes `atr_stop` ATRs against the signal close (next open), on a "
           "`premium_stop_pct` premium stop, or at 15:15. No regime filter."),
    defaults={"ema_period": 21, "atr_stop": 1.5, "premium_stop_pct": 40.0},
    parameter_notes={
        "ema_period": "21 bars (~1h45m of 5m bars): the same EMA the regime classifier uses, so the baseline "
                      "and the regime disagree only about filtering, not about the trend line.",
        "atr_stop": "1.5 ATR: outside one bar's ordinary noise (about 1 ATR), inside a real reversal.",
        "premium_stop_pct": "40%: a catastrophe guard for gaps and IV collapse, wide enough that the index "
                            "stop normally acts first.",
    },
    entry=entry,
    exits=exits,
    strike_offset=0,
)
