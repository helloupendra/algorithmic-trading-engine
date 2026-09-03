"""
strategies/contract_selector.py

Strike-grid and option-contract helpers shared by the live runner
(`execution_runner.py`) and the backtest engine (`backtest/`).

Everything here is pure Python: no Redis, broker or metrics imports, so the
module is importable offline (the strategy registry still skips it during
discovery because it defines no strategy).
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional, Tuple, Union

from strategies.base_strategy import OptionContract

# Strike step per underlying when the option chain cannot be read from the API.
# Kept as floats end-to-end: stock options trade on 2.5 / 0.5 point grids, and
# the API (decimal strikeStep) and the launch dialog report exactly that value,
# so the runner must land on the same grid.
FALLBACK_STRIKE_STEPS: Dict[str, float] = {
    "NIFTY": 50.0,
    "BANKNIFTY": 100.0,
    "FINNIFTY": 50.0,
    "MIDCPNIFTY": 25.0,
    "SENSEX": 100.0,
}
DEFAULT_STRIKE_STEP = 50.0

Strike = Union[int, float]


def fallback_strike_step(underlying: str) -> float:
    return FALLBACK_STRIKE_STEPS.get((underlying or "").upper(), DEFAULT_STRIKE_STEP)


def strike_step_from_chain(chain: List[Dict[str, Any]]) -> Optional[float]:
    """
    Smallest positive gap between consecutive distinct strikes of one expiry,
    as a float so fractional grids survive (same rule as the C# underlyings
    endpoint). Returns None when the chain has fewer than two usable strikes.
    """
    strikes = set()
    for row in chain or []:
        raw = row.get("strikePrice")
        if raw is None:
            continue
        try:
            value = float(raw)
        except (TypeError, ValueError):
            continue
        if value > 0:
            strikes.add(value)

    ordered = sorted(strikes)
    if len(ordered) < 2:
        return None

    gaps = [b - a for a, b in zip(ordered, ordered[1:]) if b - a > 0]
    if not gaps:
        return None
    step = min(gaps)
    if step <= 0:
        return None
    # Six decimals is far below any exchange tick; it only removes float noise.
    return round(step, 6)


def round_to_step(price: float, step: float) -> Strike:
    """
    Nearest strike on the underlying's grid. Returns an int when the grid is
    whole-point (57600, not 57600.0) so symbols and log lines stay readable,
    and a float (102.5) on fractional grids.
    """
    step = float(step) if step and step > 0 else DEFAULT_STRIKE_STEP
    strike = round(round(price / step) * step, 6)
    if float(strike).is_integer():
        return int(strike)
    return strike


def format_strike(value: float) -> str:
    """102.5 stays 102.5; 57600.0 prints as 57600."""
    return str(int(value)) if float(value).is_integer() else f"{value:g}"


def normalise_strike(value: Any) -> Strike:
    """Numeric strike -> int on whole-point grids, float otherwise."""
    number = float(value)
    return int(number) if number.is_integer() else number


def map_contract(raw: Dict[str, Any]) -> OptionContract:
    """API contract row (camelCase) -> the strategy-facing OptionContract."""
    return OptionContract(
        symbol=raw["symbol"],
        underlying=raw.get("underlying", ""),
        expiry_date=str(raw.get("expiryDate", "")),
        strike_price=float(raw.get("strikePrice") or 0),
        option_type=raw.get("optionType", ""),
        instrument_type=raw.get("instrumentType", ""),
        description=raw.get("description", ""),
    )


def parse_logical_symbol(symbol: str) -> Optional[Tuple[str, str, Strike]]:
    """
    Decode the Titli-style logical leg symbol "BANKNIFTY_PE_50300" into
    (underlying, option_type, strike). Real broker symbols ("NSE:BANKNIFTY...")
    and anything else return None.
    """
    text = (symbol or "").strip()
    if not text or ":" in text or "_" not in text:
        return None
    parts = text.split("_")
    if len(parts) != 3:
        return None
    underlying, option_type, raw_strike = parts
    option_type = option_type.upper()
    if option_type not in ("CE", "PE"):
        return None
    try:
        strike = normalise_strike(raw_strike)
    except (TypeError, ValueError):
        return None
    return underlying.upper(), option_type, strike


def build_atm_contracts(api_client, underlying: str, expiry_date: str, atm_strike: Strike) -> Dict[str, OptionContract]:
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
