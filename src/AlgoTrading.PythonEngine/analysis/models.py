"""
analysis/models.py

The four version-1 models, their baselines, and the one path every forecast
takes: `fit` on the sessions before a date, then `predict` one session. The
walk-forward backtest and the morning's `issue` both go through it, so the
history the page shows was produced by the same code that writes tomorrow's
forecast — not by a research script that resembles it.

Everything a model sees about session i comes from sessions before i, plus two
calendar facts known in advance: whether an option of the underlying expires
that day, and whether it is a Monday. `build_table` computes those inputs row by
row from the past only; a test corrupts every session after i and checks that
the forecast for i does not move.

    range.har        OLS of ln(range) on ln of the 1-, 5- and 22-session mean
                     ranges (the heterogeneous autoregressive model of realised
                     volatility, applied to the high-low range).
    range.har-vix    the same plus ln India VIX's previous close, an expiry-day
                     and a Monday dummy.
    trend.logit      logistic regression (IRLS, small ridge) on seven fixed
    direction.logit  inputs: the previous range against its 20-session mean,
                     the previous return and efficiency, VIX's previous close
                     and 5-session change, the expiry and Monday dummies.

The range forecast is a distribution, not a number: log-normal around the
fitted log-range with the training residuals' standard deviation. Its median,
80% interval and the probabilities of three buckets (edges at the
underlying's terciles of range in the training window) all come from it.

The baselines are deliberately plain: the mean range of the last 20 sessions,
and the trailing 250-session base rate of trend days and up days. A model that
cannot beat them is not worth its parameters.

Version 2 (MODEL_VERSION_V2) adds the backtestable context of
analysis/context.py — overnight global moves, FII positioning, breadth and
event days — to the same three questions: range.har-vix-cues,
trend.logit-cues and direction.logit-cues. Same fit, same baselines, same
windows and penalty; only the inputs grow. Each is registered only where it
beats its v1 model on validation (backtest.compare), and v1 keeps running
beside it either way.

Deterministic: no random numbers, no search. The feature sets are fixed here
and nowhere else.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from datetime import date
from typing import Any, Callable, Dict, List, Mapping, Optional, Sequence, Tuple

import numpy as np

from analysis import context as ctxmod
from analysis.data import SessionBar
from analysis.scoring import BUCKETS, TREND_EFFICIENCY, efficiency

#: Registered with every v1 model; bump the suffix for a change on the same day.
MODEL_VERSION = "2026-09-27.1"
#: Registered with every v2 model. Its inputs were fixed before any of the
#: tables they read existed, so before any v2 result could be looked at.
MODEL_VERSION_V2 = "2026-09-27.2"

#: Sessions a model must have trained on before it may forecast. With fewer, a
#: 22-session HAR and a seven-input logit are fitted to noise.
MIN_TRAIN_SESSIONS = 250

#: Baseline windows, from the contract.
BASELINE_RANGE_SESSIONS = 20
BASELINE_RATE_SESSIONS = 250
#: A base rate over fewer sessions than this is not a rate.
MIN_RATE_SESSIONS = 20

#: Ridge penalty on the logistic coefficients (standardised inputs; the
#: intercept is not penalised). Small: it keeps IRLS finite when an input
#: separates a few sessions perfectly, not to shrink real effects.
RIDGE = 1.0

#: z for the 10th/90th percentiles of a normal: the 80% interval.
Z80 = 1.2815515655446004


@dataclass(frozen=True)
class ModelSpec:
    key: str
    target: str       # range | trend | direction
    kind: str         # har | logit
    uses_vix: bool
    description: str
    version: str = MODEL_VERSION
    #: v2's inputs beyond v1's: (input name, context column, transform). The
    #: transform is None, "log", "abs" or "lopsided" (|x − 50|, for a share in %).
    extras: Tuple[Tuple[str, str, Optional[str]], ...] = ()
    #: The v1 model a v2 model has to beat on validation to be registered.
    compares_with: Optional[str] = None


MODELS: Dict[str, ModelSpec] = {m.key: m for m in (
    ModelSpec("range.har", "range", "har", False,
              "Log-range on the logs of the 1-, 5- and 22-session mean ranges (OLS); log-normal around it with "
              "the training residuals' spread."),
    ModelSpec("range.har-vix", "range", "har", True,
              "Log-range on the logs of the 1-, 5- and 22-session mean ranges, plus India VIX's previous close "
              "(log), expiry-day and Monday flags."),
    ModelSpec("trend.logit", "trend", "logit", True,
              "Logistic regression (ridge) for a trend day (|close - open| >= 0.6 x range) on the previous "
              "range against its 20-session mean, previous return and efficiency, India VIX's previous close "
              "and 5-session change, expiry-day and Monday flags."),
    ModelSpec("direction.logit", "direction", "logit", True,
              "Logistic regression (ridge) for close > open, on the same seven inputs as trend.logit. A "
              "control: nothing in the desk's research predicts intraday direction."),
)}


#: range.har-vix-cues: the size of the overnight moves (a range has no sign),
#: US VIX, FII positioning and its last change, how lopsided the previous
#: session's breadth was, the heavyweights' dispersion, and event days.
RANGE_EXTRAS: Tuple[Tuple[str, str, Optional[str]], ...] = (
    ("lnUsVix", "usVix", "log"),
    ("absSpxRet", "spxRet", "abs"),
    ("absAsiaRet", "asiaRet", "abs"),
    ("absUsdinrRet", "usdinrRet", "abs"),
    ("fiiNetLong", "fiiNetLong", None),
    ("absFiiChange1", "fiiChange1", "abs"),
    ("breadthSkew", "pctAdvancing", "lopsided"),
    ("lnHwDispersion", "hwDispersion", "log"),
    ("majorEvent", "majorEvent", None),
    ("majorEve", "majorEve", None),
    ("dataRelease", "dataRelease", None),
)

#: The logits' extras: the same groups with their signs kept. One of the four
#: US equity series (SPX; NDX, DJI and ES move with it) and the three Asian
#: indices as one mean; DXY, the US 10-year and the day after an event are
#: recorded on each forecast but left out, to keep 22 inputs from becoming 30.
LOGIT_EXTRAS: Tuple[Tuple[str, str, Optional[str]], ...] = (
    ("spxRet", "spxRet", None),
    ("usVixChange", "usVixChange", None),
    ("asiaRet", "asiaRet", None),
    ("usdinrRet", "usdinrRet", None),
    ("brentRet", "brentRet", None),
    ("fiiNetLong", "fiiNetLong", None),
    ("fiiChange1", "fiiChange1", None),
    ("fiiChange5", "fiiChange5", None),
    ("pctAdvancing", "pctAdvancing", None),
    ("netHighsLowsPct", "netHighsLowsPct", None),
    ("hwRet", "hwRet", None),
    ("hwDispersion", "hwDispersion", None),
    ("majorEvent", "majorEvent", None),
    ("majorEve", "majorEve", None),
    ("dataRelease", "dataRelease", None),
)

_CUES = ("overnight US and Asian moves, US VIX, the rupee, oil, FII index-futures positioning, NSE breadth, "
         "the heavyweights' previous session and RBI/Fed/Budget/data-release days")

MODELS_V2: Dict[str, ModelSpec] = {m.key: m for m in (
    ModelSpec("range.har-vix-cues", "range", "har", True,
              "range.har-vix plus the pre-open context (absolute sizes of the moves): " + _CUES + ".",
              MODEL_VERSION_V2, RANGE_EXTRAS, "range.har-vix"),
    ModelSpec("trend.logit-cues", "trend", "logit", True,
              "trend.logit's seven inputs plus the pre-open context: " + _CUES + ".",
              MODEL_VERSION_V2, LOGIT_EXTRAS, "trend.logit"),
    ModelSpec("direction.logit-cues", "direction", "logit", True,
              "direction.logit's seven inputs plus the pre-open context: " + _CUES + ". Still a control: skill "
              "here is a reason to look for a leak.",
              MODEL_VERSION_V2, LOGIT_EXTRAS, "direction.logit"),
)}

#: Every model of every version, by key.
SPECS: Dict[str, ModelSpec] = {**MODELS, **MODELS_V2}


class ForecastUnavailable(RuntimeError):
    """This session cannot be forecast by this model; the message says which input is missing."""


# ------------------------------------------------------------------ the table --

@dataclass
class Table:
    """
    One underlying's sessions as aligned arrays. Outcome columns describe
    session i itself; input columns describe what was known before it opened.
    A pending row (the session being issued) has no outcome.
    """

    underlying: str
    days: List[date]
    open: np.ndarray
    high: np.ndarray
    low: np.ndarray
    close: np.ndarray
    # outcomes of session i
    prev_close: np.ndarray
    range_pct: np.ndarray
    efficiency: np.ndarray
    trend: np.ndarray
    up: np.ndarray
    # inputs for session i, from sessions < i
    r1: np.ndarray
    r5: np.ndarray
    r22: np.ndarray
    mean20: np.ndarray
    base_trend: np.ndarray
    base_up: np.ndarray
    prev_ret: np.ndarray
    prev_eff: np.ndarray
    vix_prev: np.ndarray
    vix_chg5: np.ndarray
    # calendar facts about session i, known in advance
    expiry: np.ndarray
    monday: np.ndarray
    # v2's context inputs for session i (analysis/context.py), when the table was built with them
    context: Optional[ctxmod.ContextColumns] = None
    # input matrices already built, by model key (a table is never changed after it is built)
    _features: Dict[str, np.ndarray] = field(default_factory=dict, compare=False, repr=False)

    def index_of(self, day: date) -> int:
        try:
            return self.days.index(day)
        except ValueError:
            raise KeyError(f"{self.underlying} has no session {day}") from None


def build_table(underlying: str, sessions: Sequence[SessionBar], vix_close: Mapping[date, float],
                is_expiry: Callable[[date], bool], pending: Optional[date] = None,
                context: Optional[ctxmod.Context] = None) -> Table:
    """
    The table for `sessions` (oldest first), with a last row for `pending`
    when given. Every input column at row i is computed from rows < i only;
    `context` adds v2's columns, computed by the same rule (context.columns).
    """
    rows = sorted(sessions, key=lambda s: s.day)
    if pending is not None:
        rows = [s for s in rows if s.day < pending]
    days = [s.day for s in rows] + ([pending] if pending is not None else [])
    n = len(days)
    nan = np.full(n, np.nan)

    o, h, l, c = nan.copy(), nan.copy(), nan.copy(), nan.copy()
    for i, s in enumerate(rows):
        o[i], h[i], l[i], c[i] = s.open, s.high, s.low, s.close

    prev_close, rng, eff, trend, up = nan.copy(), nan.copy(), nan.copy(), nan.copy(), nan.copy()
    for i in range(len(rows)):
        eff[i] = efficiency(o[i], h[i], l[i], c[i])
        trend[i] = float(eff[i] >= TREND_EFFICIENCY)
        up[i] = float(c[i] > o[i])
        if i >= 1:
            prev_close[i] = c[i - 1]
            rng[i] = (h[i] - l[i]) / c[i - 1] * 100.0
    if pending is not None and rows:
        prev_close[n - 1] = c[len(rows) - 1]

    r1, r5, r22, mean20 = nan.copy(), nan.copy(), nan.copy(), nan.copy()
    base_trend, base_up, prev_ret, prev_eff = nan.copy(), nan.copy(), nan.copy(), nan.copy()
    vix_prev, vix_chg5 = nan.copy(), nan.copy()
    for i in range(n):
        past = rng[:i]
        r1[i] = _mean_last(past, 1)
        r5[i] = _mean_last(past, 5)
        r22[i] = _mean_last(past, 22)
        mean20[i] = _mean_last(past, BASELINE_RANGE_SESSIONS)
        base_trend[i] = _rate(trend[max(0, i - BASELINE_RATE_SESSIONS):i])
        base_up[i] = _rate(up[max(0, i - BASELINE_RATE_SESSIONS):i])
        if i >= 1:
            prev_eff[i] = eff[i - 1]
            vix_prev[i] = vix_close.get(days[i - 1], np.nan)
        if i >= 2:
            prev_ret[i] = (c[i - 1] / c[i - 2] - 1.0) * 100.0
        if i >= 6:
            vix_chg5[i] = vix_prev[i] - vix_close.get(days[i - 6], np.nan)

    expiry = np.array([float(bool(is_expiry(d))) for d in days]) if n else nan.copy()
    monday = np.array([float(d.weekday() == 0) for d in days]) if n else nan.copy()
    cols = ctxmod.columns(days, context) if context is not None else None
    return Table(underlying, days, o, h, l, c, prev_close, rng, eff, trend, up, r1, r5, r22, mean20,
                 base_trend, base_up, prev_ret, prev_eff, vix_prev, vix_chg5, expiry, monday, cols)


def _mean_last(values: np.ndarray, k: int) -> float:
    """Mean of the last k values, or NaN unless all k exist."""
    if len(values) < k:
        return np.nan
    tail = values[-k:]
    return float(tail.mean()) if np.isfinite(tail).all() else np.nan


def _rate(values: np.ndarray) -> float:
    known = values[np.isfinite(values)]
    return float(known.mean()) if len(known) >= MIN_RATE_SESSIONS else np.nan


# ------------------------------------------------------------- inputs & targets --

def features(spec: ModelSpec, t: Table) -> np.ndarray:
    """The model's input matrix (rows × inputs), NaN wherever an input is unknown. Read-only."""
    cached = t._features.get(spec.key)
    if cached is not None:
        return cached
    with np.errstate(divide="ignore", invalid="ignore"):
        if spec.kind == "har":
            cols = [np.log(t.r1), np.log(t.r5), np.log(t.r22)]
            if spec.uses_vix:
                cols += [np.log(t.vix_prev), t.expiry, t.monday]
        elif spec.kind == "logit":
            cols = [np.log(t.r1 / t.mean20), t.prev_ret, t.prev_eff, t.vix_prev, t.vix_chg5, t.expiry, t.monday]
        else:
            raise ValueError(f"unknown model kind {spec.kind!r}")
        cols += [_context_input(t, column, how) for _, column, how in spec.extras]
        X = np.column_stack(cols) if len(t.days) else np.empty((0, len(cols)))
    X[~np.isfinite(X)] = np.nan
    X.flags.writeable = False
    t._features[spec.key] = X
    return X


