"""
A dead credential must not look like a healthy feed — for any vendor.

On 2026-09-10 the FYERS ingestor connected at 08:45 with the previous day's
token. FYERS answered with {'code': -99, 'message': 'Token is expired'}, then
reported the socket as connected, and no message ever followed. The heartbeat
said "Running" for two hours because silence was only measured from the LAST
message — and there had never been one. Every strategy sat deaf.
"""

import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for
from core.live.feed_runner import FeedRunner, feed_silent_for
from core.live.vendor_feed import FeedEvent
from market_data.live.vendors.fyers import FyersFeed, is_token_rejection


class TokenRejectionDetection(unittest.TestCase):
    def test_the_real_expired_token_message_is_a_rejection(self):
        self.assertTrue(is_token_rejection({"type": "cn", "code": -99, "message": "Token is expired", "s": "error"}))

    def test_rest_style_authentication_failure_is_a_rejection(self):
        self.assertTrue(is_token_rejection({"code": -16, "message": "Could not authenticate the user", "s": "error"}))
        self.assertTrue(is_token_rejection("invalid token"))

    def test_ordinary_socket_errors_are_not(self):
        self.assertFalse(is_token_rejection({"code": 200, "message": "ok"}))
        self.assertFalse(is_token_rejection("Connection reset by peer"))
        self.assertFalse(is_token_rejection(Exception("timed out")))


class SilenceIsMeasuredFromTheConnect(unittest.TestCase):
    def test_no_message_ever_counts_from_the_connect(self):
        self.assertEqual(300, feed_silent_for(400, True, 100, None))

    def test_last_message_wins_once_there_was_one(self):
        self.assertEqual(50, feed_silent_for(400, True, 100, 350))

    def test_a_down_socket_is_not_a_stall(self):
        self.assertIsNone(feed_silent_for(400, False, 100, None))


class StatusOnADeadCredential(unittest.TestCase):
    def setUp(self):
        self.market_open = True
        self.runner = runner_for(FakeFeed(), market_open=lambda: self.market_open)
        self.runner.connected_at = 0.0
        self.runner.last_message = None
        self.runner.subscribed = {"NSE:NIFTY50-INDEX"}
        self.runner.current_credential = "dead-token"

    def test_connected_but_never_a_message_is_stalled_when_the_session_is_open(self):
        with mock.patch("core.live.feed_runner.time.monotonic", return_value=FeedRunner.STALL_AFTER_SECONDS + 1.0):
            self.assertEqual("Stalled", self.runner.compute_status())

    def test_the_same_silence_outside_the_session_is_running(self):
        self.market_open = False
        with mock.patch("core.live.feed_runner.time.monotonic", return_value=FeedRunner.STALL_AFTER_SECONDS + 1.0):
            self.assertEqual("Running", self.runner.compute_status())

    def test_a_rejected_credential_is_stalled_immediately(self):
        self.runner.on_event(FeedEvent.CREDENTIALS_REJECTED, "Token is expired")
        self.assertEqual("dead-token", self.runner.rejected_credential)
        self.assertEqual("Stalled", self.runner.compute_status())

    def test_a_rejected_credential_is_recorded_once(self):
        self.runner.on_event(FeedEvent.CREDENTIALS_REJECTED, "Token is expired")
        first = self.runner.credential_rejected_at
        self.runner.on_event(FeedEvent.CREDENTIALS_REJECTED, "Token is expired")
        self.assertEqual(first, self.runner.credential_rejected_at)

    def test_a_login_refusal_is_its_own_status(self):
        self.runner.on_event(FeedEvent.REFUSED, "User Already Connected")
        self.assertEqual("Refused", self.runner.compute_status())

    def test_the_fyers_adapter_reports_a_dead_token_as_a_rejection(self):
        events = []
        socket = mock.MagicMock()
        data_ws = mock.MagicMock()
        data_ws.FyersDataSocket.return_value = socket
        with mock.patch.dict("sys.modules", {
            "fyers_apiv3": mock.MagicMock(), "fyers_apiv3.FyersWebsocket": mock.MagicMock(data_ws=data_ws),
        }), mock.patch("market_data.live.vendors.fyers.require_app_id", return_value="APP-100"):
            FyersFeed(http=mock.MagicMock()).connect("tok", lambda t: None, lambda e, d: events.append(e))
        on_error = data_ws.FyersDataSocket.call_args.kwargs["on_error"]
        on_error({"type": "cn", "code": -99, "message": "Token is expired"})
        on_error("Connection reset by peer")
        self.assertEqual([FeedEvent.CREDENTIALS_REJECTED, FeedEvent.ERROR], events)


class WaitingForAReplacementToken(unittest.TestCase):
    def test_the_refused_token_is_not_handed_back(self):
        answers = iter([
            {"isAuthenticated": True, "accessToken": "dead-token"},
            {"isAuthenticated": True, "accessToken": "dead-token"},
            {"isAuthenticated": True, "accessToken": "fresh-token"},
        ])
        http = mock.MagicMock()
        http.get.return_value.json.side_effect = lambda: next(answers)
        with mock.patch("market_data.live.vendors.fyers.time.sleep") as sleep:
            token = FyersFeed(http=http).acquire_credentials(not_this="dead-token")
        self.assertEqual("fresh-token", token)
        self.assertEqual(2, sleep.call_count)

    def test_without_a_refused_token_the_first_valid_answer_is_used(self):
        http = mock.MagicMock()
        http.get.return_value.json.return_value = {"isAuthenticated": True, "accessToken": "dead-token"}
        with mock.patch("market_data.live.vendors.fyers.time.sleep") as sleep:
            self.assertEqual("dead-token", FyersFeed(http=http).acquire_credentials())
        sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
