from __future__ import annotations

import math
from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal


def roundup_100(x: float) -> int:
    return int(math.ceil(x / 100.0)) * 100


def rounddown_100(x: float) -> int:
    return int(math.floor(x / 100.0)) * 100


class TitliStrategy(BaseStrategy):
    name = "Titli"

    def __init__(self, params: Optional[Dict[str, Any]] = None):
        self.params = params or {}
        self.strike_step = int(self.params.get("strike_step", 100))

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "ce_list": [],
            "pe_list": [],
            "straddle_list": [],
            "last_trade_strike": None,
            "signal_count": 0,
            "current_group_id": None,
            "current_group_legs": [],
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []

        atm = self._round_to_step(inp.spot_price)
        if inp.atm_strike is not None:
            atm = int(inp.atm_strike)

        previous_trade_strike = state["last_trade_strike"]

        atm_ce = inp.contracts.get("atm_ce")
        atm_pe = inp.contracts.get("atm_pe")

        if not atm_ce or not atm_pe:
            return []

        # First setup => open first short straddle group
        if previous_trade_strike is None:
            state["last_trade_strike"] = atm
            state["signal_count"] += 1

            group_id = self._group_id(inp.timestamp_utc, state["signal_count"])
            state["current_group_id"] = group_id
            state["current_group_legs"] = [
                {"symbol": atm_ce.symbol, "side": "SELL", "quantity": 1, "price": None},
                {"symbol": atm_pe.symbol, "side": "SELL", "quantity": 1, "price": None},
            ]

            state["straddle_list"] = [atm]
            state["ce_list"] = [atm]
            state["pe_list"] = [atm]

            signals.append(
                StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Initial Titli short straddle at ATM {atm}",
                    price=inp.spot_price,
                    legs=[
                        {"symbol": atm_ce.symbol, "side": "SELL", "quantity": 1, "price": None},
                        {"symbol": atm_pe.symbol, "side": "SELL", "quantity": 1, "price": None},
                    ],
                    metadata={
                        "group_id": group_id,
                        "atm_strike": atm,
                        "underlying": inp.underlying,
                    },
                )
            )

            return signals

        # ATM changed => close old group, open new group
        if atm != previous_trade_strike:
            old_group_id = state["current_group_id"]
            old_group_legs = state["current_group_legs"]

            # CLOSE old group
            if old_group_id and old_group_legs:
                close_legs = []
                for leg in old_group_legs:
                    reverse_side = "BUY" if leg["side"] == "SELL" else "SELL"
                    close_legs.append({
                        "symbol": leg["symbol"],
                        "side": reverse_side,
                        "quantity": leg["quantity"],
                        "price": None,
                    })

                signals.append(
                    StrategySignal(
                        strategy_name=self.name,
                        signal_type="CLOSE_GROUP",
                        timestamp_utc=inp.timestamp_utc,
                        reason=f"Closing previous group because ATM shifted from {previous_trade_strike} to {atm}",
                        price=inp.spot_price,
                        legs=close_legs,
                        metadata={
                            "group_id": old_group_id,
                            "previous_atm": previous_trade_strike,
                            "current_atm": atm,
                            "underlying": inp.underlying,
                        },
                    )
                )

            # OPEN new group
            state["last_trade_strike"] = atm
            state["signal_count"] += 1

            new_group_id = self._group_id(inp.timestamp_utc, state["signal_count"])
            new_group_legs = [
                {"symbol": atm_ce.symbol, "side": "SELL", "quantity": 1, "price": None},
                {"symbol": atm_pe.symbol, "side": "SELL", "quantity": 1, "price": None},
            ]

            state["current_group_id"] = new_group_id
            state["current_group_legs"] = new_group_legs

            state["straddle_list"] = [atm]
            state["ce_list"] = [atm]
            state["pe_list"] = [atm]

            signals.append(
                StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening new Titli short straddle at ATM {atm}",
                    price=inp.spot_price,
                    legs=new_group_legs,
                    metadata={
                        "group_id": new_group_id,
                        "atm_strike": atm,
                        "underlying": inp.underlying,
                    },
                )
            )

        return signals

    def _round_to_step(self, price: float) -> int:
        step = self.strike_step if self.strike_step > 0 else 100
        return int(math.ceil(price / step) * step)

    def _group_id(self, timestamp_utc: str, counter: int) -> str:
        safe_ts = timestamp_utc.replace(":", "").replace("-", "").replace("T", "").replace("Z", "")
        return f"TITLI-{safe_ts}-{counter:03d}"