def _context_input(t: Table, column: str, how: Optional[str]) -> np.ndarray:
    """A v2 input: the context column, transformed; all NaN when the table has no context (a v1 table)."""
    if t.context is None:
        return np.full(len(t.days), np.nan)
    v = t.context.values[column]
    if how is None:
        return v
    if how == "log":
        return np.where(v > 0, np.log(np.where(v > 0, v, 1.0)), np.nan)
    if how == "abs":
        return np.abs(v)
    if how == "lopsided":
        return np.abs(v - 50.0)
    raise ValueError(f"unknown transform {how!r}")


def target(spec: ModelSpec, t: Table) -> np.ndarray:
    if spec.target == "range":
        with np.errstate(divide="ignore", invalid="ignore"):
            y = np.where(t.range_pct > 0, np.log(t.range_pct), np.nan)
        return y
    if spec.target == "trend":
        return t.trend.copy()
    if spec.target == "direction":
        return t.up.copy()
    raise ValueError(f"unknown target {spec.target!r}")


def _baseline_known(spec: ModelSpec, t: Table) -> np.ndarray:
    if spec.target == "range":
        return np.isfinite(t.mean20) & (t.mean20 > 0)
    return np.isfinite(t.base_trend if spec.target == "trend" else t.base_up)


# ------------------------------------------------------------------------ fit --

