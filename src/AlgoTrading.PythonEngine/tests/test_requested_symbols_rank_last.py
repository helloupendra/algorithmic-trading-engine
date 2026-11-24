"""Symbols a person asked for never take a desk symbol's place on a limited socket (24 Nov 2026 security review).

A trader could put ~100 stock futures on the recording list (a manual-order ticket, their own watchlist). On FYERS
(100 symbols) those took the room of every option contract the runs trade: the 17 Nov "FYERS feed full", on purpose.
The API now writes such symbols at priority 1; the feed offers them last and drops them to make room for the desk.
"""

import unittest
from unittest import mock

import _bootstrap  # noqa: F401

from _feed_fakes import FakeFeed, runner_for
from core.live.feed_runner import FeedRunner
from market_data.live.vendors.fyers import FyersFeed


class FakeSocket:
    def __init__(self):
        self.asked = []

    def subscribe(self, symbols, data_type=None):
        self.asked.append(list(symbols))

    def unsubscribe(self, symbols, data_type=None):
        pass


def fyers(limit):
    f = FyersFeed(http=mock.MagicMock(), max_symbols=limit)
    f._socket = FakeSocket()
    return f


RUN_OPTIONS = [f"NSE:BANKNIFTY26NOV{56000 + 100 * i}CE" for i in range(4)]
ASKED_FUTURES = [f"NSE:STOCK{i}26NOVFUT" for i in range(6)]


class FyersRanksRequestedLast(unittest.TestCase):
    def test_a_person_s_futures_come_after_the_desk_s_options(self):
        feed = fyers(limit=5)
        feed.set_requested(ASKED_FUTURES)

        taken = feed.subscribe(ASKED_FUTURES + RUN_OPTIONS)

        self.assertEqual(RUN_OPTIONS + ASKED_FUTURES[:1], taken)

    def test_without_requested_symbols_the_old_order_holds(self):
        feed = fyers(limit=2)

        taken = feed.subscribe(RUN_OPTIONS + ["NSE:NIFTY50-INDEX"])

        self.assertEqual(["NSE:NIFTY50-INDEX", RUN_OPTIONS[0]], taken)


class RunnerReadsPriorities(unittest.TestCase):
    def test_priority_one_rows_are_requested_and_listed_after_the_desk(self):
        feed = fyers(limit=100)
        runner = FeedRunner(feed, http=mock.MagicMock())
        rows = [
            {"symbol": "NSE:STOCK1FUT", "isActive": True, "dataType": "symbolUpdate", "priority": 1},
            {"symbol": "NSE:BANKNIFTY26NOV56000CE", "isActive": True, "dataType": "symbolUpdate", "priority": 100},
            {"symbol": "NSE:NIFTY50-INDEX", "isActive": True, "dataType": "symbolUpdate", "priority": 0},
            {"symbol": "NSE:OFF", "isActive": False, "dataType": "symbolUpdate", "priority": 100},
        ]
        runner._http.get.return_value.json.return_value = rows

        listed = runner.read_watchlist()

        self.assertEqual(["NSE:BANKNIFTY26NOV56000CE", "NSE:NIFTY50-INDEX", "NSE:STOCK1FUT"], listed)
        self.assertEqual({"NSE:STOCK1FUT"}, runner.requested)
        self.assertEqual({"NSE:STOCK1FUT"}, feed._requested)


class RunnerMakesRoomForTheDesk(unittest.TestCase):
    def test_a_declined_run_contract_takes_the_place_of_a_requested_symbol(self):
        new_contract = "NSE:BANKNIFTY26NOV57000CE"
        # The socket is full: it takes the new contract only once something has been dropped.
        state = {"dropped": False}
        feed = FakeFeed()
        feed.subscribe = lambda symbols: (feed.subscribed_calls.append(list(symbols))
                                          or [s for s in symbols if state["dropped"]])
        original_unsubscribe = feed.unsubscribe

        def unsubscribe(symbols):
            state["dropped"] = True
            return original_unsubscribe(symbols)

        feed.unsubscribe = unsubscribe
        runner = runner_for(feed, watchlist=RUN_OPTIONS + ASKED_FUTURES + [new_contract])
        runner.subscribed = set(RUN_OPTIONS + ASKED_FUTURES)
        runner.requested = set(ASKED_FUTURES)

        runner.sync_watchlist()

        self.assertIn(new_contract, runner.subscribed)
        self.assertEqual([[ASKED_FUTURES[0]]], feed.unsubscribed_calls)
        self.assertIn(ASKED_FUTURES[0], runner.declined)
        self.assertTrue(set(RUN_OPTIONS) <= runner.subscribed)

    def test_nothing_is_dropped_for_a_declined_requested_symbol(self):
        feed = FakeFeed(take=set())
        runner = runner_for(feed, watchlist=RUN_OPTIONS + ["NSE:NEWASK26NOVFUT"])
        runner.subscribed = set(RUN_OPTIONS)
        runner.requested = {"NSE:NEWASK26NOVFUT"}

        runner.sync_watchlist()

        self.assertEqual([], feed.unsubscribed_calls)
        self.assertIn("NSE:NEWASK26NOVFUT", runner.declined)


if __name__ == "__main__":
    unittest.main()
