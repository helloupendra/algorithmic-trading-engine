from __future__ import annotations

from typing import Any, Dict, List, Optional

from strategies.base_strategy import BaseStrategy, StrategyInput, StrategySignal
from strategies.strike_math import resolve_step, round_to_step
from strategies.fulcrum._direction import BUY, describe_for, direction_note, legs_summary_for, resolve_direction
from strategies.fulcrum._exit_rules import closing_legs, long_exit_reason, resolve_long_exit


class FulcrumStrategy(BaseStrategy):
    """Rolling ATM short straddle: close and re-open whenever the ATM strike changes."""
    name = "Fulcrum"
    description = (
        "Runs a rolling short straddle: sells the ATM call and put, and whenever the ATM strike moves to a "
        "different strike it closes the old straddle and opens a fresh one at the new ATM. Profits from "
        "premium decay in range-bound sessions; every roll realises the P&L of the previous straddle. "
        "Needs live spot ticks and the ATM CE/PE contracts."
    )
    category = "Adjustment"
    legs_summary = "Sell ATM CE + Sell ATM PE, rolled on every ATM change"
    default_lots = 1
    # No strike step here on purpose. The platform reads the real grid off the
    # option chain and hands it over on every bar; a default here would be one
    # underlying's number quietly applied to all of them.
    default_params: Dict[str, Any] = {}

    def __init__(self, params: Optional[Dict[str, Any]] = None):
        self.params = params or {}
        # Only an explicit override. None means "use whatever the platform says".
        self.strike_step = self.params.get("strike_step")
        # Lots per leg; the platform multiplies by the contract's lot size.
        self.lots = self.lots_from(self.params, self.default_lots)
        # Which way this variant trades. Registered twice: once as the seller it
        # was written as, once as a buyer. It holds no wings either way.
        self.direction, self.use_hedges = resolve_direction(self.params)
        # A buyer cannot use the seller's roll: see strategies/fulcrum/_exit_rules.py.
        self.long_exit = resolve_long_exit(self.params)
        self.description = (
            f"{describe_for(self.description, self.direction, self.use_hedges)} "
            f"{direction_note(self.direction, self.use_hedges)}"
        )
        self.legs_summary = legs_summary_for(self.legs_summary, self.direction, self.use_hedges)

    def initialize_state(self) -> Dict[str, Any]:
        return {
            "ce_list": [],
            "pe_list": [],
            "straddle_list": [],
            "last_trade_strike": None,
            "signal_count": 0,
            "current_group_id": None,
            "current_group_legs": [],
            # When and where the open group was entered. A long group is closed
            # on distance travelled and time held, not on the ATM changing, so
            # it has to remember its own starting point.
            "group_entry_strike": None,
            "group_entry_utc": None,
        }

    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        signals: List[StrategySignal] = []

        step = resolve_step(inp.strike_step, self.strike_step)
        atm = inp.atm_strike if inp.atm_strike is not None else round_to_step(inp.spot_price, step)

        previous_trade_strike = state["last_trade_strike"]

        atm_ce = inp.contracts.get("atm_ce")
        atm_pe = inp.contracts.get("atm_pe")

        if not atm_ce or not atm_pe:
            return []

        # First setup => open first short straddle group
        if previous_trade_strike is None:
            state["last_trade_strike"] = atm
            state["signal_count"] += 1

            group_id = self._group_id(inp.timestamp_utc, state["signal_count"])
            state["current_group_id"] = group_id
            state["current_group_legs"] = [
                {"symbol": atm_ce.symbol, "side": self.direction, "quantity": self.lots, "price": None},
                {"symbol": atm_pe.symbol, "side": self.direction, "quantity": self.lots, "price": None},
            ]

            state["straddle_list"] = [atm]
            state["ce_list"] = [atm]
            state["pe_list"] = [atm]
            state["group_entry_strike"] = atm
            state["group_entry_utc"] = inp.timestamp_utc

            signals.append(
                StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Initial Fulcrum {self._structure()} at ATM {atm}",
                    price=inp.spot_price,
                    legs=[
                        {"symbol": atm_ce.symbol, "side": self.direction, "quantity": self.lots, "price": None},
                        {"symbol": atm_pe.symbol, "side": self.direction, "quantity": self.lots, "price": None},
                    ],
                    metadata={
                        "group_id": group_id,
                        "atm_strike": atm,
                        "underlying": inp.underlying,
                    },
                )
            )

            return signals

        # What ends the open group depends on which way it trades. A seller is
        # done when the ATM moves off its strike; a buyer is done when the move
        # it paid for either arrives or runs out of time.
        if self.direction == BUY:
            close_reason = long_exit_reason(
                state.get("group_entry_strike"),
                state.get("group_entry_utc"),
                inp.spot_price,
                inp.timestamp_utc,
                step,
                self.long_exit["target_steps"],
                self.long_exit["max_hold_minutes"],
            )
        else:
            close_reason = (
                f"Closing previous group because ATM shifted from {previous_trade_strike} to {atm}"
                if atm != previous_trade_strike else None
            )

        if close_reason:
            old_group_id = state["current_group_id"]
            old_group_legs = state["current_group_legs"]

            # CLOSE old group
            if old_group_id and old_group_legs:
                close_legs = closing_legs(old_group_legs)

                signals.append(
                    StrategySignal(
                        strategy_name=self.name,
                        signal_type="CLOSE_GROUP",
                        timestamp_utc=inp.timestamp_utc,
                        reason=close_reason,
                        price=inp.spot_price,
                        legs=close_legs,
                        metadata={
                            "group_id": old_group_id,
                            "previous_atm": previous_trade_strike,
                            "current_atm": atm,
                            "underlying": inp.underlying,
                        },
                    )
                )

            # OPEN new group
            state["last_trade_strike"] = atm
            state["signal_count"] += 1

            new_group_id = self._group_id(inp.timestamp_utc, state["signal_count"])
            new_group_legs = [
                {"symbol": atm_ce.symbol, "side": self.direction, "quantity": self.lots, "price": None},
                {"symbol": atm_pe.symbol, "side": self.direction, "quantity": self.lots, "price": None},
            ]

            state["current_group_id"] = new_group_id
            state["current_group_legs"] = new_group_legs

            state["straddle_list"] = [atm]
            state["ce_list"] = [atm]
            state["pe_list"] = [atm]
            state["group_entry_strike"] = atm
            state["group_entry_utc"] = inp.timestamp_utc

            signals.append(
                StrategySignal(
                    strategy_name=self.name,
                    signal_type="OPEN_GROUP",
                    timestamp_utc=inp.timestamp_utc,
                    reason=f"Opening new Fulcrum {self._structure()} at ATM {atm}",
                    price=inp.spot_price,
                    legs=new_group_legs,
                    metadata={
                        "group_id": new_group_id,
                        "atm_strike": atm,
                        "underlying": inp.underlying,
                    },
                )
            )

        return signals

    def _structure(self) -> str:
        """How this variant's position reads in a signal reason."""
        return "long straddle" if self.direction == BUY else "short straddle"

    def _group_id(self, timestamp_utc: str, counter: int) -> str:
        safe_ts = timestamp_utc.replace(":", "").replace("-", "").replace("T", "").replace("Z", "")
        return f"FULCRUM-{safe_ts}-{counter:03d}"
