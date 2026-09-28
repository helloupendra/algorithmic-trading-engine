"""
The tick stream's publishing: one pipelined round trip per batch, a client
that gives up in seconds, and a runner that keeps storing while Redis is away.

On 28 Sep the Dhan feed wrote every tick to market:ticks with its own XADD,
one reply at a time, on the thread that also had to read the socket; and a
Redis that stopped answering could hold that thread for about a minute, since
redis-py retries a timed-out command ten times by default. No Redis server is
needed here: the client is a mock, or a local socket that never answers.
"""

import socket
import threading
import time
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from messaging.redis_publisher import RedisTickPublisher, normalize_tick


class Unprintable:
    """Blows up if anything tries to serialise it."""

    def __str__(self):
        raise AssertionError("the raw payload was serialised")

    __repr__ = __str__


def _publisher(**kwargs):
    publisher = RedisTickPublisher(**kwargs)
    publisher.stream_client = mock.MagicMock()
    return publisher


class NormalizeTests(unittest.TestCase):
    def test_include_raw_false_never_serialises_the_incoming_message(self):
        tick = {"symbol": "NSE:NIFTY50-INDEX", "lastTradedPrice": 25000.0, "rawPayload": Unprintable()}
        message = normalize_tick(tick, include_raw=False)
        self.assertEqual("", message["rawPayload"])
        self.assertEqual(25000.0, message["lastTradedPrice"])
        with self.assertRaises(AssertionError):
            normalize_tick(tick)

    def test_the_default_is_unchanged(self):
        message = normalize_tick({"symbol": "NSE:X", "ltp": 1.5})
        self.assertEqual('{"symbol": "NSE:X", "ltp": 1.5}', message["rawPayload"])


class PublisherTests(unittest.TestCase):
    TICKS = [{"symbol": f"NSE:S{i}", "exchange": "NSE", "dataType": "symbolUpdate", "lastTradedPrice": 100.0 + i}
             for i in range(5)]

    def test_a_batch_is_one_pipeline_of_capped_xadds(self):
        publisher = _publisher(maxlen=1234)
        pipe = publisher.stream_client.pipeline.return_value
        pipe.execute.return_value = ["1-0", "1-1", "1-2", "1-3", "1-4"]

        ids = publisher.publish_ticks(self.TICKS)

        publisher.stream_client.pipeline.assert_called_once_with(transaction=False)
        self.assertEqual(5, pipe.xadd.call_count)
        for call, tick in zip(pipe.xadd.call_args_list, self.TICKS):
            stream, entry = call.args
            self.assertEqual("market:ticks", stream)
            self.assertEqual({"maxlen": 1234, "approximate": True}, call.kwargs)
            self.assertEqual(tick["symbol"], entry["symbol"])
            self.assertEqual("NSE", entry["exchange"])
            self.assertIn(f'"lastTradedPrice":{tick["lastTradedPrice"]}', entry["payload"])
        pipe.execute.assert_called_once_with()
        self.assertEqual(["1-0", "1-1", "1-2", "1-3", "1-4"], ids)
        publisher.stream_client.xadd.assert_not_called()

    def test_one_tick_is_a_plain_xadd_and_none_is_nothing(self):
        publisher = _publisher()
        publisher.stream_client.xadd.return_value = "7-0"
        self.assertEqual(["7-0"], publisher.publish_ticks(self.TICKS[:1]))
        publisher.stream_client.pipeline.assert_not_called()
        stream, entry = publisher.stream_client.xadd.call_args.args
        self.assertEqual("NSE:S0", entry["symbol"])
        self.assertEqual({"maxlen": 500_000, "approximate": True}, publisher.stream_client.xadd.call_args.kwargs)
        self.assertEqual([], publisher.publish_ticks([]))

    def test_the_entry_is_byte_for_byte_what_publish_tick_always_wrote(self):
        tick = {"symbol": "NSE:X", "exchange": "NSE", "lastTradedPrice": 1.0, "when": object()}
        entry = RedisTickPublisher._entry(tick)
        self.assertEqual({"payload", "symbol", "exchange", "dataType"}, set(entry))
        self.assertTrue(entry["payload"].startswith('{"symbol":"NSE:X","exchange":"NSE","lastTradedPrice":1.0,'))
        self.assertEqual("symbolUpdate", entry["dataType"])

    def test_a_failed_pipeline_is_not_retried(self):
        publisher = _publisher()
        pipe = publisher.stream_client.pipeline.return_value
        pipe.execute.side_effect = ConnectionError("redis went away")
        with self.assertRaises(ConnectionError):
            publisher.publish_ticks(self.TICKS)
        pipe.execute.assert_called_once_with()

    def test_without_a_stream_timeout_the_stream_shares_the_client(self):
        publisher = RedisTickPublisher()
        self.assertIs(publisher.client, publisher.stream_client)

    def test_with_one_the_stream_has_a_client_that_does_not_retry(self):
        publisher = RedisTickPublisher(stream_timeout=2)
        self.assertIsNot(publisher.client, publisher.stream_client)
        kwargs = publisher.stream_client.connection_pool.connection_kwargs
        self.assertEqual((2, 2), (kwargs["socket_timeout"], kwargs["socket_connect_timeout"]))
        self.assertEqual(0, kwargs["retry"].get_retries())
        self.assertEqual(5, publisher.client.connection_pool.connection_kwargs["socket_timeout"],
                         "the lock, the watchlist signal and the alerts keep theirs")


class SilentRedis:
    """A TCP server that accepts connections and never answers a byte."""

    def __init__(self):
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(8)
        self.port = self.listener.getsockname()[1]
        self.held = []
        self._stop = threading.Event()
        threading.Thread(target=self._accept, daemon=True).start()

    def _accept(self):
        self.listener.settimeout(0.2)
        while not self._stop.is_set():
            try:
                conn, _ = self.listener.accept()
                self.held.append(conn)
            except OSError:
                continue

    def close(self):
        self._stop.set()
        for conn in self.held + [self.listener]:
            try:
                conn.close()
            except OSError:
                pass


class BoundedClientTests(unittest.TestCase):
    def test_a_redis_that_never_answers_fails_a_batch_within_three_seconds(self):
        server = SilentRedis()
        self.addCleanup(server.close)
        publisher = RedisTickPublisher(host="127.0.0.1", port=server.port, stream_timeout=2)
        self.addCleanup(publisher.close)
        started = time.monotonic()
        with self.assertRaises(Exception):
            publisher.publish_ticks([{"symbol": "NSE:A"}, {"symbol": "NSE:B"}])
        self.assertLess(time.monotonic() - started, 3.0)


if __name__ == "__main__":
    unittest.main()
