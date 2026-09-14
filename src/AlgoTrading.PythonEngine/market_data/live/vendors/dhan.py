"""
Dhan's live market feed — a binary websocket — as a VendorFeed.

Only what is Dhan's own: the login carried in the socket's query string, the
little-endian packets (one layout per response code), the segment + security id
that name an instrument instead of a symbol, the 100-instruments-per-request and
5000-per-connection limits, and the disconnect codes that say why a socket was
dropped. Storage, Redis, greeks, the watchlist and every watchdog come from
core.live.feed_runner.

Three things are Dhan's because of how much Dhan sends. Full mode carries about
ten packets a second per instrument (measured on MCX, 2026-09-14), and every
tick is stored through the API and put on the strategy stream — a few hundred
instruments at that rate would swamp a 2-vCPU server and its disk. So ticks
are conflated to one per instrument per DHAN_MIN_TICK_INTERVAL_MS, each carrying
the instrument's latest state; the five-level book stays out of the strategy
stream; and the extra instruments the API names (the universe) ride on the same
socket as the watchlist. The credential comes from the API, where the console's
daily Connect sign-in puts it, with .env as the fallback.

Layouts follow Dhan's v2 documentation and its Python SDK's struct formats.
Three things they leave open are handled defensively and named where they are
handled: whether a trade time is true UTC or IST wall-clock, the index packet's
layout, and whether one websocket frame can carry several packets.
"""

import json
import math
import os
import struct
import threading
import time
from datetime import datetime, timezone
from typing import NamedTuple
from urllib.parse import urlencode

import websocket

from core.api_client import build_session
from core.config import API_BASE_URL, ENV_FILE, VERIFY_SSL
from core.live.symbol_list import symbols_for
from core.live.vendor_feed import FeedEvent, VendorFeed

FEED_URL = "wss://api-feed.dhan.co"

#: The exchange segments, by the name requests use and the number packets carry.
SEGMENTS = {
    "IDX_I": 0,
    "NSE_EQ": 1,
    "NSE_FNO": 2,
    "NSE_CURRENCY": 3,
    "BSE_EQ": 4,
    "MCX_COMM": 5,
    "BSE_CURRENCY": 7,
    "BSE_FNO": 8,
}
_SEGMENT_BY_NUMBER = {number: name for name, number in SEGMENTS.items()}

#: Subscribe request codes. Unsubscribing is the same code plus one.
TICKER, QUOTE, FULL = 15, 17, 21

#: What each segment is subscribed as. Full is the only mode that carries the
#: book (best bid/ask and five levels) and puts OI in the same packet as the
#: price, which is what an option strategy prices against. An index has no
#: book and no OI, and an equity's book is not what anything here trades on, so
#: those take Quote: the same price, volume and day range at a third the size.
MODE_BY_SEGMENT = {
    "NSE_FNO": FULL,
    "BSE_FNO": FULL,
    "MCX_COMM": FULL,
    # Currency derivatives have a book and OI too; nothing trades them yet.
    "NSE_CURRENCY": FULL,
    "BSE_CURRENCY": FULL,
    "IDX_I": QUOTE,
    "NSE_EQ": QUOTE,
    "BSE_EQ": QUOTE,
}
DEFAULT_MODE = QUOTE

#: Dhan refuses a subscribe request naming more than this many instruments.
MAX_INSTRUMENTS_PER_REQUEST = 100

#: And the platform's resolve endpoint takes at most this many symbols a call.
MAX_SYMBOLS_PER_RESOLVE = 5000

#: At most one tick per instrument per this many milliseconds, unless
#: DHAN_MIN_TICK_INTERVAL_MS says otherwise. A second is finer than any bar the
#: platform builds and than any strategy here decides on, and it cuts Full
#: mode's ~10 packets a second to one row. 0 hands on every packet.
DEFAULT_MIN_TICK_INTERVAL_MS = 1000


class DhanInstrument(NamedTuple):
    """One instrument as Dhan names it: "NSE_FNO:47317:OPTIDX"."""

    segment: str
    security_id: int
    instrument: str

    @property
    def key(self) -> tuple[int, int]:
        """What a packet header carries: the segment's number and the security id."""
        return SEGMENTS[self.segment], self.security_id

    @property
    def vendor(self) -> str:
        return f"{self.segment}:{self.security_id}:{self.instrument}"


def parse_instrument(text) -> DhanInstrument | None:
    """ "SEGMENT:SECURITYID:INSTRUMENT", or None for anything else."""
    parts = str(text or "").strip().split(":")
    if len(parts) != 3:
        return None
    segment, security_id, instrument = (p.strip() for p in parts)
    if segment.upper() not in SEGMENTS or not security_id.isdigit() or not instrument:
        return None
    return DhanInstrument(segment.upper(), int(security_id), instrument.upper())


#: The indices, by canonical symbol. Fixed ids, the same table the API's resolve
#: endpoint uses, kept here so the indices subscribe with the API down. NIFTY's
#: option rows in the instrument master name their underlying 26000; the index
#: itself, on the feed, is 13.
INDEX_INSTRUMENTS = {
    "NSE:NIFTY50-INDEX": DhanInstrument("IDX_I", 13, "INDEX"),
    "NSE:NIFTYBANK-INDEX": DhanInstrument("IDX_I", 25, "INDEX"),
    "NSE:FINNIFTY-INDEX": DhanInstrument("IDX_I", 27, "INDEX"),
    "NSE:MIDCPNIFTY-INDEX": DhanInstrument("IDX_I", 442, "INDEX"),
    "BSE:SENSEX-INDEX": DhanInstrument("IDX_I", 51, "INDEX"),
    "BSE:BANKEX-INDEX": DhanInstrument("IDX_I", 69, "INDEX"),
}

# ------------------------------------------------------------------- packets

