"""
What a data vendor's live feed has to provide — and nothing more.

A streamer is two things: the part that knows one vendor's socket and message
shape, and the part that knows this platform — its watchlist, the Redis stream
the strategies read, the API that stores ticks, the heartbeat the console shows,
and what to do when prices stop. The second part is `FeedRunner`, written once.
A vendor is only this contract.

The first TrueData feed re-wrote pieces of the second part and got three of
them wrong in one evening: it stored ticks but never published them to Redis,
so every strategy sat waiting; and it passed the replay's subscribe snapshot,
stamped with the wall clock, into a quote store that keeps the newest exchange
stamp — which froze every contract's price. None of that is a vendor's
business, and none of it can be got wrong by one any more.
"""

from abc import ABC, abstractmethod
from typing import Any, Callable


class FeedEvent:
    """What an adapter can tell the runner. Plain strings, so logs stay readable."""

    #: The socket is open. Not proof of data: a dead credential can connect.
    CONNECTED = "connected"
    #: The socket closed or was never opened.
    DISCONNECTED = "disconnected"
    #: The vendor accepted the login (for vendors that say so).
    AUTHENTICATED = "authenticated"
    #: The vendor refused the credential this socket was built with. The runner
    #: waits for a different one rather than reconnecting with the same.
    CREDENTIALS_REJECTED = "credentials-rejected"
    #: A refusal reconnecting cannot fix (another session holds the account, the
    #: subscription has lapsed). The runner backs off and reports it.
    REFUSED = "refused"
    #: A transport or protocol error worth logging.
    ERROR = "error"
    #: Informational: heartbeat, subscribe confirmation, market status, limits.
    INFO = "info"


class VendorFeed(ABC):
    """
    One vendor's live socket, behind a contract the runner drives.

    Implementations hold the socket and nothing else: no database, no API
    session, no Redis, no watchlist. They turn the vendor's messages into
    platform ticks and hand them to `on_ticks`.

    A platform tick is a dict in the shape the API's tick endpoint takes —
    symbol (canonical), exchangeTimestampUtc, lastTradedPrice, bidPrice,
    askPrice, bidSize, askSize, open, high, low, prevClose, volume,
    openInterest, rawPayload. One extra key is the adapter's to set:

      "snapshot": True — a picture of the instrument sent on request (a
      subscribe answer, a touchline), not a trade in time order. The runner
      decides what that means for storage; the adapter only says what it is.
    """

    #: The connector key. Written into SourceKey on every row this feed
    #: produces, so it must match the C# connector key.
    key: str = "unknown"

    #: The source name the console shows for this feed's heartbeat.
    source_name: str | None = None

    #: Redis key held while this feed runs. Two connections on one vendor
    #: account get one of them dropped (FYERS), or refused outright (TrueData).
    lock_key: str | None = None

    #: The vendor's ceiling on one connection, or None.
    max_symbols: int | None = None

    #: True when this feed replays a past session rather than streaming a live
    #: one. The runner then marks every tick as a replay (so the quote store
    #: takes it although it runs behind the day's live stamps), drops snapshots
    #: (they are not part of the session being replayed), and treats the
    #: session as open whatever the wall clock says.
    is_replay: bool = False

    #: The event after which the vendor accepts subscriptions. FYERS takes them
    #: as soon as the socket opens; TrueData only once it has accepted the login.
    ready_event: str = FeedEvent.CONNECTED

    def acquire_credentials(self, not_this: Any = None) -> Any:
        """
        Block until a usable credential exists and return it.

        `not_this` is the credential the vendor just refused. Returning it again
        would reconnect with something known not to work, forever — the dead
        FYERS token of 2026-09-10 — so an adapter whose credential can be
        replaced must wait for a different one. The default suits a vendor with
        a fixed login: there is nothing to wait for.
        """
        return None

    @abstractmethod
    def connect(self, credentials: Any,
                on_ticks: Callable[[list[dict]], None],
                on_event: Callable[[str, str], None]) -> None:
        """
        Open the socket and start streaming, without blocking.

        on_ticks(ticks): platform ticks, in arrival order.
        on_event(event, detail): a FeedEvent and a sentence for the log.
        """

    @abstractmethod
    def close(self) -> None:
        """Stop streaming. Safe to call when nothing is open."""

    @abstractmethod
    def subscribe(self, symbols: list[str]) -> list[str]:
        """
        Ask for these instruments, named canonically. Return the ones actually
        asked for — a vendor can take fewer (no name for one, a symbol limit),
        and a runner that assumed otherwise would report them all as subscribed.
        """

    def unsubscribe(self, symbols: list[str]) -> bool:
        """
        Stop asking for these. True when they are off the wire. False is not
        worth a reconnect: a symbol nobody reads costs bandwidth, a rebuild costs
        every other symbol its ticks.
        """
        return False

    def to_vendor(self, canonical_symbol: str) -> str | None:
        """This vendor's name for an instrument; the canonical one by default."""
        return canonical_symbol
