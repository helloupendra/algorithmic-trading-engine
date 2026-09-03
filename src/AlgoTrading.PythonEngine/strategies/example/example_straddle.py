from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class ExampleStraddleStrategy(BaseStrategy):
    """
    A simple example strategy that sells an At-The-Money (ATM) Straddle once.
    This serves as a template for users to understand how to build and plug in their own strategies.
    """
    name = "ExampleStraddle"
    description = (
        "Template strategy: sells one ATM straddle on the first tick that carries ATM contracts and then "
        "holds it for the rest of the run. It profits when the underlying stays near the entry strike, but "
        "its purpose is to show the minimum code needed to plug a strategy into the runner. Needs live spot "
        "ticks and the ATM CE/PE contracts."
    )
    category = "Example"
    legs_summary = "Sell ATM CE + Sell ATM PE"
    default_lots = 1
    default_params: Dict[str, Any] = {}

    def __init__(self, params: Dict[str, Any] = None):
        self.params = params or {}
        # Lots per leg; the platform multiplies by the contract's lot size.
        self.lots = self.lots_from(self.params, self.default_lots)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "is_invested": False,
            "group_id": None
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        
        # Simple example: just enter once and hold
        if not state.get("is_invested"):
            if "atm_ce" in inp.contracts and "atm_pe" in inp.contracts:
                ce = inp.contracts["atm_ce"]
                pe = inp.contracts["atm_pe"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason="Initial ATM Straddle entry",
                    metadata={"group_id": group_id},
                    legs=[
                        {"symbol": ce.symbol, "side": "SELL", "quantity": self.lots},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": self.lots}
                    ]
                ))
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
