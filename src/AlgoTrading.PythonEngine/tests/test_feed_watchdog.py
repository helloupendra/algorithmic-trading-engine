"""
When a silent tick feed is worth waking someone for (core/feed_watchdog.py).

The runner's #1 invariant is that live data must never interrupt a running
strategy silently. These pin both halves of that: a dry feed during market
hours has to be reported, and every other kind of silence has to stay quiet or
the alerts become noise nobody reads.
"""

import unittest

import _bootstrap  # noqa: F401

from core.feed_watchdog import assess_feed

STALL = 90.0
REPEAT = 600.0


def assess(**overrides):
    kwargs = dict(
        now=1000.0,
        last_tick_at=None,
        listening_since=0.0,
        currently_stalled=False,
        last_report_at=0.0,
        market_open=True,
        stall_after=STALL,
        repeat_after=REPEAT,
    )
    kwargs.update(overrides)
    return assess_feed(**kwargs)


class FeedWatchdogTests(unittest.TestCase):
    def test_a_flowing_feed_says_nothing(self):
        verdict = assess(now=1000.0, last_tick_at=995.0)
        self.assertEqual(verdict.action, "quiet")
        self.assertFalse(verdict.should_report)

    def test_a_dry_feed_during_market_hours_is_reported(self):
        verdict = assess(now=1000.0, last_tick_at=880.0)
        self.assertEqual(verdict.action, "stalled")
        self.assertTrue(verdict.is_stalled)
        self.assertEqual(verdict.silent_seconds, 120)

    def test_silence_outside_market_hours_is_not_a_stall(self):
        verdict = assess(now=1000.0, last_tick_at=500.0, market_open=False)
        self.assertEqual(verdict.action, "quiet")

    def test_an_unanswerable_market_question_does_not_cry_wolf(self):
        """None means the API could not be asked — not that the market is open."""
        verdict = assess(now=1000.0, last_tick_at=500.0, market_open=None)
        self.assertEqual(verdict.action, "quiet")

    def test_never_having_received_a_tick_counts_from_the_start(self):
        """Starting into a feed that was never alive is the common failure."""
        verdict = assess(now=1000.0, last_tick_at=None, listening_since=800.0)
        self.assertEqual(verdict.action, "stalled")
        self.assertEqual(verdict.silent_seconds, 200)

    def test_a_fresh_runner_is_given_its_grace_period(self):
        verdict = assess(now=1000.0, last_tick_at=None, listening_since=950.0)
        self.assertEqual(verdict.action, "quiet")

    def test_a_standing_stall_is_not_repeated_immediately(self):
        verdict = assess(
            now=1000.0, last_tick_at=800.0, currently_stalled=True, last_report_at=900.0
        )
        self.assertEqual(verdict.action, "quiet", "one alert per stall, not one per check")

    def test_a_standing_stall_is_repeated_after_the_interval(self):
        verdict = assess(
            now=1600.0, last_tick_at=800.0, currently_stalled=True, last_report_at=900.0
        )
        self.assertEqual(verdict.action, "still-stalled")
        self.assertTrue(verdict.is_stalled)

    def test_recovery_closes_the_warning(self):
        verdict = assess(now=1000.0, last_tick_at=999.0, currently_stalled=True)
        self.assertEqual(verdict.action, "recovered")
        self.assertFalse(verdict.is_stalled)
        self.assertTrue(verdict.should_report)

    def test_recovery_is_reported_even_when_the_market_has_closed(self):
        """
        The stall alert has already gone out. Leaving it open because the bell
        rang in between is how someone spends an evening chasing a feed that
        came back on its own.
        """
        verdict = assess(
            now=1000.0, last_tick_at=999.0, currently_stalled=True, market_open=False
        )
        self.assertEqual(verdict.action, "recovered")

    def test_the_boundary_is_not_a_stall_yet(self):
        verdict = assess(now=1000.0, last_tick_at=1000.0 - STALL + 0.5)
        self.assertEqual(verdict.action, "quiet")

    def test_a_clock_that_jumps_backwards_does_not_report_negative_silence(self):
        verdict = assess(now=1000.0, last_tick_at=1200.0)
        self.assertEqual(verdict.silent_seconds, 0)
        self.assertEqual(verdict.action, "quiet")


if __name__ == "__main__":
    unittest.main()
