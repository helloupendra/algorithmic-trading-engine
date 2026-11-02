"""
A live run's OPENs wait for their market (strategies/session_gate.py).

The 2 Nov review's finding: a strategy that enters on its first tick, started
before the open, was filled at the overnight quote. The API refuses that now too;
this is the runner's side, which keeps the OPEN back and lets the strategy ask again.
"""

import unittest
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Tuple

import _bootstrap  # noqa: F401

from strategies.session_gate import SessionGate, contract_market

IST = timezone(timedelta(hours=5, minutes=30))
CALL = "NSE:NIFTY2692925000CE"
CRUDE_CALL = "MCX:CRUDEOIL26NOV5500CE"
SHARE = "NSE:RELIANCE-EQ"


def ist(year: int, month: int, day: int, hour: int, minute: int = 0, second: int = 0) -> datetime:
    return datetime(year, month, day, hour, minute, second, tzinfo=IST)


def session(is_open: bool, next_open: Optional[datetime] = None, close: Optional[datetime] = None) -> Dict[str, Any]:
    answer: Dict[str, Any] = {"isMarketOpen": is_open}
    if next_open is not None:
        answer["nextMarketOpenUtc"] = next_open.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    if close is not None:
        answer["sessionCloseUtc"] = close.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    return answer


class Desk:
    """Answers the session question from a table, counts how often it was asked, and keeps its own clock."""

    def __init__(self, now: datetime, **answers: Any) -> None:
        self.now = now
        self.answers = answers
        self.asked: List[Tuple[str, str]] = []

    def ask(self, exchange: str, segment: str) -> Optional[Dict[str, Any]]:
        self.asked.append((exchange, segment))
        return self.answers.get(f"{exchange}_{segment}")

    def gate(self, **kwargs: Any) -> SessionGate:
        return SessionGate(self.ask, now=lambda: self.now, **kwargs)


class ContractMarketTests(unittest.TestCase):
    def test_each_contract_is_judged_by_its_own_segment(self):
        cases = {
            CALL: ("NSE", "FO"),
            "NSE:NIFTY26NOVFUT": ("NSE", "FO"),
            "BSE:SENSEX26NOV82000PE": ("BSE", "FO"),
            SHARE: ("NSE", "CM"),
            "NSE:NIFTY50-INDEX": ("NSE", "CM"),
            CRUDE_CALL: ("MCX", "COM"),
            "MCX:CRUDEOIL26NOVFUT": ("MCX", "COM"),
        }
        for symbol, market in cases.items():
            self.assertEqual(market, contract_market(symbol), symbol)

    def test_a_logical_leg_is_an_option_on_the_exchange_of_the_spot(self):
        self.assertEqual(("NSE", "FO"), contract_market("NIFTY_CE_24900", "NSE:NIFTY50-INDEX"))
        self.assertEqual(("BSE", "FO"), contract_market("SENSEX_PE_82000", "BSE:SENSEX-INDEX"))
        self.assertEqual(("MCX", "COM"), contract_market("CRUDEOIL_CE_5500", "MCX:CRUDEOIL26NOVFUT"))


class WhyClosedTests(unittest.TestCase):
    def test_before_the_open_an_open_waits_and_says_when(self):
        # Pre-open, 09:05: the case of the review, a straddle filled on an overnight quote.
        desk = Desk(ist(2026, 11, 3, 9, 5), NSE_FO=session(False, next_open=ist(2026, 11, 3, 9, 15)))

        self.assertEqual("NSE FO is closed (opens 09:15 IST)", desk.gate().why_closed([CALL]))

    def test_mcx_opens_at_its_own_time(self):
        desk = Desk(ist(2026, 11, 3, 8, 50), MCX_COM=session(False, next_open=ist(2026, 11, 3, 9, 0)))

        self.assertEqual("MCX COM is closed (opens 09:00 IST)", desk.gate().why_closed([CRUDE_CALL]))

    def test_after_the_close_the_next_open_is_named_with_its_day(self):
        desk = Desk(ist(2026, 11, 6, 15, 31), NSE_FO=session(False, next_open=ist(2026, 11, 9, 9, 15)))

        self.assertEqual("NSE FO is closed (opens Mon 9 Nov 09:15 IST)", desk.gate().why_closed([CALL]))

    def test_inside_the_session_nothing_waits(self):
        desk = Desk(ist(2026, 11, 3, 9, 15), NSE_FO=session(True, close=ist(2026, 11, 3, 15, 30)))

        self.assertIsNone(desk.gate().why_closed([CALL]))

    def test_any_closed_leg_holds_the_whole_group(self):
        desk = Desk(ist(2026, 12, 8, 15, 40),
                    NSE_FO=session(True, close=ist(2026, 12, 8, 15, 45)),
                    NSE_CM=session(False, next_open=ist(2026, 12, 9, 9, 15)))

        self.assertEqual("NSE CM is closed (opens Wed 9 Dec 09:15 IST)", desk.gate().why_closed([CALL, SHARE]))

    def test_an_api_that_cannot_be_asked_never_stops_an_open(self):
        def down(_exchange: str, _segment: str) -> None:
            raise RuntimeError("the API is away")

        self.assertIsNone(SessionGate(down).why_closed([CALL]))
        self.assertIsNone(Desk(ist(2026, 11, 3, 9, 5)).gate().why_closed([CALL]), "None is could not ask, not closed")


