"""
The TrueData adapter, and the runner's rules that every vendor now shares.

The touchline row and its numbers were captured from the live sandbox socket on
2026-09-11; the trade row follows the field order in TrueData's documentation.
The runner tests pin the two failures of the first evening recap: ticks stored
but never published to the strategies' Redis stream, and a replay snapshot
stamped with the wall clock freezing every quote.
"""

import json
import os
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for
from core.live.symbol_list import parse_symbol_list
from core.live.vendor_feed import FeedEvent
from market_data.live.vendors.truedata import TrueDataFeed

TOUCHLINE = {
    "success": True, "message": "symbols added", "symbolsadded": 1,
    "symbollist": [[
        "NIFTY26091523400CE", "302965676", "2026-09-11T15:40:00",
        "133.6", "11895", "111.97", "387587655",
        "120.0", "140.0", "110.0", "128.0",
        "6543875", "5900000", "0",
        "133.65", "750", "135.0", "1200",
    ]],
    "totalsymbolsubscribed": 1,
}

TRADE = {"trade": [
    "302965676", "2026-09-11T14:02:32", "134.25", "50", "112.4", "387600000",
    "120.0", "140.0", "110.0", "128.0", "6544000", "5900000", "0",
    "", "4775", "134.2", "425", "134.5", "300",
]}


def _adapter(host="push.truedata.in", **kwargs):
    ticks, events = [], []
    feed = TrueDataFeed("u", "p", host=host, **kwargs)
    feed._on_ticks = ticks.extend
    feed._on_event = lambda e, d="": events.append((e, d))
    feed._vendor_to_canonical["NIFTY26091523400CE"] = "NSE:NIFTY2691523400CE"
    return feed, ticks, events


class TrueDataMessageTests(unittest.TestCase):
    def test_a_touchline_is_a_snapshot_under_the_canonical_symbol(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TOUCHLINE))
        self.assertEqual(1, len(ticks))
        self.assertEqual("NSE:NIFTY2691523400CE", ticks[0]["symbol"])
        self.assertTrue(ticks[0]["snapshot"], "a touchline is a picture, not a trade in time order")
        self.assertEqual(133.6, ticks[0]["lastTradedPrice"])

    def test_a_trade_is_matched_to_its_symbol_by_the_id_we_were_given(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TOUCHLINE))
        ticks.clear()
        feed._on_message(json.dumps(TRADE))
        tick = ticks[0]
        self.assertEqual("NSE:NIFTY2691523400CE", tick["symbol"])
        self.assertNotIn("snapshot", tick)
        self.assertEqual(134.25, tick["lastTradedPrice"])
        self.assertEqual(134.2, tick["bidPrice"])
        self.assertEqual(425, tick["bidSize"])
        self.assertEqual(134.5, tick["askPrice"])

    def test_the_exchange_clock_is_converted_not_relabelled(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TOUCHLINE))
        self.assertEqual("2026-09-11T10:10:00Z", ticks[0]["exchangeTimestampUtc"])

    def test_open_interest_survives_and_zero_is_unknown(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TOUCHLINE))
        self.assertEqual(6543875, ticks[0]["openInterest"])
        index = json.loads(json.dumps(TOUCHLINE))
        index["symbollist"][0][11] = "0"
        ticks.clear()
        feed._on_message(json.dumps(index))
        self.assertIsNone(ticks[0]["openInterest"])

    def test_the_payload_keeps_only_what_has_no_column(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TOUCHLINE))
        kept = json.loads(ticks[0]["rawPayload"])
        for dropped in ("ltp", "bid", "ask", "open", "high", "low", "volume", "symbol"):
            self.assertNotIn(dropped, kept)
        self.assertIn("prev_oi_close", kept)

    def test_a_duplicate_login_is_a_refusal(self):
        feed, _, events = _adapter()
        feed._on_message(json.dumps({"success": False, "message": "User Already Connected"}))
        self.assertEqual(FeedEvent.REFUSED, events[-1][0])

    def test_login_accepted_is_authenticated_and_the_vendor_limit_wins(self):
        feed, _, events = _adapter(max_symbols=50)
        feed._on_message(json.dumps({"success": True, "message": "TrueData Real Time Data Service",
                                     "segments": ["EQ"], "maxsymbols": 250, "subscription": "tick"}))
        self.assertEqual(FeedEvent.AUTHENTICATED, events[-1][0])
        self.assertEqual(250, feed.max_symbols)

    def test_a_price_for_something_never_asked_for_is_dropped(self):
        feed, ticks, _ = _adapter()
        feed._on_message(json.dumps(TRADE))
        self.assertEqual([], ticks)

    def test_a_heartbeat_is_not_a_price(self):
        feed, ticks, events = _adapter()
        feed._on_message(json.dumps({"success": True, "message": "HeartBeat", "timestamp": "2026-09-11T16:19:35"}))
        self.assertEqual([], ticks)
        self.assertEqual(FeedEvent.INFO, events[-1][0])

    def test_the_replay_host_is_a_replay(self):
        self.assertTrue(TrueDataFeed("u", "p", host="replay.truedata.in", port=8082).is_replay)
        self.assertFalse(TrueDataFeed("u", "p", host="push.truedata.in").is_replay)


