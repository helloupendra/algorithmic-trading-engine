"""
strategies/directional/trend_rider_v2_ob.py

Trend Rider V2 OB: TrendRiderV2BE with the two rules that passed the pre-registered test of 20 Nov 2026 (private
project repo, v2-obstop-be-caps-2026-11-20) and held on the untouched Aug 2020 - Aug 2024 data. TrendRiderV2 and
TrendRiderV2BE are not touched; this class only adds:

  1. **The stop at the order block** (OB+BE). V2 places its index stop 0.5 x the average daily range (ADR) against
     the entry. V2 OB places it at the order block: for a call, the low of the last 5-minute candle before the signal
     candle that closed down (bearish); for a put, the high of the last one that closed up. The distance is clamped
     to `ob_stop_min_adr` (0.15) .. `index_stop_adr` (0.5) x ADR from the entry, so it is never further than V2's
     own stop. It is checked on every tick, before the rest. V2 BE's breakeven step, V2's trail, the day-open exit
     and the 15:15 square-off are unchanged. V2's 0.5 x ADR stop stays behind it as a safety net; it can never act
     first, because the order-block stop is at or inside it.
  2. **A smaller trade after a wide day** (WIDEH), **off by default**. With `wide_prev_adr` above 0, when yesterday's
     range was at least that many x ADR, every entry of the day buys `wide_prev_size` (0.5) of the run's lots,
     rounded down, at least 1: 1 lot instead of 2. The sell is for the lots the position was bought with. The owner
     trades a fixed 2 lots ("size nhi change karni hai", 20 Nov 2026), so the default is 0: always the run's lots.

Research, NIFTY, simple P&L, 2 lots of 65, no charges (rules committed before any run):

    Sep 2024 - Sep 2026 (development)  V2 +2,53,025, deepest fall 65.9% of Rs 1 lakh; V2 BE +2,82,867 / 56.5%;
                                       OB+BE (this class) +3,14,665 / 55.9%; with WIDEH (1.0) +3,40,798 / 51.2%
    Aug 2020 - Aug 2024 (holdout)      V2 +3,98,320 / 60.8%; V2 BE +4,55,397 / 60.2%;
                                       OB+BE (this class) +4,69,573 / 56.6%; with WIDEH (1.0) +4,61,884 / 45.8%

OB+BE passed against V2 BE and held on the holdout. WIDEH passed as a size rule against V2.
Status (20 Nov 2026): a paper/testing version beside V2 and V2 BE, on the owner's word.
"""

from __future__ import annotations

import math
from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal
from strategies.directional.trend_rider import _ist
from strategies.directional.trend_rider_v2_be import TrendRiderV2BEStrategy


