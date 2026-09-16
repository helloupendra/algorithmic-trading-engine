"""
tools/stock_picker_research.py

Stock-picker research on the design period. Writes Markdown reports to
private/research/.

  r1   selection power of nightly watchlists built from bhavcopy facts
  r23  R2 (what 09:30 facts say about the rest of the day) and R3 (the
       blueprint's setups with costs and portfolio limits) on Dhan 5-minute bars

Usage (from src/AlgoTrading.PythonEngine):
    python tools/stock_picker_research.py r1  [--root ~/OpenFNO-data/equities/eod]
                                              [--from 2024-09-01] [--design-end 2025-12-31] [--top 50]
    python tools/stock_picker_research.py r23 [--intraday-root ~/OpenFNO-data/equities/intraday-5m] [same options]

Only the design period is analysed. Later dates are the holdout and are not
read beyond feature warm-up.
"""

from __future__ import annotations

import argparse
import math
import os
import sys
from datetime import date, timedelta
from typing import Dict, List, Optional, Tuple

ENGINE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
if ENGINE_DIR not in sys.path:
    sys.path.insert(0, ENGINE_DIR)

import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from core.config import REPO_ROOT  # noqa: E402
from research.equities import bhavcopy, intraday, selection as sel, setups as st  # noqa: E402

OUTCOMES = ("n_range", "n_oneway", "n_best", "n_range_atr", "n_oneway_atr", "n_best_atr", "n_eff")


def _date_means(picks: pd.DataFrame, cols) -> pd.DataFrame:
    """Median of each outcome across a date's picks, per date."""
    return picks.groupby("trade_date")[list(cols)].median()


def _ratio_table(frames: Dict[str, pd.DataFrame], universe: pd.DataFrame, halves: List[Tuple[str, str, str]]) -> str:
    uni = _date_means(universe, OUTCOMES)
    lines = ["| selector | half | dates | range % | one-way % | best % | range/ATR | one-way/ATR | best/ATR | efficiency |",
             "|---|---|---|---|---|---|---|---|---|---|"]
    for name, picks in frames.items():
        per = _date_means(picks, OUTCOMES)
        for label, lo, hi in halves:
            p = per[(per.index >= lo) & (per.index <= hi)]
            u = uni.loc[p.index]
            cells = []
            for col in OUTCOMES:
                ratio = (p[col] / u[col]).replace([np.inf, -np.inf], np.nan).dropna()
                cells.append(f"{p[col].mean():.2f} ({ratio.mean():.2f}×)")
            lines.append(f"| {name} | {label} | {len(p)} | " + " | ".join(cells) + " |")
    return "\n".join(lines)


def _direction_table(lists: Dict[str, Tuple[pd.DataFrame, int]], universe: pd.DataFrame,
                     halves: List[Tuple[str, str, str]]) -> str:
    """Next-day open-to-close move of a long (+1) or short (-1) list, in excess of the universe that day."""
    uni_move = universe.groupby("trade_date")["n_move"].mean()
    lines = ["| list | half | dates | picks | mean move % (signed) | excess vs universe % | t (dates) | hit rate |",
             "|---|---|---|---|---|---|---|---|"]
    for name, (picks, sign) in lists.items():
        for label, lo, hi in halves:
            p = picks[(picks["trade_date"] >= lo) & (picks["trade_date"] <= hi)].dropna(subset=["n_move"])
            if p.empty:
                continue
            signed = sign * p["n_move"]
            per_date = (sign * p.groupby("trade_date")["n_move"].mean()
                        - sign * uni_move.reindex(p["trade_date"].unique()).values)
            t = per_date.mean() / (per_date.std(ddof=1) / math.sqrt(len(per_date))) if len(per_date) > 2 else np.nan
            lines.append(f"| {name} | {label} | {p['trade_date'].nunique()} | {len(p)} | {signed.mean():+.3f} | "
                         f"{per_date.mean():+.3f} | {t:+.1f} | {(signed > 0).mean() * 100:.0f}% |")
    return "\n".join(lines)


