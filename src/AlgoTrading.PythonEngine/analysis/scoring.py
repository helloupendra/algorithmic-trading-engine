"""
analysis/scoring.py

What happened in a session, and how far each forecast was from it. The same
functions score the walk-forward backtest and the live forecasts, so a number
in the backtest report and a number on the scoreboard mean the same thing.

Losses (lower is better), from the contract:

  range      |ln(actual range) − ln(predicted median)|, for the model and for
             the baseline's median (the 20-session mean range).
  trend,     Brier score (p − y)².
  direction

Metrics beside the loss: for range, whether the 80% interval covered the
actual range and the Brier score of the three buckets (summed over the
buckets, Brier's original multi-category form: 0 is perfect, 2 the worst);
for trend and direction, the Brier scores again under their own names.
"""

from __future__ import annotations

import math
from typing import Any, Dict, List, Mapping, Optional, Sequence

#: A trend day: the body is at least this share of the range.
TREND_EFFICIENCY = 0.6

BUCKETS = ("quiet", "normal", "wild")


def efficiency(open_: float, high: float, low: float, close: float) -> float:
    """|close − open| / (high − low); a session that never moved has no body either."""
    span = high - low
    return abs(close - open_) / span if span > 0 else 0.0


def range_pct(high: float, low: float, prev_close: float) -> float:
    return (high - low) / prev_close * 100.0


def bucket_of(value: float, edges: Sequence[float]) -> str:
    """quiet below the first edge, wild at or above the second, normal between."""
    if value < edges[0]:
        return BUCKETS[0]
    if value < edges[1]:
        return BUCKETS[1]
    return BUCKETS[2]


def outcome(open_: float, high: float, low: float, close: float, prev_close: Optional[float],
            edges: Optional[Sequence[float]] = None) -> Dict[str, Any]:
    """
    The contract's outcome object. `range` needs the previous close (null
    without one); `bucket` needs a range forecast's edges (null for trend and
    direction, which state none).
    """
    rng = range_pct(high, low, prev_close) if prev_close else None
    eff = efficiency(open_, high, low, close)
    return {
        "open": round(open_, 2),
        "high": round(high, 2),
        "low": round(low, 2),
        "close": round(close, 2),
        "range": None if rng is None else round(rng, 4),
        "bucket": bucket_of(rng, edges) if rng is not None and edges else None,
        "trendDay": eff >= TREND_EFFICIENCY,
        "efficiency": round(eff, 4),
        "up": close > open_,
    }


def event_of(target: str, result: Mapping[str, Any]) -> int:
    """1 when the event a trend or direction forecast gave a probability for happened."""
    if target == "trend":
        return int(bool(result["trendDay"]))
    if target == "direction":
        return int(bool(result["up"]))
    raise ValueError(f"no single event for target {target!r}")


def score(target: str, prediction: Mapping[str, Any], baseline: Mapping[str, Any],
          result: Mapping[str, Any]) -> Dict[str, Any]:
    """The contract's scores object for one forecast and its session's outcome."""
    if target == "range":
        actual = result.get("range")
        if actual is None or actual <= 0:
            raise ValueError("a range forecast needs the session's range, and it must be positive")
        hit = [int(result.get("bucket") == name) for name in BUCKETS]
        model_p = [float(prediction["buckets"][name]) for name in BUCKETS]
        base_p = [float(baseline["buckets"][name]) for name in BUCKETS]
        return {
            "loss": round(abs(math.log(actual) - math.log(prediction["median"])), 6),
            "baselineLoss": round(abs(math.log(actual) - math.log(baseline["median"])), 6),
            "metrics": {
                "covered80": bool(prediction["low80"] <= actual <= prediction["high80"]),
                "brier": round(brier(model_p, hit), 6),
                "baselineBrier": round(brier(base_p, hit), 6),
            },
            "calibration": [{"p": p, "y": y} for p, y in zip(model_p, hit)],
        }
    if target in ("trend", "direction"):
        y = event_of(target, result)
        p, pb = float(prediction["p"]), float(baseline["p"])
        loss, base_loss = (p - y) ** 2, (pb - y) ** 2
        return {
            "loss": round(loss, 6),
            "baselineLoss": round(base_loss, 6),
            "metrics": {"brier": round(loss, 6), "baselineBrier": round(base_loss, 6)},
            "calibration": [{"p": p, "y": y}],
        }
    raise ValueError(f"unknown target {target!r}")


def brier(probabilities: Sequence[float], happened: Sequence[int]) -> float:
    return sum((p - y) ** 2 for p, y in zip(probabilities, happened))


def calibration_bins(pairs: Sequence[tuple]) -> List[Dict[str, Any]]:
    """
    (p, y) pairs in tenths of probability — the scoreboard's bins — with the
    count, the mean stated probability and how often the event happened.
    p = 1.0 falls in the last bin.
    """
    bins: Dict[int, List[tuple]] = {}
    for p, y in pairs:
        bins.setdefault(min(int(p * 10), 9), []).append((p, y))
    out = []
    for k in sorted(bins):
        rows = bins[k]
        out.append({"from": round(k / 10, 1), "to": round((k + 1) / 10, 1), "n": len(rows),
                    "meanP": round(sum(p for p, _ in rows) / len(rows), 4),
                    "hitRate": round(sum(y for _, y in rows) / len(rows), 4)})
    return out
