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


class Clock:
    def __init__(self, t=1000.0):
        self.t = t

    def __call__(self):
        return self.t


class BatchOnly:
    """A publisher with both methods, recording what it was asked and when."""

    def __init__(self, log, fail=False):
        self.log = log
        self.fail = fail
        self.batches = []

    def publish_ticks(self, messages):
        self.log.append(("publish", len(messages)))
        if self.fail:
            raise ConnectionError("redis is not answering")
        self.batches.append(list(messages))

    def publish_tick(self, message):
        self.log.append(("publish_tick", 1))
        if self.fail:
            raise ConnectionError("redis is not answering")
        self.batches.append([message])


class OneAtATime:
    """The old shape: publish_tick only (the TrueData and older test fakes)."""

    def __init__(self):
        self.published = []

    def publish_tick(self, message):
        self.published.append(message)


def _ticks(n):
    return [{"symbol": f"NSE:S{i}-EQ", "lastTradedPrice": 100.0 + i, "exchangeTimestampUtc": "2026-09-28T04:00:00Z",
             "rawPayload": '{"type":"quote"}'} for i in range(n)]


class RunnerBatchTests(unittest.TestCase):
    def _runner(self, publisher, **kwargs):
        from _feed_fakes import FakeFeed, runner_for

        runner = runner_for(FakeFeed(), publisher=publisher)
        for name, value in kwargs.items():
            setattr(runner, name, value)
        return runner

    def test_a_batch_of_ticks_is_one_publish_after_every_tick_is_on_its_way_to_the_api(self):
        log = []
        publisher = BatchOnly(log)
        runner = self._runner(publisher)
        offer = runner.pump.offer
        runner.pump.offer = lambda tick: (log.append(("offer", tick["symbol"])), offer(tick))

        runner.on_ticks(_ticks(5))

        self.assertEqual([("offer", f"NSE:S{i}-EQ") for i in range(5)] + [("publish", 5)], log)
        self.assertEqual([f"NSE:S{i}-EQ" for i in range(5)], [m["symbol"] for m in publisher.batches[0]])
        self.assertEqual(5, runner.pump.depth())

    def test_a_publisher_with_only_publish_tick_still_gets_every_tick(self):
        publisher = OneAtATime()
        runner = self._runner(publisher)
        runner.on_ticks(_ticks(3))
        self.assertEqual(["NSE:S0-EQ", "NSE:S1-EQ", "NSE:S2-EQ"], [m["symbol"] for m in publisher.published])

    def test_feed_stream_batch_off_goes_back_to_one_at_a_time(self):
        log = []
        runner = self._runner(BatchOnly(log), _stream_batch=False)
        runner.on_ticks(_ticks(3))
        self.assertEqual([("publish_tick", 1)] * 3, log)

    def test_a_failed_batch_is_counted_the_ticks_are_stored_and_the_stream_rests_five_seconds(self):
        log = []
        publisher = BatchOnly(log, fail=True)
        runner = self._runner(publisher)
        clock = Clock()
        printed = []
        with mock.patch("core.live.feed_runner.time.monotonic", clock), \
             mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(" ".join(map(str, a)))):
            runner.on_ticks(_ticks(4))
            self.assertEqual(4, runner._publish_errors)
            self.assertEqual(4, runner.pump.depth(), "storage does not wait on Redis")
            self.assertEqual(1, len(printed), "said on crossing 1")
            self.assertIn("redis is not answering", printed[0])

            publisher.fail = False
            clock.t += 4.9
            runner.on_ticks(_ticks(97))
            self.assertEqual([("publish", 4)], log, "Redis is left alone for five seconds")
            self.assertEqual(101, runner._publish_errors)
            self.assertEqual(2, len(printed), "said again on crossing 100, although the total jumped past it")

            clock.t += 0.1
            runner.on_ticks(_ticks(2))
            self.assertEqual([("publish", 4), ("publish", 2)], log, "and then written to again")
            self.assertEqual(101, runner._publish_errors)
        self.assertEqual(4 + 97 + 2, runner.pump.depth())

    def test_the_counts_cross_every_ten_thousand(self):
        runner = self._runner(OneAtATime())
        printed = []
        with mock.patch("builtins.print", side_effect=lambda *a, **_: printed.append(a[0])):
            runner._count_publish_errors(999)      # crosses 1 and 100: one line
            runner._count_publish_errors(9000)     # crosses 1000
            runner._count_publish_errors(1)        # reaches 10,000
            runner._count_publish_errors(5)        # crosses nothing
            runner._count_publish_errors(19000)    # crosses 20,000
        self.assertEqual(["(999 so far)", "(9999 so far)", "(10000 so far)", "(29005 so far)"],
                         [line[line.index("("):line.index(")") + 1] for line in printed])

    def test_a_message_that_cannot_be_built_is_a_publish_error_not_a_rejected_tick(self):
        runner = self._runner(OneAtATime())
        with mock.patch("core.live.feed_runner.normalize_tick", side_effect=ValueError("odd tick")), \
             mock.patch("builtins.print"):
            runner.on_ticks(_ticks(1))
        self.assertEqual((1, 0, 1), (runner._publish_errors, runner.ticks_rejected, runner.pump.depth()))


