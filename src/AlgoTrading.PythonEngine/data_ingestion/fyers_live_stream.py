"""
live_data_ingestor.py

Connects to the FYERS WebSocket to consume live market data ticks.
Continuously fetches active watchlist symbols from the local .NET API, subscribes to them,
and pushes the received tick data back into the local database for strategy consumption.
Also manages a heartbeat mechanism to notify the API that the ingestor is healthy.
"""
import json
import time
import threading
import traceback
import urllib3
import requests
import sys
import os
from datetime import datetime, timezone
from fyers_apiv3.FyersWebsocket import data_ws

# Add the parent directory to sys.path so that imports resolve correctly
sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from core.config import (
    API_BASE_URL,
    VERIFY_SSL,
    DEFAULT_DATA_TYPE,
    DEBUG_PRINT_MESSAGES,
    WATCHLIST_REFRESH_SECONDS,
    SOURCE_NAME,
    HEARTBEAT_SECONDS,
    require_app_id,
)

from messaging.redis_publisher import build_publisher_from_env, normalize_tick

publisher = build_publisher_from_env()
publisher.ensure_connection()

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

# Global socket instance
fyers = None

# Track currently subscribed symbols
subscribed_symbols = set()

# Shared HTTP session
http = requests.Session()

# Heartbeat/watchlist state
last_watchlist_refresh_utc = None
last_error_message = ""
restart_required = False
threads_started = False


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


def upsert_tick(payload: dict):
    url = f"{API_BASE_URL}/api/LiveData/ticks/upsert"
    response = http.post(url, json=payload, verify=VERIFY_SSL, timeout=30)

    if response.status_code >= 400:
        print("TICK UPSERT FAILED:", response.status_code, response.text)
    else:
        print(f"TICK UPSERT OK: {payload.get('symbol')} | LTP: {payload.get('lastTradedPrice')}")


def send_heartbeat():
    """
    Send ingestor heartbeat/status into your .NET API.
    """
    payload = {
        "sourceName": SOURCE_NAME,
        "status": "Running",
        "lastHeartbeatUtc": utc_now_iso(),
        "lastWatchlistRefreshUtc": (
            last_watchlist_refresh_utc.isoformat().replace("+00:00", "Z")
            if last_watchlist_refresh_utc else None
        ),
        "currentSubscribedSymbols": sorted(list(subscribed_symbols)),
        "lastError": last_error_message
    }

    url = f"{API_BASE_URL}/api/LiveData/heartbeat"
    response = http.post(url, json=payload, verify=VERIFY_SSL, timeout=30)

    if response.status_code >= 400:
        print("HEARTBEAT FAILED:", response.status_code, response.text)
    else:
        print("HEARTBEAT OK")


def handle_live_tick(raw_msg: dict):
    normalized = normalize_tick(raw_msg)
    
    # Track when we got the real tick so the mock generator yields
    sym = normalized["symbol"]
    if sym:
        last_real_tick[sym] = time.time()

    # Publish all subscribed symbols to Redis
    publisher.publish_tick(normalized)


def map_message_to_payload(message: dict) -> dict | None:
    symbol = message.get("symbol")
    if not symbol:
        return None

    exchange_ts = None
    if message.get("last_traded_time"):
        try:
            exchange_ts = datetime.fromtimestamp(
                int(message["last_traded_time"]),
                tz=timezone.utc
            ).isoformat().replace("+00:00", "Z")
        except Exception:
            exchange_ts = None

    payload = {
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
        "rawPayload": json.dumps(message)
    }

    return payload


def onmessage(message):
    """
    Called whenever FYERS sends a live market-data update.
    """
    global last_error_message

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
    last_error_message = str(message)
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
    Unsubscribe removed symbols if SDK supports it.
    """
    if not symbols:
        return

    print("UNSUBSCRIBING SYMBOLS:", symbols)

    if hasattr(fyers, "unsubscribe"):
        try:
            fyers.unsubscribe(symbols=symbols, data_type=DEFAULT_DATA_TYPE)
        except TypeError:
            try:
                fyers.unsubscribe(symbols=symbols)
            except Exception as ex:
                print("UNSUBSCRIBE FAILED:", ex)
    else:
        print("WARNING: unsubscribe() not found in current FYERS SDK. Restart may be required for symbol removal.")


def sync_watchlist(force_subscribe=False):
    """
    Fetch active watchlist from DB and adjust subscriptions dynamically.
    """
    global subscribed_symbols
    global last_watchlist_refresh_utc
    global last_error_message

    try:
        desired_symbols = set(get_active_watchlist())

        if subscribed_symbols and desired_symbols != subscribed_symbols:
            print("WATCHLIST CHANGED!")
            print("FLAGGING CONNECTION RESTART TO AVOID FYERS BUG...")
            global restart_required
            restart_required = True

        if not subscribed_symbols or force_subscribe:
            # First run, just subscribe
            subscribe_symbols(list(desired_symbols))

        subscribed_symbols = desired_symbols
        last_watchlist_refresh_utc = datetime.now(timezone.utc)

        print("CURRENT SUBSCRIBED SYMBOLS:", sorted(subscribed_symbols))
        last_error_message = ""

    except Exception as ex:
        last_error_message = str(ex)
        print("ERROR SYNCING WATCHLIST:", ex)
        traceback.print_exc()


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


def heartbeat_loop():
    """
    Send heartbeat periodically so .NET API can show health/status.
    """
    global last_error_message

    while True:
        try:
            # Renew the singleton lock to keep this instance alive
            publisher.client.expire("fyers:live:ingestor:lock", 15)
            send_heartbeat()
        except Exception as ex:
            last_error_message = str(ex)
            print("ERROR IN HEARTBEAT LOOP:", ex)
            traceback.print_exc()

        time.sleep(HEARTBEAT_SECONDS)


def onopen():
    """
    On socket connect
    """
    global last_error_message

    try:
        last_error_message = ""
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

def mock_tick_loop():
    while True:
        try:
            for sym in list(subscribed_symbols):
                # Mock continuous futures, or symbols that don't look like valid Fyers formats
                needs_mocking = (":" not in sym) or ("-FUT" in sym and not any(char.isdigit() for char in sym))
                if needs_mocking:
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
        
        mock_thread = threading.Thread(target=mock_tick_loop, daemon=True)
        mock_thread.start()
        
        threads_started = True

    while True:
        try:
            restart_required = False
            
            # Wipe the singleton to prevent corruption carrying over
            data_ws.FyersDataSocket._instance = None
            
            access_token = get_active_session()
            fyers_socket_token = f"{require_app_id()}:{access_token}"

            fyers = data_ws.FyersDataSocket(
                access_token=fyers_socket_token,
                log_path="",
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

            # Our own robust keep_running loop
            while not restart_required:
                time.sleep(1)
                
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