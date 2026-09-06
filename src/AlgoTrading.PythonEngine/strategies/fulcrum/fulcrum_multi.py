from __future__ import annotations

from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
from strategies.fulcrum._direction import BUY, describe_for, direction_note, legs_summary_for, resolve_direction
from strategies.fulcrum._exit_rules import closing_legs, long_time_stop_close, resolve_long_exit
from strategies.strike_math import (
    hedge_strike,
    neighbour_strike,
    resolve_step,
    round_down_to_step,
    round_to_step,
    round_up_to_step,
    steps_from_params,
    steps_to_points,
)

class FulcrumMultiStraddleStrategy(BaseStrategy):
    """One to three short straddles around spot with far OTM wings; registered as FulcrumMulti50/70/90 by threshold."""
    name = "FulcrumMultiStraddle"
    description = (
        "Keeps one to three short straddles on the strikes around the spot and buys far OTM "
        "call/put wings about 3.5% away as protection. Small moves are ignored "
        "(minor threshold); once the spot drifts past the adjustment threshold the group is closed and rebuilt "
        "around the new level, keeping a nearby straddle when it is still within 1.5 strikes. Profits from "
        "premium decay in a slowly drifting market. Designed for BANKNIFTY-scale prices; needs live spot ticks "
        "and the full option chain."
    )
    category = "Adjustment"
    supported_underlyings = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX"]
    legs_summary = "Sell 1-3 straddles on the strikes around ATM + Buy far OTM CE and PE wings"
    default_lots = 1
    # Step multiples. On BANKNIFTY's 100-point grid these are the original
    # point values the variants are named after; on any other grid they mean
    # the same fraction of a strike.
    default_params: Dict[str, Any] = {"adjustment_steps": 0.5, "minor_steps": 0.1}

    def __init__(self, params: Optional[Dict[str, Any]] = None):
        self.params = params or {}
        self.adjustment_steps = steps_from_params(
            self.params, "adjustment_steps", "adjustment_threshold",
            self.default_params["adjustment_steps"],
        )
        self.minor_steps = steps_from_params(
            self.params, "minor_steps", "minor_threshold",
            self.default_params["minor_steps"],
        )
        # Lots per leg; the platform multiplies by the contract's lot size.
        self.lots = self.lots_from(self.params, self.default_lots)
        # Which way this variant trades, and whether it holds wings. Registered
        # twice: once as the seller it was written as, once as a buyer.
        self.direction, self.use_hedges = resolve_direction(self.params)
        # A buyer's quiet-market exit. The adjustment threshold already
        # realises a winner; nothing here closed a position that simply
        # sat still paying decay. See strategies/fulcrum/_exit_rules.py.
        self.long_exit = resolve_long_exit(self.params)
        self.description = (
            f"{describe_for(self.description, self.direction, self.use_hedges)} "
            f"{direction_note(self.direction, self.use_hedges)}"
        )
        self.legs_summary = legs_summary_for(self.legs_summary, self.direction, self.use_hedges)

        # The private factories register this class several times with different
        # thresholds; make the catalog text reflect the configured variant. The
        # thresholds are strikes, not points — the point value depends on the
        # underlying's grid, which is not known until a bar arrives.
        self.variant_label = f"{self.adjustment_steps * 100:g}"
        self.description = (
            f"{self.description} This variant re-centres after a {self.adjustment_steps:g}-strike move "
            f"and ignores moves under {self.minor_steps:g} of a strike "
            f"({self.adjustment_steps * 100:g} and {self.minor_steps * 100:g} points on a 100-point grid)."
        )

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "st0": 0,
            "st1": 0,
            "st2": 0,
            "current_group_id": None,
            "group_entry_utc": None,
            "current_group_legs": [],
            "signal_count": 0,
            "straddle_list": []
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []
        price = inp.spot_price

        # A buyer with nothing happening. The adjustment threshold below
        # only fires when the spot MOVES, so without this a long group in
        # a still market is never let go of at all.
        timed_out = long_time_stop_close(
            state, inp, self.direction, self.long_exit["max_hold_minutes"],
            self.name, ("st0", "st1", "st2", "straddle_list",))
        if timed_out:
            return timed_out

        step = resolve_step(inp.strike_step, self.params.get("strike_step"))
        atm_lower = round_down_to_step(price, step)
        atm_upper = round_up_to_step(price, step)
        atm = round_to_step(price, step)

        # The thresholds in points of THIS underlying.
        adjustment_threshold = steps_to_points(self.adjustment_steps, step)
        minor_threshold = steps_to_points(self.minor_steps, step)
        # A straddle within 1.5 strikes of the new level is kept rather than
        # closed and re-opened one strike away, which would pay the spread twice.
        keep_nearby = steps_to_points(1.5, step)

        if atm == price:
            return []

        st0 = state["st0"]
        st1 = state["st1"]
        st2 = state["st2"]

        if st0 == 0: st0 = st1
        if st2 == 0: st2 = st1

        # Check threshold
        if st1 > price:
            if (st1 - price) < minor_threshold:
                return []
            elif (st1 - price) < adjustment_threshold and neighbour_strike(st1, -1, step) == st0:
                return []
                
        if st1 < price:
            if (price - st1) < minor_threshold:
                return []
            elif (price - st1) < adjustment_threshold and neighbour_strike(st1, +1, step) == st2:
                return []

        if st1 != 0:
            st1 = atm

        ce_value = 0
        pe_value = 0

        if st1 < price:
            st1 = atm_lower
            st2 = atm_upper
            ce_value = st2
            pe_value = st1
            if st0 >= st1: st0 = 0
            if st2 <= st1: st2 = 0
            if st0 != 0 and max(st0 - price, price - st0) < keep_nearby:
                pe_value = st0
            else:
                st0 = 0
        else:
            st0 = atm_lower
            st1 = atm_upper
            ce_value = st1
            pe_value = st0
            if st0 >= st1: st0 = 0
            if st2 <= st1: st2 = 0
            if st2 != 0 and max(st2 - price, price - st2) < keep_nearby:
                ce_value = st2
            else:
                st2 = 0

        active_straddles = []
        if st0 > 0: active_straddles.append(st0)
        if st1 > 0: active_straddles.append(st1)
        if st2 > 0: active_straddles.append(st2)

        # Measured from the SPOT, not from the straddle strike it protects. A
        # hedge is a moneyness — "3.5% out" — and moneyness is relative to where
        # the underlying actually is. Anchoring to the outer strike instead adds
        # that strike's own offset, which is 0.18% on BANKNIFTY and invisible,
        # but 1.6% on a ₹150 stock and pushes the wing twice as far as asked.
        pe_buy_strike = hedge_strike(price, "PE", step)
        ce_buy_strike = hedge_strike(price, "CE", step)

        new_legs = []
        if self.use_hedges:
            new_legs.append({"symbol": f"{inp.underlying}_PE_{pe_buy_strike}", "side": "BUY", "quantity": self.lots, "price": None})
            new_legs.append({"symbol": f"{inp.underlying}_CE_{ce_buy_strike}", "side": "BUY", "quantity": self.lots, "price": None})
        
        for st in active_straddles:
            new_legs.append({"symbol": f"{inp.underlying}_CE_{st}", "side": self.direction, "quantity": self.lots, "price": None})
            new_legs.append({"symbol": f"{inp.underlying}_PE_{st}", "side": self.direction, "quantity": self.lots, "price": None})

        old_legs = state["current_group_legs"]
        old_group_id = state["current_group_id"]

        legs_changed = self._legs_differ(old_legs, new_legs)

        if legs_changed:
            if old_legs and old_group_id:
                close_legs = closing_legs(old_legs)
                signals.append(StrategySignal(
                    strategy_name=f"{self.name}{self.variant_label}",
                    signal_type="CLOSE_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason="Adjusting straddles",
                    price=price,
                    legs=close_legs,
                    metadata={"group_id": old_group_id}
                ))

            state["signal_count"] += 1
            new_group_id = self._group_id(inp.timestamp_utc, state["signal_count"])
            signals.append(StrategySignal(
                strategy_name=f"{self.name}{self.variant_label}",
                signal_type="OPEN_GROUP",
                timestamp_utc=inp.timestamp_utc,
                reason=f"Fulcrum Multi Straddle Adjusted. Active: {active_straddles}",
                price=price,
                legs=new_legs,
                metadata={"group_id": new_group_id}
            ))
            state["current_group_id"] = new_group_id
            state["group_entry_utc"] = inp.timestamp_utc
            state["current_group_legs"] = new_legs

        state["st0"] = st0
        state["st1"] = st1
        state["st2"] = st2

        return signals

    def _legs_differ(self, legs1: List[Dict], legs2: List[Dict]) -> bool:
        if len(legs1) != len(legs2): return True
        return {(l["symbol"], l["side"], l["quantity"]) for l in legs1} != {(l["symbol"], l["side"], l["quantity"]) for l in legs2}

    def _group_id(self, timestamp_utc: str, counter: int) -> str:
        safe_ts = timestamp_utc.replace(":", "").replace("-", "").replace("T", "").replace("Z", "")
        return f"FULCRUM-MULTI-{safe_ts}-{counter:03d}"