class ExpiringIndexContractTests(unittest.TestCase):
    """From 7 Dec 2026 the FO segment closes at 15:45, and an expiring index contract at 15:30 inside it."""

    def gate(self, now: datetime, expiring: bool) -> SessionGate:
        desk = Desk(now, NSE_FO=session(True, close=ist(2026, 12, 8, 15, 45)))
        return desk.gate(index_expiring_today=lambda _day: expiring)

    def test_an_index_contract_stops_at_the_cash_close_on_its_expiry_day(self):
        self.assertIsNone(self.gate(ist(2026, 12, 8, 15, 29), expiring=True).why_closed([CALL]))
        self.assertEqual("an index contract stops at 15:30 IST on its expiry day",
                         self.gate(ist(2026, 12, 8, 15, 30), expiring=True).why_closed([CALL]))

    def test_any_other_day_it_trades_to_the_derivatives_close(self):
        self.assertIsNone(self.gate(ist(2026, 12, 8, 15, 40), expiring=False).why_closed([CALL]))

    def test_a_special_session_keeps_one_set_of_hours(self):
        answer = dict(session(True), specialSessionName="Muhurat")
        desk = Desk(ist(2026, 12, 8, 18, 30), NSE_FO=answer)
        gate = desk.gate(index_expiring_today=lambda _day: True)

        self.assertIsNone(gate.why_closed([CALL]))


class TheAnswerIsKeptTests(unittest.TestCase):
    def test_a_tick_does_not_ask_every_time(self):
        desk = Desk(ist(2026, 11, 3, 9, 5), NSE_FO=session(False, next_open=ist(2026, 11, 3, 9, 15)))
        gate = desk.gate()

        for _ in range(50):
            gate.why_closed([CALL])
            desk.now += timedelta(seconds=1)

        self.assertEqual(1, len(desk.asked))

    def test_a_closed_answer_ends_at_the_open_so_the_first_tick_of_the_session_goes(self):
        desk = Desk(ist(2026, 11, 3, 9, 14, 30), NSE_FO=session(False, next_open=ist(2026, 11, 3, 9, 15)))
        gate = desk.gate()
        self.assertIsNotNone(gate.why_closed([CALL]))

        desk.now = ist(2026, 11, 3, 9, 15)
        desk.answers["NSE_FO"] = session(True, close=ist(2026, 11, 3, 15, 30))

        self.assertIsNone(gate.why_closed([CALL]))

    def test_an_open_answer_ends_at_the_close(self):
        desk = Desk(ist(2026, 11, 3, 15, 29, 30), NSE_FO=session(True, close=ist(2026, 11, 3, 15, 30)))
        gate = desk.gate()
        self.assertIsNone(gate.why_closed([CALL]))

        desk.now = ist(2026, 11, 3, 15, 30)
        desk.answers["NSE_FO"] = session(False, next_open=ist(2026, 11, 4, 9, 15))

        self.assertEqual("NSE FO is closed (opens Wed 4 Nov 09:15 IST)", gate.why_closed([CALL]))

    def test_could_not_ask_is_not_asked_again_on_every_tick(self):
        desk = Desk(ist(2026, 11, 3, 10, 0))
        gate = desk.gate()

        for _ in range(10):
            gate.why_closed([CALL])
            desk.now += timedelta(seconds=1)
        self.assertEqual(1, len(desk.asked))

        desk.now += timedelta(seconds=10)
        gate.why_closed([CALL])
        self.assertEqual(2, len(desk.asked))


if __name__ == "__main__":
    unittest.main()
