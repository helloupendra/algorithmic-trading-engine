"""
strategies/index_exits.py

Exit rules measured on the underlying index, for strategies that buy an option on the index's direction.

Why on the index and not on the premium: the run's risk guard already has premium stops and targets
(risk.leg.stopLossPoints / targetPoints), and on recorded 1-minute premiums a premium stop of 30-40% sold
trend days in their first dip (TrendRider research, 16 Nov 2026). An index rule asks the question the trade
was opened on - is the move still going? - in index points, scaled by the day's normal range (ADR) so one
setting fits NIFTY, BANKNIFTY and SENSEX alike.

Configured per strategy, from its parameters ("index_exits"), as a dict or JSON text:

    "index_exits": {
      "stop_adr": 0.5,                 # sell when the index is 0.5 x ADR against the entry
      "trail_arm_adr": 0.5,            # once 0.5 x ADR in favour ...
      "trail_giveback_adr": 0.35,      # ... sell on a 0.35 x ADR give-back from the best
      "supertrend": [10, 3],           # sell when the timeframe's Supertrend turns against the trade
      "ema_close": 21,                 # sell when a candle closes on the wrong side of EMA(21)
      "psar": {"step": 0.02, "max": 0.2},   # sell when price closes through the Parabolic SAR
      "chandelier": [14, 3],           # sell when a close is 3 x ATR(14) from the best close since entry
      "exit_after_ist": "14:45"        # sell at this time whatever the trade shows
    }

Two kinds of rule, checked at different times:

  - **tick rules** (stop, trail, time): every call, on the spot the strategy is handed - live, every tick;
  - **bar rules** (Supertrend, EMA, PSAR, Chandelier): once per closed candle, on the closed candles only,
    so a rule never acts on a candle that has not finished.

The position the rules read is the strategy's own dict; it must carry "direction" ("BUY" for a call, "SELL"
for a put), "entry_spot" and, for the ADR rules, "adr". The rules keep their running state in it ("best",
"best_close"), so it is saved and restored with the strategy's state.

Every rule is off unless set; a rule that cannot be computed yet (too few candles) stands aside.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Dict, Optional, Sequence, Tuple

from strategies import indicators


def _num(value: Any, name: str) -> Optional[float]:
    if value is None or value == "":
        return None
    try:
        out = float(value)
    except (TypeError, ValueError) as ex:
        raise ValueError(f"index_exits.{name} must be a number") from ex
    if out < 0:
        raise ValueError(f"index_exits.{name} cannot be negative")
    return out


def _pair(value: Any, name: str, first: str, second: str, defaults: Tuple[float, float]) -> Optional[Tuple[float, float]]:
    """[a, b], {"first": a, "second": b} or true (the defaults); None/false is off."""
    if value is None or value is False:
        return None
    if value is True:
        return defaults
    if isinstance(value, dict):
        a, b = value.get(first, defaults[0]), value.get(second, defaults[1])
    elif isinstance(value, (list, tuple)) and len(value) == 2:
        a, b = value
    else:
        raise ValueError(f"index_exits.{name} must be [{first}, {second}], an object or true")
    a, b = _num(a, f"{name}.{first}"), _num(b, f"{name}.{second}")
    if not a or not b:
        raise ValueError(f"index_exits.{name} needs both {first} and {second} above 0")
    return a, b


@dataclass(frozen=True)
class IndexExitRules:
    stop_adr: float = 0.0
    trail_arm_adr: float = 0.0
    trail_giveback_adr: float = 0.0
    supertrend: Optional[Tuple[float, float]] = None     # (period, multiple)
    ema_close: Optional[int] = None
    psar: Optional[Tuple[float, float]] = None            # (step, max)
    chandelier: Optional[Tuple[float, float]] = None      # (ATR period, multiple)
    exit_after_ist: Optional[str] = None                  # "HH:MM"

    # ------------------------------------------------------------- parsing --
    @classmethod
    def parse(cls, raw: Any) -> Optional["IndexExitRules"]:
        """The rules from a parameter value (dict or JSON text); None when nothing is set."""
        if raw is None or raw == "" or raw == {}:
            return None
        if isinstance(raw, str):
            try:
                raw = json.loads(raw)
            except ValueError as ex:
                raise ValueError("index_exits must be an object or JSON text") from ex
        if not isinstance(raw, dict):
            raise ValueError("index_exits must be an object")
        known = {"stop_adr", "trail_arm_adr", "trail_giveback_adr", "supertrend", "ema_close", "psar",
                 "chandelier", "exit_after_ist"}
        unknown = sorted(set(raw) - known)
        if unknown:
            raise ValueError(f"index_exits: unknown rule(s) {', '.join(unknown)}")
        ema = raw.get("ema_close")
        exit_after = raw.get("exit_after_ist")
        if exit_after:
            exit_after = str(exit_after).strip()
            try:
                datetime.strptime(exit_after, "%H:%M")
            except ValueError as ex:
                raise ValueError("index_exits.exit_after_ist must be HH:MM") from ex
        rules = cls(
            stop_adr=_num(raw.get("stop_adr"), "stop_adr") or 0.0,
            trail_arm_adr=_num(raw.get("trail_arm_adr"), "trail_arm_adr") or 0.0,
            trail_giveback_adr=_num(raw.get("trail_giveback_adr"), "trail_giveback_adr") or 0.0,
            supertrend=_pair(raw.get("supertrend"), "supertrend", "period", "multiple", (10, 3.0)),
            ema_close=int(_num(ema, "ema_close")) if ema not in (None, False, 0) else None,
            psar=_pair(raw.get("psar"), "psar", "step", "max", (0.02, 0.2)),
            chandelier=_pair(raw.get("chandelier"), "chandelier", "period", "multiple", (14, 3.0)),
            exit_after_ist=exit_after or None,
        )
        if rules.trail_arm_adr and not rules.trail_giveback_adr:
            raise ValueError("index_exits.trail_giveback_adr must be above 0 when trail_arm_adr is set")
        return rules if rules.any_rule else None

    def merged(self, **overrides: Any) -> "IndexExitRules":
        """A copy with some fields replaced (a strategy's own defaults under a run's explicit settings)."""
        return IndexExitRules(**{**self.__dict__, **overrides})

    @property
    def any_rule(self) -> bool:
        return bool(self.stop_adr or self.trail_arm_adr or self.supertrend or self.ema_close or self.psar
                    or self.chandelier or self.exit_after_ist)

    @property
    def bar_rules(self) -> bool:
        return bool(self.supertrend or self.ema_close or self.psar or self.chandelier)

    def describe(self) -> Dict[str, Any]:
        """The settings, for a signal's metadata."""
        return {k: v for k, v in self.__dict__.items() if v}

    # --------------------------------------------------------------- ticks --
    def check_tick(self, position: Dict[str, Any], spot: float, now_ist: Optional[datetime],
                   name: str = "the index") -> Optional[str]:
        """The stop, the trail and the time exit, on the latest spot. Returns the reason to sell, or None."""
        if self.exit_after_ist and now_ist is not None and now_ist.strftime("%H:%M") >= self.exit_after_ist:
            return f"time exit: {now_ist:%H:%M} IST is at or after {self.exit_after_ist}"
        entry = position.get("entry_spot")
        if entry is None or spot <= 0:
            return None
        buy = position.get("direction") == "BUY"
        favour = spot - float(entry) if buy else float(entry) - spot
        position["best"] = max(float(position.get("best") or 0.0), favour)
        adr = float(position.get("adr") or 0.0)
        if adr <= 0:
            return None
        if self.stop_adr and -favour >= self.stop_adr * adr:
            return (f"index stop: {name} {spot:g} is {-favour:.0f} points against the entry {float(entry):g}, "
                    f"past {self.stop_adr:g} x the {adr:.0f}-point average range")
        best = position["best"]
        if self.trail_arm_adr and best >= self.trail_arm_adr * adr and best - favour >= self.trail_giveback_adr * adr:
            return (f"index trail: {name} {spot:g} gave back {best - favour:.0f} points from its best "
                    f"({best:.0f} in favour), past {self.trail_giveback_adr:g} x the {adr:.0f}-point average range")
        return None

    # ---------------------------------------------------------------- bars --
    def check_bar(self, position: Dict[str, Any], bars: Sequence[Any], name: str = "the index") -> Optional[str]:
        """The candle rules, on closed candles only (the last one has just closed). The reason to sell, or None."""
        if not bars or not self.bar_rules:
            return None
        buy = position.get("direction") == "BUY"
        close = float(bars[-1].close)
        if self.chandelier:
            period, multiple = int(self.chandelier[0]), self.chandelier[1]
            best_close = position.get("best_close")
            best_close = close if best_close is None else (max(best_close, close) if buy else min(best_close, close))
            position["best_close"] = best_close
            atr = indicators.wilder_atr(bars, period)
            if atr:
                line = best_close - multiple * atr if buy else best_close + multiple * atr
                if (close < line) if buy else (close > line):
                    return (f"chandelier exit: {name} closed {close:g}, {abs(best_close - close):.0f} points from its best "
                            f"close {best_close:g}, past {multiple:g} x ATR({period}) {atr:.1f}")
        if self.supertrend:
            st = indicators.supertrend(bars, int(self.supertrend[0]), self.supertrend[1])
            if st and st["direction"] == ("bearish" if buy else "bullish"):
                return (f"supertrend exit: {name}'s Supertrend({int(self.supertrend[0])}, {self.supertrend[1]:g}) "
                        f"turned {st['direction']} at {st['level']:g}")
        if self.ema_close:
            value = indicators.ema([float(b.close) for b in bars], self.ema_close)
            if value is not None and ((close < value) if buy else (close > value)):
                return f"EMA exit: {name} closed {close:g} {'below' if buy else 'above'} EMA({self.ema_close}) {value:.1f}"
        if self.psar:
            out = indicators.parabolic_sar(bars, self.psar[0], self.psar[1])
            if out and out.get("direction") == ("bearish" if buy else "bullish"):
                return f"PSAR exit: {name} closed {close:g} through the Parabolic SAR {float(out['sar']):.1f}"
        return None

