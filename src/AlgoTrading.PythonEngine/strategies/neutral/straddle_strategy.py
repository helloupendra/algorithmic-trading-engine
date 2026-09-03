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
    description = (
        "Sells one ATM call and one ATM put of the nearest expiry as a single group and holds them. "
        "Profits when the underlying stays close to the entry strike and time decay erodes both premiums; "
        "loses on a sharp move in either direction. Needs live spot ticks plus the ATM CE/PE contracts "
        "the runner supplies."
    )
    category = "Neutral"
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
                        {"symbol": ce.symbol, "side": "SELL", "quantity": self.lots},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": self.lots}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
