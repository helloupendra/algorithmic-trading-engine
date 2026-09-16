"""
research/equities/setups.py

R2 and R3 of the stock-picker blueprint on Dhan 5-minute bars.

R2  per stock and session, what was known at 09:30 (gap, opening-range width,
    first-15-minute move, relative volume) and what happened from 09:30 to
    15:15 (range, one-way move, best excursion).
R3  the blueprint's setups, one stock-session at a time, then a portfolio pass
    that applies capital, risk per trade, open-position and daily-loss limits
    in time order across stocks.

Fill rules (the same discipline as the options research):
  * A signal is decided at a 5-minute bar's CLOSE and fills at the NEXT bar's
    open, plus slippage.
  * Resting exits (stop, target) fill at their level inside a bar, or at the
    bar's open when it opens through them. When one bar touches both, the stop
    is assumed first.
  * The last entry signal is the bar starting 14:25 (closing 14:30). Anything
    open is closed at the close of the bar starting 15:10.
  * Charges are computed on the filled turnover of each side (section 2 of the
    blueprint), slippage separately.
"""

from __future__ import annotations

import math
from dataclasses import asdict, dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import numpy as np
import pandas as pd

OR_BARS = 3                 # 09:15, 09:20, 09:25
LAST_SIGNAL = "14:25"
EXIT_BAR = "15:10"


# ------------------------------------------------------------------ costs --

@dataclass(frozen=True)
class EquityCosts:
    """Intraday NSE cash equity, approximate 2026 rates (blueprint section 2)."""
    brokerage_pct: float = 0.03
    brokerage_cap: float = 20.0
    stt_sell_pct: float = 0.025
    exchange_pct: float = 0.00297
    sebi_per_crore: float = 10.0
    stamp_buy_pct: float = 0.003
    gst_pct: float = 18.0
    slippage_pct: float = 0.03

    def fill(self, price: float, buying: bool) -> float:
        slip = price * self.slippage_pct / 100.0
        return price + slip if buying else price - slip

    def charges(self, buy_value: float, sell_value: float) -> float:
        brokerage = min(buy_value * self.brokerage_pct / 100.0, self.brokerage_cap) + \
            min(sell_value * self.brokerage_pct / 100.0, self.brokerage_cap)
        turnover = buy_value + sell_value
        exchange = turnover * self.exchange_pct / 100.0
        sebi = turnover * self.sebi_per_crore / 1e7
        stt = sell_value * self.stt_sell_pct / 100.0
        stamp = buy_value * self.stamp_buy_pct / 100.0
        gst = (brokerage + exchange + sebi) * self.gst_pct / 100.0
        return brokerage + exchange + sebi + stt + stamp + gst


# ------------------------------------------------------------- session bars --

@dataclass
class SessionBars:
    symbol: str
    day: str
    times: List[str]
    open: np.ndarray
    high: np.ndarray
    low: np.ndarray
    close: np.ndarray
    volume: np.ndarray

    def index_of(self, hhmm: str) -> Optional[int]:
        try:
            return self.times.index(hhmm)
        except ValueError:
            return None


def sessions_from_frame(symbol: str, frame: pd.DataFrame) -> Dict[str, SessionBars]:
    """A stock's bars (columns bar_start_ist, open, high, low, close, volume) -> one SessionBars per date."""
    out = {}
    stamps = frame["bar_start_ist"].astype(str)
    for day, part in frame.assign(_day=stamps.str[:10], _time=stamps.str[11:16]).groupby("_day", sort=True):
        part = part.sort_values("_time")
        out[day] = SessionBars(symbol, day, list(part["_time"]), part["open"].to_numpy(float),
                               part["high"].to_numpy(float), part["low"].to_numpy(float),
                               part["close"].to_numpy(float), part["volume"].to_numpy(float))
    return out


# --------------------------------------------------------------------- R2 --

