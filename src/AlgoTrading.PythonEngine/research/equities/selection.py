"""
research/equities/selection.py

R1 of the stock-picker blueprint: does a nightly watchlist, built only from
end-of-day facts, pick stocks that move more the next day?

Everything here works on the normalised NSE bhavcopy rows
(research.equities.bhavcopy). For each stock and date d:

  features (known at d's close)   liquidity, trend, relative strength,
                                  volume and delivery expansion, compression
  outcomes (session d+1)          range, one-way move, efficiency, best
                                  excursion from the open, gap

Two rules keep it honest:

  * No look-ahead. A feature at d uses rows up to d; outcomes are the NEXT row
    of the same stock. `tests/test_equities_selection.py` changes future rows and
    checks that no feature moves.
  * Volatile stocks move more by definition. Every outcome is also measured
    in units of the stock's own ATR, and the watchlist is compared with a
    baseline that simply takes the most volatile stocks.

Returns use the exchange's previous close (`prev_close`), which the exchange
adjusts on corporate-action dates, so a split does not show up as a -50% day.
"""

from __future__ import annotations

import glob
import os
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional

import numpy as np
import pandas as pd

NUMERIC = ("open", "high", "low", "close", "last", "prev_close", "volume", "turnover", "trades", "delivery_qty",
           "delivery_pct")


@dataclass(frozen=True)
class UniverseRules:
    """L0: what may be traded the next day. Starting values from the blueprint, not tuned."""
    series: str = "EQ"
    min_price: float = 50.0
    min_sessions: int = 60
    turnover_window: int = 20
    min_median_turnover: float = 10e7        # Rs 10 crore


def load_nse(root: str, start: Optional[str] = None, end: Optional[str] = None) -> pd.DataFrame:
    """Normalised NSE rows from `<root>/normalized/nse/*.csv.gz`, sorted by symbol and date."""
    files = sorted(glob.glob(os.path.join(root, "normalized", "nse", "*.csv.gz")))
    if not files:
        raise FileNotFoundError(f"no normalised NSE files under {root}; run tools/equities_eod.py normalise")
    frames = []
    for path in files:
        month = os.path.basename(path)[:7]
        if (start and month < start[:7]) or (end and month > end[:7]):
            continue
        frames.append(pd.read_csv(path, dtype={"symbol": str, "series": str, "isin": str, "security_id": str,
                                               "name": str}, keep_default_na=False, na_values=[""]))
    df = pd.concat(frames, ignore_index=True)
    for col in NUMERIC:
        df[col] = pd.to_numeric(df[col], errors="coerce")
    df["trade_date"] = pd.to_datetime(df["trade_date"])
    if start:
        df = df[df["trade_date"] >= pd.Timestamp(start)]
    if end:
        df = df[df["trade_date"] <= pd.Timestamp(end)]
    return df.sort_values(["symbol", "trade_date"]).reset_index(drop=True)


def _ema(series: pd.Series, span: int) -> pd.Series:
    return series.ewm(span=span, adjust=False, min_periods=span).mean()


def add_features(df: pd.DataFrame, rules: UniverseRules = UniverseRules()) -> pd.DataFrame:
    """
    Per-stock features at each date's close, from that date and earlier rows
    only. The input must hold one series per symbol (filter the series first).
    """
    d = df.sort_values(["symbol", "trade_date"]).copy()
    g = d.groupby("symbol", sort=False)

    d["ret"] = d["close"] / d["prev_close"] - 1.0
    # A price index built from exchange-adjusted daily returns: splits and
    # bonuses do not break it.
    d["px"] = g["ret"].transform(lambda r: (1.0 + r.fillna(0.0)).cumprod())
    g = d.groupby("symbol", sort=False)
    d["sessions"] = g.cumcount() + 1
    d["median_turnover"] = g["turnover"].transform(
        lambda s: s.rolling(rules.turnover_window, min_periods=rules.turnover_window).median())

    true_range = np.maximum.reduce([
        (d["high"] - d["low"]).to_numpy(),
        (d["high"] - d["prev_close"]).abs().to_numpy(),
        (d["low"] - d["prev_close"]).abs().to_numpy(),
    ])
    d["tr_pct"] = true_range / d["prev_close"] * 100.0
    g = d.groupby("symbol", sort=False)
    d["atr_pct"] = g["tr_pct"].transform(lambda s: s.rolling(14, min_periods=14).mean())

    d["ema20"] = g["px"].transform(lambda s: _ema(s, 20))
    d["ema50"] = g["px"].transform(lambda s: _ema(s, 50))
    d["trend_up"] = (d["px"] > d["ema20"]) & (d["ema20"] > d["ema50"])
    d["trend_down"] = (d["px"] < d["ema20"]) & (d["ema20"] < d["ema50"])
    d["high_250"] = g["px"].transform(lambda s: s.rolling(250, min_periods=120).max())
    d["low_250"] = g["px"].transform(lambda s: s.rolling(250, min_periods=120).min())
    d["dist_high"] = (d["px"] / d["high_250"] - 1.0) * 100.0
    d["dist_low"] = (d["px"] / d["low_250"] - 1.0) * 100.0
    d["ret5"] = (g["px"].transform(lambda s: s / s.shift(5)) - 1.0) * 100.0
    d["ret20"] = (g["px"].transform(lambda s: s / s.shift(20)) - 1.0) * 100.0
    d["ret60"] = (g["px"].transform(lambda s: s / s.shift(60)) - 1.0) * 100.0
    d["vol_exp"] = g["volume"].transform(lambda s: s.rolling(5, min_periods=5).mean() /
                                         s.rolling(50, min_periods=50).mean())
    d["deliv_ratio"] = d["delivery_pct"] / g["delivery_pct"].transform(
        lambda s: s.rolling(20, min_periods=10).mean())
    rng = d["high"] - d["low"]
    d["range"] = rng
    d["nr7"] = rng <= g["range"].transform(lambda s: s.rolling(7, min_periods=7).min())
    d["inside"] = (d["high"] <= g["high"].shift(1)) & (d["low"] >= g["low"].shift(1))
    d["upper_circuitish"] = (d["close"] >= d["high"]) & (d["ret"] >= 0.019)
    d["circuit_days20"] = d.groupby("symbol", sort=False)["upper_circuitish"].transform(
        lambda s: s.astype(float).rolling(20, min_periods=1).sum())

    d["in_universe"] = ((d["series"] == rules.series) & (d["close"] >= rules.min_price)
                        & (d["sessions"] >= rules.min_sessions)
                        & (d["median_turnover"] >= rules.min_median_turnover))
    return d.drop(columns=["range"])


