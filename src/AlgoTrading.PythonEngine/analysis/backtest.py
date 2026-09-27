"""
analysis/backtest.py

The walk-forward history each model is registered with.

Expanding window, refitted monthly: every session of a month is forecast by a
fit on all sessions before the month's first day, with that session's inputs
built from the sessions before it — so no forecast saw its own session or any
later one. The fit and the forecast are `models.fit` and `models.predict`, the
functions `issue` uses every morning, and each forecast is scored by
`scoring.score`, the function `score` uses every evening.

Periods (fixed before any result was looked at, from the contract):

    design       the first forecastable session to 2024-12-31
    validation   2025
    holdout      2026-01-01 to the latest session — reported, used to choose nothing

Every model is scored on the same sessions: those where all the models in the
run could forecast. India VIX starts in August 2021, a year after NIFTY's
5-minute history, and range.har alone could forecast that first year; letting
it would compare the two range models on different days.

Numbers pool the three indices. They are not independent — NIFTY, BANKNIFTY
and SENSEX have their big days together — so the confidence interval
resamples session dates, keeping a day's three forecasts together, rather than
forecasts.

Version 2 (`python -m analysis backtest-v2`) runs v1 and v2 together, so both
are scored on the sessions all seven models could forecast, and compares each
v2 model with its v1 model session by session (`compare`). A v2 model is
registered only when the 95% interval of v1's loss minus v2's on validation
lies above zero (`beats_v1`). The holdout was looked at once, by v1's run; v2's
holdout number is a second look, reported and never used to choose.
"""

from __future__ import annotations

import logging
import math
from collections import defaultdict
from dataclasses import dataclass
from datetime import date, datetime
from typing import Any, Dict, Iterable, List, Mapping, Optional, Sequence, Tuple

import numpy as np

from analysis import scoring
from analysis import context as ctxmod
from analysis.data import MarketData, SessionSeries
from analysis.models import (MIN_TRAIN_SESSIONS, MODEL_VERSION, MODEL_VERSION_V2, SPECS, ForecastUnavailable,
                             ModelSpec, Table, build_table, features, fit, predict)

log = logging.getLogger("analysis.backtest")

DESIGN_END = date(2024, 12, 31)
VALIDATION_START, VALIDATION_END = date(2025, 1, 1), date(2025, 12, 31)
HOLDOUT_START = date(2026, 1, 1)
SPLITS = ("design", "validation", "holdout")

#: How many configurations were evaluated for each target before these were
#: fixed, counting sibling models on the same target (range.har and
#: range.har-vix are two tries at one question). Nothing was tuned: the
#: inputs, windows and ridge were written down before the first run. Raise the
#: count with every change made after looking at a result — a count that
#: stays at 1 through five revisions is the multiple-comparisons problem
#: hidden in a constant.
CONFIGURATIONS_TRIED = {"range": 2, "trend": 1, "direction": 1}
#: Version 2, counted the same way and cumulatively: v1's configurations plus
#: one v2 model per target, its inputs written down before the tables it reads
#: existed. No per-group variants were run; each would have been one more.
CONFIGURATIONS_TRIED_V2 = {"range": 3, "trend": 2, "direction": 2}

#: When v2's results could first be looked at, v1's run had already looked at
#: the holdout once (2026-09-27).
HOLDOUT_SECOND_LOOK = ("The 2026 holdout was first looked at by version 1's run on 2026-09-27; this is a second "
                       "look, reported and not used to choose anything.")

#: The confidence interval: 2,000 resamples with a fixed seed, as the scoreboard.
BOOTSTRAP_RESAMPLES = 2000
BOOTSTRAP_SEED = 20260927


def split_of(day: date) -> str:
    if day <= DESIGN_END:
        return "design"
    if day <= VALIDATION_END:
        return "validation"
    return "holdout"


@dataclass(frozen=True)
class Record:
    """One backtest forecast, scored."""

    model: str
    underlying: str
    day: date
    prediction: Dict[str, Any]
    baseline: Dict[str, Any]
    inputs: Dict[str, Any]
    outcome: Dict[str, Any]
    scores: Dict[str, Any]

    @property
    def split(self) -> str:
        return split_of(self.day)


