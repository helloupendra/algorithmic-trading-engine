"""
analysis/score.py

The evening's scores: every forecast still without an outcome whose session
has closed gets the session's outcome and its scores, POSTed to the API.

At 15:50 IST today's session exists only in live_bars (the nightly archive
writes candles at 23:50), so today is scored from the live 1-minute bars; any
earlier session from the candles. A session whose bars are not complete yet is
left for the next run — the scheduler also scores once at start-up — and only
becomes an error when it is still missing days later, so a forecast cannot
quietly stay unscored for good.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import date, datetime, time, timedelta
from typing import Callable, Dict, List, Optional, Sequence

from analysis import scoring
from analysis.api import Answer
from analysis.data import MarketData, SessionBar

log = logging.getLogger("analysis.score")

SESSION_CLOSE_IST = time(15, 30)

#: A closed session still without complete bars after this many days is an error, not a wait.
GIVE_UP_DAYS = 4


@dataclass
class ScoreResult:
    scored: int = 0
    refused: int = 0
    waiting: List[str] = field(default_factory=list)
    errors: List[str] = field(default_factory=list)
    payloads: List[dict] = field(default_factory=list)

    @property
    def exit_code(self) -> int:
        return 1 if self.errors else 0


def session_closed(day: date, now_ist: datetime) -> bool:
    today = now_ist.date()
    return day < today or (day == today and now_ist.time() >= SESSION_CLOSE_IST)


def unscored(forecasts: Sequence[dict], now_ist: datetime) -> List[dict]:
    """Forecasts with no outcome whose session has closed."""
    out = []
    for f in forecasts:
        if f.get("outcome") is not None or f.get("scores") is not None:
            continue
        day = date.fromisoformat(str(f["sessionDate"])[:10])
        if session_closed(day, now_ist):
            out.append(f)
    return out


def score_forecasts(forecasts: Sequence[dict], market: MarketData, now_ist: datetime,
                    post: Optional[Callable[[int, dict], Answer]]) -> ScoreResult:
    """Score every forecast in `forecasts` (already filtered to unscored, closed sessions)."""
    result = ScoreResult()
    bars: Dict[str, Dict[date, SessionBar]] = {n: s.by_day() for n, s in market.series.items()}
    ordered: Dict[str, List[SessionBar]] = {n: s.sessions for n, s in market.series.items()}
    for f in forecasts:
        name, target = str(f["underlying"]), str(f["target"])
        day = date.fromisoformat(str(f["sessionDate"])[:10])
        label = f"#{f.get('id')} {f.get('modelKey')} {name} {day}"
        series = market.series.get(name)
        bar = bars.get(name, {}).get(day)
        if bar is None:
            why = (series.dropped_for(day) if series else None) or "no bars stored yet"
            line = f"{label}: no complete session ({why})"
            if (now_ist.date() - day).days > GIVE_UP_DAYS:
                result.errors.append(line + f"; still missing after {GIVE_UP_DAYS} days")
            else:
                result.waiting.append(line)
            continue

        prediction, baseline = f.get("prediction") or {}, f.get("baseline") or {}
        if target == "range":
            # The scale the forecast committed to: its own stated previous close.
            prev_close = float(prediction["prevClose"])
            edges = prediction.get("bucketEdges")
        else:
            idx = ordered[name].index(bar)
            prev_close = ordered[name][idx - 1].close if idx > 0 else None
            edges = None
        try:
            result_outcome = scoring.outcome(bar.open, bar.high, bar.low, bar.close, prev_close, edges)
            scores = scoring.score(target, prediction, baseline, result_outcome)
        except (KeyError, TypeError, ValueError) as ex:
            result.errors.append(f"{label}: cannot score ({type(ex).__name__}: {ex})")
            continue
        payload = {"outcome": result_outcome, "scores": scores}
        result.payloads.append({"id": f.get("id"), **payload})
        if post is None:
            result.scored += 1
            continue
        answer = post(int(f["id"]), payload)
        if answer.ok:
            result.scored += 1
            log.info("scored %s from %s: loss %.4f, baseline %.4f", label, bar.source, scores["loss"],
                     scores["baselineLoss"])
        elif answer.conflict:
            result.refused += 1
            log.info("refused %s (409): %s", label, answer.message)
        else:
            result.errors.append(f"{label}: HTTP {answer.status} {answer.message}")
    return result


def lookback(now_ist: datetime, days: int) -> date:
    return now_ist.date() - timedelta(days=days)
