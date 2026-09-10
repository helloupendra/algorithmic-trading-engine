"""
A dead broker token must not look like a healthy feed.

On 2026-09-10 the ingestor connected at 08:45 with the previous day's token.
FYERS answered the connect with {'code': -99, 'message': 'Token is expired'},
then reported the socket as connected, and no message ever followed. The
heartbeat said "Running" for two hours because silence was only measured from
the LAST message — and there had never been one. Every strategy sat deaf.

The module talks to Redis and the FYERS SDK at import time; both are stubbed.
"""

import sys
import types
import unittest
from unittest import mock

import _bootstrap  # noqa: F401


def _install_import_stubs():
    if "fyers_apiv3" not in sys.modules:
        fyers_pkg = types.ModuleType("fyers_apiv3")
        websocket_pkg = types.ModuleType("fyers_apiv3.FyersWebsocket")
        data_ws = types.ModuleType("fyers_apiv3.FyersWebsocket.data_ws")
        data_ws.FyersDataSocket = mock.MagicMock()
        websocket_pkg.data_ws = data_ws
        fyers_pkg.FyersWebsocket = websocket_pkg
        sys.modules["fyers_apiv3"] = fyers_pkg
        sys.modules["fyers_apiv3.FyersWebsocket"] = websocket_pkg
        sys.modules["fyers_apiv3.FyersWebsocket.data_ws"] = data_ws

    publisher_module = types.ModuleType("messaging.redis_publisher")
    publisher_module.build_publisher_from_env = lambda: mock.MagicMock()
    publisher_module.normalize_tick = lambda raw: raw
    sys.modules["messaging.redis_publisher"] = publisher_module


_install_import_stubs()

from market_data.live import fyers_streamer as streamer  # noqa: E402


class TokenRejectionDetection(unittest.TestCase):
    def test_the_real_expired_token_message_is_a_rejection(self):
        self.assertTrue(streamer.is_token_rejection(
            {"type": "cn", "code": -99, "message": "Token is expired", "s": "error"}))

    def test_rest_style_authentication_failure_is_a_rejection(self):
        self.assertTrue(streamer.is_token_rejection(
            {"code": -16, "message": "Could not authenticate the user", "s": "error"}))
        self.assertTrue(streamer.is_token_rejection("invalid token"))

    def test_ordinary_socket_errors_are_not(self):
        self.assertFalse(streamer.is_token_rejection({"code": 200, "message": "ok"}))
        self.assertFalse(streamer.is_token_rejection("Connection reset by peer"))
        self.assertFalse(streamer.is_token_rejection(Exception("timed out")))


class SilenceIsMeasuredFromTheConnect(unittest.TestCase):
    def test_no_message_ever_counts_from_the_connect(self):
        # Connected at t=100, nothing since, now t=400: silent 300s.
        self.assertEqual(300, streamer.feed_silent_for(400, True, 100, None))

    def test_last_message_wins_once_there_was_one(self):
        self.assertEqual(50, streamer.feed_silent_for(400, True, 100, 350))

    def test_a_down_socket_is_not_a_stall(self):
        self.assertIsNone(streamer.feed_silent_for(400, False, 100, None))


class StatusOnADeadToken(unittest.TestCase):
    def setUp(self):
        streamer.socket_connected = True
        streamer.rejected_token = None
        streamer.token_rejected_at = None
        streamer.connected_at = 0.0
        streamer.last_tick_monotonic = None
        streamer.subscribed_symbols = {"NSE:NIFTY50-INDEX"}
        streamer.current_access_token = "dead-token"

    def tearDown(self):
        streamer.rejected_token = None
        streamer.token_rejected_at = None
        streamer.subscribed_symbols = set()
        streamer.socket_connected = False

    def test_connected_but_never_a_message_is_stalled_when_the_market_is_open(self):
        with mock.patch.object(streamer.time, "monotonic", return_value=streamer.STALL_AFTER_SECONDS + 1.0), \
             mock.patch.object(streamer, "is_market_open", return_value=True):
            self.assertEqual("Stalled", streamer.compute_status())

    def test_the_same_silence_outside_market_hours_is_running(self):
        with mock.patch.object(streamer.time, "monotonic", return_value=streamer.STALL_AFTER_SECONDS + 1.0), \
             mock.patch.object(streamer, "is_market_open", return_value=False):
            self.assertEqual("Running", streamer.compute_status())

    def test_a_rejected_token_is_stalled_immediately(self):
        streamer.onerror({"type": "cn", "code": -99, "message": "Token is expired", "s": "error"})
        self.assertEqual("dead-token", streamer.rejected_token)
        with mock.patch.object(streamer, "is_market_open", return_value=None):
            self.assertEqual("Stalled", streamer.compute_status())

    def test_a_rejected_token_is_recorded_once(self):
        streamer.onerror({"code": -99, "message": "Token is expired"})
        first = streamer.token_rejected_at
        streamer.onerror({"code": -99, "message": "Token is expired"})
        self.assertEqual(first, streamer.token_rejected_at)


class WaitingForAReplacementToken(unittest.TestCase):
    def test_the_refused_token_is_not_handed_back(self):
        answers = iter([
            {"isAuthenticated": True, "accessToken": "dead-token"},
            {"isAuthenticated": True, "accessToken": "dead-token"},
            {"isAuthenticated": True, "accessToken": "fresh-token"},
        ])
        response = mock.MagicMock()
        response.json.side_effect = lambda: next(answers)
        with mock.patch.object(streamer.http, "get", return_value=response), \
             mock.patch.object(streamer.time, "sleep") as sleep:
            token = streamer.get_active_session(not_this_token="dead-token")
        self.assertEqual("fresh-token", token)
        self.assertEqual(2, sleep.call_count)

    def test_without_a_refused_token_the_first_valid_answer_is_used(self):
        response = mock.MagicMock()
        response.json.return_value = {"isAuthenticated": True, "accessToken": "dead-token"}
        with mock.patch.object(streamer.http, "get", return_value=response), \
             mock.patch.object(streamer.time, "sleep") as sleep:
            self.assertEqual("dead-token", streamer.get_active_session())
        sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