def walk_forward(spec: ModelSpec, t: Table) -> List[Record]:
    """Every session of `t` the model can forecast, each from a fit on the months before it."""
    X = features(spec, t)
    records: List[Record] = []
    fitted: Dict[Tuple[int, int], Any] = {}
    for i, day in enumerate(t.days):
        # A session with no range has nothing to score; the data layer drops them, this keeps it that way.
        if i == 0 or not np.isfinite(X[i]).all() or not t.range_pct[i] > 0:
            continue
        month = (day.year, day.month)
        if month not in fitted:
            try:
                fitted[month] = fit(spec, t, date(day.year, day.month, 1))
            except ForecastUnavailable:
                fitted[month] = None
        model = fitted[month]
        if model is None:
            continue
        try:
            fc = predict(model, t, i)
        except ForecastUnavailable:
            continue
        edges = fc.prediction.get("bucketEdges")
        result = scoring.outcome(float(t.open[i]), float(t.high[i]), float(t.low[i]), float(t.close[i]),
                                 float(t.prev_close[i]), edges)
        records.append(Record(spec.key, t.underlying, day, fc.prediction, fc.baseline, fc.inputs, result,
                              scoring.score(spec.target, fc.prediction, fc.baseline, result)))
    return records


def run(market: MarketData, model_keys: Sequence[str], underlyings: Sequence[str],
        context: Optional[ctxmod.Context] = None) -> Dict[str, List[Record]]:
    """
    Walk every model forward on every underlying; keep the sessions all models
    forecast. `context` is v2's inputs; without it a v2 model forecasts nothing.
    """
    vix = market.vix.closes()
    by_model: Dict[str, List[Record]] = {k: [] for k in model_keys}
    for name in underlyings:
        t = build_table(name, market.series[name].sessions, vix, market.is_expiry(name), context=context)
        for key in model_keys:
            by_model[key].extend(walk_forward(SPECS[key], t))
    common = None
    for key in model_keys:
        days = {(r.underlying, r.day) for r in by_model[key]}
        common = days if common is None else common & days
    common = common or set()
    return {k: sorted((r for r in rs if (r.underlying, r.day) in common), key=lambda r: (r.day, r.underlying))
            for k, rs in by_model.items()}


# ------------------------------------------------------------------ summaries --

def summarize(spec: ModelSpec, records: Sequence[Record]) -> Dict[str, Any]:
    """The contract's backtest object for one model, with the extras the report reads."""
    out: Dict[str, Any] = {}
    for split in SPLITS:
        out[split] = split_stats(spec, [r for r in records if r.split == split])
    by_underlying: Dict[str, Any] = {}
    for name in sorted({r.underlying for r in records}):
        rows = [r for r in records if r.underlying == name]
        loss, base = _mean(r.scores["loss"] for r in rows), _mean(r.scores["baselineLoss"] for r in rows)
        by_underlying[name] = {"n": len(rows), "loss": _r(loss), "baselineLoss": _r(base), "skill": _skill(loss, base)}
    out["byUnderlying"] = by_underlying
    tried = CONFIGURATIONS_TRIED_V2 if spec.version == MODEL_VERSION_V2 else CONFIGURATIONS_TRIED
    out["configurationsTried"] = tried[spec.target]
    out["notes"] = notes(spec)
    return out


def split_stats(spec: ModelSpec, rows: Sequence[Record]) -> Optional[Dict[str, Any]]:
    """One period's numbers; None (JSON null) for a period with no forecasts, rather than a row of nulls."""
    if not rows:
        return None
    loss = _mean(r.scores["loss"] for r in rows)
    base = _mean(r.scores["baselineLoss"] for r in rows)
    lo, hi = bootstrap_by_day([(r.day, r.scores["baselineLoss"] - r.scores["loss"]) for r in rows])
    stats: Dict[str, Any] = {
        "from": min(r.day for r in rows).isoformat(),
        "to": max(r.day for r in rows).isoformat(),
        "n": len(rows),
        "loss": _r(loss),
        "baselineLoss": _r(base),
        "skill": _skill(loss, base),
        "diffCiLow": _r(lo),
        "diffCiHigh": _r(hi),
    }
    if spec.target == "range":
        stats["coverage80"] = _r(_mean(float(r.scores["metrics"]["covered80"]) for r in rows))
        stats["baselineCoverage80"] = _r(_mean(
            float(r.baseline["low80"] <= r.outcome["range"] <= r.baseline["high80"]) for r in rows))
        stats["bucketBrier"] = _r(_mean(r.scores["metrics"]["brier"] for r in rows))
        stats["baselineBucketBrier"] = _r(_mean(r.scores["metrics"]["baselineBrier"] for r in rows))
        stats["baselineCalibration"] = scoring.calibration_bins(
            [(r.baseline["buckets"][b], int(r.outcome["bucket"] == b)) for r in rows for b in scoring.BUCKETS])
    else:
        stats["baselineCalibration"] = scoring.calibration_bins(
            [(r.baseline["p"], r.scores["calibration"][0]["y"]) for r in rows])
    stats["calibration"] = scoring.calibration_bins(
        [(c["p"], c["y"]) for r in rows for c in r.scores["calibration"]])
    return stats


