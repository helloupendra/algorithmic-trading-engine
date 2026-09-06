"""
live_data_ingestor.py

Connects to the FYERS WebSocket to consume live market data ticks.
Continuously fetches active watchlist symbols from the local .NET API, subscribes to them,
and pushes the received tick data back into the local database for strategy consumption.
Also manages a heartbeat mechanism to notify the API that the ingestor is healthy.
"""
import sys
import os

# Add the parent directory to sys.path so that absolute-style imports work
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..')))

# Before anything prints: when the API that spawned us dies, stdout becomes a
# closed pipe and a plain print() would raise BrokenPipeError inside every
# thread. From then on output goes to logs/engine/ingestor-<pid>.log instead.
from core.safe_output import install_safe_stdio
install_safe_stdio(name="ingestor")

import json
import time
import threading
import traceback
import urllib3
import requests
import re
import math
from core.api_client import build_session
from datetime import datetime, timezone
from fyers_apiv3.FyersWebsocket import data_ws


# Add the parent directory to sys.path so that imports resolve correctly
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..')))

from core.config import (
    API_BASE_URL,
    VERIFY_SSL,
    DEFAULT_DATA_TYPE,
    DEBUG_PRINT_MESSAGES,
    ENABLE_MOCK_TICKS,
    WATCHLIST_REFRESH_SECONDS,
    SOURCE_NAME,
    DATA_PROVIDER_KEY,
    HEARTBEAT_SECONDS,
    require_app_id,
    FYERS_LOG_PATH,
)

from core.heartbeat import run_forever
from core.option_symbol import parse_option_symbol, years_to_expiry
from messaging.redis_publisher import build_publisher_from_env, normalize_tick

publisher = build_publisher_from_env()
publisher.ensure_connection()

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

# Global socket instance
fyers = None

# Track currently subscribed symbols
subscribed_symbols = set()

# Shared HTTP session
http = build_session()

# Heartbeat/watchlist state
last_watchlist_refresh_utc = None
last_error_message = ""
restart_required = False
threads_started = False

# Connection-health state. The heartbeat must report what is actually
# happening — a hardcoded "Running" hides a dead websocket from every
# dashboard and strategy (the exact silent failure this ingestor must never
# have). All timestamps are time.monotonic().
socket_connected = False
disconnected_since = None   # set on close, cleared on (re)connect
last_tick_monotonic = None  # last REAL websocket message of any kind

# Restart the connection (re-fetching the broker token from the API) when a
# disconnect persists this long. The SDK's own reconnect keeps retrying with
# the token it was constructed with — after the daily token expiry that loops
# forever, so the outer restart is what picks up a fresh token.
DISCONNECT_RESTART_SECONDS = 20

# Consider the feed stalled when the market is open, symbols are subscribed,
# and nothing has arrived for this long.
STALL_AFTER_SECONDS = 120

# Symbols added to the watchlist are subscribed on the LIVE socket rather than
# by tearing the connection down and rebuilding it. A rolling straddle rolls its
# ATM strike several times a day; rebuilding for each roll blacked out every
# other symbol too, and those ticks were never gap-filled.
#
# The old full restart survives as the fallback. If the SDK's incremental
# subscribe turns out not to work on this connection, NONE of a batch of newly
# added symbols will tick — one quiet strike proves nothing, a whole silent
# batch does — and the connection is rebuilt exactly as before. The window is
# generous because a far-OTM strike can legitimately be quiet for a while.
SUBSCRIBE_PROOF_SECONDS = 90

# Symbols subscribed on the live socket that have yet to prove they are getting
# data: {symbol: (subscribed_at_walltime, monotonic deadline)}. The wall time is
# what makes the proof honest — last_real_tick keeps a symbol's last tick
# forever, so a symbol removed and later re-added would otherwise be confirmed
# by data that arrived before this subscribe.
pending_subscriptions = {}

