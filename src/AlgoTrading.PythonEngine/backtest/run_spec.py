"""
backtest/run_spec.py

The backtest configuration as parsed from a SimulationRun row (Mode
"OfflineReplay"): the API writes strategy defaults + user overrides +
{lots, stop_loss, target, risk, underlying, resolution, eod_square_off_ist,
charges_per_lot} into `parametersJson`, and the spot symbol / canonical
resolution / UTC range into the row itself.

Risk rules (`RiskRules`) are shared by live runs and backtests: the API
persists them as `parametersJson.risk` (camelCase object, every field
optional) and keeps the legacy `stop_loss` / `target` keys equal to the
overall values. Readers prefer `risk` and fall back to the legacy keys.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from datetime import date, datetime, time
from typing import Any, Dict, List, Optional

from core.resolutions import to_candle_resolution, to_strategy_resolution
from strategies.base_strategy import BaseStrategy
from strategies.signal_utils import parse_optional_number

from backtest.timeutil import ist_date, parse_hhmm, parse_utc

DEFAULT_EOD_SQUARE_OFF_IST = "15:15"
DEFAULT_INITIAL_CAPITAL = 1_000_000.0


# --- risk rules -------------------------------------------------------------

def _positive(value: Any) -> Optional[float]:
    """Float when `value` is a number > 0, else None (unset)."""
    number = parse_optional_number(value)
    if number is None or number <= 0:
        return None
    return float(number)


def _pick(source: Dict[str, Any], *keys: str) -> Any:
    """First present key (camelCase or snake_case spellings) of a JSON object."""
    for key in keys:
        if key in source:
            return source[key]
    return None


def _money_text(value: Optional[float]) -> str:
    return "—" if value is None else f"₹{value:,.0f}"


def _pts_pct(points: Optional[float], percent: Optional[float]) -> str:
    """"20 pts / 5%" for the describe() lines; "—" when neither is set."""
    parts: List[str] = []
    if points is not None:
        parts.append(f"{points:g} pts")
    if percent is not None:
        parts.append(f"{percent:g}%")
    return " / ".join(parts) if parts else "—"


@dataclass(frozen=True)
class OverallRisk:
    """
    Rupee stop-loss / target / trailing stop on the run's TOTAL P&L; a trip
    ends the run.

    The trail arms when total P&L first reaches `trail_trigger` (or, without a
    trigger, as soon as it goes positive); from then on the run's best P&L is
    tracked and the run is flattened when P&L falls to `peak - trail_stop_loss`
    or below.
    """
    stop_loss: Optional[float] = None
    target: Optional[float] = None
    trail_stop_loss: Optional[float] = None
    trail_trigger: Optional[float] = None

    @property
    def is_set(self) -> bool:
        return any(v is not None for v in (self.stop_loss, self.target,
                                           self.trail_stop_loss, self.trail_trigger))

    def to_dict(self) -> Dict[str, Any]:
        return {"stopLoss": self.stop_loss, "target": self.target,
                "trailStopLoss": self.trail_stop_loss, "trailTrigger": self.trail_trigger}

    def describe(self) -> str:
        text = f"overall SL {_money_text(self.stop_loss)} · target {_money_text(self.target)}"
        if self.trail_stop_loss is not None:
            text += f" · trail {_money_text(self.trail_stop_loss)}"
            if self.trail_trigger is not None:
                text += f" from {_money_text(self.trail_trigger)}"
        return text


@dataclass(frozen=True)
class GroupRisk:
    """
    Rupee stop-loss / target / trailing stop per group (one OPEN_GROUP); a trip
    closes that group only. The trail works exactly as `OverallRisk`'s, on the
    group's own P&L.
    """
    stop_loss: Optional[float] = None
    target: Optional[float] = None
    trail_stop_loss: Optional[float] = None
    trail_trigger: Optional[float] = None

    @property
    def is_set(self) -> bool:
        return any(v is not None for v in (self.stop_loss, self.target,
                                           self.trail_stop_loss, self.trail_trigger))

    def to_dict(self) -> Dict[str, Any]:
        return {"stopLoss": self.stop_loss, "target": self.target,
                "trailStopLoss": self.trail_stop_loss, "trailTrigger": self.trail_trigger}

    def describe(self) -> str:
        text = f"group SL {_money_text(self.stop_loss)} · target {_money_text(self.target)}"
        if self.trail_stop_loss is not None:
            text += f" · trail {_money_text(self.trail_stop_loss)}"
            if self.trail_trigger is not None:
                text += f" from {_money_text(self.trail_trigger)}"
        return text


@dataclass(frozen=True)
class LegRisk:
    """
    Premium points / percent of entry premium per leg; a trip closes that leg
    only.

    The trailing stop is tracked per leg and, when both are set, separately in
    points and in percent: each metric arms at its own trigger (or as soon as
    the leg is in profit), keeps its own peak and trips on its own give-back.
    """
    stop_loss_points: Optional[float] = None
    target_points: Optional[float] = None
    stop_loss_percent: Optional[float] = None
    target_percent: Optional[float] = None
    trail_stop_loss_points: Optional[float] = None
    trail_stop_loss_percent: Optional[float] = None
    trail_trigger_points: Optional[float] = None
    trail_trigger_percent: Optional[float] = None

    @property
    def is_set(self) -> bool:
        return any(v is not None for v in (self.stop_loss_points, self.target_points,
                                           self.stop_loss_percent, self.target_percent,
                                           self.trail_stop_loss_points, self.trail_stop_loss_percent,
                                           self.trail_trigger_points, self.trail_trigger_percent))

    def to_dict(self) -> Dict[str, Any]:
        return {
            "stopLossPoints": self.stop_loss_points,
            "targetPoints": self.target_points,
            "stopLossPercent": self.stop_loss_percent,
            "targetPercent": self.target_percent,
            "trailStopLossPoints": self.trail_stop_loss_points,
            "trailStopLossPercent": self.trail_stop_loss_percent,
            "trailTriggerPoints": self.trail_trigger_points,
            "trailTriggerPercent": self.trail_trigger_percent,
        }

    def describe(self) -> str:
        text = (
            f"leg SL {_pts_pct(self.stop_loss_points, self.stop_loss_percent)} · "
            f"target {_pts_pct(self.target_points, self.target_percent)}"
        )
        if self.trail_stop_loss_points is not None or self.trail_stop_loss_percent is not None:
            text += f" · trail {_pts_pct(self.trail_stop_loss_points, self.trail_stop_loss_percent)}"
            if self.trail_trigger_points is not None or self.trail_trigger_percent is not None:
                text += f" from {_pts_pct(self.trail_trigger_points, self.trail_trigger_percent)}"
        return text


@dataclass(frozen=True)
class RiskRules:
    """
    The three risk levels, evaluated in this order on every sweep:
    leg (closes that leg) → group (closes that group) → overall (ends the run).
    Within one level the order is fixed stop-loss → trailing stop → target.
    """
    overall: OverallRisk = OverallRisk()
    group: GroupRisk = GroupRisk()
    leg: LegRisk = LegRisk()

    @property
    def is_set(self) -> bool:
        return self.overall.is_set or self.group.is_set or self.leg.is_set

    @property
    def stop_loss(self) -> Optional[float]:
        """Overall shorthand (legacy `stop_loss`)."""
        return self.overall.stop_loss

    @property
    def target(self) -> Optional[float]:
        """Overall shorthand (legacy `target`)."""
        return self.overall.target

    def to_dict(self) -> Dict[str, Any]:
        """camelCase JSON object, the shape the API persists in parametersJson.risk."""
        return {"overall": self.overall.to_dict(), "group": self.group.to_dict(), "leg": self.leg.to_dict()}

    def describe(self) -> str:
        if not self.is_set:
            return "none"
        parts: List[str] = []
        if self.overall.is_set:
            parts.append(self.overall.describe())
        if self.group.is_set:
            parts.append(self.group.describe())
        if self.leg.is_set:
            parts.append(self.leg.describe())
        return "; ".join(parts)

    @classmethod
    def from_legacy(cls, stop_loss: Any = None, target: Any = None) -> "RiskRules":
        return cls(overall=OverallRisk(stop_loss=_positive(stop_loss), target=_positive(target)))

    @classmethod
    def from_object(cls, raw: Any) -> Optional["RiskRules"]:
        """
        Parse the camelCase `risk` object (a dict or its JSON text). Returns
        None when `raw` is not an object at all, so callers can fall back to
        the legacy keys; an empty object is a valid "nothing set".
        """
        if isinstance(raw, str):
            text = raw.strip()
            if not text:
                return None
            try:
                raw = json.loads(text)
            except ValueError:
                return None
        if not isinstance(raw, dict):
            return None

        def section(name: str) -> Dict[str, Any]:
            value = raw.get(name)
            return value if isinstance(value, dict) else {}

        overall, group, leg = section("overall"), section("group"), section("leg")
        return cls(
            overall=OverallRisk(
                stop_loss=_positive(_pick(overall, "stopLoss", "stop_loss")),
                target=_positive(_pick(overall, "target")),
                trail_stop_loss=_positive(_pick(overall, "trailStopLoss", "trail_stop_loss")),
                trail_trigger=_positive(_pick(overall, "trailTrigger", "trail_trigger")),
            ),
            group=GroupRisk(
                stop_loss=_positive(_pick(group, "stopLoss", "stop_loss")),
                target=_positive(_pick(group, "target")),
                trail_stop_loss=_positive(_pick(group, "trailStopLoss", "trail_stop_loss")),
                trail_trigger=_positive(_pick(group, "trailTrigger", "trail_trigger")),
            ),
            leg=LegRisk(
                stop_loss_points=_positive(_pick(leg, "stopLossPoints", "stop_loss_points")),
                target_points=_positive(_pick(leg, "targetPoints", "target_points")),
                stop_loss_percent=_positive(_pick(leg, "stopLossPercent", "stop_loss_percent")),
                target_percent=_positive(_pick(leg, "targetPercent", "target_percent")),
                trail_stop_loss_points=_positive(_pick(leg, "trailStopLossPoints", "trail_stop_loss_points")),
                trail_stop_loss_percent=_positive(_pick(leg, "trailStopLossPercent", "trail_stop_loss_percent")),
                trail_trigger_points=_positive(_pick(leg, "trailTriggerPoints", "trail_trigger_points")),
                trail_trigger_percent=_positive(_pick(leg, "trailTriggerPercent", "trail_trigger_percent")),
            ),
        )


def parse_risk_rules(params: Optional[Dict[str, Any]]) -> RiskRules:
    """
    Risk rules of a run from its parameters: `risk` (camelCase object) when
    present, else the legacy `stop_loss` / `target` keys as the overall level.
    """
    source = params or {}
    rules = RiskRules.from_object(source.get("risk"))
    if rules is not None:
        return rules
    return RiskRules.from_legacy(source.get("stop_loss"), source.get("target"))

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
    stop_loss: Optional[float] = None          # overall shorthand (= risk.overall.stop_loss)
    target: Optional[float] = None             # overall shorthand (= risk.overall.target)
    eod_square_off_ist: str = DEFAULT_EOD_SQUARE_OFF_IST
    charges_per_lot: float = 0.0
    initial_capital: float = DEFAULT_INITIAL_CAPITAL
    user_id: Optional[int] = None
    params: Dict[str, Any] = field(default_factory=dict)
    risk: RiskRules = RiskRules()

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
            f"risk=[{self.risk.describe()}] "
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

    # `risk` is authoritative; the legacy keys mirror its overall level.
    risk = parse_risk_rules(params)
    stop_loss, target = risk.overall.stop_loss, risk.overall.target

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
        risk=risk,
    )
