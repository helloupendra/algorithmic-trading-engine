"""
backtest/trailing.py

Trailing stop-loss bookkeeping, shared by the three risk levels of the replay
(overall run P&L, per-group P&L, per-leg premium points and percent).

One subject (the run, a group id, a position id) has one `TrailState` per
metric it trails:

  - **arming**: the trail arms when the subject's favourable value first
    reaches the trigger; without a trigger it arms as soon as the value goes
    positive. Before arming nothing is tracked and nothing can trip, so a
    position that never gets ahead is never closed by a trailing rule;
  - **peak**: once armed the running maximum of the value is kept;
  - **trip**: the rule fires when the value falls to `peak - trail` or below —
    the boundary itself trips.

Peaks are in-memory only. The live guard resets them after an API restart or a
rule change (`reset`); the replay has neither, so a session's peaks live as long
as the subject does. Pure Python, no I/O.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, Hashable, Iterable, Optional


@dataclass(frozen=True)
class TrailTrip:
    """A trailing rule that fired: the numbers its reason string is built from."""
    value: float        # the subject's value now
    peak: float         # its best value since the trail armed
    trail: float        # the give-back the rule allows

    @property
    def drawdown(self) -> float:
        """How far the value has fallen from the peak (never negative)."""
        return max(0.0, self.peak - self.value)


@dataclass
class TrailState:
    """Arming flag and peak of one subject's one metric."""
    armed: bool = False
    peak: float = 0.0

    def update(self, value: float, trail: float, trigger: Optional[float]) -> Optional[TrailTrip]:
        """
        Feed the current value; returns the trip when the trail has been given
        back, else None. `trail` must be > 0 (an unset rule never gets here).
        """
        if not self.armed:
            armed_now = value >= trigger if trigger is not None else value > 0
            if not armed_now:
                return None
            self.armed = True
            self.peak = value
        elif value > self.peak:
            self.peak = value
        if value <= self.peak - trail:
            return TrailTrip(value=value, peak=self.peak, trail=trail)
        return None


class TrailTracker:
    """
    The `TrailState`s of one metric across subjects (`key` -> state).

    `evaluate` is safe to call on every sweep for every subject: a level whose
    trail is unset is a no-op, and re-arming only ever happens after `reset`.
    """

    def __init__(self) -> None:
        self._states: Dict[Hashable, TrailState] = {}

    def evaluate(self, key: Hashable, value: Optional[float], trail: Optional[float],
                 trigger: Optional[float] = None) -> Optional[TrailTrip]:
        """The trip for this subject at `value`, or None (including when the rule is unset)."""
        if trail is None or trail <= 0 or value is None:
            return None
        state = self._states.get(key)
        if state is None:
            state = TrailState()
            self._states[key] = state
        return state.update(float(value), float(trail), trigger)

    def state(self, key: Hashable) -> Optional[TrailState]:
        """The subject's state, or None when it has never been evaluated."""
        return self._states.get(key)

    def armed(self, key: Hashable) -> bool:
        state = self._states.get(key)
        return bool(state and state.armed)

    def peak(self, key: Hashable) -> Optional[float]:
        state = self._states.get(key)
        return state.peak if state and state.armed else None

    def reset(self, key: Hashable = None) -> None:
        """
        Forget one subject's peak (or every subject's when `key` is None), so
        the trail re-arms from the current value. This is what a rule change or
        an adoption after a restart does.
        """
        if key is None:
            self._states.clear()
        else:
            self._states.pop(key, None)

    def prune(self, live_keys: Iterable[Hashable]) -> None:
        """Drop the states of subjects that are gone (a closed leg or group)."""
        keep = set(live_keys)
        for key in [k for k in self._states if k not in keep]:
            del self._states[key]

    def __len__(self) -> int:
        return len(self._states)


class TrailLevels:
    """
    The four trackers a replay session needs: the run's total P&L, per-group
    P&L, and per-leg premium points and percent (tracked separately, each
    against its own trail).
    """

    RUN: Any = "run"

    def __init__(self) -> None:
        self.overall = TrailTracker()
        self.group = TrailTracker()
        self.leg_points = TrailTracker()
        self.leg_percent = TrailTracker()

    def prune_groups(self, group_ids: Iterable[str]) -> None:
        self.group.prune(group_ids)

    def prune_legs(self, keys: Iterable[Hashable]) -> None:
        self.leg_points.prune(keys)
        self.leg_percent.prune(keys)

    def reset(self) -> None:
        """Re-arm everything from the current values (rule change / restart)."""
        for tracker in (self.overall, self.group, self.leg_points, self.leg_percent):
            tracker.reset()
