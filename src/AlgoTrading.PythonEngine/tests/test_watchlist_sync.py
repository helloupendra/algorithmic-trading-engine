"""
Watchlist changes must not tear down the websocket.

A rolling straddle rolls its ATM strike several times a day. The ingestor used
to rebuild the whole connection on every watchlist change, which blacked out
every other symbol too and lost their ticks for good. These tests pin the
behaviour that replaced it, including the fallback that restores the old
rebuild when incremental subscribe turns out not to work.

The module talks to Redis and the FYERS SDK at import time, so both are stubbed
before it loads — the logic under test touches neither.
"""

import sys
import types
import unittest
from unittest import mock

import _bootstrap  # noqa: F401


def _install_import_stubs():
    """Stand-ins for the two hard dependencies the streamer imports."""
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


class WatchlistSyncTests(unittest.TestCase):
    def setUp(self):
        streamer.subscribed_symbols = set()
        streamer.pending_subscriptions.clear()
        streamer.last_real_tick.clear()
        streamer.restart_required = False
        streamer.socket_connected = True
        streamer.fyers = mock.MagicMock()

        self.subscribed = []
        self.unsubscribed = []

        patches = [
            mock.patch.object(streamer, "subscribe_symbols", self.subscribed.extend),
            mock.patch.object(streamer, "unsubscribe_symbols", self._unsubscribe),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

        self.unsubscribe_works = True

    def _unsubscribe(self, symbols):
        self.unsubscribed.extend(symbols)
        return self.unsubscribe_works

    def _watchlist(self, symbols):
        return mock.patch.object(streamer, "get_active_watchlist", lambda: list(symbols))

    # -- subscribing -------------------------------------------------------

    def test_first_sync_subscribes_everything(self):
        with self._watchlist(["NSE:A", "NSE:B"]):
            streamer.sync_watchlist(force_subscribe=True)

        self.assertEqual(sorted(self.subscribed), ["NSE:A", "NSE:B"])
        self.assertEqual(streamer.subscribed_symbols, {"NSE:A", "NSE:B"})

    def test_an_added_symbol_does_not_restart_the_connection(self):
        """The whole point: an ATM roll must not cost the other symbols."""
        with self._watchlist(["NSE:A", "NSE:B"]):
            streamer.sync_watchlist(force_subscribe=True)
        self.subscribed.clear()

        with self._watchlist(["NSE:A", "NSE:B", "NSE:C"]):
            streamer.sync_watchlist()

        self.assertEqual(self.subscribed, ["NSE:C"], "only the new symbol is subscribed")
        self.assertFalse(streamer.restart_required, "the connection must survive a roll")
        self.assertEqual(streamer.subscribed_symbols, {"NSE:A", "NSE:B", "NSE:C"})

    def test_a_removed_symbol_is_unsubscribed_in_place(self):
        with self._watchlist(["NSE:A", "NSE:B"]):
            streamer.sync_watchlist(force_subscribe=True)

        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist()

        self.assertEqual(self.unsubscribed, ["NSE:B"])
        self.assertFalse(streamer.restart_required)
        self.assertEqual(streamer.subscribed_symbols, {"NSE:A"})

    def test_a_symbol_that_cannot_be_unsubscribed_stays_on_the_wire(self):
        """
        subscribed_symbols is what the heartbeat and the stall detector read,
        so it has to say what is actually on the wire — not what we wish were.
        """
        self.unsubscribe_works = False

        with self._watchlist(["NSE:A", "NSE:B"]):
            streamer.sync_watchlist(force_subscribe=True)

        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist()

        self.assertFalse(streamer.restart_required, "an extra symbol is not worth a rebuild")
        self.assertEqual(streamer.subscribed_symbols, {"NSE:A", "NSE:B"})

    # -- the fallback ------------------------------------------------------

    def test_one_tick_proves_the_batch_and_clears_it(self):
        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist(force_subscribe=True)
        with self._watchlist(["NSE:A", "NSE:C", "NSE:D"]):
            streamer.sync_watchlist()

        self.assertEqual(len(streamer.pending_subscriptions), 2)

        # C trades; D is a quiet far-OTM strike. That is proof enough.
        subscribed_at = streamer.pending_subscriptions["NSE:C"][0]
        streamer.last_real_tick["NSE:C"] = subscribed_at + 1

        with mock.patch.object(streamer, "is_market_open", lambda: True):
            streamer.check_pending_subscriptions()

        self.assertEqual(streamer.pending_subscriptions, {})
        self.assertFalse(streamer.restart_required)

    def test_a_wholly_silent_batch_falls_back_to_the_rebuild(self):
        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist(force_subscribe=True)
        with self._watchlist(["NSE:A", "NSE:C"]):
            streamer.sync_watchlist()

        self._expire_pending()

        with mock.patch.object(streamer, "is_market_open", lambda: True):
            streamer.check_pending_subscriptions()

        self.assertTrue(streamer.restart_required, "silence during open market means rebuild")
        self.assertEqual(streamer.pending_subscriptions, {})

    def test_silence_outside_market_hours_proves_nothing(self):
        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist(force_subscribe=True)
        with self._watchlist(["NSE:A", "NSE:C"]):
            streamer.sync_watchlist()

        self._expire_pending()

        with mock.patch.object(streamer, "is_market_open", lambda: False):
            streamer.check_pending_subscriptions()

        self.assertFalse(streamer.restart_required, "a closed market is not a failed subscribe")
        self.assertIn("NSE:C", streamer.pending_subscriptions)

    def test_a_stale_tick_from_a_previous_subscribe_does_not_count(self):
        """
        last_real_tick keeps a symbol's last tick forever. A symbol removed and
        re-added must be proved by data that arrived after THIS subscribe.
        """
        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist(force_subscribe=True)
        with self._watchlist(["NSE:A", "NSE:C"]):
            streamer.sync_watchlist()

        streamer.last_real_tick["NSE:C"] = streamer.pending_subscriptions["NSE:C"][0] - 60
        self._expire_pending()

        with mock.patch.object(streamer, "is_market_open", lambda: True):
            streamer.check_pending_subscriptions()

        self.assertTrue(streamer.restart_required)

    def test_nothing_is_checked_while_the_socket_is_down(self):
        with self._watchlist(["NSE:A"]):
            streamer.sync_watchlist(force_subscribe=True)
        with self._watchlist(["NSE:A", "NSE:C"]):
            streamer.sync_watchlist()

        self._expire_pending()
        streamer.socket_connected = False

        with mock.patch.object(streamer, "is_market_open", lambda: True):
            streamer.check_pending_subscriptions()

        self.assertFalse(
            streamer.restart_required,
            "the disconnect watchdog owns a down socket; this check must not double-fire",
        )

    def _expire_pending(self):
        for symbol, (subscribed_at, _) in list(streamer.pending_subscriptions.items()):
            streamer.pending_subscriptions[symbol] = (subscribed_at, 0.0)


if __name__ == "__main__":
    unittest.main()
