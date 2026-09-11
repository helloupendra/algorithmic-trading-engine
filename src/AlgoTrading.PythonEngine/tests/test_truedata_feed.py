"""
The TrueData feed's message handling.

Every message below is the shape TrueData actually sends. The touchline row and
its numbers were captured from the live sandbox socket on 2026-09-11; the trade
row follows the field order in the vendor's own documentation (v2.6, 4.b.viii).

What is worth pinning here is not the parsing but the three ways a second feed
quietly corrupts a database: a price stored under the wrong symbol, an exchange
timestamp stored as if it were UTC, and a zero that is really an absence.
"""

import json
import unittest

import _bootstrap  # noqa: F401

from market_data.live.truedata_streamer import TrueDataFeed

# NIFTY 23400 CE, 15-09-2026 — a real contract, subscribed on the live socket.
TOUCHLINE = {
    "success": True,
    "message": "symbols added",
    "symbolsadded": 1,
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


class TrueDataFeedTests(unittest.TestCase):
    def setUp(self):
        self.ticks = []
        self.states = []
        self.feed = TrueDataFeed("u", "p")
        self.feed._on_tick = self.ticks.append
        self.feed._on_state = lambda event, detail="": self.states.append((event, detail))
        # The runner always subscribes before anything arrives, which is what
        # gives the feed its canonical name for a symbol id.
        self.feed._vendor_to_canonical["NIFTY26091523400CE"] = "NSE:NIFTY2691523400CE"

    def _subscribe(self):
        self.feed._on_message(json.dumps(TOUCHLINE))

    # ------------------------------------------------------------- the basics

    def test_a_touchline_is_stored_under_the_canonical_symbol(self):
        self._subscribe()
        self.assertEqual(1, len(self.ticks))
        self.assertEqual("NSE:NIFTY2691523400CE", self.ticks[0]["symbol"])
        self.assertEqual("truedata", self.ticks[0]["sourceKey"])
        self.assertEqual(133.6, self.ticks[0]["lastTradedPrice"])

    def test_a_trade_is_matched_to_its_symbol_by_the_id_we_were_given(self):
        self._subscribe()
        self.ticks.clear()
        self.feed._on_message(json.dumps(TRADE))

        self.assertEqual(1, len(self.ticks))
        tick = self.ticks[0]
        self.assertEqual("NSE:NIFTY2691523400CE", tick["symbol"])
        self.assertEqual(134.25, tick["lastTradedPrice"])
        self.assertEqual(134.2, tick["bidPrice"])
        self.assertEqual(425, tick["bidSize"])
        self.assertEqual(134.5, tick["askPrice"])

    def test_the_field_orders_of_trade_and_touchline_are_not_interchangeable(self):
        # A touchline row carries the symbol and its id at the front; a trade row
        # starts at the id. Reading one with the other's layout puts a symbol id
        # where a price belongs, and every number after it is shifted.
        self._subscribe()
        touchline_price = self.ticks[0]["lastTradedPrice"]
        self.ticks.clear()
        self.feed._on_message(json.dumps(TRADE))
        self.assertNotEqual(touchline_price, self.ticks[0]["lastTradedPrice"])
        self.assertEqual(134.25, self.ticks[0]["lastTradedPrice"])

    # ------------------------------------------------------------------- time

    def test_the_exchange_clock_is_converted_not_relabelled(self):
        self._subscribe()
        # 15:40 in Mumbai is 10:10 UTC. Storing 15:40 as UTC would put every
        # price five and a half hours into the future.
        self.assertEqual("2026-09-11T10:10:00Z", self.ticks[0]["exchangeTimestampUtc"])

    # -------------------------------------------------------- open interest

    def test_open_interest_survives_because_it_is_why_this_feed_exists(self):
        self._subscribe()
        self.assertEqual(6543875, self.ticks[0]["openInterest"])

    def test_a_zero_open_interest_is_unknown_rather_than_a_real_zero(self):
        # An index has no OI and the feed sends 0. Recording that as a number
        # would read as "every position was closed".
        index = json.loads(json.dumps(TOUCHLINE))
        index["symbollist"][0][11] = "0"
        self.feed._on_message(json.dumps(index))
        self.assertIsNone(self.ticks[0]["openInterest"])

    # ------------------------------------------------------------- the payload

    def test_the_payload_keeps_only_what_has_no_column(self):
        self._subscribe()
        kept = json.loads(self.ticks[0]["rawPayload"])
        for dropped in ("ltp", "bid", "ask", "open", "high", "low", "volume", "symbol"):
            self.assertNotIn(dropped, kept)
        self.assertIn("atp", kept)
        self.assertIn("prev_oi_close", kept)

    # -------------------------------------------------------------- the states

    def test_a_duplicate_login_is_its_own_state_because_retrying_will_not_help(self):
        self.feed._on_message(json.dumps({
            "success": False, "message": "User Already Connected",
            "segments": None, "maxsymbols": 0,
        }))
        self.assertIn("already-connected", [e for e, _ in self.states])

    def test_the_vendors_own_symbol_limit_wins_over_the_configured_one(self):
        self.feed.max_symbols = 50
        self.feed._on_message(json.dumps({
            "success": True, "message": "TrueData Real Time Data Service",
            "segments": ["EQ"], "maxsymbols": 250, "subscription": "tick",
        }))
        self.assertEqual(250, self.feed.max_symbols)

    def test_a_price_for_something_we_never_asked_for_is_dropped(self):
        # No subscription, so no id is known. Storing it under a guessed symbol
        # is the one outcome worse than losing it.
        self.feed._on_message(json.dumps(TRADE))
        self.assertEqual([], self.ticks)

    def test_a_heartbeat_is_reported_and_is_not_a_price(self):
        self.feed._on_message(json.dumps({
            "success": True, "message": "HeartBeat", "timestamp": "2026-09-11T16:19:35.203",
        }))
        self.assertEqual([], self.ticks)
        self.assertIn("heartbeat", [e for e, _ in self.states])


class TrueDataSubscribeTests(unittest.TestCase):
    """Subscribing is where a symbol limit and an underivable name are handled."""

    def setUp(self):
        self.sent = []
        self.states = []
        self.feed = TrueDataFeed("u", "p", max_symbols=3)
        self.feed._send = self.sent.append
        self.feed._on_state = lambda event, detail="": self.states.append((event, detail))

    def test_only_names_it_can_derive_are_asked_for(self):
        self.feed.subscribe([
            "NSE:NIFTY50-INDEX",          # derivable
            "MCX:CRUDEOIL26SEPFUT",       # a future: needs the master
            "NSE:BANKNIFTY26SEP57500CE",  # monthly: carries no expiry day
        ])
        self.assertEqual(1, len(self.sent))
        self.assertEqual(["NIFTY 50"], self.sent[0]["symbols"])

    def test_the_symbol_limit_is_respected_rather_than_discovered(self):
        self.feed.subscribe(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX",
                             "NSE:RELIANCE-EQ", "NSE:SBIN-EQ"])
        self.assertEqual(3, len(self.sent[0]["symbols"]))
        self.assertIn("limit", [e for e, _ in self.states])

    def test_unsubscribe_speaks_the_vendors_names(self):
        self.feed.subscribe(["NSE:NIFTY50-INDEX"])
        self.sent.clear()
        self.feed.unsubscribe(["NSE:NIFTY50-INDEX"])
        self.assertEqual("removesymbol", self.sent[0]["method"])
        self.assertEqual(["NIFTY 50"], self.sent[0]["symbols"])


