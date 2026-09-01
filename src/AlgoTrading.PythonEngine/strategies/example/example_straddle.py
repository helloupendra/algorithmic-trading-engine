from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class ExampleStraddleStrategy(BaseStrategy):
    """
    A simple example strategy that sells an At-The-Money (ATM) Straddle once.
    This serves as a template for users to understand how to build and plug in their own strategies.
    """
    name = "ExampleStraddle"

    def __init__(self, params: Dict[str, Any] = None):
        self.params = params or {}

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
                        {"symbol": ce.symbol, "side": "SELL", "quantity": 15},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": 15}
                    ]
                ))
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
