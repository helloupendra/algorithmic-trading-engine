from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class BullCallSpreadStrategy(BaseStrategy):
    """
    Bull Call Spread Strategy (Bullish).
    Buys an At-The-Money (ATM) or In-The-Money (ITM) Call, 
    and Sells an Out-Of-The-Money (OTM) Call.
    Used when expecting a moderate rise in the underlying asset's price.
    """
    name = "BullCallSpread"

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
            # Using ATM call as the long leg and OTM call as the short leg
            if "atm_ce" in inp.contracts and "otm_ce" in inp.contracts:
                long_ce = inp.contracts["atm_ce"]
                short_ce = inp.contracts["otm_ce"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening Bull Call Spread. Buy CE:{long_ce.strike_price}, Sell CE:{short_ce.strike_price}",
                    metadata={"group_id": group_id, "strategy_type": "Bullish"},
                    legs=[
                        {"symbol": long_ce.symbol, "side": "BUY", "quantity": self.quantity},
                        {"symbol": short_ce.symbol, "side": "SELL", "quantity": self.quantity}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
