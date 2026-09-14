"""
The Dhan adapter: packets, merging, disconnect codes, subscribe requests,
symbol resolution, trade-time encoding and configuration.

No packet here was captured from Dhan: every one is built with struct.pack from
the layouts in Dhan's v2 documentation and SDK, so these tests pin the reading
of those layouts, not Dhan's behaviour. What the live test must still settle —
the trade-time encoding, the index packet, whether frames stack — is named in
the adapter where it is handled.
"""

import importlib
import json
import os
import struct
import unittest
from datetime import datetime, timezone
from unittest import mock

import _bootstrap  # noqa: F401

from core.live.vendor_feed import FeedEvent
from market_data.live.vendors import dhan
from market_data.live.vendors.dhan import (DhanFeed, DhanInstrument, ltt_encodes_ist, ltt_to_utc,
                                           parse_instrument)

NOW = datetime(2026, 9, 14, 5, 0, 0, tzinfo=timezone.utc)          # 10:30 IST
TRADE_UTC = int(datetime(2026, 9, 14, 4, 59, 58, tzinfo=timezone.utc).timestamp())
TRADE_IST = TRADE_UTC + 19800

NSE_EQ, NSE_FNO, IDX_I, MCX_COMM = 1, 2, 0, 5

# Obviously fake: nothing real is ever written into a test.
FAKE_CLIENT_ID = "client-id-for-tests"
FAKE_TOKEN = "token-for-tests.not.real"

OPTION = "NSE:NIFTY2691523400CE"
OPTION_ID = 47317
EQUITY = "NSE:RELIANCE-EQ"
EQUITY_ID = 2885
CRUDE = "MCX:CRUDEOIL26SEPFUT"
CRUDE_ID = 450000


def ticker(segment, security_id, ltp, ltt):
    return struct.pack("<BHBIfI", 2, 16, segment, security_id, ltp, ltt)


def quote(segment, security_id, ltp, ltt, volume=1000, day_open=100.0, high=110.0, low=90.0, close=0.0,
          ltq=25, atp=101.5, sell=500, buy=700):
    return struct.pack("<BHBIfHIfIIIffff", 4, 50, segment, security_id, ltp, ltq, ltt, atp, volume,
                       sell, buy, day_open, close, high, low)


def oi(segment, security_id, value):
    return struct.pack("<BHBII", 5, 12, segment, security_id, value)


def prev_close(segment, security_id, price, prev_oi=0):
    return struct.pack("<BHBIfI", 6, 16, segment, security_id, price, prev_oi)


def full(segment, security_id, ltp, ltt, open_interest=6543875, depth=None):
    body = struct.pack("<BHBIfHIfIIIIIIffff", 8, 162, segment, security_id, ltp, 50, ltt, 112.4,
                       387600000, 1241, 1443, open_interest, 6600000, 5900000, 120.0, 0.0, 140.0, 110.0)
    levels = depth or [(425, 300, 3, 2, 134.2, 134.5)] + [(0, 0, 0, 0, 0.0, 0.0)] * 4
    return body + b"".join(struct.pack("<IIHHff", *level) for level in levels)


def index_packet(security_id, value, ltt):
    return struct.pack("<BHBIfffffI", 1, 32, IDX_I, security_id, value, 25000.0, 0.0, 25100.0, 24900.0, ltt)


def disconnect(reason):
    return struct.pack("<BHBIH", 50, 10, 0, 0, reason)


def _feed(max_symbols=5000, vendor_names=None, http=None, now=NOW):
    ticks, events, sent = [], [], []
    names = {OPTION: f"NSE_FNO:{OPTION_ID}:OPTIDX", EQUITY: f"NSE_EQ:{EQUITY_ID}:EQUITY",
             CRUDE: f"MCX_COMM:{CRUDE_ID}:FUTCOM"}
    names.update(vendor_names or {})
    feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, max_symbols=max_symbols, vendor_names=names,
                    http=http or mock.MagicMock(), now=lambda: now)
    feed._on_ticks = ticks.extend
    feed._on_event = lambda e, d="": events.append((e, d))
    feed._send = lambda message: sent.append(message) or True
    return feed, ticks, events, sent


