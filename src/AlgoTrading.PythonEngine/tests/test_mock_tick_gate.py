"""
Fabricated ticks must never reach the live tables by accident.

mock_tick_loop() writes invented prices through the same upsert_tick() path as
real market data, into the same tables, marked only by an empty rawPayload. A
strategy reading a quote cannot tell the two apart. So the gate is what stands
between a quiet chart and a fabricated one, and it is worth pinning.

The module talks to Redis and the FYERS SDK at import time; both are stubbed.
"""

import sys
import types
import unittest
from unittest import mock

import _bootstrap  # noqa: F401


def _install_import_stubs():
    if "fyers_apiv3" not in sys.modules:
        fyers_pkg = types.ModuleType("fyers_apiv3")
        websocket_pkg = types.ModuleType("fyers_apiv3.FyersWebsocket")
        data_ws = types.ModuleType("fyers_apiv3.FyersWebsocket.data_ws")
        data_ws.FyersDataSocket = mock.MagicMock()
        websocket_pkg.data_ws = data_ws
        fyers_pkg.FyersWebsocket = websocket_pkg
        sys.modules["fyers_apiv3"] = fyers_pkg
        sys.modules["fyers_apiv3.FyersWebsocket"] = websocket_pkg
        sys.modules["fyers_apiv3.FyersWebsocket.data_ws"] = data_ws

    publisher_module = types.ModuleType("messaging.redis_publisher")
    publisher_module.build_publisher_from_env = lambda: mock.MagicMock()
    publisher_module.normalize_tick = lambda raw: raw
    sys.modules["messaging.redis_publisher"] = publisher_module


_install_import_stubs()

from market_data.live import fyers_streamer as streamer  # noqa: E402

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

# Shapes the broker feed genuinely cannot carry.
UNCARRIED_SYMBOLS = [
    "GOLD",
    "NIFTY50",
    "MCX:GOLD-FUT",
    "MCX:CRUDEOIL-FUT",
]


class MockTickGateTests(unittest.TestCase):
    def test_off_by_default(self):
        """The shipped default. An invented price in a live table is the worse failure."""
        self.assertFalse(streamer.ENABLE_MOCK_TICKS)

    def test_nothing_is_mocked_while_the_flag_is_off(self):
        with mock.patch.object(streamer, "ENABLE_MOCK_TICKS", False):
            for symbol in REAL_SYMBOLS + UNCARRIED_SYMBOLS:
                self.assertFalse(
                    streamer.should_mock_symbol(symbol),
                    f"{symbol} was mocked with the flag off",
                )

    def test_real_symbols_are_never_mocked_even_with_the_flag_on(self):
        """
        The line that actually matters. A dated option or an index must keep its
        real price — or the absence of one — however the flag is set.
        """
        with mock.patch.object(streamer, "ENABLE_MOCK_TICKS", True):
            for symbol in REAL_SYMBOLS:
                self.assertFalse(
                    streamer.should_mock_symbol(symbol),
                    f"{symbol} would get a fabricated price",
                )

    def test_uncarried_shapes_are_mocked_when_asked_for(self):
        with mock.patch.object(streamer, "ENABLE_MOCK_TICKS", True):
            for symbol in UNCARRIED_SYMBOLS:
                self.assertTrue(
                    streamer.should_mock_symbol(symbol),
                    f"{symbol} should be mocked when the flag is on",
                )

    def test_a_dated_continuous_future_is_a_real_contract(self):
        """"-FUT" alone is not enough: a month in the name means a real contract."""
        with mock.patch.object(streamer, "ENABLE_MOCK_TICKS", True):
            self.assertTrue(streamer.should_mock_symbol("MCX:GOLD-FUT"))
            self.assertFalse(streamer.should_mock_symbol("MCX:GOLD26SEPFUT"))


if __name__ == "__main__":
    unittest.main()