if __name__ == "__main__":
    unittest.main()


class TrueDataGivenNamesTests(unittest.TestCase):
    """A name read from the master wins over the grammar, which declines monthlies."""

    def test_a_monthly_option_is_subscribed_by_the_name_it_was_given(self):
        sent = []
        feed = TrueDataFeed("u", "p", vendor_names={
            "NSE:BANKNIFTY26SEP56400CE": "BANKNIFTY26092956400CE",
        })
        feed._send = sent.append
        taken = feed.subscribe(["NSE:BANKNIFTY26SEP56400CE"])
        self.assertEqual(["NSE:BANKNIFTY26SEP56400CE"], taken)
        self.assertEqual(["BANKNIFTY26092956400CE"], sent[0]["symbols"])

    def test_without_a_given_name_the_monthly_is_still_declined(self):
        sent, states = [], []
        feed = TrueDataFeed("u", "p")
        feed._send = sent.append
        feed._on_state = lambda e, d="": states.append(e)
        self.assertEqual([], feed.subscribe(["NSE:BANKNIFTY26SEP56400CE"]))
        self.assertEqual([], sent)
        self.assertIn("skipped", states)


class SymbolListParsingTests(unittest.TestCase):
    """TRUEDATA_SYMBOLS, as the recap sets it on the server."""

    def test_plain_and_named_entries(self):
        from market_data.live.truedata_streamer import parse_symbol_list
        symbols, names = parse_symbol_list(
            "NSE:NIFTY50-INDEX, NSE:BANKNIFTY26SEP56400CE=BANKNIFTY26092956400CE ,,")
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE"], symbols)
        self.assertEqual({"NSE:BANKNIFTY26SEP56400CE": "BANKNIFTY26092956400CE"}, names)

    def test_nothing_set_is_the_recording_list(self):
        from market_data.live.truedata_streamer import parse_symbol_list
        self.assertEqual(([], {}), parse_symbol_list(None))
        self.assertEqual(([], {}), parse_symbol_list(""))