# Cached market-session answer from the API (checked at most once a minute).
_market_open_cache = {"value": None, "checked_at": 0.0}


def utc_now_iso():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def get_active_session():
    """
    Read the current broker session from your .NET API.
    Retries gracefully if the API is down or not logged in yet.
    """
    url = f"{API_BASE_URL}/api/auth/session"
    
    while True:
        try:
            response = http.get(url, verify=VERIFY_SSL, timeout=10)
            response.raise_for_status()
            data = response.json()

            if data.get("isAuthenticated") and data.get("accessToken"):
                return data["accessToken"]
                
            print("[API Wait] C# API is running, but FYERS is not authenticated. Please login via the dashboard. Retrying in 5s...")
            time.sleep(5)
            
        except Exception as ex:
            print(f"[API Wait] C# API is not reachable. Waiting for API to boot... ({ex})")
            time.sleep(5)


def get_active_watchlist():
    """
    Read active watchlist symbols from your .NET API.
    Expects:
    GET /api/LiveData/watchlist
    """
    url = f"{API_BASE_URL}/api/LiveData/watchlist"
    response = http.get(url, verify=VERIFY_SSL, timeout=30)
    response.raise_for_status()

    rows = response.json()

    # For now keep only active items using symbolUpdate
    active_items = [
        x for x in rows
        if x.get("isActive") and (x.get("dataType") or "").lower() == "symbolupdate"
    ]

    symbols = [x["symbol"] for x in active_items if x.get("symbol")]
    return sorted(set(symbols))


import concurrent.futures

# Posting ticks to the API happens off the websocket thread, so a slow API
# cannot stall the socket. The queue in front of those threads is the part that
# needs a ceiling.
TICK_WORKERS = 5

#: How many posts may be waiting before we start dropping them.
#:
#: ThreadPoolExecutor's own work queue is UNBOUNDED. When the API slowed on
#: 2026-09-04 the submissions piled up inside this process — invisibly, since
#: nothing measured the depth — and the feed drifted to 31 minutes behind
#: without a single log line saying so. A bounded queue turns that into a
#: visible, bounded loss: the newest prices keep flowing and the backlog is
#: reported rather than silently accumulated.
TICK_QUEUE_LIMIT = 2000

#: How often to say something when we are shedding.
TICK_DROP_LOG_EVERY = 500

tick_executor = concurrent.futures.ThreadPoolExecutor(max_workers=TICK_WORKERS)
_tick_inflight = 0
_tick_inflight_lock = threading.Lock()
_ticks_dropped = 0


def tick_queue_depth() -> int:
    """Posts submitted and not yet finished. Reported in the heartbeat."""
    return _tick_inflight


def ticks_dropped_total() -> int:
    return _ticks_dropped


def _do_upsert_tick(payload: dict):
    global _tick_inflight
    url = f"{API_BASE_URL}/api/LiveData/ticks/upsert"
    try:
        response = http.post(url, json=payload, verify=VERIFY_SSL, timeout=10)
        if response.status_code >= 400:
            print("TICK UPSERT FAILED:", response.status_code, response.text)
    except Exception as e:
        print("TICK UPSERT HTTP ERROR:", e)
    finally:
        with _tick_inflight_lock:
            _tick_inflight -= 1


def upsert_tick(payload: dict):
    """
    Queue one tick for the API, and refuse to queue without limit.

    Dropping the NEWEST tick when saturated is deliberate: the backlog already
    holds older prices for the same symbols, and the alternative — an unbounded
    queue — spends memory to deliver prices that are minutes stale by the time
    they land, which is worse than not delivering them.
    """
    global _tick_inflight, _ticks_dropped

    with _tick_inflight_lock:
        if _tick_inflight >= TICK_QUEUE_LIMIT:
            _ticks_dropped += 1
            if _ticks_dropped % TICK_DROP_LOG_EVERY == 1:
                print(f"TICK BACKLOG: {_tick_inflight} posts in flight, dropping. "
                      f"{_ticks_dropped} dropped so far — the API is not keeping up.",
                      flush=True)
            return
        _tick_inflight += 1

    tick_executor.submit(_do_upsert_tick, payload)