class GoldenMessageTests(unittest.TestCase):
    """The stream message is what it was before batching, for every vendor's shape of tick."""

    @staticmethod
    def _old_message(tick, publish_raw_payload):
        # FeedRunner._publish as it stood at ee27219, less the XADD.
        message = normalize_tick(tick)
        message["sourceKey"] = tick.get("sourceKey")
        message["isReplay"] = bool(tick.get("isReplay"))
        message["rawPayload"] = tick.get("rawPayload", "") if publish_raw_payload else ""
        return message

    def _dhan_tick(self):
        import struct
        from market_data.live.vendors.dhan import DhanFeed

        feed = DhanFeed("client-id-for-tests", "token-for-tests", http=mock.MagicMock(),
                        credentials_source=lambda: None, min_tick_interval_ms=0,
                        vendor_names={"NSE:NIFTY2692925000CE": "NSE_FNO:47317:OPTIDX"})
        feed._send = lambda message: True
        feed.subscribe(["NSE:NIFTY2692925000CE"])
        out = []
        feed._on_ticks = out.extend
        feed._on_event = lambda e, d="": None
        body = struct.pack("<BHBIfHIfIIIIIIffff", 8, 162, 2, 47317, 134.25, 50, 1790000000, 112.4,
                           387600000, 1241, 1443, 6543875, 6600000, 5900000, 120.0, 0.0, 140.0, 110.0)
        levels = [(425, 300, 3, 2, 134.2, 134.5)] + [(0, 0, 0, 0, 0.0, 0.0)] * 4
        feed._on_message(body + b"".join(struct.pack("<IIHHff", *level) for level in levels))
        return out[0], feed

    def _fyers_tick(self):
        from market_data.live.vendors.fyers import message_to_tick

        return message_to_tick({"symbol": "NSE:NIFTY50-INDEX", "ltp": 25012.35, "open_price": 25000.0,
                                "high_price": 25100.0, "low_price": 24900.0, "prev_close_price": 24950.0,
                                "exch_feed_time": 1790000000, "type": "if", "ch": 62.35, "chp": 0.25})

    def _truedata_tick(self):
        return {"symbol": "NSE:BANKNIFTY26SEP56400CE", "dataType": "symbolUpdate",
                "exchangeTimestampUtc": "2026-09-11T03:55:02Z", "lastTradedPrice": 116.4, "bidPrice": 116.3,
                "askPrice": 116.5, "bidSize": 150, "askSize": 90, "open": 110.0, "high": 120.0, "low": 105.5,
                "prevClose": 112.0, "volume": 123456, "openInterest": 45000,
                "rawPayload": '{"kind":"trade","atp":114.2}'}

    def test_the_new_message_is_the_old_one_for_every_vendor(self):
        from _feed_fakes import FakeFeed, runner_for

        dhan_tick, dhan_feed = self._dhan_tick()
        cases = [("dhan", dhan_tick, False), ("fyers", self._fyers_tick(), True),
                 ("truedata", self._truedata_tick(), True), ("truedata replay", self._truedata_tick(), True)]
        for name, tick, raw_on_stream in cases:
            with self.subTest(name):
                feed = FakeFeed(is_replay=name.endswith("replay"), publish_raw_payload=raw_on_stream)
                feed.key = name.split()[0]
                runner = runner_for(feed, publisher=OneAtATime())
                message = runner._accept(dict(tick))
                stored = runner.pump.drain(1)[0]
                old = self._old_message(stored, raw_on_stream)
                for either in (message, old):
                    either.pop("receivedUtc")
                self.assertEqual(old, message)
                self.assertEqual(name.endswith("replay"), message["isReplay"])
                self.assertEqual(stored["rawPayload"] if raw_on_stream else "", message["rawPayload"])


if __name__ == "__main__":
    unittest.main()