#: Response codes (header byte 0).
INDEX_PACKET = 1
TICKER_PACKET = 2
QUOTE_PACKET = 4
OI_PACKET = 5
PREV_CLOSE_PACKET = 6
MARKET_STATUS_PACKET = 7
FULL_PACKET = 8
DISCONNECT_PACKET = 50

_KNOWN_CODES = frozenset({INDEX_PACKET, TICKER_PACKET, QUOTE_PACKET, OI_PACKET, PREV_CLOSE_PACKET,
                          MARKET_STATUS_PACKET, FULL_PACKET, DISCONNECT_PACKET})

#: Every multi-byte field is little-endian. Quantities and ids are unsigned, as
#: in Dhan's SDK: a negative volume is not a thing, and reading one signed would
#: only turn an overflow into a plausible-looking wrong number.
_HEADER = struct.Struct("<BHBI")           # code, message length, segment, security id
HEADER_SIZE = _HEADER.size                  # 8
_TICKER = struct.Struct("<fI")              # LTP, LTT
_QUOTE = struct.Struct("<fHIfIIIffff")      # LTP, LTQ, LTT, ATP, volume, sell qty, buy qty, O, C, H, L
_OI = struct.Struct("<I")
_PREV_CLOSE = struct.Struct("<fI")          # previous close, previous-day OI
_FULL = struct.Struct("<fHIfIIIIIIffff")    # quote fields with OI, OI high, OI low before O/C/H/L
_DEPTH_LEVEL = struct.Struct("<IIHHff")     # bid qty, ask qty, bid orders, ask orders, bid, ask
_INDEX_FIELDS = struct.Struct("<fffffI")    # value, open, close, high, low, time — UNVERIFIED
_REASON = struct.Struct("<H")

#: The size of a packet whose size is fixed. The index and market-status packets
#: are not listed: their layouts are not pinned, so their length comes from the
#: header when it is plausible and from the frame when it is not.
PACKET_SIZES = {
    TICKER_PACKET: HEADER_SIZE + _TICKER.size,                          # 16
    QUOTE_PACKET: HEADER_SIZE + _QUOTE.size,                            # 50
    OI_PACKET: HEADER_SIZE + _OI.size,                                  # 12
    PREV_CLOSE_PACKET: HEADER_SIZE + _PREV_CLOSE.size,                  # 16
    FULL_PACKET: HEADER_SIZE + _FULL.size + 5 * _DEPTH_LEVEL.size,      # 162
    DISCONNECT_PACKET: HEADER_SIZE + _REASON.size,                      # 10
}

#: Prices arrive as float32, which holds about seven significant digits: 23456.05
#: comes back as 23456.05078125. The true price is a multiple of the tick size,
#: so rounding to the tick's decimals recovers it exactly. Equity, index, F&O and
#: MCX ticks are 0.01 or coarser, so two decimals; currency derivatives tick in
#: 0.0025, so four. (Above ~131072 float32 steps by 1/64, and two decimals can be
#: a cent out — only MCX prices are that large, and they tick in whole rupees.)
PRICE_DECIMALS = {"NSE_CURRENCY": 4, "BSE_CURRENCY": 4}
_DEFAULT_PRICE_DECIMALS = 2

#: Disconnect reasons -> what the runner should do about them.
DISCONNECT_REASONS = {
    805: (FeedEvent.REFUSED, "connection limit exceeded — this Dhan account already has its maximum "
                             "of sockets open; stop another client"),
    806: (FeedEvent.REFUSED, "Data APIs are not subscribed on this Dhan account — the data plan must be "
                             "active before the feed can carry anything"),
    807: (FeedEvent.CREDENTIALS_REJECTED, "access token expired"),
    808: (FeedEvent.CREDENTIALS_REJECTED, "authentication failed"),
    809: (FeedEvent.CREDENTIALS_REJECTED, "access token invalid"),
    810: (FeedEvent.CREDENTIALS_REJECTED, "client id invalid"),
}

#: What Dhan's trade times are ahead of the wall clock by, if they are IST
#: wall-clock time written as an epoch.
IST_OFFSET_SECONDS = 19800


def ltt_encodes_ist(ltt_epoch, now_utc: datetime) -> bool:
    """
    True when a trade time sits roughly 5h30m ahead of now.

    Dhan's SDK formats its trade times with timezone.utc as a time of day, which
    only reads right if the epoch holds IST wall-clock time rather than true UTC.
    That is not documented either way. No exchange stamps a trade four hours in
    the future, so a stamp that far ahead is proof of the IST reading; the window
    stops at seven hours so a stamp that is simply wrong is not taken as proof.
    """
    if not ltt_epoch:
        return False
    ahead = ltt_epoch - now_utc.timestamp()
    return 4 * 3600 < ahead < 7 * 3600


def ltt_to_utc(ltt_epoch, now_utc: datetime, ist: bool | None = None) -> str | None:
    """
    A Dhan trade time as ISO-8601 UTC, like every other adapter's stamp.

    `ist` is what the caller already knows about the encoding. None asks this one
    stamp — which only works on a fresh stamp: an IST-encoded trade from two hours
    ago no longer looks 5h30m ahead. DhanFeed therefore remembers the first proof
    and passes it on.
    """
    if not ltt_epoch or ltt_epoch <= 0:
        return None
    if ist is None:
        ist = ltt_encodes_ist(ltt_epoch, now_utc)
    seconds = ltt_epoch - IST_OFFSET_SECONDS if ist else ltt_epoch
    try:
        return datetime.fromtimestamp(seconds, tz=timezone.utc).isoformat().replace("+00:00", "Z")
    except (OverflowError, OSError, ValueError):
        return None


def _price(value, decimals):
    """A float32 price at its tick's precision. Zero is "no price", never a trade at zero."""
    if value is None or not math.isfinite(value) or value <= 0:
        return None
    return round(value, decimals)


def _decode_ticker(data, offset, size, decimals):
    ltp, ltt = _TICKER.unpack_from(data, offset + HEADER_SIZE)
    return {"ltp": _price(ltp, decimals), "ltt": ltt or None}


