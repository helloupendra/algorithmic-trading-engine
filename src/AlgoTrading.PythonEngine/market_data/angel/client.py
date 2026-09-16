"""
market_data/angel/client.py

The SmartAPI calls this desk cares about: quotes, candles, historical open
interest, live option greeks and the ready-made scanners.

Every answer is unwrapped the same way — SmartAPI replies
{status, message, errorcode, data} with HTTP 200 even when it refuses, so a
failure is a message in the body, not a status code. `AngelError` carries the
vendor's own words.

Requests are paced (`min_interval`), because the published limits are per
second and per instrument, and a burst answers AB1004 rather than data.
Nothing here writes to the platform's database.
"""

from __future__ import annotations

import time
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from typing import Any, Callable, Dict, Iterable, List, Optional, Sequence

from market_data.angel.session import AngelSession

QUOTE_PATH = "/rest/secure/angelbroking/market/v1/quote/"
LTP_PATH = "/rest/secure/angelbroking/order/v1/getLtpData"
CANDLE_PATH = "/rest/secure/angelbroking/historical/v1/getCandleData"
OI_PATH = "/rest/secure/angelbroking/historical/v1/getOIData"
GREEK_PATH = "/rest/secure/angelbroking/marketData/v1/optionGreek"
GAINERS_PATH = "/rest/secure/angelbroking/marketData/v1/gainersLosers"
PCR_PATH = "/rest/secure/angelbroking/marketData/v1/putCallRatio"

#: SmartAPI's candle intervals, keyed by the platform's own resolution names.
INTERVALS = {
    "1m": "ONE_MINUTE", "3m": "THREE_MINUTE", "5m": "FIVE_MINUTE", "10m": "TEN_MINUTE",
    "15m": "FIFTEEN_MINUTE", "30m": "THIRTY_MINUTE", "1h": "ONE_HOUR", "60m": "ONE_HOUR", "D": "ONE_DAY",
}

#: Days of candles SmartAPI returns in ONE request, per interval (its published
#: limits). Asking for more silently truncates, so callers window their range.
MAX_DAYS_PER_REQUEST = {
    "ONE_MINUTE": 30, "THREE_MINUTE": 60, "FIVE_MINUTE": 100, "TEN_MINUTE": 100,
    "FIFTEEN_MINUTE": 200, "THIRTY_MINUTE": 200, "ONE_HOUR": 400, "ONE_DAY": 2000,
}

#: At most this many instruments in one quote call (SmartAPI's documented cap).
MAX_QUOTE_SYMBOLS = 50

#: SmartAPI answers a burst with HTTP 403 and a plain-text body, not JSON — the
#: historical endpoint is the strict one (2026-09-16: "Access denied because of
#: exceeding access rate" after a handful of calls). Waiting is the only cure.
RATE_LIMIT_WAITS = (2.0, 6.0, 15.0)


class AngelError(RuntimeError):
    """SmartAPI answered, and the answer was a refusal."""


class AngelRateLimited(AngelError):
    """SmartAPI is refusing for pace alone; the same call will work after a wait."""


@dataclass(frozen=True)
class Candle:
    """One OHLCV bar as SmartAPI returns it (its timestamps carry +05:30)."""
    timestamp: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float


def interval_for(resolution: str) -> str:
    key = (resolution or "").strip()
    if key in INTERVALS:
        return INTERVALS[key]
    lower = key.lower()
    if lower in INTERVALS:
        return INTERVALS[lower]
    if key.upper() in MAX_DAYS_PER_REQUEST:
        return key.upper()
    raise ValueError(f"Angel One serves {', '.join(sorted(set(INTERVALS)))}; '{resolution}' is none of them")


def windows(start: date, end: date, interval: str) -> List[tuple]:
    """[start, end] cut to what one request may carry for this interval."""
    span = MAX_DAYS_PER_REQUEST.get(interval, 30)
    out, lo = [], start
    while lo <= end:
        hi = min(lo + timedelta(days=span - 1), end)
        out.append((lo, hi))
        lo = hi + timedelta(days=1)
    return out


def parse_candles(rows: Iterable[Sequence]) -> List[Candle]:
    """[["2026-09-16T09:15:00+05:30", o, h, l, c, v], ...] -> typed bars."""
    out: List[Candle] = []
    for row in rows or []:
        if not row or len(row) < 6:
            continue
        try:
            out.append(Candle(datetime.fromisoformat(str(row[0])), float(row[1]), float(row[2]),
                              float(row[3]), float(row[4]), float(row[5])))
        except (TypeError, ValueError):
            continue
    return out