def session_facts(bars: SessionBars) -> Optional[Dict[str, float]]:
    """
    What was known at 09:30 and what happened from 09:30 to 15:15. None when
    the session lacks its opening range, a 09:30 bar or the 15:10 bar.
    """
    if len(bars.times) < OR_BARS + 1 or bars.times[:OR_BARS] != ["09:15", "09:20", "09:25"]:
        return None
    i930 = bars.index_of("09:30")
    iexit = bars.index_of(EXIT_BAR)
    if i930 is None or iexit is None or iexit <= i930:
        return None
    p = bars.open[i930]
    rest_hi = bars.high[i930:iexit + 1].max()
    rest_lo = bars.low[i930:iexit + 1].min()
    end = bars.close[iexit]
    return {
        "open": bars.open[0], "or_high": bars.high[:OR_BARS].max(), "or_low": bars.low[:OR_BARS].min(),
        "close_0925": bars.close[OR_BARS - 1], "vol15": bars.volume[:OR_BARS].sum(), "price_0930": p,
        "rest_range": (rest_hi - rest_lo) / p * 100.0, "rest_move": (end - p) / p * 100.0,
        "rest_oneway": abs(end - p) / p * 100.0, "rest_best": max(rest_hi - p, p - rest_lo) / p * 100.0,
    }


# --------------------------------------------------------------------- R3 --

@dataclass
class Context:
    """Facts from the previous session (bhavcopy) and the 09:30 relative volume."""
    prev_close: float
    prev_high: float
    prev_low: float
    atr_pct: float
    rvol15: Optional[float] = None


@dataclass
class Candidate:
    """A setup's trade before portfolio sizing: prices and times only."""
    symbol: str
    day: str
    setup: str
    side: int                  # +1 long, -1 short
    signal_time: str
    entry_time: str
    entry_i: int
    entry_price: float         # bar open, before slippage
    stop: float
    target: Optional[float]
    trail: bool = False
    #: Tie-break when several candidates enter on the same bar: higher first.
    #: Set from relative volume, so the portfolio never prefers a stock for
    #: its name.
    priority: float = 0.0
    #: Filled by `precompute_exit` so the portfolio pass needs no bars.
    exit_i: Optional[int] = None
    exit_time: Optional[str] = None
    raw_exit: Optional[float] = None
    exit_reason: Optional[str] = None


@dataclass
class Trade:
    symbol: str
    day: str
    setup: str
    side: int
    signal_time: str
    entry_time: str
    exit_time: str
    entry_fill: float
    exit_fill: float
    stop: float
    target: Optional[float]
    qty: int
    reason: str
    gross: float
    charges: float
    net: float
    risk: float
    r: float

    def to_dict(self) -> Dict[str, object]:
        return asdict(self)


def _signal_window(bars: SessionBars) -> range:
    last = bars.index_of(LAST_SIGNAL)
    stop_at = (last + 1) if last is not None else len(bars.times) - 1
    return range(OR_BARS, min(stop_at, len(bars.times) - 1))


def orb(bars: SessionBars, ctx: Context, min_rvol: float = 2.0, max_stop_pct: float = 3.0,
        reward: float = 2.0) -> Optional[Candidate]:
    """Opening range breakout: first 5-minute close beyond the 09:15-09:30 range, relative volume >= min_rvol."""
    if len(bars.times) < OR_BARS + 2 or bars.times[:OR_BARS] != ["09:15", "09:20", "09:25"]:
        return None
    if ctx.rvol15 is None or not ctx.rvol15 >= min_rvol:
        return None
    hi, lo = bars.high[:OR_BARS].max(), bars.low[:OR_BARS].min()
    if hi <= lo:
        return None
    for i in _signal_window(bars):
        side = 1 if bars.close[i] > hi else -1 if bars.close[i] < lo else 0
        if side == 0:
            continue
        entry = bars.open[i + 1]
        stop = lo if side == 1 else hi
        distance = (entry - stop) * side
        if distance <= 0 or distance / entry * 100.0 > max_stop_pct:
            return None
        return Candidate(bars.symbol, bars.day, "orb", side, bars.times[i], bars.times[i + 1], i + 1, entry, stop,
                         entry + side * reward * distance)
    return None