class TrueDataSubscribeTests(unittest.TestCase):
    def setUp(self):
        self.sent = []
        self.feed, _, self.events = _adapter(max_symbols=3)
        self.feed._send = self.sent.append

    def test_only_names_it_can_derive_are_asked_for(self):
        taken = self.feed.subscribe(["NSE:NIFTY50-INDEX", "MCX:CRUDEOIL26SEPFUT", "NSE:BANKNIFTY26SEP57500CE"])
        self.assertEqual(["NSE:NIFTY50-INDEX"], taken)
        self.assertEqual(["NIFTY 50"], self.sent[0]["symbols"])

    def test_the_limit_is_respected_and_the_left_out_are_named(self):
        taken = self.feed.subscribe(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "NSE:RELIANCE-EQ", "NSE:SBIN-EQ"])
        self.assertEqual(3, len(taken))
        self.assertIn("NSE:SBIN-EQ", self.events[-1][1])

    def test_a_monthly_option_goes_by_the_name_it_was_given(self):
        sent = []
        feed, _, _ = _adapter(vendor_names={"NSE:BANKNIFTY26SEP56400CE": "BANKNIFTY26092956400CE"})
        feed._send = sent.append
        self.assertEqual(["NSE:BANKNIFTY26SEP56400CE"], feed.subscribe(["NSE:BANKNIFTY26SEP56400CE"]))
        self.assertEqual(["BANKNIFTY26092956400CE"], sent[0]["symbols"])

    def test_unsubscribe_speaks_the_vendors_names(self):
        self.feed.subscribe(["NSE:NIFTY50-INDEX"])
        self.sent.clear()
        self.assertTrue(self.feed.unsubscribe(["NSE:NIFTY50-INDEX"]))
        self.assertEqual({"method": "removesymbol", "symbols": ["NIFTY 50"]}, self.sent[0])


class SharedRunnerRules(unittest.TestCase):
    """What the runner does with any vendor's ticks."""

    def _runner(self, is_replay):
        publisher = mock.MagicMock()
        runner = runner_for(FakeFeed(is_replay=is_replay), publisher=publisher)
        return runner, publisher

    def test_every_tick_is_published_to_the_strategy_stream_and_stored(self):
        runner, publisher = self._runner(is_replay=False)
        runner.on_ticks([{"symbol": "NSE:NIFTY50-INDEX", "lastTradedPrice": 23400.0,
                          "exchangeTimestampUtc": "2026-09-11T03:45:00Z"}])
        publisher.publish_tick.assert_called_once()
        self.assertEqual("NSE:NIFTY50-INDEX", publisher.publish_tick.call_args.args[0]["symbol"])
        self.assertEqual(1, runner.pump.depth())

    def test_a_redis_failure_does_not_stop_the_tick_being_stored(self):
        runner, publisher = self._runner(is_replay=False)
        publisher.publish_tick.side_effect = ConnectionError("redis down")
        runner.on_ticks([{"symbol": "X", "lastTradedPrice": 1.0}])
        self.assertEqual(1, runner.pump.depth())

    def test_a_replay_snapshot_is_neither_published_nor_stored(self):
        # The first recap: a 17:31 touchline froze every contract's quote.
        runner, publisher = self._runner(is_replay=True)
        runner.on_ticks([{"symbol": "NSE:NIFTY2691523400CE", "lastTradedPrice": 116.4,
                          "exchangeTimestampUtc": "2026-09-11T12:01:02Z", "snapshot": True}])
        publisher.publish_tick.assert_not_called()
        self.assertEqual(0, runner.pump.depth())

    def test_a_replay_trade_is_marked_so_the_quote_store_takes_it(self):
        runner, publisher = self._runner(is_replay=True)
        runner.on_ticks([{"symbol": "NSE:NIFTY2691523400CE", "lastTradedPrice": 116.4,
                          "exchangeTimestampUtc": "2026-09-11T03:55:02Z"}])
        stored = runner.pump.drain(10)[0]
        self.assertTrue(stored["isReplay"])
        self.assertTrue(publisher.publish_tick.call_args.args[0]["isReplay"])

    def test_a_live_snapshot_is_kept_and_not_marked_as_a_replay(self):
        runner, _ = self._runner(is_replay=False)
        runner.on_ticks([{"symbol": "NSE:X", "lastTradedPrice": 1.0, "snapshot": True}])
        stored = runner.pump.drain(10)[0]
        self.assertNotIn("snapshot", stored)
        self.assertNotIn("isReplay", stored)

    def test_the_source_key_is_the_vendors(self):
        runner, _ = self._runner(is_replay=False)
        runner.on_ticks([{"symbol": "NSE:X", "lastTradedPrice": 1.0}])
        self.assertEqual("fake", runner.pump.drain(10)[0]["sourceKey"])

    def test_a_replay_is_a_session_in_progress_whatever_the_clock_says(self):
        runner, _ = self._runner(is_replay=True)
        runner._market_open_override = None
        self.assertTrue(runner.is_market_open())