class LttEncodingTests(unittest.TestCase):
    def test_a_true_utc_trade_time_is_taken_as_it_is(self):
        self.assertFalse(ltt_encodes_ist(TRADE_UTC, NOW))
        self.assertEqual("2026-09-14T04:59:58Z", ltt_to_utc(TRADE_UTC, NOW))

    def test_an_ist_wall_clock_trade_time_loses_its_five_thirty(self):
        self.assertTrue(ltt_encodes_ist(TRADE_IST, NOW))
        self.assertEqual("2026-09-14T04:59:58Z", ltt_to_utc(TRADE_IST, NOW))

    def test_a_stamp_far_in_the_future_is_not_proof_of_ist(self):
        self.assertFalse(ltt_encodes_ist(TRADE_UTC + 9 * 3600, NOW))

    def test_zero_is_no_time(self):
        self.assertIsNone(ltt_to_utc(0, NOW))

    def test_the_feed_remembers_the_proof_for_stamps_too_old_to_carry_it(self):
        feed, ticks, events, _ = _feed()
        feed.subscribe([OPTION])
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_IST))
        two_hours_old = TRADE_IST - 2 * 3600
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 130.0, two_hours_old))
        self.assertEqual("2026-09-14T04:59:58Z", ticks[0]["exchangeTimestampUtc"])
        self.assertEqual("2026-09-14T02:59:58Z", ticks[1]["exchangeTimestampUtc"])
        self.assertEqual(1, sum(1 for e, _ in events if e == FeedEvent.INFO and "IST" in _))


