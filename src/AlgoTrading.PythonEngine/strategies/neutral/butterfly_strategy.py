from typing import Dict, Any, List
from strategies.base_strategy import BaseStrategy, ContractRequirement, StrategyInput, StrategySignal
import uuid

class IronButterflyStrategy(BaseStrategy):
    """
    Iron Butterfly Strategy (Neutral).
    A 4-leg defined-risk strategy: 
    Sell ATM Call, Sell ATM Put (like a short straddle), 
    Buy OTM Call, Buy OTM Put (to cap the risk).
    """
    name = "IronButterfly"
    description = (
        "Four-leg, defined-risk neutral position: sells the ATM call and put (a short straddle) and buys "
        "an OTM call and an OTM put as protective wings. Profits when the underlying pins near the centre "
        "strike into expiry; the wings cap the loss on a large move. The wings sit `wing_offset_steps` "
        "strikes away from the ATM strike on the underlying's own grid (4 by default)."
    )
    category = "Neutral"
    legs_summary = "Sell ATM CE + Sell ATM PE + Buy OTM CE + Buy OTM PE"
    default_lots = 1
    default_params: Dict[str, Any] = {"wing_offset_steps": 4}

    @classmethod
    def get_contract_requirements(cls, params: Dict[str, Any] = None) -> List[ContractRequirement]:
        """The ATM body plus the two wings, `wing_offset_steps` strikes out on each side."""
        return [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="atm_pe", option_type="PE"),
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm",
                                steps=4, param="wing_offset_steps"),
            ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm",
                                steps=4, param="wing_offset_steps"),
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
                        {"symbol": atm_ce.symbol, "side": "SELL", "quantity": self.lots},
                        {"symbol": atm_pe.symbol, "side": "SELL", "quantity": self.lots},
                        {"symbol": otm_ce.symbol, "side": "BUY", "quantity": self.lots},
                        {"symbol": otm_pe.symbol, "side": "BUY", "quantity": self.lots}
                    ]
                ))
                
                state["is_invested"] = True
                state["group_id"] = group_id
                
        return signals