@dataclass(frozen=True)
class Fit:
    spec: ModelSpec
    n: int
    through: date                                # the last session trained on
    coef: np.ndarray
    # range models
    sigma: float = float("nan")
    edges: Tuple[float, float] = (float("nan"), float("nan"))
    bucket_freq: Tuple[float, float, float] = (float("nan"),) * 3
    base_q10: float = float("nan")
    base_q90: float = float("nan")
    # logistic models: inputs are standardised with the training window's mean and spread
    center: Optional[np.ndarray] = None
    scale: Optional[np.ndarray] = None


def training_rows(spec: ModelSpec, t: Table, before: date, X: Optional[np.ndarray] = None,
                  y: Optional[np.ndarray] = None) -> np.ndarray:
    """Rows of sessions before `before` whose inputs, outcome and baseline are all known."""
    X = features(spec, t) if X is None else X
    y = target(spec, t) if y is None else y
    before_mask = np.array([d < before for d in t.days], dtype=bool)
    ok = before_mask & np.isfinite(X).all(axis=1) & np.isfinite(y) & _baseline_known(spec, t)
    return np.flatnonzero(ok)


def fit(spec: ModelSpec, t: Table, before: date) -> Fit:
    """Fit `spec` on every usable session of `t` before `before`."""
    X, y = features(spec, t), target(spec, t)
    rows = training_rows(spec, t, before, X, y)
    if len(rows) < MIN_TRAIN_SESSIONS:
        raise ForecastUnavailable(f"{spec.key} {t.underlying}: {len(rows)} usable sessions before {before}, "
                                  f"{MIN_TRAIN_SESSIONS} needed")
    through = t.days[rows[-1]]
    if spec.kind == "har":
        return _fit_har(spec, t, X[rows], y[rows], rows, through)
    return _fit_logit(spec, X[rows], y[rows], through)