class MainStartsTests(unittest.TestCase):
    """main() must get as far as connecting — the check that would have caught the UnboundLocalError."""

    def test_main_builds_the_feed_and_runner_from_the_environment(self):
        import os
        from unittest import mock
        import market_data.live.truedata_streamer as streamer

        env = {
            "TRUEDATA_USERNAME": "u", "TRUEDATA_PASSWORD": "p",
            "TRUEDATA_HOST": "replay.truedata.in", "TRUEDATA_REALTIME_PORT": "8082",
            "TRUEDATA_SYMBOLS": "NSE:NIFTY50-INDEX,NSE:BANKNIFTY26SEP56400CE=BANKNIFTY26092956400CE",
        }
        built = {}

        class FakeRunner:
            def __init__(self, feed, source_name=None, fixed_symbols=None, publisher=None, **_):
                built.update(feed=feed, source=source_name, fixed=fixed_symbols, publisher=publisher)
            def run(self):
                built["ran"] = True
            def stop(self):
                pass

        fake_publisher = mock.MagicMock()
        with mock.patch.dict(os.environ, env, clear=False), \
             mock.patch("messaging.redis_publisher.build_publisher_from_env", return_value=fake_publisher), \
             mock.patch("core.live.feed_runner.FeedRunner", FakeRunner), \
             mock.patch("core.safe_output.install_safe_stdio"), \
             mock.patch("signal.signal"):
            streamer.main()

        self.assertTrue(built.get("ran"))
        # Without this the strategies never hear a single price.
        self.assertIs(fake_publisher, built["publisher"])
        fake_publisher.ensure_connection.assert_called_once()
        self.assertEqual("python-truedata-recap", built["source"])
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE"], built["fixed"])
        self.assertIn("replay.truedata.in:8082", built["feed"]._url)
        self.assertEqual("BANKNIFTY26092956400CE", built["feed"].to_vendor("NSE:BANKNIFTY26SEP56400CE"))


class FeedRunnerPublishTests(unittest.TestCase):
    """A tick must reach the strategy stream as well as the tables."""

    def test_every_tick_is_published_and_stored(self):
        from unittest import mock
        from core.live.feed_runner import FeedRunner

        publisher = mock.MagicMock()
        runner = FeedRunner(mock.MagicMock(key="truedata"), http=mock.MagicMock(), publisher=publisher)
        tick = {"symbol": "NSE:NIFTY50-INDEX", "lastTradedPrice": 23400.0,
                "exchangeTimestampUtc": "2026-09-11T03:45:00Z"}
        runner._on_tick(tick)

        publisher.publish_tick.assert_called_once_with(tick)
        self.assertEqual(1, runner._pump.depth())

    def test_a_redis_failure_does_not_stop_the_tick_being_stored(self):
        from unittest import mock
        from core.live.feed_runner import FeedRunner

        publisher = mock.MagicMock()
        publisher.publish_tick.side_effect = ConnectionError("redis down")
        runner = FeedRunner(mock.MagicMock(key="truedata"), http=mock.MagicMock(), publisher=publisher)
        runner._on_tick({"symbol": "X", "lastTradedPrice": 1.0})

        self.assertEqual(1, runner._pump.depth())
        self.assertEqual(1, runner._publish_errors)