def is_market_open() -> bool | None:
    """
    Ask the API whether the NSE cash session is open, cached for 60s.
    Returns None when the answer is unavailable (API down) — callers should
    then avoid declaring the feed stalled on guesswork.
    """
    now = time.monotonic()
    if _market_open_cache["value"] is not None and now - _market_open_cache["checked_at"] < 60:
        return _market_open_cache["value"]
    try:
        response = http.get(
            f"{API_BASE_URL}/api/MarketSession/check?exchange=NSE&segment=CM",
            verify=VERIFY_SSL,
            timeout=10,
        )
        response.raise_for_status()
        _market_open_cache["value"] = bool(response.json().get("isMarketOpen"))
        _market_open_cache["checked_at"] = now
    except Exception as ex:
        print("MARKET SESSION CHECK FAILED:", ex)
        _market_open_cache["value"] = None
    return _market_open_cache["value"]


def compute_status() -> str:
    """
    The honest feed status:
      Disconnected — websocket is down (SDK may be retrying);
      Stalled      — connected, market open, symbols subscribed, but no data
                     for STALL_AFTER_SECONDS;
      Running      — everything else (incl. idle outside market hours).
    The API marks the source unhealthy for anything except Running.
    """
    if not socket_connected:
        return "Disconnected"

    if subscribed_symbols and last_tick_monotonic is not None:
        silent_for = time.monotonic() - last_tick_monotonic
        if silent_for > STALL_AFTER_SECONDS and is_market_open() is True:
            return "Stalled"

    return "Running"


def send_heartbeat():
    """
    Send ingestor heartbeat/status into your .NET API.
    """
    payload = {
        "sourceName": SOURCE_NAME,
        "status": compute_status(),
        "lastHeartbeatUtc": utc_now_iso(),
        "lastWatchlistRefreshUtc": (
            last_watchlist_refresh_utc.isoformat().replace("+00:00", "Z")
            if last_watchlist_refresh_utc else None
        ),
        "currentSubscribedSymbols": sorted(list(subscribed_symbols)),
        # Backlog, made visible. The 31-minute drift of 2026-09-04 accumulated
        # entirely inside this process with nothing reporting it.
        "queueDepth": tick_queue_depth(),
        "ticksDropped": ticks_dropped_total(),
        "lastError": last_error_message,
        # Lets the API find (and stop) this process again after it restarts.
        "processId": os.getpid(),
    }

    url = f"{API_BASE_URL}/api/LiveData/heartbeat"
    response = http.post(url, json=payload, verify=VERIFY_SSL, timeout=30)

    if response.status_code >= 400:
        print("HEARTBEAT FAILED:", response.status_code, response.text)
    else:
        print(f"HEARTBEAT OK ({payload['status']})")


def handle_live_tick(raw_msg: dict):
    normalized = normalize_tick(raw_msg)
    
    # Track when we got the real tick so the mock generator yields
    sym = normalized["symbol"]
    if sym:
        last_real_tick[sym] = time.time()

    # Publish all subscribed symbols to Redis
    publisher.publish_tick(normalized)


# Spot per underlying, filled from the index ticks as they arrive.
#
# Deliberately EMPTY at start. It used to be seeded with two made-up prices, so
# before the first index tick every option was priced against a number nobody
# had observed — and anything that was not NIFTY or BANKNIFTY was priced against
# BANKNIFTY's 51,000 forever. A missing spot now means no greeks, which is the
# honest answer until the index reports.
latest_spots: dict[str, float] = {}

