"""
Fabricated ticks must never reach the live tables by accident — from any feed.

The mock source writes invented prices through the same path as real market
data, into the same tables, marked only by their rawPayload. A strategy reading
a quote cannot tell the two apart. So the gate is what stands between a quiet
chart and a fabricated one, and it is worth pinning.
"""

import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from core.live import mock_ticks

# Real symbols seen in this platform's watchlist and run history.
REAL_SYMBOLS = [
    "NSE:NIFTYBANK-INDEX",
    "BSE:SENSEX-INDEX",
    "NSE:NIFTY50-INDEX",
    "NSE:BANKNIFTY26SEP57600CE",
    "NSE:BANKNIFTY26SEP55000PE",
    "NSE:BANKNIFTY26SEPFUT",
    "NSE:SBIN-EQ",
]

# Shapes a broker feed genuinely cannot carry.
UNCARRIED_SYMBOLS = [
    "GOLD",
    "NIFTY50",
    "MCX:GOLD-FUT",
    "MCX:CRUDEOIL-FUT",
]


class MockTickGateTests(unittest.TestCase):
    def test_off_by_default(self):
        """The shipped default. An invented price in a live table is the worse failure."""
        self.assertFalse(mock_ticks.ENABLE_MOCK_TICKS)

    def test_nothing_is_mocked_while_the_flag_is_off(self):
        with mock.patch.object(mock_ticks, "ENABLE_MOCK_TICKS", False):
            for symbol in REAL_SYMBOLS + UNCARRIED_SYMBOLS:
                self.assertFalse(mock_ticks.should_mock_symbol(symbol), f"{symbol} was mocked with the flag off")

    def test_real_symbols_are_never_mocked_even_with_the_flag_on(self):
        """The line that matters: a dated option or an index keeps its real price, or none."""
        with mock.patch.object(mock_ticks, "ENABLE_MOCK_TICKS", True):
            for symbol in REAL_SYMBOLS:
                self.assertFalse(mock_ticks.should_mock_symbol(symbol), f"{symbol} would get a fabricated price")

    def test_uncarried_shapes_are_mocked_when_asked_for(self):
        with mock.patch.object(mock_ticks, "ENABLE_MOCK_TICKS", True):
            for symbol in UNCARRIED_SYMBOLS:
                self.assertTrue(mock_ticks.should_mock_symbol(symbol), f"{symbol} should be mocked when the flag is on")

    def test_a_dated_continuous_future_is_a_real_contract(self):
        """"-FUT" alone is not enough: a month in the name means a real contract."""
        with mock.patch.object(mock_ticks, "ENABLE_MOCK_TICKS", True):
            self.assertTrue(mock_ticks.should_mock_symbol("MCX:GOLD-FUT"))
            self.assertFalse(mock_ticks.should_mock_symbol("MCX:GOLD26SEPFUT"))


if __name__ == "__main__":
    unittest.main()
