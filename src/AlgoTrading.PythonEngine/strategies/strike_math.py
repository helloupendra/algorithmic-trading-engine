"""
Strike arithmetic that scales across underlyings.

Every Fulcrum variant used to carry its own `roundup_100` / `rounddown_100` and a
scattering of literal 100s, 500s and 2000s. Those numbers are BANKNIFTY's, and
only BANKNIFTY's: its strikes sit 100 apart on a ~57,000 spot, so a 2,000-point
wing is 3.5% out of the money. Run the same code on NIFTY at ~24,000 and that
wing is 8.3% out — a different instrument doing a different job under the same
name. On a ₹150 stock it is nonsense.

Two units, because there are two questions:

* **How far out is a hedge?** A share of the spot price. Moneyness is what makes
  a wing worth buying, and moneyness scales with price — for a 57,000 index and
  a ₹150 stock alike.
* **How far must the spot travel before we adjust?** A multiple of the strike
  step. Adjustment is really the question "has the ATM moved", and the strike
  grid is the only thing that answers it. Index steps are ~0.2% of spot, stock
  steps nearer 3%; a share-of-spot rule would make the same strategy adjust at
  wildly different frequencies on the two.

On BANKNIFTY (step 100) the multiples reproduce today's literals exactly: a
20-point threshold is 0.2 steps, 175 is 1.75, a 100-point neighbour is 1 step.
"""

from __future__ import annotations

import math
from typing import Union

Strike = Union[int, float]

#: Fallback when nothing supplies a step. Deliberately not 100: an unnoticed
#: default should misbehave on BANKNIFTY too, rather than quietly look correct
#: on the one underlying that was hardcoded and wrong everywhere else.
DEFAULT_STEP = 50.0

#: How far out a protective wing sits, as a share of spot. 3.5% is BANKNIFTY's
#: current 2,000 points on a ~57,000 spot, so today's behaviour is preserved.
DEFAULT_HEDGE_PCT = 0.035

#: Wings snap to a coarser grid than the straddle strikes, because far-OTM
#: strikes are only listed at wider intervals. 5 steps is BANKNIFTY's 500.
DEFAULT_HEDGE_GRID_STEPS = 5

#: ...but only while that coarse grid stays small next to the spot. BANKNIFTY's
#: 500 is 0.9% of a 57,000 index, so snapping to it barely moves the wing. The
#: same 5-step rule on a ₹150 stock gives a 12.5 grid — 8% of the price — and the
#: snap alone would throw the hedge nearly twice as far out as asked for. Past
#: this fraction the wing snaps to the ordinary strike grid instead.
MAX_HEDGE_GRID_FRACTION = 0.01


def _clean_step(step: Union[Strike, None]) -> float:
    try:
        value = float(step)
    except (TypeError, ValueError):
        return DEFAULT_STEP
    return value if value > 0 else DEFAULT_STEP


def _as_strike(value: float, step: float) -> Strike:
    """
    Whole numbers stay whole; fractional grids keep their fraction.

    Stock strikes are genuinely fractional (a 2.5 grid gives 102.5), and forcing
    every strike through int() would silently trade a contract that does not
    exist.
    """
    rounded = round(value, 4)
    return int(rounded) if float(rounded).is_integer() else rounded


def round_to_step(price: float, step: Union[Strike, None]) -> Strike:
    """Nearest strike on the grid."""
    s = _clean_step(step)
    return _as_strike(round(price / s) * s, s)


def round_up_to_step(price: float, step: Union[Strike, None]) -> Strike:
    """The first strike at or above the price."""
    s = _clean_step(step)
    return _as_strike(math.ceil(price / s) * s, s)


def round_down_to_step(price: float, step: Union[Strike, None]) -> Strike:
    """The last strike at or below the price."""
    s = _clean_step(step)
    return _as_strike(math.floor(price / s) * s, s)


def steps_to_points(step_multiple: float, step: Union[Strike, None]) -> float:
    """
    A threshold expressed in strike steps, as points of the underlying.

    `steps_to_points(0.2, 100)` is BANKNIFTY's old 20-point threshold; the same
    0.2 on NIFTY's 50-point grid is 10 points, which is the same *decision* about
    how close the spot has drifted to the next strike.
    """
    return float(step_multiple) * _clean_step(step)


def neighbour_strike(strike: Strike, offset_steps: int, step: Union[Strike, None]) -> Strike:
    """The strike `offset_steps` grid positions away (negative for below)."""
    s = _clean_step(step)
    return _as_strike(float(strike) + offset_steps * s, s)


def hedge_strike(
    spot: float,
    side: str,
    step: Union[Strike, None],
    hedge_pct: float = DEFAULT_HEDGE_PCT,
    grid_steps: int = DEFAULT_HEDGE_GRID_STEPS,
) -> Strike:
    """
    Where to buy a protective wing, as a share of spot snapped to a wide grid.

    `side` is "CE" for the upside wing (rounded away and up) or "PE" for the
    downside (rounded away and down) — always rounded further out of the money,
    so a rounding error makes the hedge cheaper rather than accidentally near.
    """
    s = _clean_step(step)
    spot_value = abs(float(spot))

    grid = s * max(1, int(grid_steps))
    if spot_value > 0 and grid / spot_value > MAX_HEDGE_GRID_FRACTION:
        grid = s

    distance = spot_value * float(hedge_pct)

    if (side or "").upper() == "CE":
        return _as_strike(math.ceil((spot + distance) / grid) * grid, grid)
    return _as_strike(math.floor((spot - distance) / grid) * grid, grid)


def resolve_step(
    input_step: Union[Strike, None],
    override: Union[Strike, None] = None,
    default: float = DEFAULT_STEP,
) -> float:
    """
    The strike grid a strategy should work on.

    An explicit run parameter wins, because someone typed it on purpose. Failing
    that the platform's value, which comes from the live option chain and is the
    right answer almost always. The constant is last, and only reached when a
    strategy is evaluated with no market context at all.
    """
    for candidate in (override, input_step):
        try:
            value = float(candidate)
        except (TypeError, ValueError):
            continue
        if value > 0:
            return value
    return default


#: Every threshold in this codebase was written as points on BANKNIFTY's grid.
#: A stored run parameter from before thresholds became step multiples has to be
#: read back with that assumption, or a saved "20" would silently become 20
#: strikes instead of 20 points.
LEGACY_GRID = 100.0


def steps_from_params(
    params: dict,
    steps_key: str,
    legacy_points_key: str,
    default_steps: float,
) -> float:
    """
    A threshold in strike steps, accepting the older points spelling.

    The new key wins outright. Otherwise a legacy points value is divided by the
    grid it was written against — "20 points" becomes 0.2 steps, which is 20
    points again on BANKNIFTY and 10 on NIFTY, i.e. the same decision about how
    far the spot has drifted toward the next strike.
    """
    source = params or {}

    if steps_key in source:
        try:
            value = float(source[steps_key])
            if value > 0:
                return value
        except (TypeError, ValueError):
            pass

    if legacy_points_key in source:
        try:
            points = float(source[legacy_points_key])
            if points > 0:
                return points / LEGACY_GRID
        except (TypeError, ValueError):
            pass

    return float(default_steps)