#: The spot instrument each underlying is quoted by. Mirrors the API's
#: UnderlyingCatalog; without SENSEX and the rest here their options could never
#: be priced at all.
UNDERLYING_BY_SPOT_SYMBOL = {
    "NSE:NIFTY50-INDEX": "NIFTY",
    "NSE:NIFTYBANK-INDEX": "BANKNIFTY",
    "NSE:FINNIFTY-INDEX": "FINNIFTY",
    "NSE:MIDCPNIFTY-INDEX": "MIDCPNIFTY",
    "NSE:NIFTYNXT50-INDEX": "NIFTYNXT50",
    "BSE:SENSEX-INDEX": "SENSEX",
    "BSE:BANKEX-INDEX": "BANKEX",
}

def map_message_to_payload(message: dict) -> dict | None:
    symbol = message.get("symbol")
    if not symbol:
        return None
        
    ltp = message.get("ltp")
    
    # Track spot prices for indices
    if ltp:
        underlying = UNDERLYING_BY_SPOT_SYMBOL.get(symbol)
        if underlying:
            latest_spots[underlying] = ltp

    # When the exchange stamped this, from whichever field the message carries.
    #
    # Index messages (FYERS type "if") have no `last_traded_time` — an index does
    # not trade — so reading only that field left every index tick unstamped. In
    # production that was 0 of 122,686 NIFTY50 ticks and 0 of 112,696 BANKNIFTY,
    # while every option symbol was stamped 100% of the time. The indices are the
    # symbols the strategies actually run on, so the pipeline's only latency
    # instrument was blind for exactly the ones that mattered — the 31-minute
    # backlog of 2026-09-04 was invisible in the data it wrote.
    exchange_ts = None
    for field in ("last_traded_time", "exch_feed_time", "feed_time", "timestamp"):
        raw = message.get(field)
        if not raw:
            continue
        try:
            exchange_ts = datetime.fromtimestamp(
                int(raw),
                tz=timezone.utc
            ).isoformat().replace("+00:00", "Z")
            break
        except (TypeError, ValueError, OSError, OverflowError):
            continue

    payload = {
        "symbol": symbol,
        "dataType": "symbolUpdate",
        # Say who produced this. Without it the API has to guess from the single
        # connector that claims a live feed, which stops being true the moment a
        # second one exists.
        "sourceKey": DATA_PROVIDER_KEY,
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
        "rawPayload": json.dumps(message)
    }

    # Implied volatility and greeks, when this is an option we can actually price.
    #
    # All three inputs used to be wrong. The symbol was matched with a regex
    # anchored to "NSE:", so every BSE contract (SENSEX, BANKEX) silently got
    # nothing. The spot came from a two-entry dict with hardcoded fallbacks, so
    # anything that was not NIFTY or BANKNIFTY was priced against 51,000. And
    # the time to expiry was a flat seven days "for demo" — the same option at
    # the same price implies 20% vol over seven days, 38% over two and 110% on
    # expiry morning, so the one number that decides whether an option is dear
    # or cheap was being computed against a date the contract does not have.
    if payload["lastTradedPrice"]:
        contract = parse_option_symbol(symbol)
        if contract:
            spot = latest_spots.get(contract.underlying)
            tte_years = years_to_expiry(contract.expiry)

            # No invented spot, and nothing priced after the bell. Missing greeks
            # are honest; wrong ones are read as fact by whatever comes next.
            greeks = None
            if spot and spot > 0 and tte_years > 0:
                from core.greeks_calculator import calculate_greeks
                greeks = calculate_greeks(
                    spot=spot,
                    strike=contract.strike,
                    tte_years=tte_years,
                    option_type=contract.kind,
                    option_price=payload["lastTradedPrice"],
                )

            payload["impliedVolatility"] = greeks.iv if greeks else None
            payload["delta"] = greeks.delta if greeks else None
            payload["gamma"] = greeks.gamma if greeks else None
            payload["theta"] = greeks.theta if greeks else None
            payload["vega"] = greeks.vega if greeks else None

    return payload


