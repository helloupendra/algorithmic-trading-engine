"""
strategies/directional/smc_structure_break.py

Buys an index option when market structure breaks the way Smart Money Concepts
reads a chart, and gets out when the structure turns against the position.

The structure comes from `strategies/market_structure.py`, which is the same
reading the chart draws (Data → Market structure). A leg is only tradable once
the market has taken its inducement — the pullback whose stops sit behind it —
so a break that nobody was induced into is not traded. On a bullish break the
ATM call is bought, on a bearish break the ATM put.

Unlike GhostTangentCrossings this strategy has an exit of its own: the position
is closed when the structure changes character against it, which is the level
the method itself says invalidates the trade. The run's stop-loss, target and
end-of-day square-off still apply on top.

Both modes read the same candles: the backtest feeds closed candles at the run's
resolution, and the live runner is handed a forming candle that this strategy
holds back until it closes. Nothing is read from a candle that had not printed.

**On evidence.** Structure marks are a way of describing a chart, not a proven
edge. What this strategy did on stored NIFTY candles and real option premiums,
including the sessions where it lost, is written up in
docs/strategies/SmcStructureBreak.md. Read that before running it with money.
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional

from core.resolutions import minutes_of, to_strategy_resolution
from strategies.base_strategy import (
    BaseStrategy,
    ContractRequirement,
    DataRequirement,
    StrategyInput,
    StrategySignal,
)
from strategies.market_structure import BEARISH, BOS, BULLISH, CHOCH, Bar, MarketStructure

#: What the strategy may act on.
TRADE_BOS = "bos"
TRADE_CHOCH = "choch"
TRADE_BOTH = "both"


class SmcStructureBreakStrategy(BaseStrategy):
    """Break of structure (or change of character) on the index; ATM option bought in that direction."""

    name = "SmcStructureBreak"
    description = (
        "Reads market structure the way Smart Money Concepts teaches it — swing points, the inducement a leg "
        "has to take, breaks of structure and changes of character — and buys the ATM call on a bullish break "
        "or the ATM put on a bearish one. A break only counts once the leg's inducement has been taken. The "
        "position is closed when the structure turns against it, so unlike the other directional strategies "
        "this one has an exit of its own; the run's stop-loss, target and 15:30 square-off still apply. Needs "
        "index candles at the run's resolution (5-minute by default) and, live, the spot ticks that build them."
    )
    category = "Directional"
    legs_summary = "Buy ATM CE on a bullish break, or Buy ATM PE on a bearish break"
    default_lots = 1
    default_params: Dict[str, Any] = {
        # "auto": the run's own candles when they are longer than 5 minutes, else
        # 5m. Any resolution ("5m", "15m", "1D") forces that chart.
        "timeframe": "auto",
        # Which breaks to trade: continuation ("bos"), reversal ("choch") or both.
        "trade": TRADE_BOS,
        # "break": enter on the candle that broke the level.
        # "retest": wait for price to come back to the broken level, which is what
        # the method teaches, at the cost of the entries that never come back.
        "entry": "break",
        "retest_bars": 6,
        # Strikes away from the money, on the underlying's own grid: 0 is ATM,
        # 1 is one strike out of the money.
        "strike_steps": 0,
        # A level is broken by a close, or by a wick when this says so.
        "break_on": "close",
        # "last": the pullback the leg is on now. "first": the leg's first
        # pullback, as it is taught — stricter, and it stalls on a strong trend.
        "inducement": "last",
        # Close the position when the structure changes character against it.
        "exit_on_turn": True,
    }

    #: Candles the structure needs before its first tradable break. A leg needs a
    #: swing, its pullback, the sweep and the break, and the reader confirms
    #: nothing until a candle takes the liquidity of the one before it.
    warmup_bars = 150

    @classmethod
    def get_data_requirements(cls) -> List[DataRequirement]:
        return [DataRequirement(symbol_type="index", resolution="5m")]

    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        steps = float((params or {}).get("strike_steps", 0) or 0)
        moneyness = "atm" if steps == 0 else "otm"
        return [
            ContractRequirement(key="atm_ce", option_type="CE", moneyness=moneyness, steps=steps, param="strike_steps"),
            ContractRequirement(key="atm_pe", option_type="PE", moneyness=moneyness, steps=steps, param="strike_steps"),
        ]

    def __init__(self, params: Dict[str, Any] = None):
        params = params or {}
        self.params = params
        self.timeframe = str(params.get("timeframe") or self.default_params["timeframe"]).strip()
        self.trade = str(params.get("trade") or self.default_params["trade"]).strip().lower()
        self.entry = str(params.get("entry") or self.default_params["entry"]).strip().lower()
        self.retest_bars = int(params.get("retest_bars", self.default_params["retest_bars"]))
        self.break_on = str(params.get("break_on") or self.default_params["break_on"]).strip().lower()
        self.inducement = str(params.get("inducement") or self.default_params["inducement"]).strip().lower()
        self.exit_on_turn = bool(params.get("exit_on_turn", self.default_params["exit_on_turn"]))
        self.lots = self.lots_from(params, self.default_lots)

    # ------------------------------------------------------------------ setup --
    def chart(self, inp: StrategyInput) -> str:
        """
        The candles the structure is read on. An explicit timeframe wins; on
        "auto" a run stepping through longer candles (a 15m or 1D backtest) is
        read on its own candles, and everything else on the 5-minute chart.
        """
        if self.timeframe and self.timeframe.lower() != "auto":
            return to_strategy_resolution(self.timeframe)
        run = (inp.metadata or {}).get("resolution")
        try:
            if run and minutes_of(run) > 5:
                return to_strategy_resolution(run)
        except ValueError:
            pass
        return "5m"

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "reader": MarketStructure(break_on=self.break_on, inducement_mode=self.inducement),
            "seen": None,           # timestamp of the last candle fed to the reader
            "fed": 0,               # candles fed, which names the groups
            "position": None,       # {"group_id", "direction", "symbol", "level"}
            "pending": None,        # a retest waiting to happen
            "day": None,            # the session the position was opened in
        }

    # ------------------------------------------------------------------- read --
    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        bars = inp.bars.get(self.chart(inp), {}).get("index", [])
        if not bars:
            return []

        # Live is handed the candle that is still forming; a structure read on it
        # would mark swings that may never print. The replay is handed closed
        # candles only.
        closed = bars if inp.mode == "OfflineReplay" else bars[:-1]
        events = self._feed(state, closed)
        if not closed:
            return []

        signals: List[StrategySignal] = []
        last = closed[-1]
        self._sync(state, inp, last)
        self._exit(state, inp, events, signals)
        self._enter(state, inp, events, last, signals)
        return signals

    def _feed(self, state: Dict[str, Any], closed: List[Any]) -> List[Any]:
        """Push every candle the reader has not seen yet; returns the events they fired."""
        reader: MarketStructure = state["reader"]
        fired: List[Any] = []
        for frame in closed[state["fed"]:]:
            stamp = getattr(frame, "timestamp_utc", None)
            if stamp is not None and stamp == state["seen"]:
                continue
            fired.extend(reader.push(Bar(stamp, float(frame.open), float(frame.high), float(frame.low), float(frame.close))))
            state["seen"] = stamp
            state["fed"] += 1
        return fired

    def _sync(self, state: Dict[str, Any], inp: StrategyInput, last: Any) -> None:
        """
        Forget a position the strategy no longer has. The replay says which
        groups are still open, so a stop-loss, a target or the square-off closing
        the position is seen at once. Live says nothing, so the position is
        dropped when a new session starts — a run's own risk rule closing a leg
        leaves this strategy out for the rest of that day, which the spec says.
        """
        position = state["position"]
        if position is None:
            return
        open_groups = (inp.metadata or {}).get("open_groups")
        if open_groups is not None and position["group_id"] not in open_groups:
            state["position"] = None
            return
        day = str(getattr(last, "timestamp_utc", ""))[:10]
        if state["day"] and day and day != state["day"]:
            state["position"] = None
        state["day"] = day or state["day"]

    # ------------------------------------------------------------------ trade --
    def _tradable(self, event: Any) -> bool:
        if self.trade == TRADE_BOTH:
            return True
        return event.kind == (BOS if self.trade == TRADE_BOS else CHOCH)

    def _exit(self, state: Dict[str, Any], inp: StrategyInput, events: List[Any], signals: List[StrategySignal]) -> None:
        """The method's own invalidation: the structure changed character against the position."""
        position = state["position"]
        if not position or not self.exit_on_turn:
            return
        against = BEARISH if position["direction"] == BULLISH else BULLISH
        turned = next((e for e in events if e.kind == CHOCH and e.direction == against), None)
        if turned is None:
            return

        signals.append(StrategySignal(
            strategy_name=self.name,
            signal_type="CLOSE_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=(f"Structure turned {turned.direction} at {turned.level:g}: the level that protected the "
                    f"{position['direction']} leg gave way"),
            legs=[{"symbol": position["symbol"], "side": "SELL", "quantity": self.lots}],
            metadata={"group_id": position["group_id"], "structure": state["reader"].describe()},
        ))
        state["position"] = None
        state["pending"] = None

    def _enter(self, state: Dict[str, Any], inp: StrategyInput, events: List[Any], last: Any,
               signals: List[StrategySignal]) -> None:
        if state["position"] is not None:
            return

        # A retest that was waiting: price has come back to the broken level.
        pending = state["pending"]
        if pending is not None:
            pending["bars"] -= 1
            back = float(last.low) <= pending["level"] if pending["direction"] == BULLISH else float(last.high) >= pending["level"]
            if back:
                self._open(state, inp, pending["direction"], pending["level"], f"retest of {pending['level']:g}", signals)
                return
            if pending["bars"] <= 0:
                state["pending"] = None

        for event in events:
            if not self._tradable(event):
                continue
            reason = (f"{event.kind} {event.direction} through {event.level:g} "
                      f"(inducement taken at {state['reader'].describe().get('inducement_level') or 'the pullback'})")
            if self.entry == "retest":
                state["pending"] = {"direction": event.direction, "level": event.level, "bars": self.retest_bars}
                return
            self._open(state, inp, event.direction, event.level, reason, signals)
            return

    def _open(self, state: Dict[str, Any], inp: StrategyInput, direction: str, level: float, reason: str,
              signals: List[StrategySignal]) -> None:
        key = "atm_ce" if direction == BULLISH else "atm_pe"
        contract = (inp.contracts or {}).get(key)
        if contract is None:
            return

        group_id = f"SMC_{str(inp.timestamp_utc).replace(':', '').replace('-', '')[:15]}_{len(state['reader'].events):03d}"
        signals.append(StrategySignal(
            strategy_name=self.name,
            signal_type="OPEN_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=f"{reason}; buying the {'call' if direction == BULLISH else 'put'}",
            legs=[{"symbol": contract.symbol, "side": "BUY", "quantity": self.lots}],
            metadata={
                "group_id": group_id,
                "direction": "BUY" if direction == BULLISH else "SELL",
                "structure": state["reader"].describe(),
                "level": level,
            },
        ))
        state["position"] = {"group_id": group_id, "direction": direction, "symbol": contract.symbol, "level": level}
        state["pending"] = None
