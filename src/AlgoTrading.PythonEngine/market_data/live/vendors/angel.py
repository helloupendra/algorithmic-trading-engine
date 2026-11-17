"""
Angel One's live socket (SmartAPI WebSocket 2.0), as a VendorFeed.

Why it exists (18 Nov 2026): Dhan's data plan has lapsed. FYERS needs a sign-in
by hand every morning (SEBI's 2FA, no refresh) and carries 100 symbols a socket.
The owner: NIFTY must not stop under any condition until Dhan is back. Angel
signs in by itself (client code + PIN + TOTP from .env, market_data/angel), carries
1,000 symbols a session and covers NSE, BSE and MCX.

The protocol, as angel-one/smartapi-python's smartWebSocketV2 speaks it. It was
checked on 18 Nov 2026 from the Mac and the server: the NIFTY index and a NIFTY
option came back at the same prices as FYERS.

  wss://smartapisocket.angelone.in/smart-stream, with the headers Authorization
  (Bearer <jwt>), x-api-key, x-client-code and x-feed-token. A text "ping"
  every 10 s is answered "pong".

  subscribe {"correlationID", "action": 1 (0 unsubscribes), "params": {"mode": 3,
  "tokenList": [{"exchangeType": 1, "tokens": ["99926000"]}, ...]}}
  Exchange types: 1 NSE cash and indices, 2 NFO, 3 BSE cash and indices, 4 BFO, 5 MCX.

  One binary packet per update, little-endian. Prices are in paise (/100).
     0  mode B, 1 exchange type B, 2:27 token (NUL-terminated), 27 sequence q,
    35  exchange time in ms q, 43 LTP q                                (LTP, 51 bytes)
    51  last qty q, 59 average price q, 67 volume q, 75/83 total buy/sell qty d,
    91  open q, 99 high q, 107 low q, 115 previous close q              (QUOTE, 123)
   123  last trade time q, 131 OI q, 139 OI change q, 147:347 the best five each
        way (10 x 20 bytes: flag H, qty q, price q, orders H), 347 upper circuit q,
   355  lower circuit q, 363/371 52-week high/low q                     (SNAP_QUOTE, 379)

The SDK files the best five crosswise (its "buy" list takes the packets it
flagged as sell), so the sides are told apart here by price, not by the flag:
bids are always below asks.

Angel's limits are 1,000 token subscriptions per session and 3 sessions per
client code (SmartAPI forum staff). Repeated logins are refused, so the session
is cached for 8 h (market_data/angel/session.py).
"""

from __future__ import annotations

import json
import os
import re
import struct
import threading
import time
from datetime import date, datetime, timezone
from typing import Callable, Optional

import websocket

import core.config  # noqa: F401  (loads the repo's .env, where the ANGEL_* values live)
from core.live.vendor_feed import FeedEvent, VendorFeed, close_detail
from core.option_symbol import _MONTH_CODES, _MONTHLY, _MONTHS, _WEEKLY
from market_data.angel.instruments import DEFAULT_INDEX_ALIASES, AngelInstruments, ScripRow
from market_data.angel.session import AngelAuthError, AngelSession

URL = "wss://smartapisocket.angelone.in/smart-stream"

#: Snap quote: LTP, the day's OHLC and volume, OI and the best five each way.
MODE_SNAP_QUOTE = 3
SUBSCRIBE, UNSUBSCRIBE = 1, 0
PING_SECONDS = 10.0

#: Angel's exchange segment -> the socket's exchange type.
EXCHANGE_TYPES = {"NSE": 1, "NFO": 2, "BSE": 3, "BFO": 4, "MCX": 5}
#: A platform symbol's exchange -> where its derivatives are listed.
DERIVATIVE_SEGMENT = {"NSE": "NFO", "BSE": "BFO", "MCX": "MCX"}
#: The exchange types whose instruments carry open interest.
DERIVATIVE_TYPES = frozenset({2, 4, 5})

#: Tokens per subscribe message. Angel takes a list; batches keep one message small.
TOKENS_PER_MESSAGE = 200

DEFAULT_MAX_SYMBOLS = 1000

_FUTURE = re.compile(
    r"^(?P<exchange>[A-Z]+):(?P<underlying>[A-Z0-9&]+?)(?P<yy>\d{2})"
    r"(?P<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)FUT$")

_PACKET_LTP, _PACKET_QUOTE, _PACKET_SNAP = 51, 123, 379