def onmessage(message):
    """
    Called whenever FYERS sends a live market-data update.
    """
    global last_error_message
    global last_tick_monotonic

    last_tick_monotonic = time.monotonic()

    try:
        if DEBUG_PRINT_MESSAGES:
            print("LIVE MESSAGE:")
            print(json.dumps(message, indent=2, default=str))

        if isinstance(message, list):
            for item in message:
                payload = map_message_to_payload(item)
                if payload:
                    upsert_tick(payload)
                handle_live_tick(item)

        elif isinstance(message, dict):
            payload = map_message_to_payload(message)
            if payload:
                upsert_tick(payload)
            handle_live_tick(message)

        else:
            print("UNKNOWN MESSAGE FORMAT:", message)

        last_error_message = ""

    except Exception as ex:
        last_error_message = str(ex)
        print("ERROR IN onmessage:", ex)
        traceback.print_exc()


def onerror(message):
    global last_error_message
    last_error_message = str(message)
    print("SOCKET ERROR:")
    print(message)


def onclose(message):
    global last_error_message
    global socket_connected
    global disconnected_since
    last_error_message = str(message)
    socket_connected = False
    if disconnected_since is None:
        disconnected_since = time.monotonic()
    print("SOCKET CLOSED:")
    print(message)


def subscribe_symbols(symbols):
    """
    Subscribe newly added symbols.
    """
    if not symbols:
        return

    if fyers is None:
        print("WARN: FYERS socket not ready, skipping subscribe. Will subscribe on connect.")
        return

    print("SUBSCRIBING SYMBOLS:", symbols)
    fyers.subscribe(symbols=symbols, data_type=DEFAULT_DATA_TYPE)


def unsubscribe_symbols(symbols):
    """
    Unsubscribe removed symbols if the SDK supports it.

    Returns True when they are genuinely off the wire. A False answer is not a
    problem worth restarting the socket for: a symbol nobody reads costs a
    little bandwidth and nothing else, whereas a rebuild costs every other
    symbol its ticks.
    """
    if not symbols:
        return True

    print("UNSUBSCRIBING SYMBOLS:", symbols)

    if not hasattr(fyers, "unsubscribe"):
        print("NOTE: unsubscribe() not in this FYERS SDK — leaving these symbols on the wire.")
        return False

    try:
        fyers.unsubscribe(symbols=symbols, data_type=DEFAULT_DATA_TYPE)
        return True
    except TypeError:
        try:
            fyers.unsubscribe(symbols=symbols)
            return True
        except Exception as ex:
            print("UNSUBSCRIBE FAILED:", ex)
            return False
    except Exception as ex:
        print("UNSUBSCRIBE FAILED:", ex)
        return False


def sync_watchlist(force_subscribe=False):
    """
    Fetch the active watchlist and adjust subscriptions on the LIVE socket.

    Only a fresh connection subscribes the whole list. After that the difference
    is applied in place — added symbols are subscribed, removed ones dropped if
    the SDK allows it — so an intraday ATM roll costs nothing to the symbols it
    did not touch. ``subscribed_symbols`` stays the truth about what is on the
    wire, since the heartbeat and the stall detector both read it.
    """
    global subscribed_symbols
    global last_watchlist_refresh_utc
    global last_error_message

    try:
        desired_symbols = set(get_active_watchlist())

        if force_subscribe or not subscribed_symbols:
            subscribe_symbols(sorted(desired_symbols))
            subscribed_symbols = set(desired_symbols)
            # A fresh socket is proved by the feed as a whole, not per symbol:
            # the disconnect watchdog and the stall detector already cover it.
            pending_subscriptions.clear()
        else:
            added = desired_symbols - subscribed_symbols
            removed = subscribed_symbols - desired_symbols

            if added:
                print("WATCHLIST CHANGED — subscribing on the live socket:", sorted(added))
                subscribe_symbols(sorted(added))
                subscribed_symbols = subscribed_symbols | added
                marker = (time.time(), time.monotonic() + SUBSCRIBE_PROOF_SECONDS)
                for symbol in added:
                    pending_subscriptions[symbol] = marker

            if removed:
                if unsubscribe_symbols(sorted(removed)):
                    subscribed_symbols = subscribed_symbols - removed
                else:
                    print("NOTE: still subscribed to", sorted(removed),
                          "— harmless, and cheaper than rebuilding the connection.")
                for symbol in removed:
                    pending_subscriptions.pop(symbol, None)

        last_watchlist_refresh_utc = datetime.now(timezone.utc)

        print("CURRENT SUBSCRIBED SYMBOLS:", sorted(subscribed_symbols))
        last_error_message = ""

    except Exception as ex:
        last_error_message = str(ex)
        print("ERROR SYNCING WATCHLIST:", ex)
        traceback.print_exc()


