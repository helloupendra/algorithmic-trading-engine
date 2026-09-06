"""
When a LONG Fulcrum variant lets go of a group.

Every Fulcrum was written as a seller, and a seller's exit is the ATM change:
premium was sold at the money, the spot drifted, the position is no longer at
the money, so close it and re-sell at the new strike. While the ATM sits still
the seller is being paid.

Applied unchanged to a buyer, that rule is precisely inverted, and it is the
most expensive mistake this family can make:

* A long straddle earns when the underlying MOVES. The ATM changing is the
  first evidence the move is happening — so closing there cuts the trade at the
  moment it starts working. Every winner is taken at roughly one strike step.
* When the ATM does NOT change, the buyer is paying theta for nothing. That is
  the case the old rule holds through.

Cut the winners, hold the losers. A long straddle already needs a small number
of large wins to pay for many small losses, so removing the large wins does not
make it a worse strategy — it makes it one that cannot be profitable at all.

What a buyer exits on instead:

* The move arrived — the spot is `target_steps` strikes away from where the
  group was opened. Deliberately more than one step, so the trade is given room
  to become the thing it was opened to catch.
* The move did not arrive — `max_hold_minutes` have passed. Theta is a
  certainty and the move is not, so a long straddle that has gone nowhere is
  losing on schedule.

Both are measured from the underlying and the clock, which is everything the
strategy is actually given. Premium-based stops and targets are NOT re-derived
here: the platform already enforces them from real fills and marks, at leg and
group level (`parametersJson.risk`), and a second implementation working off
bar closes would only disagree with the ledger.
"""

from __future__ import annotations

from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional, Sequence

from strategies.base_strategy import StrategyInput, StrategySignal
from strategies.fulcrum._direction import BUY

#: Strikes the spot must travel from the opening strike before a long group is
#: closed as a winner. One step is the seller's threshold and far too tight for
#: a buyer; two gives the move room to develop.
DEFAULT_TARGET_STEPS = 2.0

#: Minutes a long group is held while the spot goes nowhere. Past this the
#: position is paying decay for a move that is not coming.
DEFAULT_MAX_HOLD_MINUTES = 45.0


def _parse_utc(value: Any) -> Optional[datetime]:
    """Lenient ISO-8601 -> aware UTC. None when it cannot be read."""
    if isinstance(value, datetime):
        dt = value
    else:
        text = str(value or "").strip()
        if not text:
            return None
        if text.endswith(("Z", "z")):
            text = text[:-1] + "+00:00"
        try:
            dt = datetime.fromisoformat(text)
        except ValueError:
            return None
    return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt.astimezone(timezone.utc)


def resolve_long_exit(params: Optional[Dict[str, Any]]) -> Dict[str, float]:
    """The buy-side exit thresholds for a run, defaults where unset."""
    source = params or {}

    def number(key: str, fallback: float) -> float:
        raw = source.get(key)
        if raw is None or raw == "":
            return fallback
        try:
            value = float(raw)
        except (TypeError, ValueError):
            return fallback
        return value if value > 0 else fallback

    return {
        "target_steps": number("target_steps", DEFAULT_TARGET_STEPS),
        "max_hold_minutes": number("max_hold_minutes", DEFAULT_MAX_HOLD_MINUTES),
    }


