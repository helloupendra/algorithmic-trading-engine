"""
research/candidates/

Seed research candidates. These are research ideas evaluated by
research/harness.py - not live strategies, not in the strategy registry, and
not specced under docs/strategies. Each module exposes `CANDIDATE`.
"""

from __future__ import annotations

from typing import Dict, List

from research.candidate import Candidate
from research.candidates import baseline_ema_trend, orb_breakout_buy, regime_momentum_buy, vwap_pullback_buy

BASELINE_NAME = "baseline_ema_trend"

CANDIDATES: Dict[str, Candidate] = {
    module.CANDIDATE.name: module.CANDIDATE
    for module in (baseline_ema_trend, orb_breakout_buy, vwap_pullback_buy, regime_momentum_buy)
}


def get(name: str) -> Candidate:
    if name not in CANDIDATES:
        raise KeyError(f"unknown candidate {name!r}; known: {', '.join(CANDIDATES)}")
    return CANDIDATES[name]


def names() -> List[str]:
    return list(CANDIDATES)
