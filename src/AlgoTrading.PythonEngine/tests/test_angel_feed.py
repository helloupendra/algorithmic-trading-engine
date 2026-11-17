"""
market_data/live/vendors/angel.py — Angel One's websocket as a live feed (18 Nov 2026).

The owner: until Dhan's data plan is back, NIFTY must not stop under any condition. Angel
signs in by itself and carries 1,000 symbols, so it is the feed that needs nobody in the
morning. These pin the parts that would price the wrong thing if they slipped: the symbol
map, the packet layout, paise, the two sides of the book, and what reaches the runner.
"""
import json
import struct
import unittest
from datetime import date
from unittest import mock

import _bootstrap  # noqa: F401

from core.live.vendor_feed import FeedEvent
from market_data.angel.instruments import ScripRow
from market_data.live.vendors import ADAPTERS
from market_data.live.vendors.angel import (AngelFeed, AngelSymbolMap, MODE_SNAP_QUOTE, packet_to_tick,
                                            parse_packet)


def row(token, symbol, name, exchange, expiry="", strike=-1.0, kind=""):
    return ScripRow(token=token, symbol=symbol, name=name, expiry=expiry, strike=strike, lot_size=1,
                    instrument_type=kind, exchange=exchange, tick_size=5.0)


ROWS = [
    row("99926000", "Nifty 50", "NIFTY", "NSE", kind="AMXIDX"),
    row("99926009", "Nifty Bank", "BANKNIFTY", "NSE", kind="AMXIDX"),
    row("99919000", "SENSEX", "SENSEX", "BSE", kind="AMXIDX"),
    row("99926017", "India VIX", "INDIA VIX", "NSE", kind="AMXIDX"),
    row("2885", "RELIANCE-EQ", "RELIANCE", "NSE"),
    row("64462", "NIFTY23NOV2625400CE", "NIFTY", "NFO", "23NOV2026", 2540000.0, "OPTIDX"),
    row("64469", "NIFTY23NOV2625400PE", "NIFTY", "NFO", "23NOV2026", 2540000.0, "OPTIDX"),
    row("70001", "NIFTY30NOV2625400CE", "NIFTY", "NFO", "30NOV2026", 2540000.0, "OPTIDX"),
    row("62414", "BANKNIFTY23NOV2661700CE", "BANKNIFTY", "NFO", "23NOV2026", 6170000.0, "OPTIDX"),
    row("821516", "SENSEX26N1982200CE", "SENSEX", "BFO", "19NOV2026", 8220000.0, "OPTIDX"),
    row("61471", "NIFTY23NOV26FUT", "NIFTY", "NFO", "23NOV2026", -1.0, "FUTIDX"),
    row("576264", "CRUDEOIL18DEC26FUT", "CRUDEOIL", "MCX", "18DEC2026", 0.0, "FUTCOM"),
    row("585917", "CRUDEOIL16DEC268000CE", "CRUDEOIL", "MCX", "16DEC2026", 800000.0, "OPTFUT"),
]


class SymbolMapTests(unittest.TestCase):
    def setUp(self):
        self.map = AngelSymbolMap(ROWS)

    def test_every_kind_the_desk_trades_maps_to_its_exchange_type_and_token(self):
        cases = {
            "NSE:NIFTY50-INDEX": (1, "99926000"),
            "NSE:NIFTYBANK-INDEX": (1, "99926009"),
            "BSE:SENSEX-INDEX": (3, "99919000"),
            "NSE:INDIAVIX-INDEX": (1, "99926017"),
            "NSE:RELIANCE-EQ": (1, "2885"),
            "NSE:NIFTY26N2325400CE": (2, "64462"),       # weekly: the day is in the symbol
            "NSE:NIFTY26N2325400PE": (2, "64469"),
            "BSE:SENSEX26N1982200CE": (4, "821516"),
            "NSE:NIFTY26NOVFUT": (2, "61471"),
            "MCX:CRUDEOIL26DECFUT": (5, "576264"),
            "MCX:CRUDEOIL26DEC8000CE": (5, "585917"),
        }
        for symbol, expected in cases.items():
            self.assertEqual(expected, self.map.resolve(symbol), symbol)

    def test_a_monthly_symbol_is_the_months_last_expiry(self):
        # BANKNIFTY's November contract expires on the 23rd: the 24th is Guru Nanak Jayanti.
        self.assertEqual((2, "62414"), self.map.resolve("NSE:BANKNIFTY26NOV61700CE"))
        self.assertEqual((2, "70001"), self.map.resolve("NSE:NIFTY26NOV25400CE"))

    def test_anything_not_exactly_there_is_none_never_a_near_miss(self):
        for symbol in ("NSE:NIFTY26N2425400CE",      # no expiry on the 24th
                       "NSE:NIFTY26N2325450CE",      # no such strike
                       "NSE:NOSUCH-EQ", "NSE:NOSUCH-INDEX", "CDS:USDINR26NOVFUT", "garbage", ""):
            self.assertIsNone(self.map.resolve(symbol), symbol)


