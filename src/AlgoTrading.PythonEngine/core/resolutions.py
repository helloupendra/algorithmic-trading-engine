"""
core/resolutions.py

The one place where bar resolution strings are translated.

Two spellings exist in the platform:

  - canonical / candle-table form (`candles.Resolution`, `/api/MarketData/history/local`):
    "1", "5", "15", "D"
  - strategy-facing form (`DataRequirement.resolution`, keys of `StrategyInput.bars`):
    "1m", "5m", "15m", "1D"

Never compare resolution strings without normalising through these helpers.
"""

from __future__ import annotations

from typing import Optional

# Minutes in one bar for the intraday resolutions; the day resolution is the
# special value below because a session is not a fixed number of minutes.
DAY_RESOLUTION_MINUTES = 1440

_DAY_ALIASES = {"D", "1D", "DAY", "DAILY", "1DAY"}


def _clean(value: Optional[str]) -> str:
    return str(value or "").strip()


def to_candle_resolution(value: Optional[str]) -> str:
    """
    Strategy-facing (or already canonical) resolution -> canonical code.

    "5m" -> "5", "15m" -> "15", "1D"/"D" -> "D", "5" -> "5", "1m" -> "1".
    Unknown spellings are returned upper-cased so the caller sees them fail loudly.
    """
    text = _clean(value)
    if not text:
        raise ValueError("resolution is required")
    upper = text.upper()
    if upper in _DAY_ALIASES:
        return "D"
    if upper.endswith("M") and upper[:-1].isdigit():
        return str(int(upper[:-1]))
    if upper.isdigit():
        return str(int(upper))
    return upper


def to_strategy_resolution(value: Optional[str]) -> str:
    """
    Canonical (or already strategy-facing) resolution -> strategy-facing form.

    "5" -> "5m", "15" -> "15m", "D"/"1D" -> "1D", "5m" -> "5m".
    """
    code = to_candle_resolution(value)
    if code == "D":
        return "1D"
    return f"{code}m"


def minutes_of(value: Optional[str]) -> int:
    """Bar length in minutes; the day resolution reports DAY_RESOLUTION_MINUTES."""
    code = to_candle_resolution(value)
    if code == "D":
        return DAY_RESOLUTION_MINUTES
    return int(code)


def is_daily(value: Optional[str]) -> bool:
    return to_candle_resolution(value) == "D"


def same_resolution(a: Optional[str], b: Optional[str]) -> bool:
    """True when both strings name the same bar length, whatever the spelling."""
    try:
        return to_candle_resolution(a) == to_candle_resolution(b)
    except ValueError:
        return False


def resolution_label(value: Optional[str]) -> str:
    """Display label: "5" -> "5m", "D" -> "1D"."""
    return to_strategy_resolution(value)
