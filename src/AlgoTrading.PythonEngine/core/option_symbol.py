"""
Reading an option's underlying, strike, type and expiry out of its symbol.

The live ingestor used to do this inline with one regex anchored to `NSE:`,
which meant BSE contracts — every SENSEX and BANKEX option — never matched and
silently got no greeks at all. It then priced whatever did match with a spot
looked up from a two-entry dict (`NIFTY` and `BANKNIFTY`, with hardcoded
fallbacks of 24000 and 51000) and a time to expiry of exactly seven days,
commented "for demo".

That last one is not a rounding error. The same BANKNIFTY 57600 CE at 538.30
implies 20% volatility over seven days, 38% over two, and 110% on expiry
morning. IV is the number that decides whether an option is worth buying or
selling, and it was being computed against a date the contract does not have.

A monthly symbol carries only its month. Until 27 Sep 2026 it was dated to the
month's last THURSDAY, NSE's rule until SEBI gave each exchange one expiry day:
from 1 Sep 2025 NSE's equity derivatives expire on TUESDAY and BSE's on
Thursday. So every NSE monthly (all of BANKNIFTY's contracts, and NIFTY's in the
last week of a month) was priced two days short: the feed's greeks, and the
chain poller's greeks AND the expiry date it stores on every snapshot. A
reviewer found it when PositionGreeks (C#) compared the feed's date with the
instrument master's. `monthly_expiry` is the rule now.
"""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import date, datetime, timedelta, timezone
from functools import lru_cache
from typing import AbstractSet, Any, Dict, Mapping, NamedTuple, Optional, Set, Tuple

#: Weekly: NSE:BANKNIFTY26O0757600CE — yy, month code, dd.
#: Monthly: NSE:BANKNIFTY26SEP57600CE — yy, MON.
#: Exchange-agnostic on purpose: BSE contracts are options too.
_MONTHLY = re.compile(
    r"^(?P<exchange>[A-Z]+):(?P<underlying>[A-Z]+)(?P<yy>\d{2})(?P<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)(?P<strike>\d+(?:\.\d+)?)(?P<kind>CE|PE)$"
)

_WEEKLY = re.compile(
    r"^(?P<exchange>[A-Z]+):(?P<underlying>[A-Z]+)(?P<yy>\d{2})(?P<m>[1-9OND])(?P<dd>\d{2})(?P<strike>\d+(?:\.\d+)?)(?P<kind>CE|PE)$"
)

_MONTHS = {
    "JAN": 1, "FEB": 2, "MAR": 3, "APR": 4, "MAY": 5, "JUN": 6,
    "JUL": 7, "AUG": 8, "SEP": 9, "OCT": 10, "NOV": 11, "DEC": 12,
}

#: The weekly format compresses October, November and December to a letter,
#: because a single digit only reaches 9.
_MONTH_CODES = {**{str(i): i for i in range(1, 10)}, "O": 10, "N": 11, "D": 12}

#: Trading closes at 15:30 IST, which is 10:00 UTC. An option expiring today is
#: worth its remaining hours, not a whole day.
_EXPIRY_UTC_HOUR = 10

#: SEBI's one-expiry-day-per-exchange rule, for contracts expiring from 1 Sep
#: 2025: NSE's equity derivatives expire on Tuesday, BSE's on Thursday. NSE's
#: monthlies expired on the last Thursday before it.
NSE_TUESDAY_FROM = date(2025, 9, 1)
_TUESDAY, _THURSDAY = 1, 3

#: The API's reference data. The API finds this engine at
#: <its content root>/../AlgoTrading.PythonEngine (PythonEngineLocator); this is
#: the same relation read the other way, so any checkout has both.
SEED_DIR = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "AlgoTrading.Api", "SeedData"))


class OptionSymbol(NamedTuple):
    exchange: str
    underlying: str
    strike: float
    kind: str          # "CE" or "PE"
    expiry: date


class ExpiryReference(NamedTuple):
    """
    What dating a monthly contract reads besides the weekday rule.

    recorded_monthly  (underlying, year, month) -> the last index option expiry
                      the exchange's bhavcopies recorded that month, which is
                      that month's monthly contract.
    recorded_through  exchange -> the last trading day those bhavcopies were
                      read to. A month is taken from them only when they cover
                      all of it: a half-read month's last entry is a weekly.
    holidays          exchange -> its full-day closures.
    """
    recorded_monthly: Mapping[Tuple[str, int, int], date]
    recorded_through: Mapping[str, date]
    holidays: Mapping[str, AbstractSet[date]]


#: The weekday rule alone: no records, no holidays.
NO_REFERENCE = ExpiryReference({}, {}, {})


def parse_option_symbol(symbol: str, reference: Optional[ExpiryReference] = None) -> Optional[OptionSymbol]:
    """
    The parts of an option symbol, or None if it is not one.

    A weekly symbol carries its expiry day and is read as written. A monthly one
    carries only its month and is dated by `monthly_expiry`, against the API's
    seed data unless a `reference` is given.
    """
    if not symbol:
        return None

    text = symbol.strip().upper()

    match = _MONTHLY.match(text)
    if match:
        year = 2000 + int(match.group("yy"))
        month = _MONTHS[match.group("mon")]
        exchange, underlying = match.group("exchange"), match.group("underlying")
        expiry = (monthly_expiry(exchange, underlying, year, month, reference) if reference is not None
                  else _seeded_monthly_expiry(exchange, underlying, year, month))
        return _build(match, expiry)

    match = _WEEKLY.match(text)
    if match:
        code = _MONTH_CODES.get(match.group("m"))
        if code is None:
            return None
        try:
            expiry = date(2000 + int(match.group("yy")), code, int(match.group("dd")))
        except ValueError:
            return None
        return _build(match, expiry)

    return None


