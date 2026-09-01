from __future__ import annotations

import json
import os
import time
from datetime import datetime, timezone
from typing import Any, Dict, Optional
# pyrefly: ignore [missing-import]
import redis
from dotenv import load_dotenv
load_dotenv()

def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def extract_exchange(symbol: str) -> str:
    """
    Example:
    NSE:NIFTYBANK-INDEX -> NSE
    BSE:SENSEX -> BSE
    """
    if not symbol:
        return ""
    parts = symbol.split(":", 1)
    return parts[0].strip().upper() if len(parts) == 2 else ""


def safe_float(value: Any) -> Optional[float]:
    try:
        if value is None:
            return None
        return float(value)
    except Exception:
        return None


class RedisTickPublisher:
    """
    Publishes normalized market ticks into a Redis Stream.

    Stream layout:
      XADD market:ticks * 
        payload "{...json...}"
        symbol "NSE:NIFTYBANK-INDEX"
        exchange "NSE"
        dataType "symbolUpdate"
    """

    def __init__(
        self,
        host: str = "localhost",
        port: int = 6379,
        db: int = 0,
        password: Optional[str] = None,
        stream_name: str = "market:ticks",
        maxlen: int = 500_000,
        socket_timeout: int = 5,
        decode_responses: bool = True,
    ) -> None:
        self.stream_name = stream_name
        self.maxlen = maxlen

        self.client = redis.Redis(
            host=host,
            port=port,
            db=db,
            password=password,
            socket_timeout=socket_timeout,
            decode_responses=decode_responses,
        )

    def ping(self) -> bool:
        try:
            return bool(self.client.ping())
        except Exception:
            return False

    def ensure_connection(self, retries: int = 5, delay_seconds: float = 1.0) -> None:
        last_error: Optional[Exception] = None

        for attempt in range(1, retries + 1):
            try:
                if self.client.ping():
                    return
            except Exception as ex:
                last_error = ex

            time.sleep(delay_seconds)

        raise RuntimeError(f"Redis connection failed after {retries} retries. Last error: {last_error}")

    def publish_tick(self, tick: Dict[str, Any]) -> str:
        """
        Publish one normalized tick to Redis stream.

        Returns:
            Redis stream message ID
        """
        payload = json.dumps(tick, separators=(",", ":"), default=str)

        message_id = self.client.xadd(
            self.stream_name,
            {
                "payload": payload,
                "symbol": tick.get("symbol", ""),
                "exchange": tick.get("exchange", ""),
                "dataType": tick.get("dataType", "symbolUpdate"),
            },
            maxlen=self.maxlen,
            approximate=True,
        )

        return message_id

    def close(self) -> None:
        try:
            self.client.close()
        except Exception:
            pass


def normalize_tick(
    raw_msg: Dict[str, Any],
    *,
    symbol: Optional[str] = None,
    data_type: str = "symbolUpdate",
) -> Dict[str, Any]:
    """
    Convert raw incoming broker tick into a generic normalized payload.

    You may need to adjust field names depending on the exact FYERS message structure.

    Expected output example:
    {
      "exchange": "NSE",
      "symbol": "NSE:NIFTYBANK-INDEX",
      "dataType": "symbolUpdate",
      "exchangeTimestampUtc": "2026-06-09T08:31:05.200Z",
      "lastTradedPrice": 55050.60,
      "bidPrice": null,
      "askPrice": null,
      "bidSize": null,
      "askSize": null,
      "open": 54265,
      "high": 55070.90,
      "low": 54242.30,
      "close": 54063.75,
      "volume": null,
      "receivedUtc": "2026-06-09T08:31:05.215Z",
      "rawPayload": "{...}"
    }
    """
    resolved_symbol = symbol or raw_msg.get("symbol") or raw_msg.get("n") or ""
    exchange = extract_exchange(resolved_symbol)

    # Try different possible timestamp fields from upstream message
    exchange_ts = (
        raw_msg.get("exchangeTimestampUtc")
        or raw_msg.get("exchange_timestamp_utc")
        or raw_msg.get("timestamp")
        or raw_msg.get("t")
    )

    # If timestamp is epoch milliseconds or seconds, convert it
    if isinstance(exchange_ts, (int, float)):
        # heuristic
        if exchange_ts > 10_000_000_000:  # ms
            dt = datetime.fromtimestamp(exchange_ts / 1000, tz=timezone.utc)
        else:  # seconds
            dt = datetime.fromtimestamp(exchange_ts, tz=timezone.utc)
        exchange_ts = dt.isoformat()

    normalized = {
        "exchange": exchange,
        "symbol": resolved_symbol,
        "dataType": data_type or raw_msg.get("type") or "symbolUpdate",
        "exchangeTimestampUtc": exchange_ts,
        "lastTradedPrice": safe_float(
            raw_msg.get("lastTradedPrice")
            or raw_msg.get("ltp")
            or raw_msg.get("lp")
        ),
        "bidPrice": safe_float(raw_msg.get("bidPrice") or raw_msg.get("bp")),
        "askPrice": safe_float(raw_msg.get("askPrice") or raw_msg.get("ap")),
        "bidSize": safe_float(raw_msg.get("bidSize") or raw_msg.get("bs")),
        "askSize": safe_float(raw_msg.get("askSize") or raw_msg.get("as")),
        "open": safe_float(raw_msg.get("open") or raw_msg.get("o")),
        "high": safe_float(raw_msg.get("high") or raw_msg.get("h")),
        "low": safe_float(raw_msg.get("low") or raw_msg.get("l")),
        "close": safe_float(
            raw_msg.get("close")
            or raw_msg.get("prevClose")
            or raw_msg.get("c")
        ),
        "volume": safe_float(raw_msg.get("volume") or raw_msg.get("v")),
        "openInterest": safe_float(
            raw_msg.get("oi") 
            or raw_msg.get("open_interest")
            or raw_msg.get("min_oi")
            or raw_msg.get("openInterest")
        ),
        "impliedVolatility": safe_float(raw_msg.get("impliedVolatility")),
        "delta": safe_float(raw_msg.get("delta")),
        "gamma": safe_float(raw_msg.get("gamma")),
        "theta": safe_float(raw_msg.get("theta")),
        "vega": safe_float(raw_msg.get("vega")),
        "receivedUtc": utc_now_iso(),
        "rawPayload": json.dumps(raw_msg, default=str),
    }

    return normalized


def build_publisher_from_env() -> RedisTickPublisher:
    return RedisTickPublisher(
        host=os.getenv("REDIS_HOST", "localhost"),
        port=int(os.getenv("REDIS_PORT", "6379")),
        db=int(os.getenv("REDIS_DB", "0")),
        password=os.getenv("REDIS_PASSWORD") or None,
        stream_name=os.getenv("REDIS_STREAM_NAME", "market:ticks"),
        maxlen=int(os.getenv("REDIS_STREAM_MAXLEN", "500000")),
    )


if __name__ == "__main__":
    """
    Small standalone test:
    python redis_publisher.py
    """
    publisher = build_publisher_from_env()
    publisher.ensure_connection()

    sample_raw = {
        "symbol": "NSE:NIFTYBANK-INDEX",
        "ltp": 55050.60,
        "open": 54265,
        "high": 55070.90,
        "low": 54242.30,
        "prevClose": 54063.75,
        "volume": None,
        "timestamp": utc_now_iso(),
    }

    normalized = normalize_tick(sample_raw)
    msg_id = publisher.publish_tick(normalized)

    print("Published to Redis stream:", msg_id)
    publisher.close()