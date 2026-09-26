import _bootstrap  # noqa: F401

import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest import mock

from sentinel.clock import IST, remember_day, session_from_answers
from sentinel.context import AgentState
from _sentinel_fakes import make_context


def ist(hour, minute, day=20, month=10):
    return datetime(2026, month, day, hour, minute, tzinfo=IST).astimezone(timezone.utc)


def utc(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


# 20 Oct 2026, Dussehra: NSE shut all day, MCX trading its evening session only.
DUSSEHRA = {
    "NSE": {"isTradingDay": False, "isMarketOpen": False, "isHoliday": True, "holidayName": "Dussehra",
            "sessionOpenUtc": utc(ist(9, 15)), "sessionCloseUtc": utc(ist(15, 30))},
    "MCX": {"isTradingDay": True, "isMarketOpen": False, "isHoliday": False, "holidayName": None,
            "sessionOpenUtc": utc(ist(17, 0)), "sessionCloseUtc": utc(ist(23, 55))},
}


class RememberedCalendarTests(unittest.TestCase):
    def test_a_holiday_is_remembered_for_the_rest_of_the_day(self):
        kept = remember_day(ist(11, 0), DUSSEHRA, None)
        midday = session_from_answers(ist(12, 0), {}, kept)
        self.assertFalse(midday.trading_day)
        self.assertFalse(midday.nse_open)
        self.assertFalse(midday.mcx_open)          # MCX's own hours, not the weekday rule's 09:00
        self.assertEqual("Dussehra", midday.holiday_name)
        self.assertTrue(midday.from_calendar and midday.remembered)
        self.assertTrue(session_from_answers(ist(18, 0), {}, kept).mcx_open)
        self.assertFalse(session_from_answers(ist(23, 56), {}, kept).mcx_open)

    def test_a_market_that_answers_is_taken_as_it_answers(self):
        kept = remember_day(ist(11, 0), DUSSEHRA, None)
        now = session_from_answers(ist(12, 0), {"NSE": DUSSEHRA["NSE"]}, kept)
        self.assertFalse(now.nse_open)
        self.assertTrue(now.remembered)            # MCX came from memory
        answered = session_from_answers(ist(12, 0), DUSSEHRA, kept)
        self.assertFalse(answered.remembered)

    def test_a_market_missing_from_an_answer_keeps_what_was_remembered(self):
        kept = remember_day(ist(11, 0), DUSSEHRA, None)
        kept = remember_day(ist(11, 1), {"NSE": DUSSEHRA["NSE"]}, kept)
        self.assertEqual({"NSE", "MCX"}, set(kept["markets"]))

    def test_yesterday_is_not_remembered_today(self):
        kept = remember_day(ist(11, 0), DUSSEHRA, None)
        next_day = session_from_answers(ist(11, 0, day=21), {}, kept)
        self.assertTrue(next_day.trading_day)      # the weekday rule: a Wednesday
        self.assertFalse(next_day.from_calendar)
        self.assertEqual({"NSE"}, set(remember_day(ist(9, 0, day=21), {"NSE": DUSSEHRA["NSE"]}, kept)["markets"]))

    def test_a_trading_day_without_hours_uses_the_weekday_hours(self):
        kept = remember_day(ist(9, 0, day=19), {"NSE": {"isTradingDay": True, "isMarketOpen": False}}, None)
        self.assertTrue(session_from_answers(ist(10, 0, day=19), {}, kept).nse_open)
        self.assertFalse(session_from_answers(ist(16, 0, day=19), {}, kept).nse_open)

    def test_a_damaged_memory_is_the_weekday_rule(self):
        for kept in ("nonsense", {"day": "2026-10-20", "markets": "x"},
                     {"day": "2026-10-20", "markets": {"NSE": {"tradingDay": "no"}}}):
            session = session_from_answers(ist(12, 0), {}, kept)
            self.assertTrue(session.nse_open, kept)
            self.assertFalse(session.from_calendar, kept)


class ContextCalendarTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.answers = {"/api/MarketSession/check?exchange=NSE*": DUSSEHRA["NSE"],
                        "/api/MarketSession/check?exchange=MCX*": DUSSEHRA["MCX"]}

    def test_the_day_is_written_once_and_read_back_after_a_restart(self):
        ctx = make_context(self.tmp, api=self.answers, now=ist(11, 0))
        with mock.patch.object(AgentState, "save", autospec=True, side_effect=AgentState.save) as save:
            for _ in range(3):
                ctx.fresh_cycle()
                self.assertFalse(ctx.session().trading_day)
        self.assertEqual(1, save.call_count)     # the same answer all day is not rewritten every round
        self.assertEqual("2026-10-20", json.loads((self.tmp / "logs" / "sentinel" / "calendar.json")
                                                  .read_text())["day"])

        restarted = make_context(self.tmp, api={}, now=ist(12, 0))   # a new process, and no API
        session = restarted.session()
        self.assertFalse(session.trading_day)
        self.assertTrue(session.remembered)


if __name__ == "__main__":
    unittest.main()