def _build(match: "re.Match[str]", expiry: date) -> OptionSymbol:
    return OptionSymbol(
        exchange=match.group("exchange"),
        underlying=match.group("underlying"),
        strike=float(match.group("strike")),
        kind=match.group("kind"),
        expiry=expiry,
    )


def monthly_expiry(exchange: str, underlying: str, year: int, month: int,
                   reference: Optional[ExpiryReference] = None) -> date:
    """
    The day a monthly contract of `underlying` on `exchange` expires.

    1. A month the exchange's own records cover in full: the last expiry they
       list for the underlying that month. That is what happened, including what
       no weekday rule knows: BANKNIFTY's Wednesday monthlies of 2024,
       FINNIFTY's Tuesdays, SENSEX's Fridays and then Tuesdays before Sep 2025,
       and every holiday shift. Only past months are covered.
    2. Otherwise (every live contract): the month's last Tuesday on NSE from Sep
       2025, its last Thursday on NSE before that and on BSE, moved back to the
       previous trading day while that day is a weekend or a full-day holiday.
       A year the holiday calendar does not list yet gets no shift.

    Other exchanges get the Thursday rule, which is not their calendar (an MCX
    option expires days before its future). Nothing prices them from this: the
    enricher has no commodity spot, and the chain poller reads index chains.
    """
    ref = reference if reference is not None else _seed_reference()
    exchange, underlying = exchange.strip().upper(), underlying.strip().upper()

    through = ref.recorded_through.get(exchange)
    if through is not None and through >= _month_end(year, month):
        recorded = ref.recorded_monthly.get((underlying, year, month))
        if recorded is not None:
            return recorded

    tuesdays = exchange == "NSE" and date(year, month, 1) >= NSE_TUESDAY_FROM
    day = _last_weekday(year, month, _TUESDAY if tuesdays else _THURSDAY)
    closed = ref.holidays.get(exchange, frozenset())
    while day.weekday() >= 5 or day in closed:
        day -= timedelta(days=1)
    return day


def load_reference(seed_dir: str = SEED_DIR) -> ExpiryReference:
    """
    The recorded expiries (index_option_expiries.json, which
    tools/option_expiry_calendar.py builds from the NSE and BSE bhavcopies and
    the backtests price expired contracts by) and the holiday calendar
    (market_calendar.json, the file the API seeds its own from).

    A file that is missing or unreadable leaves its part empty and says so once
    on stderr: monthlies are then dated by the weekday rule alone, which is
    right in every month whose last week has no holiday.
    """
    recorded: Dict[Tuple[str, int, int], date] = {}
    through: Dict[str, date] = {}
    holidays: Dict[str, Set[date]] = {}

    expiries = _read_seed(os.path.join(seed_dir, "index_option_expiries.json"))
    for exchange, coverage in (expiries.get("coverage") or {}).items():
        last = _iso_date(coverage.get("last")) if isinstance(coverage, dict) else None
        if last is not None:
            through[str(exchange).upper()] = last
    for underlying, days in (expiries.get("underlyings") or {}).items():
        for text in days or []:
            day = _iso_date(text)
            if day is None:
                continue
            key = (str(underlying).upper(), day.year, day.month)
            if day > recorded.get(key, date.min):
                recorded[key] = day

    calendar = _read_seed(os.path.join(seed_dir, "market_calendar.json"))
    for entry in calendar.get("holidays") or []:
        day = _iso_date(entry.get("date"))
        # A missing closure is a full day, as the API's seeder reads it.
        full_day = str(entry.get("closure") or "FullDay").lower() == "fullday"
        if day is not None and full_day and entry.get("exchange"):
            holidays.setdefault(str(entry["exchange"]).upper(), set()).add(day)

    return ExpiryReference(recorded, through, {k: frozenset(v) for k, v in holidays.items()})


@lru_cache(maxsize=1)
def _seed_reference() -> ExpiryReference:
    return load_reference()


@lru_cache(maxsize=4096)
def _seeded_monthly_expiry(exchange: str, underlying: str, year: int, month: int) -> date:
    # Every option tick of a monthly contract asks; the answer never changes
    # within a process.
    return monthly_expiry(exchange, underlying, year, month, _seed_reference())


def _read_seed(path: str) -> Dict[str, Any]:
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError) as ex:
        print(f"option_symbol: cannot read {path} ({type(ex).__name__}: {ex}); "
              f"monthly expiries fall back to the weekday rule without it", file=sys.stderr, flush=True)
        return {}


def _iso_date(text: Any) -> Optional[date]:
    try:
        return date.fromisoformat(str(text)[:10])
    except (TypeError, ValueError):
        return None


def _month_end(year: int, month: int) -> date:
    following = date(year + 1, 1, 1) if month == 12 else date(year, month + 1, 1)
    return following - timedelta(days=1)


def _last_weekday(year: int, month: int, weekday: int) -> date:
    """The month's last Monday (0) ... Sunday (6)."""
    end = _month_end(year, month)
    return end - timedelta(days=(end.weekday() - weekday) % 7)


def years_to_expiry(expiry: date, now: Optional[datetime] = None) -> float:
    """
    Time to expiry in years, measured to the closing bell.

    Never returns zero or less: the pricing model divides by it, and an expired
    contract should produce no greeks rather than an infinity. Callers treat a
    non-positive answer as "do not price this".
    """
    moment = now or datetime.now(timezone.utc)
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)

    close = datetime(expiry.year, expiry.month, expiry.day, _EXPIRY_UTC_HOUR, 0, tzinfo=timezone.utc)
    seconds = (close - moment).total_seconds()
    return seconds / (365.0 * 24 * 3600)
