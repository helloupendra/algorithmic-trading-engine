from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, ContractRequirement, StrategyInput, StrategySignal
import uuid

class BearPutSpreadStrategy(BaseStrategy):
    """
    Bear Put Spread Strategy (Bearish).
    Buys an At-The-Money (ATM) Put, and Sells an Out-Of-The-Money (OTM) Put.
    Used when expecting a moderate drop in the underlying asset's price.
    """
    name = "BearPutSpread"
    description = (
        "Buys the ATM put and sells an OTM put of the same expiry, a debit spread for a moderate decline. "
        "Profits as the underlying falls towards the short strike, with both the maximum gain and the "
        "maximum loss fixed at entry. The short put sits `otm_offset_steps` strikes below the ATM strike "
        "on the underlying's own grid (2 by default)."
    )
    category = "Bearish"
    legs_summary = "Buy ATM PE + Sell OTM PE"
    default_lots = 1
    default_params: Dict[str, Any] = {"otm_offset_steps": 2}

    @classmethod
    def get_contract_requirements(cls, params: Dict[str, Any] = None) -> List[ContractRequirement]:
        """The ATM put to buy and the OTM put, `otm_offset_steps` strikes below it, to sell."""
        return [
            ContractRequirement(key="atm_pe", option_type="PE"),
            ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm",
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
                        {"symbol": long_pe.symbol, "side": "BUY", "quantity": self.lots},
                        {"symbol": short_pe.symbol, "side": "SELL", "quantity": self.lots}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