def run_r1(root: str, start: str, design_end: str, top: int, out_dir: str) -> str:
    warmup_start = (date.fromisoformat(start) - timedelta(days=400)).isoformat()
    # One extra month past the design end so the last design date has a next session.
    read_end = (date.fromisoformat(design_end) + timedelta(days=10)).isoformat()
    raw = sel.load_nse(root, warmup_start, read_end)
    eq = raw[raw["series"] == "EQ"]
    feats = sel.add_next_day(sel.add_market_relative(sel.add_features(eq)))
    # Shock features need the warm-up rows, so they are computed before the design slice.
    feats = sel.add_inplay_score(feats)
    design = feats[(feats["trade_date"] >= start) & (feats["trade_date"] <= design_end)
                   & feats["next_date"].notna()].copy()
    universe = design[design["in_universe"]]

    mid = pd.Timestamp(start) + (pd.Timestamp(design_end) - pd.Timestamp(start)) / 2
    halves = [("H1", pd.Timestamp(start), mid), ("H2", mid + pd.Timedelta(days=1), pd.Timestamp(design_end))]

    design["abs_rs20"] = design["rs20"].abs()
    design["near_extreme"] = -np.minimum(design["dist_high"].abs(), design["dist_low"].abs())
    design["abs_ret"] = design["ret"].abs()
    design["nr7_vol"] = np.where(design["nr7"], design["vol_exp"], np.nan)
    for col in ("vol_exp", "deliv_ratio", "abs_rs20", "near_extreme"):
        design[f"pr_{col}"] = sel.percentile_rank(design, col)
    design["l1_score"] = design[["pr_vol_exp", "pr_deliv_ratio", "pr_abs_rs20", "pr_near_extreme"]].mean(axis=1,
                                                                                                         skipna=False)
    selectors = {
        "universe (all)": universe,
        f"baseline: top {top} ATR%": sel.pick_top(design, "atr_pct", top),
        f"baseline: top {top} turnover": sel.pick_top(design, "median_turnover", top),
        f"baseline: top {top} |today's move|": sel.pick_top(design, "abs_ret", top),
        f"volume expansion (5d/50d)": sel.pick_top(design, "vol_exp", top),
        f"delivery % vs 20d": sel.pick_top(design, "deliv_ratio", top),
        f"|relative strength 20d|": sel.pick_top(design, "abs_rs20", top),
        f"near 52-week high or low": sel.pick_top(design, "near_extreme", top),
        f"NR7 day, by volume expansion": sel.pick_top(design, "nr7_vol", top),
        f"L1 composite (4 ranks)": sel.pick_top(design, "l1_score", top),
    }
    long_list = sel.pick_top(design[design["trend_up"]], "rs20", top)
    short_list = sel.pick_top(design[design["trend_down"]], "rs20", top, ascending=True)
    lists = {"long: trend up, strongest rs20": (long_list, 1), "short: trend down, weakest rs20": (short_list, -1)}

    # Iteration 2, added AFTER reading iteration 1: only "today's move" beat
    # the ATR baseline on the /ATR columns, so these ask the same question
    # with today's shock measured in the stock's own units.
    iteration2 = {
        "universe (all)": universe,
        f"baseline: top {top} ATR%": selectors[f"baseline: top {top} ATR%"],
        f"today's |move| in ATRs": sel.pick_top(design, "move_atr", top),
        f"today's true range in ATRs": sel.pick_top(design, "range_atr", top),
        f"today's volume ÷ 50d average": sel.pick_top(design, "vol_shock", top),
        f"in-play score (move in ATRs + volume shock)": sel.pick_top(design, "inplay_score", top),
    }
    # Iteration 3, declared before running it: size of move (ATR% level) and
    # "moving more than usual" (today's shock) together. Among the 200 most
    # volatile universe stocks, the top by in-play score.
    iteration2[f"iteration 3: top {top} in-play among the 200 highest ATR%"] = sel.inplay_among_volatile(design, top)
    drift = universe.groupby("trade_date")["n_move"].agg(["mean", "median"])
    drift_lines = ["| half | dates | mean of daily mean move % | t (dates) | mean of daily median % | days negative |",
                   "|---|---|---|---|---|---|"]
    for label, lo, hi in halves:
        part = drift[(drift.index >= lo) & (drift.index <= hi)]
        t = part["mean"].mean() / (part["mean"].std(ddof=1) / math.sqrt(len(part)))
        drift_lines.append(f"| {label} | {len(part)} | {part['mean'].mean():+.3f} | {t:+.1f} | "
                           f"{part['median'].mean():+.3f} | {(part['mean'] < 0).mean() * 100:.0f}% |")

    sizes = universe.groupby("trade_date").size()
    md = [
        "# R1: selection power of nightly watchlists (design period)", "",
        f"Picks made on dates {start} to {design_end}; outcomes are the next NSE session. Holdout dates after "
        f"{design_end} are not analysed. Features use a 400-day warm-up before {start}.", "",
        f"Universe: NSE series EQ, close ≥ ₹50, ≥ 60 sessions listed, 20-session median turnover ≥ ₹10 crore. "
        f"Stocks per date: median {int(sizes.median())}, min {int(sizes.min())}, max {int(sizes.max())} "
        f"({sizes.index.nunique()} dates).", "",
        "## How to read", "",
        f"Each selector takes the top {top} universe stocks per date. For each date the median outcome across the picks "
        "is taken, then averaged over dates. The number in brackets is the average of (selector ÷ universe) per "
        "date: 1.00× means no better than a random universe stock.", "",
        "* range % = next-day (high − low) / open; one-way % = |close − open| / open; best % = the larger of "
        "(high − open) and (open − low).",
        "* /ATR columns divide by the stock's own 14-day ATR% at the pick date: they ask whether the stock moves "
        "more THAN USUAL, which picking volatile stocks cannot fake.",
        "* efficiency = |close − open| / (high − low): 1 is a clean one-way day, 0 a round trip.", "",
        "Blueprint gate R1: range ≥ 1.5× and one-way ≥ 1.3× the universe in both halves, and better than the ATR "
        "baseline on the /ATR columns.", "",
        "## Selection power", "", _ratio_table(selectors, universe, halves), "",
        "## Direction", "",
        "Signed next-day open-to-close move of a long list and a short list, and the excess over the universe's mean "
        "move that day (so a market-wide up day does not count). The t-statistic treats each date as one "
        "observation.", "",
        _direction_table(lists, universe, halves), "",
        "## Iteration 2: today's shock in the stock's own units (added after reading the tables above)", "",
        "Selectors tried so far: 10 in iteration 1, 4 in iteration 2, 1 in iteration 3 (the last row). The gate is unchanged.", "",
        _ratio_table(iteration2, universe, halves), "",
        "## Open-to-close drift of the universe", "",
        "The average universe stock's next-day move from open to close, before any cost. A negative drift is a "
        "headwind for intraday longs and a tailwind for intraday shorts.", "",
        "\n".join(drift_lines), "",
    ]
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "stock-picker-r1-selection-power.md")
    with open(path, "w") as fh:
        fh.write("\n".join(md))
    return path