def prev_day_break(bars: SessionBars, ctx: Context, atr_buffer: float = 0.3, max_stop_pct: float = 3.0,
                   reward: float = 2.0) -> Optional[Candidate]:
    """First 5-minute close above yesterday's high (below yesterday's low) after 09:30, having opened inside."""
    if len(bars.times) < OR_BARS + 2:
        return None
    opened = bars.open[0]
    atr_price = ctx.atr_pct / 100.0 * ctx.prev_close
    for i in _signal_window(bars):
        if opened < ctx.prev_high and bars.close[i] > ctx.prev_high:
            side, level = 1, ctx.prev_high
        elif opened > ctx.prev_low and bars.close[i] < ctx.prev_low:
            side, level = -1, ctx.prev_low
        else:
            continue
        entry = bars.open[i + 1]
        stop = level - side * atr_buffer * atr_price
        distance = (entry - stop) * side
        if distance <= 0 or distance / entry * 100.0 > max_stop_pct:
            return None
        return Candidate(bars.symbol, bars.day, "prev_day_break", side, bars.times[i], bars.times[i + 1], i + 1,
                         entry, stop, entry + side * reward * distance)
    return None


def gap_drive(bars: SessionBars, ctx: Context, min_gap_pct: float = 1.5) -> Optional[Candidate]:
    """A gap of at least min_gap_pct that does not fill half of itself by 09:30: enter at 09:30, trail 15-minute lows."""
    if len(bars.times) < OR_BARS + 2 or bars.times[:OR_BARS] != ["09:15", "09:20", "09:25"]:
        return None
    gap = (bars.open[0] / ctx.prev_close - 1.0) * 100.0
    side = 1 if gap >= min_gap_pct else -1 if gap <= -min_gap_pct else 0
    if side == 0:
        return None
    half = ctx.prev_close + (bars.open[0] - ctx.prev_close) / 2.0
    held = bars.low[:OR_BARS].min() > half if side == 1 else bars.high[:OR_BARS].max() < half
    if not held or bars.times[OR_BARS] != "09:30":
        return None
    return Candidate(bars.symbol, bars.day, "gap_drive", side, "09:25", "09:30", OR_BARS, bars.open[OR_BARS], half,
                     None, trail=True)


SETUPS = {"orb": orb, "prev_day_break": prev_day_break, "gap_drive": gap_drive}


def walk(bars: SessionBars, c: Candidate) -> Tuple[int, float, str]:
    """(exit bar index, raw exit price, reason) for a candidate, bar by bar from its entry bar."""
    iexit = bars.index_of(EXIT_BAR)
    last = iexit if iexit is not None else len(bars.times) - 1
    stop = c.stop
    side = c.side
    for i in range(c.entry_i, last + 1):
        o, h, l = bars.open[i], bars.high[i], bars.low[i]
        # Stop first (also when the same bar reaches the target).
        if side == 1 and l <= stop:
            return i, (o if o <= stop else stop), "stop"
        if side == -1 and h >= stop:
            return i, (o if o >= stop else stop), "stop"
        if c.target is not None:
            if side == 1 and h >= c.target:
                return i, (o if o >= c.target else c.target), "target"
            if side == -1 and l <= c.target:
                return i, (o if o <= c.target else c.target), "target"
        if c.trail and i >= 2:
            # Trail under (over) the last three completed bars, never loosening.
            window = slice(max(0, i - 2), i + 1)
            level = bars.low[window].min() if side == 1 else bars.high[window].max()
            stop = max(stop, level) if side == 1 else min(stop, level)
    return last, bars.close[last], "time"


def precompute_exit(bars: SessionBars, c: Candidate) -> Candidate:
    """The exit does not depend on quantity: work it out once, while the bars are loaded."""
    c.exit_i, c.raw_exit, c.exit_reason = walk(bars, c)
    c.exit_time = bars.times[c.exit_i]
    return c


