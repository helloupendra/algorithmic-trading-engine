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

The research gates are parameters, so a replay can switch one off and measure
what it was worth. Execution guards always apply. Time stops, breakeven,
trailing, trade windows and daily limits
are not here: the run's own rules (backtest/rules.py, `exits` and `limits`)
already apply them to any strategy.

The original stays registered and unchanged; this one is a separate catalog
entry.

Historical replay was loss-making. Correct execution is not evidence of a
profitable trading edge; see docs/strategies/GhostTangentCrossingsV2.md.
"""

from __future__ import annotations

import math
from dataclasses import asdict, replace
from datetime import date, datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from core.market_hours import CASH_CLOSE, SESSION_OPEN
from core.resolutions import is_daily, minutes_of, to_strategy_resolution
from strategies.base_strategy import BarFrame, DataRequirement, StrategyInput, StrategySignal
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.market_structure import frames_after, stamp_key

IST = timezone(timedelta(hours=5, minutes=30))

#: What an opposite signal does to a held position.
OPPOSITE_REVERSE = "reverse"
OPPOSITE_CLOSE = "close"
OPPOSITE_IGNORE = "ignore"

#: Why a crossing was found but not traded. Counted in `state["dropped"]`, each
#: crossing once, so a replay can say what every fix removed.
DROP_REASONS = ("short_leg", "stale", "line_expired", "conflict", "same_direction", "opposite_ignored",
                "no_contract", "invalidated", "stale_data", "unknown_book", "untracked_position")

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
    requires_position_sync = True
    description = (
        "GhostTangentCrossings with fresh closed-candle signals and synchronized position tracking. Same ZigZag "
        "pivots and ellipse-tangent trigger line on the 5-minute index chart, but it reads closed candles "
        "inside the session only, trades a crossing only while it is fresh and close to the swing it came "
        "from, skips one- and two-bar legs, holds one position at a time (an opposite signal reverses it) "
        "and closes the position when a candle closes beyond the swing point the trade bet against. Every "
        "signal records the leg and the line it was read from. The run's stop-loss, target, exits and "
        "close square-off still apply. Historical replay was loss-making; validate before promotion."
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
        params = dict(params or {})
        for key in ("stop_at_pivot", "use_ghost_signals"):
            value = params.get(key, self.default_params[key])
            if isinstance(value, str) and value.lower() in ("true", "false"):
                value = value.lower() == "true"
            if not isinstance(value, bool):
                raise ValueError(f"{key} must be true or false")
            params[key] = value
        for key, lower, upper in (("pivot_forward", 1, 250), ("max_signal_age_bars", 0, 10000),
                                  ("min_leg_bars", 0, 10000), ("lots", 1, 10000)):
            value = params.get(key, params.get("quantity", 1) if key == "lots" else self.default_params[key])
            try:
                number = float(value)
                valid = not isinstance(value, bool) and math.isfinite(number) and number.is_integer() and lower <= number <= upper
            except (TypeError, ValueError):
                valid = False
            if not valid:
                raise ValueError(f"{key} must be an integer between {lower} and {upper}")
            params[key] = int(number)
        super().__init__(params)
        p, d = self.params, self.default_params
        self.max_signal_age_bars = int(p.get("max_signal_age_bars", d["max_signal_age_bars"]))
        try:
            self.line_life = float(p.get("line_life", d["line_life"]))
        except (TypeError, ValueError) as exc:
            raise ValueError("line_life must be finite and non-negative") from exc
        self.min_leg_bars = int(p.get("min_leg_bars", d["min_leg_bars"]))
        self.stop_at_pivot = bool(p.get("stop_at_pivot", d["stop_at_pivot"]))
        self.on_opposite = str(p.get("on_opposite") or d["on_opposite"]).strip().lower()
        if not math.isfinite(self.line_life) or self.line_life < 0:
            raise ValueError("line_life must be finite and non-negative")
        if self.on_opposite not in (OPPOSITE_REVERSE, OPPOSITE_CLOSE, OPPOSITE_IGNORE):
            raise ValueError("on_opposite must be reverse, close or ignore")
        if self.pivot_type not in ("Wick", "Body"):
            raise ValueError("pivot_type must be Wick or Body")
        if self.smc_filter not in ("off", "with", "against", "idm"):
            raise ValueError("smc_filter must be off, with, against or idm")
        if self.timeframe.lower() != "auto" and minutes_of(self.timeframe) not in (1, 3, 5, 10, 15, 30, 60, 120, 240, 1440):
            raise ValueError("Unsupported timeframe")
        # These rules convert bare BUY/SELL signals, not this strategy's groups.
        # Silently accepting them produces a backtest of the wrong contracts.
        contract = p.get("contract") or {}
        if not isinstance(contract, dict) or contract.get("strike_offset", 0) != 0 or contract.get("flip", False) is not False:
            raise ValueError("GhostTangentCrossingsV2 trades ATM options; contract strike_offset/flip are not supported")

    def get_data_requirements(self=None) -> List[DataRequirement]:
        # The catalogue asks the class; the runners ask a configured instance.
        chart = self.timeframe if self is not None else "auto"
        return [DataRequirement("index", "5m" if chart.lower() == "auto" else to_strategy_resolution(chart))]

    def initialize_state(self) -> Dict[str, Any]:
        state = super().initialize_state()
        state.update({
            "position": None,       # {"group_id", "direction", "symbol", "stop", "since", "day"}
            "opened": 0,            # positions opened, which names the groups
            "dropped": {why: 0 for why in DROP_REASONS},
            "dropped_keys": [],
            "history": [],
            "pending": [],
            "handled_crosses": [],
            "reading_version": 2,
        })
        return state

    # ------------------------------------------------------------------ read --
    def _readable(self, bars: List[Any], mode: str, chart: str, as_of: Any = None) -> List[Any]:
        """
        The candles the geometry may read: closed ones, and on an intraday chart
        only those inside the regular index session. A replay supplies closed
        candles; live closure uses the input clock, not the tail's position.
        Daily candles are stamped 05:30 IST and complete at the cash close.
        """
        now = stamp_key(as_of)
        closed = bars if mode == "OfflineReplay" or now is not None else bars[:-1]
        # The engine hands a replay one list that grows in place. Reading it from
        # the top on every candle costs the square of the run: about 700 million
        # candle reads for two years. A live window is rebuilt, so it starts over.
        start, kept = 0, []
        seen = getattr(self, "_replay_read", None)
        if mode == "OfflineReplay" and seen is not None and seen[0] is bars and seen[1] <= len(bars):
            start, kept = seen[1], seen[2]
        for frame in closed[start:]:
            at = _ist(getattr(frame, "timestamp_utc", None))
            if at is None:
                continue
            if not is_daily(chart) and not SESSION_OPEN <= at.time() < CASH_CLOSE:
                continue
            if mode != "OfflineReplay" and now is not None:
                end = (at.replace(hour=CASH_CLOSE.hour, minute=CASH_CLOSE.minute, second=0, microsecond=0)
                       if is_daily(chart) else min(at + timedelta(minutes=minutes_of(chart)),
                           at.replace(hour=CASH_CLOSE.hour, minute=CASH_CLOSE.minute, second=0, microsecond=0)))
                if end > now:
                    continue
            try:
                values = [float(getattr(frame, key)) for key in ("open", "high", "low", "close")]
            except (AttributeError, TypeError, ValueError):
                continue
            if not all(math.isfinite(v) and v > 0 for v in values) or values[1] < max(values[0], values[2], values[3]) or values[2] > min(values[0], values[3]):
                continue
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
        meta = inp.metadata or {}
        bars = self._readable(frames.get("index", []), inp.mode, chart, meta.get("bars_as_of", inp.timestamp_utc))
        if not bars:
            return []
        # Old checkpoints counted ticks rather than every unseen closed candle.
        # Rebuild only the geometry; never discard the position it was managing.
        seen = stamp_key(state.get("last_processed_bar_time"))
        first = min(stamp_key(b.timestamp_utc) for b in bars) if inp.mode != "OfflineReplay" else stamp_key(bars[0].timestamp_utc)
        lost_window = (inp.mode != "OfflineReplay" and seen is not None and first > seen
                       and first - seen > timedelta(minutes=minutes_of(chart)))
        rebased = state.get("reading_version") != 2 or state.get("history_chart", chart) != chart or lost_window
        if rebased:
            saved = {key: state.get(key) for key in ("position", "opened", "dropped", "dropped_keys",
                                                    "last_handled_cross", "handled_crosses")}
            state.clear()
            state.update(self.initialize_state())
            state.update({key: value for key, value in saved.items() if value is not None})
        state["history_chart"] = chart
        self._sync(state, inp, bars[-1])
        seen = stamp_key(state.get("last_processed_bar_time"))
        # Reject backward/duplicate timestamps; a rolling window may overlap the
        # persisted buffer, but the geometry must advance once per candle only.
        candidates = frames_after(bars, seen) if inp.mode == "OfflineReplay" else bars
        fresh = sorted({stamp_key(b.timestamp_utc): b for b in candidates
                        if seen is None or stamp_key(b.timestamp_utc) > seen}.items())
        history = ([BarFrame(**row) for row in state["history"]] if fresh else
                   [BarFrame(**state["history"][-1])] if state["history"] else [])
        signals: List[StrategySignal] = []
        for _, frame in fresh:
            history.append(BarFrame(inp.underlying, chart, stamp_key(frame.timestamp_utc).isoformat(),
                                    frame.open, frame.high, frame.low, frame.close, getattr(frame, "volume", 0)))
            del history[:-max(1024, 500 + self.pivot_forward + 1)]
            readable = replace(inp, bars={**inp.bars, chart: {**frames, "index": history}})
            found = super().on_bar(state, readable)
            if found:
                state["pending"] = [asdict(sig) for sig in found]
            if meta.get("source") != "warmup":
                self._stop(state, inp, frame, signals)
        if not history:
            return []
        if fresh:
            state["history"] = [vars(frame).copy() for frame in history]
        newest = history[-1]
        if meta.get("source") == "warmup":
            state["pending"] = []
            return []
        if rebased:
            for row in state["pending"]:
                self._consume(state, StrategySignal(**row))
            state["pending"] = []
            return signals
        if signals:
            # A stop invalidates the entry thesis. Do not reopen it on the same
            # candle (including subsequent ticks on that candle).
            state["pending"] = []
            return signals
        found = [StrategySignal(**row) for row in state["pending"]]
        found = self._filter_by_structure(state, found)
        self._act(state, inp, newest, found, signals)
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
        if not found:
            return None

        cross_at = len(bars) - 1 - (bar_index - found_idx)
        cross_time = str(getattr(bars[cross_at], "timestamp_utc", "")) if 0 <= cross_at < len(bars) else None
        if self._handled(state, {"cross_time": cross_time, "cross_bar": found_idx}):
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
        cross_at = len(bars) - 1 - age
        trigger_at_cross = tangent["price"] + slope * (found_idx - tangent["index"])
        context = {
            "kind": "ghost" if is_ghost else "confirmed",
            "leg": "up" if polarity else "down",
            "a": {"bar": start_x, "price": round(float(end_y), 2)},
            "b": {"bar": end_x, "price": float(start_y)},
            "leg_bars": leg_bars,
            "leg_points": round(abs(float(start_y) - float(end_y)), 2),
            "tangent": {"bar": tangent["index"], "price": float(tangent["price"])},
            "slope": float(slope),
            "cross_bar": found_idx,
            "cross_time": cross_time,
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
    @staticmethod
    def _cross_key(ghost: Dict[str, Any]) -> str:
        at = stamp_key(ghost.get("cross_time"))
        return at.isoformat() if at is not None else f"bar:{ghost.get('cross_bar')}"

    def _consume(self, state: Dict[str, Any], sig: StrategySignal) -> None:
        ghost = (sig.metadata or {}).get("ghost", {})
        state["last_signal_idx"] = ghost.get("cross_bar")
        keys = state.setdefault("handled_crosses", [])
        key = self._cross_key(ghost)
        if key not in keys:
            keys.append(key)
        del keys[:-100]
        at = stamp_key(ghost.get("cross_time"))
        last = stamp_key(state.get("last_handled_cross"))
        if at is not None and (last is None or at > last):
            state["last_handled_cross"] = at.isoformat()

    def _handled(self, state: Dict[str, Any], ghost: Dict[str, Any]) -> bool:
        at, last = stamp_key(ghost.get("cross_time")), stamp_key(state.get("last_handled_cross"))
        return (at is not None and last is not None and at <= last) or self._cross_key(ghost) in state.get("handled_crosses", [])

    def _sync(self, state: Dict[str, Any], inp: StrategyInput, newest: Any) -> None:
        """
        Only an authoritative book can say a position is gone. An unavailable
        book or a change of date must never turn exposure into a flat state.
        """
        position = state["position"]
        if position is None:
            return
        open_groups = (inp.metadata or {}).get("open_groups")
        if open_groups is not None:
            if position["group_id"] not in open_groups:
                state["position"] = None
            return

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
        found = [sig for sig in found if not self._handled(state, (sig.metadata or {}).get("ghost", {}))]
        if not found:
            return
        if len({sig.signal_type for sig in found}) > 1:
            # One candle crossed both lines: there is no reading to trade.
            self._drop(state, "conflict", getattr(newest, "timestamp_utc", ""))
            for sig in found:
                self._consume(state, sig)
            return
        sig = found[-1]
        ghost = (sig.metadata or {}).get("ghost", {})
        age = state["bar_index"] - ghost.get("cross_bar", state["bar_index"])
        if "tangent" in ghost:
            ghost["trigger_now"] = ghost["tangent"]["price"] + ghost["slope"] * (state["bar_index"] - ghost["tangent"]["bar"])
            ghost["newest_bar"], ghost["signal_age_bars"] = state["bar_index"], age
        if self.max_signal_age_bars and age > self.max_signal_age_bars:
            self._drop(state, "stale", self._cross_key(ghost))
            self._consume(state, sig)
            return
        position = state["position"]
        if position is not None:
            if position["direction"] == sig.signal_type:
                self._drop(state, "same_direction", sig.metadata.get("ghost", {}).get("cross_bar"))
                self._consume(state, sig)
                return
            if self.on_opposite == OPPOSITE_IGNORE:
                self._drop(state, "opposite_ignored", sig.metadata.get("ghost", {}).get("cross_bar"))
                self._consume(state, sig)
                return
            signals.append(self._close(position, inp, f"Opposite signal, {sig.signal_type}: {sig.reason}"))
            state["position"] = None
            if self.on_opposite == OPPOSITE_CLOSE:
                self._consume(state, sig)
                return
        self._open(state, inp, newest, sig, signals)

    def _open(self, state: Dict[str, Any], inp: StrategyInput, newest: Any, sig: StrategySignal,
              signals: List[StrategySignal]) -> None:
        buy = sig.signal_type == "BUY"
        meta = inp.metadata or {}
        groups = meta.get("open_groups")
        ghost = (sig.metadata or {}).get("ghost", {})
        key = self._cross_key(ghost)
        if inp.mode != "OfflineReplay" and groups is None:
            self._drop(state, "unknown_book", key)
            return
        # Groups being closed earlier in this batch are protected by run_tick:
        # a failed/unknown close prevents the later OPEN and restores state.
        closing = {s.metadata.get("group_id") for s in signals if s.signal_type == "CLOSE_GROUP"}
        if groups is not None and set(groups) - closing:
            self._drop(state, "untracked_position", key)
            return
        if inp.mode != "OfflineReplay":
            now, at = stamp_key(inp.timestamp_utc), stamp_key(newest.timestamp_utc)
            close_at = (at.astimezone(IST).replace(hour=CASH_CLOSE.hour, minute=CASH_CLOSE.minute, second=0, microsecond=0)
                        if is_daily(self.chart(inp)) else at + timedelta(minutes=minutes_of(self.chart(inp))))
            if now is None or now < close_at or now - close_at > timedelta(minutes=minutes_of(self.chart(inp))):
                self._drop(state, "stale_data", key)
                return
        pivot = ghost.get("b", {}).get("price")
        trigger = ghost.get("trigger_now")
        spot = float(inp.spot_price)
        if (not math.isfinite(spot) or spot <= 0
                or (self.stop_at_pivot and pivot is not None and (min(newest.close, spot) < pivot if buy else max(newest.close, spot) > pivot))
                or (trigger is not None and (newest.close <= trigger if buy else newest.close >= trigger))):
            self._drop(state, "invalidated", key)
            self._consume(state, sig)
            return
        contract = (inp.contracts or {}).get("atm_ce" if buy else "atm_pe")
        today = _ist(inp.timestamp_utc)
        try:
            expiry = date.fromisoformat(str(contract.expiry_date)[:10]) if contract is not None else None
        except ValueError:
            expiry = None
        if (contract is None or not contract.symbol or contract.option_type.upper() != ("CE" if buy else "PE")
                or contract.underlying.upper() != inp.underlying.upper()
                or expiry is None or (today is not None and expiry < today.date())):
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
            "lots": self.lots,
        }
        self._consume(state, sig)

    def _close(self, position: Dict[str, Any], inp: StrategyInput, reason: str) -> StrategySignal:
        return StrategySignal(
            strategy_name=self.name,
            signal_type="CLOSE_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=reason,
            legs=[{"symbol": position["symbol"], "side": "SELL", "quantity": position.get("lots", self.lots)}],
            metadata={"group_id": position["group_id"]},
        )
