"""
Time as the desk reads it: India Standard Time, trading days, session hours.

The authoritative calendar (holidays, MCX half days) lives in the API. When the
API answers, :func:`session_from_answers` uses it; when it does not — which is
exactly when Sentinel is needed most — what the calendar said earlier the same
day stands in (:func:`remember_day`), and failing that the plain weekday rule
below keeps the agents from treating a Saturday as a silent market.
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
    # The calendar's word from earlier today, because the API is not answering now.
    remembered: bool = False

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


# The markets the API's calendar is asked about: the NSE cash market (BSE and
# the F&O segments keep its hours) and MCX commodities.
CALENDAR_MARKETS = (("NSE", "CM"), ("MCX", "COM"))


def ask_calendar(get_json: Callable[[str], Any]) -> dict[str, dict]:
    """
    Each market's answer from the API's calendar, for the markets that
    answered. ``get_json(path)`` returns the decoded body of a GET, or raises.
    The API answers one market at a time (GET /api/MarketSession/check).
    """
    answers: dict[str, dict] = {}
    for exchange, segment in CALENDAR_MARKETS:
        try:
            body = get_json(f"/api/MarketSession/check?exchange={exchange}&segment={segment}")
        except Exception:
            continue
        if isinstance(body, dict) and isinstance(body.get("isMarketOpen"), bool):
            answers[exchange] = body
    return answers


def _parse_utc(value: Any) -> Optional[datetime]:
    if not isinstance(value, str) or not value:
        return None
    try:
        moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None
    return moment if moment.tzinfo else moment.replace(tzinfo=timezone.utc)


def remember_day(moment_utc: datetime, answers: dict[str, dict], kept: Any) -> dict:
    """
    What to keep of today's calendar: whether each market trades today, and
    between which times. A holiday does not stop being one because the API
    went down, and an API that is down for a minute or two is ordinary: the
    desk restarts it whenever a deploy changes it, and Sentinel restarts itself
    on every deploy too (sentinel/reload.py), so what is kept lives in a file,
    not in memory. Without it the weekday rule would call Gandhi Jayanti an
    open market two minutes into a deploy, and the health agent would report
    every feed silent.
    """
    day = ist_date(moment_utc)
    markets = dict(kept["markets"]) if isinstance(kept, dict) and kept.get("day") == day \
        and isinstance(kept.get("markets"), dict) else {}
    for exchange, body in answers.items():
        trading = body.get("isTradingDay")
        if not isinstance(trading, bool):
            continue
        markets[exchange] = {
            "tradingDay": trading,
            "openUtc": body.get("sessionOpenUtc") if isinstance(body.get("sessionOpenUtc"), str) else None,
            "closeUtc": body.get("sessionCloseUtc") if isinstance(body.get("sessionCloseUtc"), str) else None,
            "holiday": body.get("holidayName") if body.get("isHoliday") and isinstance(body.get("holidayName"), str)
            else None,
        }
    return {"day": day, "markets": markets}


def _recalled(exchange: str, facts: Any, moment_utc: datetime, fallback: Session) -> Optional[dict]:
    """A calendar answer rebuilt from what was kept earlier today, as the API would give it now."""
    if not isinstance(facts, dict) or not isinstance(facts.get("tradingDay"), bool):
        return None
    trading = facts["tradingDay"]
    opens, closes = _parse_utc(facts.get("openUtc")), _parse_utc(facts.get("closeUtc"))
    if not trading:
        is_open = False
    elif opens is not None and closes is not None:
        is_open = opens <= moment_utc < closes
    else:
        is_open = fallback.nse_open if exchange == "NSE" else fallback.mcx_open
    holiday = facts.get("holiday") if isinstance(facts.get("holiday"), str) else None
    return {"isTradingDay": trading, "isMarketOpen": is_open, "isHoliday": holiday is not None,
            "holidayName": holiday}


def session_from_answers(moment_utc: datetime, answers: dict[str, dict], kept: Any = None) -> Session:
    """
    The session from the calendar's answers; a market that did not answer is
    taken from what its calendar said earlier the same IST day, and failing
    that from the weekday rule.
    """
    fallback = session_by_weekday(moment_utc)
    markets = kept.get("markets") if isinstance(kept, dict) and kept.get("day") == ist_date(moment_utc) \
        and isinstance(kept.get("markets"), dict) else {}

    def answer(exchange: str) -> tuple[Optional[dict], bool]:
        if exchange in answers:
            return answers[exchange], False
        recalled = _recalled(exchange, markets.get(exchange), moment_utc, fallback)
        return recalled, recalled is not None

    nse, nse_recalled = answer("NSE")
    mcx, mcx_recalled = answer("MCX")
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
        remembered=nse_recalled or mcx_recalled,
    )


def session_from_api(moment_utc: datetime, get_json: Callable[[str], Any]) -> Session:
    """The API's calendar — holidays, MCX's split sessions — falling back to the weekday rule on any failure."""
    return session_from_answers(moment_utc, ask_calendar(get_json))


def ist_date(moment_utc: datetime) -> str:
    """yyyy-MM-dd in IST — the form every date query in the API takes."""
    return to_ist(moment_utc).strftime("%Y-%m-%d")