class PacketTests(unittest.TestCase):
    def setUp(self):
        self.feed, self.ticks, self.events, _ = _feed()
        self.feed.subscribe([OPTION, EQUITY, CRUDE, "NSE:NIFTY50-INDEX"])

    def test_a_ticker_packet_is_a_price_and_a_time(self):
        self.feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        tick = self.ticks[0]
        self.assertEqual(OPTION, tick["symbol"])
        self.assertEqual("symbolUpdate", tick["dataType"])
        self.assertEqual(134.25, tick["lastTradedPrice"])
        self.assertEqual("2026-09-14T04:59:58Z", tick["exchangeTimestampUtc"])
        self.assertIsNone(tick["bidPrice"])
        self.assertEqual({"type": "ticker"}, json.loads(tick["rawPayload"]))

    def test_a_quote_packet_carries_the_day_and_no_book(self):
        self.feed._on_message(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC, volume=1234567,
                                    day_open=2900.0, high=2920.4, low=2890.15))
        tick = self.ticks[0]
        self.assertEqual(EQUITY, tick["symbol"])
        self.assertEqual(2913.05, tick["lastTradedPrice"], "float32 noise is rounded away")
        self.assertEqual(1234567, tick["volume"])
        self.assertEqual((2900.0, 2920.4, 2890.15), (tick["open"], tick["high"], tick["low"]))
        self.assertIsNone(tick["bidPrice"])
        self.assertIsNone(tick["askSize"])
        self.assertIsNone(tick["openInterest"])
        raw = json.loads(tick["rawPayload"])
        self.assertEqual({"type": "quote", "ltq": 25, "atp": 101.5, "tot_buy_qty": 700, "tot_sell_qty": 500}, raw)
        self.assertNotIn("close", raw, "a zero close is no close")

    def test_oi_and_previous_close_are_merged_into_later_ticks(self):
        self.feed._on_message(prev_close(NSE_FNO, OPTION_ID, 128.0, prev_oi=5900000))
        self.feed._on_message(oi(NSE_FNO, OPTION_ID, 6543875))
        self.assertEqual([], self.ticks, "no price known yet, so nothing to emit")

        self.feed._on_message(quote(NSE_FNO, OPTION_ID, 133.6, TRADE_UTC))
        self.assertEqual(128.0, self.ticks[-1]["prevClose"])
        self.assertEqual(6543875, self.ticks[-1]["openInterest"])

        self.feed._on_message(oi(NSE_FNO, OPTION_ID, 6550000))
        tick = self.ticks[-1]
        self.assertEqual(6550000, tick["openInterest"])
        self.assertEqual(133.6, tick["lastTradedPrice"], "the last known price")
        self.assertEqual("2026-09-14T04:59:58Z", tick["exchangeTimestampUtc"], "the last trade's time")
        self.assertEqual({"type": "oi"}, json.loads(tick["rawPayload"]))

    def test_a_full_packet_carries_the_book_and_oi(self):
        depth = [(425, 300, 3, 2, 134.2, 134.5), (900, 650, 7, 5, 134.15, 134.55)] + [(0, 0, 0, 0, 0.0, 0.0)] * 3
        self.feed._on_message(full(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC, depth=depth))
        tick = self.ticks[0]
        self.assertEqual((134.2, 134.5, 425, 300),
                         (tick["bidPrice"], tick["askPrice"], tick["bidSize"], tick["askSize"]))
        self.assertEqual(6543875, tick["openInterest"])
        self.assertEqual(387600000, tick["volume"])
        self.assertEqual((120.0, 140.0, 110.0), (tick["open"], tick["high"], tick["low"]))
        raw = json.loads(tick["rawPayload"])
        self.assertEqual([[425, 3, 134.2, 134.5, 2, 300], [900, 7, 134.15, 134.55, 5, 650]], raw["depth"],
                         "empty levels are trimmed, filled ones kept")
        self.assertEqual((6600000, 5900000, 112.4), (raw["oi_high"], raw["oi_low"], raw["atp"]))

    def test_an_emptied_side_of_the_book_is_none_not_the_old_price(self):
        self.feed._on_message(full(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        empty_bid = [(0, 300, 0, 2, 0.0, 134.5)] + [(0, 0, 0, 0, 0.0, 0.0)] * 4
        self.feed._on_message(full(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC, depth=empty_bid))
        self.assertIsNone(self.ticks[-1]["bidPrice"])
        self.assertIsNone(self.ticks[-1]["bidSize"])
        self.assertEqual(134.5, self.ticks[-1]["askPrice"])

    def test_an_index_packet_is_read_as_far_as_it_goes(self):
        self.feed._on_message(index_packet(13, 25012.35, TRADE_UTC))
        tick = self.ticks[0]
        self.assertEqual("NSE:NIFTY50-INDEX", tick["symbol"])
        self.assertEqual(25012.35, tick["lastTradedPrice"])
        self.assertEqual((25000.0, 25100.0, 24900.0), (tick["open"], tick["high"], tick["low"]))
        self.assertEqual("2026-09-14T04:59:58Z", tick["exchangeTimestampUtc"])

        short = struct.pack("<BHBIf", 1, 12, IDX_I, 13, 25020.5)
        self.feed._on_message(short)
        self.assertEqual(25020.5, self.ticks[-1]["lastTradedPrice"])

    def test_a_price_for_something_never_asked_for_is_dropped(self):
        self.feed._on_message(ticker(NSE_FNO, 99999, 10.0, TRADE_UTC))
        self.assertEqual([], self.ticks)

    def test_a_truncated_packet_is_an_error_not_a_price(self):
        self.feed._on_message(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC)[:30])
        self.assertEqual([], self.ticks)
        self.assertEqual(FeedEvent.ERROR, self.events[-1][0])

    def test_market_status_and_unknown_codes_are_said_once(self):
        status = struct.pack("<BHBI", 7, 8, 0, 0)
        self.feed._on_message(status)
        self.feed._on_message(status)
        unknown = struct.pack("<BHBI", 99, 8, 0, 0)
        self.feed._on_message(unknown)
        self.feed._on_message(unknown)
        self.assertEqual([], self.ticks)
        self.assertEqual(1, sum(1 for e, d in self.events if e == FeedEvent.INFO and "market status" in d))
        self.assertEqual(1, sum(1 for e, d in self.events if e == FeedEvent.ERROR and "99" in d))


class StackedFrameTests(unittest.TestCase):
    def setUp(self):
        self.feed, self.ticks, self.events, _ = _feed()
        self.feed.subscribe([OPTION, EQUITY])

    def test_packets_stacked_in_one_frame_are_all_read(self):
        frame = quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC) + oi(NSE_FNO, OPTION_ID, 10) \
            + ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC)
        self.feed._on_message(frame)
        self.assertEqual([EQUITY, OPTION], [t["symbol"] for t in self.ticks])
        self.assertEqual(10, self.ticks[1]["openInterest"], "the OI packet before it was merged")

    def test_bytes_that_do_not_start_a_packet_are_ignored_and_said_once(self):
        frame = ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC) + b"\xff" * 12
        self.feed._on_message(frame)
        self.feed._on_message(frame)
        self.assertEqual(2, len(self.ticks))
        self.assertEqual(1, sum(1 for e, d in self.events if e == FeedEvent.INFO and "not another packet" in d))


