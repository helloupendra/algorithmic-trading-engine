"""
Watchlist changes must not tear down the websocket — for any vendor.

A rolling straddle rolls its ATM strike several times a day. The FYERS ingestor
used to rebuild the whole connection on every watchlist change, which blacked
out every other symbol too and lost their ticks for good. These tests pin the
behaviour that replaced it, now in the runner every vendor shares, including
the fallback that restores the rebuild when incremental subscribe does not take.
"""

import unittest

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for


class WatchlistSyncTests(unittest.TestCase):
    def setUp(self):
        self.feed = FakeFeed()
        self.runner = runner_for(self.feed)
        self.market_open = True
        self.runner._market_open_override = lambda: self.market_open

    def _sync(self, symbols, force=False):
        self.runner._test_watchlist = list(symbols)
        self.runner.sync_watchlist(force_subscribe=force)

    def test_first_sync_subscribes_everything(self):
        self._sync(["NSE:A", "NSE:B"], force=True)
        self.assertEqual([["NSE:A", "NSE:B"]], self.feed.subscribed_calls)
        self.assertEqual({"NSE:A", "NSE:B"}, self.runner.subscribed)

    def test_an_added_symbol_does_not_restart_the_connection(self):
        self._sync(["NSE:A", "NSE:B"], force=True)
        self._sync(["NSE:A", "NSE:B", "NSE:C"])
        self.assertEqual(["NSE:C"], self.feed.subscribed_calls[-1], "only the new symbol is subscribed")
        self.assertFalse(self.runner.restart_required, "the connection must survive a roll")
        self.assertEqual({"NSE:A", "NSE:B", "NSE:C"}, self.runner.subscribed)

    def test_a_removed_symbol_is_unsubscribed_in_place(self):
        self._sync(["NSE:A", "NSE:B"], force=True)
        self._sync(["NSE:A"])
        self.assertEqual([["NSE:B"]], self.feed.unsubscribed_calls)
        self.assertFalse(self.runner.restart_required)
        self.assertEqual({"NSE:A"}, self.runner.subscribed)

    def test_a_symbol_that_cannot_be_unsubscribed_stays_on_the_wire(self):
        # `subscribed` is what the heartbeat and the stall detector read, so it
        # must say what is actually on the wire.
        self.feed.unsubscribe_works = False
        self._sync(["NSE:A", "NSE:B"], force=True)
        self._sync(["NSE:A"])
        self.assertFalse(self.runner.restart_required, "an extra symbol is not worth a rebuild")
        self.assertEqual({"NSE:A", "NSE:B"}, self.runner.subscribed)

    def test_symbols_the_vendor_declines_are_not_counted_or_offered_again(self):
        feed = FakeFeed(take={"NSE:A"})
        runner = runner_for(feed, watchlist=["NSE:A", "NSE:B"])
        runner.sync_watchlist(force_subscribe=True)
        self.assertEqual({"NSE:A"}, runner.subscribed)
        self.assertEqual({"NSE:B"}, runner.declined)
        runner.sync_watchlist()
        self.assertEqual(1, len(feed.subscribed_calls), "a declined symbol is not offered every few seconds")

    # -- the fallback ------------------------------------------------------

    def _add_batch(self):
        self._sync(["NSE:A"], force=True)
        self._sync(["NSE:A", "NSE:C", "NSE:D"])

    def _expire_pending(self):
        for symbol, (subscribed_at, _) in list(self.runner.pending_subscriptions.items()):
            self.runner.pending_subscriptions[symbol] = (subscribed_at, 0.0)

    def test_one_tick_proves_the_batch_and_clears_it(self):
        self._add_batch()
        self.assertEqual(2, len(self.runner.pending_subscriptions))
        # C trades; D is a quiet far-OTM strike. That is proof enough.
        subscribed_at = self.runner.pending_subscriptions["NSE:C"][0]
        self.runner.last_real_tick["NSE:C"] = subscribed_at + 1
        self.runner.check_pending_subscriptions()
        self.assertEqual({}, self.runner.pending_subscriptions)
        self.assertFalse(self.runner.restart_required)

    def test_a_wholly_silent_batch_falls_back_to_the_rebuild(self):
        self._add_batch()
        self._expire_pending()
        self.runner.check_pending_subscriptions()
        self.assertTrue(self.runner.restart_required, "silence during open session means rebuild")
        self.assertEqual({}, self.runner.pending_subscriptions)

    def test_silence_outside_the_session_proves_nothing(self):
        self._add_batch()
        self._expire_pending()
        self.market_open = False
        self.runner.check_pending_subscriptions()
        self.assertFalse(self.runner.restart_required, "a closed market is not a failed subscribe")
        self.assertIn("NSE:C", self.runner.pending_subscriptions)

    def test_a_stale_tick_from_a_previous_subscribe_does_not_count(self):
        self._add_batch()
        self.runner.last_real_tick["NSE:C"] = self.runner.pending_subscriptions["NSE:C"][0] - 60
        self._expire_pending()
        self.runner.check_pending_subscriptions()
        self.assertTrue(self.runner.restart_required)

    def test_nothing_is_checked_while_the_socket_is_down(self):
        self._add_batch()
        self._expire_pending()
        self.runner.socket_connected = False
        self.runner.check_pending_subscriptions()
        self.assertFalse(self.runner.restart_required)


if __name__ == "__main__":
    unittest.main()
