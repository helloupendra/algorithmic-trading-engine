"""
strategies/directional/trend_rider_v2.py

Trend Rider V2: TrendRider with the three changes the owner asked for on 16-17 Nov 2026.

  1. **No half-hour wait.** TrendRider can enter only after the 09:15-09:45 range has formed, so a day
     that trends from the open has often run a long way before its first trade. With `levels: "prev_day"`
     (the default) V2 uses yesterday's high and low instead: from the first closed candle, a close above
     yesterday's high and the day's open buys the call, a close below yesterday's low and the open buys
     the put. `levels: "opening_range"` keeps TrendRider's own entry.
  2. **A stop-loss on the index.** The position is sold when the index has moved `index_stop_adr` x the
     average daily range (ADR) against the entry. It is a stop on the underlying, not on the premium: a
     premium stop of 30-40% sold the trend days in their first dip.
  3. **Keep a big profit.** Once the index is `trail_arm_adr` x ADR in the trade's favour, the position
     is sold when the index gives back `trail_giveback_adr` x ADR from its best.

Both exits are checked on every call, which live is every tick (the spot), so they act within seconds;
TrendRider's own exit (a candle closing back across the day's open) and the run's square-off still
apply. Everything else is TrendRider's: the day filters, one position at a time, re-entry only on a new
day extreme, up to `max_entries` a day. Setting `index_stop_adr` or `trail_arm_adr` to 0 turns that exit
off.

Status (17 Nov 2026): put on the desk on paper at the owner's request, to be watched live. The
combination has not passed the research checks that TrendRider's other changes were held to; see
docs/strategies/TrendRiderV2.md, Limitations.
"""

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal
from strategies.directional.trend_rider import BAR_MINUTES, TrendRiderStrategy, _compact, _ist


