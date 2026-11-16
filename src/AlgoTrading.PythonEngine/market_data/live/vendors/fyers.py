"""
FYERS's live socket, as a VendorFeed.

Only what is FYERS's own: how its token is obtained and refused, how its SDK is
driven, and what its messages look like. Everything that used to sit around
this in fyers_streamer.py — the Redis stream, the batched posting, greeks, the
watchlist, the heartbeat, the watchdogs — is now core.live.feed_runner, shared
with every other vendor.
"""

import json
import os
import time
from datetime import datetime, timezone

from core.api_client import build_session
from core.tls import tls_verify
from core.config import (API_BASE_URL, DEFAULT_DATA_TYPE, FYERS_LOG_PATH,
                         SOURCE_NAME, VERIFY_SSL, require_app_id)
from core.live.vendor_feed import FeedEvent, VendorFeed

#: How often to ask the API for a replacement while a rejected token stands.
TOKEN_REPLACEMENT_POLL_SECONDS = 10

#: Symbols one FYERS data socket may carry on the account's plan. Asking for
#: more is not trimmed by FYERS: the whole subscription is refused (code 51001,
#: "You requested more than your Standard plan's 100-symbol limit"), the socket
#: stays open and silent, and the watchdog reconnects into the same refusal
#: every two minutes — the dead feed of 16 Nov 2026, 229 symbols asked. The
#: Standard plan allows 100, Prime 5,000; set FYERS_MAX_SYMBOLS to the plan's.
DEFAULT_MAX_SYMBOLS = 100


def _max_symbols_from_env() -> int:
    raw = os.environ.get("FYERS_MAX_SYMBOLS", "").strip()
    try:
        value = int(raw) if raw else DEFAULT_MAX_SYMBOLS
    except ValueError:
        value = DEFAULT_MAX_SYMBOLS
    return value if value > 0 else DEFAULT_MAX_SYMBOLS


def _priority(symbol: str) -> int:
    """
    Which symbols keep their place when not all fit: spot indices first (every
    strategy and the morning check read them), then futures, then NIFTY's own
    contracts, then the rest. Within a rank the caller's order is kept.
    """
    name = str(symbol).upper()
    if name.endswith("-INDEX"):
        return 0
    if name.endswith("FUT"):
        return 1
    if name.startswith("NSE:NIFTY") and not name.startswith(("NSE:NIFTYBANK", "NSE:NIFTYNXT")):
        return 2
    return 3


def is_token_rejection(message) -> bool:
    """
    True when a socket error from FYERS means the access token itself is bad.
    Seen shapes: {'type': 'cn', 'code': -99, 'message': 'Token is expired'}
    and the -16 "Could not authenticate the user" family on REST.
    """
    if isinstance(message, dict):
        code = message.get("code")
        text = str(message.get("message", ""))
    else:
        code = None
        text = str(message)
    if code in (-99, -16, -8):
        return True
    lowered = text.lower()
    return "token" in lowered and ("expired" in lowered or "invalid" in lowered) \
        or "could not authenticate" in lowered


# Every field of a FYERS message that this platform already stores in a column
# of its own. Keeping them in rawPayload too was storing each tick twice: on
# 2026-09-11 rawPayload was 73% of every row. What is NOT listed survives, so a
# field FYERS adds later is still captured.
_RAW_FIELDS_ALREADY_COLUMNS = frozenset({
    "symbol",           # Symbol
    "type",             # DataType
    "last_traded_time", # ExchangeTimestampUtc
    "exch_feed_time",   # ExchangeTimestampUtc
    "ltp",              # LastTradedPrice
    "bid_price",        # BidPrice
    "ask_price",        # AskPrice
    "bid_size",         # BidSize
    "ask_size",         # AskSize
    "open_price",       # Open
    "high_price",       # High
    "low_price",        # Low
    "prev_close_price", # PrevClose
    "vol_traded_today", # Volume
})