def add_market_relative(d: pd.DataFrame) -> pd.DataFrame:
    """
    Relative strength against the universe's median stock on the same date
    (an equal-weight market proxy that needs no index file).
    """
    out = d.copy()
    uni = out[out["in_universe"]]
    for col in ("ret5", "ret20", "ret60"):
        median = uni.groupby("trade_date")[col].median()
        out[f"rs{col[3:]}"] = out[col] - out["trade_date"].map(median)
    return out


def add_next_day(d: pd.DataFrame) -> pd.DataFrame:
    """Outcomes of the stock's NEXT session, in % of that session's open and in units of today's ATR%."""
    out = d.sort_values(["symbol", "trade_date"]).copy()
    g = out.groupby("symbol", sort=False)
    nxt = {c: g[c].shift(-1) for c in ("trade_date", "open", "high", "low", "close", "prev_close")}
    out["next_date"] = nxt["trade_date"]
    o = nxt["open"]
    out["n_range"] = (nxt["high"] - nxt["low"]) / o * 100.0
    out["n_move"] = (nxt["close"] - o) / o * 100.0
    out["n_oneway"] = out["n_move"].abs()
    out["n_eff"] = (nxt["close"] - o).abs() / (nxt["high"] - nxt["low"]).replace(0, np.nan)
    out["n_best"] = np.maximum(nxt["high"] - o, o - nxt["low"]) / o * 100.0
    out["n_gap"] = (o / nxt["prev_close"] - 1.0) * 100.0
    for col in ("n_range", "n_oneway", "n_best"):
        out[f"{col}_atr"] = out[col] / out["atr_pct"]
    # Up-move from the open and down-move from the open, for long and short lists.
    out["n_up"] = (nxt["high"] - o) / o * 100.0
    out["n_down"] = (o - nxt["low"]) / o * 100.0
    return out


def pick_top(frame: pd.DataFrame, score: str, n: int, ascending: bool = False) -> pd.DataFrame:
    """The top `n` universe stocks per date by `score` (NaN scores never picked)."""
    uni = frame[frame["in_universe"] & frame[score].notna()]
    ranked = uni.sort_values(["trade_date", score], ascending=[True, ascending])
    return ranked.groupby("trade_date", sort=False).head(n)


def percentile_rank(frame: pd.DataFrame, col: str) -> pd.Series:
    """Cross-sectional percentile (0..1) of `col` among universe stocks on each date; NaN outside the universe."""
    uni = frame["in_universe"] & frame[col].notna()
    ranks = frame.loc[uni].groupby("trade_date")[col].rank(pct=True)
    return ranks.reindex(frame.index)


def add_inplay_score(d: pd.DataFrame) -> pd.DataFrame:
    """
    Today's shock in the stock's own units: |return| in ATRs and volume over
    its previous 50-session average, each as a cross-sectional percentile among
    universe stocks, averaged. Needs 50 sessions of warm-up per stock.
    """
    out = d.copy()
    g = out.groupby("symbol", sort=False)
    out["move_atr"] = out["ret"].abs() * 100.0 / out["atr_pct"]
    out["range_atr"] = out["tr_pct"] / out["atr_pct"]
    out["vol_shock"] = out["volume"] / g["volume"].transform(lambda s: s.shift(1).rolling(50, min_periods=50).mean())
    out["pr_move_atr"] = percentile_rank(out, "move_atr")
    out["pr_vol_shock"] = percentile_rank(out, "vol_shock")
    out["inplay_score"] = out[["pr_move_atr", "pr_vol_shock"]].mean(axis=1, skipna=False)
    return out


def inplay_among_volatile(d: pd.DataFrame, top: int = 50, pool: int = 200) -> pd.DataFrame:
    """R1 iteration 3: among the `pool` highest-ATR% universe stocks of each date, the `top` by in-play score."""
    atr_rank = d[d["in_universe"]].groupby("trade_date")["atr_pct"].rank(ascending=False)
    volatile = d.loc[atr_rank[atr_rank <= pool].index]
    return pick_top(volatile, "inplay_score", top)
