"""
Polls the broker's option chain and records open interest.

Why this exists at all: the FYERS websocket does not carry open interest. A live
`SymbolUpdate` message has ltp, bid, ask, volume and the day's OHLC — that is
the whole of it. Every screen that talks about OI, OI change, PCR, max pain or
"long build / short cover" needs a number the tick feed has never once sent, so
it has to be fetched separately.

What it writes is deliberately a TIME SERIES, not a current-value cache. The
chain screen only needs the latest row, but the intraday OI-change curves need a
day of them, and a backtest needs the row that was true at the replay clock.
None of that can be reconstructed later: a strike's open interest at 10:15 is
knowable only because something wrote it down at 10:15.
"""

from __future__ import annotations

import sys
import os
import time
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional

sys.path.append(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..")))

from core.safe_output import install_safe_stdio

install_safe_stdio(name="chain-poller")

import requests

from core.api_client import build_session, PlatformApiClient
from core.config import API_BASE_URL, VERIFY_SSL, DATA_PROVIDER_KEY, require_app_id
from core.option_symbol import parse_option_symbol

#: FYERS' option chain. The v3 path; probed and confirmed present (it answers
#: 401 without a token rather than 404).
FYERS_CHAIN_URL = "https://api-t1.fyers.in/data/options-chain-v3"

#: How many strikes either side of the money to ask for.
DEFAULT_STRIKE_COUNT = int(os.getenv("CHAIN_STRIKE_COUNT", "20"))

#: Seconds between polls. Open interest moves slowly compared with price — the
#: exchange itself updates it in bursts — so a few seconds loses nothing and
#: keeps well inside the broker's rate limits.
POLL_SECONDS = float(os.getenv("CHAIN_POLL_SECONDS", "5"))

#: The underlyings to track, spot symbol per underlying.
DEFAULT_UNDERLYINGS = [u.strip().upper() for u in
                       (os.getenv("CHAIN_UNDERLYINGS") or "BANKNIFTY,NIFTY").split(",") if u.strip()]

SPOT_SYMBOLS = {
    "NIFTY": "NSE:NIFTY50-INDEX",
    "BANKNIFTY": "NSE:NIFTYBANK-INDEX",
    "FINNIFTY": "NSE:FINNIFTY-INDEX",
    "MIDCPNIFTY": "NSE:MIDCPNIFTY-INDEX",
    "SENSEX": "BSE:SENSEX-INDEX",
    "BANKEX": "BSE:BANKEX-INDEX",
}


def _first_number(row: Dict[str, Any], *names: str) -> Optional[float]:
    """
    The first of several field names that carries a usable number.

    Brokers rename these between versions — `oi`, `openInterest`, `open_interest`
    have all been the same field — and a poller that knows only one spelling
    fails by writing nulls rather than by complaining. Trying each is cheap and
    survives the rename.
    """
    for name in names:
        if name not in row:
            continue
        value = row[name]
        if value is None or value == "":
            continue
        try:
            return float(value)
        except (TypeError, ValueError):
            continue
    return None


def normalise_chain_row(row: Dict[str, Any], spot: float) -> Optional[Dict[str, Any]]:
    """
    One broker chain row as the platform's snapshot shape, or None if it is not
    an option row (the response mixes the underlying in with the strikes).
    """
    symbol = (row.get("symbol") or row.get("Symbol") or "").strip()
    contract = parse_option_symbol(symbol)
    if contract is None:
        return None

    return {
        "underlying": contract.underlying,
        "expiryDate": contract.expiry.isoformat(),
        "strikePrice": contract.strike,
        "optionType": contract.kind,
        "symbol": symbol,
        "spotPrice": spot,
        "lastTradedPrice": _first_number(row, "ltp", "last_price", "lastPrice"),
        "bidPrice": _first_number(row, "bid", "bid_price", "bidPrice"),
        "askPrice": _first_number(row, "ask", "ask_price", "askPrice"),
        "volume": _first_number(row, "volume", "vol_traded_today", "totalTradedVolume"),
        "openInterest": _first_number(row, "oi", "openInterest", "open_interest"),
        "sourceKey": DATA_PROVIDER_KEY,
    }


class OptionChainPoller:
    def __init__(self, api: Optional[PlatformApiClient] = None):
        self.api = api or PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)
        self.http = build_session()
        self._logged_shape = False

    def broker_token(self) -> Optional[str]:
        try:
            session = self.http.get(f"{API_BASE_URL}/api/auth/session",
                                    verify=VERIFY_SSL, timeout=10).json()
        except Exception as ex:
            print(f"WARN: could not read the broker session: {ex}", flush=True)
            return None

        if not session.get("isAuthenticated"):
            return None
        return session.get("accessToken") or None

    def fetch(self, underlying: str, token: str) -> List[Dict[str, Any]]:
        """The broker's chain for one underlying, already normalised."""
        spot_symbol = SPOT_SYMBOLS.get(underlying)
        if not spot_symbol:
            return []

        response = requests.get(
            FYERS_CHAIN_URL,
            params={"symbol": spot_symbol, "strikecount": DEFAULT_STRIKE_COUNT, "timestamp": ""},
            headers={"Authorization": f"{require_app_id()}:{token}"},
            timeout=20,
        )

        if response.status_code != 200:
            print(f"CHAIN {underlying}: HTTP {response.status_code} {response.text[:160]}", flush=True)
            return []

        payload = response.json()
        data = payload.get("data") or {}
        rows = data.get("optionsChain") or data.get("options_chain") or []

        # Say what the broker actually sent, once. The field names are the part
        # most likely to have moved, and a silent poller writing nulls is the
        # failure this print exists to prevent.
        if rows and not self._logged_shape:
            self._logged_shape = True
            print(f"CHAIN {underlying}: broker row fields = {sorted(rows[0].keys())}", flush=True)

        spot = _first_number(data, "indiavixData", "underlyingValue") or 0.0
        for row in rows:
            if not parse_option_symbol((row.get("symbol") or "")):
                spot = _first_number(row, "ltp") or spot
                break

        out = []
        for row in rows:
            normalised = normalise_chain_row(row, spot)
            if normalised:
                out.append(normalised)
        return out

    def run_once(self, underlyings: List[str]) -> int:
        token = self.broker_token()
        if not token:
            print("CHAIN: broker not connected; nothing to poll.", flush=True)
            return 0

        written = 0
        for underlying in underlyings:
            try:
                rows = self.fetch(underlying, token)
            except Exception as ex:
                print(f"CHAIN {underlying}: fetch failed: {ex}", flush=True)
                continue

            if not rows:
                continue

            try:
                self.api.post_option_chain_snapshots(rows)
                written += len(rows)
            except Exception as ex:
                print(f"CHAIN {underlying}: could not store {len(rows)} rows: {ex}", flush=True)

        return written

    def run_forever(self, underlyings: Optional[List[str]] = None) -> None:
        targets = underlyings or DEFAULT_UNDERLYINGS
        print(f"CHAIN POLLER started for {', '.join(targets)} every {POLL_SECONDS:g}s", flush=True)

        while True:
            started = time.monotonic()
            try:
                written = self.run_once(targets)
                if written:
                    print(f"CHAIN: stored {written} strike rows at "
                          f"{datetime.now(timezone.utc).isoformat(timespec='seconds')}", flush=True)
            except Exception as ex:
                # A poll that fails is a gap in a chart, never a reason to stop
                # recording the rest of the session.
                print(f"CHAIN: poll failed: {ex}", flush=True)

            time.sleep(max(0.5, POLL_SECONDS - (time.monotonic() - started)))


if __name__ == "__main__":
    OptionChainPoller().run_forever()
