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

#: The chain: every strike's price in one call, plus India VIX and the chain's
#: total call/put open interest. What it does NOT carry is per-strike open
#: interest — the rows are ask/bid/ltp/ltpch/ltpchp/strike_price/option_type and
#: nothing else. That was confirmed against the live endpoint, not assumed.
FYERS_CHAIN_URL = "https://api-t1.fyers.in/data/options-chain-v3"

#: Per-strike open interest, which lives here and only here. One symbol per
#: call — a comma-separated list is refused with a 400.
#:
#: Carries `oi`, `pdoi` (the previous day's close, the standard baseline for
#: "OI change"), `oipercent` and volume.
FYERS_DEPTH_URL = "https://api-t1.fyers.in/data/depth"

#: Depth calls per round.
#:
#: Measured against the live API: the 12th request inside a second is refused
#: with a 429, so the ceiling is about ten per second. Eight leaves room for the
#: chain call and for a retry without ever reaching for the limit.
DEPTH_CALLS_PER_ROUND = int(os.getenv("CHAIN_DEPTH_CALLS_PER_ROUND", "8"))

#: How many strikes either side of the money to ask for.
DEFAULT_STRIKE_COUNT = int(os.getenv("CHAIN_STRIKE_COUNT", "20"))

#: Seconds between polls. Open interest moves slowly compared with price — the
#: exchange itself updates it in bursts — so a few seconds loses nothing and
#: keeps well inside the broker's rate limits.
POLL_SECONDS = float(os.getenv("CHAIN_POLL_SECONDS", "5"))

#: How far to back off while the broker keeps refusing us.
#:
#: The usual reason is the daily token having expired, which stays true for
#: hours. At the normal interval that is the same line seven hundred times an
#: hour — a log nobody reads, and the one place the real field names get
#: reported when the poller first works. Backing off keeps the failure visible
#: without burying everything around it.
MAX_BACKOFF_SECONDS = float(os.getenv("CHAIN_MAX_BACKOFF_SECONDS", "60"))

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


def _whole(value: Optional[float]) -> Optional[int]:
    """
    A count, as an integer.

    Open interest and volume are counts of contracts and the platform stores
    them as integers, but the broker sends them through JSON as numbers — so
    `251580` arrives as `251580.0` and a whole batch is rejected by model
    binding, with a 400 that names nothing. Rounded rather than truncated: these
    are already whole, and `int()` on a float that happens to be 251579.9999
    would quietly lose one.
    """
    return None if value is None else int(round(value))


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
        # The day's move, as the broker reports it. Measured from the previous
        # close — the same basis as pdoi — so the build-up reading works from
        # the poller's very first round instead of waiting for a session's
        # worth of snapshots to have something to compare against.
        "priceChange": _first_number(row, "ltpch"),
        "bidPrice": _first_number(row, "bid", "bid_price", "bidPrice"),
        "askPrice": _first_number(row, "ask", "ask_price", "askPrice"),
        # Volume and open interest are NOT in this response — they come from a
        # separate per-symbol call and are merged in later. Left absent rather
        # than zeroed: a zero would be read as "nothing is written here".
        "sourceKey": DATA_PROVIDER_KEY,
    }


class ChainFetchError(RuntimeError):
    """The broker refused or could not answer. Usually an expired daily token."""


class RateLimited(ChainFetchError):
    """Asked for too much too quickly. Back off; nothing is wrong with the data."""


