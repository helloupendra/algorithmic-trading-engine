"""
strategies/market_structure.py

Market structure the way Smart Money Concepts reads it — swing points, breaks
of structure, changes of character and the inducement — as a reader a strategy
can feed one candle at a time.

This is the same reading the chart draws (Data → Market structure,
`src/AlgoTrading.Infrastructure/Smc/MarketStructure.cs`), rule for rule, so a
strategy never trades a structure the chart does not show. The C# version reads
a whole series at once for a chart; this one is incremental, because a live
runner has one new candle at a time. `tests/test_market_structure.py` runs the
schematic from the C# tests through this reader and asserts the same marks.

It is the structure rules that match, and only those: the order blocks,
fair-value gaps and order-flow runs the C# reader also emits are chart-side
readings, not ported here, so a strategy cannot see them — the same way
`method=fractal` has no equivalent on this side.

The rules, and where the schools differ, are in docs/smart-money-concepts.md.
In short:

- A **swing high** stands when a later candle trades below the low of the candle
  that made it (the "valid pullback" rule); a swing low is the mirror. A swing is
  only known at the candle that confirmed it, so nothing here looks ahead.
- **HH / HL / LH / LL** compare each swing with the previous swing of its kind.
- The **inducement** is a pullback inside the current leg: in a rally, a swing low
  above the protected low. It is taken when a candle trades through it, wick
  included, and a leg stays induced until the next break.
- A **break of structure** is a close through the last confirmed swing in the
  trend's direction, and only counts once the inducement has been taken.
- A **change of character** is a close through the level that protected the trend
  — the pullback the last break came out of. The trend turns there. The first
  break of all is recorded as a BOS.

Nothing in this module trades; it only reads. What a strategy does with a BOS is
its own business, and the evidence for whether that pays is in the strategy's
own spec.
"""

from __future__ import annotations

from dataclasses import dataclass, field, replace
from typing import Any, Dict, List, Optional

HIGH = "high"
LOW = "low"
BULLISH = "bullish"
BEARISH = "bearish"
NONE = "none"
BOS = "BOS"
CHOCH = "CHOCH"


@dataclass(frozen=True)
class Bar:
    """One candle, as the reader needs it."""
    time_utc: str
    open: float
    high: float
    low: float
    close: float


@dataclass(frozen=True)
class Swing:
    index: int
    time_utc: str
    price: float
    kind: str                       # HIGH | LOW
    confirmed_index: int
    confirmed_time_utc: str
    label: Optional[str] = None     # "HH" | "HL" | "LH" | "LL"; None for the first of its kind

    @property
    def is_high(self) -> bool:
        return self.kind == HIGH


@dataclass(frozen=True)
class Event:
    """A level giving way: BOS (the trend carried on) or CHOCH (it turned)."""
    kind: str                       # BOS | CHOCH
    direction: str                  # BULLISH | BEARISH
    level: float
    level_index: int
    level_time_utc: str
    break_index: int
    break_time_utc: str
    break_price: float


@dataclass
class Inducement:
    """The pullback inside a leg whose stops the market is said to come for."""
    kind: str
    level: float
    index: int
    time_utc: str
    swept_index: Optional[int] = None
    swept_time_utc: Optional[str] = None


