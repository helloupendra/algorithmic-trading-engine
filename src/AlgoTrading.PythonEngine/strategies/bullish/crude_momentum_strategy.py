"""
strategies/bullish/crude_momentum_strategy.py

Crude Oil intraday momentum: a 5-minute candle whose whole body clears both the
session VWAP and the 9-period EMA buys the ATM call.

The indicators are read off the MCX crude FUTURE - the run's spot symbol, since
a commodity has no spot and the near-month future stands in for it - and the
trade is expressed in that underlying's options, the only instrument this
account trades.

There is no exit here on purpose. The platform already enforces premium stops,
targets and the break-even trail from real fills at leg, group and overall level
(`parametersJson.risk`, swept every few seconds by the API's risk guard). A
second implementation working off bar closes could only disagree with the
ledger, and the strategy is never told its fill price anyway.
"""

from __future__ import annotations

import uuid
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional

from strategies import indicators
from strategies.base_strategy import (
    BaseStrategy,
    ContractRequirement,
    DataRequirement,
    StrategyInput,
    StrategySignal,
)

# MCX runs one session a day, 09:00 to 23:30/23:55 IST, so an IST calendar date
# identifies a session exactly. VWAP is anchored to it.
IST = timezone(timedelta(hours=5, minutes=30))


class CrudeMomentumStrategy(BaseStrategy):
    """
    Buys the ATM call when a bullish 5-minute crude candle closes with its whole
    body above both the session VWAP and the 9 EMA.
    """

    name = "CrudeMomentum"
    description = (
        "Intraday momentum on MCX crude. On the 5-minute chart of the near-month crude future it tracks the "
        "session VWAP and a 9-period EMA, and buys the ATM call when a bullish candle (close > open) closes "
        "with its ENTIRE body above both lines - open and close each above VWAP and above the EMA. Runs the "
        "whole MCX session with no time-of-day filter. It has no exit of its own: the run's risk rules close "
        "the leg, so set a leg stop-loss, target and trail when you start it. After an entry it will not buy "
        "again until a candle closes back below one of the two lines, which stops it stacking positions on a "
        "run of strong candles, and it takes at most `max_trades_per_session` entries a day. VWAP is anchored "
        "to the MCX session open, so it needs at least `min_session_bars` five-minute bars of that session "
        "before it will trade."
    )
    category = "Bullish"
    # A commodity, not an index family - the platform's default list does not
    # apply, and the launch dialog disables any underlying missing from here.
    supported_underlyings: List[str] = ["CRUDEOIL", "CRUDEOILM"]
    instrument_kind = "options"
    legs_summary = "Buy ATM CE"
    default_lots = 1
    default_params: Dict[str, Any] = {
        "ema_period": 9,
        # One hour of the session. VWAP over a handful of bars is not the
        # session's VWAP, and a strategy that trades off one is guessing.
        "min_session_bars": 12,
        "max_trades_per_session": 3,
        # The spec reads "closes completely above"; the body clearing both lines
        # is the usual meaning. Turn this on to demand the low clears them too.
        "require_low_above": False,
    }

    @classmethod
    def get_data_requirements(cls) -> List[DataRequirement]:
        return [DataRequirement(symbol_type="index", resolution="5m")]

    @classmethod
    def get_contract_requirements(cls, params: Optional[Dict[str, Any]] = None) -> List[ContractRequirement]:
        """
        Only the ATM call. This strategy is long-only, and every requirement is
        resolved and force-subscribed on the live feed each tick, so asking for
        a put would buy a subscription nothing ever reads.
        """
        return [ContractRequirement(key="atm_ce", option_type="CE")]

    def __init__(self, params: Dict[str, Any] = None):
        params = params or {}
        self.params = params
        self.ema_period = max(2, int(params.get("ema_period", self.default_params["ema_period"])))
        self.min_session_bars = max(1, int(params.get("min_session_bars", self.default_params["min_session_bars"])))
        self.max_trades_per_session = max(
            1, int(params.get("max_trades_per_session", self.default_params["max_trades_per_session"]))
        )
        self.require_low_above = bool(params.get("require_low_above", self.default_params["require_low_above"]))
        # Lots per leg; the platform multiplies by the contract's lot size.
        self.lots = self.lots_from(params, self.default_lots)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            # Armed means "a qualifying candle may be taken". An entry disarms
            # it; a candle closing back below either line arms it again. The
            # strategy is never told when the risk guard closed the position, so
            # re-entry is gated on the setup resetting, not on being flat.
            "armed": True,
            "session_date": None,
            "trades_this_session": 0,
            # The bar a decision was last taken on, so one closed candle is
            # judged once however many ticks arrive during the next one.
            "last_evaluated_bar": None,
            "last_entry_bar": None,
            "last_group_id": None,
        }

    # ---------------------------------------------------------------- maths --
    # Delegated to strategies/indicators so a filter, a backtest and this
    # strategy cannot drift into three different definitions of the same word.

    _parse_utc = staticmethod(indicators.parse_utc)
    _ema = staticmethod(indicators.ema)
    _vwap = staticmethod(indicators.vwap)

    @classmethod
    def _session_date(cls, bar):
        """The IST calendar date of a bar, which is its MCX session."""
        return indicators.session_date(bar)

    # ----------------------------------------------------------------- loop --

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []

        # Warmup replays the last fortnight bar by bar to build indicator state.
        # This strategy derives VWAP and the EMA from the bars it is handed on
        # every call, so it has none to build - and letting those bars count as
        # setups would spend the session's trade budget before the session did.
        if (inp.metadata or {}).get("source") == "warmup":
            return signals

        bars = inp.bars.get("5m", {}).get("index", [])
        # The newest bar is still forming. Decisions are taken on the one before
        # it, which is the last candle that has actually closed - the spec asks
        # for a candle that CLOSES above the lines, not one that is above them
        # halfway through.
        if len(bars) < self.ema_period + 2:
            return signals

        signal_bar = bars[-2]
        bar_stamp = getattr(signal_bar, "timestamp_utc", None)
        if bar_stamp is None or state.get("last_evaluated_bar") == bar_stamp:
            return signals
        state["last_evaluated_bar"] = bar_stamp

        session = self._session_date(signal_bar)
        if session is None:
            return signals

        # A new session resets the trade budget and re-arms, whatever the last
        # one ended on.
        if state.get("session_date") != session:
            state["session_date"] = session
            state["trades_this_session"] = 0
            state["armed"] = True

        history = bars[: len(bars) - 1]  # everything up to and including the signal bar
        session_bars = [b for b in history if self._session_date(b) == session]

        vwap = self._vwap(session_bars)
        ema = self._ema([float(b.close) for b in history], self.ema_period)
        if vwap is None or ema is None:
            return signals

        open_price = float(signal_bar.open)
        close_price = float(signal_bar.close)
        low_price = float(signal_bar.low)
        line = max(vwap, ema)

        # Re-arm on the setup breaking, whether or not a position is open. This
        # is what stops a run of strong candles opening a position on each one.
        if close_price < vwap or close_price < ema:
            state["armed"] = True
            return signals

        if len(session_bars) < self.min_session_bars:
            # VWAP anchored to a fraction of the session is not the session's
            # VWAP. Better to sit out than to trade off a number that is wrong.
            return signals

        if state.get("trades_this_session", 0) >= self.max_trades_per_session:
            return signals

        if not state.get("armed", True):
            return signals

        bullish = close_price > open_price
        body_above = open_price > vwap and open_price > ema and close_price > vwap and close_price > ema
        wick_above = (not self.require_low_above) or low_price > line
        if not (bullish and body_above and wick_above):
            return signals

        contract = inp.contracts.get("atm_ce")
        if contract is None:
            # The master had no CE at this strike for the traded expiry. The
            # runner has already logged which one; staying armed means the next
            # qualifying candle is still taken.
            return signals

        group_id = str(uuid.uuid4())
        signals.append(
            StrategySignal(
                strategy_name=self.name,
                signal_type="OPEN_GROUP",
                timestamp_utc=inp.timestamp_utc,
                reason=(
                    f"5m body above VWAP and {self.ema_period} EMA: "
                    f"O {open_price:.2f} / C {close_price:.2f} vs VWAP {vwap:.2f}, EMA {ema:.2f} "
                    f"({len(session_bars)} session bars). Buying CE {contract.strike_price}."
                ),
                symbol=contract.symbol,
                price=None,
                legs=[{
                    "symbol": contract.symbol,
                    "side": "BUY",
                    "quantity": self.lots,
                    "price": None,
                }],
                metadata={
                    "group_id": group_id,
                    "strategy_type": "Bullish",
                    "direction": "BUY",
                    "signal_bar_utc": str(bar_stamp),
                    "vwap": round(vwap, 2),
                    "ema": round(ema, 2),
                    "session_bars": len(session_bars),
                    "underlying_price": close_price,
                },
            )
        )

        state["armed"] = False
        state["trades_this_session"] = state.get("trades_this_session", 0) + 1
        state["last_entry_bar"] = bar_stamp
        state["last_group_id"] = group_id

        return signals
