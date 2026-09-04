from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, ContractRequirement, StrategyInput, StrategySignal
import uuid

class StrangleStrategy(BaseStrategy):
    """
    Short Strangle Strategy (Neutral/Volatility).
    Sells an Out-of-The-Money (OTM) Call and an OTM Put.
    Profits from a wider range of sideways movement than a Straddle, but collects lower premium.
    """
    name = "ShortStrangle"
    description = (
        "Sells one OTM call and one OTM put of the nearest expiry and holds them. Profits from a wider "
        "sideways range than a straddle while collecting less premium; loses when the underlying breaks "
        "out past either strike. Both strikes sit `otm_offset_steps` strikes away from the ATM strike "
        "on the underlying's own grid (2 by default)."
    )
    category = "Neutral"
    legs_summary = "Sell OTM CE + Sell OTM PE"
    default_lots = 1
    default_params: Dict[str, Any] = {"otm_offset_steps": 2}

    @classmethod
    def get_contract_requirements(cls, params: Dict[str, Any] = None) -> List[ContractRequirement]:
        """One OTM call and one OTM put, `otm_offset_steps` strikes out on each side."""
        return [
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm",
                                steps=2, param="otm_offset_steps"),
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
                        {"symbol": ce.symbol, "side": "SELL", "quantity": self.lots},
                        {"symbol": pe.symbol, "side": "SELL", "quantity": self.lots}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
