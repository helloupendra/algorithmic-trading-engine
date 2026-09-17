"""
strategies/builder/signal_builder.py

A strategy written from the console instead of in Python: the operator lists the
conditions that make a long setup and the ones that make a short one, and this
turns them into the platform's BUY / SELL signals.

    {
      "long_conditions":  ["close>vwap", "close>ema:9", "body>=0.5", "rsi_cross_up:60"],
      "short_conditions": ["close<vwap", "close<ema:9", "body>=0.5", "rsi_cross_down:40"],
      "instrument_kind":  "options"        # or "equity"
    }

It deliberately does nothing else. When to trade, how often, where the stop
moves, which strike a BUY takes and what a fill costs are the run's own rules
(`backtest/rules.py`, `strategies/signal_filters.py`), so the same setup can be
tried under different rules without editing the setup — and two runs of it stay
comparable.

The conditions are judged on the last CLOSED bar of the run's resolution
(`strategies/builder/conditions.py`). A side with no conditions never fires, and
a bar where both sides hold is left alone: that is a contradiction, not a trade.
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, ContractRequirement, DataRequirement, StrategyInput, StrategySignal
from strategies.builder import conditions as cond


class SignalBuilderStrategy(BaseStrategy):
    """Long and short setups built from named conditions, as a backtestable strategy."""

    name = "SignalBuilder"
    description = (
        "A strategy you write in the console: list the conditions for a long setup and for a short "
        "one (price against VWAP or an EMA, an EMA pair, RSI levels or a cross, candle body, "
        "Supertrend, an opening-range break, the gap) and it emits the matching BUY or SELL. When it "
        "may trade, how often, its exits, the strike it takes and its costs are the run's own rules."
    )
    category = "Custom"
    legs_summary = "One leg per signal: the ATM option of the run's underlying, or the instrument itself"
    supported_underlyings: List[str] = ["NIFTY", "BANKNIFTY", "SENSEX", "FINNIFTY", "MIDCPNIFTY"]
    instrument_kind = "options"
    default_lots = 1
    default_parameters: Dict[str, Any] = {
        "long_conditions": ["close>vwap", "close>ema:9", "body>=0.5", "rsi_cross_up:60"],
        "short_conditions": ["close<vwap", "close<ema:9", "body>=0.5", "rsi_cross_down:40"],
        "resolution": "5m",
        "instrument_kind": "options",
        "explain_every_bar": False,
    }

    def __init__(self, params: Optional[Dict[str, Any]] = None) -> None:
        self.params = dict(params or {})
        self.lots = self.lots_from(self.params, self.default_lots)
        self.long = cond.parse_all(self._p("long_conditions"))
        self.short = cond.parse_all(self._p("short_conditions"))
        # Unreadable conditions fail here, when the run starts, rather than on
        # the bar they would first have been checked.
        self._long_holds = cond.compile_all(self.long)
        self._short_holds = cond.compile_all(self.short)
        self.explain = bool(self._p("explain_every_bar"))
        self._said = ""

    def _p(self, key: str) -> Any:
        return self.params.get(key, self.default_parameters.get(key))

    # --- catalogue ----------------------------------------------------------

    @classmethod
    def get_data_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[DataRequirement]:
        resolution = str((params or {}).get("resolution") or cls.default_parameters["resolution"])
        return [DataRequirement(symbol_type="index", resolution=resolution)]

    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        # The engine turns a BUY/SELL into the ATM leg (or the instrument itself
        # on an equity run), and the run's contract rules may move that strike.
        if str((params or {}).get("instrument_kind") or cls.instrument_kind) == "equity":
            return []
        return list(super().get_contract_requirements(params))

    # --- the loop -----------------------------------------------------------

    def initialize_state(self) -> Dict[str, Any]:
        return {"last_bar": None}

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        if (inp.metadata or {}).get("source") == "warmup":
            return []

        resolution = str(self._p("resolution") or "5m")
        bars = (inp.bars or {}).get(resolution, {}).get("index") or []
        if inp.mode == "LivePaper":
            # Live, the newest bar is still forming; a setup is judged on closed bars only.
            bars = bars[:-1]
        if not bars:
            return []

        stamp = getattr(bars[-1], "timestamp_utc", None)
        if stamp is None or state.get("last_bar") == stamp:
            return []
        state["last_bar"] = stamp

        long_ok, short_ok = self._long_holds(bars), self._short_holds(bars)
        if self.explain and not (long_ok or short_ok):
            self._explain(bars)
        if long_ok == short_ok:
            # Neither side, or a bar that argues for both: nothing honest to do.
            return []

        side = "BUY" if long_ok else "SELL"
        held = self.long if long_ok else self.short
        return [StrategySignal(
            strategy_name=self.name,
            signal_type=side,
            timestamp_utc=inp.timestamp_utc,
            reason=f"{'long' if long_ok else 'short'} setup: {cond.describe(held)}",
            legs=[],
            metadata={"direction": side, "conditions": list(held), "signal_bar_utc": str(stamp)},
        )]

    def _explain(self, bars: List[Any]) -> None:
        """One line saying which condition failed, and only when it changes."""
        parts = []
        for name, holds in cond.explain(self.long, bars):
            if not holds:
                parts.append(name)
        text = f"[{self.name}] no long setup: {', '.join(parts) or 'nothing'} did not hold"
        if text != self._said:
            self._said = text
            print(text, flush=True)