def _max_symbols_from_env() -> int:
    raw = os.environ.get("ANGEL_MAX_SYMBOLS", "").strip()
    try:
        value = int(raw) if raw else DEFAULT_MAX_SYMBOLS
    except ValueError:
        value = DEFAULT_MAX_SYMBOLS
    return value if value > 0 else DEFAULT_MAX_SYMBOLS


# ------------------------------------------------------------------ symbol map

class AngelSymbolMap:
    """
    The platform's symbols (FYERS's spelling: NSE:NIFTY50-INDEX, NSE:NIFTY26N2325400CE,
    NSE:BANKNIFTY26NOV61700CE, MCX:CRUDEOIL26NOVFUT, NSE:RELIANCE-EQ) to Angel's
    (exchange type, token), from Angel's scrip master. Matched on every part of a
    contract; a symbol it cannot place exactly gets None, never a near miss.
    """

    def __init__(self, rows):
        self._indices: dict[tuple[str, str], str] = {}
        self._index_names: dict[tuple[str, str], str] = {}
        self._cash: dict[tuple[str, str], str] = {}
        self._options: dict[tuple, str] = {}
        self._options_by_month: dict[tuple, list[tuple[date, str]]] = {}
        self._futures_by_month: dict[tuple, list[tuple[date, str]]] = {}
        for row in rows:
            self._add(row)

    def _add(self, row: ScripRow) -> None:
        if row.is_index:
            self._indices[(row.exchange, row.symbol.upper())] = row.token
            self._index_names.setdefault((row.exchange, row.name.upper().replace(" ", "")), row.token)
            return
        if row.exchange in ("NSE", "BSE") and not row.instrument_type:
            self._cash[(row.exchange, row.symbol.upper())] = row.token
            return
        expiry = row.expiry_date
        if expiry is None:
            return
        name = row.name.upper()
        if row.instrument_type.startswith("OPT"):
            kind = row.symbol.upper()[-2:]
            if kind not in ("CE", "PE"):
                return
            strike = int(round(row.strike))
            self._options[(row.exchange, name, expiry, strike, kind)] = row.token
            self._options_by_month.setdefault((row.exchange, name, expiry.year, expiry.month, strike, kind),
                                              []).append((expiry, row.token))
        elif row.instrument_type.startswith("FUT"):
            self._futures_by_month.setdefault((row.exchange, name, expiry.year, expiry.month),
                                              []).append((expiry, row.token))

    def resolve(self, canonical: str) -> Optional[tuple[int, str]]:
        text = (canonical or "").strip().upper()
        exchange, _, rest = text.partition(":")
        if not rest or exchange not in ("NSE", "BSE", "MCX"):
            return None

        if rest.endswith("-INDEX"):
            key = rest[: -len("-INDEX")]
            alias = DEFAULT_INDEX_ALIASES.get(f"{exchange}:{key}")
            token = (self._indices.get((exchange, alias.upper())) if alias
                     else self._index_names.get((exchange, key)))
            return (EXCHANGE_TYPES[exchange], token) if token else None

        if rest.endswith("-EQ") and exchange in ("NSE", "BSE"):
            token = self._cash.get((exchange, rest))
            return (EXCHANGE_TYPES[exchange], token) if token else None

        segment = DERIVATIVE_SEGMENT[exchange]
        match = _FUTURE.match(text)
        if match:
            rows = self._futures_by_month.get(
                (segment, match.group("underlying"), 2000 + int(match.group("yy")), _MONTHS[match.group("mon")]))
            return (EXCHANGE_TYPES[segment], max(rows)[1]) if rows else None

        match = _WEEKLY.match(text)
        if match and match.group("m") in _MONTH_CODES:
            try:
                expiry = date(2000 + int(match.group("yy")), _MONTH_CODES[match.group("m")], int(match.group("dd")))
            except ValueError:
                return None
            token = self._options.get((segment, match.group("underlying"), expiry,
                                       int(round(float(match.group("strike")) * 100)), match.group("kind")))
            return (EXCHANGE_TYPES[segment], token) if token else None

        match = _MONTHLY.match(text)
        if match:
            # A monthly symbol names its month only: the month's last expiry is its contract.
            rows = self._options_by_month.get(
                (segment, match.group("underlying"), 2000 + int(match.group("yy")), _MONTHS[match.group("mon")],
                 int(round(float(match.group("strike")) * 100)), match.group("kind")))
            return (EXCHANGE_TYPES[segment], max(rows)[1]) if rows else None
        return None