def bootstrap_by_day(diffs: Sequence[Tuple[date, float]], resamples: int = BOOTSTRAP_RESAMPLES,
                     seed: int = BOOTSTRAP_SEED) -> Tuple[float, float]:
    """
    95% interval of the mean of `diffs` (baseline loss − loss), resampling
    whole session dates: a day's forecasts on the three indices go in or out
    together.
    """
    sums: Dict[date, float] = defaultdict(float)
    counts: Dict[date, int] = defaultdict(int)
    for day, d in diffs:
        sums[day] += d
        counts[day] += 1
    days = sorted(sums)
    s = np.array([sums[d] for d in days])
    c = np.array([counts[d] for d in days], dtype=float)
    rng = np.random.default_rng(seed)
    means = np.empty(resamples)
    for start in range(0, resamples, 200):     # in blocks, so a long history never builds a huge index matrix
        stop = min(start + 200, resamples)
        pick = rng.integers(0, len(days), size=(stop - start, len(days)))
        means[start:stop] = s[pick].sum(axis=1) / c[pick].sum(axis=1)
    lo, hi = np.quantile(means, [0.025, 0.975])
    return float(lo), float(hi)


def geometric_baseline_check(records: Sequence[Record], market: MarketData) -> Optional[Dict[str, Any]]:
    """
    The contract's range baseline is the 20-session MEAN range, scored against
    a forecast MEDIAN. Ranges are skewed, so a mean sits above the median and
    loses a little to anything fitted in logs for that alone. This measures how
    much of a range model's skill a 20-session geometric mean — the same
    baseline in logs — would take back. Reported, not registered.
    """
    if not records:
        return None
    tables = {}
    rows = []
    for r in records:
        if r.underlying not in tables:
            s = market.series[r.underlying].sessions
            tables[r.underlying] = build_table(r.underlying, s, {}, lambda _d: False)
        t = tables[r.underlying]
        i = t.index_of(r.day)
        past = t.range_pct[max(0, i - 20):i]
        if len(past) < 20 or not np.isfinite(past).all():
            continue
        geo = float(np.exp(np.mean(np.log(past))))
        rows.append((r, abs(math.log(r.outcome["range"]) - math.log(geo))))
    if not rows:
        return None
    out = {}
    for split in SPLITS:
        sel = [(r, g) for r, g in rows if r.split == split]
        if not sel:
            continue
        loss = _mean(r.scores["loss"] for r, _ in sel)
        out[split] = {"n": len(sel), "loss": _r(loss), "geometricLoss": _r(_mean(g for _, g in sel)),
                      "skillVsGeometric": _skill(loss, _mean(g for _, g in sel))}
    return out


def compare(v1: Sequence[Record], v2: Sequence[Record]) -> Dict[str, Optional[Dict[str, Any]]]:
    """
    v2 against v1, paired session by session: per period, both mean losses
    and the 95% interval of mean(v1 loss − v2 loss), resampling session dates
    as everywhere else. Positive means v2 was better. Only sessions both
    forecast count; `run` already made those the same.
    """
    base = {(r.underlying, r.day): r for r in v1}
    pairs = [(base[(r.underlying, r.day)], r) for r in v2 if (r.underlying, r.day) in base]
    out: Dict[str, Optional[Dict[str, Any]]] = {}
    for split in SPLITS:
        rows = [(a, b) for a, b in pairs if a.split == split]
        if not rows:
            out[split] = None
            continue
        v1_loss, v2_loss = _mean(a.scores["loss"] for a, _ in rows), _mean(b.scores["loss"] for _, b in rows)
        lo, hi = bootstrap_by_day([(a.day, a.scores["loss"] - b.scores["loss"]) for a, b in rows])
        out[split] = {"n": len(rows), "v1Loss": _r(v1_loss), "v2Loss": _r(v2_loss),
                      "skillVsV1": _skill(v2_loss, v1_loss), "diffCiLow": _r(lo), "diffCiHigh": _r(hi)}
    return out


