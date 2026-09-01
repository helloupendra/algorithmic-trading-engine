from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class IronButterflyStrategy(BaseStrategy):
    """
    Iron Butterfly Strategy (Neutral).
    A 4-leg defined-risk strategy: 
    Sell ATM Call, Sell ATM Put (like a short straddle), 
    Buy OTM Call, Buy OTM Put (to cap the risk).
    """
    name = "IronButterfly"

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
            # Requires both ATM and OTM contracts for the 4 wings
            req_keys = ["atm_ce", "atm_pe", "otm_ce", "otm_pe"]
            
            if all(k in inp.contracts for k in req_keys):
                atm_ce = inp.contracts["atm_ce"]
                atm_pe = inp.contracts["atm_pe"]
                otm_ce = inp.contracts["otm_ce"]
                otm_pe = inp.contracts["otm_pe"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening Iron Butterfly. Center: {atm_ce.strike_price}, Wings: CE:{otm_ce.strike_price} PE:{otm_pe.strike_price}",
                    metadata={"group_id": group_id, "strategy_type": "Neutral"},
                    legs=[
                        {"symbol": atm_ce.symbol, "side": "SELL", "quantity": self.quantity},
                        {"symbol": atm_pe.symbol, "side": "SELL", "quantity": self.quantity},
                        {"symbol": otm_ce.symbol, "side": "BUY", "quantity": self.quantity},
                        {"symbol": otm_pe.symbol, "side": "BUY", "quantity": self.quantity}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
