"""
Which way a Fulcrum variant trades, and whether it hedges.

Every Fulcrum was written as a seller: short straddles funded by premium, with far
OTM wings bought to cap the tail. Turning one into a buyer is not a matter of
flipping a sign — it inverts what the position earns and what it costs:

* A seller collects premium and decay works for it. A buyer pays premium and
  decay works against it, so every roll costs money rather than realising it.
* A seller's loss is unbounded, which is what the wings are for. A buyer's loss
  is already capped at the premium paid, so a protective wing buys nothing and
  only adds cost. Buy variants therefore hold no hedges by default.

Kept here rather than duplicated in five files, so the five stay one strategy
with a parameter rather than ten that can drift apart.
"""

from __future__ import annotations

from typing import Any, Dict, Optional, Tuple

SELL = "SELL"
BUY = "BUY"


def opposite(side: str) -> str:
    return BUY if (side or "").upper() == SELL else SELL


def resolve_direction(params: Optional[Dict[str, Any]]) -> Tuple[str, bool]:
    """
    The direction to trade and whether to hold protective wings.

    Hedging follows the direction unless asked otherwise: a short position wants
    wings, a long one does not. `use_hedges` overrides that for anyone who wants
    a hedged long — it is a legitimate structure, just not the default.
    """
    source = params or {}

    raw = str(source.get("direction", SELL) or SELL).strip().upper()
    direction = BUY if raw in (BUY, "LONG", "B") else SELL

    if "use_hedges" in source:
        value = source["use_hedges"]
        if isinstance(value, str):
            hedges = value.strip().lower() in ("true", "1", "yes", "y")
        else:
            hedges = bool(value)
    else:
        hedges = direction == SELL

    return direction, hedges


def direction_note(direction: str, hedges: bool) -> str:
    """One sentence for the catalog, so the two variants are told apart on sight."""
    if direction == BUY:
        base = ("This is the BUY variant: it goes long the straddles instead of short, so it "
                "pays premium and time decay works against it — it needs the underlying to move, "
                "not to sit still.")
        return base if hedges else base + " It holds no wings: a long option's loss is already capped at the premium paid."
    return "This is the SELL variant: it collects premium and profits from decay, hedged by far OTM wings."


def describe_for(base: str, direction: str, hedges: bool) -> str:
    """
    The class's catalogue prose, corrected for how this variant trades.

    Same problem as the leg summary and the same fix: `description` is a class
    attribute, so a class registered as both a seller and a buyer describes
    itself as a seller on both cards. Appending "this is the BUY variant" to a
    paragraph that opens "Sells short straddles…" contradicts itself in the
    space of two sentences.
    """
    text = base or ""

    if (direction or "").upper() == BUY:
        for old, new in (
            ("Sells three short straddles", "Buys three straddles"),
            ("Sells short straddles", "Buys straddles"),
            ("Keeps one to three short straddles", "Keeps one to three long straddles"),
            ("one to three short straddles", "one to three long straddles"),
            ("sells the ATM call and put", "buys the ATM call and put"),
            ("a rolling short straddle", "a rolling long straddle"),
            ("the short legs are sold in double lots", "the long legs are bought in double lots"),
            ("short straddle", "long straddle"),
            # The decay clause is the dangerous one: it sits on the deploy
            # button and tells a buyer that the thing costing it money is what
            # earns it. A seller profits from decay; a buyer pays it.
            ("Profits from premium decay in range-bound sessions; every roll realises the "
             "P&L of the previous straddle.",
             "Pays premium, so decay works against it: it needs the underlying to travel, "
             "and a range-bound session is its losing case."),
            ("Profits from premium decay in range-bound sessions.",
             "Pays premium, so decay works against it — a range-bound session is its losing case."),
            ("Profits from premium decay", "Pays premium and loses to decay"),
            # Double lots invert with the direction too: a seller collects twice
            # the premium and fears the breakout; a buyer PAYS twice and the
            # breakout is the outcome it is paying for.
            ("the position collects more premium in a tight range at the cost of a bigger "
             "loss on a breakout",
             "the position pays more premium in a tight range in exchange for a bigger gain "
             "on a breakout"),
            # ...and the exit, which is no longer the seller's roll.
            ("whenever the ATM strike moves to a different strike it closes the old straddle "
             "and opens a fresh one at the new ATM",
             "it holds the straddle until the spot has travelled the target number of strikes, "
             "or a time stop closes a position that has gone nowhere"),
        ):
            text = text.replace(old, new)

    if not hedges:
        # Removing a clause from the middle of a sentence leaves its punctuation
        # behind: "...on either side —." reads as an unfinished thought.
        for clause in (
            " and buys far OTM call/put wings about 3.5% away as protection",
            " plus far OTM call/put wings",
            ", and buys far OTM call/put wings about 3.5% away as protection",
        ):
            text = text.replace(clause, "")

        text = text.replace(" —.", ".").replace(" ,", ",").replace("  ", " ")

    return text


def legs_summary_for(base: str, direction: str, hedges: bool) -> str:
    """
    The class's own one-line leg summary, corrected for how this variant trades.

    `legs_summary` is a class attribute, so a class registered as both a seller
    and a buyer would show the seller's text on both cards — telling someone
    deploying the buy variant that it sells. That is the worst kind of wrong: it
    is on the button they are about to press.
    """
    text = base or ""

    if (direction or "").upper() == BUY:
        text = text.replace("Sell ", "Buy ").replace("sell ", "buy ")
        # The seller rolls on the ATM changing; the buyer must not, or it takes
        # every winner at one strike step. See _exit_rules.py.
        text = text.replace("rolled on every ATM change",
                            "held until the move arrives or the time stop")

    if not hedges:
        # Removing a clause from the middle of a sentence leaves its punctuation
        # behind: "...on either side —." reads as an unfinished thought.
        for clause in (
            " + Buy far OTM CE and PE wings",
            " + Buy far OTM call and put wings",
        ):
            text = text.replace(clause, "")

    return text