def packet(token="64462", exchange_type=2, ltp=17100, mode=MODE_SNAP_QUOTE, snap=True, levels=None,
           oi=4650165, volume=32510725, stamp_ms=1763374200000):
    data = bytearray(379 if snap else 51)
    data[0], data[1] = mode, exchange_type
    data[2:2 + len(token)] = token.encode()
    struct.pack_into("<qqq", data, 27, 7, stamp_ms, ltp)
    if snap:
        struct.pack_into("<qqq", data, 51, 75, 16900, volume)
        struct.pack_into("<dd", data, 75, 1000.0, 2000.0)
        struct.pack_into("<qqqq", data, 91, 15035, 27115, 14120, 15425)
        struct.pack_into("<qq", data, 123, 0, oi)
        for i, (flag, qty, price) in enumerate(levels or []):
            struct.pack_into("<HqqH", data, 147 + 20 * i, flag, qty, price, 3)
    return bytes(data)


class PacketTests(unittest.TestCase):
    def test_a_snap_quote_reads_in_rupees_with_both_sides_of_the_book(self):
        # Flag 1 holds the lower prices here: the side is told by price, not by the flag.
        levels = [(1, 585, 17035), (1, 300, 17000), (0, 1755, 17245), (0, 900, 17300)]
        tick = packet_to_tick(parse_packet(packet(levels=levels)), "NSE:NIFTY26N2325400CE")
        self.assertEqual("NSE:NIFTY26N2325400CE", tick["symbol"])
        self.assertEqual(171.0, tick["lastTradedPrice"])
        self.assertEqual((170.35, 585), (tick["bidPrice"], tick["bidSize"]))
        self.assertEqual((172.45, 1755), (tick["askPrice"], tick["askSize"]))
        self.assertEqual((150.35, 271.15, 141.2, 154.25), (tick["open"], tick["high"], tick["low"], tick["prevClose"]))
        self.assertEqual(32510725, tick["volume"])
        self.assertEqual(4650165, tick["openInterest"])
        self.assertEqual("2025-11-17T10:10:00Z", tick["exchangeTimestampUtc"])
        self.assertEqual("2:64462", json.loads(tick["rawPayload"])["angel"])

    def test_one_side_alone_names_no_bid_or_ask(self):
        tick = packet_to_tick(parse_packet(packet(levels=[(0, 10, 17245)])), "X")
        self.assertIsNone(tick["bidPrice"])
        self.assertIsNone(tick["askPrice"])

    def test_an_index_or_a_cash_share_has_no_open_interest(self):
        index = packet_to_tick(parse_packet(packet("99926000", 1, ltp=2538740, oi=0, volume=0)), "NSE:NIFTY50-INDEX")
        self.assertEqual(25387.4, index["lastTradedPrice"])
        self.assertIsNone(index["openInterest"])
        self.assertIsNone(index["volume"])
        share = packet_to_tick(parse_packet(packet("2885", 1, ltp=136700, oi=426012000)), "NSE:RELIANCE-EQ")
        self.assertIsNone(share["openInterest"])

    def test_an_ltp_packet_carries_the_price_alone(self):
        tick = packet_to_tick(parse_packet(packet("99926000", 1, ltp=2538740, mode=1, snap=False)), "NSE:NIFTY50-INDEX")
        self.assertEqual(25387.4, tick["lastTradedPrice"])
        self.assertIsNone(tick["open"])
        self.assertIsNone(tick["bidPrice"])

    def test_a_short_frame_is_not_a_packet(self):
        self.assertIsNone(parse_packet(b"\x01\x02abc"))


