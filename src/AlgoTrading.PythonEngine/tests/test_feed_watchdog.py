"""
When a silent tick feed is worth waking someone for (core/feed_watchdog.py).

The runner's #1 invariant is that live data must never interrupt a running
strategy silently. These pin both halves of that: a dry feed during market
hours has to be reported, and every other kind of silence has to stay quiet or
the alerts become noise nobody reads.
"""

import os
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from core.feed_watchdog import assess_feed

STALL = 90.0
REPEAT = 600.0
CONFIRM = 180.0


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
        confirm_after=CONFIRM,
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
        # Reported at 185 s of silence, so the 180 s confirmation is already made.
        verdict = assess(
            now=1000.0, last_tick_at=800.0, currently_stalled=True, last_report_at=985.0
        )
        self.assertEqual(verdict.action, "quiet", "one alert per stall, not one per check")

    def test_a_stall_reported_at_90s_is_reported_again_at_180s(self):
        """
        The API sends a stall to Telegram only at 180 s (after the feed's own
        120 s reconnect). Without this report it would hear nothing more until
        the ten-minute repeat, and a real outage would reach the desk late.
        """
        verdict = assess(
            now=985.0, last_tick_at=800.0, currently_stalled=True, last_report_at=895.0
        )
        self.assertEqual(verdict.action, "still-stalled")
        self.assertTrue(verdict.is_stalled)
        self.assertEqual(verdict.silent_seconds, 185)

    def test_the_180s_report_is_made_once(self):
        verdict = assess(
            now=1015.0, last_tick_at=800.0, currently_stalled=True, last_report_at=985.0
        )
        self.assertEqual(verdict.action, "quiet")

    def test_not_before_180s(self):
        verdict = assess(
            now=970.0, last_tick_at=800.0, currently_stalled=True, last_report_at=895.0
        )
        self.assertEqual(verdict.action, "quiet")

    def test_a_stall_first_seen_past_180s_is_not_reported_twice(self):
        """A runner that starts into a long silence reports it once, not stall-then-confirm."""
        verdict = assess(
            now=1015.0, last_tick_at=None, listening_since=700.0, currently_stalled=True, last_report_at=1000.0
        )
        self.assertEqual(verdict.action, "quiet")

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


# --------------------------------------------------------------------------
# The live feed's own watchdogs on its socket (core/live/feed_runner.py):
# no frame at all for FEED_FRAME_SILENCE_SECONDS, and bytes piling up unread.


class SocketFeed:
    """Mixed into the fake feed: a socket whose idle time and backlog the test sets."""

    idle = None
    backlog = None
    feed_stats = None

    def transport_idle_seconds(self):
        return self.idle

    def socket_backlog_bytes(self):
        return self.backlog

    def stats(self):
        return dict(self.feed_stats or {})


def _socket_runner(market_open=True, **switches):
    from _feed_fakes import FakeFeed, runner_for

    class Feed(SocketFeed, FakeFeed):
        pass

    feed = Feed()
    feed.key = "dhan"
    runner = runner_for(feed, watchlist=["NSE:NIFTY50-INDEX"], market_open=lambda: market_open)
    runner.subscribed = {"NSE:NIFTY50-INDEX"}
    for name, value in switches.items():
        setattr(runner, name, value)
    runner.watchdog_alerts = mock.MagicMock()
    return runner, feed


