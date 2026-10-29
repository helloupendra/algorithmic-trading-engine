"""
The regular hours of NSE and BSE, cash and derivatives, on any IST day: the one
place the engine keeps them. The API's MarketSessionService answers from the
same rule (src/AlgoTrading.Infrastructure/Services/EquityMarketHours.cs).

Until 6 Dec 2026 both segments close at 15:30. SEBI's circular
HO/47/11/11(3)2025-MRD-POD2/I/23843/2026 of 15 Oct 2026 keeps the cash market's
continuous session to 15:30, followed by a closing auction from 15:30 to 15:40,
and moves the derivatives close to 15:45 from 7 Dec 2026 (paras 4.2.1,
4.2.4-4.2.5 and 6.1). The cash indices are computed from cash prices, so NIFTY
50 or SENSEX stop moving at about 15:30 while their options and futures trade on.

One exception, para 4.2.4: on an expiry day, the expiring index derivatives
stop at 15:30. Stock derivatives trade to 15:45 on every day, expiry days
included (para 4.2.5).

A day before LATER_DERIVATIVES_CLOSE_FROM is answered as it always was, so
backtests, replays and any reading of an earlier day keep 15:30. A later
circular is one change here.

Standard library only: Sentinel imports this one module from outside its
package (sentinel/clock.py), and must not pull the engine in with it.
"""
from __future__ import annotations

import re
from datetime import date, time

#: The open of both segments.
SESSION_OPEN = time(9, 15)

#: The cash market's continuous session ends here, on every day.
CASH_CLOSE = time(15, 30)

#: The first IST day the derivatives close at LATER_DERIVATIVES_CLOSE.
LATER_DERIVATIVES_CLOSE_FROM = date(2026, 12, 7)

#: The derivatives close from LATER_DERIVATIVES_CLOSE_FROM.
LATER_DERIVATIVES_CLOSE = time(15, 45)


def derivatives_close(day: date) -> time:
    """When NSE and BSE options and futures stop trading on the IST day `day`."""
    return LATER_DERIVATIVES_CLOSE if day >= LATER_DERIVATIVES_CLOSE_FROM else CASH_CLOSE


def derivatives_close_later(day: date) -> bool:
    """True on a day the derivatives trade past the cash close; never before 7 Dec 2026."""
    return derivatives_close(day) > CASH_CLOSE


def contract_close(day: date, index_contract_expiring_that_day: bool) -> time:
    """
    When one contract stops trading on `day`: an index option or future expiring
    that day at the cash close (para 4.2.4), any other at the derivatives close.
    """
    return CASH_CLOSE if index_contract_expiring_that_day else derivatives_close(day)


_DERIVATIVE = re.compile(r"\d(CE|PE)$|FUT$", re.IGNORECASE)


def is_derivative_symbol(symbol: str | None) -> bool:
    """An option or future in the FYERS grammar ("NSE:NIFTY2612224500CE", "NSE:NIFTY26DECFUT"); not "-EQ" or "-INDEX"."""
    return bool(_DERIVATIVE.search((symbol or "").strip()))
