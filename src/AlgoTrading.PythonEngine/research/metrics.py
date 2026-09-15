"""
research/metrics.py

Trade-level statistics and breakdowns. Pure functions over `Trade` objects (or
anything with `net_pnl`, `r_multiple`, `exit_utc`, `bars_held`).
"""

from __future__ import annotations

from collections import OrderedDict
from typing import Any, Callable, Dict, List, Optional, Sequence

WEEKDAYS = ("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")

#: Time-of-day buckets by the signal's IST clock: the opening hour, the
#: late morning, lunch, and the afternoon run to the square-off.
TIME_BUCKETS = (("09:15-10:00", "09:15", "10:00"), ("10:00-11:30", "10:00", "11:30"),
                ("11:30-13:30", "11:30", "13:30"), ("13:30-15:30", "13:30", "15:30"))


def summarize(trades: Sequence[Any]) -> Dict[str, Any]:
    """
    count, win rate, average win/loss (Rs), expectancy per trade in Rs and in R,
    profit factor, net/gross/charges, max drawdown of the closed-trade equity
    curve (from a zero start), longest losing streak.

    A trade that nets exactly zero counts as neither a win nor a loss. Profit
    factor is None when there are no losing rupees (it would be infinite).
    """
    count = len(trades)
    nets = [float(t.net_pnl) for t in trades]
    wins = [x for x in nets if x > 0]
    losses = [x for x in nets if x < 0]
    rs = [float(t.r_multiple) for t in trades if t.r_multiple is not None]

    peak = equity = 0.0
    max_dd = 0.0
    streak = longest = 0
    for x in nets:
        equity += x
        peak = max(peak, equity)
        max_dd = max(max_dd, peak - equity)
        if x < 0:
            streak += 1
            longest = max(longest, streak)
        else:
            streak = 0

    gross_win, gross_loss = sum(wins), -sum(losses)
    return {
        "trades": count,
        "wins": len(wins),
        "losses": len(losses),
        "win_rate": (len(wins) / count) if count else None,
        "avg_win": (gross_win / len(wins)) if wins else None,
        "avg_loss": (-gross_loss / len(losses)) if losses else None,
        "expectancy": (sum(nets) / count) if count else None,
        "expectancy_r": (sum(rs) / len(rs)) if rs else None,
        "profit_factor": (gross_win / gross_loss) if gross_loss > 0 else None,
        "net": sum(nets),
        "gross": sum(float(getattr(t, "gross_pnl", 0.0)) for t in trades),
        "charges": sum(float(getattr(t, "charges", 0.0)) for t in trades),
        "max_drawdown": max_dd,
        "longest_losing_streak": longest,
        "avg_bars_held": (sum(t.bars_held for t in trades) / count) if count else None,
        "best": max(nets) if nets else None,
        "worst": min(nets) if nets else None,
        "stale_exits": sum(1 for t in trades if getattr(t, "stale_exit", False)),
    }


def equity_curve(trades: Sequence[Any]) -> List[Dict[str, Any]]:
    """Cumulative net P&L after each trade, in exit order."""
    total = 0.0
    out = []
    for t in sorted(trades, key=lambda t: t.exit_utc):
        total += float(t.net_pnl)
        out.append({"at": t.exit_utc, "equity": total})
    return out


def time_bucket(hhmm: str) -> str:
    for name, lo, hi in TIME_BUCKETS:
        if lo <= hhmm < hi:
            return name
    return "other"


def breakdown(trades: Sequence[Any], key: Callable[[Any], str],
              order: Optional[Sequence[str]] = None) -> List[Dict[str, Any]]:
    """`summarize` per group, groups in `order` first then first-seen order."""
    groups: "OrderedDict[str, List[Any]]" = OrderedDict()
    for name in order or ():
        groups[name] = []
    for t in trades:
        groups.setdefault(key(t), []).append(t)
    rows = []
    for name, members in groups.items():
        if not members and order is not None:
            continue
        row = {"group": name}
        row.update(summarize(members))
        rows.append(row)
    return rows


def standard_breakdowns(trades: Sequence[Any]) -> Dict[str, List[Dict[str, Any]]]:
    from research.regime import LABELS

    return {
        "regime": breakdown(trades, lambda t: t.regime_at_signal or "NOT_READY", order=LABELS),
        "weekday": breakdown(trades, lambda t: t.weekday, order=WEEKDAYS),
        "time_of_day": breakdown(trades, lambda t: time_bucket(t.signal_ist), order=[b[0] for b in TIME_BUCKETS]),
        "month": sorted(breakdown(trades, lambda t: t.session[:7]), key=lambda r: r["group"]),
        "side": breakdown(trades, lambda t: t.side, order=("CE", "PE")),
        "exit_reason": breakdown(trades, lambda t: t.exit_reason),
    }