# --------------------------------------------------------------------- R2/R3 --

def _load_stock_bars(root: str, symbol: str) -> Optional[pd.DataFrame]:
    folder = os.path.join(root, "raw", symbol)
    if not os.path.isdir(folder):
        return None
    parts = [pd.read_csv(os.path.join(folder, f)) for f in sorted(os.listdir(folder)) if f.endswith(".csv.gz")]
    if not parts:
        return None
    return pd.concat(parts, ignore_index=True).drop_duplicates("bar_start_ist")


def _rvol15(sessions: Dict[str, "st.SessionBars"]) -> Dict[str, float]:
    """First-15-minute volume over the average of the previous 20 sessions that had a full opening range."""
    days = sorted(sessions)
    vol = pd.Series([sessions[d].volume[:st.OR_BARS].sum() if sessions[d].times[:st.OR_BARS] == ["09:15", "09:20", "09:25"]
                     else np.nan for d in days], index=days)
    base = vol.shift(1).rolling(20, min_periods=10).mean()
    return (vol / base).replace([np.inf, -np.inf], np.nan).to_dict()


def _half_of(d: pd.Timestamp, start: str, design_end: str) -> str:
    mid = pd.Timestamp(start) + (pd.Timestamp(design_end) - pd.Timestamp(start)) / 2
    return "H1" if d <= mid else "H2"