def long_exit_reason(
    entry_strike: Optional[float],
    entry_timestamp_utc: Any,
    spot_price: Optional[float],
    now_timestamp_utc: Any,
    step: float,
    target_steps: float = DEFAULT_TARGET_STEPS,
    max_hold_minutes: float = DEFAULT_MAX_HOLD_MINUTES,
) -> Optional[str]:
    """
    Why a long group should close now, or None to keep holding.

    Returns a sentence rather than a flag: it goes onto the signal as its
    reason, and months later the run history has to say why a trade was closed,
    not merely that it was.
    """
    if entry_strike is None or spot_price is None or not step or step <= 0:
        return None

    displacement = abs(float(spot_price) - float(entry_strike))
    needed = float(target_steps) * float(step)

    if needed > 0 and displacement >= needed:
        return (
            f"Target: the spot moved {displacement:.2f} from the opening strike "
            f"{entry_strike:g}, past the {target_steps:g}-strike threshold ({needed:.2f})."
        )

    opened = _parse_utc(entry_timestamp_utc)
    now = _parse_utc(now_timestamp_utc)
    if opened is not None and now is not None and max_hold_minutes > 0:
        if now - opened >= timedelta(minutes=float(max_hold_minutes)):
            held = (now - opened).total_seconds() / 60.0
            return (
                f"Time stop: held {held:.0f} minutes with the spot only {displacement:.2f} "
                f"from the opening strike {entry_strike:g}; decay is the only thing working."
            )

    return None


def long_time_stop_reason(
    entry_timestamp_utc: Any,
    now_timestamp_utc: Any,
    max_hold_minutes: float = DEFAULT_MAX_HOLD_MINUTES,
) -> Optional[str]:
    """
    Why a long group held through a quiet market should close, or None.

    The adjusting variants already let go of a group when the spot travels past
    their adjustment threshold — for a buyer that is the winner being realised,
    and it needs no correction. What they have no answer for is the market that
    does not move: their threshold gate returns early, bar after bar, while a
    long straddle pays decay for a move that never comes. This is the exit for
    that case, and the only one those variants were missing.
    """
    opened = _parse_utc(entry_timestamp_utc)
    now = _parse_utc(now_timestamp_utc)

    if opened is None or now is None or max_hold_minutes <= 0:
        return None

    if now - opened < timedelta(minutes=float(max_hold_minutes)):
        return None

    held = (now - opened).total_seconds() / 60.0
    return (
        f"Time stop: held {held:.0f} minutes without the move this position was "
        f"opened for; decay is the only thing working."
    )


def closing_legs(legs: Any) -> list:
    """
    The legs that flatten an open group: same contracts, opposite sides.

    One implementation rather than the five near-copies this reversal had
    grown into — a group that closes with one leg on the wrong side does not
    fail loudly, it quietly doubles a position.
    """
    out = []
    for leg in legs or []:
        side = str(leg.get("side", "")).upper()
        out.append({
            "symbol": leg.get("symbol"),
            "side": "BUY" if side == "SELL" else "SELL",
            "quantity": leg.get("quantity"),
            "price": None,
        })
    return out


def long_time_stop_close(
    state: Dict[str, Any],
    inp: StrategyInput,
    direction: str,
    max_hold_minutes: float,
    strategy_name: str,
    reset_keys: Sequence[str] = (),
) -> List[StrategySignal]:
    """
    Closes an open LONG group that has run out of time, and forgets it.

    Returns the signals to emit — empty when this variant sells, when nothing
    is open, or when the position still has time to work. The state reset is
    part of the same step on purpose: a group closed in the market but still
    recorded in state is a phantom position, and the next bar would adjust legs
    that are no longer held.
    """
    if (direction or "").upper() != BUY:
        return []

    group_id = state.get("current_group_id")
    legs = state.get("current_group_legs")
    if not group_id or not legs:
        return []

    reason = long_time_stop_reason(
        state.get("group_entry_utc"), inp.timestamp_utc, max_hold_minutes)
    if not reason:
        return []

    signal = StrategySignal(
        strategy_name=strategy_name,
        signal_type="CLOSE_GROUP",
        timestamp_utc=inp.timestamp_utc,
        reason=reason,
        price=inp.spot_price,
        legs=closing_legs(legs),
        metadata={
            "group_id": group_id,
            "underlying": inp.underlying,
            "exit": "time_stop",
        },
    )

    state["current_group_id"] = None
    state["current_group_legs"] = []
    state["group_entry_utc"] = None
    for key in reset_keys:
        current = state.get(key)
        state[key] = [] if isinstance(current, list) else 0

    return [signal]