def _fit_har(spec: ModelSpec, t: Table, X: np.ndarray, y: np.ndarray, rows: np.ndarray, through: date) -> Fit:
    coef, sigma = ols(X, y)
    ranges = t.range_pct[rows]
    e1, e2 = (float(v) for v in np.quantile(ranges, [1 / 3, 2 / 3]))
    freq = (float(np.mean(ranges < e1)), float(np.mean((ranges >= e1) & (ranges < e2))), float(np.mean(ranges >= e2)))
    base_err = np.log(ranges) - np.log(t.mean20[rows])
    q10, q90 = (float(v) for v in np.quantile(base_err, [0.1, 0.9]))
    return Fit(spec, len(rows), through, coef, sigma=sigma, edges=(e1, e2), bucket_freq=freq,
               base_q10=q10, base_q90=q90)


def _fit_logit(spec: ModelSpec, X: np.ndarray, y: np.ndarray, through: date) -> Fit:
    center = X.mean(axis=0)
    scale = X.std(axis=0)
    scale[scale == 0] = 1.0   # an input constant in the window (no expiries in it, say) carries no signal
    coef = logistic_irls((X - center) / scale, y, RIDGE)
    return Fit(spec, len(y), through, coef, center=center, scale=scale)


def ols(X: np.ndarray, y: np.ndarray) -> Tuple[np.ndarray, float]:
    """Least squares with an intercept: (coefficients, intercept first; residual standard deviation)."""
    A = np.column_stack([np.ones(len(y)), X])
    coef, *_ = np.linalg.lstsq(A, y, rcond=None)
    resid = y - A @ coef
    dof = max(len(y) - A.shape[1], 1)
    return coef, float(math.sqrt(float(resid @ resid) / dof))