def _decode_quote(data, offset, size, decimals):
    ltp, ltq, ltt, atp, volume, sell, buy, day_open, day_close, high, low = \
        _QUOTE.unpack_from(data, offset + HEADER_SIZE)
    return {
        "ltp": _price(ltp, decimals), "ltq": ltq, "ltt": ltt or None, "atp": _price(atp, decimals),
        "volume": volume, "tot_sell_qty": sell, "tot_buy_qty": buy,
        # Dhan fills "close" only once the session has closed; it is not the
        # previous close, which comes in a packet of its own.
        "open": _price(day_open, decimals), "close": _price(day_close, decimals),
        "high": _price(high, decimals), "low": _price(low, decimals),
    }


def _decode_oi(data, offset, size, decimals):
    (oi,) = _OI.unpack_from(data, offset + HEADER_SIZE)
    return {"oi": oi}


def _decode_prev_close(data, offset, size, decimals):
    prev_close, prev_oi = _PREV_CLOSE.unpack_from(data, offset + HEADER_SIZE)
    return {"prev_close": _price(prev_close, decimals), "prev_oi": prev_oi}


def _decode_full(data, offset, size, decimals):
    (ltp, ltq, ltt, atp, volume, sell, buy, oi, oi_high, oi_low,
     day_open, day_close, high, low) = _FULL.unpack_from(data, offset + HEADER_SIZE)
    depth = []
    level_offset = offset + HEADER_SIZE + _FULL.size
    for _ in range(5):
        bid_qty, ask_qty, bid_orders, ask_orders, bid, ask = _DEPTH_LEVEL.unpack_from(data, level_offset)
        depth.append((bid_qty, bid_orders, _price(bid, decimals), _price(ask, decimals), ask_orders, ask_qty))
        level_offset += _DEPTH_LEVEL.size
    return {
        "ltp": _price(ltp, decimals), "ltq": ltq, "ltt": ltt or None, "atp": _price(atp, decimals),
        "volume": volume, "tot_sell_qty": sell, "tot_buy_qty": buy,
        "oi": oi, "oi_high": oi_high, "oi_low": oi_low,
        "open": _price(day_open, decimals), "close": _price(day_close, decimals),
        "high": _price(high, decimals), "low": _price(low, decimals),
        "depth": depth,
    }


def _decode_index(data, offset, size, decimals):
    """
    UNVERIFIED layout: value, open, close, high, low, update time. Read only as
    far as the packet is long, so a shorter real packet still yields its value
    rather than a struct error or bytes from the next packet.
    """
    body = size - HEADER_SIZE
    if body < 4:
        return None
    names = ("ltp", "open", "close", "high", "low")
    fields = {}
    for i, name in enumerate(names):
        if body >= 4 * (i + 1):
            (value,) = struct.unpack_from("<f", data, offset + HEADER_SIZE + 4 * i)
            fields[name] = _price(value, decimals)
    if body >= _INDEX_FIELDS.size:
        (ltt,) = struct.unpack_from("<I", data, offset + HEADER_SIZE + 20)
        fields["ltt"] = ltt or None
    return fields


_DECODERS = {
    TICKER_PACKET: ("ticker", _decode_ticker),
    QUOTE_PACKET: ("quote", _decode_quote),
    OI_PACKET: ("oi", _decode_oi),
    PREV_CLOSE_PACKET: ("prev_close", _decode_prev_close),
    FULL_PACKET: ("full", _decode_full),
    INDEX_PACKET: ("index", _decode_index),
}

#: Fields kept per instrument between packets. In Quote mode OI and the previous
#: close arrive in packets of their own, and a tick that dropped them because the
#: price came separately would store every option with no OI.
_STATE_FIELDS = ("ltp", "ltt", "open", "high", "low", "volume", "oi", "prev_close")

#: Decoded fields that have no column of their own, so they go to rawPayload.
#: They are merged per instrument like the columns: a conflated tick stands for
#: every packet since the last one, so a previous-close packet that happened to
#: arrive last must not leave it without the book the Full packet before it had.
_RAW_FIELDS = ("ltq", "atp", "tot_buy_qty", "tot_sell_qty", "close", "oi_high", "oi_low", "prev_oi")


def _looks_like_header(data, offset) -> bool:
    """Whether the bytes at offset could start another packet."""
    if len(data) - offset < HEADER_SIZE:
        return False
    code, _, segment, _ = _HEADER.unpack_from(data, offset)
    if code not in _KNOWN_CODES or segment not in _SEGMENT_BY_NUMBER:
        return False
    size = PACKET_SIZES.get(code)
    return size is None or len(data) - offset >= size


def _credentials_from_env_file():
    """
    The Dhan credential as .env holds it now — the fallback when the API has no
    session to give. Only the file can change under a running process — its
    environment was fixed when it started — so a token refreshed by pasting it
    into .env is found here without a restart.
    """
    try:
        from dotenv import dotenv_values
        values = dotenv_values(ENV_FILE)
    except Exception:
        return None
    client_id = (values.get("DHAN_CLIENT_ID") or os.getenv("DHAN_CLIENT_ID") or "").strip()
    token = (values.get("DHAN_ACCESS_TOKEN") or "").strip()
    return (client_id, token) if client_id and token else None


