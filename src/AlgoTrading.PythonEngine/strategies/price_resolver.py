# market_python_files/strategies/price_resolver.py
import math
from dataclasses import dataclass
from typing import Dict, Any


@dataclass
class UnderlyingPriceContext:
    """
    Holds the current snapshot of the underlying spot price and the newly computed ATM strike.
    """
    underlying: str
    spot_symbol: str
    spot_price: float
    atm_strike: int
    timestamp_utc: str


def round_to_step(price: float, step: int = 100) -> int:
    """Rounds a given spot price to the nearest logical option strike step."""
    return int(math.ceil(price / step) * step)


def resolve_underlying_price_context(
    api_client,
    underlying: str,
    spot_symbol: str,
    strike_step: int = 100,
) -> UnderlyingPriceContext:
    """
    Queries the platform for the latest spot price of the underlying symbol,
    and calculates the current At-The-Money (ATM) strike.
    """
    quote = api_client.get_latest_quote(spot_symbol)

    spot_price = float(quote["lastTradedPrice"])
    atm_strike = round_to_step(spot_price, strike_step)

    return UnderlyingPriceContext(
        underlying=underlying,
        spot_symbol=spot_symbol,
        spot_price=spot_price,
        atm_strike=atm_strike,
        timestamp_utc=quote["updatedUtc"],
    )