# ------------------------------------------------------------------- packets

def _price(raw: int, exchange_type: int) -> Optional[float]:
    if not raw:
        return None
    return round(raw / (10_000_000 if exchange_type == 13 else 100), 4)


def parse_packet(data: bytes) -> Optional[dict]:
    """One binary update as plain numbers (prices still in paise), or None when it is too short to be one."""
    if len(data) < _PACKET_LTP:
        return None
    packet = {
        "mode": data[0],
        "exchange_type": data[1],
        "token": data[2:27].split(b"\x00", 1)[0].decode(errors="replace"),
    }
    packet["sequence"], packet["exchange_ms"], packet["ltp"] = struct.unpack_from("<qqq", data, 27)
    if len(data) >= _PACKET_QUOTE:
        (packet["last_qty"], packet["average_price"], packet["volume"]) = struct.unpack_from("<qqq", data, 51)
        packet["total_buy_qty"], packet["total_sell_qty"] = struct.unpack_from("<dd", data, 75)
        packet["open"], packet["high"], packet["low"], packet["close"] = struct.unpack_from("<qqqq", data, 91)
    if len(data) >= _PACKET_SNAP:
        packet["last_trade_s"], packet["oi"] = struct.unpack_from("<qq", data, 123)
        levels = []
        for offset in range(147, 347, 20):
            flag, quantity, price, orders = struct.unpack_from("<HqqH", data, offset)
            if price > 0:
                levels.append((flag, price, quantity, orders))
        packet["levels"] = levels
    return packet


def _touch(levels: list[tuple[int, int, int, int]]) -> tuple[Optional[tuple[int, int]], Optional[tuple[int, int]]]:
    """
    ((bid price, qty), (ask price, qty)) from the best five, in paise. The two flags are the two
    sides; which flag is which is read from the prices, because bids sit below asks.
    """
    sides: dict[int, list[tuple[int, int]]] = {}
    for flag, price, quantity, _ in levels:
        sides.setdefault(flag, []).append((price, quantity))
    if not sides:
        return None, None
    if len(sides) < 2:
        return None, None              # one side alone cannot say which it is
    ordered = sorted(sides.values(), key=lambda side: min(p for p, _ in side))
    return max(ordered[0]), min(ordered[-1])


def packet_to_tick(packet: dict, canonical: str) -> dict:
    ex = packet["exchange_type"]
    bid, ask = _touch(packet.get("levels") or [])
    stamp = packet.get("exchange_ms") or 0
    exchange_ts = (datetime.fromtimestamp(stamp / 1000, tz=timezone.utc).isoformat().replace("+00:00", "Z")
                   if stamp > 0 else None)
    return {
        "symbol": canonical,
        "dataType": "symbolUpdate",
        "exchangeTimestampUtc": exchange_ts,
        "lastTradedPrice": _price(packet["ltp"], ex),
        "bidPrice": _price(bid[0], ex) if bid else None,
        "askPrice": _price(ask[0], ex) if ask else None,
        "bidSize": bid[1] if bid else None,
        "askSize": ask[1] if ask else None,
        "open": _price(packet.get("open") or 0, ex),
        "high": _price(packet.get("high") or 0, ex),
        "low": _price(packet.get("low") or 0, ex),
        "prevClose": _price(packet.get("close") or 0, ex),
        "volume": packet.get("volume") or None,
        # Only a derivative has OI. An index sends zero, and a cash share sends a number that
        # is not OI (RELIANCE-EQ: 426,012,000 on 17 Nov): both are unknown, not a value.
        "openInterest": (packet.get("oi") or None) if ex in DERIVATIVE_TYPES else None,
        "rawPayload": json.dumps({"angel": f"{ex}:{packet['token']}", "mode": packet["mode"],
                                  "seq": packet["sequence"],
                                  "atp": _price(packet.get("average_price") or 0, ex)}),
    }


# ---------------------------------------------------------------------- feed