def beats_v1(comparison: Mapping[str, Any]) -> bool:
    """The registration rule: on validation, the whole 95% interval of v1 loss − v2 loss is above zero."""
    v = comparison.get("validation")
    return bool(v) and v.get("diffCiLow") is not None and v["diffCiLow"] > 0


def notes(spec: ModelSpec) -> str:
    if spec.version == MODEL_VERSION_V2:
        return (
            f"Version 2: {spec.compares_with}'s inputs plus the pre-open context (analysis/context.py), each "
            "strictly from before the session. Walk-forward as v1, expanding window refitted monthly, scored on the "
            "sessions all seven v1 and v2 models could forecast. Registered only because its validation loss beat "
            f"{spec.compares_with}'s with a 95% interval above zero (versusV1). " + HOLDOUT_SECOND_LOOK
            + " History is not what makes a model Proven: only live forecasts are."
        )
    text = (
        "Walk-forward, expanding window refitted monthly; each session forecast from sessions before it. "
        f"At least {MIN_TRAIN_SESSIONS} training sessions. Scored only on sessions every v1 model could "
        "forecast (India VIX from Aug 2021). Loss and n pool NIFTY, BANKNIFTY and SENSEX; diffCi is a 95% "
        "bootstrap of mean(baselineLoss - loss) resampling session dates. The holdout is reported and used to "
        "choose nothing. History is not what makes a model Proven: only live forecasts are."
    )
    if spec.target == "range":
        text += (" The baseline is the 20-session mean range scored against a median; part of any skill is the "
                 "mean-above-median gap of a skewed range (see the report's geometric-mean check).")
    if spec.target == "direction":
        text += " A control: skill here would more likely mean a leak than an edge."
    return text


def registration(spec: ModelSpec, backtest: Mapping[str, Any]) -> Dict[str, Any]:
    """POST /api/Forecasts/models body."""
    return {"key": spec.key, "version": spec.version, "target": spec.target, "description": spec.description,
            "backtest": dict(backtest)}


def registration_v2(spec: ModelSpec, summary: Mapping[str, Any], comparison: Mapping[str, Any]) -> Dict[str, Any]:
    """A v2 model's body: its own backtest against the baseline, and `versusV1`, the comparison it was chosen on."""
    body = dict(summary)
    body["versusV1"] = {"model": spec.compares_with, "version": MODEL_VERSION,
                        **{split: comparison.get(split) for split in SPLITS},
                        "chosenOn": "validation", "holdout": HOLDOUT_SECOND_LOOK}
    return registration(spec, body)


# --------------------------------------------------------------------- report --

def report(summaries: Mapping[str, Mapping[str, Any]], market: MarketData, generated: datetime,
           geometric: Mapping[str, Any]) -> str:
    """The human-readable report written to logs/analysis/backtest-<date>.md."""
    lines = [f"# Analysis backtest — model version {MODEL_VERSION}", "",
             f"Generated {generated:%Y-%m-%d %H:%M} IST. Walk-forward, expanding window, refitted monthly. "
             "Loss is lower-is-better; skill = 1 − loss / baseline loss; the CI is a 95% bootstrap of "
             "(baseline loss − loss) by session date. Nothing below makes a model Proven — only live, scored "
             "forecasts do.", ""]
    lines += _data_section(market)
    for key, summary in summaries.items():
        lines += _model_section(key, summary, geometric.get(key))

    lines += ["## Reading this honestly", "",
              "- The three indices move together; pooled n overstates the independent evidence. The CI "
              "resamples days, not forecasts, for that reason.",
              "- The validation and holdout periods were not used to choose anything: every input, window and "
              "penalty was fixed in code before the first run.",
              "- A range model beating a 20-day average is the expected result — volatility clusters, and the "
              "literature has shown it for decades. The question the live scoreboard answers is whether the "
              "margin survives forecasts written before the open.",
              "- Direction is a control. Skill there should be read as a possible leak before it is read as an "
              "edge.", ""]
    return "\n".join(lines)


