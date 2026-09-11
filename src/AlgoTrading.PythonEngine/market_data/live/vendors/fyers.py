"""
FYERS's live socket, as a VendorFeed.

Only what is FYERS's own: how its token is obtained and refused, how its SDK is
driven, and what its messages look like. Everything that used to sit around
this in fyers_streamer.py — the Redis stream, the batched posting, greeks, the
watchlist, the heartbeat, the watchdogs — is now core.live.feed_runner, shared
with every other vendor.
"""

import json
import time
from datetime import datetime, timezone

from core.api_client import build_session
from core.config import (API_BASE_URL, DEFAULT_DATA_TYPE, FYERS_LOG_PATH,
                         SOURCE_NAME, VERIFY_SSL, require_app_id)
from core.live.vendor_feed import FeedEvent, VendorFeed

#: How often to ask the API for a replacement while a rejected token stands.
TOKEN_REPLACEMENT_POLL_SECONDS = 10


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
    max_symbols = 200
    ready_event = FeedEvent.CONNECTED

    def __init__(self, http=None, api_base_url: str | None = None, verify_ssl: bool | None = None):
        self._http = http or build_session()
        self._api = (api_base_url or API_BASE_URL).rstrip("/")
        self._verify = VERIFY_SSL if verify_ssl is None else verify_ssl
        self._socket = None

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
        if not symbols:
            return []
        if self._socket is None:
            return []
        self._socket.subscribe(symbols=list(symbols), data_type=DEFAULT_DATA_TYPE)
        return list(symbols)

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
            return True
        except TypeError:
            try:
                self._socket.unsubscribe(symbols=list(symbols))
                return True
            except Exception as ex:
                print(f"[fyers] UNSUBSCRIBE FAILED: {ex}", flush=True)
                return False
        except Exception as ex:
            print(f"[fyers] UNSUBSCRIBE FAILED: {ex}", flush=True)
            return False