class AngelClient:
    """One logged-in SmartAPI session, with the data calls on top."""

    def __init__(self, session: Optional[AngelSession] = None, post: Optional[Callable] = None,
                 min_interval: float = 0.35, sleep=time.sleep, clock=time.monotonic) -> None:
        self.session = session or AngelSession()
        self._post = post
        self._min_interval = min_interval
        self._sleep = sleep
        self._clock = clock
        self._last_call = 0.0

    # ------------------------------------------------------------- plumbing

    def _poster(self) -> Callable:
        if self._post is not None:
            return self._post
        import requests

        def post(url: str, json: dict, headers: dict) -> dict:
            response = requests.post(url, json=json, headers=headers, timeout=60)
            try:
                return response.json()
            except ValueError:
                text = (response.text or "").strip()
                if response.status_code in (403, 429) and "rate" in text.lower():
                    raise AngelRateLimited(f"{text[:120]} (HTTP {response.status_code})") from None
                raise AngelError(f"SmartAPI answered HTTP {response.status_code}: {text[:120] or 'empty body'}") from None

        self._post = post
        return post

    def get(self, path: str) -> Any:
        """A SmartAPI GET (the profile endpoint refuses a POST), unwrapped like the rest."""
        import requests

        self.session.login()
        self._pace()
        response = requests.get(f"{self.session.credentials.root_url}{path}", headers=self.session.headers(),
                                timeout=30)
        try:
            answer = response.json()
        except ValueError:
            raise AngelError(f"{path}: HTTP {response.status_code} with no JSON") from None
        if not isinstance(answer, dict) or not answer.get("status"):
            message = str((answer or {}).get("message") or f"HTTP {response.status_code}")
            raise AngelError(f"{path}: {message}")
        return answer.get("data")

    def _pace(self) -> None:
        gap = self._min_interval - (self._clock() - self._last_call)
        if gap > 0:
            self._sleep(gap)
        self._last_call = self._clock()

    def call(self, path: str, body: dict) -> Any:
        """
        One SmartAPI call: paced, logged in, unwrapped, refusals raised.

        A rate-limit refusal is waited out rather than reported: the historical
        endpoint refuses a burst even when the account is fine, and a caller
        windowing two years of candles would otherwise stop on the first one.
        """
        self.session.login()
        for attempt, wait in enumerate((0.0,) + RATE_LIMIT_WAITS):
            if wait:
                self._sleep(wait)
            self._pace()
            try:
                answer = self._poster()(f"{self.session.credentials.root_url}{path}", body,
                                        self.session.headers())
                break
            except AngelRateLimited as ex:
                if attempt >= len(RATE_LIMIT_WAITS):
                    raise AngelRateLimited(f"{path}: {ex} — still rate-limited after "
                                           f"{sum(RATE_LIMIT_WAITS):.0f}s of waiting") from None
        if not isinstance(answer, dict):
            raise AngelError(f"SmartAPI returned {type(answer).__name__}, not an object")
        if not answer.get("status"):
            message = str(answer.get("message") or "no reason given")
            code = str(answer.get("errorcode") or "")
            raise AngelError(f"{path}: {message}{(' [' + code + ']') if code else ''}")
        return answer.get("data")

    # ----------------------------------------------------------------- data

    def ltp(self, exchange: str, trading_symbol: str, token: str) -> dict:
        """Last traded price for one instrument."""
        return self.call(LTP_PATH, {"exchange": exchange, "tradingsymbol": trading_symbol, "symboltoken": str(token)})

    def quotes(self, tokens_by_exchange: Dict[str, Sequence[str]], mode: str = "FULL") -> dict:
        """
        Quotes for up to MAX_QUOTE_SYMBOLS instruments at once.

        mode: LTP (price only), OHLC, or FULL — FULL is the one that carries
        depth, open interest and the day's totals.
        """
        total = sum(len(v) for v in tokens_by_exchange.values())
        if total > MAX_QUOTE_SYMBOLS:
            raise ValueError(f"SmartAPI takes at most {MAX_QUOTE_SYMBOLS} instruments per quote call, asked for {total}")
        payload = {"mode": mode.upper(), "exchangeTokens": {k: [str(t) for t in v] for k, v in tokens_by_exchange.items()}}
        return self.call(QUOTE_PATH, payload)

    def candles(self, exchange: str, token: str, resolution: str, start: datetime, end: datetime) -> List[Candle]:
        """Bars for ONE window; use `history` for a range longer than the interval allows."""
        interval = interval_for(resolution)
        data = self.call(CANDLE_PATH, {
            "exchange": exchange, "symboltoken": str(token), "interval": interval,
            "fromdate": start.strftime("%Y-%m-%d %H:%M"), "todate": end.strftime("%Y-%m-%d %H:%M"),
        })
        return parse_candles(data)

    def history(self, exchange: str, token: str, resolution: str, start: date, end: date,
                session_start: str = "09:15", session_end: str = "15:30") -> List[Candle]:
        """Bars for any range, one request per window the interval allows."""
        interval = interval_for(resolution)
        out: List[Candle] = []
        for lo, hi in windows(start, end, interval):
            out.extend(self.candles(
                exchange, token, interval,
                datetime.combine(lo, datetime.strptime(session_start, "%H:%M").time()),
                datetime.combine(hi, datetime.strptime(session_end, "%H:%M").time()),
            ))
        return out

    def open_interest(self, exchange: str, token: str, resolution: str, start: datetime, end: datetime) -> Any:
        """Historical open interest for a derivative — the series the chain recorder cannot go back for."""
        return self.call(OI_PATH, {
            "exchange": exchange, "symboltoken": str(token), "interval": interval_for(resolution),
            "fromdate": start.strftime("%Y-%m-%d %H:%M"), "todate": end.strftime("%Y-%m-%d %H:%M"),
        })

    def option_greeks(self, underlying: str, expiry: date) -> Any:
        """Delta, gamma, theta, vega and IV for every strike of one live expiry."""
        return self.call(GREEK_PATH, {"name": underlying.upper(), "expirydate": expiry.strftime("%d%b%Y").upper()})

    def gainers_losers(self, data_type: str = "PercPriceGainers", expiry_type: str = "NEAR") -> Any:
        """Angel's own scanner: PercPriceGainers/Losers, PercOIGainers/Losers."""
        return self.call(GAINERS_PATH, {"datatype": data_type, "expirytype": expiry_type})

    def put_call_ratio(self) -> Any:
        """PCR per underlying, as Angel computes it."""
        return self.call(PCR_PATH, {})