class FrameSilenceTests(unittest.TestCase):
    def _check(self, idle, **kwargs):
        runner, feed = _socket_runner(**kwargs)
        feed.idle = idle
        printed = []
        with mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(" ".join(map(str, a)))):
            runner.check_frame_silence()
        return runner, printed

    def test_no_frame_for_46s_in_open_session_is_a_dead_line(self):
        from core.live import watchdog_alerts

        runner, printed = self._check(46.0)
        self.assertTrue(runner.restart_required)
        self.assertEqual(1, len(printed))
        self.assertIn("connected but silent for 46s in open session (no frame at all, not even a ping)", printed[0])
        cause, detail = runner.watchdog_alerts.report.call_args.args
        self.assertEqual(watchdog_alerts.SILENT, cause)
        self.assertIn("not even a ping", detail)

    def test_44s_is_not_yet(self):
        runner, printed = self._check(44.0)
        self.assertFalse(runner.restart_required)
        self.assertEqual([], printed)

    def test_a_closed_market_is_never_a_dead_line(self):
        runner, _ = self._check(600.0, market_open=False)
        self.assertFalse(runner.restart_required)

    def test_the_switch_at_zero_turns_the_rule_off(self):
        runner, _ = self._check(600.0, _frame_silence_seconds=0.0)
        self.assertFalse(runner.restart_required)

    def test_nothing_subscribed_is_nothing_to_wait_for(self):
        runner, feed = _socket_runner()
        runner.subscribed = set()
        feed.idle = 600.0
        runner.check_frame_silence()
        self.assertFalse(runner.restart_required)

    def test_the_default_is_45s_and_the_environment_can_change_it(self):
        from _feed_fakes import FakeFeed, runner_for

        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("FEED_FRAME_SILENCE_SECONDS", None)
            self.assertEqual(45.0, runner_for(FakeFeed())._frame_silence_seconds)
            os.environ["FEED_FRAME_SILENCE_SECONDS"] = "0"
            self.assertEqual(0.0, runner_for(FakeFeed())._frame_silence_seconds)


class WatchCycleTests(unittest.TestCase):
    """One pass of the connection manager's loop, on a clock the test holds."""

    def _one_cycle(self, runner, now, last_message):
        def connect(credentials, on_ticks, on_event):
            runner.socket_connected = True
            runner.connected_at = 0.0
            runner.last_message = last_message

        runner._feed.connect = connect
        runner._feed.acquire_credentials = lambda not_this=None: "token"

        def one_wait(_):
            runner._stop.set()          # this pass is the last
            return False

        with mock.patch.object(runner._stop, "wait", side_effect=one_wait), \
             mock.patch("core.live.feed_runner.time.monotonic", return_value=now), \
             mock.patch("builtins.print"):
            runner._connect_once_and_watch()

    def test_before_the_first_frame_only_the_120s_rule_applies(self):
        runner, feed = _socket_runner()
        feed.idle = None
        self._one_cycle(runner, now=100.0, last_message=None)
        self.assertFalse(runner.restart_required, "100 s with no frame yet is the 120 s rule's to call")

        runner, feed = _socket_runner()
        self._one_cycle(runner, now=121.0, last_message=None)
        self.assertTrue(runner.restart_required)

    def test_frames_without_ticks_still_restart_only_after_120s(self):
        # Pings arrive (the line is alive) but nothing trades.
        runner, feed = _socket_runner()
        feed.idle = 3.0
        self._one_cycle(runner, now=110.0, last_message=0.5)
        self.assertFalse(runner.restart_required)

        runner, feed = _socket_runner()
        feed.idle = 3.0
        self._one_cycle(runner, now=121.0, last_message=0.5)
        self.assertTrue(runner.restart_required)
        self.assertIn("connected but silent for 120s in open session", runner.watchdog_alerts.report.call_args.args[1])

    def test_a_line_with_no_frame_is_restarted_well_before_120s(self):
        runner, feed = _socket_runner()
        feed.idle = 50.0
        self._one_cycle(runner, now=60.0, last_message=10.0)
        self.assertTrue(runner.restart_required)
        self.assertIn("not even a ping", runner.watchdog_alerts.report.call_args.args[1])


