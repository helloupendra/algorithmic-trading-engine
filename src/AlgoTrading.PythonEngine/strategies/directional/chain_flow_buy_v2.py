"""
strategies/directional/chain_flow_buy_v2.py

ChainFlowBuy with the faults its review found (the original's spec, "Limitations"), built to be replayed
offline beside the original. The entry rule - the trend candle and the four chain confirmations - is the
original's and runs unchanged: V2 calls the original's on_bar, then decides what to do with what it
signalled and with the position V2 holds.

  - One position at a time. The original buys again on a re-armed side while its first leg is still
    open, and can hold a CE and a PE together.
  - An exit of its own. The original has none and leaves every bought premium to the run's rules or the
    close. V2 closes when the trend that justified the entry breaks (the close crosses back over the slow
    EMA) or the OI flow turns against the position.
  - The contract bought is the one whose volume was checked. The original confirms the ATM option's
    volume surge, then buys the strike nearest 0.5 delta up to three strikes away; V2 searches the ATM
    strike only (the original's own `delta_search_strikes`, set to 0) and refuses an entry whose strike
    or expiry is not the ATM contract's.
  - The OI flow is measured over the window it names. Samples are kept by count, so after a chain outage
    the original compared OI with a sample from before it and "15 minutes of flow" could be an hour; V2
    refuses a flow whose earlier sample is older than `flow_max_lookback_minutes`.
  - In a replay, a chain captured after the decision is refused. The original's age check is two-sided,
    so up to 180 s of the future passed it.

When V2 drops an entry the original signalled, the original's own bookkeeping for it (the day's trade
count, the disarmed side) is put back, so a dropped entry neither uses up a trade nor disarms its side.

Every change is a parameter, and with all of them off V2 signals what the original signals.

**On evidence.** None yet. This exists to be tested offline before it is run anywhere else; see
docs/strategies/ChainFlowBuyV2.md.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Tuple

from strategies import indicators
from strategies.base_strategy import StrategyInput, StrategySignal
from strategies.directional.chain_flow_buy import ChainFlowBuyStrategy, data_check, near_money, oi_by_strike, oi_flow

#: Which EMA a close must cross back over to end the trend a position was opened on.
TREND_SLOW = "slow"
TREND_FAST = "fast"
TREND_OFF = "off"

#: What an opposite entry does to a held position.
OPPOSITE_REVERSE = "reverse"
OPPOSITE_CLOSE = "close"
OPPOSITE_IGNORE = "ignore"

#: Why an entry the original signalled was not taken. Counted in `state["dropped"]`.
DROP_REASONS = ("chain_ahead", "flow_too_old", "contract_mismatch", "same_side_held", "opposite_ignored")


def undo_entry(state: Dict[str, Any], last_group_id: Any) -> None:
    """
    Put back what the original's entry changed, as if it had not signalled: one fewer trade today, no side
    disarmed, the previous group id. No side is the right value, not a guess: the original re-arms a side
    whenever its EMA stack stops holding, before it checks entries, and an entry on one side needs the
    other side's stack broken, so the moment before any entry has no side disarmed. Read from the state the
    entry left rather than saved before the call, so it keeps whatever else the call did first (a session
    reset, a re-arm).
    """
    state["trades_this_session"] = max(0, int(state.get("trades_this_session") or 0) - 1)
    state["disarmed_side"] = None
    state["last_group_id"] = last_group_id


class ChainFlowBuyV2Strategy(ChainFlowBuyStrategy):
    """ChainFlowBuy that holds one position, exits when its trend or flow turns, and buys the contract it checked."""

    name = "ChainFlowBuyV2"
    description = (
        "ChainFlowBuy with its known faults fixed, for testing beside the original. The same entry: a "
        "5-minute trend candle confirmed by put-minus-call OI flow, an ATM option volume surge, long build-up "
        "or short covering and a calm IV. V2 buys the ATM contract whose volume was checked, refuses a flow "
        "measured against a sample older than `flow_max_lookback_minutes`, holds one position at a time (an "
        "opposite entry reverses it), and closes it when the close crosses back over the slow EMA or the OI "
        "flow turns against it. In a replay a chain captured after the decision is refused. The run's "
        "stop-loss, target, exits and close square-off still apply. Untested: run it offline first."
    )
    legs_summary = "Buy one ATM CE (bullish) or PE (bearish); one position at a time"
    default_params: Dict[str, Any] = {
        **ChainFlowBuyStrategy.default_params,
        # The original's own parameter: 0 searches the chain's ATM strike only, so the contract bought is
        # the at-the-money one whose volume surge the entry checked. 3 is the original's search.
        "delta_search_strikes": 0,
        # Refuse an entry whose strike or expiry is not the ATM contract's whose volume was read.
        "require_same_contract": True,
        # The oldest the earlier OI sample may be, in minutes. 0 is no limit, as the original.
        "flow_max_lookback_minutes": 30,
        # Hold one position at a time. False: the original's stacking, and no exits of V2's own.
        "one_position": True,
        # An opposite entry while a position is held: "reverse" closes it and opens the other side,
        # "close" only closes it, "ignore" keeps it.
        "on_opposite": OPPOSITE_REVERSE,
        # Close when the close crosses back over this EMA: "slow", "fast" or "off".
        "exit_on_trend_break": TREND_SLOW,
        # Close when the OI flow reads at least `flow_min_pct` against the position.
        "exit_on_flow_flip": True,
    }

    def initialize_state(self) -> Dict[str, Any]:
        state = super().initialize_state()
        state.update({
            "position": None,       # {"group_id", "side", "symbol", "since", "session"}
            "dropped": {why: 0 for why in DROP_REASONS},
        })
        return state

    # ------------------------------------------------------------------ loop --
    def on_bar(self, state: Dict[str, Any], inp: StrategyInput) -> List[StrategySignal]:
        evaluated_before = state.get("last_evaluated_bar")
        group_before = state.get("last_group_id")
        found = super().on_bar(state, inp)
        stamp = state.get("last_evaluated_bar")
        # The original evaluates each closed candle once; so does everything here.
        if stamp is None or stamp == evaluated_before:
            return found

        bars = inp.bars.get("5m", {}).get("index", [])
        closed = bars if inp.mode == "OfflineReplay" else bars[:-1]
        bar_start = indicators.parse_utc(stamp)
        if not closed or bar_start is None:
            return found
        reading = self._read(state, inp, closed, bar_start)

        signals: List[StrategySignal] = []
        if self._p("one_position"):
            self._sync(state, inp, reading)
            self._exit(state, inp, reading, signals)
        for sig in found:
            if sig.signal_type != "OPEN_GROUP":
                signals.append(sig)
                continue
            self._enter(state, inp, reading, sig, group_before, signals)
        return signals

    def _read(self, state: Dict[str, Any], inp: StrategyInput, closed: List[Any], bar_start: datetime) -> Dict[str, Any]:
        """
        What this candle's decision saw, read the way the original read it: the clock, the chain it
        fetched, whether that chain passed the data check, the OI flow and how old its earlier sample was.
        """
        decided_at = bar_start + timedelta(minutes=5)
        replay = inp.mode != "LivePaper"
        if replay:
            now_utc, chain_as_of = decided_at, decided_at
        elif inp.recap_date is not None:
            now_utc = indicators.parse_utc(inp.timestamp_utc) or decided_at
            chain_as_of = now_utc
        else:
            now_utc, chain_as_of = datetime.now(timezone.utc), None

        signal_bar = closed[-1]
        close = float(signal_bar.close)
        closes = [float(b.close) for b in closed]
        # The chain the original just read for this decision: the same call, answered from its cache (the
        # live throttle, or the replay's moment), so no second request is made.
        chain = None if (inp.metadata or {}).get("chain") == "none" else self._fetch_chain(inp.underlying, as_of=chain_as_of)
        ok, _, _ = data_check(chain, close, now_utc, float(self._p("max_chain_age_seconds")),
                              float(self._p("max_spot_gap_pct")))
        captured = None
        if chain:
            captured = indicators.parse_utc((chain.get("header") or {}).get("snapshotCapturedUtc") or chain.get("asOfUtc"))
        # A replay (or a recap) asks for the chain as of its own moment; one captured after it is the future.
        ahead = (replay or inp.recap_date is not None) and captured is not None and captured > now_utc

        flow, flow_age = (None, None)
        if ok and not ahead:
            flow, flow_age = self._flow(state, chain, close, decided_at)
        return {
            "stamp": str(getattr(signal_bar, "timestamp_utc", "")),
            "session": indicators.session_date(signal_bar),
            "close": close,
            "ema_fast": indicators.ema(closes, int(self._p("ema_fast"))),
            "ema_slow": indicators.ema(closes, int(self._p("ema_slow"))),
            "chain": chain,
            "chain_ahead": ahead,
            "flow": flow,
            "flow_age_s": flow_age,
        }

    def _flow(self, state: Dict[str, Any], chain: Dict[str, Any], close: float,
              decided_at: datetime) -> Tuple[Optional[Dict[str, float]], Optional[float]]:
        """
        The original's OI flow for this decision, and the age of the sample it was measured from. The
        original has just appended this decision's sample; the earlier one is the newest sample at least
        `flow_lookback_minutes` older, as the original picks it.
        """
        samples = state.get("oi_samples") or []
        if not chain or not samples or samples[-1][0] != decided_at.timestamp():
            return None, None
        lookback_s = 60.0 * float(self._p("flow_lookback_minutes"))
        then_at, then = None, None
        for at, snapshot in samples[:-1]:
            if decided_at.timestamp() - at >= lookback_s:
                then_at, then = at, snapshot
        if then is None:
            return None, None
        atm = float((chain.get("header") or {}).get("atTheMoneyStrike") or chain.get("atTheMoneyStrike") or close)
        strikes = oi_by_strike(near_money(chain, atm, int(self._p("flow_strikes"))))
        now = {k: tuple(v) for k, v in samples[-1][1].items() if k in strikes}
        flow = oi_flow(now, {k: tuple(v) for k, v in then.items()})
        return flow, decided_at.timestamp() - then_at

    # ---------------------------------------------------------------- trade --
    def _sync(self, state: Dict[str, Any], inp: StrategyInput, reading: Dict[str, Any]) -> None:
        """
        Forget a position V2 no longer has. A replay says which groups are open, so a run rule or the
        square-off closing it is seen at once. Live says nothing, so it is dropped when a new session
        starts: a run rule closing it leaves V2 believing it is held for the rest of that day.
        """
        position = state["position"]
        if position is None:
            return
        open_groups = (inp.metadata or {}).get("open_groups")
        if open_groups is not None:
            if position["group_id"] not in open_groups:
                state["position"] = None
            return
        if reading["session"] and position.get("session") and reading["session"] != position["session"]:
            state["position"] = None

    def _exit(self, state: Dict[str, Any], inp: StrategyInput, reading: Dict[str, Any],
              signals: List[StrategySignal]) -> None:
        """
        The position's own invalidation, judged on candles that closed after the entry's: the close back
        over the chosen EMA, or the OI flow reading at least `flow_min_pct` against the position.
        """
        position = state["position"]
        if position is None:
            return
        at, since = indicators.parse_utc(reading["stamp"]), indicators.parse_utc(position.get("since"))
        if at is None or (since is not None and at <= since):
            return
        call = position["side"] == "CE"
        why = None

        trend = str(self._p("exit_on_trend_break")).strip().lower()
        ema = reading["ema_slow"] if trend == TREND_SLOW else reading["ema_fast"] if trend == TREND_FAST else None
        if ema is not None and ((call and reading["close"] < ema) or (not call and reading["close"] > ema)):
            why = (f"trend broke: close {reading['close']:.2f} {'below' if call else 'above'} the "
                   f"{trend} EMA {ema:.2f}")

        flow, min_flow = reading["flow"], float(self._p("flow_min_pct"))
        cap = 60.0 * float(self._p("flow_max_lookback_minutes"))
        fresh = reading["flow_age_s"] is not None and (not cap or reading["flow_age_s"] <= cap)
        if why is None and self._p("exit_on_flow_flip") and flow is not None and fresh and \
                ((call and flow["net_pct"] <= -min_flow) or (not call and flow["net_pct"] >= min_flow)):
            why = (f"OI flow turned against the {position['side']}: {flow['net_pct']:+.2f}% "
                   f"(puts {flow['put_change']:+.0f}, calls {flow['call_change']:+.0f})")

        if why is not None:
            signals.append(self._close(position, inp, why))
            state["position"] = None

    def _enter(self, state: Dict[str, Any], inp: StrategyInput, reading: Dict[str, Any], sig: StrategySignal,
               group_before: Any, signals: List[StrategySignal]) -> None:
        def drop(why: str) -> None:
            undo_entry(state, group_before)
            state["dropped"][why] = state["dropped"].get(why, 0) + 1
            print(f"[{self.name}] {inp.underlying} {sig.metadata.get('option_side')} entry dropped: {why}", flush=True)

        side = str(sig.metadata.get("option_side") or "")
        if reading["chain_ahead"]:
            return drop("chain_ahead")
        cap = 60.0 * float(self._p("flow_max_lookback_minutes"))
        if cap and (reading["flow_age_s"] is None or reading["flow_age_s"] > cap):
            return drop("flow_too_old")
        atm = (inp.contracts or {}).get("atm_ce" if side == "CE" else "atm_pe")
        if self._p("require_same_contract"):
            chain_expiry = str((reading["chain"] or {}).get("expiryDate") or "")[:10]
            same_strike = atm is not None and sig.metadata.get("strike") is not None and \
                float(atm.strike_price) == float(sig.metadata["strike"])
            same_expiry = atm is None or not chain_expiry or not atm.expiry_date or atm.expiry_date[:10] == chain_expiry
            if not (same_strike and same_expiry):
                return drop("contract_mismatch")

        position = state["position"] if self._p("one_position") else None
        if position is not None:
            if position["side"] == side:
                return drop("same_side_held")
            opposite = str(self._p("on_opposite")).strip().lower()
            if opposite == OPPOSITE_IGNORE:
                return drop("opposite_ignored")
            signals.append(self._close(position, inp, f"opposite entry, {side}: {sig.reason}"))
            state["position"] = None
            if opposite == OPPOSITE_CLOSE:
                undo_entry(state, group_before)
                return

        sig.metadata = dict(sig.metadata or {})
        sig.metadata["v2"] = {
            "flow_lookback_s": round(reading["flow_age_s"]) if reading["flow_age_s"] is not None else None,
            "atm_contract": atm.symbol if atm is not None else None,
        }
        signals.append(sig)
        if self._p("one_position"):
            state["position"] = {
                "group_id": sig.metadata.get("group_id"),
                "side": side,
                "symbol": sig.legs[0]["symbol"] if sig.legs else None,
                "since": reading["stamp"],
                "session": reading["session"],
            }

    def _close(self, position: Dict[str, Any], inp: StrategyInput, why: str) -> StrategySignal:
        return StrategySignal(
            strategy_name=self.name,
            signal_type="CLOSE_GROUP",
            timestamp_utc=inp.timestamp_utc,
            reason=f"Close {position['side']}: {why}",
            symbol=position["symbol"],
            legs=[{"symbol": position["symbol"], "side": "SELL", "quantity": self.lots}],
            metadata={"group_id": position["group_id"], "option_side": position["side"]},
        )