def collect(eod_root: str, intraday_root: str, start: str, design_end: str, top: int, log=print):
    warmup_start = (date.fromisoformat(start) - timedelta(days=400)).isoformat()
    read_end = (date.fromisoformat(design_end) + timedelta(days=10)).isoformat()
    raw = sel.load_nse(eod_root, warmup_start, read_end)
    feats = sel.add_next_day(sel.add_inplay_score(sel.add_market_relative(sel.add_features(raw[raw["series"] == "EQ"]))))
    design = feats[(feats["trade_date"] >= start) & (feats["trade_date"] <= design_end) & feats["next_date"].notna()]
    picks = sel.inplay_among_volatile(design, top)
    pick_keys = set(zip(picks["symbol"], picks["trade_date"]))
    uni = design[design["in_universe"]]
    contexts: Dict[str, Dict[str, dict]] = {}
    for row in uni[["symbol", "trade_date", "next_date", "close", "high", "low", "atr_pct"]].itertuples(index=False):
        contexts.setdefault(row.symbol, {})[row.next_date.strftime("%Y-%m-%d")] = {
            "pick_date": row.trade_date, "prev_close": row.close, "prev_high": row.high, "prev_low": row.low,
            "atr_pct": row.atr_pct, "is_pick": (row.symbol, row.trade_date) in pick_keys,
            "half": _half_of(row.trade_date, start, design_end)}
    facts_rows: List[dict] = []
    candidates: List["st.Candidate"] = []
    tags: Dict[Tuple[str, str], dict] = {}
    missing = []
    symbols = sorted(contexts)
    for k, symbol in enumerate(symbols, 1):
        if k % 200 == 0 or k == len(symbols):
            log(f"{k}/{len(symbols)} stocks, {len(candidates)} candidates so far")
        frame = _load_stock_bars(intraday_root, symbol)
        if frame is None:
            missing.append(symbol)
            continue
        sessions = st.sessions_from_frame(symbol, frame)
        rvol = _rvol15(sessions)
        for day, c in contexts[symbol].items():
            bars = sessions.get(day)
            if bars is None:
                continue
            tags[(symbol, day)] = c
            facts = st.session_facts(bars)
            if facts is not None:
                facts_rows.append({"symbol": symbol, "day": day, **c, **facts, "rvol15": rvol.get(day)})
            ctx = st.Context(c["prev_close"], c["prev_high"], c["prev_low"], c["atr_pct"], rvol.get(day))
            for name, fn in st.SETUPS.items():
                cand = fn(bars, ctx)
                if cand is not None:
                    cand.priority = float(rvol.get(day) or 0.0) if not pd.isna(rvol.get(day)) else 0.0
                    candidates.append(st.precompute_exit(bars, cand))
    return pd.DataFrame(facts_rows), candidates, tags, missing, len(pick_keys)


def _r2_table(facts: pd.DataFrame) -> str:
    f = facts.copy()
    f["gap_atr"] = ((f["open"] / f["prev_close"] - 1.0) * 100.0).abs() / f["atr_pct"]
    for col in ("rest_range", "rest_oneway", "rest_best"):
        f[f"{col}_atr"] = f[col] / f["atr_pct"]
    cols = ["rest_range", "rest_oneway", "rest_best", "rest_range_atr", "rest_oneway_atr", "rest_best_atr"]
    per_day_uni = f.groupby("day")[cols].median()

    def top_by(frame, col, n):
        return frame.dropna(subset=[col]).sort_values(["day", col], ascending=[True, False]).groupby("day").head(n)

    picks = f[f["is_pick"]]
    groups = {
        "universe (all)": f,
        "nightly picks (50)": picks,
        "picks: top 10 by 09:30 relative volume": top_by(picks, "rvol15", 10),
        "universe: top 10 by 09:30 relative volume": top_by(f, "rvol15", 10),
        "universe: top 10 by |gap| in ATRs": top_by(f, "gap_atr", 10),
    }
    lines = ["| group | half | days | range % | one-way % | best % | range/ATR | one-way/ATR | best/ATR |",
             "|---|---|---|---|---|---|---|---|---|"]
    for name, g in groups.items():
        per = g.groupby("day")[cols].median()
        half = g.groupby("day")["half"].first()
        for label in ("H1", "H2"):
            days = half[half == label].index
            p, u = per.loc[days], per_day_uni.loc[days]
            cells = [f"{p[c].mean():.2f} ({(p[c] / u[c]).mean():.2f}×)" for c in cols]
            lines.append(f"| {name} | {label} | {len(days)} | " + " | ".join(cells) + " |")
    drift = ["| group | half | stock-days | mean move 09:30→15:15 % | median % |", "|---|---|---|---|---|"]
    for name in ("universe (all)", "nightly picks (50)"):
        g = groups[name]
        for label in ("H1", "H2"):
            part = g[g["half"] == label]["rest_move"]
            drift.append(f"| {name} | {label} | {len(part)} | {part.mean():+.3f} | {part.median():+.3f} |")
    return "\n".join(lines) + "\n\n" + "\n".join(drift)