class TrendRiderV2Strategy(TrendRiderStrategy):
    name = "TrendRiderV2"
    description = (
        "TrendRider with an earlier entry, an index stop-loss and an index trailing exit: from 09:15 a close beyond "
        "yesterday's high (low) and the day's open buys the call (put) one strike in the money; it is sold when the "
        "index moves 0.5 x the average daily range against it, or gives back 0.35 x that range from its best once it "
        "is 0.5 x in favour, or when a candle closes back across the day's open."
    )
    default_params: Dict[str, Any] = {
        **TrendRiderStrategy.default_params,
        "levels": "prev_day",          # prev_day | opening_range
        "first_entry": "09:15",
        "index_stop_adr": 0.5,         # 0 turns the index stop off
        "trail_arm_adr": 0.5,          # 0 turns the trailing exit off
        "trail_giveback_adr": 0.35,
    }

    def __init__(self, params: Dict[str, Any] = None):
        params = dict(params or {})
        super().__init__(params)
        p = {**self.default_params, **params}
        self.levels = str(p["levels"]).strip().lower()
        if self.levels not in ("prev_day", "opening_range"):
            raise ValueError("levels must be prev_day or opening_range")
        try:
            self.index_stop_adr = float(p["index_stop_adr"])
            self.trail_arm_adr = float(p["trail_arm_adr"])
            self.trail_giveback_adr = float(p["trail_giveback_adr"])
        except (TypeError, ValueError) as ex:
            raise ValueError(f"index_stop_adr, trail_arm_adr and trail_giveback_adr must be numbers: {ex}") from ex
        if min(self.index_stop_adr, self.trail_arm_adr, self.trail_giveback_adr) < 0:
            raise ValueError("index_stop_adr, trail_arm_adr and trail_giveback_adr cannot be negative")
        if self.trail_arm_adr and not self.trail_giveback_adr:
            raise ValueError("trail_giveback_adr must be above 0 when the trailing exit is on")

    # ------------------------------------------------------------------ run ---
    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        exit_now = self._guard(state, inp)
        if exit_now is not None:
            return [exit_now]
        return super().on_bar(state, inp)

    def _guard(self, state: Dict[str, Any], inp: StrategyInput) -> Optional[StrategySignal]:
        """The index stop and the index trail, on every call: live, every tick of the spot."""
        position = state.get("position")
        if position is None or position.get("entry_spot") is None:
            return None
        meta = inp.metadata or {}
        if meta.get("source") == "warmup":
            return None
        open_groups = meta.get("open_groups")
        if open_groups is not None and position["group_id"] not in open_groups:
            return None                     # the run closed it: TrendRider's _sync frees the strategy
        spot = float(inp.spot_price or 0)
        if spot <= 0:
            return None
        buy = position["direction"] == "BUY"
        entry, adr = float(position["entry_spot"]), float(position["adr"])
        favour = spot - entry if buy else entry - spot
        position["best"] = max(float(position.get("best") or 0.0), favour)
        name = inp.underlying or "the index"
        reason = None
        if self.index_stop_adr and -favour >= self.index_stop_adr * adr:
            reason = (f"Trend Rider V2 stop: {name} {spot:g} is {-favour:.0f} points against the entry {entry:g}, "
                      f"past {self.index_stop_adr:g} x the {adr:.0f}-point average range")
        elif (self.trail_arm_adr and position["best"] >= self.trail_arm_adr * adr
              and position["best"] - favour >= self.trail_giveback_adr * adr):
            reason = (f"Trend Rider V2 trail: {name} {spot:g} gave back {position['best'] - favour:.0f} points from its "
                      f"best ({position['best']:.0f} in favour), past {self.trail_giveback_adr:g} x the "
                      f"{adr:.0f}-point average range")
        if reason is None:
            return None
        state["position"] = None
        now = _ist(inp.timestamp_utc)
        if now is not None:
            # No new entry from the candle this exit falls in: the same wait TrendRider keeps after its own exit.
            start = now.replace(minute=now.minute - now.minute % BAR_MINUTES, second=0, microsecond=0)
            state["busy_until"] = (start + timedelta(minutes=BAR_MINUTES)).strftime("%H:%M")
        return self._close(position, inp, reason)

    # ---------------------------------------------------------------- entry ---
    def _enter(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
               day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        signals = (super()._enter(state, inp, at, bar, day, day_open) if self.levels == "opening_range"
                   else self._enter_prev_day(state, inp, at, bar, day, day_open))
        if signals and state.get("position") is not None:
            ctx = signals[0].metadata.get("trend_rider") or {}
            state["position"].update(entry_spot=float(bar.close), adr=float(ctx["adr"]), best=0.0)
            ctx.update(entry_spot=float(bar.close), index_stop_adr=self.index_stop_adr,
                       trail_arm_adr=self.trail_arm_adr, trail_giveback_adr=self.trail_giveback_adr)
        return signals

    def _enter_prev_day(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
                        day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        hhmm = at.strftime("%H:%M")
        if state["count"] >= self.max_entries or not self.first_entry <= hhmm <= self.last_entry:
            return []
        if state["busy_until"] and hhmm < state["busy_until"]:
            return []
        adr, prev = self._context(state, at.date().isoformat())
        if adr is None or prev is None:
            self._skip(state, "no_history")
            return []
        lvl_hi, lvl_lo = prev[1], prev[2]
        side = ("BUY" if bar.close > lvl_hi and bar.close > day_open else
                "SELL" if bar.close < lvl_lo and bar.close < day_open else None)
        if side is None:
            return []
        buy = side == "BUY"
        day_hi, day_lo = max(b.high for b in day), min(b.low for b in day)
        hi_lo = day_hi if buy else day_lo
        extreme = state["extreme"]
        fresh = state["last_side"] != side or extreme is None or (hi_lo > extreme if buy else hi_lo < extreme)
        new_extreme = bar.high >= day_hi if buy else bar.low <= day_lo
        if not (fresh and (state["count"] == 0 or state["last_side"] != side or new_extreme)):
            return []
        prev_range = (prev[1] - prev[2]) / adr
        gap = (day_open - prev[3]) / prev[3] * 100 * (1 if buy else -1)
        if prev_range > self.skip_after_wide:
            self._skip(state, "after_wide_day")
            return []
        if self.max_prev_range and prev_range >= self.max_prev_range:
            self._skip(state, "yesterday_not_narrow")
            return []
        if (day_hi - day_lo) / adr < self.min_range_frac:
            self._skip(state, "range_too_small")
            return []
        if gap < -self.max_gap_against_pct:
            self._skip(state, "gap_against")
            return []
        contract = (inp.contracts or {}).get("buy_ce" if buy else "buy_pe")
        if (contract is None or not contract.symbol or contract.option_type.upper() != ("CE" if buy else "PE")
                or str(contract.expiry_date)[:10] < at.date().isoformat()):
            self._skip(state, "no_contract")
            return []
        state["opened"] += 1
        group_id = f"TR2_{_compact(inp.timestamp_utc)}_{state['opened']:03d}"
        state["position"] = {"group_id": group_id, "direction": side, "symbol": contract.symbol, "day": at.date().isoformat()}
        state["count"] += 1
        state["last_side"] = side
        state["extreme"] = hi_lo
        name = inp.underlying or "the index"
        context = {"levels": "prev_day", "prev_high": lvl_hi, "prev_low": lvl_lo, "day_open": day_open, "adr": round(adr, 2),
                   "prev_range_adr": round(prev_range, 3), "range_adr": round((day_hi - day_lo) / adr, 3),
                   "gap_pct": round(gap, 3), "entry_of_day": state["count"]}
        level = "yesterday's high" if buy else "yesterday's low"
        return [StrategySignal(
            strategy_name=self.name,
            signal_type="OPEN_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=(f"Trend Rider V2: {name} {bar.close:g} closed {'above' if buy else 'below'} {level} "
                    f"({lvl_hi if buy else lvl_lo:g}) and the day's open {day_open:g}; buying the {'call' if buy else 'put'}"),
            legs=[{"symbol": contract.symbol, "side": "BUY", "quantity": self.lots}],
            metadata={"group_id": group_id, "direction": side, "trend_rider": context},
        )]
