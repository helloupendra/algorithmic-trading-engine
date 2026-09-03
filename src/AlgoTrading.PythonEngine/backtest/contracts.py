"""
backtest/contracts.py

Option-contract resolution for the replay: expiries as-of a bar date, the
strike grid, ATM strikes and exact contracts (cached per run), plus the
Titli-style logical leg symbols ("BANKNIFTY_PE_50300") that the strategies
emit instead of broker symbols.

Only answers are cached. A lookup that fails (API error, timeout) is retried
on the next call and reported as "lookup failed", never as "not in the
instrument master" - the two mean different things to the reader.

The API is duck-typed: it needs `get_expiries`, `get_option_chain` and
`get_exact_contract` (see core/api_client.PlatformApiClient).
"""

from __future__ import annotations

from datetime import date
from typing import Any, Callable, Dict, List, Optional, Tuple, Union

from strategies.base_strategy import OptionContract
from strategies.contract_selector import (
    Strike,
    fallback_strike_step,
    format_strike,
    map_contract,
    parse_logical_symbol,
    round_to_step,
    strike_step_from_chain,
)

ContractKey = Tuple[str, Strike, str]


def _date_str(value: Union[date, str]) -> str:
    if isinstance(value, date):
        return value.isoformat()
    return str(value or "")[:10]


class ContractResolver:
    """Expiries, strike step, ATM strikes and exact contracts for one underlying."""

    def __init__(self, api: Any, underlying: str, log: Callable[[str], None] = print) -> None:
        self.api = api
        self.underlying = (underlying or "").strip().upper()
        self.log = log
        self._expiries: Optional[List[str]] = None
        self._step: Optional[float] = None
        self._step_source = ""
        self._contracts: Dict[ContractKey, Optional[OptionContract]] = {}
        self._failures: Dict[ContractKey, str] = {}     # last error per key, cleared on a successful lookup
        self.lookups = 0
        self.failed_lookups = 0

    # --- expiries -----------------------------------------------------------

    @property
    def expiries(self) -> List[str]:
        """All expiry dates (yyyy-MM-dd, ascending) the instrument master holds."""
        if self._expiries is None:
            rows = self.api.get_expiries(self.underlying) or []
            dates = set()
            for row in rows:
                raw = row.get("expiryDate") if isinstance(row, dict) else row
                text = _date_str(raw)
                if text:
                    dates.add(text)
            self._expiries = sorted(dates)
        return self._expiries

    def expiry_for(self, ist_day: Union[date, str]) -> Optional[str]:
        """Earliest expiry on or after the bar's IST date (None when the master has none)."""
        wanted = _date_str(ist_day)
        for expiry in self.expiries:
            if expiry >= wanted:
                return expiry
        return None

    # --- strike grid --------------------------------------------------------

    def step_for(self, expiry: Optional[str]) -> float:
        """
        Strike step from the option chain (resolved once), else the
        per-underlying fallback. `expiry` is only used on the first call.
        """
        if self._step is not None:
            return self._step
        candidates = [e for e in [expiry] if e] + [e for e in self.expiries if e != expiry]
        for candidate in candidates:
            try:
                chain = self.api.get_option_chain(self.underlying, candidate)
            except Exception as ex:
                self.log(f"[{self.underlying}] WARN: could not read option chain for {candidate}: {ex}")
                continue
            step = strike_step_from_chain(chain)
            if step:
                self._step = step
                self._step_source = f"chain {candidate} ({len(chain)} contracts)"
                self.log(f"[{self.underlying}] Strike step {format_strike(step)} derived from {self._step_source}")
                return step
            break
        self._step = fallback_strike_step(self.underlying)
        self._step_source = "fallback"
        self.log(f"[{self.underlying}] Using fallback strike step {format_strike(self._step)}")
        return self._step

    @property
    def step(self) -> float:
        return self.step_for(self.expiries[0] if self.expiries else None)

    @property
    def step_source(self) -> str:
        return self._step_source

    def atm(self, spot: float, step: Optional[float] = None) -> Strike:
        return round_to_step(float(spot), step if step else self.step)

    # --- contracts ----------------------------------------------------------

    @staticmethod
    def _key(expiry: str, strike: Strike, option_type: str) -> ContractKey:
        return (str(expiry), strike, str(option_type).upper())

    def contract(self, expiry: str, strike: Strike, option_type: str) -> Optional[OptionContract]:
        """
        Exact contract from the instrument master. Answers (found or a
        definite "missing") are cached; a failed lookup is not, so the next
        bar retries instead of treating a transient error as a data gap.
        """
        key = self._key(expiry, strike, option_type)
        if key in self._contracts:
            return self._contracts[key]
        self.lookups += 1
        try:
            raw = self.api.get_exact_contract(
                underlying=self.underlying, expiry=key[0], strike=key[1], option_type=key[2]
            )
        except Exception as ex:
            self.failed_lookups += 1
            self._failures[key] = f"{type(ex).__name__}: {ex}"
            self.log(f"[{self.underlying}] WARN: contract lookup failed for {key[0]} {key[1]} {key[2]}: {ex} (will retry)")
            return None
        contract = map_contract(raw) if raw and raw.get("symbol") else None
        self._contracts[key] = contract
        self._failures.pop(key, None)
        return contract

    def missing_reason(self, expiry: Optional[str], strike: Strike, option_type: str) -> str:
        """Why `contract()` returned None for this key, worded for the skip list."""
        side = str(option_type).upper()
        if expiry:
            error = self._failures.get(self._key(expiry, strike, side))
            if error:
                return f"contract lookup failed for {side} {format_strike(strike)} ({error})"
        return f"no {side} {format_strike(strike)} contract in the instrument master for expiry {expiry or 'n/a'}"

    def atm_contracts(self, expiry: str, atm_strike: Strike) -> Dict[str, OptionContract]:
        """{"atm_ce", "atm_pe"} for the strike, omitting sides the master lacks."""
        contracts: Dict[str, OptionContract] = {}
        for key, option_type in (("atm_ce", "CE"), ("atm_pe", "PE")):
            found = self.contract(expiry, atm_strike, option_type)
            if found:
                contracts[key] = found
        return contracts

    def resolve_logical(self, symbol: str, expiry: str) -> Optional[str]:
        """
        "BANKNIFTY_PE_50300" -> the broker symbol of that contract for `expiry`
        (None when the master lacks it). Real symbols come back unchanged.
        """
        parsed = parse_logical_symbol(symbol)
        if parsed is None:
            return symbol
        underlying, option_type, strike = parsed
        if underlying != self.underlying:
            self.log(f"[{self.underlying}] WARN: logical symbol {symbol} names another underlying")
        contract = self.contract(expiry, strike, option_type)
        return contract.symbol if contract else None

    def logical_missing_reason(self, symbol: str, expiry: Optional[str]) -> str:
        """Why `resolve_logical` returned None for this leg symbol."""
        parsed = parse_logical_symbol(symbol)
        if parsed is None:
            return f"{symbol!r} is neither a broker symbol nor a logical UNDERLYING_CE_STRIKE symbol"
        _, option_type, strike = parsed
        return self.missing_reason(expiry, strike, option_type)