def logistic_irls(Z: np.ndarray, y: np.ndarray, ridge: float = RIDGE, max_iter: int = 100,
                  tol: float = 1e-10) -> np.ndarray:
    """
    Logistic regression with an intercept by iteratively reweighted least
    squares (Newton's method), with an L2 penalty on every coefficient but the
    intercept. Returns the coefficients, intercept first.
    """
    A = np.column_stack([np.ones(len(y)), Z])
    k = A.shape[1]
    penalty = ridge * np.eye(k)
    penalty[0, 0] = 0.0
    w = np.zeros(k)
    for _ in range(max_iter):
        p = _sigmoid(A @ w)
        weight = p * (1.0 - p)
        gradient = A.T @ (y - p) - penalty @ w
        hessian = (A * weight[:, None]).T @ A + penalty
        step = np.linalg.solve(hessian, gradient)
        w = w + step
        if float(np.max(np.abs(step))) < tol:
            break
    return w


def _sigmoid(x: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-np.clip(x, -35.0, 35.0)))


def _norm_cdf(x: float) -> float:
    return 0.5 * (1.0 + math.erf(x / math.sqrt(2.0)))


# -------------------------------------------------------------------- predict --

@dataclass(frozen=True)
class Forecast:
    prediction: Dict[str, Any]
    baseline: Dict[str, Any]
    inputs: Dict[str, Any]


def predict(model: Fit, t: Table, i: int) -> Forecast:
    """Forecast session i of `t` with a fitted model. Row i's inputs must all be known."""
    spec = model.spec
    x = features(spec, t)[i]
    missing = [name for name, v in zip(_input_names(spec), x) if not np.isfinite(v)]
    if missing:
        sources = _sources(spec)
        raise ForecastUnavailable(f"{spec.key} {t.underlying} {t.days[i]}: no "
                                  + ", ".join(f"{m} ({sources[m]})" if m in sources else m for m in missing))
    if not _baseline_known(spec, t)[i]:
        raise ForecastUnavailable(f"{spec.key} {t.underlying} {t.days[i]}: the baseline needs more sessions")
    if spec.kind == "har":
        return _predict_range(model, t, i, x)
    return _predict_probability(model, t, i, x)


def forecast(spec: ModelSpec, t: Table, i: int, fit_before: date) -> Forecast:
    """Fit on the sessions before `fit_before` (never after session i) and forecast session i."""
    if fit_before > t.days[i]:
        raise ValueError("a forecast may not be fitted on its own session or later")
    return predict(fit(spec, t, fit_before), t, i)