class TrendRiderV2OBStrategy(TrendRiderV2BEStrategy):
    name = "TrendRiderV2OB"
    description = (
        "TrendRiderV2BE with the stop that passed the 20 Nov 2026 test: the index stop sits at the order block (the "
        "last candle before the signal that closed against the trade, 0.15-0.5 x the average daily range from the "
        "entry) instead of 0.5 x ADR. The breakeven step, the trail and the day-open exit are V2 BE's; the run's lots "
        "are always bought. A paper/testing version beside V2 and V2 BE."
    )
    legs_summary = ("Buy CE 1 strike ITM on an up-trend day, or Buy PE 1 strike ITM on a down-trend day (itm_steps); "
                    "one position at a time")
    default_params: Dict[str, Any] = {
        **TrendRiderV2BEStrategy.default_params,
        # OB+BE: the stop at the order block, clamped to ob_stop_min_adr .. index_stop_adr x ADR from the entry.
        "ob_stop": True,
        "ob_stop_min_adr": 0.15,
        # WIDEH, off (0): after a day of at least wide_prev_adr x ADR, buy wide_prev_size of the lots (down, at least
        # 1). The tested setting is 1.0; the owner keeps a fixed 2 lots (20 Nov 2026), so it is off by default.
        "wide_prev_adr": 0.0,
        "wide_prev_size": 0.5,
    }

    def __init__(self, params: Dict[str, Any] = None):
        params = dict(params or {})
        super().__init__(params)
        p = {**self.default_params, **params}
        try:
            self.ob_stop = str(p["ob_stop"]).strip().lower() in ("1", "true", "yes", "on")
            self.ob_stop_min_adr = float(p["ob_stop_min_adr"] or 0)
            self.wide_prev_adr = float(p["wide_prev_adr"] or 0)
            self.wide_prev_size = float(p["wide_prev_size"] if p["wide_prev_size"] is not None else 1)
        except (TypeError, ValueError) as ex:
            raise ValueError(f"ob_stop_min_adr, wide_prev_adr and wide_prev_size must be numbers: {ex}") from ex
        if self.ob_stop and not self.index_stop_adr:
            raise ValueError("ob_stop needs index_stop_adr above 0: it is the order-block stop's widest distance")
        if self.ob_stop and not 0 <= self.ob_stop_min_adr <= self.index_stop_adr:
            raise ValueError("ob_stop_min_adr must be between 0 and index_stop_adr")
        if self.wide_prev_adr < 0 or not 0 < self.wide_prev_size <= 1:
            raise ValueError("wide_prev_adr cannot be negative and wide_prev_size must be above 0 and at most 1")

    # ---------------------------------------------------------------- entry ---
    def _enter(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
               day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        signals = super()._enter(state, inp, at, bar, day, day_open)
        position = state.get("position")
        if not signals or position is None or position.get("entry_spot") is None:
            return signals
        sig = signals[0]
        ctx = sig.metadata.get("trend_rider")
        ctx = ctx if isinstance(ctx, dict) else {}
        adr = float(position["adr"])
        entry = float(position["entry_spot"])

        lots = self.lots
        prev_range = ctx.get("prev_range_adr")
        if self.wide_prev_adr and prev_range is not None and float(prev_range) >= self.wide_prev_adr:
            lots = max(1, int(math.floor(self.lots * self.wide_prev_size + 1e-9)))
            if lots < self.lots:
                ctx["size_rule"] = (f"yesterday's range was {float(prev_range):g} x ADR (at least "
                                    f"{self.wide_prev_adr:g}): {lots} of {self.lots} lots")
        position["lots"] = lots
        ctx["lots"] = lots
        for leg in sig.legs:
            leg["quantity"] = lots

        if self.ob_stop:
            buy = position.get("direction") == "BUY"
            level = self._ob_stop_level(inp, buy, entry, adr)
            position["ob_stop"] = level
            ctx["ob_stop"] = round(level, 2)
            # The run page and the alerts draw the stop from index_exits.stop_adr: give them the order block's.
            exits = dict(ctx.get("index_exits") or {})
            exits["stop_adr"] = round(abs(entry - level) / adr, 4) if adr > 0 else exits.get("stop_adr")
            ctx["index_exits"] = exits
        return signals

    def _ob_stop_level(self, inp: StrategyInput, buy: bool, entry: float, adr: float) -> float:
        """The order block: the last candle before the signal candle closing against the trade (bearish for a call,
        bullish for a put); its low (high) is the stop, clamped to ob_stop_min_adr .. index_stop_adr x ADR."""
        closed = self._closed(inp)
        lo_d, hi_d = self.ob_stop_min_adr * adr, self.index_stop_adr * adr
        dist = hi_d
        for _, b in reversed(closed[:-1]):
            if (b.close < b.open) if buy else (b.close > b.open):
                dist = (entry - b.low) if buy else (b.high - entry)
                break
        dist = min(max(dist, lo_d), hi_d)
        return entry - dist if buy else entry + dist

    # ----------------------------------------------------------------- exits ---
    def _tick_exit(self, state: Dict[str, Any], inp: StrategyInput) -> Optional[StrategySignal]:
        position = state.get("position")
        if self.ob_stop and position is not None and position.get("ob_stop") is not None:
            meta = inp.metadata or {}
            open_groups = meta.get("open_groups")
            spot = float(inp.spot_price or 0)
            if (meta.get("source") != "warmup" and (open_groups is None or position["group_id"] in open_groups)
                    and spot > 0):
                level = float(position["ob_stop"])
                if (spot <= level) if position.get("direction") == "BUY" else (spot >= level):
                    state["position"] = None
                    now = _ist(inp.timestamp_utc)
                    if now is not None:
                        start = now.replace(minute=now.minute - now.minute % self.bar_minutes, second=0, microsecond=0)
                        state["busy_until"] = (start + timedelta(minutes=self.bar_minutes)).strftime("%H:%M")
                    name = inp.underlying or "the index"
                    return self._close(position, inp, f"{self.name} index stop (order block): {name} {spot:g} "
                                                      f"crossed {level:.2f}")
        return super()._tick_exit(state, inp)

    def _close(self, position: Dict[str, Any], inp: StrategyInput, reason: str) -> StrategySignal:
        """Sells the lots the position was bought with (half after a wide day), not the run's."""
        sig = super()._close(position, inp, reason)
        lots = position.get("lots")
        if lots:
            for leg in sig.legs:
                leg["quantity"] = int(lots)
        return sig

    def _relabel(self, sig: StrategySignal, inp: StrategyInput) -> StrategySignal:
        sig = super()._relabel(sig, inp)
        reason = sig.reason or ""
        if reason.startswith("Trend Rider V2 BE: "):
            sig.reason = "Trend Rider V2 OB: " + reason[len("Trend Rider V2 BE: "):]
        return sig
