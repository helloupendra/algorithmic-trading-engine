"""
What a live feed could not deliver reaches its heartbeat.

A batch the API refused was one printed line — never counted — and the backlog
counts the heartbeat did carry were dropped by the API's heartbeat DTO. A tick
the feed could not handle was neither published nor stored, uncounted. Each is
now a count in every heartbeat, and a recent failure is the heartbeat's error
when the connection itself has none.
"""

import contextlib
import io
import sys
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for

import core.greeks_calculator as greeks_calculator


class FeedDeliveryCountTests(unittest.TestCase):
    def _runner(self):
        runner = runner_for(FakeFeed())
        runner._http.post.return_value.status_code = 200
        return runner

    def _heartbeat(self, runner):
        runner._http.post.reset_mock()
        runner._http.post.return_value.status_code = 200
        runner.send_heartbeat()
        return runner._http.post.call_args.kwargs["json"]

    def test_a_batch_the_api_refused_is_counted_and_reported(self):
        runner = self._runner()
        runner._http.post.return_value.status_code = 500
        runner._http.post.return_value.text = "database unavailable"

        with contextlib.redirect_stdout(io.StringIO()):
            runner._post_batch([{"symbol": "NSE:X"}, {"symbol": "NSE:Y"}])

        payload = self._heartbeat(runner)
        self.assertEqual(2, payload["ticksNotStored"])
        self.assertIn("2 tick(s) not stored", payload["lastError"])
        self.assertIn("HTTP 500", payload["lastError"])

    def test_a_batch_that_never_got_an_answer_is_counted_too(self):
        runner = self._runner()
        runner._http.post.side_effect = ConnectionError("API down")

        with contextlib.redirect_stdout(io.StringIO()):
            runner._post_batch([{"symbol": "NSE:X"}])

        runner._http.post.side_effect = None
        self.assertEqual(1, self._heartbeat(runner)["ticksNotStored"])

    def test_the_note_fades_but_the_count_stays(self):
        runner = self._runner()
        runner._http.post.return_value.status_code = 500
        with contextlib.redirect_stdout(io.StringIO()):
            runner._post_batch([{"symbol": "NSE:X"}])

        at = runner.last_store_failure[0]
        self.assertEqual("", runner.health_note(now=at + runner.STORE_FAILURE_NOTE_SECONDS + 1))
        self.assertEqual(1, self._heartbeat(runner)["ticksNotStored"])

    def test_a_connection_error_is_not_hidden_behind_the_note(self):
        runner = self._runner()
        runner._http.post.return_value.status_code = 500
        with contextlib.redirect_stdout(io.StringIO()):
            runner._post_batch([{"symbol": "NSE:X"}])
        runner.last_error = "disconnected"

        self.assertEqual("disconnected", self._heartbeat(runner)["lastError"])

    def test_a_tick_the_feed_cannot_handle_is_counted(self):
        runner = self._runner()
        runner.enricher.enrich = mock.MagicMock(side_effect=ValueError("bad price"))

        with contextlib.redirect_stdout(io.StringIO()):
            runner.on_ticks([{"symbol": "NSE:X", "lastTradedPrice": 1.0}])

        self.assertEqual(1, self._heartbeat(runner)["ticksRejected"])

    def test_the_heartbeat_names_a_missing_greeks_library(self):
        runner = self._runner()
        with mock.patch.object(greeks_calculator, "IMPORT_ERROR", "No module named 'vollib'"):
            payload = self._heartbeat(runner)

        self.assertEqual("No module named 'vollib'", payload["greeksUnavailable"])
        self.assertIn("option greeks unavailable", payload["lastError"])

    def test_a_pricing_module_that_will_not_load_does_not_stop_the_heartbeat(self):
        # A heartbeat that raised every beat would make a working feed look dead.
        runner = self._runner()
        with mock.patch.dict(sys.modules, {"core.greeks_calculator": None}):
            payload = self._heartbeat(runner)

        self.assertIn("did not load", payload["greeksUnavailable"])

    def test_a_healthy_feed_reports_zeros_and_no_error(self):
        payload = self._heartbeat(self._runner())
        self.assertEqual(0, payload["ticksNotStored"])
        self.assertEqual(0, payload["ticksRejected"])
        self.assertEqual("", payload["greeksUnavailable"])
        self.assertEqual("", payload["lastError"])
        self.assertIn("queueDepth", payload)
        self.assertIn("ticksDropped", payload)


if __name__ == "__main__":
    unittest.main()