def settle(bars: Optional[SessionBars], c: Candidate, qty: int, costs: EquityCosts, risk_rupees: float) -> Trade:
    if c.exit_time is None:
        precompute_exit(bars, c)
    raw_exit, reason, exit_time = c.raw_exit, c.exit_reason, c.exit_time
    buying_first = c.side == 1
    entry_fill = costs.fill(c.entry_price, buying=buying_first)
    exit_fill = costs.fill(raw_exit, buying=not buying_first)
    entry_value, exit_value = entry_fill * qty, exit_fill * qty
    buy_value, sell_value = (entry_value, exit_value) if buying_first else (exit_value, entry_value)
    gross = (exit_fill - entry_fill) * qty * c.side
    charges = costs.charges(buy_value, sell_value)
    net = gross - charges
    return Trade(c.symbol, c.day, c.setup, c.side, c.signal_time, c.entry_time, exit_time, entry_fill, exit_fill,
                 c.stop, c.target, qty, reason, gross, charges, net, risk_rupees,
                 net / risk_rupees if risk_rupees > 0 else float("nan"))


@dataclass(frozen=True)
class Portfolio:
    capital: float = 500_000.0
    risk_pct: float = 0.5
    max_open: int = 3
    daily_loss_pct: float = 1.5
    max_position_pct: float = 100.0       # position value cap as % of capital (1x, no leverage)


def size(c: Candidate, book: Portfolio) -> Tuple[int, float]:
    risk = book.capital * book.risk_pct / 100.0
    distance = abs(c.entry_price - c.stop)
    if distance <= 0:
        return 0, 0.0
    qty = int(risk // distance)
    qty = min(qty, int(book.capital * book.max_position_pct / 100.0 // c.entry_price))
    return qty, qty * distance


def run_portfolio(candidates: Sequence[Candidate], bars_of: Optional[Dict[Tuple[str, str], SessionBars]], book: Portfolio,
                  costs: EquityCosts, allowed_sides: Iterable[int] = (1, -1)) -> List[Trade]:
    """
    Candidates of one or more setups, all days: taken in entry-time order per
    day while fewer than `max_open` positions are open, one trade per stock a
    day, and no new entry once the day's realised loss reaches the limit.
    """
    sides = set(allowed_sides)
    trades: List[Trade] = []
    by_day: Dict[str, List[Candidate]] = {}
    for c in candidates:
        if c.side in sides:
            by_day.setdefault(c.day, []).append(c)
    for day in sorted(by_day):
        open_trades: List[Trade] = []
        realised = 0.0
        traded = set()
        for c in sorted(by_day[day], key=lambda x: (x.entry_time, -x.priority, x.symbol)):
            still_open = []
            for t in open_trades:
                if t.exit_time < c.entry_time:
                    realised += t.net
                else:
                    still_open.append(t)
            open_trades = still_open
            if c.symbol in traded or len(open_trades) >= book.max_open:
                continue
            if realised <= -book.capital * book.daily_loss_pct / 100.0:
                continue
            qty, risk = size(c, book)
            if qty < 1:
                continue
            t = settle(bars_of[(c.symbol, c.day)] if c.exit_time is None else None, c, qty, costs, risk)
            trades.append(t)
            open_trades.append(t)
            traded.add(c.symbol)
    return trades


def summarize(trades: Sequence[Trade]) -> Dict[str, float]:
    if not trades:
        return {"trades": 0}
    net = np.array([t.net for t in trades])
    wins, losses = net[net > 0], net[net <= 0]
    by_day = pd.Series(net, index=[t.day for t in trades]).groupby(level=0).sum()
    equity = by_day.cumsum()
    drawdown = (equity - equity.cummax()).min()
    ordered = np.sort(net)
    return {
        "trades": len(trades), "days": int(by_day.size), "net": float(net.sum()),
        "gross": float(sum(t.gross for t in trades)), "charges": float(sum(t.charges for t in trades)),
        "win_rate": float((net > 0).mean()), "profit_factor": float(wins.sum() / -losses.sum()) if losses.sum() < 0 else float("inf"),
        "expectancy_r": float(np.nanmean([t.r for t in trades])), "max_drawdown": float(drawdown),
        "net_without_best5": float(ordered[:-5].sum()) if len(ordered) > 5 else float("nan"),
        "day_t": float(by_day.mean() / (by_day.std(ddof=1) / math.sqrt(len(by_day)))) if len(by_day) > 2 else float("nan"),
    }