def check_pending_subscriptions():
    """
    The fallback to the old behaviour: rebuild the connection when a batch of
    newly added symbols is still silent past its deadline.

    Any one of them ticking proves incremental subscribe works, so the whole
    batch is cleared. Only a wholly silent batch — during market hours, on a
    connected socket — is evidence that the subscribe did not take, and that is
    the case the full restart exists for.
    """
    global restart_required

    if not pending_subscriptions or not socket_connected:
        return

    now = time.monotonic()
    pending = list(pending_subscriptions.items())

    # Any one of them proves the batch. This is a separate pass on purpose: a
    # single quiet symbol must not shadow a live one just by sorting earlier.
    for symbol, (subscribed_at, _) in pending:
        if last_real_tick.get(symbol, 0) > subscribed_at:
            print(f"SUBSCRIBE CONFIRMED: {symbol} is sending data.")
            pending_subscriptions.clear()
            return

    # Still waiting while any of them has time left.
    if any(now < deadline for _, (_, deadline) in pending):
        return

    # Nothing arrived, and the market has to be open for silence to mean
    # anything. Outside market hours the symbols simply are not trading.
    if is_market_open() is not True:
        extended = now + SUBSCRIBE_PROOF_SECONDS
        for symbol, (subscribed_at, _) in list(pending_subscriptions.items()):
            pending_subscriptions[symbol] = (subscribed_at, extended)
        return

    print(
        "SUBSCRIBE UNCONFIRMED: no data for any of",
        sorted(pending_subscriptions),
        f"in {SUBSCRIBE_PROOF_SECONDS}s of open market — rebuilding the connection.",
    )
    pending_subscriptions.clear()
    restart_required = True


def redis_subscriber_loop():
    """
    Listens to Redis Pub/Sub for watchlist updates and triggers sync.
    """
    global last_error_message

    try:
        pubsub = publisher.client.pubsub()
        pubsub.subscribe("watchlist_updates")
        print("STARTED REDIS SUBSCRIBER FOR WATCHLIST UPDATES")

        for message in pubsub.listen():
            if message['type'] == 'message':
                print("Received watchlist update signal from Redis!")
                sync_watchlist()
    except Exception as ex:
        last_error_message = str(ex)
        print("ERROR IN REDIS SUBSCRIBER:", ex)
        traceback.print_exc()


def heartbeat_step():
    """One heartbeat: renew the singleton lock, then report status to the API."""
    publisher.client.expire("fyers:live:ingestor:lock", 15)
    send_heartbeat()


def _record_heartbeat_error(ex: BaseException) -> None:
    global last_error_message
    last_error_message = str(ex)
    try:
        traceback.print_exc()
    except BaseException:
        pass


def heartbeat_loop(max_iterations=None):
    """
    Send heartbeat periodically so .NET API can show health/status.

    This thread must never die: an unreachable API is retried on the next
    beat, and a closed stdout (the API restarted) cannot kill it because
    both the step and its error reporting are guarded — see core.heartbeat.
    `max_iterations` bounds the loop for tests.
    """
    return run_forever(
        heartbeat_step,
        HEARTBEAT_SECONDS,
        log=print,
        on_error=_record_heartbeat_error,
        max_iterations=max_iterations,
        label="heartbeat loop",
    )


