"""
strategies/ghost_tangent_crossings_v2.py

GhostTangentCrossings with the fixes its spec lists under Limitations, built to
be replayed offline beside the original. The geometry - ZigZag pivots, the
quarter-ellipse, its tangent and the crossing scan - is the original's,
inherited unchanged. What changes is which crossings are traded and what
happens after the entry:

  - every signal carries the leg, the line and the crossing it was read from
    (`metadata.ghost`), so a run can be analysed from its own rows instead of
    rebuilding each line from the candles;
  - only closed candles inside the index session are read: live is handed a
    forming candle, and the ingestor's pre-open prints became pivots (the
    09:00 spike behind the first signal of 8 and 9 Sep);
  - a crossing older than `max_signal_age_bars` is not traded: the original
    scans from the later pivot forward and bought at today's premium on a
    close up to 25 bars old;
  - a crossing further past the later pivot than `line_life` lengths of its
    leg is not traded: the line moves away from the pivot without bound, so
    in a flat market the original's "break" was only a matter of time;
  - legs shorter than `min_leg_bars` are not traded: a one-bar leg fires on
    its own pivot bar, a two-bar leg draws a flat line;
  - one position at a time: a signal in the held direction is ignored, an
    opposite one closes the position and, with `on_opposite` = "reverse",
    opens the other side;
  - a position is closed when a candle closes beyond the later pivot of the
    leg it was opened on (`stop_at_pivot`): the swing the trade bet against
    is still going.

Every change is a parameter, so a replay can switch one off and measure what
it was worth. Time stops, breakeven, trailing, trade windows and daily limits
are not here: the run's own rules (backtest/rules.py, `exits` and `limits`)
already apply them to any strategy.

The original stays registered and unchanged; this one is a separate catalog
entry.

**On evidence.** None yet. This exists to be tested offline before it is run
anywhere else; see docs/strategies/GhostTangentCrossingsV2.md.
"""

from __future__ import annotations

from dataclasses import replace
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from core.market_hours import CASH_CLOSE, SESSION_OPEN
from core.resolutions import is_daily
from strategies.base_strategy import StrategyInput, StrategySignal
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.market_structure import stamp_key

IST = timezone(timedelta(hours=5, minutes=30))

#: What an opposite signal does to a held position.
OPPOSITE_REVERSE = "reverse"
OPPOSITE_CLOSE = "close"
OPPOSITE_IGNORE = "ignore"

#: Why a crossing was found but not traded. Counted in `state["dropped"]`, each
#: crossing once, so a replay can say what every fix removed.
DROP_REASONS = ("short_leg", "stale", "line_expired", "conflict", "same_direction", "opposite_ignored",
                "no_contract")

#: How many recently dropped crossings are remembered, so a crossing re-found on
#: every tick is counted once. Two legs are evaluated per call; a handful is plenty.
_DROPPED_KEYS_KEPT = 50


def _ist(stamp: Any) -> Optional[datetime]:
    key = stamp_key(stamp)
    return key.astimezone(IST) if key is not None else None


def _compact(stamp: Any) -> str:
    return str(stamp).replace(":", "").replace("-", "")[:15]


