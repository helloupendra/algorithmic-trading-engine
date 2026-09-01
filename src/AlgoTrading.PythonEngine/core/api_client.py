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
    """Fetches and caches the service account's access token, thread-safely."""

    def __init__(self) -> None:
        self._token: Optional[str] = None
        self._lock = threading.Lock()

    @staticmethod
    def _credentials() -> tuple[str, str]:
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
                raise ServiceCredentialsError(
                    f"Service account '{username}' was rejected by the API. "
                    "Check ENGINE_SERVICE_PASSWORD in .env matches the account."
                )
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
    """A requests.Session that keeps a valid bearer token attached."""

    def request(self, method: str, url: str, *args: Any, **kwargs: Any) -> requests.Response:  # type: ignore[override]
        # Never attach a token to the login call itself — that would recurse.
        if any(path in str(url) for path in _AUTH_PATHS):
            return super().request(method, url, *args, **kwargs)

        kwargs.setdefault("verify", VERIFY_SSL)

        headers = dict(kwargs.pop("headers", None) or {})
        headers["Authorization"] = f"Bearer {_provider.get()}"

        response = super().request(method, url, *args, headers=headers, **kwargs)

        # Token expired or the API restarted with a new key: re-login once.
        if response.status_code == 401:
            _provider.clear()
            headers["Authorization"] = f"Bearer {_provider.get(force_refresh=True)}"
            response = super().request(method, url, *args, headers=headers, **kwargs)

        return response


def build_session() -> AuthenticatedSession:
    """A session that authenticates every request to the trading API."""
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
    def __init__(self, base_url: str, verify_ssl: bool = False):
        self.base_url = base_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self.http = build_session()

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

    def get_exact_contract(
        self,
        underlying: str,
        expiry: str,
        strike: int,
        option_type: str,
    ) -> dict[str, Any]:
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
