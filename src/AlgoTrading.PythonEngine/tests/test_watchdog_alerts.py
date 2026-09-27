"""
A feed's watchdog restarts reach the System channel with their cause, once per
cause per ten minutes (core/live/watchdog_alerts.py, core/live/feed_runner.py).

On 25 Sep 2026 the desk got 572 feed alerts from the runners and not one said
why the feed had gone quiet: the cause was a WATCHDOG line in the feed's own
log. And on 24 Sep the Dhan feed looped connect → subscribe → "connection
lost" every few seconds, so one message per restart would have been a storm
of its own.
"""

import json
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for
from core.live import watchdog_alerts
from core.live.feed_runner import FeedRunner
from core.live.watchdog_alerts import WatchdogAlerts


class RecordingClient:
    def __init__(self, fail=False):
        self.published: list[tuple[str, dict]] = []
        self.fail = fail

    def publish(self, channel, data):
        if self.fail:
            raise ConnectionError("redis is down")
        self.published.append((channel, json.loads(data)))
        return 1


class Publisher:
    def __init__(self, client):
        self.client = client


class Clock:
    def __init__(self, now=1000.0):
        self.now = now

    def __call__(self):
        return self.now


class ThrottleTests(unittest.TestCase):
    def setUp(self):
        self.client = RecordingClient()
        self.clock = Clock()
        self.alerts = WatchdogAlerts("dhan", Publisher(self.client), clock=self.clock, log=lambda _: None)

    def test_the_cause_reaches_the_system_channel(self):
        self.assertTrue(self.alerts.report(watchdog_alerts.SILENT, "connected but silent for 121s in open session."))
        channel, payload = self.client.published[0]
        self.assertEqual("alerts:new", channel)
        # "process" is what AlertSubscriberService.ChannelFor sends to the System channel.
        self.assertEqual("process", payload["Source"])
        self.assertEqual("warning", payload["Severity"])
        self.assertIn("Dhan feed reconnecting: connected but no data", payload["Title"])
        self.assertIn("connected but silent for 121s", payload["Message"])

    def test_keys_are_pascal_case_because_the_subscriber_binds_case_sensitively(self):
        self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 21s.")
        payload = self.client.published[0][1]
        for key in ("Title", "Message", "Source", "Severity"):
            self.assertIn(key, payload)
        self.assertNotIn("title", payload)

    def test_the_same_cause_is_sent_once_per_ten_minutes(self):
        self.assertTrue(self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 21s."))
        self.clock.now += 30
        self.assertFalse(self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 22s."))
        self.clock.now += 540   # 570 s after the first
        self.assertFalse(self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 23s."))
        self.clock.now += 30    # 600 s after the first
        self.assertTrue(self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 24s."))
        self.assertEqual(2, len(self.client.published))

    def test_a_different_cause_is_news_and_goes_at_once(self):
        self.alerts.report(watchdog_alerts.DISCONNECT, "socket down for 21s.")
        self.clock.now += 5
        self.assertTrue(self.alerts.report(watchdog_alerts.CREDENTIAL, "the credential was rejected."))
        titles = [payload["Title"] for _, payload in self.client.published]
        self.assertEqual(2, len(titles))
        self.assertIn("the socket stayed down", titles[0])
        self.assertIn("the credential was rejected", titles[1])

    def test_a_redis_that_is_down_is_tried_once_per_window_and_never_raises(self):
        client = RecordingClient(fail=True)
        logged = []
        alerts = WatchdogAlerts("dhan", Publisher(client), clock=self.clock, log=logged.append)
        self.assertFalse(alerts.report(watchdog_alerts.SILENT, "silent."))
        self.assertFalse(alerts.report(watchdog_alerts.SILENT, "silent."))
        self.assertEqual(1, len(logged))

    def test_no_publisher_means_no_message(self):
        self.assertFalse(WatchdogAlerts("dhan", None).report(watchdog_alerts.SILENT, "silent."))

    def test_a_long_vendor_message_is_cut(self):
        self.alerts.report(watchdog_alerts.CREDENTIAL, "x" * 2000)
        self.assertLess(len(self.client.published[0][1]["Message"]), 500)


class FeedRunnerWatchdogTests(unittest.TestCase):
    """The runner's WATCHDOG restarts go through the throttle, with the cause."""

    def setUp(self):
        self.client = RecordingClient()
        self.feed = FakeFeed()
        self.feed.key = "dhan"
        self.runner = runner_for(self.feed, watchlist=["NSE:NIFTY50-INDEX"], publisher=Publisher(self.client),
                                 market_open=lambda: True)
        self.feed.acquire_credentials = lambda not_this=None: "token"

    def _one_cycle(self, prepare):
        """Runs one connection cycle; `prepare` sets the fault after the connect."""
        original_connect = self.feed.connect

        def connect(credentials, on_ticks, on_event):
            original_connect(credentials, on_ticks, on_event)
            prepare()

        self.feed.connect = connect
        with mock.patch.object(self.runner._stop, "wait", return_value=False):
            self.runner._connect_once_and_watch()

    def test_a_silent_socket_is_published_as_silent(self):
        def prepare():
            self.runner.socket_connected = True
            self.runner.subscribed = {"NSE:NIFTY50-INDEX"}
            self.runner.connected_at = 0.0
            self.runner.last_message = None

        with mock.patch("core.live.feed_runner.time.monotonic", return_value=FeedRunner.STALL_AFTER_SECONDS + 5.0):
            self._one_cycle(prepare)

        self.assertEqual(1, len(self.client.published))
        payload = self.client.published[0][1]
        self.assertIn("connected but no data", payload["Title"])
        self.assertIn("not one message since the connect", payload["Message"])

    def test_a_down_socket_is_published_as_disconnect_with_the_last_error(self):
        def prepare():
            self.runner.socket_connected = False
            self.runner.disconnected_since = 0.0
            self.runner.last_error = "connection closed (1006: Connection to remote host was lost)"

        with mock.patch("core.live.feed_runner.time.monotonic", return_value=FeedRunner.DISCONNECT_RESTART_SECONDS + 5.0):
            self._one_cycle(prepare)

        payload = self.client.published[0][1]
        self.assertIn("the socket stayed down", payload["Title"])
        self.assertIn("Connection to remote host was lost", payload["Message"])

    def test_a_restart_loop_on_one_cause_is_one_message(self):
        def prepare():
            self.runner.socket_connected = False
            self.runner.disconnected_since = 0.0

        with mock.patch("core.live.feed_runner.time.monotonic", return_value=FeedRunner.DISCONNECT_RESTART_SECONDS + 5.0):
            for _ in range(5):
                self._one_cycle(prepare)

        self.assertEqual(1, len(self.client.published))


if __name__ == "__main__":
    unittest.main()