def onopen():
    """
    On socket connect
    """
    global last_error_message
    global socket_connected
    global disconnected_since

    try:
        last_error_message = ""
        socket_connected = True
        disconnected_since = None
        print("FYERS WEBSOCKET CONNECTED!")
        
        # Now that socket is ready, fetch the active DB watchlist and subscribe
        sync_watchlist(force_subscribe=True)
    except Exception as ex:
        last_error_message = str(ex)
        print("ERROR IN onopen:", ex)
        traceback.print_exc()


import random

mock_prices = {}
mock_open_prices = {}
last_real_tick = {}

def should_mock_symbol(symbol: str) -> bool:
    """
    Whether this symbol gets a fabricated price.

    Only symbols the broker feed cannot carry: anything not in FYERS
    "EXCHANGE:NAME" form, and continuous futures like "MCX:GOLD-FUT" that name
    no contract month. A real, dated option or index symbol must never match —
    fabricating a price for one would put an invented number in the same table
    as the real ones, indistinguishable to every strategy reading it.

    The flag is checked here as well as at the thread start: two locks on the
    same door, so a future caller cannot reach the fabrication by another route.
    """
    if not ENABLE_MOCK_TICKS:
        return False
    if ":" not in symbol:
        return True
    return "-FUT" in symbol and not any(char.isdigit() for char in symbol)


def mock_tick_loop():
    while True:
        try:
            for sym in list(subscribed_symbols):
                # Mock continuous futures, or symbols that don't look like valid Fyers formats
                if should_mock_symbol(sym):
                    if sym not in mock_prices:
                        if "GOLD" in sym:
                            mock_prices[sym] = 153122.0
                        elif "SILVER" in sym:
                            mock_prices[sym] = 253400.0
                        elif "COPPER" in sym:
                            mock_prices[sym] = 1342.0
                        elif "CRUDE" in sym or "OIL" in sym:
                            mock_prices[sym] = 7600.0
                        elif "NATURALGAS" in sym:
                            mock_prices[sym] = 291.0
                        elif "IDEA" in sym:
                            mock_prices[sym] = 14.67
                        elif "NIFTY50" in sym:
                            mock_prices[sym] = 23989.15
                        elif "NIFTYBANK" in sym:
                            mock_prices[sym] = 57297.15
                        elif "SBIN" in sym:
                            mock_prices[sym] = 1015.30
                        else:
                            import hashlib
                            h = int(hashlib.md5(sym.encode()).hexdigest(), 16)
                            mock_prices[sym] = 50.0 + (h % 3450)
                            
                        mock_open_prices[sym] = mock_prices[sym] * 0.995
                            
                    # Do not randomize the price so it remains static (no UI flickering)
                    price = mock_prices[sym]
                    
                    payload = {
                        "symbol": sym,
                        "dataType": "symbolUpdate",
                        "exchangeTimestampUtc": utc_now_iso(),
                        "lastTradedPrice": price,
                        "bidPrice": price - 0.5,
                        "askPrice": price + 0.5,
                        "bidSize": random.randint(1, 10),
                        "askSize": random.randint(1, 10),
                        "open": mock_open_prices[sym],
                        "high": max(mock_open_prices[sym], price * 1.005),
                        "low": min(mock_open_prices[sym], price * 0.99),
                        "prevClose": mock_open_prices[sym] * 1.002,
                        "volume": random.randint(100, 5000),
                        "rawPayload": "{}"
                    }
                    upsert_tick(payload)
        except Exception as e:
            print("MOCK TICK ERROR:", e)
        time.sleep(1)

