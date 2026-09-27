"""
analysis/api.py

The Forecasts endpoints, called as the engine's service account through
core.api_client (ENGINE_SERVICE_USERNAME / ENGINE_SERVICE_PASSWORD and
API_BASE_URL from the repo-root .env, exactly as the strategy runners sign in).

The API owns the clock that makes a forecast proof: it stamps IssuedUtc and
refuses a forecast once the session has opened, or an outcome before it has
closed (409). Those refusals are answers, not failures, and come back as such.
"""

from __future__ import annotations

from datetime import date
from typing import Any, Dict, List, NamedTuple, Optional


class Answer(NamedTuple):
    status: int
    body: Any
    message: str

    @property
    def ok(self) -> bool:
        return 200 <= self.status < 300

    @property
    def conflict(self) -> bool:
        return self.status == 409


class ApiError(RuntimeError):
    """The API could not be asked, or answered with something other than a refusal the contract names."""


class ForecastApi:
    def __init__(self, base_url: Optional[str] = None, http: Any = None, verify_ssl: Optional[bool] = None):
        from core.config import API_BASE_URL, VERIFY_SSL

        self.base_url = (base_url or API_BASE_URL).rstrip("/")
        self.verify = VERIFY_SSL if verify_ssl is None else verify_ssl
        if http is None:
            from core.api_client import build_session

            http = build_session()
        self.http = http

    def register_model(self, payload: Dict[str, Any]) -> Answer:
        return self._post("/api/Forecasts/models", payload)

    def issue(self, payload: Dict[str, Any]) -> Answer:
        return self._post("/api/Forecasts", payload)

    def post_outcome(self, forecast_id: int, payload: Dict[str, Any]) -> Answer:
        return self._post(f"/api/Forecasts/{int(forecast_id)}/outcome", payload)

    def forecasts(self, from_day: date, to_day: date) -> List[Dict[str, Any]]:
        return self._get_list("/api/Forecasts", {"from": from_day.isoformat(), "to": to_day.isoformat()})

    def models(self) -> List[Dict[str, Any]]:
        """Every registered model version: which v2 models the morning may issue."""
        return self._get_list("/api/Forecasts/models", None)

    def _get_list(self, path: str, params: Optional[Dict[str, str]]) -> List[Dict[str, Any]]:
        try:
            resp = self.http.get(f"{self.base_url}{path}", params=params, verify=self.verify, timeout=30)
        except Exception as ex:
            raise ApiError(f"GET {path} failed: {type(ex).__name__}: {ex}") from None
        if resp.status_code >= 400:
            raise ApiError(f"GET {path}: HTTP {resp.status_code} {_message(resp)}")
        body = _json(resp)
        if not isinstance(body, list):
            # The contract answers a list. Anything else read as "nothing to score" would leave every
            # forecast unscored without a word.
            raise ApiError(f"GET {path}: expected a list, got {type(body).__name__}")
        return body

    def _post(self, path: str, payload: Dict[str, Any]) -> Answer:
        try:
            resp = self.http.post(f"{self.base_url}{path}", json=payload, verify=self.verify, timeout=30)
        except Exception as ex:
            raise ApiError(f"POST {path} failed: {type(ex).__name__}: {ex}") from None
        return Answer(resp.status_code, _json(resp), _message(resp) if resp.status_code >= 400 else "")


def _json(resp: Any) -> Any:
    if not getattr(resp, "content", None):
        return None
    try:
        return resp.json()
    except ValueError:
        return None


def _message(resp: Any) -> str:
    """The API's {message} when it sent one (a JSON 403 is the module filter; an empty one the auth pipeline)."""
    body = _json(resp)
    if isinstance(body, dict):
        return str(body.get("message") or body.get("title") or body)
    text = (getattr(resp, "text", "") or "").strip()
    return text[:300] or getattr(resp, "reason", "") or "no body"
