"""
TrueData's live socket — real time or the evening replay — as a VendorFeed.

Only what is TrueData's own: the login in the socket's query string, its
message shapes (a touchline row and a trade row, in different field orders),
its symbol grammar, its 50-symbol trial limit, and the one-session-per-account
refusal. Storage, Redis, greeks, the watchlist and every watchdog come from
core.live.feed_runner.
"""

import json
import os
import threading
from datetime import datetime, timedelta, timezone

import websocket

from core.live.symbol_list import symbols_for
from core.live.truedata_symbols import to_vendor
from core.live.vendor_feed import FeedEvent, VendorFeed

#: The exchange's clock. Every TrueData timestamp is in it.
_IST_OFFSET = timedelta(hours=5, minutes=30)

#: The "trade" and "bidask" rows, in the order TrueData sends them (v2.6, 4.b.viii).
TRADE_FIELDS = (
    "symbol_id", "timestamp", "ltp", "ltq", "atp", "volume",
    "open", "high", "low", "prev_close", "oi", "prev_oi_close",
    "turnover", "special_tag", "tick_seq", "bid", "bid_qty", "ask", "ask_qty",
)

#: A touchline / "symbols added" row: a different order, two fields longer at the
#: front. Reading one with the other's layout puts a symbol id where a price
#: belongs, so they are named separately rather than sliced by index.
TOUCHLINE_FIELDS = (
    "symbol", "symbol_id", "timestamp", "ltp", "ltq", "atp", "volume",
    "open", "high", "low", "prev_close", "oi", "prev_oi_close",
    "turnover", "bid", "bid_qty", "ask", "ask_qty",
)

#: Fields already stored in a column of their own, dropped from rawPayload.
_FIELDS_ALREADY_COLUMNS = frozenset({
    "symbol", "symbol_id", "timestamp", "ltp", "volume",
    "open", "high", "low", "prev_close", "bid", "ask", "bid_qty", "ask_qty",
})


def _number(value):
    """TrueData sends numbers as strings. Absent and unparseable are None."""
    if value in (None, "", "-"):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _integer(value):
    number = _number(value)
    return int(number) if number is not None else None


def _timestamp_utc(stamp):
    """The exchange's clock, stored as UTC: the 5:30 comes off here."""
    if not stamp:
        return None
    try:
        ist = datetime.fromisoformat(str(stamp))
    except ValueError:
        return None
    if ist.tzinfo is None:
        ist = ist.replace(tzinfo=timezone(_IST_OFFSET))
    return ist.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