def main():
    global fyers
    global last_error_message
    global restart_required
    global threads_started
    global socket_connected
    global disconnected_since

    print("STARTING FYERS LIVE DATA INGESTOR...")
    print(f"API BASE URL: {API_BASE_URL}")

    # ACQUIRE SINGLETON LOCK
    lock_key = "fyers:live:ingestor:lock"
    # Try to acquire lock with 15 second expiry
    if not publisher.client.set(lock_key, "active", nx=True, ex=15):
        print("\n" + "="*50)
        print("🚨 [CRITICAL WARNING] 🚨")
        print("Another instance of the Live Data Ingestor is already running!")
        print("Starting multiple ingestors will cause FYERS to forcefully drop")
        print("connections and corrupt your live tick streams.")
        print("This instance will now safely exit.")
        print("="*50 + "\n")
        sys.exit(1)

    # Start background threads ONLY ONCE
    if not threads_started:
        watchlist_thread = threading.Thread(target=redis_subscriber_loop, daemon=True)
        watchlist_thread.start()

        heartbeat_thread = threading.Thread(target=heartbeat_loop, daemon=True)
        heartbeat_thread.start()
        
        # Off by default. Read once at import, so an ingestor already running
        # keeps whatever it started with until it is restarted.
        if ENABLE_MOCK_TICKS:
            print("WARNING: ENABLE_MOCK_TICKS is on — fabricated prices will be "
                  "written to the live tables for symbols the feed does not carry.")
            mock_thread = threading.Thread(target=mock_tick_loop, daemon=True)
            mock_thread.start()
        else:
            print("Mock ticks disabled (ENABLE_MOCK_TICKS=False); only real feed data is stored.")
        
        threads_started = True

    while True:
        try:
            restart_required = False
            # Fresh connection attempt: clear the watchdog timer so it only
            # measures from the next actual close, not the previous outage.
            socket_connected = False
            disconnected_since = None

            # Wipe the singleton to prevent corruption carrying over
            data_ws.FyersDataSocket._instance = None
            
            access_token = get_active_session()
            fyers_socket_token = f"{require_app_id()}:{access_token}"

            fyers = data_ws.FyersDataSocket(
                access_token=fyers_socket_token,
                log_path=FYERS_LOG_PATH,
                litemode=False,
                write_to_file=False,
                reconnect=True,
                on_connect=onopen,
                on_close=onclose,
                on_error=onerror,
                on_message=onmessage,
            )

            print("INITIATING FYERS CONNECTION...")
            fyers.connect()

            # Keep-running loop with a disconnect watchdog. The SDK's internal
            # reconnect reuses the token this socket was built with — after
            # the daily token expiry that retries forever with a dead token,
            # while everything looks alive. If the socket stays down past the
            # threshold, force a full restart so get_active_session() pulls
            # the CURRENT token from the API (or honestly blocks until a new
            # broker login happens, with status reported as Disconnected).
            connect_started = time.monotonic()
            while not restart_required:
                time.sleep(1)

                # A watchlist change no longer rebuilds the connection; it
                # subscribes in place. This is the guard that catches the case
                # where that did not take.
                check_pending_subscriptions()

                if not socket_connected:
                    # Down since the last close — or, if it never opened at
                    # all (e.g. dead token at connect), since connect().
                    down_base = disconnected_since if disconnected_since is not None else connect_started
                    down_for = time.monotonic() - down_base
                    if down_for > DISCONNECT_RESTART_SECONDS:
                        print(
                            f"WATCHDOG: socket down for {int(down_for)}s — "
                            "forcing full reconnect with a fresh broker token."
                        )
                        restart_required = True

            print("RESTART FLAG DETECTED! CLOSING CURRENT FYERS CONNECTION...")
            try:
                fyers.close_connection()
            except Exception as e:
                print("Warning while closing connection:", e)
                
            time.sleep(1) # Small pause before rebuilding

        except Exception as ex:
            last_error_message = str(ex)
            print("ERROR IN CONNECTION MANAGER:", ex)
            traceback.print_exc()
            time.sleep(5)


if __name__ == "__main__":
    main()