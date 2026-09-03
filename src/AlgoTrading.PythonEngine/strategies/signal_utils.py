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
