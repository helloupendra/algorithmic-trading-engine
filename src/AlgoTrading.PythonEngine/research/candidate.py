"""
research/candidate.py

The research candidate interface: an entry function and an exit policy.

Deliberately NOT `strategies.base_strategy.BaseStrategy`. A live strategy has
to cope with ticks, restarts, contract selection and the API; a research
candidate only has to answer "given everything up to this bar's close, do I buy
a CE or a PE now?". Keeping it that small keeps the simulator fast enough to
walk forward over a parameter grid, and keeps research ideas out of the live
registry until one earns a real implementation and spec.

A candidate:
  - `entry(ctx) -> Optional[Signal]` is called at the CLOSE of each eligible
    bar with a `BarContext` that exposes only that bar and earlier ones;
  - `exits(params) -> ExitPolicy` says how a position is closed;
  - declares a strike offset (0 = ATM) and at most four parameters, each with a
    documented default, plus an optional small grid for walk-forward selection.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from datetime import datetime, time
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence

from research.regime import RegimeReading, TREND_DOWN, TREND_UP, VOLATILE_CHOP
from research.ta import ema_series

CE = "CE"
PE = "PE"
MAX_PARAMETERS = 4


@dataclass(frozen=True)
class Signal:
    """Buy this option type at the next bar's open."""
    side: str       # "CE" | "PE"
    reason: str

    def __post_init__(self) -> None:
        if self.side not in (CE, PE):
            raise ValueError(f"Signal side must be CE or PE, not {self.side!r}")


@dataclass(frozen=True)
class ExitPolicy:
    """
    How a bought option is closed. Every rule is optional except the forced
    exit; the first rule to fire closes the whole position.

    Resting orders, filled INSIDE a bar at their level (or at the bar's open
    when the open is already through the level):
      stop_premium_pct     exit when the premium falls this % below the entry fill
      target_premium_pct   exit when the premium rises this % above the entry fill
      trail_giveback_pct / trail_arm_pct
                           once the best premium gain reaches trail_arm_pct % of
                           the entry, exit if the premium gives back
                           trail_giveback_pct % of the entry from that best gain
                           (the replay's leg-trailing semantics, backtest/trailing.py)

    Decisions at a bar's CLOSE, filled at the NEXT bar's open:
      stop_underlying_atr  exit when the index closes this many ATRs (ATR at the
                           signal bar) against the trade from the signal close
      time_stop_bars       exit after holding this many bars
      exit_on_regime_flip  exit when the regime turns against the trade: a CE on
                           TREND_DOWN or VOLATILE_CHOP, a PE on TREND_UP or
                           VOLATILE_CHOP (RANGE is a pause, not a flip)
      force_exit_ist       exit at this IST time (15:15 by default): decided at
                           the close of the bar that ends at it
    """
    stop_premium_pct: Optional[float] = None
    target_premium_pct: Optional[float] = None
    trail_giveback_pct: Optional[float] = None
    trail_arm_pct: Optional[float] = None
    stop_underlying_atr: Optional[float] = None
    time_stop_bars: Optional[int] = None
    exit_on_regime_flip: bool = False
    force_exit_ist: time = time(15, 15)

    def against(self, side: str, label: Optional[str]) -> bool:
        if not self.exit_on_regime_flip or label is None:
            return False
        if side == CE:
            return label in (TREND_DOWN, VOLATILE_CHOP)
        return label in (TREND_UP, VOLATILE_CHOP)

    def to_dict(self) -> Dict[str, Any]:
        out = asdict(self)
        out["force_exit_ist"] = self.force_exit_ist.strftime("%H:%M")
        return out


