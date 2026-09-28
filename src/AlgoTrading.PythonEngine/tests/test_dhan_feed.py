"""
The Dhan adapter: packets, merging, conflation, disconnect codes, subscribe
requests, symbol resolution, the universe, trade-time encoding, credentials and
configuration.

No packet here was captured from Dhan: every one is built with struct.pack from
the layouts in Dhan's v2 documentation and SDK, so these tests pin the reading
of those layouts, not Dhan's behaviour. What the live test must still settle —
the trade-time encoding, the index packet, whether frames stack — is named in
the adapter where it is handled.

No test here reaches the network or the repository's .env: every feed that
could ask the API is given a fake one, and every feed that could read .env is
given its own credential source. A real token must never be able to appear in
a failing assertion's diff.
"""

import base64
import importlib
import json
import os
import struct
import threading
import time
import unittest
from datetime import datetime, timedelta, timezone
from unittest import mock

import requests

import _bootstrap  # noqa: F401

from core.live.vendor_feed import FeedEvent
from market_data.live.vendors import dhan
from market_data.live.vendors.dhan import (DhanFeed, DhanInstrument, ltt_encodes_ist, ltt_to_utc,
                                           parse_instrument, token_expiry)

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


def _feed(max_symbols=5000, vendor_names=None, http=None, now=NOW, interval_ms=0, clock=None):
    """
    A feed with no socket. Every packet becomes a tick unless `interval_ms`
    turns conflation on: the packet tests pin decoding, not conflation.
    """
    ticks, events, sent = [], [], []
    names = {OPTION: f"NSE_FNO:{OPTION_ID}:OPTIDX", EQUITY: f"NSE_EQ:{EQUITY_ID}:EQUITY",
             CRUDE: f"MCX_COMM:{CRUDE_ID}:FUTCOM"}
    names.update(vendor_names or {})
    feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, max_symbols=max_symbols, vendor_names=names,
                    http=http or mock.MagicMock(), now=lambda: now, credentials_source=lambda: None,
                    min_tick_interval_ms=interval_ms, clock=clock)
    feed._on_ticks = ticks.extend
    feed._on_event = lambda e, d="": events.append((e, d))
    feed._send = lambda message: sent.append(message) or True
    return feed, ticks, events, sent


def _response(status=200, body=None):
    response = mock.MagicMock()
    response.status_code = status
    response.json.return_value = body
    if status >= 400:
        response.raise_for_status.side_effect = requests.HTTPError(f"{status} from the fake API")
    else:
        response.raise_for_status.return_value = None
    return response


class FakeApi:
    """
    The platform API as the Dhan feed asks it. Each path answers from its own
    list in turn, the last answer repeating; an exception in the list is raised.
    """

    def __init__(self, answers=None):
        self._answers = {path: list(queue) for path, queue in (answers or {}).items()}
        self.asked: list[str] = []

    def get(self, url, **_):
        path = url.split("/api/", 1)[1]
        self.asked.append(path)
        queue = self._answers.get(path)
        if not queue:
            return _response(404, {"message": "no such path in the fake API"})
        answer = queue.pop(0) if len(queue) > 1 else queue[0]
        if isinstance(answer, Exception):
            raise answer
        return answer

    def set(self, path, *answers):
        self._answers[path] = list(answers)


def _session(client_id, token, source="sign-in", expires="2026-09-15T03:30:00Z"):
    return _response(200, {"clientId": client_id, "accessToken": token, "source": source, "expiresUtc": expires})


def _jwt(claims, signature="signature-for-tests"):
    """
    A token shaped like Dhan's — a JWT — built here from `claims`, so no literal
    one sits in the repository for the secret scanner to flag. Unsigned: the
    feed reads the expiry and never checks a signature.
    """
    def part(value):
        return base64.urlsafe_b64encode(json.dumps(value).encode()).rstrip(b"=").decode()
    return ".".join([part({"typ": "JWT", "alg": "HS512"}), part(claims), signature])


class Clock:
    """Monotonic seconds that only move when a test moves them."""

    def __init__(self, t=1000.0):
        self.t = t

    def __call__(self):
        return self.t


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
        self.assertEqual({"type": "ticker", "dhan": f"NSE_FNO:{OPTION_ID}"}, json.loads(tick["rawPayload"]))

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
        self.assertEqual({"type": "quote", "dhan": f"NSE_EQ:{EQUITY_ID}", "ltq": 25, "atp": 101.5, "tot_buy_qty": 700, "tot_sell_qty": 500}, raw)
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
        raw = json.loads(tick["rawPayload"])
        self.assertEqual("oi", raw["type"])
        self.assertEqual((25, 101.5, 5900000), (raw["ltq"], raw["atp"], raw["prev_oi"]),
                         "the raw fields are the instrument's merged state, like the columns")

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

    def test_from_env_no_longer_needs_the_credential(self):
        # The console's Connect puts it in the API; .env is only the fallback.
        self._env()
        with mock.patch.object(dhan, "_credentials_from_env_file", return_value=None):
            feed = DhanFeed.from_env()
        self.assertIsNone(feed._env_credential)
        self.assertEqual(1.0, feed._min_interval, "a second between an instrument's ticks by default")

    def test_from_env_reads_the_feed_url_named_symbols_and_credential(self):
        self._env(DHAN_CLIENT_ID=FAKE_CLIENT_ID, DHAN_ACCESS_TOKEN=FAKE_TOKEN,
                  DHAN_FEED_URL="wss://feed.example/", DHAN_SYMBOLS=f"{OPTION}=NSE_FNO:{OPTION_ID}:OPTIDX")
        api = FakeApi({"Dhan/session": [_response(404, {"message": "Dhan is not signed in"})]})
        with mock.patch.object(dhan, "_credentials_from_env_file", return_value=None), \
             mock.patch.object(dhan, "build_session", return_value=api):
            feed = DhanFeed.from_env()
            with mock.patch("builtins.print"):
                credential = feed.acquire_credentials()
        self.assertEqual("wss://feed.example", feed.url)
        self.assertEqual(f"NSE_FNO:{OPTION_ID}:OPTIDX", feed.to_vendor(OPTION))
        self.assertEqual(5000, feed.max_symbols)
        self.assertEqual((FAKE_CLIENT_ID, FAKE_TOKEN), credential)
        self.assertEqual(["Dhan/session"], api.asked, "the API was asked first")

    def test_the_tick_interval_is_configurable_and_checked(self):
        for value, seconds in (("250", 0.25), ("0", 0.0)):
            self._env(DHAN_MIN_TICK_INTERVAL_MS=value)
            self.assertEqual(seconds, DhanFeed.from_env()._min_interval, value)
        for bad in ("fast", "-5", "1.5"):
            self._env(DHAN_MIN_TICK_INTERVAL_MS=bad)
            with self.assertRaises(SystemExit) as failure:
                DhanFeed.from_env()
            self.assertIn("DHAN_MIN_TICK_INTERVAL_MS", str(failure.exception))

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
                        sleep=sleeps.append, http=FakeApi())
        with mock.patch("builtins.print"):
            credential = feed.acquire_credentials(not_this=(FAKE_CLIENT_ID, FAKE_TOKEN))
        self.assertEqual((FAKE_CLIENT_ID, "fresh-token-for-tests"), credential)
        self.assertEqual([30, 30], sleeps)

    def test_the_socket_url_carries_the_login_and_nothing_logged_does(self):
        feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, http=FakeApi(), credentials_source=lambda: None)
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


