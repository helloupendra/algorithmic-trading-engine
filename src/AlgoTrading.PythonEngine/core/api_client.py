"""
core/api_client.py

Authenticated HTTP access to AlgoTrading.Api.

Every API endpoint now requires a bearer token, so the engine signs in as a
dedicated service account and attaches the token to each request. Tokens are
short-lived, so the session refreshes automatically: a 401 triggers one re-login
and a single retry, which also covers the case where the API was restarted with
a new signing key.

Usage — the session is a drop-in replacement for `requests.Session()`:

    from core.api_client import build_session
    http = build_session()
    http.get(f"{API_BASE_URL}/api/LiveData/watchlist")

or, for one-off calls in the operational tools:

    from core.api_client import api_get, api_post, api_delete
"""

from __future__ import annotations

import os
import threading
from typing import Any, Optional

import requests

from core.config import API_BASE_URL, VERIFY_SSL

# Requests that must never carry (or trigger) authentication.
_AUTH_PATHS = ("/api/UserAuth/login", "/api/UserAuth/refresh")


class ServiceCredentialsError(RuntimeError):
    """Raised when the engine has no usable service credentials."""


class _TokenProvider:
    """
    Fetches and caches an account's access token, thread-safely. Without
    explicit credentials it signs in as the engine service account
    (ENGINE_SERVICE_USERNAME / ENGINE_SERVICE_PASSWORD).
    """

    def __init__(self, username: Optional[str] = None, password: Optional[str] = None) -> None:
        self._token: Optional[str] = None
        self._lock = threading.Lock()
        self._username = (username or "").strip() or None
        self._password = password or None

    def _credentials(self) -> tuple[str, str]:
        if self._username and self._password:
            return self._username, self._password

        username = (os.getenv("ENGINE_SERVICE_USERNAME") or "").strip()
        password = os.getenv("ENGINE_SERVICE_PASSWORD") or ""

        if not username or not password:
            raise ServiceCredentialsError(
                "ENGINE_SERVICE_USERNAME / ENGINE_SERVICE_PASSWORD are not set.\n"
                "  The API now requires authentication for every endpoint.\n"
                "  Add both to the repo-root .env (see .env.example), then restart\n"
                "  the API so it provisions the service account."
            )
        return username, password

    def get(self, force_refresh: bool = False) -> str:
        with self._lock:
            if self._token and not force_refresh:
                return self._token

            username, password = self._credentials()
            response = requests.post(
                f"{API_BASE_URL}/api/UserAuth/login",
                json={"userNameOrEmail": username, "password": password},
                timeout=15,
                verify=VERIFY_SSL,
            )

            if response.status_code == 400 or response.status_code == 401:
                hint = (
                    "Check the username/password you passed."
                    if self._username
                    else "Check ENGINE_SERVICE_PASSWORD in .env matches the account."
                )
                raise ServiceCredentialsError(f"Account '{username}' was rejected by the API. {hint}")
            response.raise_for_status()

            token = response.json().get("accessToken")
            if not token:
                raise ServiceCredentialsError("Login succeeded but returned no access token.")

            self._token = token
            return token

    def clear(self) -> None:
        with self._lock:
            self._token = None


_provider = _TokenProvider()


class AuthenticatedSession(requests.Session):
    """
    A requests.Session that keeps a valid bearer token attached. By default the
    shared service-account provider is used; a session built with its own
    credentials (see PlatformApiClient) carries its own provider.
    """

    def __init__(self, provider: Optional[_TokenProvider] = None) -> None:
        super().__init__()
        self._provider = provider or _provider

    def request(self, method: str, url: str, *args: Any, **kwargs: Any) -> requests.Response:  # type: ignore[override]
        # Never attach a token to the login call itself — that would recurse.
        if any(path in str(url) for path in _AUTH_PATHS):
            return super().request(method, url, *args, **kwargs)

        kwargs.setdefault("verify", VERIFY_SSL)

        headers = dict(kwargs.pop("headers", None) or {})
        headers["Authorization"] = f"Bearer {self._provider.get()}"

        response = super().request(method, url, *args, headers=headers, **kwargs)

        # Token expired or the API restarted with a new key: re-login once.
        if response.status_code == 401:
            self._provider.clear()
            headers["Authorization"] = f"Bearer {self._provider.get(force_refresh=True)}"
            response = super().request(method, url, *args, headers=headers, **kwargs)

        return response


def build_session(username: Optional[str] = None, password: Optional[str] = None) -> AuthenticatedSession:
    """
    A session that authenticates every request to the trading API — as the
    engine service account, or as `username` when credentials are given (the
    terminal tools sign in as an admin for the admin-only endpoints).
    """
    if username and password:
        return AuthenticatedSession(_TokenProvider(username, password))
    return AuthenticatedSession()


