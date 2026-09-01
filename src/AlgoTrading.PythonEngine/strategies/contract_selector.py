from typing import Dict

from .base_strategy import OptionContract
from .execution_runner import map_contract


def build_atm_contracts(api_client, underlying: str, expiry_date: str, atm_strike: int) -> Dict[str, OptionContract]:
    """
    Fetches the exact CE and PE contracts for a given At-The-Money (ATM) strike from the platform API.
    These contracts are then passed into the strategy so it knows exactly what instruments it can trade.
    """
    atm_ce_raw = api_client.get_exact_contract(
        underlying=underlying,
        expiry=expiry_date,
        strike=atm_strike,
        option_type="CE",
    )
    atm_pe_raw = api_client.get_exact_contract(
        underlying=underlying,
        expiry=expiry_date,
        strike=atm_strike,
        option_type="PE",
    )

    return {
        "atm_ce": map_contract(atm_ce_raw),
        "atm_pe": map_contract(atm_pe_raw),
    }