class TrueDataFeed(VendorFeed):
    """
    One TrueData socket. TrueData allows one session per account: a second is
    refused with "User Already Connected" rather than displacing the first, and
    that is reported as a refusal, because the fix is to stop the other client.
    """

    key = "truedata"
    lock_key = "feed:truedata:lock"
    ready_event = FeedEvent.AUTHENTICATED

    def __init__(self, username, password, host="push.truedata.in", port=8086, max_symbols=50,
                 vendor_names=None):
        self._username = username
        self._password = password
        self._host = host
        self._port = port
        self.max_symbols = max_symbols
        self.is_replay = host.startswith("replay.")
        self.source_name = "python-truedata-recap" if self.is_replay else "python-truedata-feed"
        self._given_names = {k.upper(): v for k, v in (vendor_names or {}).items()}

        self._app = None
        self._closing = False
        # Bumped on every connect: a socket being torn down still calls back on
        # its own thread, and its "disconnected" must not land on its replacement.
        self._generation = 0

        self._canonical_by_id: dict[str, str] = {}
        self._vendor_to_canonical: dict[str, str] = {}
        self._subscribed: set[str] = set()
        self._lock = threading.Lock()

        self._on_ticks = None
        self._on_event = None

    @classmethod
    def from_env(cls):
        username = os.getenv("TRUEDATA_USERNAME", "").strip()
        password = os.getenv("TRUEDATA_PASSWORD", "").strip()
        if not username or not password:
            raise SystemExit("TRUEDATA_USERNAME and TRUEDATA_PASSWORD are not set. Add them to .env — "
                             "they are never stored in the repository.")
        _, names = symbols_for(cls.key)
        return cls(
            username, password,
            host=os.getenv("TRUEDATA_HOST", "push.truedata.in").strip() or "push.truedata.in",
            # 8086 sandbox, 8084 production, 8082 the evening replay.
            port=int(os.getenv("TRUEDATA_REALTIME_PORT", "8086")),
            vendor_names=names,
        )

    @property
    def url(self) -> str:
        return f"wss://{self._host}:{self._port}"

    def acquire_credentials(self, not_this=None):
        # A fixed login: there is nothing different to wait for.
        return (self._username, self._password)

    # -------------------------------------------------------------- the socket

    def connect(self, credentials, on_ticks, on_event):
        username, password = credentials
        self._on_ticks = on_ticks
        self._on_event = on_event
        self._closing = False
        self._generation += 1
        generation = self._generation

        def live(handler):
            def wrapped(*args):
                if generation == self._generation and not self._closing:
                    handler(*args)
            return wrapped

        self._app = websocket.WebSocketApp(
            f"{self.url}?user={username}&password={password}",
            on_open=live(lambda _: on_event(FeedEvent.CONNECTED, self.url)),
            on_message=live(lambda _, raw: self._on_message(raw)),
            on_error=live(lambda _, err: on_event(FeedEvent.ERROR, str(err))),
            on_close=live(lambda _, code, msg: on_event(FeedEvent.DISCONNECTED, f"{code} {msg}")),
        )
        threading.Thread(target=self._app.run_forever, name="truedata-socket", daemon=True,
                         kwargs={"ping_interval": 30, "ping_timeout": 10}).start()

    def close(self):
        self._closing = True
        with self._lock:
            self._subscribed.clear()
        if self._app is not None:
            try:
                self._app.close()
            except Exception:
                pass

    # ------------------------------------------------------------ subscribing

    def to_vendor(self, canonical_symbol):
        return self._given_names.get((canonical_symbol or "").upper()) or to_vendor(canonical_symbol)

    def subscribe(self, symbols):
        wanted, underivable = {}, []
        for canonical in symbols:
            vendor = self.to_vendor(canonical)
            if vendor:
                wanted[vendor] = canonical
            else:
                underivable.append(canonical)

        if underivable:
            shown = ", ".join(underivable[:8]) + (f" (+{len(underivable) - 8} more)" if len(underivable) > 8 else "")
            self._event(FeedEvent.INFO, f"{len(underivable)} symbol(s) have no TrueData name "
                                        f"(futures and monthly options need a given name): {shown}")
        if not wanted:
            return []

        with self._lock:
            room = self.max_symbols - len(self._subscribed) if self.max_symbols else len(wanted)
            if room <= 0:
                self._event(FeedEvent.INFO, f"at the {self.max_symbols}-symbol limit; "
                                            f"{len(wanted)} more were not asked for")
                return []
            if len(wanted) > room:
                left_out = [wanted[v] for v in list(wanted)[room:]]
                shown = ", ".join(left_out[:8]) + (f" (+{len(left_out) - 8} more)" if len(left_out) > 8 else "")
                self._event(FeedEvent.INFO, f"only {room} of {len(wanted)} fit under the "
                                            f"{self.max_symbols}-symbol limit; left out: {shown}")
                wanted = dict(list(wanted.items())[:room])
            self._vendor_to_canonical.update(wanted)
            self._subscribed.update(wanted)

        self._send({"method": "addsymbol", "symbols": list(wanted)})
        return list(wanted.values())

    def unsubscribe(self, symbols):
        vendors = [v for v in (self.to_vendor(c) for c in symbols) if v]
        if not vendors:
            return True
        with self._lock:
            self._subscribed.difference_update(vendors)
        self._send({"method": "removesymbol", "symbols": vendors})
        return True

    def _send(self, message):
        if self._app is None or self._app.sock is None:
            return
        try:
            self._app.send(json.dumps(message))
        except Exception as ex:
            self._event(FeedEvent.ERROR, f"could not send {message.get('method')}: {ex}")

    # --------------------------------------------------------------- messages

    def _on_message(self, raw):
        try:
            message = json.loads(raw)
        except (TypeError, ValueError):
            self._event(FeedEvent.ERROR, f"unreadable message: {str(raw)[:120]}")
            return

        if "trade" in message:
            self._emit(dict(zip(TRADE_FIELDS, message["trade"])), "trade")
            return
        if "bidask" in message:
            self._emit(dict(zip(TRADE_FIELDS, message["bidask"])), "bidask")
            return

        text = str(message.get("message", "")).lower()
        if text == "heartbeat":
            self._event(FeedEvent.INFO, f"heartbeat {message.get('timestamp', '')}")
            return

        if "symbollist" in message:
            for row in message.get("symbollist") or []:
                fields = dict(zip(TOUCHLINE_FIELDS, row))
                canonical = self._vendor_to_canonical.get(fields.get("symbol"))
                symbol_id = str(fields.get("symbol_id") or "")
                if canonical and symbol_id:
                    with self._lock:
                        self._canonical_by_id[symbol_id] = canonical
                if canonical:
                    # A picture sent on request, not a trade in time order. What
                    # that means for storage is the runner's decision.
                    self._emit(fields, "touchline", canonical=canonical, snapshot=True)
            self._event(FeedEvent.INFO, f"subscribed {message.get('symbolsadded', 0)} symbol(s); "
                                        f"{message.get('totalsymbolsubscribed', 0)} total")
            return

        if message.get("success") is True and "real time data service" in text:
            vendor_max = message.get("maxsymbols")
            if isinstance(vendor_max, int) and vendor_max > 0:
                self.max_symbols = vendor_max
            self._event(FeedEvent.AUTHENTICATED,
                        f"segments={message.get('segments')} maxsymbols={message.get('maxsymbols')} "
                        f"subscription={message.get('subscription')} validity={message.get('validity')}")
            return

        if message.get("success") is False:
            # One session per account, or a lapsed subscription: reconnecting
            # cannot fix either.
            self._event(FeedEvent.REFUSED, str(message.get("message") or "refused"))
            return

        if "market" in text:
            self._event(FeedEvent.INFO, str(message.get("data") or message.get("message")))

    def _emit(self, fields, kind, canonical=None, snapshot=False):
        if self._on_ticks is None:
            return
        if canonical is None:
            canonical = self._canonical_by_id.get(str(fields.get("symbol_id") or ""))
            if canonical is None:
                # A price for something not asked for cannot be stored under a
                # symbol we trust.
                return
        tick = {
            "symbol": canonical,
            "dataType": "symbolUpdate",
            "exchangeTimestampUtc": _timestamp_utc(fields.get("timestamp")),
            "lastTradedPrice": _number(fields.get("ltp")),
            "bidPrice": _number(fields.get("bid")),
            "askPrice": _number(fields.get("ask")),
            "bidSize": _integer(fields.get("bid_qty")),
            "askSize": _integer(fields.get("ask_qty")),
            "open": _number(fields.get("open")),
            "high": _number(fields.get("high")),
            "low": _number(fields.get("low")),
            "prevClose": _number(fields.get("prev_close")),
            "volume": _integer(fields.get("volume")),
            # An index has no OI and sends zero: unknown, not "all positions closed".
            "openInterest": (_integer(fields.get("oi")) or None),
            "rawPayload": json.dumps(
                {k: v for k, v in fields.items() if k not in _FIELDS_ALREADY_COLUMNS and v not in (None, "")}
                or {"kind": kind}),
        }
        if snapshot:
            tick["snapshot"] = True
        self._on_ticks([tick])

    def _event(self, event, detail=""):
        if self._on_event is not None:
            self._on_event(event, detail)
