"""
The recap clock. A run on a replay of today must never see a price from later
in the day than the replay has reached — the platform holds the whole day
already, because it happened this morning.
"""

import unittest
from datetime import date

import _bootstrap  # noqa: F401

from core.recap_session import RecapSession

DAY = RecapSession(date(2026, 9, 11))


class RecapMarkingTests(unittest.TestCase):
    def test_an_ordinary_run_is_not_a_recap(self):
        self.assertIsNone(RecapSession.from_params({"lots": 2}))
        self.assertIsNone(RecapSession.from_params(None))

    def test_a_recap_run_carries_its_date(self):
        session = RecapSession.from_params({"session": "recap", "recap_date": "2026-09-11"})
        self.assertEqual(date(2026, 9, 11), session.day)

    def test_a_recap_without_a_date_is_refused_rather_than_dated_by_the_clock(self):
        with self.assertRaises(ValueError):
            RecapSession.from_params({"session": "recap"})


class RecapClockTests(unittest.TestCase):
    def test_the_session_is_09_15_to_15_30_in_mumbai(self):
        # 03:45 UTC is 09:15 IST; 10:00 UTC is 15:30 IST.
        self.assertTrue(DAY.in_session("2026-09-11T03:45:00Z"))
        self.assertFalse(DAY.in_session("2026-09-11T03:44:59Z"))
        self.assertFalse(DAY.in_session("2026-09-11T10:00:00Z"))

    def test_a_tick_stamped_with_the_wall_clock_is_not_in_the_session(self):
        # 18:15 IST on the replay's own day. If the vendor ever stamped replay
        # ticks with the time they were sent, this is where it would show — and
        # it must be refused, not fed to the strategy as if it were 09:15.
        self.assertFalse(DAY.in_session("2026-09-11T12:45:00Z"))

    def test_the_close_is_reached_on_the_replayed_day(self):
        self.assertTrue(DAY.past_close("2026-09-11T10:00:00Z"))
        self.assertFalse(DAY.past_close("2026-09-11T09:59:59Z"))

    def test_a_stamp_on_another_day_does_not_stop_the_run(self):
        # A clock mistake must not square a run off.
        self.assertFalse(DAY.past_close("2026-09-12T11:00:00Z"))
        self.assertFalse(DAY.past_close("2026-09-10T11:00:00Z"))


class NoLookaheadTests(unittest.TestCase):
    def test_bars_after_the_replay_clock_are_dropped(self):
        rows = [  # newest first, as the API returns them
            {"barStartUtc": "2026-09-11T09:55:00Z"},   # 15:25 real, this morning
            {"barStartUtc": "2026-09-11T07:00:00Z"},   # 12:30 real
            {"barStartUtc": "2026-09-11T04:25:00Z"},   # 09:55 — before the replay clock
            {"barStartUtc": "2026-09-10T09:55:00Z"},   # yesterday
        ]
        kept = DAY.drop_future_bars(rows, "2026-09-11T04:30:00Z")  # replay is at 10:00 IST
        self.assertEqual(["2026-09-11T04:25:00Z", "2026-09-10T09:55:00Z"],
                         [r["barStartUtc"] for r in kept])

    def test_no_clock_means_no_bars_rather_than_every_bar(self):
        self.assertEqual([], DAY.drop_future_bars([{"barStartUtc": "2026-09-11T04:25:00Z"}], None))

    def test_warm_up_stops_the_day_before(self):
        self.assertEqual("2026-09-10", DAY.warmup_end_date())
        self.assertTrue(DAY.before_session("2026-09-10T09:55:00Z"))
        self.assertFalse(DAY.before_session("2026-09-11T03:45:00Z"))


if __name__ == "__main__":
    unittest.main()