class DisconnectTests(unittest.TestCase):
    def _event_for(self, reason):
        feed, _, events, _ = _feed()
        feed._on_message(disconnect(reason))
        return events[-1]

    def test_limits_and_lapsed_data_plans_are_refusals(self):
        self.assertEqual(FeedEvent.REFUSED, self._event_for(805)[0])
        event, detail = self._event_for(806)
        self.assertEqual(FeedEvent.REFUSED, event)
        self.assertIn("Data APIs are not subscribed", detail)

    def test_a_bad_login_is_a_rejected_credential(self):
        for reason in (807, 808, 809, 810):
            self.assertEqual(FeedEvent.CREDENTIALS_REJECTED, self._event_for(reason)[0], reason)

    def test_an_unknown_reason_is_an_error_that_names_the_code(self):
        event, detail = self._event_for(899)
        self.assertEqual(FeedEvent.ERROR, event)
        self.assertIn("899", detail)


class SubscribeTests(unittest.TestCase):
    def test_requests_carry_at_most_100_instruments_named_by_segment_and_string_id(self):
        names = {f"NSE:OPT{i}": f"NSE_FNO:{40000 + i}:OPTIDX" for i in range(250)}
        feed, _, _, sent = _feed(vendor_names=names)
        taken = feed.subscribe(list(names))
        self.assertEqual(250, len(taken))
        self.assertEqual([100, 100, 50], [m["InstrumentCount"] for m in sent])
        self.assertEqual([21, 21, 21], [m["RequestCode"] for m in sent])
        self.assertEqual({"ExchangeSegment": "NSE_FNO", "SecurityId": "40000"}, sent[0]["InstrumentList"][0])
        self.assertEqual(50, len(sent[2]["InstrumentList"]))

    def test_the_mode_follows_the_segment(self):
        feed, _, _, sent = _feed()
        feed.subscribe(["NSE:NIFTY50-INDEX", EQUITY, OPTION, CRUDE])
        by_code = {m["RequestCode"]: [i["ExchangeSegment"] for i in m["InstrumentList"]] for m in sent}
        self.assertEqual({17: ["IDX_I", "NSE_EQ"], 21: ["NSE_FNO", "MCX_COMM"]}, by_code)

    def test_unsubscribe_is_the_request_code_plus_one(self):
        feed, ticks, _, sent = _feed()
        feed.subscribe(["NSE:NIFTY50-INDEX", OPTION])
        sent.clear()
        self.assertTrue(feed.unsubscribe(["NSE:NIFTY50-INDEX", OPTION, "NSE:NEVER-SUBSCRIBED"]))
        self.assertEqual({18: [{"ExchangeSegment": "IDX_I", "SecurityId": "13"}],
                          22: [{"ExchangeSegment": "NSE_FNO", "SecurityId": str(OPTION_ID)}]},
                         {m["RequestCode"]: m["InstrumentList"] for m in sent})
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual([], ticks, "an unsubscribed instrument's late packets are not stored")

    def test_the_limit_is_respected_and_the_left_out_are_named(self):
        feed, _, events, _ = _feed(max_symbols=2)
        taken = feed.subscribe(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", EQUITY])
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX"], taken)
        self.assertIn(EQUITY, events[-1][1])

    def test_nothing_is_taken_when_the_request_cannot_be_sent(self):
        feed, ticks, _, _ = _feed()
        feed._send = lambda message: False
        self.assertEqual([], feed.subscribe([OPTION]))
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual([], ticks)

    def test_two_names_for_one_instrument_take_only_the_first(self):
        feed, _, events, _ = _feed(vendor_names={"NSE:ALIAS": f"NSE_FNO:{OPTION_ID}:OPTIDX"})
        self.assertEqual([OPTION], feed.subscribe([OPTION, "NSE:ALIAS"]))
        self.assertIn("NSE:ALIAS", events[-1][1])


class ResolutionTests(unittest.TestCase):
    def _http(self, resolved=None, unresolved=None):
        http = mock.MagicMock()
        http.post.return_value.json.return_value = {"resolved": resolved or {}, "unresolved": unresolved or []}
        return http

    def test_the_index_table_and_given_names_never_ask_the_api(self):
        http = self._http()
        feed, _, _, _ = _feed(http=http)
        self.assertEqual(["NSE:NIFTY50-INDEX", OPTION], feed.subscribe(["NSE:NIFTY50-INDEX", OPTION]))
        http.post.assert_not_called()

    def test_the_index_table_comes_before_a_given_name(self):
        feed, _, _, sent = _feed(vendor_names={"NSE:NIFTY50-INDEX": "IDX_I:26000:INDEX"})
        feed.subscribe(["NSE:NIFTY50-INDEX"])
        self.assertEqual("13", sent[0]["InstrumentList"][0]["SecurityId"])

    def test_the_api_names_the_rest_and_the_unresolved_are_not_taken(self):
        http = self._http(resolved={"NSE:BANKNIFTY26SEP56400CE": "NSE_FNO:52000:OPTIDX"},
                          unresolved=["NSE:NOSUCH-EQ"])
        feed, ticks, events, sent = _feed(http=http)
        taken = feed.subscribe(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE", "NSE:NOSUCH-EQ"])

        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:BANKNIFTY26SEP56400CE"], taken)
        url = http.post.call_args.args[0]
        self.assertTrue(url.endswith("/api/Dhan/instruments/resolve"), url)
        self.assertEqual({"symbols": ["NSE:BANKNIFTY26SEP56400CE", "NSE:NOSUCH-EQ"]},
                         http.post.call_args.kwargs["json"])
        self.assertEqual(FeedEvent.INFO, events[-1][0])
        self.assertIn("NSE:NOSUCH-EQ", events[-1][1])
        self.assertEqual("NSE_FNO:52000:OPTIDX", feed.to_vendor("NSE:BANKNIFTY26SEP56400CE"))

        feed._on_message(ticker(NSE_FNO, 52000, 210.5, TRADE_UTC))
        self.assertEqual("NSE:BANKNIFTY26SEP56400CE", ticks[-1]["symbol"])

    def test_answers_are_cached_and_the_unresolved_are_reported_once(self):
        http = self._http(resolved={"NSE:BANKNIFTY26SEP56400CE": "NSE_FNO:52000:OPTIDX"},
                          unresolved=["NSE:NOSUCH-EQ"])
        feed, _, events, _ = _feed(http=http)
        feed.subscribe(["NSE:BANKNIFTY26SEP56400CE", "NSE:NOSUCH-EQ"])
        feed.close()
        feed.subscribe(["NSE:BANKNIFTY26SEP56400CE", "NSE:NOSUCH-EQ"])
        self.assertEqual({"symbols": ["NSE:NOSUCH-EQ"]}, http.post.call_args.kwargs["json"],
                         "the resolved symbol is not asked again; the unresolved one is")
        self.assertEqual(1, sum(1 for e, d in events if "NSE:NOSUCH-EQ" in d))

    def test_a_malformed_answer_is_unresolved(self):
        http = self._http(resolved={"NSE:ODD-EQ": "NSE_EQ:not-a-number:EQUITY"})
        feed, _, _, _ = _feed(http=http)
        self.assertEqual([], feed.subscribe(["NSE:ODD-EQ"]))

    def test_an_unreachable_api_is_an_error_and_not_a_verdict(self):
        http = mock.MagicMock()
        http.post.side_effect = ConnectionError("api down")
        feed, _, events, _ = _feed(http=http)
        self.assertEqual([OPTION], feed.subscribe([OPTION, "NSE:BANKNIFTY26SEP56400CE"]))
        self.assertEqual(FeedEvent.ERROR, events[-1][0])
        http.post.side_effect = None
        http.post.return_value.json.return_value = {"resolved": {"NSE:BANKNIFTY26SEP56400CE": "NSE_FNO:52000:OPTIDX"}}
        feed.close()
        self.assertIn("NSE:BANKNIFTY26SEP56400CE", feed.subscribe(["NSE:BANKNIFTY26SEP56400CE"]))

    def test_instrument_names_parse_strictly(self):
        self.assertEqual(DhanInstrument("NSE_FNO", 47317, "OPTIDX"), parse_instrument("NSE_FNO:47317:OPTIDX"))
        for bad in ("NSE_FNO:47317", "NOPE:1:EQUITY", "NSE_EQ:-1:EQUITY", "", None):
            self.assertIsNone(parse_instrument(bad), bad)


class CredentialAndConnectionTests(unittest.TestCase):
    ENV_KEYS = ("DHAN_CLIENT_ID", "DHAN_ACCESS_TOKEN", "DHAN_FEED_URL", "DHAN_SYMBOLS")

    def _env(self, **values):
        # The repo's .env is loaded into os.environ on import: every Dhan key is
        # removed first so a real credential can never reach a test.
        patcher = mock.patch.dict(os.environ, {}, clear=False)
        patcher.start()
        self.addCleanup(patcher.stop)
        for key in self.ENV_KEYS:
            os.environ.pop(key, None)
        os.environ.update(values)

    def test_missing_credentials_fail_by_name_without_echoing_values(self):
        self._env()
        with self.assertRaises(SystemExit) as both:
            DhanFeed.from_env()
        self.assertIn("DHAN_CLIENT_ID and DHAN_ACCESS_TOKEN are not set", str(both.exception))

        self._env(DHAN_CLIENT_ID=FAKE_CLIENT_ID)
        with self.assertRaises(SystemExit) as one:
            DhanFeed.from_env()
        self.assertIn("DHAN_ACCESS_TOKEN is not set", str(one.exception))
        self.assertNotIn(FAKE_CLIENT_ID, str(one.exception))

    def test_from_env_reads_the_feed_url_and_named_symbols(self):
        self._env(DHAN_CLIENT_ID=FAKE_CLIENT_ID, DHAN_ACCESS_TOKEN=FAKE_TOKEN,
                  DHAN_FEED_URL="wss://feed.example/", DHAN_SYMBOLS=f"{OPTION}=NSE_FNO:{OPTION_ID}:OPTIDX")
        feed = DhanFeed.from_env()
        self.assertEqual("wss://feed.example", feed.url)
        self.assertEqual(f"NSE_FNO:{OPTION_ID}:OPTIDX", feed.to_vendor(OPTION))
        self.assertEqual(5000, feed.max_symbols)
        self.assertEqual((FAKE_CLIENT_ID, FAKE_TOKEN), feed.acquire_credentials())

    def test_a_malformed_named_symbol_fails_at_startup(self):
        self._env(DHAN_CLIENT_ID=FAKE_CLIENT_ID, DHAN_ACCESS_TOKEN=FAKE_TOKEN,
                  DHAN_SYMBOLS=f"{OPTION}=NIFTY 23400 CE")
        with self.assertRaises(SystemExit) as failure:
            DhanFeed.from_env()
        self.assertIn(OPTION, str(failure.exception))

    def test_after_a_rejection_it_waits_for_a_different_token(self):
        answers = iter([(FAKE_CLIENT_ID, FAKE_TOKEN), None, (FAKE_CLIENT_ID, "fresh-token-for-tests")])
        sleeps = []
        feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, credentials_source=lambda: next(answers),
                        sleep=sleeps.append)
        with mock.patch("builtins.print"):
            credential = feed.acquire_credentials(not_this=(FAKE_CLIENT_ID, FAKE_TOKEN))
        self.assertEqual((FAKE_CLIENT_ID, "fresh-token-for-tests"), credential)
        self.assertEqual(2, len(sleeps))

    def test_the_socket_url_carries_the_login_and_nothing_logged_does(self):
        feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN)
        events = []
        with mock.patch.object(dhan.websocket, "WebSocketApp") as app, \
             mock.patch.object(dhan.threading, "Thread"):
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), lambda ticks: None, lambda e, d="": events.append((e, d)))
            url = app.call_args.args[0]
            handlers = app.call_args.kwargs

        self.assertTrue(url.startswith("wss://api-feed.dhan.co?"), url)
        for part in ("version=2", f"token={FAKE_TOKEN}", f"clientId={FAKE_CLIENT_ID}", "authType=2"):
            self.assertIn(part, url)

        handlers["on_open"](None)
        handlers["on_error"](None, RuntimeError(f"failed to GET {url}"))
        handshake = RuntimeError("Handshake status 401 Unauthorized")
        handshake.status_code = 401
        handlers["on_error"](None, handshake)
        self.assertEqual([FeedEvent.CONNECTED, FeedEvent.ERROR, FeedEvent.CREDENTIALS_REJECTED],
                         [e for e, _ in events])
        for _, detail in events:
            self.assertNotIn(FAKE_TOKEN, detail)
            self.assertNotIn(FAKE_CLIENT_ID, detail)


class RegistrationTests(unittest.TestCase):
    def test_dhan_is_a_registered_adapter(self):
        from market_data.live.vendors import ADAPTERS
        module_name, class_name = ADAPTERS["dhan"].split(":")
        self.assertIs(DhanFeed, getattr(importlib.import_module(module_name), class_name))
        self.assertEqual("dhan", DhanFeed.key)
        self.assertEqual("feed:dhan:lock", DhanFeed.lock_key)
        self.assertEqual("python-dhan-feed", DhanFeed.source_name)


if __name__ == "__main__":
    unittest.main()