class GhostTangentCrossingsV2Strategy(GhostTangentCrossingsStrategy):
    """GhostTangentCrossings that trades only fresh crossings, holds one position and stops at the leg's pivot."""

    name = "GhostTangentCrossingsV2"
    description = (
        "GhostTangentCrossings with its known faults fixed, for testing beside the original. Same ZigZag "
        "pivots and ellipse-tangent trigger line on the 5-minute index chart, but it reads closed candles "
        "inside the session only, trades a crossing only while it is fresh and close to the swing it came "
        "from, skips one- and two-bar legs, holds one position at a time (an opposite signal reverses it) "
        "and closes the position when a candle closes beyond the swing point the trade bet against. Every "
        "signal records the leg and the line it was read from. The run's stop-loss, target, exits and "
        "close square-off still apply. Untested: run it offline first."
    )
    category = "Directional"
    legs_summary = "Buy ATM CE on an up-break, or Buy ATM PE on a down-break; one position at a time"
    default_lots = 1
    default_params: Dict[str, Any] = {
        **GhostTangentCrossingsStrategy.default_params,
        # Candles between the crossing and the newest closed one that a signal
        # may be old. 0 trades a crossing of any age, as the original does.
        "max_signal_age_bars": 1,
        # How far past the later pivot a crossing may come, in lengths of its
        # leg. 0 is no limit, as the original.
        "line_life": 1.0,
        # The shortest leg, in candles, whose crossing is traded. 0 trades every leg.
        "min_leg_bars": 3,
        # Close the position when a candle closes beyond the later pivot of the
        # leg it was opened on.
        "stop_at_pivot": True,
        # An opposite signal while a position is held: "reverse" closes it and
        # opens the other side, "close" only closes it, "ignore" keeps it.
        "on_opposite": OPPOSITE_REVERSE,
    }

    def __init__(self, params: Dict[str, Any] = None):
        super().__init__(params)
        p, d = self.params, self.default_params
        self.max_signal_age_bars = int(p.get("max_signal_age_bars", d["max_signal_age_bars"]))
        self.line_life = float(p.get("line_life", d["line_life"]))
        self.min_leg_bars = int(p.get("min_leg_bars", d["min_leg_bars"]))
        self.stop_at_pivot = bool(p.get("stop_at_pivot", d["stop_at_pivot"]))
        self.on_opposite = str(p.get("on_opposite") or d["on_opposite"]).strip().lower()

    def initialize_state(self) -> Dict[str, Any]:
        state = super().initialize_state()
        state.update({
            "position": None,       # {"group_id", "direction", "symbol", "stop", "since", "day"}
            "opened": 0,            # positions opened, which names the groups
            "dropped": {why: 0 for why in DROP_REASONS},
            "dropped_keys": [],
        })
        return state

    # ------------------------------------------------------------------ read --
    def _readable(self, bars: List[Any], mode: str, chart: str) -> List[Any]:
        """
        The candles the geometry may read: closed ones, and on an intraday chart
        only those inside the index session. A replay hands over closed candles;
        live hands over a forming one last. A daily candle is stamped 05:30 IST,
        so the session test does not apply to it.
        """
        closed = bars if mode == "OfflineReplay" else bars[:-1]
        if is_daily(chart):
            return list(closed)
        # The engine hands a replay one list that grows in place. Reading it from
        # the top on every candle costs the square of the run: about 700 million
        # candle reads for two years. A live window is rebuilt, so it starts over.
        start, kept = 0, []
        seen = getattr(self, "_replay_read", None)
        if mode == "OfflineReplay" and seen is not None and seen[0] is bars and seen[1] <= len(bars):
            start, kept = seen[1], seen[2]
        for frame in closed[start:]:
            at = _ist(getattr(frame, "timestamp_utc", None))
            # A candle with no readable time is kept rather than silently lost.
            if at is None or SESSION_OPEN <= at.time() < CASH_CLOSE:
                kept.append(frame)
        if mode == "OfflineReplay":
            self._replay_read = (bars, len(bars), kept)
        return kept

    def _read_structure(self, state: Dict[str, Any], bars: List[Any], mode: str) -> None:
        # The candles handed on are closed already (see _readable); the original
        # holds the newest back live, which here would read the structure one late.
        super()._read_structure(state, bars, "OfflineReplay")

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        chart = self.chart(inp)
        frames = inp.bars.get(chart, {})
        bars = self._readable(frames.get("index", []), inp.mode, chart)
        if not bars:
            return []
        readable = replace(inp, bars={**inp.bars, chart: {**frames, "index": bars}})
        newest = bars[-1]

        signals: List[StrategySignal] = []
        self._sync(state, inp, newest)
        self._stop(state, inp, newest, signals)
        self._act(state, inp, newest, super().on_bar(state, readable), signals)
        return signals

    # ------------------------------------------------------------- geometry --
    def _evaluate_zig_zag(self, state: Dict[str, Any], bars: List[Any], bar_index: int, start_x: int, end_x: int,
                          start_y: float, end_y: float, polarity: bool, inp: StrategyInput,
                          is_ghost: bool = False) -> Optional[StrategySignal]:
        """
        The original's line and crossing, then the gates. Note the original's
        naming: `start_x` is the earlier pivot's bar but `start_y` the LATER
        pivot's price, `end_y` the earlier one's.
        """
        ellipse_points = self._generate_ellipse(start_x, end_x, start_y, end_y)
        tangent, slope = self._ellipse_slope(start_x, end_x, start_y, end_y, ellipse_points)
        current_trigger = tangent["price"] + slope * (bar_index - tangent["index"])
        state["target_sell_trigger" if polarity else "target_buy_trigger"] = current_trigger

        forward_length = end_x - tangent["index"]
        found, found_price, found_idx = self._check_b(bar_index, bars, tangent["index"], tangent["price"],
                                                       forward_length, slope, polarity)
        # Already signalled: before the gates, so a traded crossing ageing past
        # the limit is not counted as a stale one.
        if not found or state["last_signal_idx"] == found_idx:
            return None

        leg_bars = end_x - start_x
        age = bar_index - found_idx
        if self.min_leg_bars and leg_bars < self.min_leg_bars:
            self._drop(state, "short_leg", found_idx)
            return None
        if self.max_signal_age_bars and age > self.max_signal_age_bars:
            self._drop(state, "stale", found_idx)
            return None
        if self.line_life and (found_idx - end_x) > self.line_life * leg_bars:
            self._drop(state, "line_expired", found_idx)
            return None
        state["last_signal_idx"] = found_idx

        cross_at = len(bars) - 1 - age
        trigger_at_cross = tangent["price"] + slope * (found_idx - tangent["index"])
        context = {
            "kind": "ghost" if is_ghost else "confirmed",
            "leg": "up" if polarity else "down",
            "a": {"bar": start_x, "price": round(float(end_y), 2)},
            "b": {"bar": end_x, "price": round(float(start_y), 2)},
            "leg_bars": leg_bars,
            "leg_points": round(abs(float(start_y) - float(end_y)), 2),
            "tangent": {"bar": tangent["index"], "price": round(float(tangent["price"]), 2)},
            "slope": round(float(slope), 4),
            "cross_bar": found_idx,
            "cross_time": str(getattr(bars[cross_at], "timestamp_utc", "")) if 0 <= cross_at < len(bars) else None,
            "cross_close": round(float(found_price), 2),
            "trigger_at_cross": round(float(trigger_at_cross), 2),
            "trigger_now": round(float(current_trigger), 2),
            "newest_bar": bar_index,
            "signal_age_bars": age,
        }
        kind = "(Ghost)" if is_ghost else "(Confirmed)"
        return StrategySignal(
            strategy_name=self.name,
            signal_type="SELL" if polarity else "BUY",
            timestamp_utc=inp.timestamp_utc,
            reason=f"Ghost Tangent Break {kind} detected at bar {found_idx} for {inp.underlying}",
            symbol=inp.underlying,
            price=found_price,
            metadata={"ghost": context},
        )

    def _drop(self, state: Dict[str, Any], why: str, key: Any) -> None:
        """Counts a crossing that was not traded, once however many ticks re-find it."""
        mark = f"{why}:{key}"
        keys: List[str] = state["dropped_keys"]
        if mark in keys:
            return
        keys.append(mark)
        del keys[:-_DROPPED_KEYS_KEPT]
        state["dropped"][why] = state["dropped"].get(why, 0) + 1

    # ---------------------------------------------------------------- trade --
    def _sync(self, state: Dict[str, Any], inp: StrategyInput, newest: Any) -> None:
        """
        Forget a position the strategy no longer has. The replay says which
        groups are open, so a run rule or the square-off closing it is seen at
        once, and a position carried overnight on daily candles is kept. Live
        says nothing, so the position is dropped when a new session starts: a
        run's risk rule closing it leaves this strategy believing it is held
        for the rest of that day.
        """
        position = state["position"]
        if position is None:
            return
        open_groups = (inp.metadata or {}).get("open_groups")
        if open_groups is not None:
            if position["group_id"] not in open_groups:
                state["position"] = None
            return
        at = _ist(getattr(newest, "timestamp_utc", None))
        if at is not None and position.get("day") and at.date().isoformat() != position["day"]:
            state["position"] = None

    def _stop(self, state: Dict[str, Any], inp: StrategyInput, newest: Any, signals: List[StrategySignal]) -> None:
        """
        The leg's own invalidation: a candle closed beyond the later pivot, so
        the swing the position bet against is still going. Judged only on
        candles that closed after the entry's own.
        """
        position = state["position"]
        if position is None or not self.stop_at_pivot or position.get("stop") is None:
            return
        at, since = stamp_key(getattr(newest, "timestamp_utc", None)), stamp_key(position.get("since"))
        if at is None or (since is not None and at <= since):
            return
        close, stop = float(newest.close), float(position["stop"])
        beyond = close > stop if position["direction"] == "SELL" else close < stop
        if not beyond:
            return
        side = "put" if position["direction"] == "SELL" else "call"
        signals.append(self._close(position, inp, (
            f"Closed {close:g} {'above' if position['direction'] == 'SELL' else 'below'} the leg's later pivot "
            f"{stop:g}: the swing the {side} bet against is still going")))
        state["position"] = None

    def _act(self, state: Dict[str, Any], inp: StrategyInput, newest: Any, found: List[StrategySignal],
             signals: List[StrategySignal]) -> None:
        if not found:
            return
        if len({sig.signal_type for sig in found}) > 1:
            # One candle crossed both lines: there is no reading to trade.
            self._drop(state, "conflict", getattr(newest, "timestamp_utc", ""))
            return
        sig = found[-1]
        position = state["position"]
        if position is not None:
            if position["direction"] == sig.signal_type:
                self._drop(state, "same_direction", sig.metadata.get("ghost", {}).get("cross_bar"))
                return
            if self.on_opposite == OPPOSITE_IGNORE:
                self._drop(state, "opposite_ignored", sig.metadata.get("ghost", {}).get("cross_bar"))
                return
            signals.append(self._close(position, inp, f"Opposite signal, {sig.signal_type}: {sig.reason}"))
            state["position"] = None
            if self.on_opposite == OPPOSITE_CLOSE:
                return
        self._open(state, inp, newest, sig, signals)

    def _open(self, state: Dict[str, Any], inp: StrategyInput, newest: Any, sig: StrategySignal,
              signals: List[StrategySignal]) -> None:
        buy = sig.signal_type == "BUY"
        contract = (inp.contracts or {}).get("atm_ce" if buy else "atm_pe")
        if contract is None:
            self._drop(state, "no_contract", sig.metadata.get("ghost", {}).get("cross_bar"))
            return
        state["opened"] += 1
        group_id = f"GTC2_{_compact(inp.timestamp_utc)}_{state['opened']:03d}"
        metadata = dict(sig.metadata or {})
        metadata.update({"group_id": group_id, "direction": sig.signal_type})
        signals.append(StrategySignal(
            strategy_name=self.name,
            signal_type="OPEN_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=f"{sig.reason}; buying the {'call' if buy else 'put'}",
            legs=[{"symbol": contract.symbol, "side": "BUY", "quantity": self.lots}],
            metadata=metadata,
        ))
        at = stamp_key(getattr(newest, "timestamp_utc", None))
        day = _ist(getattr(newest, "timestamp_utc", None))
        state["position"] = {
            "group_id": group_id,
            "direction": sig.signal_type,
            "symbol": contract.symbol,
            "stop": metadata.get("ghost", {}).get("b", {}).get("price"),
            # ISO strings, not datetimes: the state is saved as JSON.
            "since": at.isoformat() if at is not None else None,
            "day": day.date().isoformat() if day is not None else None,
        }

    def _close(self, position: Dict[str, Any], inp: StrategyInput, reason: str) -> StrategySignal:
        return StrategySignal(
            strategy_name=self.name,
            signal_type="CLOSE_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=reason,
            legs=[{"symbol": position["symbol"], "side": "SELL", "quantity": self.lots}],
            metadata={"group_id": position["group_id"]},
        )