class ApiCredentialTests(unittest.TestCase):
    """The token from the console's daily Connect lives in the API, not in .env."""

    API_CLIENT = "api-client-for-tests"
    API_TOKEN = "api-token-for-tests.not.real"
    ENV_CREDENTIAL = (FAKE_CLIENT_ID, FAKE_TOKEN)

    def _feed(self, api, env_file=None, started_with=(None, None)):
        self.sleeps, self.printed = [], []
        feed = DhanFeed(*started_with, http=api, credentials_source=env_file or (lambda: None),
                        sleep=self.sleeps.append)
        patcher = mock.patch("builtins.print", side_effect=lambda *a, **_: self.printed.append(" ".join(map(str, a))))
        patcher.start()
        self.addCleanup(patcher.stop)
        return feed

    def _assert_no_secret_printed(self, *secrets):
        for line in self.printed:
            for secret in secrets:
                self.assertNotIn(secret, line)

    def test_the_api_session_comes_first(self):
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, self.API_TOKEN)]})
        feed = self._feed(api, env_file=lambda: self.ENV_CREDENTIAL, started_with=self.ENV_CREDENTIAL)
        self.assertEqual((self.API_CLIENT, self.API_TOKEN), feed.acquire_credentials())
        self.assertEqual(1, len(self.printed))
        self.assertIn("the API (sign-in, expires 2026-09-15T03:30:00Z)", self.printed[0])
        self._assert_no_secret_printed(self.API_TOKEN, self.API_CLIENT, FAKE_TOKEN)

    def test_env_is_the_fallback_when_the_api_cannot_give_one(self):
        for answer in (ConnectionError("api down"),
                       _response(404, {"message": "Dhan is not signed in"}),
                       _response(500, {"message": "boom"}),
                       _response(200, {"clientId": self.API_CLIENT, "accessToken": ""})):
            api = FakeApi({"Dhan/session": [answer]})
            feed = self._feed(api, env_file=lambda: self.ENV_CREDENTIAL)
            self.assertEqual(self.ENV_CREDENTIAL, feed.acquire_credentials(), answer)
            self.assertIn(".env", self.printed[-1])
            self.assertEqual([], self.sleeps)

    def test_the_environment_it_started_with_is_the_last_resort(self):
        api = FakeApi({"Dhan/session": [ConnectionError("api down")]})
        feed = self._feed(api, started_with=self.ENV_CREDENTIAL)
        self.assertEqual(self.ENV_CREDENTIAL, feed.acquire_credentials())

    def test_a_rejected_token_waits_for_a_different_one_from_the_api(self):
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, self.API_TOKEN),
                                        _session(self.API_CLIENT, self.API_TOKEN),
                                        _session(self.API_CLIENT, "reconnected-token-for-tests")]})
        feed = self._feed(api)
        credential = feed.acquire_credentials(not_this=(self.API_CLIENT, self.API_TOKEN))
        self.assertEqual((self.API_CLIENT, "reconnected-token-for-tests"), credential)
        self.assertEqual([30, 30], self.sleeps, "polled every 30 s until the token changed")
        waiting = [line for line in self.printed if "press Connect" in line]
        self.assertEqual(1, len(waiting), "one line while waiting, not one per poll")
        self._assert_no_secret_printed(self.API_TOKEN, "reconnected-token-for-tests", self.API_CLIENT)

    def test_a_refused_token_is_never_taken_again_from_any_source(self):
        # Yesterday's token in .env and a stale one in the API: remembering only
        # the latest refusal would swap between the two dead ones forever.
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, self.API_TOKEN)]})
        feed = self._feed(api, env_file=lambda: self.ENV_CREDENTIAL)
        self.assertEqual(self.ENV_CREDENTIAL, feed.acquire_credentials(not_this=(self.API_CLIENT, self.API_TOKEN)))

        api.set("Dhan/session", _session(self.API_CLIENT, self.API_TOKEN), _session(self.API_CLIENT, self.API_TOKEN),
                _session(self.API_CLIENT, "fresh-token-for-tests"))
        credential = feed.acquire_credentials(not_this=self.ENV_CREDENTIAL)
        self.assertEqual((self.API_CLIENT, "fresh-token-for-tests"), credential)
        self.assertEqual(2, len(self.sleeps))

    def test_with_no_credential_anywhere_it_waits_instead_of_exiting(self):
        api = FakeApi({"Dhan/session": [_response(404, {"message": "Dhan is not signed in"}),
                                        _session(self.API_CLIENT, self.API_TOKEN)]})
        feed = self._feed(api)
        self.assertEqual((self.API_CLIENT, self.API_TOKEN), feed.acquire_credentials())
        self.assertEqual([30], self.sleeps)
        self.assertIn("the API has no Dhan session", self.printed[0])

    def test_a_refused_token_is_redacted_from_later_errors_too(self):
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, "fresh-token-for-tests")]})
        feed = self._feed(api)
        feed.acquire_credentials(not_this=(self.API_CLIENT, self.API_TOKEN))
        redacted = feed._redact(f"GET wss://x?token={self.API_TOKEN}&token=fresh-token-for-tests")
        self.assertNotIn(self.API_TOKEN, redacted)
        self.assertNotIn("fresh-token-for-tests", redacted)