class EntryPointTests(unittest.TestCase):
    def test_symbol_lists_parse_plain_and_named_entries(self):
        symbols, names = parse_symbol_list(
            "NSE:NIFTY50-INDEX, NSE:BANKNIFTY26SEP56400CE=BANKNIFTY26092956400CE ,,")
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE"], symbols)
        self.assertEqual({"NSE:BANKNIFTY26SEP56400CE": "BANKNIFTY26092956400CE"}, names)
        self.assertEqual(([], {}), parse_symbol_list(None))

    def test_an_unknown_vendor_is_refused_by_name(self):
        from market_data.live.vendors import build_feed
        with self.assertRaises(SystemExit):
            build_feed("nobody")

    def test_run_feed_wires_the_vendor_the_stream_and_the_named_symbols(self):
        import market_data.live.run_feed as run_feed
        env = {
            "TRUEDATA_USERNAME": "u", "TRUEDATA_PASSWORD": "p",
            "TRUEDATA_HOST": "replay.truedata.in", "TRUEDATA_REALTIME_PORT": "8082",
            "TRUEDATA_SYMBOLS": "NSE:NIFTY50-INDEX,NSE:BANKNIFTY26SEP56400CE=BANKNIFTY26092956400CE",
        }
        built = {}

        class FakeRunner:
            def __init__(self, feed, publisher=None, fixed_symbols=None, **_):
                built.update(feed=feed, publisher=publisher, fixed=fixed_symbols)
                self.source_name = feed.source_name
            def run(self):
                built["ran"] = True
            def stop(self):
                pass

        publisher = mock.MagicMock()
        with mock.patch.dict(os.environ, env, clear=False), \
             mock.patch("messaging.redis_publisher.build_publisher_from_env", return_value=publisher), \
             mock.patch("core.live.feed_runner.FeedRunner", FakeRunner), \
             mock.patch("core.safe_output.install_safe_stdio"), \
             mock.patch("signal.signal"):
            run_feed.main(["--vendor", "truedata"])

        self.assertTrue(built["ran"])
        self.assertIs(publisher, built["publisher"], "without the stream the strategies hear nothing")
        publisher.ensure_connection.assert_called_once()
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE"], built["fixed"])
        self.assertTrue(built["feed"].is_replay)
        self.assertEqual("wss://replay.truedata.in:8082", built["feed"].url)
        self.assertEqual("BANKNIFTY26092956400CE", built["feed"].to_vendor("NSE:BANKNIFTY26SEP56400CE"))


if __name__ == "__main__":
    unittest.main()


class HeartbeatTests(unittest.TestCase):
    def test_the_heartbeat_names_its_feed_so_its_pid_lands_in_its_own_slot(self):
        runner = runner_for(FakeFeed())
        runner._http.post.return_value.status_code = 200
        runner.send_heartbeat()
        payload = runner._http.post.call_args.kwargs["json"]
        self.assertEqual("fake", payload["feedKey"])
        self.assertIn("processId", payload)
