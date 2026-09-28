"""
scripts/pager/pager.py — the phone call that wakes the owner.

The pager's own tests as they stood beside it on the server (v1.1), now in the
engine's suite.
"""
import os
import sys
import unittest
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401

# The pager lives in scripts/pager, which is not on the engine path.
PAGER_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts", "pager"))
if PAGER_DIR not in sys.path:
    sys.path.insert(0, PAGER_DIR)

import pager  # noqa: E402
from pager import Seen, State, problems, step  # noqa: E402
from sentinel.clock import IST  # noqa: E402



def ist(h, m=0, s=0, day=28):
    return datetime(2026, 9, day, h, m, s, tzinfo=IST).astimezone(timezone.utc)


def healthy(now, **kw):
    base = dict(now=now, api_up=True, trading_day=True, nse_open=True, dhan=True, fyers=False,
                newest_index_tick=now - timedelta(seconds=5), planned=23, plan_live=23, live=24)
    base.update(kw)
    return Seen(**base)


class Problems(unittest.TestCase):
    def test_a_healthy_session_has_none(self):
        self.assertEqual({}, problems(healthy(ist(10, 0)), 23))

    def test_a_weekend_special_session_is_quiet_unless_runs_were_started(self):
        sunday = healthy(ist(10, 0, day=27), dhan=False, fyers=False, plan_live=0, live=0, newest_index_tick=None)
        self.assertEqual({}, problems(sunday, 0))
        self.assertIn("no-ticks", problems(healthy(ist(10, 0, day=27), plan_live=2, newest_index_tick=None), 2))

    def test_runs_dropped_is_off_until_it_can_tell_failures_from_stops(self):
        self.assertNotIn("runs-dropped", problems(healthy(ist(11, 0), live=2), 23))

    def test_not_a_trading_day_is_always_quiet(self):
        self.assertEqual({}, problems(Seen(now=ist(10, 0), api_up=False, trading_day=False), 0))

    def test_api_down_only_in_its_window(self):
        self.assertIn("api-down", problems(Seen(now=ist(10, 0), api_up=False, trading_day=True), 0))
        self.assertEqual({}, problems(Seen(now=ist(8, 0), api_up=False, trading_day=True), 0))
        evening = Seen(now=ist(22, 0), api_up=False, trading_day=True, last_close=ist(23, 30))
        self.assertIn("api-down", problems(evening, 0))

    def test_no_broker_needs_both_out_and_known(self):
        self.assertIn("no-broker", problems(healthy(ist(9, 1), dhan=False, fyers=False), 23))
        self.assertEqual({}, problems(healthy(ist(9, 1), dhan=False, fyers=True), 23))
        self.assertEqual({}, problems(healthy(ist(9, 1), dhan=False, fyers=None), 23))
        self.assertNotIn("no-broker", problems(healthy(ist(8, 50), dhan=False, fyers=False), 23))

    def test_no_ticks_after_three_minutes_of_open_market(self):
        now = ist(10, 0)
        self.assertIn("no-ticks", problems(healthy(now, newest_index_tick=now - timedelta(seconds=200)), 23))
        self.assertIn("no-ticks", problems(healthy(now, newest_index_tick=None), 23))
        self.assertNotIn("no-ticks", problems(healthy(ist(9, 16), newest_index_tick=None), 23))
        self.assertNotIn("no-ticks", problems(healthy(now, nse_open=False, newest_index_tick=None), 23))

    def test_runs_not_up_and_dropped(self):
        self.assertIn("runs-not-up", problems(healthy(ist(9, 31), plan_live=0), 0))
        self.assertNotIn("runs-not-up", problems(healthy(ist(9, 31), plan_live=0, planned=0), 0))
        # The manual book is "Running" all day; it must not hide a morning that deployed nothing.
        self.assertIn("runs-not-up", problems(healthy(ist(9, 31), plan_live=0, live=1), 0))
        # 28 Sep: the morning job waits for the 09:15 open before it deploys a run.
        self.assertNotIn("runs-not-up", problems(healthy(ist(9, 20), plan_live=0), 0))
        pager.RUNS_DROPPED_ON = True
        self.addCleanup(setattr, pager, "RUNS_DROPPED_ON", False)
        self.assertIn("runs-dropped", problems(healthy(ist(11, 0), live=10), 23))
        self.assertNotIn("runs-dropped", problems(healthy(ist(11, 0), live=12), 23))
        self.assertNotIn("runs-dropped", problems(healthy(ist(15, 20), live=2), 23))   # the day's own close


class Escalation(unittest.TestCase):
    def run_minutes(self, minutes, found_for, start=ist(10, 0)):
        state, texts, calls = State(), [], []
        for i in range(minutes):
            now = start + timedelta(minutes=i)
            seen = healthy(now)
            step(state, seen, found_for(i), lambda m: texts.append(m) or True, lambda w: calls.append(w) or True)
        return state, texts, calls

    def test_one_slow_minute_pages_nobody(self):
        _, texts, calls = self.run_minutes(5, lambda i: {"no-ticks": "x"} if i == 1 else {})
        self.assertEqual(([], []), (texts, calls))

    def test_a_lasting_problem_calls_three_times_then_only_texts(self):
        state, texts, calls = self.run_minutes(90, lambda i: {"no-ticks": "old prices"})
        self.assertEqual(3, len(calls))
        self.assertTrue(texts[0].startswith("🚨 PAGE"))
        self.assertTrue(any(t.startswith("🚨 STILL") for t in texts))
        self.assertEqual(3, state.calls_today["no-ticks"])

    def test_calls_are_ten_minutes_apart(self):
        state, _, calls = self.run_minutes(15, lambda i: {"api-down": "x"})
        self.assertEqual(2, len(calls))   # at 3 min held and 10 min later

    def test_recovery_texts_once(self):
        _, texts, _ = self.run_minutes(10, lambda i: {"no-ticks": "x"} if i < 6 else {})
        self.assertEqual(1, sum(t.startswith("✅ RESOLVED") for t in texts))

    def test_a_problem_that_cleared_before_its_grace_says_nothing(self):
        _, texts, calls = self.run_minutes(6, lambda i: {"no-ticks": "x"} if i < 2 else {})
        self.assertEqual(([], []), (texts, calls))

    def test_state_survives_a_restart(self):
        import tempfile, pathlib
        with tempfile.TemporaryDirectory() as d:
            path = pathlib.Path(d) / "state.json"
            state, _, _ = self.run_minutes(5, lambda i: {"no-ticks": "x"})
            state.save(path)
            again = State.load(path)
            self.assertEqual(state.calls_today, again.calls_today)
            self.assertIn("no-ticks", again.open)


if __name__ == "__main__":
    unittest.main()