def _r3_table(candidates, tags, book: "st.Portfolio") -> Tuple[str, int]:
    rows = ["| setup | pool | side | slippage | half | trades | net ₹ | gross ₹ | charges ₹ | win % | PF | exp R | max DD ₹ | net w/o best 5 ₹ | day t |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    configs = 0
    for setup in st.SETUPS:
        for pool in ("picks", "universe"):
            pool_c = [c for c in candidates if c.setup == setup and (pool == "universe" or tags[(c.symbol, c.day)]["is_pick"])]
            for side_name, sides in (("long", (1,)), ("short", (-1,)), ("both", (1, -1))):
                for slip in (0.03, 0.10):
                    configs += 1
                    for label in ("H1", "H2"):
                        half_c = [c for c in pool_c if tags[(c.symbol, c.day)]["half"] == label]
                        trades = st.run_portfolio(half_c, None, book, st.EquityCosts(slippage_pct=slip), sides)
                        s = st.summarize(trades)
                        if not s.get("trades"):
                            rows.append(f"| {setup} | {pool} | {side_name} | {slip:.2f}% | {label} | 0 | | | | | | | | | |")
                            continue
                        rows.append(f"| {setup} | {pool} | {side_name} | {slip:.2f}% | {label} | {s['trades']} | "
                                    f"{s['net']:,.0f} | {s['gross']:,.0f} | {s['charges']:,.0f} | {s['win_rate'] * 100:.0f} | "
                                    f"{s['profit_factor']:.2f} | {s['expectancy_r']:+.3f} | {s['max_drawdown']:,.0f} | "
                                    f"{s['net_without_best5']:,.0f} | {s['day_t']:+.1f} |")
    return "\n".join(rows), configs


def run_r23(eod_root: str, intraday_root: str, start: str, design_end: str, top: int, out_dir: str) -> str:
    facts, candidates, tags, missing, pick_count = collect(eod_root, intraday_root, start, design_end, top)
    book = st.Portfolio()
    r3, configs = _r3_table(candidates, tags, book)
    by_setup = pd.Series([c.setup for c in candidates]).value_counts().to_dict()
    md = [
        "# R2 and R3: the rest of the day, and the setups (design period)", "",
        f"Pick dates {start} to {design_end}; trades on the next session. Holdout sessions after that are not "
        "analysed. Intraday bars: Dhan 5-minute. Universe and nightly picks as in R1 (picks = iteration 3: top "
        f"{top} in-play among the 200 highest ATR%).", "",
        "Relative volume at 09:30 needs 10 earlier sessions of intraday bars, which start on 2024-09-01, so the "
        "first two to four weeks have no ORB candidates.", "",
        f"Stock-days with a full 09:15–15:15 session: {len(facts):,}; nightly pick stock-days: {pick_count:,}. "
        f"Universe stocks with no intraday file: {len(missing)} ({', '.join(missing[:15])}{'…' if len(missing) > 15 else ''}).", "",
        "## R2: what 09:30 says about 09:30 → 15:15", "",
        "Each group's median per day, averaged over days; brackets are the per-day ratio to the universe. "
        "/ATR divides by the stock's daily ATR% at the pick date.", "",
        _r2_table(facts), "",
        "## R3: setups with costs and portfolio limits", "",
        f"Portfolio: capital ₹{book.capital:,.0f}, risk {book.risk_pct}% a trade (₹{book.capital * book.risk_pct / 100:,.0f}), "
        f"at most {book.max_open} open positions, no new entry after a day's realised loss of {book.daily_loss_pct}%, "
        "position value capped at 1× capital, one trade per stock a day; same-bar entries ordered by 09:30 relative "
        "volume. Charges per blueprint section 2; slippage each side as shown.", "",
        f"Candidates before the portfolio pass: {by_setup}. Configurations reported: {configs} "
        "(3 setups × 2 pools × 3 side choices × 2 slippages), each split into halves. None was tuned.", "",
        "Gate R3: net profit and profit factor ≥ 1.2 in both halves, still positive without the best 5 trades and "
        "at 0.10% slippage; then the holdout once.", "",
        r3, "",
    ]
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "stock-picker-r2-r3.md")
    with open(path, "w") as fh:
        fh.write("\n".join(md))
    return path


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("r1", "r23"))
    parser.add_argument("--root", default=bhavcopy.DEFAULT_ROOT)
    parser.add_argument("--intraday-root", default=intraday.DEFAULT_ROOT)
    parser.add_argument("--from", dest="date_from", default="2024-09-01")
    parser.add_argument("--design-end", default="2025-12-31")
    parser.add_argument("--top", type=int, default=50)
    parser.add_argument("--out", default=os.path.join(str(REPO_ROOT), "private", "research"))
    args = parser.parse_args(argv)
    if args.command == "r1":
        print(run_r1(args.root, args.date_from, args.design_end, args.top, args.out))
    else:
        print(run_r23(args.root, args.intraday_root, args.date_from, args.design_end, args.top, args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