class Features:
    """
    Per-bar facts computed once per run and shared by every simulation over
    the same bars: IST clock, session keys and boundaries, regime readings, and
    cached causal EMA series.
    """

    def __init__(self, bars: Sequence[Any], readings: Sequence[RegimeReading], bar_minutes: int = 5) -> None:
        if len(bars) != len(readings):
            raise ValueError("bars and readings must be the same length")
        from strategies.indicators import ist_of

        self.bars = list(bars)
        self.readings = list(readings)
        self.bar_minutes = bar_minutes
        self.closes = [float(b.close) for b in self.bars]
        self.moments: List[datetime] = [ist_of(b) for b in self.bars]
        self.sessions = [m.strftime("%Y-%m-%d") for m in self.moments]
        self.session_start: List[int] = []
        start = 0
        for i, key in enumerate(self.sessions):
            if i == 0 or key != self.sessions[i - 1]:
                start = i
            self.session_start.append(start)
        self._ema: Dict[int, List[Optional[float]]] = {}

    def __len__(self) -> int:
        return len(self.bars)

    def ema(self, period: int) -> List[Optional[float]]:
        if period not in self._ema:
            self._ema[period] = ema_series(self.closes, period)
        return self._ema[period]

    def is_last_of_session(self, i: int) -> bool:
        return i == len(self.bars) - 1 or self.sessions[i + 1] != self.sessions[i]


@dataclass
class BarContext:
    """
    What a candidate sees at the close of bar `i`: that bar, earlier bars, the
    regime so far, and its own per-session scratch dict. Nothing after `i` is
    reachable through it.
    """
    i: int
    features: Features
    params: Mapping[str, Any]
    state: Dict[str, Any] = field(default_factory=dict)
    trades_today: int = 0

    @property
    def bar(self) -> Any:
        return self.features.bars[self.i]

    @property
    def regime(self) -> RegimeReading:
        return self.features.readings[self.i]

    @property
    def ist(self) -> datetime:
        return self.features.moments[self.i]

    @property
    def session(self) -> str:
        return self.features.sessions[self.i]

    @property
    def session_bar(self) -> int:
        return self.i - self.features.session_start[self.i]

    def regime_back(self, back: int) -> Optional[RegimeReading]:
        j = self._back(back)
        return None if j is None else self.features.readings[j]

    def bar_back(self, back: int) -> Optional[Any]:
        j = self._back(back)
        return None if j is None else self.features.bars[j]

    def ema(self, period: int, back: int = 0) -> Optional[float]:
        j = self._back(back)
        return None if j is None else self.features.ema(period)[j]

    def session_bars(self) -> List[Any]:
        return self.features.bars[self.features.session_start[self.i]: self.i + 1]

    def recent(self, count: int, same_session: bool = True) -> List[Any]:
        lo = max(0, self.i - count + 1)
        if same_session:
            lo = max(lo, self.features.session_start[self.i])
        return self.features.bars[lo: self.i + 1]

    def _back(self, back: int) -> Optional[int]:
        if back < 0:
            raise ValueError("a candidate cannot look forward")
        j = self.i - back
        return j if j >= 0 else None


EntryFn = Callable[[BarContext], Optional[Signal]]
ExitsFn = Callable[[Mapping[str, Any]], ExitPolicy]


@dataclass(frozen=True)
class Candidate:
    name: str
    title: str
    rules: str                               # plain-language description for the report
    defaults: Mapping[str, Any]              # at most four parameters
    parameter_notes: Mapping[str, str]       # why each default is what it is
    entry: EntryFn
    exits: ExitsFn
    strike_offset: int = 0                   # 0 = ATM
    grid: Mapping[str, Sequence[Any]] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if len(self.defaults) > MAX_PARAMETERS:
            raise ValueError(f"{self.name}: at most {MAX_PARAMETERS} parameters, has {len(self.defaults)}")
        unknown = set(self.grid) - set(self.defaults)
        if unknown:
            raise ValueError(f"{self.name}: grid names unknown parameters {sorted(unknown)}")
        missing = set(self.defaults) - set(self.parameter_notes)
        if missing:
            raise ValueError(f"{self.name}: parameters without a documented default {sorted(missing)}")

    def params(self, overrides: Optional[Mapping[str, Any]] = None) -> Dict[str, Any]:
        merged = dict(self.defaults)
        for key, value in (overrides or {}).items():
            if key not in merged:
                raise ValueError(f"{self.name}: unknown parameter {key!r}")
            merged[key] = value
        return merged