class FakeApp:
    def __init__(self):
        self.sent = []
        self.sock = mock.MagicMock(connected=True)

    def send(self, text):
        self.sent.append(json.loads(text))


def feed(max_symbols=1000):
    f = AngelFeed(session_factory=mock.MagicMock(), symbol_map_loader=lambda: AngelSymbolMap(ROWS),
                  max_symbols=max_symbols)
    f._map = AngelSymbolMap(ROWS)
    f._app = FakeApp()
    return f


class FeedTests(unittest.TestCase):
    def test_the_adapter_is_registered_under_its_connector_key(self):
        self.assertEqual("market_data.live.vendors.angel:AngelFeed", ADAPTERS["angel"])
        self.assertEqual("angel", AngelFeed.key)

    def test_subscribe_asks_per_exchange_type_in_snap_quote_mode(self):
        f = feed()
        taken = f.subscribe(["NSE:NIFTY50-INDEX", "NSE:NIFTY26N2325400CE", "BSE:SENSEX-INDEX", "NSE:NOSUCH-EQ"])
        self.assertEqual(["NSE:NIFTY50-INDEX", "NSE:NIFTY26N2325400CE", "BSE:SENSEX-INDEX"], taken)
        lists = {m["params"]["tokenList"][0]["exchangeType"]: m["params"]["tokenList"][0]["tokens"] for m in f._app.sent}
        self.assertEqual({1: ["99926000"], 2: ["64462"], 3: ["99919000"]}, lists)
        self.assertTrue(all(m["action"] == 1 and m["params"]["mode"] == MODE_SNAP_QUOTE for m in f._app.sent))

    def test_a_symbol_already_on_the_wire_is_not_asked_again_and_the_limit_holds(self):
        f = feed(max_symbols=2)
        self.assertEqual(["NSE:NIFTY50-INDEX"], f.subscribe(["NSE:NIFTY50-INDEX"]))
        self.assertEqual(["NSE:NIFTYBANK-INDEX"], f.subscribe(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX",
                                                               "BSE:SENSEX-INDEX"]))
        self.assertEqual([], f.subscribe(["BSE:SENSEX-INDEX"]))
        self.assertTrue(f.unsubscribe(["NSE:NIFTY50-INDEX"]))
        self.assertEqual(0, f._app.sent[-1]["action"])
        self.assertEqual(["BSE:SENSEX-INDEX"], f.subscribe(["BSE:SENSEX-INDEX"]))

    def test_a_packet_reaches_the_runner_under_the_platforms_symbol(self):
        f = feed()
        f.subscribe(["NSE:NIFTY26N2325400CE"])
        got = []
        f._on_ticks = got.extend
        f._on_message(packet())
        f._on_message("pong")
        f._on_message(packet(token="99999"))           # not asked for: not stored under a guessed name
        self.assertEqual(["NSE:NIFTY26N2325400CE"], [t["symbol"] for t in got])
        self.assertIsNotNone(f.transport_idle_seconds())

    def test_a_refused_token_is_replaced_by_a_fresh_login(self):
        session = mock.MagicMock(jwt="old", feed_token="feed")
        f = AngelFeed(session_factory=lambda: session, symbol_map_loader=lambda: AngelSymbolMap(ROWS))
        self.assertEqual(("old", "feed"), f.acquire_credentials())
        session.login.assert_called_with(force=False)
        f.acquire_credentials(not_this=("old", "feed"))
        session.login.assert_called_with(force=True)

    def test_a_refused_handshake_reads_as_a_rejected_credential(self):
        f = feed()
        events = []
        with mock.patch("market_data.live.vendors.angel.websocket.WebSocketApp") as app, \
                mock.patch("market_data.live.vendors.angel.threading.Thread"):
            f._session = mock.MagicMock()
            f.connect(("jwt", "feed"), lambda ticks: None, lambda e, d="": events.append(e))
            on_error = app.call_args.kwargs["on_error"]
        on_error(None, Exception("Handshake status 401 Unauthorized"))
        on_error(None, Exception("Connection reset by peer"))
        self.assertEqual([FeedEvent.CREDENTIALS_REJECTED, FeedEvent.ERROR], events)


if __name__ == "__main__":
    unittest.main()