class ExpiredTokenTests(unittest.TestCase):
    """
    Dhan accepts the socket from an expired token and drops it without a
    reason, so no refusal is ever recorded: on 16 Sep the feed reconnected 238
    times on one, until Dhan blocked the client id. The token's own expiry is
    read instead, a dead one is passed over from every source, and why is said.
    """

    # 08:45 IST on 16 Sep, when the morning job starts the feed.
    NOW = datetime(2026, 9, 16, 3, 15, tzinfo=timezone.utc)
    API_CLIENT = "api-client-for-tests"
    # The token the server's .env still held on 27 Sep: expired 15 Sep 15:20 IST.
    DEAD = _jwt({"exp": int(datetime(2026, 9, 15, 9, 50, 51, tzinfo=timezone.utc).timestamp())},
                signature="dead-signature-for-tests")
    FRESH = _jwt({"exp": int(datetime(2026, 9, 16, 23, 15, tzinfo=timezone.utc).timestamp())},
                 signature="fresh-signature-for-tests")

    def _feed(self, api, env_file=None, started_with=(None, None)):
        self.sleeps, self.printed = [], []
        feed = DhanFeed(*started_with, http=api, credentials_source=env_file or (lambda: None),
                        sleep=self.sleeps.append, now=lambda: self.NOW)
        patcher = mock.patch("builtins.print", side_effect=lambda *a, **_: self.printed.append(" ".join(map(str, a))))
        patcher.start()
        self.addCleanup(patcher.stop)
        return feed

    def _assert_no_secret_printed(self, *secrets):
        for line in self.printed:
            for secret in secrets:
                self.assertNotIn(secret, line)

    def test_expired_env_token_is_skipped_with_reason(self):
        api = FakeApi({"Dhan/session": [_response(404, {"message": "Dhan is not signed in."}),
                                        _session(self.API_CLIENT, self.FRESH)]})
        feed = self._feed(api, env_file=lambda: (FAKE_CLIENT_ID, self.DEAD))
        self.assertEqual((self.API_CLIENT, self.FRESH), feed.acquire_credentials())
        self.assertEqual([30], self.sleeps, "it waited for a sign-in rather than hand back the dead token")
        waiting = [line for line in self.printed if "press Connect" in line]
        self.assertEqual(1, len(waiting))
        self.assertIn("the token from .env expired 15 Sep 15:20 IST", waiting[0])
        self._assert_no_secret_printed(self.DEAD, self.FRESH, FAKE_CLIENT_ID, self.API_CLIENT)

    def test_valid_api_token_preferred_over_expired_env(self):
        env_asked = []
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, self.FRESH, expires="2026-09-16T23:15:00Z")]})
        feed = self._feed(api, env_file=lambda: env_asked.append(".env") or (FAKE_CLIENT_ID, self.DEAD),
                          started_with=(FAKE_CLIENT_ID, self.DEAD))
        self.assertEqual((self.API_CLIENT, self.FRESH), feed.acquire_credentials(),
                         "a JWT whose expiry is hours away is used")
        self.assertEqual([], self.sleeps)
        self.assertEqual([], env_asked, ".env is not read while the API has a live token")
        self.assertEqual(["[dhan] using the Dhan credential from the API (sign-in, expires 2026-09-16T23:15:00Z)."],
                         self.printed, "nothing was passed over, so no reasons")

    def test_all_expired_waits_for_connect_without_connecting(self):
        from _feed_fakes import runner_for
        # The API's sign-in is 30 s from its end: inside the margin, so as dead
        # as the other two.
        closing = _jwt({"exp": int((self.NOW + timedelta(seconds=30)).timestamp())},
                       signature="closing-signature-for-tests")
        api = FakeApi({"Dhan/session": [_session(self.API_CLIENT, closing, expires="2026-09-16T03:15:30Z"),
                                        _session(self.API_CLIENT, closing, expires="2026-09-16T03:15:30Z"),
                                        _session(self.API_CLIENT, self.FRESH)]})
        feed = self._feed(api, env_file=lambda: (FAKE_CLIENT_ID, self.DEAD), started_with=(FAKE_CLIENT_ID, self.DEAD))
        feed.connect = mock.MagicMock()
        runner = runner_for(feed)
        runner._stop.set()          # one connect, then no watching

        runner._connect_once_and_watch()

        feed.connect.assert_called_once()
        self.assertEqual((self.API_CLIENT, self.FRESH), feed.connect.call_args.args[0],
                         "the only connect is on the token Connect brought")
        self.assertEqual([30, 30], self.sleeps, "the API was asked every 30 s meanwhile")
        self.assertEqual(["Dhan/session"] * 3, api.asked)
        waiting = [line for line in self.printed if "no usable Dhan credential" in line]
        self.assertEqual(1, len(waiting), "said once, not once per poll")
        for why in ("the token from the API (sign-in, expires 2026-09-16T03:15:30Z) expires 16 Sep 08:45 IST",
                    "the token from .env expired 15 Sep 15:20 IST",
                    "the token from the environment the feed started with expired 15 Sep 15:20 IST"):
            self.assertIn(why, waiting[0])
        self._assert_no_secret_printed(closing, self.DEAD, self.FRESH, FAKE_CLIENT_ID, self.API_CLIENT)

    def test_non_jwt_token_used_as_before(self):
        # Not a JWT, a JWT with no expiry or an unreadable one: offered exactly
        # as before this check existed, and Dhan is the judge.
        for token in (FAKE_TOKEN, "plain-token-for-tests", "a.b!@#.c", _jwt({"sub": "no-expiry"}),
                      _jwt({"exp": "tomorrow"}), _jwt({"exp": 10 ** 20}), _jwt(["not", "an", "object"])):
            self.assertIsNone(token_expiry(token), token)
            api = FakeApi({"Dhan/session": [ConnectionError("api down")]})
            feed = self._feed(api, env_file=lambda: (FAKE_CLIENT_ID, token))
            self.assertEqual((FAKE_CLIENT_ID, token), feed.acquire_credentials(), token)
            self.assertEqual([], self.sleeps, token)
        self.assertEqual(datetime(2026, 9, 15, 9, 50, 51, tzinfo=timezone.utc), token_expiry(self.DEAD))

    def test_fallback_logs_skip_reasons(self):
        said = ("Dhan is not signed in. Press Connect on the Dhan connector page (once a day), or set "
                "Dhan:AccessToken. The token in the configuration (DHAN_ACCESS_TOKEN) expired on 15 Sep 15:20 IST.")
        api = FakeApi({"Dhan/session": [_response(404, {"message": said})]})
        feed = self._feed(api, env_file=lambda: (FAKE_CLIENT_ID, self.DEAD), started_with=(FAKE_CLIENT_ID, self.FRESH))
        self.assertEqual((FAKE_CLIENT_ID, self.FRESH), feed.acquire_credentials())
        self.assertEqual([f"[dhan] the API has no Dhan session (404: {said}); the token from .env expired "
                          f"15 Sep 15:20 IST — using the Dhan credential from the environment the feed started with."],
                         self.printed)
        self._assert_no_secret_printed(self.DEAD, self.FRESH, FAKE_CLIENT_ID)


