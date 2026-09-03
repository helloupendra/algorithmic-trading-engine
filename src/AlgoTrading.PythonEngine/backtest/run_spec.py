"""
backtest/run_spec.py

The backtest configuration as parsed from a SimulationRun row (Mode
"OfflineReplay"): the API writes strategy defaults + user overrides +
{lots, stop_loss, target, underlying, resolution, eod_square_off_ist,
charges_per_lot} into `parametersJson`, and the spot symbol / canonical
resolution / UTC range into the row itself.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import date, datetime, time
from typing import Any, Dict, Optional

from core.resolutions import to_candle_resolution, to_strategy_resolution
from strategies.base_strategy import BaseStrategy
from strategies.signal_utils import parse_optional_number

from backtest.timeutil import ist_date, parse_hhmm, parse_utc

DEFAULT_EOD_SQUARE_OFF_IST = "15:15"
DEFAULT_INITIAL_CAPITAL = 1_000_000.0

# Spot symbol -> underlying, for rows whose parametersJson lacks "underlying".
_SPOT_UNDERLYINGS = {
    "NIFTYBANK": "BANKNIFTY",
    "NIFTY50": "NIFTY",
    "FINNIFTY": "FINNIFTY",
    "MIDCPNIFTY": "MIDCPNIFTY",
    "SENSEX": "SENSEX",
}


def underlying_from_spot(spot_symbol: str) -> str:
    """"NSE:NIFTYBANK-INDEX" -> "BANKNIFTY"; "NSE:RELIANCE-EQ" -> "RELIANCE"."""
    text = (spot_symbol or "").strip().upper()
    for token, underlying in _SPOT_UNDERLYINGS.items():
        if token in text:
            return underlying
    if ":" in text:
        text = text.split(":", 1)[1]
    return text.split("-", 1)[0]


@dataclass
class BacktestRun:
    run_id: int
    strategy_name: str
    spot_symbol: str
    underlying: str
    resolution_code: str          # canonical: "1" | "5" | "15" | "D"
    resolution: str               # strategy-facing: "1m" | "5m" | "15m" | "1D"
    from_utc: datetime
    to_utc: datetime
    from_date: date               # IST calendar days
    to_date: date
    lots: int = 1
    stop_loss: Optional[float] = None
    target: Optional[float] = None
    eod_square_off_ist: str = DEFAULT_EOD_SQUARE_OFF_IST
    charges_per_lot: float = 0.0
    initial_capital: float = DEFAULT_INITIAL_CAPITAL
    user_id: Optional[int] = None
    params: Dict[str, Any] = field(default_factory=dict)

    @property
    def eod_square_off(self) -> Optional[time]:
        return parse_hhmm(self.eod_square_off_ist)

    def describe(self) -> str:
        return (
            f"strategy={self.strategy_name} run_id={self.run_id} underlying={self.underlying} "
            f"spot_symbol={self.spot_symbol} resolution={self.resolution} "
            f"range={self.from_date.isoformat()}..{self.to_date.isoformat()} lots={self.lots} "
            f"stop_loss={self.stop_loss if self.stop_loss is not None else 'none'} "
            f"target={self.target if self.target is not None else 'none'} "
            f"eod_square_off_ist={self.eod_square_off_ist or 'none'} "
            f"charges_per_lot={self.charges_per_lot:g} initial_capital={self.initial_capital:g}"
        )


def parse_parameters(raw: Any) -> Dict[str, Any]:
    """parametersJson (string or object) -> dict; anything unusable -> {}."""
    if raw is None:
        return {}
    if isinstance(raw, dict):
        return dict(raw)
    try:
        parsed = json.loads(raw) if isinstance(raw, str) else raw
    except (TypeError, ValueError):
        return {}
    return dict(parsed) if isinstance(parsed, dict) else {}


def parse_run_row(run_row: Dict[str, Any], default_lots: int = 1) -> BacktestRun:
    """
    SimulationRun row (camelCase JSON from GET /api/Simulator/runs/{id}) -> BacktestRun.
    Raises ValueError with a plain message for rows that cannot drive a replay.
    """
    run_id = int(run_row.get("id") or run_row.get("runId") or 0)
    if run_id <= 0:
        raise ValueError("run row has no id")

    mode = str(run_row.get("mode") or "")
    if mode and mode != "OfflineReplay":
        raise ValueError(f"run {run_id} has mode {mode!r}; backtests need OfflineReplay")

    strategy_name = str(run_row.get("strategyName") or "").strip()
    if not strategy_name:
        raise ValueError(f"run {run_id} has no strategyName")

    spot_symbol = str(run_row.get("symbol") or "").strip()
    if not spot_symbol:
        raise ValueError(f"run {run_id} has no symbol")

    params = parse_parameters(run_row.get("parametersJson"))

    underlying = str(params.get("underlying") or "").strip().upper() or underlying_from_spot(spot_symbol)

    raw_resolution = run_row.get("resolution") or params.get("resolution") or "5"
    resolution_code = to_candle_resolution(str(raw_resolution))
    resolution = to_strategy_resolution(resolution_code)

    from_raw, to_raw = run_row.get("fromUtc"), run_row.get("toUtc")
    if not from_raw or not to_raw:
        raise ValueError(f"run {run_id} has no fromUtc/toUtc range")
    from_utc, to_utc = parse_utc(from_raw), parse_utc(to_raw)
    if to_utc < from_utc:
        raise ValueError(f"run {run_id} range ends before it starts")

    lots = BaseStrategy.lots_from(params, default_lots)

    stop_loss = parse_optional_number(params.get("stop_loss"))
    if stop_loss is not None and stop_loss <= 0:
        stop_loss = None
    target = parse_optional_number(params.get("target"))
    if target is not None and target <= 0:
        target = None

    eod_raw = params.get("eod_square_off_ist", DEFAULT_EOD_SQUARE_OFF_IST)
    eod_square_off_ist = "" if eod_raw is None else str(eod_raw).strip()
    parse_hhmm(eod_square_off_ist)  # validate early

    charges = parse_optional_number(params.get("charges_per_lot")) or 0.0
    if charges < 0:
        charges = 0.0

    capital = parse_optional_number(run_row.get("initialCapital"))
    if capital is None:
        capital = parse_optional_number(params.get("initial_capital"))
    if capital is None or capital <= 0:
        capital = DEFAULT_INITIAL_CAPITAL

    user_id = run_row.get("userId")

    return BacktestRun(
        run_id=run_id,
        strategy_name=strategy_name,
        spot_symbol=spot_symbol,
        underlying=underlying,
        resolution_code=resolution_code,
        resolution=resolution,
        from_utc=from_utc,
        to_utc=to_utc,
        from_date=ist_date(from_utc),
        to_date=ist_date(to_utc),
        lots=lots,
        stop_loss=stop_loss,
        target=target,
        eod_square_off_ist=eod_square_off_ist,
        charges_per_lot=float(charges),
        initial_capital=float(capital),
        user_id=int(user_id) if isinstance(user_id, (int, float)) else None,
        params=params,
    )