def report_v2(summaries: Mapping[str, Mapping[str, Any]], comparisons: Mapping[str, Mapping[str, Any]],
              market: MarketData, coverage: Sequence[Tuple[str, int, Optional[date]]], sessions: int,
              generated: datetime, geometric: Mapping[str, Any]) -> str:
    """The version 2 report (logs/analysis/backtest-v2-<date>.md): v2 against v1, and what gets registered."""
    lines = [f"# Analysis backtest — version 2 ({MODEL_VERSION_V2}) against version 1 ({MODEL_VERSION})", "",
             f"Generated {generated:%Y-%m-%d %H:%M} IST. Both versions walked forward together and scored on the "
             "same sessions (those all seven models could forecast). A v2 model is registered only when, on "
             "validation (2025), the 95% interval of mean(v1 loss − v2 loss), resampling session dates, lies "
             "above zero. " + HOLDOUT_SECOND_LOOK, ""]
    lines += ["## Version 2 against version 1", "",
              "| v2 model | v1 model | Period | n | v1 loss | v2 loss | Skill vs v1 | (v1 − v2) CI 95% |",
              "| --- | --- | --- | --- | --- | --- | --- | --- |"]
    decisions = []
    for key, comparison in comparisons.items():
        spec = SPECS[key]
        for split in SPLITS:
            c = comparison.get(split)
            label = split + (" (second look)" if split == "holdout" else "")
            if c is None:
                lines.append(f"| {key} | {spec.compares_with} | {label} | 0 | — | — | — | — |")
                continue
            lines.append(f"| {key} | {spec.compares_with} | {label} | {c['n']} | {_f(c['v1Loss'])} | "
                         f"{_f(c['v2Loss'])} | {_f(c['skillVsV1'])} | {_f(c['diffCiLow'])} to {_f(c['diffCiHigh'])} |")
        v = comparison.get("validation")
        if beats_v1(comparison):
            decisions.append(f"- **{key}: registered.** On validation its loss beat {spec.compares_with}'s with the "
                             f"whole interval above zero ({_f(v['diffCiLow'])} to {_f(v['diffCiHigh'])}).")
        elif v is None:
            decisions.append(f"- **{key}: not registered.** It forecast no validation session; "
                             f"{spec.compares_with} stays.")
        else:
            decisions.append(f"- **{key}: not registered; version 2 added nothing here.** The validation interval "
                             f"({_f(v['diffCiLow'])} to {_f(v['diffCiHigh'])}) does not exclude zero. "
                             f"{spec.compares_with} stays.")
    lines += ["", "## Decision", ""] + decisions
    lines += ["", "Configurations tried, cumulative with version 1: " + ", ".join(
        f"{t} {n}" for t, n in CONFIGURATIONS_TRIED_V2.items()) + ". Nothing was tuned: the v2 inputs, the "
        "transforms, the windows and the ridge penalty were written down before the context tables existed.", ""]

    lines += _data_section(market)
    lines += ["### Context inputs", "",
              f"Sessions with a value, of {sessions} NIFTY sessions (a model forecasts only where all its inputs "
              "exist).", "", "| Input | Sessions | First |", "| --- | --- | --- |"]
    lines += [f"| {name} | {n} | {first or '—'} |" for name, n, first in coverage]
    lines.append("")
    for key, summary in summaries.items():
        lines += _model_section(key, summary, geometric.get(key))

    lines += ["## Reading this honestly", "",
              "- Registration is decided on validation alone. The holdout row is a second look: v1's run saw it "
              "first, so it is evidence, not a test.",
              "- More inputs fit noise more easily. The walk-forward refits monthly on the past only, so an input "
              "that only fits noise shows up as a worse v2 loss, not a better one.",
              "- The three indices move together and share every context input; the CI resamples days, not "
              "forecasts.",
              "- Direction is a control. A v2 direction model that beats v1 is a reason to look for a leak before "
              "it is a finding.",
              "- GIFT Nifty, news and the earnings load have no history and are in no model; they are recorded on "
              "each live forecast (`liveOnly`) until there is enough to test them.", ""]
    return "\n".join(lines)


