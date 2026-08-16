from __future__ import annotations

import math
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal

def roundup_100(x: float) -> int:
    return int(math.ceil(x / 100.0)) * 100

def rounddown_100(x: float) -> int:
    return int(math.floor(x / 100.0)) * 100

class TitliQtyAdjustmentStrategy(BaseStrategy):
    name = "TitliQtyAdjustment"

    def __init__(self, params: Optional[Dict[str, Any]] = None):
        self.params = params or {}
        self.adjustment_threshold = int(self.params.get("adjustment_threshold", 70))
        self.minor_threshold = int(self.params.get("minor_threshold", 10))

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "st0": 0,
            "st1": 0,
            "st2": 0,
            "current_group_id": None,
            "current_group_legs": [],
            "signal_count": 0,
            "straddle_list": []
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []
        price = inp.spot_price

        atm_lower = rounddown_100(price)
        atm_upper = roundup_100(price)
        atm = int(round(price, -2))

        if atm == price:
            return []

        st0 = state["st0"]
        st1 = state["st1"]
        st2 = state["st2"]

        if st0 == 0: st0 = st1
        if st2 == 0: st2 = st1

        # Check threshold
        if st1 > price:
            if (st1 - price) < self.minor_threshold:
                return []
            elif (st1 - price) < self.adjustment_threshold and (st1 - 100) == st0:
                return []
                
        if st1 < price:
            if (price - st1) < self.minor_threshold:
                return []
            elif (price - st1) < self.adjustment_threshold and (st1 + 100) == st2:
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
            if st0 != 0 and max(st0 - price, price - st0) < 170:
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
            if st2 != 0 and max(st2 - price, price - st2) < 170:
                ce_value = st2
            else:
                st2 = 0

        active_straddles = []
        if st0 > 0: active_straddles.append(st0)
        if st1 > 0: active_straddles.append(st1)
        if st2 > 0: active_straddles.append(st2)

        pe_buy_strike = int(math.floor((pe_value - 2000) / 500.0) * 500)
        ce_buy_strike = int(math.ceil((ce_value + 2000) / 500.0) * 500)

        # Apply QTY adjustment if 3 straddles are active
        qty_multiplier = 1
        if len(active_straddles) == 3:
            qty_multiplier = 2  # The original script doubles up some legs when there are 3 straddles.

        new_legs = []
        new_legs.append({"symbol": f"{inp.underlying}_PE_{pe_buy_strike}", "side": "BUY", "quantity": 1, "price": None})
        new_legs.append({"symbol": f"{inp.underlying}_CE_{ce_buy_strike}", "side": "BUY", "quantity": 1, "price": None})
        
        for st in active_straddles:
            new_legs.append({"symbol": f"{inp.underlying}_CE_{st}", "side": "SELL", "quantity": qty_multiplier, "price": None})
            new_legs.append({"symbol": f"{inp.underlying}_PE_{st}", "side": "SELL", "quantity": qty_multiplier, "price": None})

        old_legs = state["current_group_legs"]
        old_group_id = state["current_group_id"]

        legs_changed = self._legs_differ(old_legs, new_legs)

        if legs_changed:
            if old_legs and old_group_id:
                close_legs = [{"symbol": l["symbol"], "side": "BUY" if l["side"] == "SELL" else "SELL", "quantity": l["quantity"], "price": None} for l in old_legs]
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
                reason=f"Titli Qty Adjusted. Active: {active_straddles}, Qty: {qty_multiplier}",
                price=price,
                legs=new_legs,
                metadata={"group_id": new_group_id}
            ))
            state["current_group_id"] = new_group_id
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
        return f"TITLI-QTY-{safe_ts}-{counter:03d}"