class BacklogTests(unittest.TestCase):
    def _samples(self, runner, feed, values, start=0.0):
        printed = []
        with mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(" ".join(map(str, a)))):
            for i, value in enumerate(values):
                feed.backlog = value
                runner.watch_backlog(start + i)
        return printed

    def test_ten_seconds_at_200kb_is_falling_behind_said_once_and_carried_in_the_heartbeat(self):
        runner, feed = _socket_runner()
        feed.feed_stats = {"pass_ms_p99": 4.2}
        printed = self._samples(runner, feed, [200 * 1024] * 15)
        self.assertEqual(1, len(printed))
        self.assertIn("[dhan] FALLING BEHIND: 200 KB unread on the socket for 10s (emit pass p99 4.2 ms)",
                      printed[0])
        self.assertIn("falling behind", runner.health_note())

    def test_nine_seconds_is_nothing(self):
        runner, feed = _socket_runner()
        printed = self._samples(runner, feed, [200 * 1024] * 9 + [10 * 1024])
        self.assertEqual([], printed)
        self.assertEqual("", runner.health_note())

    def test_ten_seconds_back_under_the_line_clears_the_note(self):
        runner, feed = _socket_runner()
        self._samples(runner, feed, [200 * 1024] * 12)
        printed = self._samples(runner, feed, [0] * 9, start=12.0)
        self.assertEqual([], printed)
        self.assertIn("falling behind", runner.health_note(), "not after nine")
        printed = self._samples(runner, feed, [0], start=21.0)
        self.assertEqual(["[dhan] socket drained again after 21s"], printed)
        self.assertEqual("", runner.health_note())

    def test_a_closed_market_or_no_socket_says_nothing(self):
        runner, feed = _socket_runner(market_open=False)
        self.assertEqual([], self._samples(runner, feed, [900 * 1024] * 20))
        runner, feed = _socket_runner()
        self.assertEqual([], self._samples(runner, feed, [None] * 20))
        self.assertEqual([], runner._backlog_samples)

    def test_the_line_is_configurable(self):
        runner, feed = _socket_runner(_backlog_alert_bytes=512 * 1024)
        self.assertEqual([], self._samples(runner, feed, [200 * 1024] * 20))


class StatsLineTests(unittest.TestCase):
    def test_a_feed_with_stats_gets_a_line_a_minute(self):
        runner, feed = _socket_runner()
        feed.feed_stats = {"frames": 1000, "bytes": 100 * 1024, "pings": 1, "superseded": 0,
                           "dropped_unknown": 0, "ticks_out": 100, "pass_ms_p99": 1.0, "pass_ms_max": 2.0}
        printed = []
        with mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(" ".join(map(str, a)))):
            runner.print_stats_if_due(0.0)
            feed.backlog = 2048
            runner.watch_backlog(30.0)
            runner.print_stats_if_due(30.0)
            feed.feed_stats = {"frames": 181000, "bytes": 100 * 1024 + 60 * 400 * 1024, "pings": 7,
                               "superseded": 18000, "dropped_unknown": 3, "ticks_out": 18100,
                               "pass_ms_p99": 3.5, "pass_ms_max": 9.25}
            runner._stream_batches += 600
            runner._stream_batch_ms.extend([1.0] * 600)
            runner.print_stats_if_due(60.0)
        self.assertEqual(1, len(printed))
        line = printed[0]
        for part in ("[dhan] read path: frames 180000 (3000/s), 400 KB/s, pings 6, superseded 10%, dropped 3",
                     "socket backlog p99 2 KB max 2 KB", "emitted 300 ticks/s, pass p99 3.5 ms max 9.2",
                     "stream batches 600, p99 1.0 ms, errors 0"):
            self.assertIn(part, line)

    def test_a_feed_without_stats_gets_none(self):
        from _feed_fakes import FakeFeed, runner_for

        runner = runner_for(FakeFeed())
        with mock.patch("builtins.print") as printed:
            for second in range(0, 200, 10):
                runner.print_stats_if_due(float(second))
        printed.assert_not_called()


if __name__ == "__main__":
    unittest.main()