class DhanFeed(VendorFeed):
    """
    One Dhan feed socket: up to 5000 instruments, named by segment and security
    id. Dhan checks the login after the socket opens and answers a bad one with a
    disconnect packet, so a connect is not proof of a login — but there is no
    "accepted" message to wait for either, and subscriptions sent before the
    check are simply dropped with the socket.
    """

    key = "dhan"
    source_name = "python-dhan-feed"
    lock_key = "feed:dhan:lock"
    ready_event = FeedEvent.CONNECTED

    #: The rawPayload carries the five-level book. It is stored through the
    #: API, but no strategy prices against level five, and on the stream every
    #: consumer reads it would be most of every message.
    publish_raw_payload = False

    #: How often to look for a credential while there is none, or while only
    #: refused ones exist. Someone has to press Connect; a minute of slack
    #: either way is nothing, a request every second is noise in two logs.
    TOKEN_REPLACEMENT_POLL_SECONDS = 30

    #: How long the API's universe is used before asking again. It follows the
    #: money — strikes move as the index does — so it goes stale in minutes,
    #: not seconds.
    UNIVERSE_CACHE_SECONDS = 300

    #: How soon to ask again after the universe could not be fetched. Sooner
    #: than the cache, so an API back from a restart is picked up quickly;
    #: not every refresh, so an API that times out does not hold up the
    #: watchlist sync every five seconds.
    UNIVERSE_RETRY_SECONDS = 30

    def __init__(self, client_id=None, access_token=None, feed_url=FEED_URL, max_symbols=5000, vendor_names=None,
                 http=None, api_base_url=None, verify_ssl=None, credentials_source=None, now=None,
                 sleep=time.sleep, min_tick_interval_ms=DEFAULT_MIN_TICK_INTERVAL_MS, clock=None):
        # The credential this process was started with, if any. The API's
        # session and .env are asked first (see acquire_credentials); this is
        # the last resort, and the pair actually in use afterwards.
        self._client_id = (client_id or "").strip() or None
        self._access_token = (access_token or "").strip() or None
        self._env_credential = ((self._client_id, self._access_token)
                                if self._client_id and self._access_token else None)
        # Every credential Dhan has refused. Tokens last a day and a refused one
        # never comes back to life; remembering only the latest would swap
        # between two dead ones (yesterday's in .env, a stale one in the API).
        self._rejected: set[tuple[str, str]] = set()

        self._feed_url = (feed_url or FEED_URL).rstrip("/")
        self.max_symbols = max_symbols

        # Given names are configuration: a malformed one fails at startup, rather
        # than quietly sending that contract to the API to be looked up instead.
        self._given: dict[str, DhanInstrument] = {}
        malformed = []
        for canonical, vendor in (vendor_names or {}).items():
            instrument = parse_instrument(vendor)
            if instrument is None:
                malformed.append(canonical)
            else:
                self._given[canonical.strip().upper()] = instrument
        if malformed:
            raise ValueError(f"DHAN_SYMBOLS names {len(malformed)} symbol(s) with a Dhan name that is not "
                             f"SEGMENT:SECURITYID:INSTRUMENT: {', '.join(malformed)}")

        # The platform API: the credential, the universe, and every instrument
        # the index table and DHAN_SYMBOLS do not name. Built on first use, so a
        # feed that never needs it never signs in.
        self._http = http
        self._http_lock = threading.Lock()
        self._api = (api_base_url or API_BASE_URL).rstrip("/")
        self._verify = VERIFY_SSL if verify_ssl is None else verify_ssl
        self._resolved: dict[str, DhanInstrument] = {}
        self._reported_unresolved: set[str] = set()

        self._credentials_source = credentials_source or _credentials_from_env_file
        self._now = now or (lambda: datetime.now(timezone.utc))
        self._sleep = sleep
        # Monotonic seconds, for conflation and the universe cache. Injectable
        # so the tests step time instead of sleeping through it.
        self._clock = clock or time.monotonic

        # The universe: the last good list, when to ask again, and whether the
        # asking is currently failing (said once per run of failures).
        self._universe: list[str] = []
        self._universe_next_ask = None
        self._universe_failing = False
        self._universe_lock = threading.Lock()

        # Conflation. `_emit_lock` covers merging a packet into its instrument's
        # state, deciding whether it goes out, and handing it on — on the socket
        # thread and the flusher alike — so the runner is never called from two
        # threads at once and an instrument's ticks leave in the order they
        # were built.
        self._min_interval = max(int(min_tick_interval_ms or 0), 0) / 1000.0
        self._emit_lock = threading.Lock()
        self._last_emitted: dict[str, float] = {}           # canonical -> clock time
        self._held: dict[str, tuple[int, int]] = {}          # canonical -> key, updated since then
        self._flusher = None                                 # (thread, stop event)

        self._app = None
        self._closing = False
        # Bumped on every connect: a socket being torn down still calls back on
        # its own thread, and its "disconnected" must not land on its replacement.
        self._generation = 0

        self._subscribed: dict[str, DhanInstrument] = {}
        self._canonical_by_key: dict[tuple[int, int], str] = {}
        self._state: dict[tuple[int, int], dict] = {}
        # Latched on the first stamp that proves Dhan writes IST wall-clock time.
        self._ltt_is_ist = False
        # Things worth saying once per connection, not once per packet.
        self._said: set = set()
        self._lock = threading.Lock()

        self._on_ticks = None
        self._on_event = None

    @classmethod
    def from_env(cls):
        # Neither half of the credential is required: the console's daily
        # Connect puts it in the API, which is asked first. When set, these are
        # the fallback for an API that is down or has no session.
        client_id = os.getenv("DHAN_CLIENT_ID", "").strip()
        token = os.getenv("DHAN_ACCESS_TOKEN", "").strip()
        interval = os.getenv("DHAN_MIN_TICK_INTERVAL_MS", "").strip()
        try:
            interval_ms = int(interval) if interval else DEFAULT_MIN_TICK_INTERVAL_MS
            if interval_ms < 0:
                raise ValueError
        except ValueError:
            raise SystemExit(f"DHAN_MIN_TICK_INTERVAL_MS must be a whole number of milliseconds, 0 or more "
                             f"(0 hands on every packet); it is {interval!r}.")
        _, names = symbols_for(cls.key)
        try:
            return cls(client_id or None, token or None,
                       feed_url=os.getenv("DHAN_FEED_URL", "").strip() or FEED_URL,
                       vendor_names=names, min_tick_interval_ms=interval_ms)
        except ValueError as ex:
            raise SystemExit(str(ex))

    @property
    def url(self) -> str:
        """The feed's address without the login — the only form that is ever logged."""
        return self._feed_url

    def _session(self):
        """The platform API session, built once, by whichever thread needs it first."""
        with self._http_lock:
            if self._http is None:
                self._http = build_session()
            return self._http

    # ------------------------------------------------------------ credentials

    def acquire_credentials(self, not_this=None):
        """
        (client id, access token): the API's Dhan session first — the console's
        daily Connect sign-in puts a fresh token there — then .env as it is now,
        then the environment this process started with.

        A Dhan token lasts a day, and reconnecting with one Dhan has refused
        would be refused again every few seconds. So a refused credential is
        never offered again, from any source, and while nothing else exists this
        asks the API and .env every TOKEN_REPLACEMENT_POLL_SECONDS, saying so
        once. The same wait covers a first start with no credential anywhere.
        """
        if not_this:
            self._rejected.add(tuple(not_this))
        waiting_said = False
        while True:
            reasons = []
            for credential, where in self._credentials(reasons):
                if credential in self._rejected:
                    reasons.append(f"the one from {where} was already refused")
                    continue
                self._client_id, self._access_token = credential
                # Redacted although it names only a source and an expiry: the
                # text comes from the API, and the token must not reach a log
                # even if the API misbehaves.
                print(f"[dhan] using the Dhan credential from {self._redact(where)}.", flush=True)
                return credential
            if not waiting_said:
                print(f"[dhan] no usable Dhan credential ({self._redact('; '.join(reasons)) or 'none found'}) — press Connect "
                      f"on the Dhan connector page, or put DHAN_CLIENT_ID and DHAN_ACCESS_TOKEN in .env. "
                      f"Checking again every {self.TOKEN_REPLACEMENT_POLL_SECONDS}s.", flush=True)
                waiting_said = True
            self._sleep(self.TOKEN_REPLACEMENT_POLL_SECONDS)

    def _credentials(self, reasons):
        """
        Yields (credential, where it came from), freshest source first, asking
        each only when the one before gave nothing usable. Why a source gave
        nothing goes into `reasons`, for the one line said while waiting.
        """
        from_api, detail = self._credentials_from_api()
        if from_api is not None:
            yield from_api, detail
        else:
            reasons.append(detail)

        try:
            from_file = self._credentials_source()
        except Exception as ex:
            from_file = None
            reasons.append(f".env could not be read: {self._redact(ex)}")
        if from_file:
            yield tuple(from_file), ".env"
        else:
            reasons.append(".env has no Dhan credential")

        if self._env_credential is not None:
            yield self._env_credential, "the environment the feed started with"

    def _credentials_from_api(self):
        """
        ((client id, token), where) from GET /api/Dhan/session, or (None, why
        not). Anything short of both halves — the API down, 404 because nobody
        has signed in and no token is configured, an odd body — is None, and
        .env is asked next. `where` names the API's source and expiry (neither
        is a secret) so the log says which token the feed is running on.
        """
        try:
            response = self._session().get(f"{self._api}/api/Dhan/session", verify=self._verify, timeout=15)
            if response.status_code == 404:
                return None, "the API has no Dhan session"
            response.raise_for_status()
            body = response.json()
            client_id = str(body.get("clientId") or "").strip()
            token = str(body.get("accessToken") or "").strip()
            source, expires = body.get("source"), body.get("expiresUtc")
        except Exception as ex:
            return None, f"the API could not be asked for the Dhan session ({self._redact(ex)})"
        if not client_id or not token:
            return None, "the API's Dhan session has no client id or access token"
        where = f"the API ({source or 'unknown source'}" + (f", expires {expires}" if expires else "") + ")"
        return (client_id, token), where

    # --------------------------------------------------------------- universe

    def extra_symbols(self):
        """
        What the API says a Dhan feed should carry beyond the watchlist
        (GET /api/Dhan/universe): the indices, index futures, the strikes
        around the money on the nearest expiries, MCX futures and crude options
        near the money. It follows the market, so it is asked for again every
        UNIVERSE_CACHE_SECONDS. A failed ask answers with the last good list —
        an empty one would unsubscribe all of it mid-session — and is said once
        per run of failures, not on every refresh.
        """
        with self._universe_lock:
            now = self._clock()
            if self._universe_next_ask is not None and now < self._universe_next_ask:
                return list(self._universe)
            try:
                response = self._session().get(f"{self._api}/api/Dhan/universe", verify=self._verify, timeout=10)
                response.raise_for_status()
                body = response.json()
                symbols = body.get("symbols") if isinstance(body, dict) else None
                if not isinstance(symbols, list):
                    raise ValueError("the answer carries no symbols list")
            except Exception as ex:
                self._universe_next_ask = now + self.UNIVERSE_RETRY_SECONDS
                if not self._universe_failing:
                    self._universe_failing = True
                    print(f"[dhan] could not get the Dhan universe from the API ({self._redact(ex)}) — keeping "
                          f"the last {len(self._universe)} symbol(s), asking again every "
                          f"{self.UNIVERSE_RETRY_SECONDS}s.", flush=True)
                return list(self._universe)

            universe = list(dict.fromkeys(s.strip() for s in symbols if isinstance(s, str) and s.strip()))
            self._universe_next_ask = now + self.UNIVERSE_CACHE_SECONDS
            if self._universe_failing or universe != self._universe:
                # Said when it changes, with the API's own warnings (an
                # underlying it had no spot for, say): a strike list that is
                # quietly short is otherwise only noticed when a price is missing.
                counts, warnings = body.get("counts"), body.get("warnings")
                print(f"[dhan] universe: {len(universe)} symbol(s) from the API"
                      + (f" {counts}" if isinstance(counts, dict) and counts else "")
                      + (f"; warnings: {warnings}" if isinstance(warnings, list) and warnings else "")
                      + (" — the API answers again" if self._universe_failing else "") + ".", flush=True)
            self._universe_failing = False
            self._universe = universe
            return list(universe)

    # -------------------------------------------------------------- the socket

    def connect(self, credentials, on_ticks, on_event):
        client_id, token = credentials
        self._on_ticks = on_ticks
        self._on_event = on_event
        self._closing = False
        self._said = set()
        self._generation += 1
        generation = self._generation

        def live(handler):
            def wrapped(*args):
                if generation == self._generation and not self._closing:
                    handler(*args)
            return wrapped

        query = urlencode({"version": 2, "token": token, "clientId": client_id, "authType": 2})
        # No client pings: Dhan pings every 10 s and websocket-client answers
        # each with a pong on its own.
        self._app = websocket.WebSocketApp(
            f"{self._feed_url}?{query}",
            on_open=live(lambda _: on_event(FeedEvent.CONNECTED, self.url)),
            on_message=live(lambda _, raw: self._on_message(raw)),
            on_error=live(lambda _, err: self._on_error(err)),
            on_close=live(lambda _, code, msg: on_event(FeedEvent.DISCONNECTED,
                                                        self._redact(f"{code} {msg}"))),
        )
        self._start_flusher()
        threading.Thread(target=self._app.run_forever, name="dhan-socket", daemon=True).start()

    def close(self):
        self._closing = True
        self._stop_flusher()
        with self._emit_lock:
            # Every instrument still inside its interval gets its last state
            # handed on: those packets arrived, and a price that moved and then
            # went quiet would otherwise be stored a step behind until it next
            # traded — possibly tomorrow. The next connection starts each
            # instrument's interval afresh.
            try:
                self._deliver(self._take_held(everything=True))
            except Exception as ex:
                print(f"[dhan] could not hand on the last held ticks while closing: {self._redact(ex)}", flush=True)
            self._last_emitted.clear()
        with self._lock:
            self._subscribed.clear()
            self._canonical_by_key.clear()
        if self._app is not None:
            try:
                self._app.close()
            except Exception:
                pass

    # ------------------------------------------------------------ conflation

    def _start_flusher(self):
        """One flusher per connection, and none when every packet goes straight out."""
        self._stop_flusher()
        if self._min_interval <= 0:
            return
        stop = threading.Event()
        thread = threading.Thread(target=self._flush_until, args=(stop,), name="dhan-conflation", daemon=True)
        self._flusher = (thread, stop)
        thread.start()

    def _stop_flusher(self):
        flusher, self._flusher = self._flusher, None
        if flusher is None:
            return
        thread, stop = flusher
        stop.set()
        if thread is not threading.current_thread():
            # Bounded: a runner stuck on a dead Redis must not hang the close.
            thread.join(timeout=2)

    def _flush_until(self, stop):
        """
        Hands on the instruments whose interval has run out since their last
        update — the ones that moved and then went quiet, which no later packet
        would ever carry out. Wakes ten times an interval (at most every 100 ms),
        so a held state leaves at most that late.
        """
        period = min(max(self._min_interval / 10, 0.01), 0.1)
        while not stop.wait(period):
            try:
                self.flush_due()
            except Exception as ex:
                self._once(("flush-failed",), FeedEvent.ERROR,
                           f"could not hand on conflated ticks: {self._redact(ex)}")

    def flush_due(self) -> int:
        """Hand on every held instrument whose interval has elapsed. Returns how many."""
        with self._emit_lock:
            ticks = self._take_held(everything=False)
            self._deliver(ticks)
            return len(ticks)

    def _take_held(self, everything):
        """Ticks for the held instruments that are due (or all of them). Call under _emit_lock."""
        if not self._held:
            return []
        now = self._clock()
        ticks = []
        for canonical, key in list(self._held.items()):
            last = self._last_emitted.get(canonical)
            if not everything and last is not None and now - last < self._min_interval:
                continue
            del self._held[canonical]
            state = self._state.get(key)
            if state and state.get("ltp") is not None:
                self._last_emitted[canonical] = now
                ticks.append(self._tick(canonical, state))
        return ticks

    def _deliver(self, ticks):
        if ticks and self._on_ticks is not None:
            self._on_ticks(ticks)

    def _on_error(self, err):
        # A handshake refused outright (rather than a disconnect packet after the
        # upgrade) is how a server usually says the query-string login is bad.
        status = getattr(err, "status_code", None)
        detail = self._redact(str(err))
        if status == 401:
            self._event(FeedEvent.CREDENTIALS_REJECTED, f"handshake refused with HTTP 401: {detail}")
        else:
            self._event(FeedEvent.ERROR, detail)

    def _redact(self, text):
        """Transport errors can quote the request. The login never reaches a log."""
        text = str(text)
        secrets = {self._access_token, self._client_id}
        for client_id, token in self._rejected:
            secrets.update((client_id, token))
        # Longest first, so a client id inside a token cannot leave the rest of
        # the token behind.
        for secret in sorted((s for s in secrets if s), key=len, reverse=True):
            text = text.replace(secret, "***")
        return text

    # ------------------------------------------------------------ subscribing

    def to_vendor(self, canonical_symbol):
        instrument = self._known(canonical_symbol)
        return instrument.vendor if instrument else None

    def _known(self, canonical):
        upper = (canonical or "").strip().upper()
        return INDEX_INSTRUMENTS.get(upper) or self._given.get(upper) or self._resolved.get(upper)

    def _resolve(self, symbols):
        """
        {canonical: DhanInstrument} for the symbols that have one: the index
        table, then DHAN_SYMBOLS, then the platform's instrument mapping. Only
        the API's answers are cached — a contract's security id never changes —
        and a symbol it could not map is asked again after a reconnect, since
        the instrument master is re-imported daily.
        """
        found, ask = {}, []
        for canonical in symbols:
            instrument = self._known(canonical)
            if instrument is not None:
                found[canonical] = instrument
            else:
                ask.append(canonical)

        unresolved = []
        for start in range(0, len(ask), MAX_SYMBOLS_PER_RESOLVE):
            chunk = ask[start:start + MAX_SYMBOLS_PER_RESOLVE]
            try:
                response = self._session().post(f"{self._api}/api/Dhan/instruments/resolve",
                                           json={"symbols": chunk}, verify=self._verify, timeout=15)
                response.raise_for_status()
                answer = {str(k).strip().upper(): v for k, v in ((response.json() or {}).get("resolved") or {}).items()}
            except Exception as ex:
                # Not the same as unresolved: the symbols may well exist. They are
                # not taken now and are offered again on the next reconnect.
                self._event(FeedEvent.ERROR, f"could not resolve {len(chunk)} symbol(s) to Dhan instruments "
                                             f"through the API: {ex}")
                continue
            for canonical in chunk:
                instrument = parse_instrument(answer.get(canonical.strip().upper()))
                if instrument is None:
                    unresolved.append(canonical)
                else:
                    self._resolved[canonical.strip().upper()] = instrument
                    found[canonical] = instrument

        new = [s for s in unresolved if s.strip().upper() not in self._reported_unresolved]
        if new:
            self._reported_unresolved.update(s.strip().upper() for s in new)
            self._event(FeedEvent.INFO, f"{len(new)} symbol(s) have no Dhan instrument and were not asked for: "
                                        f"{_shown(new)}")
        return found

    def subscribe(self, symbols):
        if not symbols:
            return []
        found = self._resolve(list(dict.fromkeys(symbols)))
        if not found:
            return []

        with self._lock:
            already, wanted, clashes = [], {}, []
            claimed = dict(self._canonical_by_key)
            for canonical, instrument in found.items():
                if self._subscribed.get(canonical) == instrument:
                    already.append(canonical)
                    continue
                holder = claimed.get(instrument.key)
                if holder is not None and holder != canonical:
                    # A packet names an instrument, not a symbol: two names for
                    # one security id could not both be told apart.
                    clashes.append(f"{canonical} (same instrument as {holder})")
                    continue
                claimed[instrument.key] = canonical
                wanted[canonical] = instrument

            if clashes:
                self._event(FeedEvent.INFO, f"{len(clashes)} symbol(s) not asked for: {_shown(clashes)}")

            room = self.max_symbols - len(self._subscribed) if self.max_symbols else len(wanted)
            if len(wanted) > max(room, 0):
                left_out = list(wanted)[max(room, 0):]
                self._event(FeedEvent.INFO, f"only {max(room, 0)} of {len(wanted)} fit under the "
                                            f"{self.max_symbols}-instrument limit; left out: {_shown(left_out)}")
                wanted = dict(list(wanted.items())[:max(room, 0)])

            # Mapped before the request goes out: the first packets can arrive
            # before send() returns.
            for canonical, instrument in wanted.items():
                self._subscribed[canonical] = instrument
                self._canonical_by_key[instrument.key] = canonical

        unsent = set()
        for code, batch in _requests(wanted, subscribe=True):
            if not self._send(_request(code, [instrument for _, instrument in batch])):
                unsent.update(canonical for canonical, _ in batch)
        if unsent:
            with self._lock:
                for canonical in unsent:
                    instrument = self._subscribed.pop(canonical, None)
                    if instrument is not None:
                        self._canonical_by_key.pop(instrument.key, None)

        return [s for s in found if s in already or (s in wanted and s not in unsent)]

    def unsubscribe(self, symbols):
        with self._lock:
            leaving = {s: self._subscribed[s] for s in dict.fromkeys(symbols or []) if s in self._subscribed}
        if not leaving:
            return True
        all_sent = True
        for code, batch in _requests(leaving, subscribe=False):
            if not self._send(_request(code, [instrument for _, instrument in batch])):
                all_sent = False
                continue
            with self._lock:
                for canonical, instrument in batch:
                    self._subscribed.pop(canonical, None)
                    self._canonical_by_key.pop(instrument.key, None)
        return all_sent

    def _send(self, message) -> bool:
        if self._app is None or self._app.sock is None:
            return False
        try:
            self._app.send(json.dumps(message))
            return True
        except Exception as ex:
            self._event(FeedEvent.ERROR, f"could not send request {message.get('RequestCode')}: "
                                         f"{self._redact(ex)}")
            return False

    # --------------------------------------------------------------- messages

    def _on_message(self, raw):
        if isinstance(raw, str):
            self._once(("text",), FeedEvent.INFO, f"unexpected text message: {self._redact(raw[:120])}")
            return
        data = bytes(raw)
        if len(data) < HEADER_SIZE:
            self._once(("short",), FeedEvent.ERROR, f"a {len(data)}-byte frame is shorter than a packet header")
            return

        # Held from the first packet's merge to the hand-off, so the flusher
        # cannot slip a tick for the same instrument in between.
        with self._emit_lock:
            ticks = []
            offset = 0
            while True:
                remaining = len(data) - offset
                code, declared, segment, security_id = _HEADER.unpack_from(data, offset)
                size = PACKET_SIZES.get(code)
                if size is None:
                    size = declared if HEADER_SIZE <= declared <= remaining else remaining
                elif remaining < size:
                    self._once(("truncated", code), FeedEvent.ERROR,
                               f"a code-{code} packet arrived with {remaining} bytes; it needs {size}")
                    break

                self._handle(code, segment, security_id, data, offset, size, ticks)
                offset += size
                if offset >= len(data):
                    break
                # Dhan documents one packet per message. Should it ever stack
                # them, the rest is read too — but only when it starts like a
                # packet, so padding or a longer layout is never read as prices.
                if not _looks_like_header(data, offset):
                    self._once(("trailing", code), FeedEvent.INFO,
                               f"{len(data) - offset} byte(s) after a code-{code} packet are not another packet; "
                               f"ignored")
                    break

            self._deliver(ticks)

    def _handle(self, code, segment, security_id, data, offset, size, ticks):
        if code == DISCONNECT_PACKET:
            (reason,) = _REASON.unpack_from(data, offset + HEADER_SIZE)
            event, meaning = DISCONNECT_REASONS.get(reason, (FeedEvent.ERROR, None))
            self._event(event, f"Dhan disconnected the feed: {reason} {meaning}" if meaning
                        else f"Dhan disconnected the feed with unknown reason code {reason}")
            return
        if code == MARKET_STATUS_PACKET:
            self._once(("market-status",), FeedEvent.INFO, "market status packet received (body not decoded)")
            return
        if code not in _DECODERS:
            self._once(("unknown", code), FeedEvent.ERROR,
                       f"unknown response code {code} ({size} bytes: {data[offset:offset + 16].hex()})")
            return

        key = (segment, security_id)
        canonical = self._canonical_by_key.get(key)
        if canonical is None:
            # A price for something not asked for cannot be stored under a
            # symbol we trust.
            return

        kind, decode = _DECODERS[code]
        decimals = PRICE_DECIMALS.get(_SEGMENT_BY_NUMBER.get(segment), _DEFAULT_PRICE_DECIMALS)
        fields = decode(data, offset, size, decimals)
        if fields is None:
            self._once(("undecodable", code), FeedEvent.ERROR,
                       f"a code-{code} packet of {size} bytes is too short to read")
            return

        state = self._state.setdefault(key, {})
        for name in _STATE_FIELDS:
            if fields.get(name) is not None:
                state[name] = fields[name]
        raw = state.setdefault("raw", {})
        raw["type"] = kind
        for name in _RAW_FIELDS:
            if fields.get(name) is not None:
                raw[name] = fields[name]
        if code == FULL_PACKET:
            # The book replaces the book: an emptied side is None, not the last
            # price someone was once bidding.
            bid_qty, _, bid, ask, _, ask_qty = fields["depth"][0]
            state["bid"], state["ask"] = bid, ask
            state["bid_qty"] = bid_qty if bid is not None else None
            state["ask_qty"] = ask_qty if ask is not None else None
            depth = [list(level) for level in fields["depth"]]
            while depth and not any(depth[-1]):
                depth.pop()
            if depth:
                raw["depth"] = depth
            else:
                raw.pop("depth", None)

        if state.get("ltp") is None:
            # OI or a previous close before any price: kept for the first tick.
            return

        if self._min_interval <= 0:
            ticks.append(self._tick(canonical, state))
            return
        # Conflation: the first update after a quiet interval goes out at once,
        # so a move out of calm is not delayed at all; updates inside the
        # interval only mark the instrument, and the flusher sends its state as
        # it stands when the interval runs out — at most one interval (plus a
        # wake-up) late, never lost.
        now = self._clock()
        last = self._last_emitted.get(canonical)
        if last is None or now - last >= self._min_interval:
            self._last_emitted[canonical] = now
            self._held.pop(canonical, None)
            ticks.append(self._tick(canonical, state))
        else:
            self._held[canonical] = key

    def _tick(self, canonical, state):
        """A platform tick from an instrument's merged state, as it stands now."""
        return {
            "symbol": canonical,
            "dataType": "symbolUpdate",
            # An OI or previous-close packet has no time of its own; the tick it
            # produces carries the last trade's.
            "exchangeTimestampUtc": self._stamp(state.get("ltt")),
            "lastTradedPrice": state["ltp"],
            "bidPrice": state.get("bid"),
            "askPrice": state.get("ask"),
            "bidSize": state.get("bid_qty"),
            "askSize": state.get("ask_qty"),
            "open": state.get("open"),
            "high": state.get("high"),
            "low": state.get("low"),
            "prevClose": state.get("prev_close"),
            "volume": state.get("volume"),
            # An index has no OI and sends zero: unknown, not "all positions closed".
            "openInterest": state.get("oi") or None,
            # Serialised now: the state keeps changing after this tick has gone.
            "rawPayload": json.dumps(state.get("raw") or {}, separators=(",", ":")),
        }

    def _stamp(self, ltt):
        if not ltt:
            return None
        now = self._now()
        if not self._ltt_is_ist and ltt_encodes_ist(ltt, now):
            self._ltt_is_ist = True
            self._event(FeedEvent.INFO, "Dhan's trade times run 5h30m ahead of UTC — reading them as IST "
                                        "wall-clock time from now on")
        return ltt_to_utc(ltt, now, ist=self._ltt_is_ist)

    def _once(self, what, event, detail):
        if what in self._said:
            return
        self._said.add(what)
        self._event(event, detail)

    def _event(self, event, detail=""):
        if self._on_event is not None:
            self._on_event(event, detail)


def _requests(instruments: dict, subscribe: bool):
    """(request code, [(canonical, instrument), ...]) per mode, at most 100 per request."""
    by_code: dict[int, list] = {}
    for canonical, instrument in instruments.items():
        code = MODE_BY_SEGMENT.get(instrument.segment, DEFAULT_MODE)
        by_code.setdefault(code if subscribe else code + 1, []).append((canonical, instrument))
    for code, items in by_code.items():
        for start in range(0, len(items), MAX_INSTRUMENTS_PER_REQUEST):
            yield code, items[start:start + MAX_INSTRUMENTS_PER_REQUEST]


def _request(code, instruments):
    # The segment goes by name and the security id as a string: Dhan's request
    # format, although packets carry both as numbers.
    return {
        "RequestCode": code,
        "InstrumentCount": len(instruments),
        "InstrumentList": [{"ExchangeSegment": i.segment, "SecurityId": str(i.security_id)}
                           for i in instruments],
    }


def _shown(names):
    return ", ".join(names[:8]) + (f" (+{len(names) - 8} more)" if len(names) > 8 else "")
