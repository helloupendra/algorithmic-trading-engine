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
"""

from __future__ import annotations

import re
from datetime import date, datetime, timezone
from typing import NamedTuple, Optional

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


class OptionSymbol(NamedTuple):
    exchange: str
    underlying: str
    strike: float
    kind: str          # "CE" or "PE"
    expiry: date


def parse_option_symbol(symbol: str) -> Optional[OptionSymbol]:
    """The parts of an option symbol, or None if it is not one."""
    if not symbol:
        return None

    text = symbol.strip().upper()

    match = _MONTHLY.match(text)
    if match:
        year = 2000 + int(match.group("yy"))
        month = _MONTHS[match.group("mon")]
        return _build(match, _last_thursday(year, month))

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


def _last_thursday(year: int, month: int) -> date:
    """
    Monthly contracts expire on the last Thursday. Only an approximation when a
    holiday moves it — a day out at a month's distance changes IV by very little,
    where seven days at one day's distance changes it by a factor of five.
    """
    day = 31
    while day > 0:
        try:
            candidate = date(year, month, day)
        except ValueError:
            day -= 1
            continue
        if candidate.weekday() == 3:  # Thursday
            return candidate
        day -= 1
    return date(year, month, 28)


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
