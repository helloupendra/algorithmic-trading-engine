from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class StraddleStrategy(BaseStrategy):
    """
    Short Straddle Strategy (Neutral/Volatility).
    Sells an At-The-Money (ATM) Call and an ATM Put simultaneously.
    Profits if the underlying stays near the strike price (low volatility / sideways market).
    """
    name = "ShortStraddle"

    def __init__(self, params: Dict[str, Any] = None):
        self.params = params or {}
        # Default quantity if not provided in params
        self.quantity = self.params.get("quantity", 15)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "is_invested": False,
            "group_id": None
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        
        # We only want to enter once in this basic implementation
        if not state.get("is_invested"):
            if "atm_ce" in inp.contracts and "atm_pe" in inp.contracts:
                ce = inp.contracts["atm_ce"]
                pe = inp.contracts["atm_pe"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening Short Straddle at strike {ce.strike_price}",
                    metadata={"group_id": group_id, "strategy_type": "Neutral"},
                    legs=[
                        {"symbol": ce.symbol, "side": "SELL", "quantity": self.quantity},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": self.quantity}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
