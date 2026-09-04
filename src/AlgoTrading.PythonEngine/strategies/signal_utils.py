"""
strategies/signal_utils.py

Signal plumbing shared by the live runner and the backtest engine: run
parameter parsing, metadata stamping and the `/api/Simulator/signals` payload.
Pure Python, importable offline.
"""

from __future__ import annotations

import json
from typing import Any, Dict, Optional

from strategies.base_strategy import StrategyInput, StrategySignal


def parse_optional_number(value: Any) -> Optional[float]:
    """Float for numeric-looking values, None for null/blank/garbage."""
    if value is None:
        return None
    if isinstance(value, str) and not value.strip():
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def count_open_groups(state: Dict[str, Any]) -> int:
    """
    Best-effort count of open position groups from a strategy's state, using
    the conventions the bundled strategies follow. Unknown layouts yield 0.
    """
    if not isinstance(state, dict):
        return 0
    for key in ("open_groups", "active_groups", "groups"):
        value = state.get(key)
        if isinstance(value, (list, dict, set, tuple)):
            return len(value)
    if state.get("current_group_id"):
        return 1
    if state.get("is_invested") and state.get("group_id"):
        return 1
    return 0


def stamp_signal_metadata(sig: StrategySignal, inp: StrategyInput) -> None:
    """Every published signal carries its reason and the market context it fired in."""
    if sig.metadata is None:
        sig.metadata = {}
    sig.metadata["reason"] = sig.reason
    sig.metadata["spot_price"] = inp.spot_price
    sig.metadata["atm_strike"] = inp.atm_strike


def signal_to_ui_payload(sig: StrategySignal, timestamp_utc: str) -> Dict[str, Any]:
    """Body for the Strategy feed copy of a signal (what the live cards render)."""
    return {
        "timestamp_utc": timestamp_utc,
        "signal_type": sig.signal_type,
        "reason": sig.reason,
        "legs": sig.legs,
        "metadata": sig.metadata,
    }


class UiSignalPublisher:
    """
    Posts the dashboard copy of each signal to the run-scoped feed,
    `POST /api/Strategy/runs/{runId}/signals`. An API that predates that route
    answers a bare 404 (no JSON body): then, and only then, the publisher
    falls back to the legacy `POST /api/Strategy/{strategyId}/signals` and
    stays on it. A 404 *with* a `message` body comes from the new controller
    ("run is not active") and is reported, not re-routed — the legacy route
    could land the signal on a different run of the same strategy.

    `http` is any requests-like session (`post(url, json=..., timeout=...)`
    returning an object with `status_code` and `json()`), so the class is
    testable offline.
    """

    def __init__(self, http: Any, base_url: str, run_id: Optional[int], strategy_id: int,
                 timeout: float = 30.0) -> None:
        self._http = http
        self._base_url = base_url.rstrip("/")
        self._run_id = run_id
        self._strategy_id = strategy_id
        self._timeout = timeout
        # Legacy route pinned once the run route proved missing.
        self.use_legacy_route = run_id is None

    @property
    def run_url(self) -> str:
        return f"{self._base_url}/api/Strategy/runs/{self._run_id}/signals"

    @property
    def legacy_url(self) -> str:
        return f"{self._base_url}/api/Strategy/{self._strategy_id}/signals"

    def publish(self, payload: Dict[str, Any]) -> bool:
        """True when some route accepted the signal (2xx)."""
        if not self.use_legacy_route:
            resp = self._http.post(self.run_url, json=payload, timeout=self._timeout)
            status = int(getattr(resp, "status_code", 0) or 0)
            if 200 <= status < 300:
                return True
            if status == 404 and not _has_message_body(resp):
                print(
                    f"WARN: {self.run_url} not found on this API; "
                    f"falling back to {self.legacy_url} for the UI signal feed"
                )
                self.use_legacy_route = True
            else:
                print(f"WARN: UI signal feed answered {status} for run {self._run_id}: {_body_text(resp)}")
                return False

        resp = self._http.post(self.legacy_url, json=payload, timeout=self._timeout)
        status = int(getattr(resp, "status_code", 0) or 0)
        if 200 <= status < 300:
            return True
        print(f"WARN: UI signal feed answered {status} for strategy {self._strategy_id}: {_body_text(resp)}")
        return False


def _has_message_body(resp: Any) -> bool:
    """ASP.NET's unrouted 404 is empty; the controller's carries {message}."""
    try:
        body = resp.json()
    except Exception:
        return False
    return isinstance(body, dict) and bool(body.get("message"))


def _body_text(resp: Any) -> str:
    text = getattr(resp, "text", "")
    if not isinstance(text, str):
        return ""
    text = text.strip()
    return text[:200] if text else "(empty body)"


def signal_to_request(simulation_run_id: int, sig: StrategySignal) -> Dict[str, Any]:
    """Body for POST /api/Simulator/signals (leg quantities are lots)."""
    group_id = sig.metadata.get("group_id", "")

    return {
        "simulationRunId": simulation_run_id,
        "strategyName": sig.strategy_name,
        "signalType": sig.signal_type,
        "timestampUtc": sig.timestamp_utc,
        "groupId": group_id,
        "metadataJson": json.dumps(sig.metadata, default=str),
        "legs": sig.legs,
    }
