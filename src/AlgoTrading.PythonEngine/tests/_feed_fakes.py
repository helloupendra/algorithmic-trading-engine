"""A vendor adapter that records what the runner asks of it, for the feed tests."""

from unittest import mock

from core.live.vendor_feed import FeedEvent, VendorFeed


class FakeFeed(VendorFeed):
    key = "fake"

    def __init__(self, is_replay=False, take=None, unsubscribe_works=True):
        self.is_replay = is_replay
        self.ready_event = FeedEvent.CONNECTED
        self.subscribed_calls: list[list[str]] = []
        self.unsubscribed_calls: list[list[str]] = []
        self._take = take
        self.unsubscribe_works = unsubscribe_works

    def connect(self, credentials, on_ticks, on_event):
        pass

    def close(self):
        pass

    def subscribe(self, symbols):
        self.subscribed_calls.append(list(symbols))
        return [s for s in symbols if self._take is None or s in self._take]

    def unsubscribe(self, symbols):
        self.unsubscribed_calls.append(list(symbols))
        return self.unsubscribe_works


def runner_for(feed, watchlist=(), publisher=None, market_open=None):
    """A FeedRunner with no network: the watchlist is given, the API is a mock."""
    from core.live.feed_runner import FeedRunner

    runner = FeedRunner(feed, http=mock.MagicMock(), publisher=publisher,
                        market_open=market_open)
    runner.read_watchlist = lambda: list(runner._test_watchlist)
    runner._test_watchlist = list(watchlist)
    runner.socket_connected = True
    return runner
