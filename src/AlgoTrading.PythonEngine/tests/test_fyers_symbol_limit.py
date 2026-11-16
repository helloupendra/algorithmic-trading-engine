"""FYERS: the socket asks for no more symbols than the plan allows, indices first (16 Nov 2026)."""

import os
import unittest
from unittest import mock

from market_data.live.vendors.fyers import DEFAULT_MAX_SYMBOLS, FyersFeed


class FakeSocket:
    def __init__(self):
        self.asked = []
        self.dropped = []

    def subscribe(self, symbols, data_type=None):
        self.asked.append(list(symbols))

    def unsubscribe(self, symbols, data_type=None):
        self.dropped.append(list(symbols))


def feed(limit=None):
    f = FyersFeed(http=mock.MagicMock(), max_symbols=limit)
    f._socket = FakeSocket()
    return f


OPTIONS = [f"NSE:BANKNIFTY26NOV{50000 + 100 * i}CE" for i in range(60)] + \
          [f"NSE:NIFTY26N17{24000 + 50 * i}CE" for i in range(60)]
SPOTS = ["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX", "MCX:CRUDEOIL26NOVFUT"]


class FyersSymbolLimitTest(unittest.TestCase):
    def test_the_standard_plan_limit_is_the_default(self):
        with mock.patch.dict(os.environ, {"FYERS_MAX_SYMBOLS": ""}):
            self.assertEqual(100, DEFAULT_MAX_SYMBOLS)
            self.assertEqual(100, FyersFeed(http=mock.MagicMock()).max_symbols)
        with mock.patch.dict(os.environ, {"FYERS_MAX_SYMBOLS": "5000"}):
            self.assertEqual(5000, FyersFeed(http=mock.MagicMock()).max_symbols)
        with mock.patch.dict(os.environ, {"FYERS_MAX_SYMBOLS": "lots"}):
            self.assertEqual(100, FyersFeed(http=mock.MagicMock()).max_symbols)

    def test_never_asks_for_more_than_the_limit_and_keeps_indices_first(self):
        f = feed(100)
        taken = f.subscribe(OPTIONS + SPOTS)      # 124 asked, indices last in the runner's order
        self.assertEqual(100, len(taken))
        self.assertEqual(1, len(f._socket.asked))
        self.assertEqual(100, len(f._socket.asked[0]))
        self.assertEqual(SPOTS[:3], taken[:3])                     # indices first
        self.assertEqual("MCX:CRUDEOIL26NOVFUT", taken[3])        # then futures
        self.assertTrue(all(s.startswith("NSE:NIFTY26") for s in taken[4:64]))   # then NIFTY's options
        self.assertEqual(36, sum(s.startswith("NSE:BANKNIFTY") for s in taken))

    def test_a_later_addition_gets_only_the_room_left(self):
        f = feed(5)
        self.assertEqual(SPOTS, f.subscribe(SPOTS))
        self.assertEqual([OPTIONS[0]], f.subscribe(OPTIONS[:3]))
        self.assertEqual([], f.subscribe(OPTIONS[3:5]))
        self.assertTrue(f.unsubscribe([OPTIONS[0]]))
        self.assertEqual([OPTIONS[1]], f.subscribe(OPTIONS[1:3]))

    def test_a_symbol_already_on_the_wire_is_not_asked_again(self):
        f = feed(10)
        f.subscribe(SPOTS)
        self.assertEqual([OPTIONS[0]], f.subscribe([SPOTS[0], OPTIONS[0]]))


if __name__ == "__main__":
    unittest.main()
