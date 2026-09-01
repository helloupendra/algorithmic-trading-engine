from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
import uuid

class StrangleStrategy(BaseStrategy):
    """
    Short Strangle Strategy (Neutral/Volatility).
    Sells an Out-of-The-Money (OTM) Call and an OTM Put.
    Profits from a wider range of sideways movement than a Straddle, but collects lower premium.
    """
    name = "ShortStrangle"

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
            # Assuming runner provides OTM contracts identified by 'otm_ce' and 'otm_pe'
            if "otm_ce" in inp.contracts and "otm_pe" in inp.contracts:
                ce = inp.contracts["otm_ce"]
                pe = inp.contracts["otm_pe"]
                
                group_id = str(uuid.uuid4())
                
                signals.append(StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening Short Strangle at strikes CE:{ce.strike_price}, PE:{pe.strike_price}",
                    metadata={"group_id": group_id, "strategy_type": "Neutral"},
                    legs=[
                        {"symbol": ce.symbol, "side": "SELL", "quantity": self.quantity},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": self.quantity}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
