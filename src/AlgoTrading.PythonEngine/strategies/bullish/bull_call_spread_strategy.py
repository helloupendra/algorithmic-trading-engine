from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, ContractRequirement, StrategyInput, StrategySignal
import uuid

class BullCallSpreadStrategy(BaseStrategy):
    """
    Bull Call Spread Strategy (Bullish).
    Buys an At-The-Money (ATM) or In-The-Money (ITM) Call, 
    and Sells an Out-Of-The-Money (OTM) Call.
    Used when expecting a moderate rise in the underlying asset's price.
    """
    name = "BullCallSpread"
    description = (
        "Buys the ATM call and sells an OTM call of the same expiry, a debit spread for a moderate rise. "
        "Profits as the underlying climbs towards the short strike, with both the maximum gain and the "
        "maximum loss fixed at entry. The short call sits `otm_offset_steps` strikes above the ATM strike "
        "on the underlying's own grid (2 by default)."
    )
    category = "Bullish"
    legs_summary = "Buy ATM CE + Sell OTM CE"
    default_lots = 1
    default_params: Dict[str, Any] = {"otm_offset_steps": 2}

    @classmethod
    def get_contract_requirements(cls, params: Dict[str, Any] = None) -> List[ContractRequirement]:
        """The ATM call to buy and the OTM call, `otm_offset_steps` strikes above it, to sell."""
        return [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm",
                                steps=2, param="otm_offset_steps"),
        ]

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
                        {"symbol": long_ce.symbol, "side": "BUY", "quantity": self.lots},
                        {"symbol": short_ce.symbol, "side": "SELL", "quantity": self.lots}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
