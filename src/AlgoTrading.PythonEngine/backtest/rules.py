"""
backtest/rules.py

The run's own trading rules, applied to whatever strategy it replays.

A strategy answers "is my setup here?". These answer "how am I allowed to trade
it?" — how often, how long a position may live, where the stop moves as a trade
goes well, which contract a bare BUY/SELL takes, and what a fill costs. They are
written once and apply to every strategy, because that is what makes two runs
comparable: the same question asked of a different strategy.

Configured per run, in `parametersJson`, beside the market-context `filters`:

    "limits": {
      "max_trades_per_day": 1,
      "max_trades_per_window": 1,
      "max_open_groups": 1,
      "cooldown_after_loss_minutes": 30,
      "block_direction_after_loss": true,
      "stop_after_losses": 2,
      "stop_after_day_loss": 3000
    },
    "exits": {
      "time_exit_minutes": 30,
      "breakeven_after_points": 10,
      "breakeven_after_percent": 15,
      "step_trail_step_percent": 5,
      "exit_on_ema_cross": 20,
      "exit_on_vwap_cross": true,
      "exit_on_supertrend": [10, 3]
    },
    "contract": { "strike_offset": 0, "flip": false },
    "costs": { "slippage_percent": 0.5, "brokerage_per_order": 20 }

Every field is optional and "not set" means "not enforced". Nothing here reads
the future: a limit sees the trades that have already closed, an exit sees the
position's own marks and the bars that have already closed.

Pure Python, no I/O: the engine feeds it times, marks and bars.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from typing import Any, Dict, List, Optional, Sequence, Tuple

from core.charges import CostModel

#: Windows a run may trade in, as ("09:20", "11:00") pairs in IST.
Window = Tuple[str, str]


def _as_float(value: Any) -> Optional[float]:
    try:
        return None if value is None or value == "" else float(value)
    except (TypeError, ValueError):
        return None


def _as_int(value: Any) -> Optional[int]:
    number = _as_float(value)
    return None if number is None else int(number)


def _as_bool(value: Any) -> bool:
    if isinstance(value, str):
        return value.strip().lower() in ("true", "yes", "1")
    return bool(value)


def minutes_of(text: str) -> Optional[int]:
    """"09:20" -> 560 minutes after midnight; None when it is not a time."""
    try:
        hours, minutes = str(text).strip().split(":")
        value = int(hours) * 60 + int(minutes)
    except (AttributeError, TypeError, ValueError):
        return None
    return value if 0 <= value < 24 * 60 else None


def parse_windows(raw: Any) -> List[Window]:
    """
    [["09:20","11:00"], ["13:00","15:15"]] -> the same as pairs. A single
    ["09:20","15:05"] pair is accepted too, which is the older one-window form.
    """
    if not isinstance(raw, (list, tuple)) or not raw:
        return []
    if len(raw) == 2 and all(isinstance(x, str) for x in raw):
        raw = [raw]
    windows: List[Window] = []
    for item in raw:
        if isinstance(item, (list, tuple)) and len(item) == 2 and all(minutes_of(x) is not None for x in item):
            windows.append((str(item[0]), str(item[1])))
    return windows


def window_index(windows: Sequence[Window], moment_ist: datetime) -> Optional[int]:
    """Which window this IST moment falls in (0-based), or None when it is in none."""
    if not windows:
        return None
    minute = moment_ist.hour * 60 + moment_ist.minute
    for index, (start, end) in enumerate(windows):
        first, last = minutes_of(start), minutes_of(end)
        if first is not None and last is not None and first <= minute <= last:
            return index
    return None


# ------------------------------------------------------------------ limits --

@dataclass
class Limits:
    """How often a run may open, and when it must stop for the day."""
    max_trades_per_day: Optional[int] = None
    max_trades_per_window: Optional[int] = None
    max_open_groups: Optional[int] = None
    cooldown_after_loss_minutes: Optional[int] = None
    block_direction_after_loss: bool = False
    stop_after_losses: Optional[int] = None
    stop_after_day_loss: Optional[float] = None

    @property
    def is_set(self) -> bool:
        return any([
            self.max_trades_per_day, self.max_trades_per_window, self.max_open_groups,
            self.cooldown_after_loss_minutes, self.block_direction_after_loss,
            self.stop_after_losses, self.stop_after_day_loss,
        ])

    def describe(self) -> str:
        parts = []
        if self.max_trades_per_day:
            parts.append(f"at most {self.max_trades_per_day} trade(s) a day")
        if self.max_trades_per_window:
            parts.append(f"at most {self.max_trades_per_window} per window")
        if self.max_open_groups:
            parts.append(f"at most {self.max_open_groups} open position(s)")
        if self.cooldown_after_loss_minutes:
            parts.append(f"{self.cooldown_after_loss_minutes}m cooldown after a loss")
        if self.block_direction_after_loss:
            parts.append("no repeat of a direction that lost today")
        if self.stop_after_losses:
            parts.append(f"day ends after {self.stop_after_losses} losing trade(s)")
        if self.stop_after_day_loss:
            parts.append(f"day ends at −₹{self.stop_after_day_loss:,.0f} realized")
        return ", ".join(parts)


class TradeLimiter:
    """
    Counts what the run has done today and answers whether another entry is
    allowed. The engine calls `allow` before an opening signal is filled, then
    `opened` / `closed` so the next answer knows what happened.
    """

    def __init__(self, limits: Limits, windows: Sequence[Window] = ()) -> None:
        self.limits = limits
        self.windows = list(windows)
        self.day: Optional[date] = None
        self.opens_today = 0
        self.opens_by_window: Dict[int, int] = {}
        self.losses_today = 0
        self.realized_today = 0.0
        self.last_loss_at: Optional[datetime] = None
        self.lost_directions: set = set()
        self.day_closed_reason: Optional[str] = None
        self.blocked: Dict[str, int] = {}

    def start_day(self, day: date) -> None:
        self.day = day
        self.opens_today = 0
        self.opens_by_window = {}
        self.losses_today = 0
        self.realized_today = 0.0
        self.last_loss_at = None
        self.lost_directions = set()
        self.day_closed_reason = None

    def allow(self, moment_ist: datetime, direction: Optional[str], open_groups: int) -> Optional[Tuple[str, str]]:
        """
        None when the entry may proceed, else (rule, reason). `direction` is
        "bullish"/"bearish" when the signal expresses one, else None, and the
        direction rules then stand aside rather than guess.
        """
        limits = self.limits
        if self.day_closed_reason:
            return "day_closed", self.day_closed_reason
        if limits.max_open_groups is not None and open_groups >= limits.max_open_groups:
            return "max_open_groups", (
                f"{open_groups} position(s) already open (limit {limits.max_open_groups})")
        if limits.max_trades_per_day is not None and self.opens_today >= limits.max_trades_per_day:
            return "max_trades_per_day", (
                f"{self.opens_today} trade(s) taken today (limit {limits.max_trades_per_day})")
        if limits.max_trades_per_window is not None and self.windows:
            index = window_index(self.windows, moment_ist)
            if index is not None and self.opens_by_window.get(index, 0) >= limits.max_trades_per_window:
                start, end = self.windows[index]
                return "max_trades_per_window", (
                    f"{self.opens_by_window[index]} trade(s) already taken in {start}–{end} "
                    f"(limit {limits.max_trades_per_window})")
        if limits.cooldown_after_loss_minutes and self.last_loss_at is not None:
            elapsed = (moment_ist - self.last_loss_at).total_seconds() / 60.0
            if elapsed < limits.cooldown_after_loss_minutes:
                return "cooldown_after_loss", (
                    f"{elapsed:.0f}m since the last losing trade "
                    f"(cooldown {limits.cooldown_after_loss_minutes}m)")
        if limits.block_direction_after_loss and direction and direction in self.lost_directions:
            return "block_direction_after_loss", f"a {direction} trade already lost today"
        return None

    def opened(self, moment_ist: datetime, direction: Optional[str]) -> None:
        self.opens_today += 1
        index = window_index(self.windows, moment_ist)
        if index is not None:
            self.opens_by_window[index] = self.opens_by_window.get(index, 0) + 1

    def closed(self, moment_ist: datetime, direction: Optional[str], realized: float) -> Optional[str]:
        """
        Books a closed trade. Returns the reason the day is over when this close
        ended it (the engine squares off and stops entering), else None.
        """
        self.realized_today += realized
        if realized < 0:
            self.losses_today += 1
            self.last_loss_at = moment_ist
            if direction:
                self.lost_directions.add(direction)
        limits = self.limits
        if limits.stop_after_losses is not None and self.losses_today >= limits.stop_after_losses:
            self.day_closed_reason = (
                f"{self.losses_today} losing trade(s) today (limit {limits.stop_after_losses})")
        elif limits.stop_after_day_loss is not None and self.realized_today <= -abs(limits.stop_after_day_loss):
            self.day_closed_reason = (
                f"day realized ₹{self.realized_today:,.0f} at or below −₹{abs(limits.stop_after_day_loss):,.0f}")
        return self.day_closed_reason

    def count_block(self, rule: str) -> None:
        self.blocked[rule] = self.blocked.get(rule, 0) + 1


# ------------------------------------------------------------------- exits --

@dataclass
class Exits:
    """Exits the run adds on top of the strategy's own and the leg risk rules."""
    time_exit_minutes: Optional[int] = None
    breakeven_after_points: Optional[float] = None
    breakeven_after_percent: Optional[float] = None
    step_trail_step_percent: Optional[float] = None
    exit_on_ema_cross: Optional[int] = None
    exit_on_vwap_cross: bool = False
    exit_on_supertrend: Optional[Tuple[int, float]] = None

    @property
    def is_set(self) -> bool:
        return any([
            self.time_exit_minutes, self.breakeven_after_points, self.breakeven_after_percent,
            self.step_trail_step_percent, self.exit_on_ema_cross, self.exit_on_vwap_cross,
            self.exit_on_supertrend,
        ])

    @property
    def needs_bars(self) -> bool:
        return bool(self.exit_on_ema_cross or self.exit_on_vwap_cross or self.exit_on_supertrend)

    def describe(self) -> str:
        parts = []
        if self.time_exit_minutes:
            parts.append(f"exit {self.time_exit_minutes}m after entry")
        if self.breakeven_after_points:
            parts.append(f"stop to entry once +{self.breakeven_after_points:g} pts")
        if self.breakeven_after_percent:
            parts.append(f"stop to entry once +{self.breakeven_after_percent:g}%")
        if self.step_trail_step_percent:
            parts.append(f"then stop up {self.step_trail_step_percent:g}% per {self.step_trail_step_percent:g}%")
        if self.exit_on_ema_cross:
            parts.append(f"exit when the index closes against EMA {self.exit_on_ema_cross}")
        if self.exit_on_vwap_cross:
            parts.append("exit when the index closes against VWAP")
        if self.exit_on_supertrend:
            period, multiple = self.exit_on_supertrend
            parts.append(f"exit when Supertrend({period:g},{multiple:g}) turns against")
        return ", ".join(parts)


