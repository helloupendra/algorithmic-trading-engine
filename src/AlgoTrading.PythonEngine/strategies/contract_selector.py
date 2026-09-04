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

from strategies.base_strategy import ContractRequirement, OptionContract

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


# --- contract requirements (ATM / OTM / ITM) --------------------------------

def _as_number(value: Any) -> Optional[float]:
    """Float when `value` is a finite number (or its text), else None."""
    if value is None or isinstance(value, bool):
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    if number != number or number in (float("inf"), float("-inf")):
        return None
    return number


def requirement_distance(req: ContractRequirement, step: float, params: Optional[Dict[str, Any]] = None) -> float:
    """
    How far from the ATM strike `req` sits, in points on the underlying's grid.

    The run parameter named by `req.param` wins when it is set and > 0: a name
    ending in `_points` is read as absolute points, any other name as a number
    of strikes (multiplied by `step`). Otherwise `req.points` is used when set,
    else `req.steps * step`.
    """
    grid = float(step) if step and step > 0 else DEFAULT_STRIKE_STEP
    if req.param:
        override = _as_number((params or {}).get(req.param))
        if override is not None and override > 0:
            return override if req.param.endswith("_points") else override * grid
    if req.points is not None:
        points = _as_number(req.points)
        if points is not None:
            return max(0.0, points)
    steps = _as_number(req.steps) or 0.0
    return max(0.0, steps) * grid


def strike_for_requirement(req: ContractRequirement, atm: float, step: float,
                           params: Optional[Dict[str, Any]] = None) -> Strike:
    """
    The strike `req` resolves to for an ATM strike of `atm`.

    CE: OTM is above the ATM strike, ITM below it; PE is the mirror image. An
    "atm" requirement ignores the distance entirely. The result is snapped back
    onto the underlying's grid with `round_to_step`, so fractional grids (2.5)
    keep landing on real strikes.
    """
    moneyness = str(req.moneyness or "atm").strip().lower()
    option_type = str(req.option_type or "CE").strip().upper()
    centre = float(atm)
    if moneyness not in ("otm", "itm"):
        return round_to_step(centre, step)
    distance = requirement_distance(req, step, params)
    away = distance if moneyness == "otm" else -distance
    if option_type == "PE":
        away = -away
    return round_to_step(centre + away, step)


def describe_requirement(req: ContractRequirement, step: float, params: Optional[Dict[str, Any]] = None) -> str:
    """
    One human-readable line for the [CONFIG] log and the catalog, e.g.
    "otm_ce: OTM CE +2 strikes (+200 pts)" or "atm_ce: ATM CE".
    """
    moneyness = str(req.moneyness or "atm").strip().lower()
    option_type = str(req.option_type or "CE").strip().upper()
    head = f"{req.key}: {moneyness.upper()} {option_type}"
    if moneyness not in ("otm", "itm"):
        return head + (" (optional)" if req.optional else "")
    grid = float(step) if step and step > 0 else DEFAULT_STRIKE_STEP
    distance = requirement_distance(req, grid, params)
    sign = "+" if (moneyness == "otm") == (option_type != "PE") else "-"
    strikes = distance / grid if grid else 0.0
    text = f"{head} {sign}{format_strike(round(strikes, 4))} strikes ({sign}{format_strike(round(distance, 4))} pts)"
    if req.param:
        text += f" [param {req.param}]"
    if req.optional:
        text += " (optional)"
    return text


def strikes_for_requirements(requirements: List[ContractRequirement], atm: float, step: float,
                             params: Optional[Dict[str, Any]] = None) -> List[Tuple[ContractRequirement, Strike]]:
    """(requirement, strike) for every requirement, in declaration order."""
    return [(req, strike_for_requirement(req, atm, step, params)) for req in requirements or []]


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


class ExactContractCache:
    """
    Process-lifetime cache of `get_exact_contract` answers, keyed by
    (expiry, strike, option type).

    Definite answers (a contract, or a definite "the master does not have it")
    are cached; a lookup that raised is NOT, so the next tick retries instead of
    turning a transient API error into a permanent hole in the strategy's view.
    """

    def __init__(self, api_client: Any, underlying: str, log: Any = print) -> None:
        self.api = api_client
        self.underlying = (underlying or "").strip().upper()
        self.log = log
        self._answers: Dict[Tuple[str, Strike, str], Optional[Dict[str, Any]]] = {}
        self.lookups = 0
        self.failed_lookups = 0

    def get(self, expiry: str, strike: Strike, option_type: str) -> Optional[Dict[str, Any]]:
        key = (str(expiry), strike, str(option_type).upper())
        if key in self._answers:
            return self._answers[key]
        self.lookups += 1
        try:
            raw = self.api.get_exact_contract(
                underlying=self.underlying, expiry=key[0], strike=key[1], option_type=key[2]
            )
        except Exception as ex:
            self.failed_lookups += 1
            self.log(f"[CONTRACT] WARN: lookup failed for {key[0]} {format_strike(float(key[1]))} {key[2]}: {ex} (will retry)")
            return None
        answer = raw if raw and raw.get("symbol") else None
        self._answers[key] = answer
        return answer


def contracts_for_requirements(cache: ExactContractCache, requirements: List[ContractRequirement],
                               expiry_date: str, atm_strike: float, step: float,
                               params: Optional[Dict[str, Any]] = None,
                               on_missing: Any = None) -> Dict[str, OptionContract]:
    """
    {requirement key -> OptionContract} for every requirement the instrument
    master can satisfy at `expiry_date`.

    A key the master lacks is left out (the strategy sees the absence) and
    reported once through `on_missing(key, strike, option_type)` so the caller
    can log it without repeating the line on every tick.
    """
    contracts: Dict[str, OptionContract] = {}
    for req, strike in strikes_for_requirements(requirements, atm_strike, step, params):
        raw = cache.get(expiry_date, strike, req.option_type)
        if not raw:
            if on_missing is not None:
                on_missing(req.key, strike, str(req.option_type).upper())
            continue
        contracts[req.key] = map_contract(raw)
    return contracts
