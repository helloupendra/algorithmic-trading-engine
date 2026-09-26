"""
Time as the desk reads it: India Standard Time, trading days, session hours.

The authoritative calendar (holidays, MCX half days) lives in the API. When the
API answers, :func:`session_from_api` uses it; when it does not — which is
exactly when Sentinel is needed most — the plain weekday rule below keeps the
agents from treating a Saturday as a silent market.
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, time, timedelta, timezone
from typing import Any, Callable, Optional

IST = timezone(timedelta(hours=5, minutes=30))

NSE_OPEN = time(9, 15)
NSE_CLOSE = time(15, 30)
MCX_OPEN = time(9, 0)
MCX_CLOSE = time(23, 30)


def now_utc() -> datetime:
    return datetime.now(timezone.utc)


def to_ist(moment: datetime) -> datetime:
    if moment.tzinfo is None:
        moment = moment.replace(tzinfo=timezone.utc)
    return moment.astimezone(IST)


@dataclass(frozen=True)
class Session:
    """Whether each market is trading at one moment."""

    trading_day: bool
    nse_open: bool
    mcx_open: bool
    holiday_name: Optional[str] = None
    from_calendar: bool = False

    @property
    def any_open(self) -> bool:
        return self.nse_open or self.mcx_open


def session_by_weekday(moment_utc: datetime) -> Session:
    """The fallback: Monday to Friday, fixed hours, no holidays."""
    ist = to_ist(moment_utc)
    weekday = ist.weekday() < 5
    t = ist.time()
    return Session(
        trading_day=weekday,
        nse_open=weekday and NSE_OPEN <= t < NSE_CLOSE,
        mcx_open=weekday and MCX_OPEN <= t < MCX_CLOSE,
    )


def session_from_api(moment_utc: datetime, get_json: Callable[[str], Any]) -> Session:
    """
    The API's calendar — holidays, MCX's split sessions — falling back to the
    weekday rule on any failure.

    ``get_json(path)`` returns the decoded body of a GET, or raises. The API
    answers one market at a time (GET /api/MarketSession/check), so this asks
    twice: the NSE cash market and MCX commodities.
    """
    fallback = session_by_weekday(moment_utc)

    def ask(exchange: str, segment: str) -> Optional[dict]:
        try:
            body = get_json(f"/api/MarketSession/check?exchange={exchange}&segment={segment}")
        except Exception:
            return None
        return body if isinstance(body, dict) and isinstance(body.get("isMarketOpen"), bool) else None

    nse = ask("NSE", "CM")
    mcx = ask("MCX", "COM")
    if nse is None and mcx is None:
        return fallback

    holiday = None
    if nse is not None and nse.get("isHoliday") and isinstance(nse.get("holidayName"), str):
        holiday = nse["holidayName"]

    return Session(
        trading_day=bool(nse["isTradingDay"]) if nse is not None and isinstance(nse.get("isTradingDay"), bool)
        else fallback.trading_day,
        nse_open=nse["isMarketOpen"] if nse is not None else fallback.nse_open,
        mcx_open=mcx["isMarketOpen"] if mcx is not None else fallback.mcx_open,
        holiday_name=holiday,
        from_calendar=True,
    )


def ist_date(moment_utc: datetime) -> str:
    """yyyy-MM-dd in IST — the form every date query in the API takes."""
    return to_ist(moment_utc).strftime("%Y-%m-%d")
