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
apply.

  4. **Sold at 15:15, live as in the lab** (`square_off_ist`, from 20 Nov 2026 on the owner's word). Every
     backtest squares off at 15:15, but a live run held its position until the platform stopped it at the
     15:30 close (18 and 19 Nov 2026), so live and the lab could end the same trade at different prices.
     "auto" (the default) sells on the first tick at or after 15:15 on NSE and BSE and adds nothing on MCX
     (TrendRiderV2BE), whose run is squared off at its own close; "HH:MM" sets another time, "none" turns
     it off, and a run's own `index_exits.exit_after_ist` wins. In a backtest the engine's 15:15 square-off
     acts first, so the backtest's trades do not change. Everything else is TrendRider's, except that two of its day filters are off by default: "today's range is
still small" (`min_range_frac` 0) and "yesterday must have been narrow" (`max_prev_range` 0). The rest are kept: the
after-a-wide-day and gap-against filters, one position at a time, re-entry only on a new
day extreme, up to `max_entries` a day. Setting `index_stop_adr` or `trail_arm_adr` to 0 turns that exit
off.

Status (17 Nov 2026): put on the desk on paper at the owner's request, to be watched live. The
combination has not passed the research checks that TrendRider's other changes were held to; see
docs/strategies/TrendRiderV2.md, Limitations.
"""

from __future__ import annotations

from datetime import datetime
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal
from strategies.directional.trend_rider import TrendRiderStrategy, _compact
from strategies.index_exits import IndexExitRules


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
        # TrendRider's "today's range is still too small" filter (0.4 x ADR) is off: a break of yesterday's high or
        # low is already a range. Of the three combinations replayed for the desk (16-17 Nov 2026), this one had the
        # smallest deepest fall, the rule chosen before the replay.
        "min_range_frac": 0.0,
        # TrendRider's "yesterday must have been narrow" filter (filter D) is off: it kept V2 out of every day of the
        # 29 Oct - 12 Nov 2026 rally (run #965). Off, V2 was positive in all six periods of Sep 2024 - Sep 2026 and of
        # the untouched Aug 2020 - Aug 2024 (lab L8, 17 Nov), with ~3x the trades. On by the owner's word, 17 Nov.
        "max_prev_range": 0.0,
        # V2's stop and trail are its own three parameters below; "index_exits" only adds rules on top (TrendRider's
        # default stop and trail would otherwise override a changed index_stop_adr).
        "index_exits": None,
        "index_stop_adr": 0.5,         # 0 turns the index stop off
        "trail_arm_adr": 0.5,          # 0 turns the trailing exit off
        "trail_giveback_adr": 0.35,
        # When an open position is sold whatever it shows: "auto" is 15:15 on NSE/BSE (the backtest's square-off) and
        # nothing on MCX; "HH:MM" sets it; "none" turns it off. Live runs had held to the 15:30 close (18-19 Nov 2026).
        "square_off_ist": "auto",
    }

    #: The "auto" square-off of a market's session: NSE and BSE sell at 15:15, as every backtest does. MCX runs keep
    #: the platform's square-off at their own close (TrendRiderV2BE reads candles to 23:55 there).
    AUTO_SQUARE_OFF = {"nse": "15:15", "mcx": None}

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
        # The square-off time: "auto" until the market is known (V2 itself reads only the NSE session).
        self.square_off_param = self._square_off_param(p.get("square_off_ist"))
        self.square_off_from_run = bool(self.index_exits is not None and self.index_exits.exit_after_ist)
        # V2's own stop, trail and square-off time, with anything the run's "index_exits" sets on top (it wins where
        # both speak).
        rules = IndexExitRules(stop_adr=self.index_stop_adr, trail_arm_adr=self.trail_arm_adr,
                               trail_giveback_adr=self.trail_giveback_adr,
                               exit_after_ist=self.square_off_for("nse"))
        if self.index_exits is not None:
            rules = rules.merged(**{k: v for k, v in self.index_exits.__dict__.items() if v})
        self.index_exits = rules if rules.any_rule else None

    # ----------------------------------------------------------- square-off ---
    @staticmethod
    def _square_off_param(value: Any) -> str:
        """"auto", "none" or an HH:MM, from the run's square_off_ist."""
        text = str(value if value is not None else "auto").strip().lower()
        if text in ("", "auto"):
            return "auto"
        if text in ("none", "null", "off", "false"):
            return "none"
        try:
            return datetime.strptime(text, "%H:%M").strftime("%H:%M")
        except ValueError as ex:
            raise ValueError(f"square_off_ist must be auto, none or HH:MM, got {value!r}") from ex

    def square_off_for(self, market: str) -> Optional[str]:
        """The time an open position is sold on this market's session, or None for no such exit."""
        if self.square_off_param == "none":
            return None
        if self.square_off_param == "auto":
            return self.AUTO_SQUARE_OFF.get(market)
        return self.square_off_param

    # ---------------------------------------------------------------- entry ---
    def _enter(self, state: Dict[str, Any], inp: StrategyInput, at: datetime, bar: BarFrame,
               day: List[BarFrame], day_open: float) -> List[StrategySignal]:
        signals = (super()._enter(state, inp, at, bar, day, day_open) if self.levels == "opening_range"
                   else self._enter_prev_day(state, inp, at, bar, day, day_open))
        if signals and state.get("position") is not None:
            ctx = signals[0].metadata.get("trend_rider") or {}
            state["position"].update(entry_spot=float(bar.close), adr=float(ctx["adr"]), best=0.0)
            ctx.update(entry_spot=float(bar.close), index_exits=self.index_exits.describe() if self.index_exits else {})
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
