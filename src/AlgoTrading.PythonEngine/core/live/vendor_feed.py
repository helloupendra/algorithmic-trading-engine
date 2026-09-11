"""
What a live feed has to provide, and what it gets for free.

Two vendors differ in four things: how you open a socket, how you ask for a
symbol, what a price message looks like, and what the vendor calls an
instrument. Everything else a streamer does — reading the watchlist, batching
ticks to the API, the heartbeat, noticing that prices stopped — belongs to this
platform and is written once.
"""

from abc import ABC, abstractmethod


class VendorFeed(ABC):
    """
    One vendor's live socket, behind a contract the runner can drive.

    Implementations hold the socket and nothing else: no database, no API
    session, no watchlist. They hand normalised ticks to `on_tick`, which the
    runner supplies, and the runner decides what happens to them.
    """

    #: Written into the SourceKey of every row this feed produces. Must match
    #: the connector key on the C# side, or lineage stops meaning anything.
    key: str = "unknown"

    #: The vendor's ceiling on one connection. The runner refuses to subscribe
    #: past it and says so, rather than letting the vendor cut the feed off.
    max_symbols: int | None = None

    @abstractmethod
    def connect(self, on_tick, on_state) -> None:
        """
        Open the socket and stream until `close()`.

        on_tick(payload: dict): one normalised tick, ready for the API.
        on_state(event: str, detail: str): connected / authenticated / refused /
        disconnected, for the log and the heartbeat. The runner never has to
        parse a vendor's words to know what happened.
        """

    @abstractmethod
    def close(self) -> None:
        """Stop streaming. Must be safe to call when nothing is open."""

    @abstractmethod
    def subscribe(self, canonical_symbols: list[str]) -> list[str]:
        """
        Ask for these instruments, named canonically, and return the ones that
        were actually asked for.

        The return value is the point. A vendor can take fewer than it was
        given — a name it cannot derive, a symbol limit — and a runner that
        assumed otherwise would report every one of them as subscribed while
        prices for a third of them never arrived.
        """

    @abstractmethod
    def unsubscribe(self, canonical_symbols: list[str]) -> None:
        """Stop asking for these."""

    @abstractmethod
    def to_vendor(self, canonical_symbol: str) -> str | None:
        """
        This vendor's name for an instrument, or None when it cannot be derived.

        None is an answer, not a failure: the runner skips that symbol and says
        which, instead of subscribing to something it guessed at.
        """