@dataclass
class MarketStructure:
    """
    Reads structure candle by candle. `push` returns the events that candle
    fired — usually none, so a strategy can act on the return value directly.

    Only closed candles may be pushed. A live runner is handed a forming candle;
    pushing it would mark a swing that may never print.
    """

    #: "close" (a candle must close through a level) or "wick".
    break_on: str = "close"
    #: "last" (the pullback the leg is on now) or "first" (the leg's first pullback).
    inducement_mode: str = "last"

    bars: List[Bar] = field(default_factory=list)
    swings: List[Swing] = field(default_factory=list)
    events: List[Event] = field(default_factory=list)
    inducements: List[Inducement] = field(default_factory=list)

    trend: str = NONE
    #: Breaking this turns the trend: the pullback the last break came out of.
    guard: Optional[Swing] = None
    #: Breaking this, once the inducement is taken, is a break of structure.
    target: Optional[Swing] = None
    inducement: Optional[Inducement] = None
    inducement_taken: bool = False

    # --- swing detection (the valid-pullback rule) --------------------------
    _leg: int = 0                   # +1 while an up leg's high runs, -1 for a down leg, 0 before the first
    _high_index: int = 0
    _low_index: int = 0
    _first_high: Optional[Swing] = None
    _first_low: Optional[Swing] = None
    _last_high: Optional[Swing] = None
    _last_low: Optional[Swing] = None
    _last_label_high: Optional[float] = None
    _last_label_low: Optional[float] = None

    # ------------------------------------------------------------------ read --
    def push(self, bar: Bar) -> List[Event]:
        """Consume one closed candle; returns the events it fired."""
        self.bars.append(bar)
        index = len(self.bars) - 1
        if index == 0:
            return []

        for swing in self._swings_confirmed_at(index):
            self._adopt(swing)

        self._sweep(index, bar)
        return self._breaks(index, bar)

    @property
    def protected_level(self) -> Optional[float]:
        return self.guard.price if self.guard else None

    @property
    def break_level(self) -> Optional[float]:
        return self.target.price if self.target else None

    @property
    def inducement_level(self) -> Optional[float]:
        return self.inducement.level if self.inducement and self.inducement.swept_index is None else None

    @property
    def last_event(self) -> Optional[Event]:
        return self.events[-1] if self.events else None

    def describe(self) -> Dict[str, Any]:
        """The state in one dict, for a log line or a signal's metadata."""
        return {
            "trend": self.trend,
            "protected_level": self.protected_level,
            "break_level": self.break_level,
            "inducement_level": self.inducement_level,
            "inducement_taken": self.inducement_taken,
            "swings": len(self.swings),
            "events": len(self.events),
        }

    # --------------------------------------------------------------- internals --
    def _swings_confirmed_at(self, index: int) -> List[Swing]:
        """
        The valid-pullback rule: a high stands when a later candle takes the low
        of the candle that made it, and the mirror for a low. At most one swing
        can confirm on a candle, because the two tests are exclusive.
        """
        bar = self.bars[index]
        found: List[Swing] = []
        if self._leg >= 0 and bar.high > self.bars[self._high_index].high:
            self._high_index = index
        if self._leg <= 0 and bar.low < self.bars[self._low_index].low:
            self._low_index = index

        if self._leg >= 0 and index > self._high_index and bar.low < self.bars[self._high_index].low:
            made = self.bars[self._high_index]
            found.append(Swing(self._high_index, made.time_utc, made.high, HIGH, index, bar.time_utc))
            self._leg = -1
            self._low_index = self._extreme(self._high_index, index, lowest=True)
        elif self._leg <= 0 and index > self._low_index and bar.high > self.bars[self._low_index].high:
            made = self.bars[self._low_index]
            found.append(Swing(self._low_index, made.time_utc, made.low, LOW, index, bar.time_utc))
            self._leg = 1
            self._high_index = self._extreme(self._low_index, index, lowest=False)

        return found

    def _extreme(self, start: int, end: int, lowest: bool) -> int:
        best = start
        for i in range(start + 1, end + 1):
            if (self.bars[i].low < self.bars[best].low) if lowest else (self.bars[i].high > self.bars[best].high):
                best = i
        return best

    def _adopt(self, swing: Swing) -> None:
        """Take up a confirmed swing: label it, and make it a level where it is one."""
        if swing.is_high:
            if self._last_label_high is not None:
                swing = replace(swing, label="HH" if swing.price > self._last_label_high else "LH")
            self._last_label_high = swing.price
        else:
            if self._last_label_low is not None:
                swing = replace(swing, label="HL" if swing.price > self._last_label_low else "LL")
            self._last_label_low = swing.price
        self.swings.append(swing)

        if swing.is_high:
            self._last_high = swing
            if self._first_high is None:
                self._first_high = swing
            # In a rally the next high is the level to break; in a fall it is the
            # pullback whose stops the market will want first.
            if self.trend == BULLISH:
                self.target = swing
            elif self.trend == BEARISH and self.guard is not None and swing.price < self.guard.price:
                self._induce(swing)
        else:
            self._last_low = swing
            if self._first_low is None:
                self._first_low = swing
            if self.trend == BEARISH:
                self.target = swing
            elif self.trend == BULLISH and self.guard is not None and swing.price > self.guard.price:
                self._induce(swing)

    def _induce(self, swing: Swing) -> None:
        """
        The leg's inducement. "first" keeps the leg's first pullback, as the
        concept is taught; "last" takes the pullback the leg is on now, which is
        what the widely used indicators do and what keeps working on a strong
        trend. Whether the leg has been induced is not forgotten because a newer
        pullback formed — only a break starts the leg over.
        """
        if self.inducement_mode == "first" and self.inducement is not None:
            return
        if self.inducement is not None:
            self.inducements.append(self.inducement)
        self.inducement = Inducement(swing.kind, swing.price, swing.index, swing.time_utc)

    def _sweep(self, index: int, bar: Bar) -> None:
        live = self.inducement
        if live is None or live.swept_index is not None or index <= live.index:
            return
        taken = bar.low < live.level if live.kind == LOW else bar.high > live.level
        if taken:
            live.swept_index = index
            live.swept_time_utc = bar.time_utc
            self.inducement_taken = True

    def _breaks(self, index: int, bar: Bar) -> List[Event]:
        up = bar.close if self.break_on == "close" else bar.high
        down = bar.close if self.break_on == "close" else bar.low

        # The level that turns the trend: the protected one once a trend is known,
        # and before that whichever of the first two swings gives way.
        turn_up = None if self.trend == BULLISH else (self.guard if self.trend == BEARISH else self._first_high)
        turn_down = None if self.trend == BEARISH else (self.guard if self.trend == BULLISH else self._first_low)

        if turn_up is not None and turn_up.is_high and up > turn_up.price:
            return [self._break(CHOCH, BULLISH, turn_up, index, bar, up)]
        if turn_down is not None and not turn_down.is_high and down < turn_down.price:
            return [self._break(CHOCH, BEARISH, turn_down, index, bar, down)]
        if (self.trend == BULLISH and self.inducement_taken
                and self.target is not None and self.target.is_high and up > self.target.price):
            return [self._break(BOS, BULLISH, self.target, index, bar, up)]
        if (self.trend == BEARISH and self.inducement_taken
                and self.target is not None and not self.target.is_high and down < self.target.price):
            return [self._break(BOS, BEARISH, self.target, index, bar, down)]
        return []

    def _break(self, kind: str, direction: str, level: Swing, index: int, bar: Bar, price: float) -> Event:
        # The first break of all is a continuation of nothing: it is a BOS.
        if kind == CHOCH and self.trend == NONE:
            kind = BOS
        event = Event(kind, direction, level.price, level.index, level.time_utc, index, bar.time_utc, price)
        self.events.append(event)

        self.trend = direction
        # What the move came from now protects the trend: the pullback the break
        # came out of, which is the inducement the market has just taken.
        self.guard = (self._last_low or self.guard) if direction == BULLISH else (self._last_high or self.guard)
        # The level to break next is not known until the next swing confirms.
        self.target = None
        if self.inducement is not None:
            self.inducements.append(self.inducement)
            self.inducement = None
        self.inducement_taken = False
        return event


def bars_from_frames(frames: List[Any]) -> List[Bar]:
    """BarFrame objects (backtest or live) to the reader's bars."""
    return [Bar(f.timestamp_utc, float(f.open), float(f.high), float(f.low), float(f.close)) for f in frames]