class ConflationTests(unittest.TestCase):
    """Full mode sends ~10 packets a second per instrument; one tick a second leaves."""

    def setUp(self):
        self.clock = Clock()
        self.feed, self.ticks, self.events, _ = _feed(interval_ms=1000, clock=self.clock)
        self.feed.subscribe([OPTION, EQUITY])

    def _at(self, seconds, packet):
        self.clock.t = 1000.0 + seconds
        self.feed._on_message(packet)

    def test_a_burst_collapses_to_one_tick_per_interval_carrying_the_latest_state(self):
        for i in range(10):
            depth = [(400 + i, 300 + i, 3, 2, 134.0 + i / 10, 134.5 + i / 10)] + [(0, 0, 0, 0, 0.0, 0.0)] * 4
            self._at(i / 10, full(NSE_FNO, OPTION_ID, 134.25 + i / 10, TRADE_UTC + i, open_interest=6500000 + i,
                                  depth=depth))
        self.assertEqual(1, len(self.ticks), "the first update goes out at once, the rest are held")
        self.assertEqual(134.25, self.ticks[0]["lastTradedPrice"])

        self.clock.t = 1000.99
        self.assertEqual(0, self.feed.flush_due(), "not before the interval has run out")
        self.clock.t = 1001.0
        self.assertEqual(1, self.feed.flush_due())
        tick = self.ticks[-1]
        self.assertEqual(135.15, tick["lastTradedPrice"])
        self.assertEqual((134.9, 135.4, 409, 309), (tick["bidPrice"], tick["askPrice"], tick["bidSize"], tick["askSize"]))
        self.assertEqual(6500009, tick["openInterest"])
        self.assertEqual("2026-09-14T05:00:07Z", tick["exchangeTimestampUtc"])
        self.assertEqual([[409, 3, 134.9, 135.4, 2, 309]], json.loads(tick["rawPayload"])["depth"])
        self.assertEqual(0, self.feed.flush_due(), "nothing new, nothing sent")

    def test_a_steady_stream_leaves_at_most_one_tick_per_interval(self):
        sent_at = []
        self.feed._on_ticks = lambda ticks: sent_at.extend([self.clock.t] * len(ticks))
        for step in range(35):                     # 3.5 s of a packet every 100 ms
            self._at(step / 10, ticker(NSE_FNO, OPTION_ID, 130.0 + step, TRADE_UTC))
            self.feed.flush_due()
        self.assertEqual(4, len(sent_at))
        gaps = [later - earlier for earlier, later in zip(sent_at, sent_at[1:])]
        self.assertTrue(all(gap >= 0.999 for gap in gaps), gaps)

    def test_a_symbol_that_went_quiet_still_gets_its_last_state_out(self):
        self._at(0.0, ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self._at(0.3, ticker(NSE_FNO, OPTION_ID, 136.0, TRADE_UTC + 1))
        self._at(0.5, quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC))
        self.assertEqual([OPTION, EQUITY], [t["symbol"] for t in self.ticks],
                         "instruments are conflated separately")
        # Nothing more arrives for the option, ever.
        self.clock.t = 1001.0
        self.feed.flush_due()
        self.assertEqual((OPTION, 136.0), (self.ticks[-1]["symbol"], self.ticks[-1]["lastTradedPrice"]))
        self.assertEqual(3, len(self.ticks), "the equity sent once and had nothing held")

    def test_a_packet_without_a_price_arriving_last_keeps_the_book_in_the_tick(self):
        self._at(0.0, full(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self._at(0.2, full(NSE_FNO, OPTION_ID, 134.5, TRADE_UTC + 1))
        self._at(0.4, prev_close(NSE_FNO, OPTION_ID, 128.0, prev_oi=5900000))
        self.clock.t = 1001.0
        self.feed.flush_due()
        tick = self.ticks[-1]
        raw = json.loads(tick["rawPayload"])
        self.assertEqual((134.5, 128.0, 134.2), (tick["lastTradedPrice"], tick["prevClose"], tick["bidPrice"]))
        self.assertEqual(("prev_close", 5900000), (raw["type"], raw["prev_oi"]))
        self.assertIn("depth", raw, "the book from the Full packet before it is still there")

    def test_an_interval_of_zero_passes_every_packet_through(self):
        feed, ticks, _, _ = _feed(interval_ms=0, clock=self.clock)
        feed.subscribe([OPTION])
        for i in range(5):
            feed._on_message(ticker(NSE_FNO, OPTION_ID, 130.0 + i, TRADE_UTC))
        self.assertEqual([130.0, 131.0, 132.0, 133.0, 134.0], [t["lastTradedPrice"] for t in ticks])
        with mock.patch.object(dhan.websocket, "WebSocketApp"), mock.patch.object(dhan.threading, "Thread") as thread:
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), ticks.extend, lambda e, d="": None)
        # The emitter is how a stored packet leaves, so it runs even when
        # nothing is ever held.
        self.assertEqual(["dhan-emit", "dhan-socket"], [c.kwargs["name"] for c in thread.call_args_list])

        inline = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, http=mock.MagicMock(), credentials_source=lambda: None,
                          min_tick_interval_ms=0, ingest="inline")
        with mock.patch.object(dhan.websocket, "WebSocketApp"), mock.patch.object(dhan.threading, "Thread") as thread:
            inline.connect((FAKE_CLIENT_ID, FAKE_TOKEN), ticks.extend, lambda e, d="": None)
        self.assertIsNone(inline._emitter, "inline, nothing is held and nothing stored: no emitter")
        self.assertEqual(["dhan-socket"], [c.kwargs["name"] for c in thread.call_args_list])

    def test_close_hands_on_what_is_held_and_starts_the_next_connection_afresh(self):
        self._at(0.0, ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self._at(0.2, ticker(NSE_FNO, OPTION_ID, 135.0, TRADE_UTC))
        self.feed.close()
        self.assertEqual([134.25, 135.0], [t["lastTradedPrice"] for t in self.ticks])
        self.feed.subscribe([OPTION])
        self._at(0.3, ticker(NSE_FNO, OPTION_ID, 135.5, TRADE_UTC))
        self.assertEqual(135.5, self.ticks[-1]["lastTradedPrice"], "a new connection's first update is not held")


class EmitterThreadTests(unittest.TestCase):
    """The real "dhan-emit" thread, on the real clock, with short periods."""

    def _connected(self, interval_ms, emit_period_ms=dhan.DEFAULT_EMIT_PERIOD_MS, on_ticks=None):
        feed, _, _, _ = _feed(interval_ms=interval_ms)
        feed._emit_period = min(max(emit_period_ms, 10), 1000) / 1000
        received = []
        lock = threading.Lock()

        def record(batch):
            with lock:
                received.extend(batch)

        with mock.patch.object(dhan.websocket, "WebSocketApp"):
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), on_ticks or record, lambda e, d="": None)
        self.addCleanup(feed.close)
        feed.subscribe([OPTION, EQUITY])
        return feed, received

    def _wait_for(self, condition, seconds=3.0):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if condition():
                return True
            time.sleep(0.005)
        return False

    @staticmethod
    def _emitters():
        return [t for t in threading.enumerate() if t.name == "dhan-emit" and t.is_alive()]

    def test_a_stored_packet_is_delivered_within_two_periods(self):
        feed, received = self._connected(interval_ms=1000, emit_period_ms=250)
        stored_at = time.monotonic()
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual([], received, "the socket thread hands nothing on itself")
        self.assertTrue(self._wait_for(lambda: len(received) == 1, seconds=0.5), received)
        self.assertLess(time.monotonic() - stored_at, 0.5, "within two 250 ms periods")
        self.assertEqual(134.25, received[0]["lastTradedPrice"])

    def test_the_emitter_sends_a_quiet_symbols_last_state_on_its_own(self):
        feed, received = self._connected(interval_ms=50, emit_period_ms=10)
        thread, _ = feed._emitter
        self.assertTrue(thread.is_alive())
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 136.0, TRADE_UTC))
        self.assertTrue(self._wait_for(lambda: len(received) == 2), received)
        self.assertEqual(136.0, received[-1]["lastTradedPrice"])

    def test_a_quiet_symbols_stored_state_goes_out_once_it_is_due(self):
        feed, received = self._connected(interval_ms=50, emit_period_ms=10)
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertTrue(self._wait_for(lambda: len(received) == 1), received)
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 136.0, TRADE_UTC))
        self.assertTrue(self._wait_for(lambda: len(received) == 2), received)
        self.assertEqual([134.25, 136.0], [t["lastTradedPrice"] for t in received])

    def test_close_joins_the_emitter_and_hands_on_everything_pending_and_held(self):
        feed, received = self._connected(interval_ms=60000, emit_period_ms=1000)
        thread, stop = feed._emitter
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))       # out at once
        feed._on_message(ticker(NSE_FNO, OPTION_ID, 136.0, TRADE_UTC))        # held for a minute
        feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC))       # stored, no pass yet
        feed.close()
        self.assertFalse(thread.is_alive())
        self.assertTrue(stop.is_set())
        self.assertIsNone(feed._emitter)
        self.assertEqual([(OPTION, 134.25), (EQUITY, 2913.05), (OPTION, 136.0)],
                         [(t["symbol"], t["lastTradedPrice"]) for t in received],
                         "the stored and the held states went out with the close, not a minute later")
        self.assertEqual({}, feed._pending)

    def test_a_reconnect_replaces_the_emitter_rather_than_adding_one(self):
        feed, _ = self._connected(interval_ms=50, emit_period_ms=10)
        first, _ = feed._emitter
        with mock.patch.object(dhan.websocket, "WebSocketApp"):
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), lambda ticks: None, lambda e, d="": None)
        second, _ = feed._emitter
        self.assertFalse(first.is_alive())
        self.assertTrue(second.is_alive())
        self.assertEqual([second], [t for t in self._emitters() if t is first or t is second],
                         "exactly one dhan-emit thread for this feed")

    def test_an_exception_in_on_ticks_does_not_kill_the_emitter(self):
        calls, received = [], []

        def on_ticks(batch):
            calls.append(len(batch))
            if len(calls) == 1:
                raise RuntimeError("the runner could not take them")
            received.extend(batch)

        events = []
        feed, _ = self._connected(interval_ms=0, emit_period_ms=10, on_ticks=on_ticks)
        feed._on_event = lambda e, d="": events.append((e, d))
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertTrue(self._wait_for(lambda: len(calls) == 1))
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 136.0, TRADE_UTC))
        self.assertTrue(self._wait_for(lambda: len(received) == 1), calls)
        thread, _ = feed._emitter
        self.assertTrue(thread.is_alive())
        self.assertEqual(136.0, received[0]["lastTradedPrice"])
        self.assertEqual(1, sum(1 for e, d in events if e == FeedEvent.ERROR and "could not hand on" in d))