@dataclass
class _PositionState:
    opened_ist: datetime
    peak_points: float = 0.0
    peak_percent: float = 0.0
    armed: bool = False          # the breakeven stop is in force
    stop_percent: float = 0.0    # where the stepped stop sits, in % of entry


class ExitManager:
    """
    Per-position exits: the clock, the moving stop, and the index turning against
    the position. It keeps each position's peak, so a stop only ever rises.
    """

    def __init__(self, exits: Exits) -> None:
        self.exits = exits
        self._positions: Dict[Tuple[str, str], _PositionState] = {}
        self.counts: Dict[str, int] = {}

    def opened(self, key: Tuple[str, str], moment_ist: datetime) -> None:
        self._positions[key] = _PositionState(opened_ist=moment_ist)

    def prune(self, open_keys: Sequence[Tuple[str, str]]) -> None:
        live = set(open_keys)
        for key in [k for k in self._positions if k not in live]:
            del self._positions[key]

    def check(self, key: Tuple[str, str], moment_ist: datetime, points: Optional[float],
              percent: Optional[float], direction: Optional[str],
              index_view: Optional[str] = None) -> Optional[Tuple[str, str]]:
        """
        (rule, reason) when this position should be closed now, else None.

        `points`/`percent` are the position's own P&L from entry, profit
        positive. `direction` is the view the position expresses ("bullish" for
        a long call or a short put); `index_view` is what the index says now by
        whichever exit indicator is configured. A position whose direction is
        unknown is never closed by an indicator rule.
        """
        state = self._positions.get(key)
        if state is None:
            state = self._positions[key] = _PositionState(opened_ist=moment_ist)
        exits = self.exits

        if exits.time_exit_minutes:
            age = (moment_ist - state.opened_ist).total_seconds() / 60.0
            if age >= exits.time_exit_minutes:
                return "time_exit", f"held {age:.0f}m (limit {exits.time_exit_minutes}m)"

        if points is not None:
            state.peak_points = max(state.peak_points, points)
        if percent is not None:
            state.peak_percent = max(state.peak_percent, percent)

        # Breakeven, then a stop that steps up with every further step of profit.
        if not state.armed:
            if exits.breakeven_after_points and state.peak_points >= exits.breakeven_after_points:
                state.armed = True
            elif exits.breakeven_after_percent and state.peak_percent >= exits.breakeven_after_percent:
                state.armed = True
        if state.armed:
            step = exits.step_trail_step_percent or 0.0
            start = exits.breakeven_after_percent or 0.0
            if step > 0 and state.peak_percent > start:
                state.stop_percent = max(state.stop_percent, ((state.peak_percent - start) // step) * step)
            if percent is not None and percent <= state.stop_percent:
                where = "entry" if state.stop_percent <= 0 else f"+{state.stop_percent:g}%"
                return "moving_stop", (
                    f"stop at {where} hit: {percent:+.1f}% after a peak of {state.peak_percent:+.1f}%")

        if index_view and direction and index_view != direction:
            return "index_turned", f"the index is {index_view} against this {direction} position"
        return None

    def count(self, rule: str) -> None:
        self.counts[rule] = self.counts.get(rule, 0) + 1


# -------------------------------------------------------------- contract --

@dataclass
class ContractRules:
    """What a bare BUY/SELL signal is traded as."""
    #: Strikes away from ATM, positive = in the money, negative = out of the money.
    strike_offset: int = 0
    #: Buy the other side instead (a BUY signal takes the PE), which is how a
    #: study tests whether a strategy is wrong rather than merely unprofitable.
    flip: bool = False

    @property
    def is_set(self) -> bool:
        return bool(self.strike_offset or self.flip)

    def describe(self) -> str:
        parts = []
        if self.strike_offset:
            way = "ITM" if self.strike_offset > 0 else "OTM"
            parts.append(f"{abs(self.strike_offset)} strike(s) {way}")
        if self.flip:
            parts.append("flipped side")
        return ", ".join(parts)

    def side_for(self, direction: str) -> str:
        """The option side a BUY/SELL signal takes: "CE" or "PE"."""
        wants_call = str(direction).upper() == "BUY"
        if self.flip:
            wants_call = not wants_call
        return "CE" if wants_call else "PE"

    def strike_for(self, atm: float, side: str, step: float) -> float:
        """The strike this signal trades: ATM moved `strike_offset` steps in or out of the money."""
        if not self.strike_offset:
            return float(atm)
        away = self.strike_offset * float(step)
        return float(atm) - away if str(side).upper() == "CE" else float(atm) + away


# ----------------------------------------------------------------- bundle --

@dataclass
class RunRules:
    """Everything this module configures for one run."""
    limits: Limits = field(default_factory=Limits)
    exits: Exits = field(default_factory=Exits)
    contract: ContractRules = field(default_factory=ContractRules)
    costs: Optional[CostModel] = None
    windows: List[Window] = field(default_factory=list)

    @property
    def is_set(self) -> bool:
        return bool(self.limits.is_set or self.exits.is_set or self.contract.is_set or self.costs)

    def describe(self) -> str:
        parts = [text for text in (self.limits.describe(), self.exits.describe(), self.contract.describe()) if text]
        if self.costs:
            parts.append(f"costs {self.costs.slippage_pct:g}% slippage + statutory charges")
        return "; ".join(parts) or "none"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "limits": self.limits.describe(),
            "exits": self.exits.describe(),
            "contract": self.contract.describe(),
            "costs": self.costs.to_dict() if self.costs else None,
        }


def parse_rules(params: Optional[Dict[str, Any]]) -> RunRules:
    """
    The run's rule blocks out of `parametersJson`. Missing blocks mean "not
    enforced", so a run that configures none behaves exactly as before.
    """
    params = params or {}

    def block(name: str) -> Dict[str, Any]:
        raw = params.get(name)
        return raw if isinstance(raw, dict) else {}

    limits_raw, exits_raw, contract_raw = block("limits"), block("exits"), block("contract")
    filters_raw = block("filters")

    limits = Limits(
        max_trades_per_day=_as_int(limits_raw.get("max_trades_per_day")),
        max_trades_per_window=_as_int(limits_raw.get("max_trades_per_window")),
        max_open_groups=_as_int(limits_raw.get("max_open_groups")),
        cooldown_after_loss_minutes=_as_int(limits_raw.get("cooldown_after_loss_minutes")),
        block_direction_after_loss=_as_bool(limits_raw.get("block_direction_after_loss")),
        stop_after_losses=_as_int(limits_raw.get("stop_after_losses")),
        stop_after_day_loss=_as_float(limits_raw.get("stop_after_day_loss")),
    )

    supertrend = exits_raw.get("exit_on_supertrend")
    parsed_supertrend: Optional[Tuple[int, float]] = None
    if isinstance(supertrend, (list, tuple)) and len(supertrend) == 2:
        period, multiple = _as_int(supertrend[0]), _as_float(supertrend[1])
        if period and multiple:
            parsed_supertrend = (period, multiple)
    elif _as_bool(supertrend):
        parsed_supertrend = (10, 3.0)

    exits = Exits(
        time_exit_minutes=_as_int(exits_raw.get("time_exit_minutes")),
        breakeven_after_points=_as_float(exits_raw.get("breakeven_after_points")),
        breakeven_after_percent=_as_float(exits_raw.get("breakeven_after_percent")),
        step_trail_step_percent=_as_float(exits_raw.get("step_trail_step_percent")),
        exit_on_ema_cross=_as_int(exits_raw.get("exit_on_ema_cross")),
        exit_on_vwap_cross=_as_bool(exits_raw.get("exit_on_vwap_cross")),
        exit_on_supertrend=parsed_supertrend,
    )

    contract = ContractRules(
        strike_offset=_as_int(contract_raw.get("strike_offset")) or 0,
        flip=_as_bool(contract_raw.get("flip")),
    )

    costs_raw = params.get("costs")
    costs: Optional[CostModel] = None
    if isinstance(costs_raw, dict) and costs_raw:
        # The console writes friendly names; the model's own field names work too.
        mapped = dict(costs_raw)
        for friendly, field_name in (("slippage_percent", "slippage_pct"), ("gst_percent", "gst_pct"),
                                     ("stt_sell_percent", "stt_sell_pct")):
            if friendly in mapped:
                mapped[field_name] = mapped.pop(friendly)
        costs = CostModel.from_dict(mapped)
    elif _as_bool(costs_raw):
        costs = CostModel()

    windows = parse_windows(filters_raw.get("windows_ist") or filters_raw.get("trade_window_ist"))
    return RunRules(limits=limits, exits=exits, contract=contract, costs=costs, windows=windows)