def _data_section(market: MarketData) -> List[str]:
    lines = ["## Data", "", "| Series | Sessions | First | Last | Days left out |", "| --- | --- | --- | --- | --- |"]
    for name, series in list(market.series.items()) + [("INDIAVIX", market.vix)]:
        lines.append(f"| {name} | {len(series.sessions)} | {series.first or '—'} | {series.last or '—'} | "
                     f"{len(series.dropped)} |")
    lines.append("")
    for name, series in list(market.series.items()) + [("INDIAVIX", market.vix)]:
        reasons = _reason_counts(series)
        if reasons:
            lines.append(f"- {name} left out: " + "; ".join(f"{why} × {n}" for why, n in reasons))
    live = {name: sum(1 for s in series.sessions if s.source.startswith("live"))
            for name, series in market.series.items()}
    if any(live.values()):
        lines.append("- Sessions taken from live_bars (not archived to candles yet): "
                     + ", ".join(f"{k} {v}" for k, v in live.items() if v))
    lines.append("")
    return lines


def _model_section(key: str, summary: Mapping[str, Any], geometric: Optional[Mapping[str, Any]]) -> List[str]:
    spec = SPECS[key]
    is_range = spec.target == "range"
    lines = [f"## {key} ({spec.version})", "", spec.description, "",
             "| Period | From | To | n | Loss | Baseline | Skill | Diff CI 95% |"
             + (" Coverage 80% | Baseline cov. | Bucket Brier | Baseline Brier |" if is_range else ""),
             "| --- | --- | --- | --- | --- | --- | --- | --- |"
             + (" --- | --- | --- | --- |" if is_range else "")]
    for split in SPLITS:
        s = summary[split]
        if s is None:
            empty = "| — " * (8 if is_range else 4)
            lines.append(f"| {split} | — | — | 0 {empty}|")
            continue
        row = (f"| {split} | {s['from'] or '—'} | {s['to'] or '—'} | {s['n']} | {_f(s['loss'])} | "
               f"{_f(s['baselineLoss'])} | {_f(s['skill'])} | {_f(s.get('diffCiLow'))} to "
               f"{_f(s.get('diffCiHigh'))} |")
        if is_range:
            row += (f" {_f(s.get('coverage80'))} | {_f(s.get('baselineCoverage80'))} | "
                    f"{_f(s.get('bucketBrier'))} | {_f(s.get('baselineBucketBrier'))} |")
        lines.append(row)
    lines += ["", "By underlying (all periods): " + ", ".join(
        f"{k} n={v['n']} skill={_f(v['skill'])}" for k, v in summary["byUnderlying"].items()), ""]
    for split in SPLITS:
        cal = (summary[split] or {}).get("calibration") or []
        if cal:
            lines.append(f"Calibration, {split}: " + "; ".join(
                f"{c['from']:.1f}-{c['to']:.1f} n={c['n']} said {c['meanP']:.2f} happened {c['hitRate']:.2f}"
                for c in cal))
    if geometric:
        lines += ["", "Against a 20-session geometric-mean baseline instead: " + "; ".join(
            f"{split} skill {_f(v['skillVsGeometric'])} (geometric loss {_f(v['geometricLoss'])})"
            for split, v in geometric.items())]
    lines += ["", f"Configurations tried for `{spec.target}`: {summary['configurationsTried']}.", ""]
    return lines


def _reason_counts(series: SessionSeries) -> List[Tuple[str, int]]:
    counts: Dict[str, int] = defaultdict(int)
    for _, why in series.dropped:
        # "63 of 75 bars (candles 5m)" -> "fewer than 60 bars ..." would hide the spread; keep the kind only.
        kind = why.split("(")[0].strip()
        kind = "too few bars" if kind.endswith("bars") else kind
        kind = "first bar late" if kind.startswith("first bar") else kind
        kind = "last bar early" if kind.startswith("last bar") else kind
        counts[kind] += 1
    return sorted(counts.items(), key=lambda kv: -kv[1])


def _mean(values: Iterable[float]) -> float:
    vals = list(values)
    return sum(vals) / len(vals) if vals else float("nan")


def _skill(loss: float, base: float) -> Optional[float]:
    if not (math.isfinite(loss) and math.isfinite(base)) or base <= 0:
        return None
    return round(1.0 - loss / base, 4)


def _r(value: Optional[float], digits: int = 4) -> Optional[float]:
    if value is None or not math.isfinite(value):
        return None
    return round(value, digits)


def _f(value: Any) -> str:
    return "—" if value is None else f"{value:.4f}" if isinstance(value, float) else str(value)