class OptionChainPoller:
    def __init__(self, api: Optional[PlatformApiClient] = None):
        self.api = api or PlatformApiClient(API_BASE_URL, verify_ssl=VERIFY_SSL)
        self.http = build_session()
        self._logged_shape = False
        self._consecutive_failures = 0
        self._depth_cursor = 0
        #: Last known OI per symbol, so a contract not asked about this round
        #: still reports the figure it had rather than dropping to null.
        self._last_depth: Dict[str, Dict[str, Any]] = {}

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
            raise ChainFetchError(f"HTTP {response.status_code} {response.text[:160]}")

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

    def fetch_depth(self, symbol: str, token: str) -> Optional[Dict[str, Any]]:
        """
        One contract's open interest and volume.

        `pdoi` is the previous day's closing open interest, which is what the
        market means by "OI change" — a cleaner baseline than the session's
        first snapshot, and one the broker computes rather than us.
        """
        response = requests.get(
            FYERS_DEPTH_URL,
            params={"symbol": symbol, "ohlcv_flag": "1"},
            headers={"Authorization": f"{require_app_id()}:{token}"},
            timeout=15,
        )

        if response.status_code == 429:
            raise RateLimited(f"depth rate limit reached on {symbol}")

        if response.status_code != 200:
            raise ChainFetchError(f"depth HTTP {response.status_code} for {symbol}")

        row = (response.json().get("d") or {}).get(symbol)
        if not isinstance(row, dict):
            return None

        return {
            "openInterest": _whole(_first_number(row, "oi")),
            "previousDayOpenInterest": _whole(_first_number(row, "pdoi")),
            "volume": _whole(_first_number(row, "v", "volume")),
        }

    def _depth_slice(self, rows: List[Dict[str, Any]], spot: float) -> List[str]:
        """
        The next few contracts to ask about — nearest the money first.

        Depth is one call per contract and the broker allows about ten a second,
        so a forty-strike chain cannot be refreshed every round. It does not need
        to be: open interest is republished by the exchange in bursts, so a
        contract revisited every twenty or thirty seconds loses nothing, while
        prices — which do move continuously — come from the chain call and are
        refreshed in full every round.

        The order is not round-robin over the raw list, which would leave the
        at-the-money strike waiting its turn behind strikes two thousand points
        out of the money. Contracts are visited in order of distance from the
        spot, so the rows anyone is actually looking at fill in first and stay
        the freshest.
        """
        if not rows:
            return []

        ordered = [
            r["symbol"]
            for r in sorted(rows, key=lambda r: (abs(float(r["strikePrice"]) - spot), r["symbol"]))
        ]

        start = self._depth_cursor % len(ordered)
        window = (ordered + ordered)[start:start + DEPTH_CALLS_PER_ROUND]
        self._depth_cursor = (start + len(window)) % len(ordered)
        return window

    def run_once(self, underlyings: List[str]) -> int:
        token = self.broker_token()
        if not token:
            print("CHAIN: broker not connected; nothing to poll.", flush=True)
            return 0

        written = 0
        failures = 0

        for underlying in underlyings:
            try:
                rows = self.fetch(underlying, token)
            except ChainFetchError as ex:
                failures += 1
                self._report_failure(underlying, str(ex))
                continue
            except Exception as ex:
                failures += 1
                self._report_failure(underlying, f"{type(ex).__name__}: {ex}")
                continue

            if not rows:
                continue

            # Open interest, for as much of the chain as the rate limit allows
            # this round. Everything else on the row is already current.
            self._merge_open_interest(rows, token)

            try:
                self.api.post_option_chain_snapshots(rows)
                written += len(rows)
            except Exception as ex:
                failures += 1
                self._report_failure(underlying, f"could not store {len(rows)} rows: {ex}")

        # A round where something came back resets the backoff, even if another
        # underlying failed — the broker is clearly reachable.
        if written:
            if self._consecutive_failures:
                print(f"CHAIN: recovered after {self._consecutive_failures} failed round(s).", flush=True)
            self._consecutive_failures = 0
        elif failures:
            self._consecutive_failures += 1

        return written

    def _merge_open_interest(self, rows: List[Dict[str, Any]], token: str) -> None:
        """
        Fills in open interest and volume from the depth endpoint.

        Only a slice of the chain is asked about each round — the broker allows
        about ten calls a second and a chain is forty-odd contracts. The rest
        carry their last known figure, which is honest: it IS what open interest
        was a few seconds ago, and OI is republished in bursts anyway. A contract
        never yet seen stays null rather than zero.
        """
        spot = float(rows[0].get("spotPrice") or 0)

        for symbol in self._depth_slice(rows, spot):
            try:
                depth = self.fetch_depth(symbol, token)
            except RateLimited as ex:
                # Not a data problem. Stop asking this round and carry on.
                self._report_failure(symbol, str(ex))
                break
            except Exception as ex:
                self._report_failure(symbol, f"depth failed: {ex}")
                continue

            if depth:
                self._last_depth[symbol] = depth

        for row in rows:
            depth = self._last_depth.get(row["symbol"])
            if not depth:
                continue
            row["openInterest"] = depth.get("openInterest")
            row["previousDayOpenInterest"] = depth.get("previousDayOpenInterest")
            row["volume"] = depth.get("volume")

    def _report_failure(self, underlying: str, detail: str) -> None:
        """
        Says it the first few times, then progressively less.

        The first occurrence is the one that matters and it is always printed;
        after that the same line repeating every few seconds only makes the log
        harder to read.
        """
        n = self._consecutive_failures
        if n < 3 or n % 20 == 0:
            suffix = f" (round {n + 1})" if n else ""
            print(f"CHAIN {underlying}: {detail}{suffix}", flush=True)

    def backoff_seconds(self) -> float:
        """
        How long to wait before the next round. Doubles per failed round, up to
        the ceiling; back to the normal interval as soon as anything succeeds.
        """
        if self._consecutive_failures == 0:
            return POLL_SECONDS
        return min(MAX_BACKOFF_SECONDS, POLL_SECONDS * (2 ** min(self._consecutive_failures, 8)))

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

            wait = self.backoff_seconds()
            time.sleep(max(0.5, wait - (time.monotonic() - started)))


if __name__ == "__main__":
    OptionChainPoller().run_forever()