def _predict_range(model: Fit, t: Table, i: int, x: np.ndarray) -> Forecast:
    mu = float(model.coef[0] + model.coef[1:] @ x)
    s = model.sigma
    e1, e2 = model.edges
    p_low = _norm_cdf((math.log(e1) - mu) / s)
    p_mid = _norm_cdf((math.log(e2) - mu) / s)
    prev_close = float(t.prev_close[i])
    prediction = range_distribution(math.exp(mu), math.exp(mu - Z80 * s), math.exp(mu + Z80 * s), prev_close,
                                    (p_low, p_mid - p_low, 1.0 - p_mid), model.edges)
    base = float(t.mean20[i])
    baseline = range_distribution(base, base * math.exp(model.base_q10), base * math.exp(model.base_q90),
                                  prev_close, model.bucket_freq, model.edges)
    inputs: Dict[str, Any] = {
        "prevSession": t.days[i - 1].isoformat(),
        "prevClose": round(prev_close, 2),
        "r1": round(float(t.r1[i]), 4),
        "r5": round(float(t.r5[i]), 4),
        "r22": round(float(t.r22[i]), 4),
    }
    if model.spec.uses_vix:
        inputs.update({"vixPrevClose": round(float(t.vix_prev[i]), 2), "expiryDay": bool(t.expiry[i]),
                       "monday": bool(t.monday[i])})
    inputs.update(_context_inputs(model.spec, t, i))
    inputs.update(_training(model))
    return Forecast(prediction, baseline, inputs)


def _predict_probability(model: Fit, t: Table, i: int, x: np.ndarray) -> Forecast:
    z = (x - model.center) / model.scale
    p = float(_sigmoid(np.array([model.coef[0] + model.coef[1:] @ z]))[0])
    base = float(t.base_trend[i] if model.spec.target == "trend" else t.base_up[i])
    inputs = {
        "prevSession": t.days[i - 1].isoformat(),
        "rangeRatio": round(float(t.r1[i] / t.mean20[i]), 4),
        "prevReturn": round(float(t.prev_ret[i]), 4),
        "prevEfficiency": round(float(t.prev_eff[i]), 4),
        "vixPrevClose": round(float(t.vix_prev[i]), 2),
        "vixChange5": round(float(t.vix_chg5[i]), 2),
        "expiryDay": bool(t.expiry[i]),
        "monday": bool(t.monday[i]),
        **_context_inputs(model.spec, t, i),
        **_training(model),
    }
    return Forecast({"p": round(p, 4)}, {"p": round(base, 4)}, inputs)


def _context_inputs(spec: ModelSpec, t: Table, i: int) -> Dict[str, Any]:
    """A v2 forecast records every context column (raw, not transformed), the ones it did not use included."""
    if not spec.extras or t.context is None:
        return {}
    return t.context.row(i)


def _training(model: Fit) -> Dict[str, Any]:
    return {"trainingSessions": model.n, "trainedThrough": model.through.isoformat()}


def _input_names(spec: ModelSpec) -> List[str]:
    if spec.kind == "har":
        names = ["r1", "r5", "r22"] + (["vixPrevClose", "expiryDay", "monday"] if spec.uses_vix else [])
    else:
        names = ["rangeRatio", "prevReturn", "prevEfficiency", "vixPrevClose", "vixChange5", "expiryDay", "monday"]
    return names + [name for name, _, _ in spec.extras]


def _sources(spec: ModelSpec) -> Dict[str, str]:
    """Input name -> the table it is read from, for v2's inputs: what to look at when one is missing."""
    return {name: ctxmod.SOURCES[column] for name, column, _ in spec.extras if column in ctxmod.SOURCES}


def range_distribution(median: float, low80: float, high80: float, prev_close: float,
                       buckets: Sequence[float], edges: Sequence[float]) -> Dict[str, Any]:
    """The contract's range object: percentages of the previous close, and the same in index points."""
    def points(pct: float) -> float:
        return round(pct * prev_close / 100.0, 2)

    return {
        "median": round(median, 4),
        "low80": round(low80, 4),
        "high80": round(high80, 4),
        "prevClose": round(prev_close, 2),
        "points": {"median": points(median), "low80": points(low80), "high80": points(high80)},
        "buckets": {name: round(float(p), 4) for name, p in zip(BUCKETS, buckets)},
        "bucketEdges": [round(float(e), 4) for e in edges],
    }


def forecast_payload(spec: ModelSpec, underlying: str, session: date, fc: Forecast) -> Dict[str, Any]:
    """POST /api/Forecasts body."""
    return {
        "modelKey": spec.key,
        "modelVersion": spec.version,
        "target": spec.target,
        "underlying": underlying,
        "sessionDate": session.isoformat(),
        "prediction": fc.prediction,
        "baseline": fc.baseline,
        "inputs": fc.inputs,
    }
