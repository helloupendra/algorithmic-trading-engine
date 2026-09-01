from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class BearPutSpreadStrategy(BaseStrategy):
    """
    Bear Put Spread Strategy (Bearish).
    Buys an At-The-Money (ATM) Put, and Sells an Out-Of-The-Money (OTM) Put.
    Used when expecting a moderate drop in the underlying asset's price.
    """
    name = "BearPutSpread"

    def __init__(self, params: Dict[str, Any] = None):
        self.params = params or {}
        self.quantity = self.params.get("quantity", 15)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "is_invested": False,
            "group_id": None
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals = []
        
        if not state.get("is_invested"):
            # Using ATM put as the long leg and OTM put as the short leg
            if "atm_pe" in inp.contracts and "otm_pe" in inp.contracts:
                long_pe = inp.contracts["atm_pe"]
                short_pe = inp.contracts["otm_pe"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening Bear Put Spread. Buy PE:{long_pe.strike_price}, Sell PE:{short_pe.strike_price}",
                    metadata={"group_id": group_id, "strategy_type": "Bearish"},
                    legs=[
                        {"symbol": long_pe.symbol, "side": "BUY", "quantity": self.quantity},
                        {"symbol": short_pe.symbol, "side": "SELL", "quantity": self.quantity}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
