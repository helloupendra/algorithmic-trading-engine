from __future__ import annotations

from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
from strategies.fulcrum._direction import BUY, describe_for, direction_note, legs_summary_for, resolve_direction
from strategies.fulcrum._exit_rules import closing_legs, long_time_stop_close, resolve_long_exit
from strategies.strike_math import (
    hedge_strike,
    neighbour_strike,
    resolve_step,
    round_to_step,
    steps_to_points,
)

class Fulcrum3Straddle175Strategy(BaseStrategy):
    """Three short straddles (ATM and its two neighbours) with far OTM wings, re-centred on a 1.75-step drift."""
    name = "Fulcrum3Straddle175"
    description = (
        "Sells three short straddles — at the ATM strike and the adjacent strike on either side — and buys far "
        "OTM call/put wings about 3.5% away as protection. The group is closed and rebuilt once the spot has "
        "drifted roughly 1.75 strikes from the outer straddle. Profits from premium decay while the spot stays "
        "inside the three-strike band. Distances scale with the underlying: on BANKNIFTY's 100-point grid that "
        "is the original 175 points, on NIFTY's 50-point grid it is 87.5. Needs live spot ticks and the full "
        "option chain."
    )
    category = "Adjustment"
    supported_underlyings = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX"]
    legs_summary = "Sell 3 straddles (ATM and both neighbours) + Buy far OTM CE and PE wings"
    default_lots = 1
    default_params: Dict[str, Any] = {}

    def __init__(self, params: Optional[Dict[str, Any]] = None):
        self.params = params or {}
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
        atm = round_to_step(price, step)
        atm_lower = neighbour_strike(atm, -1, step)
        atm_upper = neighbour_strike(atm, +1, step)

        # The re-centre band, in strikes rather than BANKNIFTY points: the
        # original 105 and 195 are 1.05 and 1.95 of a 100-point grid.
        inner_band = steps_to_points(1.05, step)
        outer_band = steps_to_points(1.95, step)

        if atm == price:
            return []

        st0 = state["st0"]
        st1 = state["st1"]
        st2 = state["st2"]

        if st0 == 0: st0 = st1
        if st2 == 0: st2 = st1

        # Check threshold
        if st1 > price:
            if max(st2 - price, price - st2) < inner_band:
                pass
            elif (st2 - price) < outer_band:
                return []

        if st1 < price:
            if max(st2 - price, price - st2) < inner_band:
                pass
            elif (price - st2) < outer_band:
                return []

        if st1 != 0:
            st1 = atm

        ce_value = 0
        pe_value = 0

        if st1 < price or st1 > price:
            st0 = atm 
            st1 = atm_lower
            st2 = atm_upper
            pe_value = st1
            ce_value = st2

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
                    strategy_name=self.name,
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
                strategy_name=self.name,
                signal_type="OPEN_GROUP",
                timestamp_utc=inp.timestamp_utc,
                reason=f"Fulcrum 3 Straddle Adjusted. Active: {active_straddles}",
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
        return f"FULCRUM-3-{safe_ts}-{counter:03d}"