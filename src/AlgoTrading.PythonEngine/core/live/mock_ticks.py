"""
Fabricated ticks for symbols no feed carries. Off unless asked for.

Lifted out of the FYERS streamer unchanged, gate and all. Invented prices go to
the same tables as real ones, and the only thing marking them is their
rawPayload, so the gate is what stands between a quiet chart and a fabricated
one — and it applies to every feed, not only the one it was written for.
"""

import hashlib
import random
import time
from datetime import datetime, timezone

from core.config import ENABLE_MOCK_TICKS


def should_mock_symbol(symbol: str, enabled: bool | None = None) -> bool:
    """
    Whether this symbol gets a fabricated price.

    Only symbols a broker feed cannot carry: anything not in "EXCHANGE:NAME"
    form, and continuous futures like "MCX:GOLD-FUT" that name no contract
    month. A real, dated option or index symbol must never match — fabricating a
    price for one would put an invented number in the same table as the real
    ones, indistinguishable to every strategy reading it.
    """
    if not (ENABLE_MOCK_TICKS if enabled is None else enabled):
        return False
    if ":" not in symbol:
        return True
    return "-FUT" in symbol and not any(char.isdigit() for char in symbol)


_SEED_PRICES = (
    ("GOLD", 153122.0), ("SILVER", 253400.0), ("COPPER", 1342.0),
    ("CRUDE", 7600.0), ("OIL", 7600.0), ("NATURALGAS", 291.0), ("IDEA", 14.67),
    ("NIFTY50", 23989.15), ("NIFTYBANK", 57297.15), ("SBIN", 1015.30),
)


class MockTickSource:
    """Static prices for the symbols the gate lets through, once a second."""

    def __init__(self, subscribed: callable, offer: callable):
        self._subscribed = subscribed
        self._offer = offer
        self._prices: dict[str, float] = {}
        self._opens: dict[str, float] = {}

    def _price_for(self, sym: str) -> float:
        if sym not in self._prices:
            seed = next((p for name, p in _SEED_PRICES if name in sym), None)
            if seed is None:
                seed = 50.0 + (int(hashlib.md5(sym.encode()).hexdigest(), 16) % 3450)
            self._prices[sym] = seed
            self._opens[sym] = seed * 0.995
        return self._prices[sym]

    def run_forever(self) -> None:
        while True:
            try:
                for sym in list(self._subscribed()):
                    if not should_mock_symbol(sym):
                        continue
                    # Not randomised, so the UI does not flicker.
                    price = self._price_for(sym)
                    opened = self._opens[sym]
                    self._offer({
                        "symbol": sym,
                        "dataType": "symbolUpdate",
                        "exchangeTimestampUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                        "lastTradedPrice": price,
                        "bidPrice": price - 0.5,
                        "askPrice": price + 0.5,
                        "bidSize": random.randint(1, 10),
                        "askSize": random.randint(1, 10),
                        "open": opened,
                        "high": max(opened, price * 1.005),
                        "low": min(opened, price * 0.99),
                        "prevClose": opened * 1.002,
                        "volume": random.randint(100, 5000),
                        # Says so in the row itself.
                        "rawPayload": '{"mock": true}',
                    })
            except Exception as ex:
                print("MOCK TICK ERROR:", ex, flush=True)
            time.sleep(1)