def trim_raw_payload(message):
    """The message with the fields that are already columns removed; never the empty object."""
    kept = {k: v for k, v in message.items() if k not in _RAW_FIELDS_ALREADY_COLUMNS}
    if not kept:
        return '{"trimmed": true}'
    return json.dumps(kept)


def message_to_tick(message: dict) -> dict | None:
    """One FYERS message as a platform tick, or None when it names no symbol."""
    symbol = message.get("symbol")
    if not symbol:
        return None

    # When the exchange stamped this, from whichever field the message carries.
    # Index messages (type "if") have no last_traded_time — an index does not
    # trade — and reading only that field left every index tick unstamped: 0 of
    # 122,686 NIFTY50 ticks, while every option was stamped.
    exchange_ts = None
    for field in ("last_traded_time", "exch_feed_time", "feed_time", "timestamp"):
        raw = message.get(field)
        if not raw:
            continue
        try:
            exchange_ts = datetime.fromtimestamp(int(raw), tz=timezone.utc).isoformat().replace("+00:00", "Z")
            break
        except (TypeError, ValueError, OSError, OverflowError):
            continue

    return {
        "symbol": symbol,
        "dataType": "symbolUpdate",
        "exchangeTimestampUtc": exchange_ts,
        "lastTradedPrice": message.get("ltp"),
        "bidPrice": message.get("bid_price"),
        "askPrice": message.get("ask_price"),
        "bidSize": message.get("bid_size"),
        "askSize": message.get("ask_size"),
        "open": message.get("open_price") or message.get("open"),
        "high": message.get("high_price") or message.get("high"),
        "low": message.get("low_price") or message.get("low"),
        "prevClose": message.get("prev_close_price") or message.get("close"),
        "volume": message.get("vol_traded_today") or message.get("volume"),
        "openInterest": message.get("min_oi") or message.get("oi") or message.get("open_interest", 0),
        "rawPayload": trim_raw_payload(message),
    }


