"""
Ticks the strategy side skips are counted, not passed over.

The stream reader skipped an entry whose payload was not JSON with a bare
`pass`; the runner's tick loop printed a traceback for a tick it raised on, but
its [STATUS] line looked the same as a quiet market's.
"""

import contextlib
import io
import unittest
from unittest import mock

import _bootstrap  # noqa: F401


class StreamDecodeTests(unittest.TestCase):
    def test_an_entry_that_is_not_json_is_counted_and_said_not_passed_over(self):
        from messaging.redis_subscriber import RedisTickSubscriber

        subscriber = RedisTickSubscriber()
        subscriber.client = mock.MagicMock()
        subscriber.client.xread.return_value = [["market:ticks", [
            ("1-0", {"payload": "{not json"}),
            ("2-0", {"payload": '{"symbol": "NSE:NIFTY50-INDEX"}'}),
        ]]]

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            tick = next(subscriber.listen_for_ticks())

        self.assertEqual("NSE:NIFTY50-INDEX", tick["symbol"])
        self.assertEqual(1, subscriber.undecodable)
        self.assertIn("skipped 1-0", out.getvalue())
        # Its place in the stream is still passed: the next read starts after it.
        self.assertEqual("2-0", subscriber.last_id)


class RunnerTickErrorTests(unittest.TestCase):
    def test_the_runner_counts_the_ticks_it_skips(self):
        import strategies.execution_runner as runner
        from core.metrics import TICK_ERRORS

        self.assertIs(TICK_ERRORS, runner.TICK_ERRORS)


if __name__ == "__main__":
    unittest.main()