class AngelFeed(VendorFeed):
    key = "angel"
    source_name = "python-angel-feed"
    lock_key = "feed:angel:lock"
    ready_event = FeedEvent.CONNECTED

    def __init__(self, session_factory: Callable[[], AngelSession] = AngelSession,
                 symbol_map_loader: Optional[Callable[[], AngelSymbolMap]] = None,
                 max_symbols: Optional[int] = None):
        self._session_factory = session_factory
        self._load_map = symbol_map_loader or (lambda: AngelSymbolMap(AngelInstruments.load().rows))
        self.max_symbols = max_symbols if max_symbols and max_symbols > 0 else _max_symbols_from_env()
        self._map: Optional[AngelSymbolMap] = None
        self._map_day: Optional[date] = None
        self._session: Optional[AngelSession] = None

        self._app = None
        self._closing = False
        self._generation = 0
        self._lock = threading.Lock()
        self._subscribed: dict[str, tuple[int, str]] = {}      # canonical -> (exchange type, token)
        self._canonical_by_token: dict[tuple[int, str], str] = {}
        self._reported_unmapped: set[str] = set()
        self._last_frame: Optional[float] = None
        self._on_ticks = None
        self._on_event = None
        # What the log says once a minute: proof that prices flow, and how fast
        # (scripts/switch-feed.sh reads it before it stops the old feed).
        self._window_start = time.monotonic()
        self._window_packets = 0
        self._window_symbols: set[str] = set()
        self._last_nifty: Optional[float] = None
        self._first_said = False

    @classmethod
    def from_env(cls):
        return cls()

    # --------------------------------------------------------- credentials

    def acquire_credentials(self, not_this=None):
        """
        A logged-in session's (jwt, feed token): signing in needs nobody (PIN + TOTP). A token the
        socket refused is replaced by a fresh login. Waits and tries again while Angel refuses.
        The scrip master is (re)read here, once a day, so a subscribe never waits on a download.
        """
        waited = 0
        while True:
            try:
                if self._map is None or self._map_day != date.today():
                    self._map = self._load_map()
                    self._map_day = date.today()
                session = self._session or self._session_factory()
                refused = not_this is not None and not_this[0] == session.jwt
                session.login(force=refused)
                self._session = session
                return session.jwt, session.feed_token
            except (AngelAuthError, OSError, ValueError) as ex:
                waited += 1
                print(f"[angel] cannot sign in yet ({ex}); trying again in 30s", flush=True)
                time.sleep(30 if waited < 20 else 120)

    # ------------------------------------------------------------- the socket

    def connect(self, credentials, on_ticks, on_event):
        jwt, feed_token = credentials
        session = self._session
        self._on_ticks = on_ticks
        self._on_event = on_event
        self._closing = False
        self._generation += 1
        generation = self._generation
        with self._lock:
            self._subscribed.clear()     # a new socket carries nothing yet
        self._last_frame = None
        self._first_said = False

        def live(handler):
            def wrapped(*args):
                if generation == self._generation and not self._closing:
                    handler(*args)
            return wrapped

        def on_open(app):
            on_event(FeedEvent.CONNECTED, "Angel One smart-stream connected")
            threading.Thread(target=self._ping, args=(generation,), name="angel-ping", daemon=True).start()

        def on_error(app, error):
            text = str(error)
            if any(code in text for code in ("401", "403", "Unauthorized", "Invalid", "invalid token")):
                on_event(FeedEvent.CREDENTIALS_REJECTED, text[:300])
            else:
                on_event(FeedEvent.ERROR, text[:300])

        headers = [f"Authorization: Bearer {jwt}", f"x-api-key: {session.credentials.api_key}",
                   f"x-client-code: {session.credentials.client_code}", f"x-feed-token: {feed_token}"]
        self._app = websocket.WebSocketApp(
            URL, header=headers,
            on_open=live(on_open),
            on_message=live(lambda _, message: self._on_message(message)),
            on_error=live(on_error),
            on_close=live(lambda _, code, msg: on_event(FeedEvent.DISCONNECTED, close_detail(code, msg))),
        )
        threading.Thread(target=self._app.run_forever, name="angel-socket", daemon=True).start()

    def _ping(self, generation: int) -> None:
        while generation == self._generation and not self._closing:
            time.sleep(PING_SECONDS)
            if generation != self._generation or self._closing:
                return
            try:
                if self._app is not None and self._app.sock is not None and self._app.sock.connected:
                    self._app.send("ping")
            except Exception:
                return

    def close(self):
        self._closing = True
        with self._lock:
            self._subscribed.clear()
        if self._app is not None:
            try:
                self._app.close()
            except Exception:
                pass

    def transport_idle_seconds(self):
        return None if self._last_frame is None else time.monotonic() - self._last_frame

    # ------------------------------------------------------------ subscribing

    def to_vendor(self, canonical_symbol):
        found = self._map.resolve(canonical_symbol) if self._map else None
        return f"{found[0]}:{found[1]}" if found else None

    def subscribe(self, symbols):
        if not symbols or self._map is None:
            return []
        wanted, unmapped = {}, []
        with self._lock:
            for canonical in symbols:
                name = str(canonical or "").strip()
                if not name or name in self._subscribed or name in wanted:
                    continue
                found = self._map.resolve(name)
                if found is None:
                    unmapped.append(name)
                else:
                    wanted[name] = found
            room = max(self.max_symbols - len(self._subscribed), 0)
            if len(wanted) > room:
                left_out = list(wanted)[room:]
                self._event(FeedEvent.INFO, f"only {room} of {len(wanted)} fit under the {self.max_symbols}-symbol "
                                            f"limit; left out: {_shown(left_out)}")
                wanted = dict(list(wanted.items())[:room])
            self._subscribed.update(wanted)
            for canonical, key in wanted.items():
                self._canonical_by_token[key] = canonical

        new = [s for s in unmapped if s.upper() not in self._reported_unmapped]
        if new:
            self._reported_unmapped.update(s.upper() for s in new)
            self._event(FeedEvent.INFO, f"{len(new)} symbol(s) have no Angel instrument and were not asked for: "
                                        f"{_shown(new)}")
        if wanted:
            self._send_tokens(SUBSCRIBE, list(wanted.values()))
        return list(wanted)

    def unsubscribe(self, symbols):
        with self._lock:
            keys = [self._subscribed.pop(s) for s in list(symbols) if s in self._subscribed]
        if keys:
            self._send_tokens(UNSUBSCRIBE, keys)
        return True

    def _send_tokens(self, action: int, keys: list[tuple[int, str]]) -> None:
        by_type: dict[int, list[str]] = {}
        for exchange_type, token in keys:
            by_type.setdefault(exchange_type, []).append(token)
        for exchange_type, tokens in sorted(by_type.items()):
            for start in range(0, len(tokens), TOKENS_PER_MESSAGE):
                self._send({"correlationID": f"openfno{action}{exchange_type}"[:10], "action": action,
                            "params": {"mode": MODE_SNAP_QUOTE,
                                       "tokenList": [{"exchangeType": exchange_type,
                                                      "tokens": tokens[start:start + TOKENS_PER_MESSAGE]}]}})

    def _send(self, message: dict) -> None:
        if self._app is None or self._app.sock is None:
            return
        try:
            self._app.send(json.dumps(message))
        except Exception as ex:
            self._event(FeedEvent.ERROR, f"could not send a {'subscribe' if message.get('action') else 'unsubscribe'}: {ex}")

    # --------------------------------------------------------------- messages

    def _on_message(self, message):
        self._last_frame = time.monotonic()
        if isinstance(message, str):
            text = message.strip()
            if text and text.lower() != "pong":
                # Angel answers a bad request with a JSON error, e.g. {"errorCode":"E1002",...}.
                self._event(FeedEvent.INFO, f"text frame: {text[:200]}")
            return
        packet = parse_packet(message)
        if packet is None:
            return
        canonical = self._canonical_by_token.get((packet["exchange_type"], packet["token"]))
        if canonical is None or self._on_ticks is None:
            return
        tick = packet_to_tick(packet, canonical)
        self._on_ticks([tick])
        self._count(canonical, tick["lastTradedPrice"])

    def _count(self, canonical: str, price) -> None:
        if not self._first_said:
            self._first_said = True
            print(f"[angel] first prices arriving on this connection ({canonical} {price})", flush=True)
        self._window_packets += 1
        self._window_symbols.add(canonical)
        if canonical == "NSE:NIFTY50-INDEX":
            self._last_nifty = price
        now = time.monotonic()
        if now - self._window_start >= 60:
            print(f"[angel] last {int(now - self._window_start)} s: {self._window_packets} packet(s) for "
                  f"{len(self._window_symbols)} symbol(s); NIFTY 50 {self._last_nifty}", flush=True)
            self._window_start, self._window_packets, self._window_symbols = now, 0, set()

    def _event(self, event, detail=""):
        if self._on_event is not None:
            self._on_event(event, detail)
        else:
            print(f"[angel] {event}: {detail}", flush=True)


def _shown(symbols: list[str]) -> str:
    return ", ".join(symbols[:8]) + (f" (+{len(symbols) - 8} more)" if len(symbols) > 8 else "")