class FyersFeed(VendorFeed):
    key = "fyers"
    # Kept as they were, so the console's heartbeat row and any running lock
    # carry over unchanged.
    source_name = SOURCE_NAME
    lock_key = "fyers:live:ingestor:lock"
    max_symbols = DEFAULT_MAX_SYMBOLS
    ready_event = FeedEvent.CONNECTED

    def __init__(self, http=None, api_base_url: str | None = None, verify_ssl: bool | str | None = None,
                 max_symbols: int | None = None):
        self._api = (api_base_url or API_BASE_URL).rstrip("/")
        self._verify = tls_verify(VERIFY_SSL if verify_ssl is None else verify_ssl)
        self._http = http or build_session(base_url=self._api, verify_ssl=self._verify)
        self._socket = None
        self.max_symbols = max_symbols if max_symbols and max_symbols > 0 else _max_symbols_from_env()
        #: What is on the wire now: the plan's limit counts these.
        self._subscribed: set[str] = set()

    @classmethod
    def from_env(cls):
        return cls()

    # ------------------------------------------------------------ credentials

    def acquire_credentials(self, not_this=None):
        """
        The current FYERS access token from the API, waiting until there is one.

        With not_this, keeps polling until the API hands out a different token:
        the one FYERS just refused is no use however many times it is re-read.
        """
        url = f"{self._api}/api/auth/session"
        waited_on_rejected = False
        while True:
            try:
                response = self._http.get(url, verify=self._verify, timeout=10)
                response.raise_for_status()
                data = response.json()
                if data.get("isAuthenticated") and data.get("accessToken"):
                    token = data["accessToken"]
                    if not_this is not None and token == not_this:
                        if not waited_on_rejected:
                            print("[fyers] the API still holds the token FYERS rejected — waiting for a new "
                                  f"sign-in (checking every {TOKEN_REPLACEMENT_POLL_SECONDS}s).", flush=True)
                            waited_on_rejected = True
                        time.sleep(TOKEN_REPLACEMENT_POLL_SECONDS)
                        continue
                    return token
                print("[fyers] the API is up but FYERS is not signed in. Retrying in 5s...", flush=True)
                time.sleep(5)
            except Exception as ex:
                print(f"[fyers] the API is not reachable yet ({ex}). Retrying in 5s...", flush=True)
                time.sleep(5)

    # -------------------------------------------------------------- the socket

    def connect(self, credentials, on_ticks, on_event):
        from fyers_apiv3.FyersWebsocket import data_ws

        def on_message(message):
            items = message if isinstance(message, list) else [message] if isinstance(message, dict) else []
            ticks = [t for t in (message_to_tick(m) for m in items if isinstance(m, dict)) if t]
            if ticks:
                on_ticks(ticks)
            elif not items:
                on_event(FeedEvent.ERROR, f"unknown message format: {str(message)[:160]}")

        def on_error(message):
            if is_token_rejection(message):
                on_event(FeedEvent.CREDENTIALS_REJECTED, str(message))
            else:
                on_event(FeedEvent.ERROR, str(message))

        # The SDK keeps a singleton; a rebuild must not inherit the dead one.
        data_ws.FyersDataSocket._instance = None
        # A new socket carries nothing yet.
        self._subscribed = set()
        self._socket = data_ws.FyersDataSocket(
            access_token=f"{require_app_id()}:{credentials}",
            log_path=FYERS_LOG_PATH,
            litemode=False,
            write_to_file=False,
            reconnect=True,
            on_connect=lambda: on_event(FeedEvent.CONNECTED, "FYERS websocket connected"),
            on_close=lambda message: on_event(FeedEvent.DISCONNECTED, str(message)),
            on_error=on_error,
            on_message=on_message,
        )
        self._socket.connect()

    def close(self):
        if self._socket is None:
            return
        try:
            self._socket.close_connection()
        finally:
            self._socket = None

    def subscribe(self, symbols):
        """
        Subscribe what fits under the plan's limit and return those. FYERS
        refuses a request that would take the socket past the limit outright,
        so the rest is left out here: indices, futures and NIFTY's contracts
        first (`_priority`), the runner's order within each.
        """
        if not symbols or self._socket is None:
            return []
        wanted, seen = [], set(self._subscribed)
        for symbol in symbols:
            name = str(symbol or "").strip()
            if name and name not in seen:
                seen.add(name)
                wanted.append(name)
        wanted.sort(key=_priority)
        room = max(self.max_symbols - len(self._subscribed), 0)
        if len(wanted) > room:
            left_out = wanted[room:]
            shown = ", ".join(left_out[:8]) + (f" (+{len(left_out) - 8} more)" if len(left_out) > 8 else "")
            print(f"[fyers] only {room} of {len(wanted)} symbol(s) fit under the {self.max_symbols}-symbol limit "
                  f"(FYERS_MAX_SYMBOLS); left out: {shown}", flush=True)
            wanted = wanted[:room]
        if not wanted:
            return []
        self._socket.subscribe(symbols=wanted, data_type=DEFAULT_DATA_TYPE)
        self._subscribed.update(wanted)
        return wanted

    def unsubscribe(self, symbols):
        """
        True when the symbols are genuinely off the wire. Older SDKs have no
        unsubscribe, and one version takes no data_type.
        """
        if not symbols:
            return True
        if self._socket is None or not hasattr(self._socket, "unsubscribe"):
            return False
        try:
            self._socket.unsubscribe(symbols=list(symbols), data_type=DEFAULT_DATA_TYPE)
            self._subscribed.difference_update(str(s).strip() for s in symbols)
            return True
        except TypeError:
            try:
                self._socket.unsubscribe(symbols=list(symbols))
                self._subscribed.difference_update(str(s).strip() for s in symbols)
                return True
            except Exception as ex:
                print(f"[fyers] UNSUBSCRIBE FAILED: {ex}", flush=True)
                return False
        except Exception as ex:
            print(f"[fyers] UNSUBSCRIBE FAILED: {ex}", flush=True)
            return False