# A shared session for the small operational tools, which make one or two calls.
_shared = build_session()


def api_get(url: str, **kwargs: Any) -> requests.Response:
    return _shared.get(url, **kwargs)


def api_post(url: str, **kwargs: Any) -> requests.Response:
    return _shared.post(url, **kwargs)


def api_delete(url: str, **kwargs: Any) -> requests.Response:
    return _shared.delete(url, **kwargs)


def api_put(url: str, **kwargs: Any) -> requests.Response:
    return _shared.put(url, **kwargs)

class PlatformApiClient:
    """
    Typed access to the endpoints the engine uses. Signs in as the engine
    service account unless `username`/`password` are given — the backtest
    terminal wrapper passes an admin's credentials because POST
    /api/Backtest/runs is admin-only and the service account is a Trader.
    """

    def __init__(self, base_url: str, verify_ssl: bool = False,
                 username: Optional[str] = None, password: Optional[str] = None):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = build_session(username, password)

    def get_latest_quote(self, symbol: str) -> dict[str, Any]:
        resp = self.http.get(
            f"{self.base_url}/api/LiveData/latest",
            params={"symbol": symbol},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_recent_bars(self, symbol: str, resolution: str = "1m", take: int = 1) -> list[dict[str, Any]]:
        resp = self.http.get(
            f"{self.base_url}/api/LiveData/bars",
            params={"symbol": symbol, "resolution": resolution, "take": take},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def upsert_watchlist(self, symbol: str, priority: int = 50) -> dict[str, Any]:
        payload = {
            "symbol": symbol,
            "dataType": "symbolUpdate",
            "isActive": True,
            "priority": priority
        }

        resp = self.http.post(
            f"{self.base_url}/api/LiveData/watchlist",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_expiries(self, underlying: str) -> list[dict[str, Any]]:
        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/expiries",
            params={"underlying": underlying},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_option_chain(
        self,
        underlying: str,
        expiry: str,
        from_strike: Optional[float] = None,
        to_strike: Optional[float] = None,
    ) -> list[dict[str, Any]]:
        """
        Every CE/PE contract of one expiry (optionally bounded by strike).
        Used once at startup to derive the underlying's strike step.
        """
        params: dict[str, Any] = {"underlying": underlying, "expiry": expiry}
        if from_strike is not None:
            params["fromStrike"] = from_strike
        if to_strike is not None:
            params["toStrike"] = to_strike

        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/chain",
            params=params,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_fno_underlyings(self) -> list[dict[str, Any]]:
        """F&O inventory: [{underlying, exchange, spotSymbol, lotSize, lotSizeSource, strikeStep, nextExpiry, ...}]."""
        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/underlyings",
            verify=self.verify_ssl,
            timeout=60,
        )
        resp.raise_for_status()
        return resp.json() or []

    def get_exact_contract(
        self,
        underlying: str,
        expiry: str,
        strike: float,
        option_type: str,
    ) -> dict[str, Any]:
        # `strike` may be fractional (stock options on a 2.5-point grid); the API
        # binds it as a decimal and matches it exactly.
        resp = self.http.get(
            f"{self.base_url}/api/Instruments/derivatives/contract",
            params={
                "underlying": underlying,
                "expiry": expiry,
                "strike": strike,
                "optionType": option_type,
            },
            verify=self.verify_ssl,
            timeout=30,
        )
        if resp.status_code == 404:
            return None
        resp.raise_for_status()
        return resp.json()

    def create_simulation_signal(self, payload: dict[str, Any]) -> dict[str, Any]:
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/signals",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def get_simulation_run(self, run_id: int) -> dict[str, Any]:
        resp = self.http.get(
            f"{self.base_url}/api/Simulator/runs/{run_id}",
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    def create_simulation_run(self, payload: dict[str, Any]) -> dict[str, Any]:
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs",
            json=payload,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json()

    # --- Historical candles (backtesting) ---------------------------------

    def get_local_history(self, symbol: str, resolution: str, from_date: str, to_date: str) -> list[dict[str, Any]]:
        """
        Stored candles for one symbol at a canonical resolution ("5", "D").
        Dates are yyyy-MM-dd; to_date is inclusive. Rows carry
        {symbol, resolution, timestampUtc, open, high, low, close, volume}.
        """
        resp = self.http.get(
            f"{self.base_url}/api/MarketData/history/local",
            params={"symbol": symbol, "resolution": resolution, "fromDate": from_date, "toDate": to_date},
            verify=self.verify_ssl,
            timeout=120,
        )
        resp.raise_for_status()
        return resp.json() or []

    def sync_history(self, symbol: str, resolution: str, from_date: str, to_date: str) -> list[dict[str, Any]]:
        """Pull candles from FYERS into the local store and return them (needs a broker session)."""
        resp = self.http.post(
            f"{self.base_url}/api/MarketData/history/sync",
            json={"symbol": symbol, "resolution": resolution, "fromDate": from_date, "toDate": to_date},
            verify=self.verify_ssl,
            timeout=180,
        )
        if resp.status_code >= 400:
            # Surface the API's own reason ("FYERS history API failed. HTTP 422:
            # Invalid symbol provided" for an expired contract, "No valid FYERS
            # session" when the broker is not linked) instead of the bare
            # "400 Client Error" that requests would raise, so callers can branch.
            detail = ""
            try:
                payload = resp.json()
                if isinstance(payload, dict):
                    detail = str(payload.get("message") or payload.get("title") or "")
            except ValueError:
                detail = resp.text[:300]
            raise RuntimeError(f"history sync HTTP {resp.status_code}: {detail or resp.reason}")
        body = resp.json()
        return body if isinstance(body, list) else []

    def get_broker_session(self) -> dict[str, Any]:
        """Broker session status ({isAuthenticated, ...}); raises when the API is unreachable."""
        resp = self.http.get(
            f"{self.base_url}/api/auth/session",
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json() or {}

    # --- Offline-replay bookkeeping (backtest runner -> Simulator) --------

    def post_equity_snapshots(self, run_id: int, items: list[dict[str, Any]]) -> Any:
        """Bulk insert (<= 5000) of {snapshotUtc, realizedPnl, unrealizedPnl, usedCapital, openPositions, closedPositions}."""
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs/{run_id}/equity-snapshots",
            json=items,
            verify=self.verify_ssl,
            timeout=120,
        )
        resp.raise_for_status()
        return _json_or_none(resp)

    def post_marks(self, run_id: int, at_utc: str, marks: list[dict[str, Any]]) -> Any:
        """Mark the run's open positions: {atUtc, marks: [{symbol, price}]}."""
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs/{run_id}/marks",
            json={"atUtc": at_utc, "marks": marks},
            verify=self.verify_ssl,
            timeout=60,
        )
        resp.raise_for_status()
        return _json_or_none(resp)

    def post_progress(self, run_id: int, progress: dict[str, Any]) -> Any:
        """{percent, barsProcessed, totalBars, currentUtc, trades, message}; 404 when the run is not running."""
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs/{run_id}/progress",
            json=progress,
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return _json_or_none(resp)

    def complete_run(self, run_id: int, status: str, summary: dict[str, Any], error: Optional[str] = None) -> Any:
        """Finish an OfflineReplay run: status "Completed" | "Failed", optional error, summary object."""
        payload: dict[str, Any] = {"status": status, "summary": summary}
        if error:
            payload["error"] = error
        resp = self.http.post(
            f"{self.base_url}/api/Simulator/runs/{run_id}/complete",
            json=payload,
            verify=self.verify_ssl,
            timeout=60,
        )
        resp.raise_for_status()
        return _json_or_none(resp)

    # --- Backtest module (terminal wrapper) -------------------------------

    def get_strategy_catalog(self) -> list[dict[str, Any]]:
        resp = self.http.get(
            f"{self.base_url}/api/Strategy",
            verify=self.verify_ssl,
            timeout=60,
        )
        resp.raise_for_status()
        return resp.json() or []

    def start_backtest(self, payload: dict[str, Any]) -> dict[str, Any]:
        """POST /api/Backtest/runs -> {runId, message}; raises with the API's message on 4xx."""
        resp = self.http.post(
            f"{self.base_url}/api/Backtest/runs",
            json=payload,
            verify=self.verify_ssl,
            timeout=60,
        )
        if resp.status_code >= 400:
            raise RuntimeError(f"{resp.status_code}: {_error_message(resp)}")
        return resp.json()

    def get_backtest_run(self, run_id: int) -> dict[str, Any]:
        resp = self.http.get(
            f"{self.base_url}/api/Backtest/runs/{run_id}",
            verify=self.verify_ssl,
            timeout=60,
        )
        resp.raise_for_status()
        return resp.json()

    def get_backtest_logs(self, run_id: int, take: int = 200) -> list[str]:
        resp = self.http.get(
            f"{self.base_url}/api/Backtest/runs/{run_id}/logs",
            params={"take": take},
            verify=self.verify_ssl,
            timeout=30,
        )
        resp.raise_for_status()
        return resp.json() or []


def _json_or_none(resp: requests.Response) -> Any:
    """Body as JSON when there is one (204 / empty bodies yield None)."""
    if not resp.content:
        return None
    try:
        return resp.json()
    except ValueError:
        return None


def _error_message(resp: requests.Response) -> str:
    """The API's {message} when present, else the raw body / status text."""
    try:
        body = resp.json()
        if isinstance(body, dict):
            return str(body.get("message") or body.get("title") or body)
        return str(body)
    except ValueError:
        return resp.text.strip() or resp.reason or "request failed"