class ReaderIsolationTests(unittest.TestCase):
    """
    What the socket thread does per frame, and what it must never do. On 28
    Sep it decoded, conflated and published every frame itself, and waited on
    the lock the flusher held across a Redis write: Dhan's bytes piled up in
    the kernel and the feed went quiet every few minutes.
    """

    def _wide_feed(self, count=300, interval_ms=0):
        names = {f"NSE:OPT{i}": f"NSE_FNO:{40000 + i}:OPTIDX" for i in range(count)}
        feed, ticks, events, _ = _feed(vendor_names=names, interval_ms=interval_ms)
        feed.subscribe(list(names))
        return feed, ticks, events, names

    def test_a_flood_while_delivery_is_blocked_is_stored_fast_and_bounded(self):
        feed, _, _, names = self._wide_feed()
        entered, release = threading.Event(), threading.Event()
        delivered = []

        def slow_on_ticks(batch):
            delivered.extend(batch)
            entered.set()
            release.wait(10)

        feed._on_ticks = slow_on_ticks
        feed._store_frame(ticker(NSE_FNO, 40000, 1.0, TRADE_UTC))
        blocked = threading.Thread(target=feed.emit_pass, daemon=True)
        blocked.start()
        self.assertTrue(entered.wait(5), "the pass is inside on_ticks, holding _emit_lock")
        self.assertTrue(feed._emit_lock.locked())

        # Each round of 300 frames names every instrument once, and the rounds
        # cycle through ticker, quote and full packets.
        last_price, frames = {}, []
        for n in range(20000):
            i, kind = n % 300, (n // 300) % 3
            price = 100.0 + n / 100
            make = (ticker, quote, full)[kind]
            frames.append(make(NSE_FNO, 40000 + i, price, TRADE_UTC))
            last_price[f"NSE:OPT{i}"] = round(price, 2)

        started = time.perf_counter()
        for frame in frames:
            feed._store_frame(frame)
        elapsed = time.perf_counter() - started

        self.assertLess(elapsed, 2.0, f"20,000 frames took {elapsed:.2f}s to store")
        self.assertLessEqual(sum(len(slot) for slot in feed._pending.values()), 300 * 3)
        self.assertEqual(20000 - 300 * 3, feed._superseded)

        release.set()
        blocked.join(5)
        delivered.clear()
        feed.emit_pass()
        self.assertEqual(300, len(delivered))
        for tick in delivered:
            self.assertAlmostEqual(last_price[tick["symbol"]], tick["lastTradedPrice"], places=2,
                                   msg=tick["symbol"])

    def test_the_socket_thread_does_no_io(self):
        import builtins
        import socket
        import redis

        feed, _, events, _ = self._wide_feed(count=50)

        def refuse(*_, **__):
            raise AssertionError("the socket thread did I/O")

        with mock.patch.object(socket.socket, "send", refuse), \
             mock.patch.object(redis.Redis, "execute_command", refuse), \
             mock.patch.object(requests.Session, "request", refuse), \
             mock.patch.object(builtins, "print", refuse):
            for n in range(1000):
                feed._store_frame(full(NSE_FNO, 40000 + n % 50, 100.0 + n, TRADE_UTC))
        self.assertEqual(1000, feed._frames_in)
        self.assertEqual([], events)

    def test_a_pass_merges_to_the_same_state_as_one_packet_at_a_time(self):
        import random

        rng = random.Random(20260928)
        ids = [40000 + i for i in range(5)]
        names = {f"NSE:OPT{i}": f"NSE_FNO:{sid}:OPTIDX" for i, sid in enumerate(ids)}

        def price():
            return round(rng.uniform(1.0, 500.0), 2)

        def packet():
            sid = rng.choice(ids)
            kind = rng.randrange(5)
            ltt = TRADE_UTC + rng.randrange(1, 600)
            if kind == 0:
                return ticker(NSE_FNO, sid, price(), ltt)
            if kind == 1:
                return quote(NSE_FNO, sid, price(), ltt, volume=rng.randrange(1, 10 ** 6), day_open=price(),
                             high=price(), low=price(), close=price(), ltq=rng.randrange(1, 900), atp=price(),
                             sell=rng.randrange(0, 10 ** 5), buy=rng.randrange(0, 10 ** 5))
            if kind == 2:
                return oi(NSE_FNO, sid, rng.randrange(0, 10 ** 7))
            if kind == 3:
                return prev_close(NSE_FNO, sid, price(), prev_oi=rng.randrange(0, 10 ** 7))
            levels = [(rng.randrange(1, 5000), rng.randrange(1, 5000), rng.randrange(1, 50), rng.randrange(1, 50),
                       price(), price()) for _ in range(rng.randrange(1, 6))]
            levels += [(0, 0, 0, 0, 0.0, 0.0)] * (5 - len(levels))
            return full(NSE_FNO, sid, price(), ltt, open_interest=rng.randrange(0, 10 ** 7), depth=levels)

        for sequence in range(2000):
            packets = [packet() for _ in range(rng.randrange(1, 41))]
            one_by_one, _, _, _ = _feed(vendor_names=names)
            one_by_one.subscribe(list(names))
            in_one_pass, _, _, _ = _feed(vendor_names=names)
            in_one_pass.subscribe(list(names))
            for p in packets:
                one_by_one._on_message(p)
                in_one_pass._store_frame(p)
            in_one_pass.emit_pass()
            self.assertEqual(one_by_one._state, in_one_pass._state, f"sequence {sequence}")

    def test_a_zero_field_in_the_latest_packet_keeps_the_value_from_before_the_pass(self):
        # The one place a pass differs from packet-at-a-time: only each kind's
        # latest packet is merged, so a field an earlier, superseded packet
        # carried and the latest one leaves at zero is not seen at all.
        feed, ticks, _, _ = _feed()
        feed.subscribe([EQUITY])
        feed._on_message(quote(NSE_EQ, EQUITY_ID, 2900.0, TRADE_UTC, high=2905.0))
        feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2910.0, TRADE_UTC, high=2920.0))
        feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2915.0, TRADE_UTC, high=0.0))
        feed.emit_pass()
        self.assertEqual(2915.0, ticks[-1]["lastTradedPrice"])
        self.assertEqual(2905.0, ticks[-1]["high"], "the 2920 high was superseded before the pass saw it")

    def test_kinds_merge_in_the_order_their_latest_packets_arrived(self):
        feed, ticks, _, _ = _feed()
        feed.subscribe([OPTION])
        merged = []
        original = feed._merge

        def recording(code, *args):
            merged.append(code)
            return original(code, *args)

        feed._merge = recording
        feed._store_frame(quote(NSE_FNO, OPTION_ID, 133.0, TRADE_UTC))
        feed._store_frame(full(NSE_FNO, OPTION_ID, 134.0, TRADE_UTC))
        feed._store_frame(oi(NSE_FNO, OPTION_ID, 7000000))
        feed.emit_pass()
        self.assertEqual([dhan.QUOTE_PACKET, dhan.FULL_PACKET, dhan.OI_PACKET], merged)
        self.assertEqual(1, len(ticks), "one tick for the instrument, carrying all three")
        self.assertEqual((134.0, 7000000), (ticks[0]["lastTradedPrice"], ticks[0]["openInterest"]))
        self.assertEqual("oi", json.loads(ticks[0]["rawPayload"])["type"])

    def test_a_disconnect_packet_is_heard_at_once(self):
        feed, ticks, events, _ = _feed()
        feed._store_frame(disconnect(805))
        self.assertEqual(FeedEvent.REFUSED, events[-1][0], "no pass needed")
        self.assertEqual({}, feed._pending)

    def test_market_status_text_and_truncated_frames_are_each_said_once(self):
        feed, ticks, events, _ = _feed()
        feed.subscribe([EQUITY])
        for _ in range(2):
            feed._store_frame(struct.pack("<BHBI", 7, 8, 0, 0))
            feed._store_frame("hello")
            feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC)[:30])
        self.assertEqual(1, sum(1 for e, d in events if "market status" in d))
        self.assertEqual(1, sum(1 for e, d in events if "unexpected text" in d))
        self.assertEqual(1, sum(1 for e, d in events if "needs 50" in d))
        self.assertEqual({}, feed._pending)

    def test_stacked_packets_become_one_entry_each(self):
        feed, _, _, _ = _feed()
        feed.subscribe([OPTION, EQUITY])
        feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC) + oi(NSE_FNO, OPTION_ID, 10)
                          + ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual(3, sum(len(slot) for slot in feed._pending.values()))
        self.assertEqual(1, feed._frames_in)

    def test_packets_for_unknown_instruments_are_dropped_and_counted(self):
        feed, ticks, _, _ = _feed()
        feed._store_frame(ticker(NSE_FNO, 99999, 10.0, TRADE_UTC))
        self.assertEqual({}, feed._pending)
        self.assertEqual(1, feed._dropped_unknown)
        feed.emit_pass()
        self.assertEqual([], ticks)

    def test_an_instrument_unsubscribed_between_store_and_pass_emits_nothing(self):
        feed, ticks, _, _ = _feed()
        feed.subscribe([OPTION, EQUITY])
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        feed._store_frame(quote(NSE_EQ, EQUITY_ID, 2913.05, TRADE_UTC))
        feed.unsubscribe([OPTION])
        feed.emit_pass()
        self.assertEqual([EQUITY], [t["symbol"] for t in ticks])

    def test_deliveries_never_overlap(self):
        feed, _, _, names = self._wide_feed(count=100)
        feed._emit_period = 0.01
        active, most, delivered = [0], [0], [0]
        guard = threading.Lock()

        def on_ticks(batch):
            with guard:
                active[0] += 1
                most[0] = max(most[0], active[0])
            time.sleep(0.001)
            delivered[0] += len(batch)
            with guard:
                active[0] -= 1

        with mock.patch.object(dhan.websocket, "WebSocketApp"):
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), on_ticks, lambda e, d="": None)
        stop = threading.Event()

        def flood():
            n = 0
            while not stop.is_set():
                feed._store_frame(ticker(NSE_FNO, 40000 + n % 100, 100.0 + n % 7, TRADE_UTC))
                n += 1

        flooder = threading.Thread(target=flood, daemon=True)
        flooder.start()
        # Passes from the test thread too, as the inline path makes them.
        deadline = time.monotonic() + 0.5
        while time.monotonic() < deadline:
            feed.emit_pass()
        feed.close()
        stop.set()
        flooder.join(2)
        self.assertGreater(delivered[0], 0)
        self.assertEqual(1, most[0], "on_ticks was never entered twice at once")

    @unittest.skipIf(dhan.fcntl is None, "FIONREAD needs fcntl (not on Windows)")
    def test_the_socket_backlog_is_the_kernels_unread_count(self):
        import socket
        from types import SimpleNamespace

        feed, _, _, _ = _feed()
        self.assertIsNone(feed.socket_backlog_bytes(), "no socket yet")
        server = socket.socket()
        server.bind(("127.0.0.1", 0))
        server.listen(1)
        client = socket.create_connection(server.getsockname())
        accepted, _ = server.accept()
        self.addCleanup(lambda: [s.close() for s in (client, accepted, server)])
        feed._app = SimpleNamespace(sock=SimpleNamespace(sock=client))
        self.assertEqual(0, feed.socket_backlog_bytes())
        accepted.sendall(b"x" * 5000)
        deadline = time.monotonic() + 3
        while feed.socket_backlog_bytes() != 5000 and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertEqual(5000, feed.socket_backlog_bytes())
        client.recv(3000)
        self.assertEqual(2000, feed.socket_backlog_bytes())
        feed._app = SimpleNamespace(sock=None)
        self.assertIsNone(feed.socket_backlog_bytes())

    def test_transport_idle_time_moves_on_frames_and_pings(self):
        clock = Clock()
        feed, _, _, _ = _feed(clock=clock)
        feed.subscribe([OPTION])
        self.assertIsNone(feed.transport_idle_seconds(), "nothing yet")
        feed._store_frame(ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        clock.t += 30
        self.assertEqual(30, feed.transport_idle_seconds())
        feed._note_ping()
        self.assertEqual(0, feed.transport_idle_seconds(), "a ping is proof of life")
        clock.t += 12
        feed._store_frame(ticker(NSE_FNO, 99999, 1.0, TRADE_UTC))
        self.assertEqual(0, feed.transport_idle_seconds(), "any frame is, even one for nobody")
        self.assertEqual({"frames": 2, "pings": 1, "dropped_unknown": 1},
                         {k: feed.stats()[k] for k in ("frames", "pings", "dropped_unknown")})

    def test_the_ingest_mode_and_period_come_from_the_environment(self):
        patcher = mock.patch.dict(os.environ, {}, clear=False)
        patcher.start()
        self.addCleanup(patcher.stop)
        for key in ("DHAN_CLIENT_ID", "DHAN_ACCESS_TOKEN", "DHAN_FEED_URL", "DHAN_SYMBOLS",
                    "DHAN_MIN_TICK_INTERVAL_MS", "DHAN_INGEST", "DHAN_EMIT_PERIOD_MS"):
            os.environ.pop(key, None)

        feed = DhanFeed.from_env()
        self.assertEqual(("emitter", 0.1), (feed._ingest, feed._emit_period), "the defaults are the fix")

        os.environ.update(DHAN_INGEST="Inline", DHAN_EMIT_PERIOD_MS="5000")
        feed = DhanFeed.from_env()
        self.assertEqual(("inline", 1.0), (feed._ingest, feed._emit_period), "the period is clamped to 1 s")
        os.environ["DHAN_EMIT_PERIOD_MS"] = "1"
        self.assertEqual(0.01, DhanFeed.from_env()._emit_period, "and to 10 ms")

        for key, bad in (("DHAN_INGEST", "threaded"), ("DHAN_EMIT_PERIOD_MS", "fast"), ("DHAN_EMIT_PERIOD_MS", "0")):
            os.environ.update(DHAN_INGEST="emitter", DHAN_EMIT_PERIOD_MS="100")
            os.environ[key] = bad
            with self.assertRaises(SystemExit) as failure:
                DhanFeed.from_env()
            self.assertIn(key, str(failure.exception))

    def test_inline_mode_hands_every_frame_on_from_the_socket_thread(self):
        feed = DhanFeed(FAKE_CLIENT_ID, FAKE_TOKEN, http=mock.MagicMock(), credentials_source=lambda: None,
                        min_tick_interval_ms=1000, ingest="inline",
                        vendor_names={OPTION: f"NSE_FNO:{OPTION_ID}:OPTIDX"})
        feed._send = lambda message: True
        received = []
        with mock.patch.object(dhan.websocket, "WebSocketApp") as app:
            feed.connect((FAKE_CLIENT_ID, FAKE_TOKEN), received.extend, lambda e, d="": None)
        self.addCleanup(feed.close)
        feed.subscribe([OPTION])
        on_message = app.call_args.kwargs["on_message"]
        on_message(None, ticker(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual([134.25], [t["lastTradedPrice"] for t in received], "handed on inside the callback")


class UniverseTests(unittest.TestCase):
    """The instruments beyond the watchlist, from GET /api/Dhan/universe."""

    def setUp(self):
        self.clock = Clock()
        self.api = FakeApi()
        self.feed = DhanFeed(http=self.api, credentials_source=lambda: None, clock=self.clock)
        self.printed = []
        patcher = mock.patch("builtins.print", side_effect=lambda *a, **_: self.printed.append(" ".join(map(str, a))))
        patcher.start()
        self.addCleanup(patcher.stop)

    def _universe(self, *symbols):
        return _response(200, {"symbols": list(symbols), "generatedUtc": "2026-09-15T03:40:00Z",
                               "counts": {"indices": 1}})

    def test_the_universe_is_cleaned_and_cached_for_five_minutes(self):
        self.api.set("Dhan/universe", self._universe("NSE:NIFTY50-INDEX", " NSE:NIFTY2691523950CE ",
                                                     "NSE:NIFTY50-INDEX", "", None, 7),
                     self._universe("MCX:CRUDEOIL26SEPFUT"))
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:NIFTY2691523950CE"], self.feed.extra_symbols())
        self.clock.t += 299
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:NIFTY2691523950CE"], self.feed.extra_symbols())
        self.assertEqual(1, len(self.api.asked), "cached")
        self.clock.t += 1
        self.assertEqual(["MCX:CRUDEOIL26SEPFUT"], self.feed.extra_symbols(), "the market moved")
        self.assertEqual(["Dhan/universe", "Dhan/universe"], self.api.asked)

    def test_a_failure_keeps_the_last_good_list_and_is_said_once_per_streak(self):
        self.api.set("Dhan/universe", self._universe("NSE:A"), ConnectionError("api down"),
                     _response(503, {"message": "restarting"}), self._universe("NSE:B"), ConnectionError("down again"))
        self.assertEqual(["NSE:A"], self.feed.extra_symbols())

        self.clock.t += 300
        self.assertEqual(["NSE:A"], self.feed.extra_symbols())
        self.clock.t += 10
        self.assertEqual(["NSE:A"], self.feed.extra_symbols())
        self.assertEqual(2, len(self.api.asked), "not asked again on every refresh while failing")
        self.clock.t += 30
        self.assertEqual(["NSE:A"], self.feed.extra_symbols())
        self.assertEqual(1, sum("could not get the Dhan universe" in line for line in self.printed))

        self.clock.t += 30
        self.assertEqual(["NSE:B"], self.feed.extra_symbols())
        self.clock.t += 300
        self.assertEqual(["NSE:B"], self.feed.extra_symbols())
        self.assertEqual(2, sum("could not get the Dhan universe" in line for line in self.printed),
                         "a new streak is said again")

    def test_no_universe_yet_is_an_empty_list_not_an_exception(self):
        self.api.set("Dhan/universe", _response(404, {"message": "not found"}))
        self.assertEqual([], self.feed.extra_symbols())
        self.api.set("Dhan/universe", _response(200, {"unexpected": True}))
        self.clock.t += 30
        self.assertEqual([], self.feed.extra_symbols())


class RawPayloadTests(unittest.TestCase):
    def test_dhan_keeps_its_book_off_the_strategy_stream(self):
        self.assertFalse(DhanFeed.publish_raw_payload)

    def test_the_book_still_goes_to_the_api(self):
        from _feed_fakes import runner_for
        feed, _, _, _ = _feed()
        publisher = mock.MagicMock()
        runner = runner_for(feed, publisher=publisher)
        feed._on_ticks = runner.on_ticks
        feed.subscribe([OPTION])
        feed._on_message(full(NSE_FNO, OPTION_ID, 134.25, TRADE_UTC))
        self.assertEqual("", publisher.publish_tick.call_args.args[0]["rawPayload"])
        self.assertIn("depth", json.loads(runner.pump.drain(10)[0]["rawPayload"]))


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


class CloseDetailTests(unittest.TestCase):
    """A dropped socket reads as words, never as the heartbeat error "None None"."""

    def test_close_reasons_read_as_words(self):
        from core.live.vendor_feed import close_detail
        self.assertEqual("connection dropped without a reason", close_detail(None, None))
        self.assertEqual("connection closed (1006)", close_detail(1006, None))
        self.assertEqual("connection closed (1000: bye)", close_detail(1000, "bye"))
        self.assertEqual("connection closed (going away)", close_detail(None, b"going away"))

