"""
NSE and BSE close at 15:30 until 6 Dec 2026; from 7 Dec the derivatives close at 15:45 (SEBI circular of
15 Oct 2026). One rule in core.market_hours, and the places that read it, on both sides of the date: a day
before it, and today's (30 Oct 2026), must read exactly as they always did.
"""

import os
import sys
import tempfile
import unittest
from datetime import date, datetime, time, timedelta, timezone
from pathlib import Path

import _bootstrap  # noqa: F401

from core import market_hours as mh
from core.recap_session import RecapSession

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from market_data.replay import desk_replay as dr  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))
TODAY = date(2026, 10, 30)      # the last session this was built before: nothing may move on it
FRIDAY_BEFORE = date(2026, 12, 4)
MONDAY_FROM = date(2026, 12, 7)


def ist(day: date, hh: int, mm: int) -> datetime:
    return datetime.combine(day, time(hh, mm), IST)


class TheRuleTests(unittest.TestCase):
    def test_the_derivatives_close_moves_to_15_45_on_7_december_2026_and_not_a_day_before(self):
        self.assertEqual(time(15, 30), mh.derivatives_close(TODAY))
        self.assertEqual(time(15, 30), mh.derivatives_close(FRIDAY_BEFORE))
        self.assertEqual(time(15, 30), mh.derivatives_close(date(2026, 12, 6)))
        self.assertEqual(time(15, 45), mh.derivatives_close(MONDAY_FROM))
        self.assertEqual(time(15, 45), mh.derivatives_close(date(2027, 3, 1)))

    def test_the_cash_close_stays_15_30(self):
        self.assertEqual(time(15, 30), mh.CASH_CLOSE)
        self.assertFalse(mh.derivatives_close_later(FRIDAY_BEFORE))
        self.assertTrue(mh.derivatives_close_later(MONDAY_FROM))

    def test_an_expiring_index_contract_stops_at_15_30_on_its_expiry_day(self):
        # Para 4.2.4: expiring index derivatives cease at 3:30 p.m. on expiry days.
        self.assertEqual(time(15, 30), mh.contract_close(date(2026, 12, 8), index_contract_expiring_that_day=True))
        self.assertEqual(time(15, 45), mh.contract_close(date(2026, 12, 8), index_contract_expiring_that_day=False))
        self.assertEqual(time(15, 30), mh.contract_close(FRIDAY_BEFORE, index_contract_expiring_that_day=False))


class RecapCloseTests(unittest.TestCase):
    def test_a_replayed_day_before_7_december_ends_at_15_30_as_it_always_did(self):
        for day in (TODAY, FRIDAY_BEFORE):
            s = RecapSession(day)
            self.assertEqual(time(15, 30), s.close_ist)
            self.assertEqual(ist(day, 15, 30).astimezone(timezone.utc), s.close_utc)

    def test_a_replayed_day_from_7_december_ends_at_its_derivatives_close(self):
        s = RecapSession(MONDAY_FROM)
        self.assertEqual(time(15, 45), s.close_ist)
        self.assertTrue(s.in_session(ist(MONDAY_FROM, 15, 40)))
        s.note_in_session(ist(MONDAY_FROM, 15, 43))
        self.assertFalse(s.reached_close(ist(MONDAY_FROM, 15, 44)))
        self.assertTrue(s.reached_close(ist(MONDAY_FROM, 15, 45)))

    def test_an_expiry_day_recap_ends_with_the_expiring_contracts(self):
        expiry = RecapSession(date(2026, 12, 8))
        self.assertTrue(expiry.end_at(mh.contract_close(expiry.day, True)))
        self.assertEqual(time(15, 30), expiry.close_ist)
        # Never later than the day's own close, and before 7 Dec there is nothing to move.
        self.assertFalse(expiry.end_at(time(16, 0)))
        before = RecapSession(FRIDAY_BEFORE)
        self.assertFalse(before.end_at(mh.contract_close(before.day, True)))
        self.assertEqual(time(15, 30), before.close_ist)


class ReplayPlayerTests(unittest.TestCase):
    def plan(self, day):
        return dr.ReplayPlan(day=day, speed=1.0, start=time(9, 15), session=1, runs=[1])

    def test_a_day_before_7_december_plays_to_15_33_as_it_always_did(self):
        for day in (TODAY, FRIDAY_BEFORE):
            self.assertEqual(dr.PLAY_UNTIL, dr.play_until(day))
            self.assertEqual(ist(day, 15, 33).astimezone(timezone.utc), self.plan(day).play_until_utc)
            self.assertEqual(1.0, self.plan(day).progress(ist(day, 15, 30)))

    def test_a_day_from_7_december_plays_past_the_15_45_close(self):
        p = self.plan(MONDAY_FROM)
        self.assertEqual(time(15, 48), dr.play_until(MONDAY_FROM))
        self.assertLess(p.progress(ist(MONDAY_FROM, 15, 30)), 1.0)
        self.assertEqual(1.0, p.progress(ist(MONDAY_FROM, 15, 45)))


class CashSideTests(unittest.TestCase):
    """The places that read the index, which is computed from cash prices, keep 15:30 on every day."""

    def test_a_backtest_session_and_a_forecast_still_end_at_15_30(self):
        from analysis.score import session_closed
        from backtest.timeutil import in_session

        self.assertFalse(in_session(ist(MONDAY_FROM, 15, 35)))
        self.assertTrue(in_session(ist(MONDAY_FROM, 15, 29)))
        self.assertTrue(session_closed(MONDAY_FROM, ist(MONDAY_FROM, 15, 31)))
        self.assertFalse(session_closed(TODAY, ist(TODAY, 15, 29)))


class SentinelTests(unittest.TestCase):
    def test_the_desk_stops_nse_runs_at_the_derivatives_close(self):
        from sentinel.agents import trading
        from sentinel.clock import NSE_CLOSE, nse_runs_stop

        self.assertEqual(time(15, 30), NSE_CLOSE)
        self.assertEqual(time(15, 30), nse_runs_stop(TODAY))
        self.assertEqual(time(15, 45), nse_runs_stop(MONDAY_FROM))
        self.assertEqual(time(15, 25), trading.plan_nse_end(TODAY))
        self.assertEqual(time(15, 40), trading.plan_nse_end(MONDAY_FROM))
        self.assertEqual((time(9, 15), time(15, 30)), trading.nse_hours(FRIDAY_BEFORE))

    def test_a_hold_until_the_close_lasts_to_the_runs_stop(self):
        from sentinel.agents.logs import _until_close

        noon_before = ist(FRIDAY_BEFORE, 12, 0).timestamp()
        noon_from = ist(MONDAY_FROM, 12, 0).timestamp()
        self.assertEqual(3.5 * 3600, _until_close(noon_before))
        self.assertEqual(3.75 * 3600, _until_close(noon_from))

    def test_the_shared_hours_module_restarts_sentinel_like_its_own_files(self):
        from sentinel import reload

        with tempfile.TemporaryDirectory() as root:
            package = Path(root) / "sentinel"
            (package).mkdir()
            (Path(root) / "core").mkdir()
            own = package / "clock.py"
            own.write_text("")
            shared = Path(root) / "core" / "market_hours.py"
            shared.write_text("")
            os.utime(own, (1000, 1000))
            os.utime(shared, (2000, 2000))
            self.assertEqual(2000, reload.newest_source(package))


if __name__ == "__main__":
    unittest.main()
