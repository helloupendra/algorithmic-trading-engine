"""
strategies/session_gate.py

Whether a live run may post an OPEN_GROUP now: not while the contract's market is
closed. The API refuses it too (PaperTradingService.CreateSignalAsync), and the API
is the authority; this keeps a tick-driven strategy from posting, and being
refused, on every tick before the open.

Why it is needed (2 Nov review): the morning job starts runners about 30 minutes
before the open, and a runner handles every tick it reads. Vendors send ticks
outside the session: BSE's replay of the previous close stamped at midnight, NSE's
pre-open index values from 09:00. A strategy that opens when it holds nothing would
sell a straddle at 09:05, fill at yesterday's closing bid, and at 09:15 mark any gap
as P&L on a trade that could never have happened. The window just after the close
is the same, until the close sweep stops the run.

The runner holds the OPEN (`Booking.held`), so the strategy is put back and asks
again on its next tick, as after a refusal, and enters on the first tick of the
session. Closes pass: getting flat never waits. A recap run does not ask: it trades
a past session, on the replay's clock (core/recap_session.py).

The hours, the holidays and MCX's evening are the API's (GET
/api/MarketSession/check), with a contract's own segment: from 7 Dec 2026 a share
stops at 15:30 and an option at 15:45. What that answer cannot know is the contract,
so the 15:30 stop of an index contract on its expiry day is applied here
(core/market_hours.py). When the API cannot be asked the OPEN is posted, and the API
judges it.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, Iterable, Optional, Tuple

from core.market_hours import contract_close, is_derivative_symbol

IST = timezone(timedelta(hours=5, minutes=30))

#: How long an answer is kept when nothing says when it changes.
ANSWER_SECONDS = 60.0

#: How long "could not ask" is kept: an API that is away is not asked on every tick.
UNKNOWN_SECONDS = 15.0


def contract_market(symbol: Optional[str], spot_symbol: Optional[str] = None) -> Tuple[str, str]:
    """
    The (exchange, segment) whose hours a leg trades in: "COM" on MCX, "FO" for an
    option or future, "CM" for a share. As EquityMarketHours.SegmentOf in the API. A
    strategy's logical leg ("BANKNIFTY_PE_50300") has no exchange yet and is an
    option on the exchange of the run's spot.
    """
    text = (symbol or "").strip().upper()
    logical = ":" not in text
    exchange = (spot_symbol or "NSE:").strip().upper().split(":", 1)[0] if logical else text.split(":", 1)[0]
    if exchange == "MCX":
        return "MCX", "COM"
    return exchange, "FO" if logical or is_derivative_symbol(text) else "CM"


def _when(value: Any) -> Optional[datetime]:
    """An API timestamp as an aware datetime; None when absent or unreadable."""
    if not value:
        return None
    try:
        parsed = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None
    return parsed if parsed.tzinfo is not None else parsed.replace(tzinfo=timezone.utc)


@dataclass
class _Answer:
    session: Optional[Dict[str, Any]]
    good_until: datetime


class SessionGate:
    """
    ``ask(exchange, segment)`` answers as GET /api/MarketSession/check does, or None
    when it could not. ``index_expiring_today(day)`` says whether the run trades an
    index contract that expires on that IST day.
    """

    def __init__(
        self,
        ask: Callable[[str, str], Optional[Dict[str, Any]]],
        *,
        spot_symbol: Optional[str] = None,
        index_expiring_today: Callable[[Any], bool] = lambda _day: False,
        now: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
    ) -> None:
        self._ask = ask
        self._spot = spot_symbol
        self._index_expiring_today = index_expiring_today
        self._now = now
        self._answers: Dict[Tuple[str, str], _Answer] = {}

    def why_closed(self, symbols: Iterable[Optional[str]]) -> Optional[str]:
        """Why an OPEN of these contracts must wait, in words for the log; None when it may go."""
        now = self._now()
        for symbol in symbols:
            why = self._why_contract_closed(symbol, now)
            if why is not None:
                return why
        return None

    def _why_contract_closed(self, symbol: Optional[str], now: datetime) -> Optional[str]:
        market = contract_market(symbol, self._spot)
        session = self._session(market, now)
        if session is None:
            return None

        exchange, segment = market
        ist = now.astimezone(IST)
        if not session.get("isMarketOpen"):
            next_open = _when(session.get("nextMarketOpenUtc"))
            if next_open is None:
                return f"{exchange} {segment} is closed"
            opens = next_open.astimezone(IST)
            at = f"{opens:%H:%M}" if opens.date() == ist.date() else f"{opens:%a} {opens.day} {opens:%b %H:%M}"
            return f"{exchange} {segment} is closed (opens {at} IST)"

        if (segment == "FO" and not session.get("specialSessionName")
                and self._index_expiring_today(ist.date())
                and ist.time() >= contract_close(ist.date(), True)):
            close = contract_close(ist.date(), True)
            return f"an index contract stops at {close:%H:%M} IST on its expiry day"
        return None

    def _session(self, market: Tuple[str, str], now: datetime) -> Optional[Dict[str, Any]]:
        kept = self._answers.get(market)
        if kept is not None and now < kept.good_until:
            return kept.session

        try:
            session = self._ask(*market)
        except Exception:  # noqa: BLE001 — not being able to ask never stops an order; the API judges it
            session = None

        good_until = now + timedelta(seconds=UNKNOWN_SECONDS if session is None else ANSWER_SECONDS)
        if session is not None:
            # Never kept past the moment it changes, so an OPEN goes on the first tick of the session.
            edge = _when(session.get("sessionCloseUtc" if session.get("isMarketOpen") else "nextMarketOpenUtc"))
            if edge is not None and now < edge < good_until:
                good_until = edge
        self._answers[market] = _Answer(session, good_until)
        return session
