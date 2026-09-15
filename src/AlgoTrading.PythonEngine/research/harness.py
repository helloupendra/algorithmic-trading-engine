"""
research/harness.py

Simulate a research candidate on index bars with REAL option premiums, and
evaluate it walk-forward.

Fill rules
----------
  * Signals are decided at a bar's CLOSE. The entry fills at the NEXT bar's
    OPEN, at the premium of the contract that was ATM+offset at that next bar
    (`option_history_bars`, rolling ATM). A signal on a session's last bar, or
    whose fill bar would start at/after `last_entry_ist`, is not taken.
  * The contract is FIXED at entry: its strike (and expiry date when the table
    has one) is recorded and every later bar looks up the row whose Strike is
    that strike, whatever offset it now sits at (`PremiumBook.at_strike`). The
    table only stores ATM +/- N rows, so when spot moves more than N strikes
    the held contract is simply absent from that bar. Then:
      - no resting order can be checked on that bar (its high/low are unknown),
        and the bar is counted in `unpriced_bars`;
      - an exit decided at a close waits for the next bar where the contract is
        priced again, and fills at that bar's open;
      - if the session's data ends while it is still unpriced, the trade closes
        at its LAST KNOWN premium and is flagged `stale_exit`. The report counts
        these; they are never silently mixed in.
    Trades are intraday (forced exit 15:15), so ExpiryCode 1 names the same
    contract for the whole trade - the expiry roll happens between sessions.
  * Resting exits (premium stop, trailing stop, target) fill inside the bar at
    their level, or at the open when the open is already through it. When a
    bar touches both the stop and the target, the stop is assumed first. The
    trail level used on a bar comes from earlier bars' highs only; the bar's
    own high updates it afterwards.
  * Close-decided exits (index ATR stop, time stop, regime flip, 15:15) fill at
    the next bar's open. A session whose data ends before 15:15 closes at the
    last bar's close (`SESSION_END`).
  * Premiums are never estimated from the index. No premium at the fill bar
    means no trade, recorded in `skipped` with the reason.

Costs: every fill pays slippage (`CostModel.buy_fill` / `sell_fill`) and every
round trip pays brokerage and statutory charges on the filled turnover.

R: the risk unit is the premium stop (entry fill x stop %) x quantity; a
candidate without a premium stop uses `risk_unit_pct` of the entry premium
(30% by default) so R stays comparable across candidates.

Walk-forward: rolling calendar windows (default 3 months train, 1 month test,
stepping 1 month). The candidate's grid is scored on the TRAIN window only (net
Rs, needing `min_train_trades`); the chosen parameters then run on the TEST
window, which the choice never saw. Only test-window trades are reported as
out-of-sample. Indicators and regimes are causal, so computing them once over
the whole history leaks nothing between windows.

Only one position at a time; at most `max_entries_per_session` entry signals a
session (a signal whose fill is skipped still uses one, so a missing premium
cannot turn into a signal on every bar).
"""

from __future__ import annotations

import itertools
from dataclasses import asdict, dataclass, field
from datetime import date, datetime, time, timedelta
from typing import Any, Dict, List, Mapping, Optional, Sequence, Tuple

from backtest.trailing import TrailState
from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Features, Signal
from research.costs import CostModel
from research.data import PremiumBook
from research.metrics import WEEKDAYS, summarize
from research.regime import TREND_DOWN, TREND_UP


@dataclass(frozen=True)
class SimConfig:
    underlying: str
    lot_size: int
    lots: int = 1
    last_entry_ist: time = time(14, 30)
    max_entries_per_session: int = 3
    risk_unit_pct: float = 30.0
    costs: CostModel = field(default_factory=CostModel)

    def to_dict(self) -> Dict[str, Any]:
        out = asdict(self)
        out["last_entry_ist"] = self.last_entry_ist.strftime("%H:%M")
        return out


@dataclass
class Trade:
    candidate: str
    session: str
    weekday: str
    side: str
    strike: float
    expiry_date: Optional[str]
    entry_offset: int
    signal_utc: str
    signal_ist: str
    signal_reason: str
    regime_at_signal: Optional[str]
    entry_utc: str
    entry_premium: float          # bar open, before slippage
    entry_fill: float
    exit_utc: str
    exit_ist: str
    exit_premium: float           # level/open/close used, before slippage
    exit_fill: float
    exit_reason: str
    quantity: int
    gross_pnl: float
    charges: float
    net_pnl: float
    slippage_cost: float
    risk_rupees: float
    r_multiple: Optional[float]
    bars_held: int
    unpriced_bars: int
    stale_exit: bool
    mfe_pct: float
    mae_pct: float
    underlying_at_signal: float

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)


@dataclass
class Skip:
    session: str
    signal_ist: str
    side: str
    reason: str


@dataclass
class SimResult:
    candidate: str
    params: Dict[str, Any]
    trades: List[Trade]
    skipped: List[Skip]

    @property
    def summary(self) -> Dict[str, Any]:
        return summarize(self.trades)


@dataclass
class _Position:
    side: str
    strike: float
    expiry_date: Optional[str]
    offset: int
    signal_i: int
    signal: Signal
    entry_utc: str
    entry_premium: float
    entry_fill: float
    stop_level: Optional[float]
    target_level: Optional[float]
    underlying_ref: float
    atr_ref: Optional[float]
    last_mark: float
    last_mark_utc: str
    peak_high: float
    low_seen: float
    trail: TrailState = field(default_factory=TrailState)
    bars_held: int = 0
    unpriced_bars: int = 0
    pending_exit: Optional[str] = None


def _hhmm(moment: datetime) -> str:
    return moment.strftime("%H:%M")


def simulate(candidate: Candidate, params: Mapping[str, Any], features: Features, book: PremiumBook,
             config: SimConfig, start: Optional[date] = None, end: Optional[date] = None) -> SimResult:
    """Run one candidate with one parameter set over the sessions in [start, end]."""
    policy: ExitPolicy = candidate.exits(params)
    bars = features.bars
    n = len(bars)
    qty = config.lots * config.lot_size
    lo_key = start.isoformat() if start else None
    hi_key = end.isoformat() if end else None

    trades: List[Trade] = []
    skipped: List[Skip] = []
    position: Optional[_Position] = None
    pending_entry: Optional[Tuple[int, Signal]] = None
    state: Dict[str, Any] = {}
    entries_today = 0
    current_session: Optional[str] = None

    def close(pos: _Position, i: int, raw_exit: float, reason: str, stale: bool = False) -> None:
        exit_fill = config.costs.sell_fill(raw_exit)
        gross = (exit_fill - pos.entry_fill) * qty
        charge = config.costs.charges(pos.entry_fill * qty, exit_fill * qty)["total"]
        net = gross - charge
        stop_pct = policy.stop_premium_pct if policy.stop_premium_pct else config.risk_unit_pct
        risk = pos.entry_fill * stop_pct / 100.0 * qty
        signal_moment = features.moments[pos.signal_i]
        exit_moment = features.moments[i]
        # A stale exit is booked on bar i at the last premium seen (pos.last_mark_utc).
        exit_utc = bars[i].timestamp_utc
        trades.append(Trade(
            candidate=candidate.name,
            session=features.sessions[pos.signal_i],
            weekday=WEEKDAYS[signal_moment.weekday()],
            side=pos.side,
            strike=pos.strike,
            expiry_date=pos.expiry_date,
            entry_offset=pos.offset,
            signal_utc=bars[pos.signal_i].timestamp_utc,
            signal_ist=_hhmm(signal_moment),
            signal_reason=pos.signal.reason,
            regime_at_signal=features.readings[pos.signal_i].label,
            entry_utc=pos.entry_utc,
            entry_premium=pos.entry_premium,
            entry_fill=pos.entry_fill,
            exit_utc=exit_utc,
            exit_ist=_hhmm(exit_moment),
            exit_premium=raw_exit,
            exit_fill=exit_fill,
            exit_reason=reason,
            quantity=qty,
            gross_pnl=gross,
            charges=charge,
            net_pnl=net,
            slippage_cost=((raw_exit - pos.entry_premium) - (exit_fill - pos.entry_fill)) * qty,
            risk_rupees=risk,
            r_multiple=(net / risk) if risk > 0 else None,
            bars_held=pos.bars_held,
            unpriced_bars=pos.unpriced_bars,
            stale_exit=stale,
            mfe_pct=(pos.peak_high / pos.entry_fill - 1.0) * 100.0 if pos.entry_fill else 0.0,
            mae_pct=(pos.low_seen / pos.entry_fill - 1.0) * 100.0 if pos.entry_fill else 0.0,
            underlying_at_signal=features.closes[pos.signal_i],
        ))

    for i in range(n):
        session = features.sessions[i]
        if lo_key and session < lo_key:
            continue
        if hi_key and session > hi_key:
            break
        ts = bars[i].timestamp_utc
        moment = features.moments[i]

        if session != current_session:
            # A pending entry never crosses sessions: signals on a session's
            # last bar are refused in step 4.
            current_session = session
            state = {}
            entries_today = 0
        last_of_session = features.is_last_of_session(i)

        # 1. Fill a pending entry at this bar's open.
        entered_now = False
        if pending_entry is not None:
            signal_i, signal = pending_entry
            pending_entry = None
            row = book.at_offset(ts, signal.side, candidate.strike_offset)
            signal_clock = _hhmm(features.moments[signal_i])
            if row is None:
                skipped.append(Skip(session, signal_clock, signal.side,
                                    f"no {signal.side} premium at offset {candidate.strike_offset:+d} for the fill bar"))
            elif row.open <= 0:
                skipped.append(Skip(session, signal_clock, signal.side, "non-positive premium at the fill bar"))
            else:
                fill = config.costs.buy_fill(row.open)
                position = _Position(
                    side=signal.side, strike=row.strike, expiry_date=row.expiry_date, offset=row.strike_offset,
                    signal_i=signal_i, signal=signal, entry_utc=ts, entry_premium=row.open, entry_fill=fill,
                    stop_level=fill * (1 - policy.stop_premium_pct / 100.0) if policy.stop_premium_pct else None,
                    target_level=fill * (1 + policy.target_premium_pct / 100.0) if policy.target_premium_pct else None,
                    underlying_ref=features.closes[signal_i], atr_ref=features.readings[signal_i].atr,
                    last_mark=row.open, last_mark_utc=ts, peak_high=row.open, low_seen=row.open,
                )
                entered_now = True

        # 2. Manage the open position on this bar.
        if position is not None:
            pos = position
            row = book.at_strike(ts, pos.side, pos.strike, pos.expiry_date)
            closed = False
            if row is None:
                pos.unpriced_bars += 1
            else:
                if pos.pending_exit and not entered_now:
                    close(pos, i, row.open, pos.pending_exit)
                    closed = True
                else:
                    levels = [lvl for lvl in (pos.stop_level, _trail_level(pos, policy)) if lvl is not None]
                    stop = max(levels) if levels else None
                    stop_reason = "TRAIL_STOP" if (stop is not None and pos.stop_level != stop) else "PREMIUM_STOP"
                    if stop is not None and row.open <= stop and not entered_now:
                        close(pos, i, row.open, stop_reason)
                        closed = True
                    elif stop is not None and row.low <= stop:
                        close(pos, i, stop, stop_reason)
                        closed = True
                    elif pos.target_level is not None and row.open >= pos.target_level and not entered_now:
                        close(pos, i, row.open, "TARGET")
                        closed = True
                    elif pos.target_level is not None and row.high >= pos.target_level:
                        close(pos, i, pos.target_level, "TARGET")
                        closed = True
                if not closed:
                    pos.peak_high = max(pos.peak_high, row.high)
                    pos.low_seen = min(pos.low_seen, row.low)
                    pos.last_mark, pos.last_mark_utc = row.close, ts
                    if policy.trail_giveback_pct:
                        trigger = pos.entry_fill * (policy.trail_arm_pct or 0.0) / 100.0
                        pos.trail.update(row.high - pos.entry_fill,
                                         pos.entry_fill * policy.trail_giveback_pct / 100.0,
                                         trigger if policy.trail_arm_pct else None)
            if closed:
                position = None
            else:
                pos.bars_held += 1
                # 3. Close-decided exits.
                bar_end = (moment + timedelta(minutes=features.bar_minutes)).time()
                reading = features.readings[i]
                if last_of_session:
                    if row is not None:
                        close(pos, i, row.close, pos.pending_exit or "SESSION_END")
                    else:
                        close(pos, i, pos.last_mark, (pos.pending_exit or "SESSION_END") + "_UNPRICED", stale=True)
                    position = None
                elif pos.pending_exit is None:
                    if bar_end >= policy.force_exit_ist:
                        pos.pending_exit = "FORCED_EXIT"
                    elif policy.stop_underlying_atr and pos.atr_ref:
                        distance = policy.stop_underlying_atr * pos.atr_ref
                        close_now = features.closes[i]
                        if (pos.side == CE and close_now <= pos.underlying_ref - distance) or \
                                (pos.side == PE and close_now >= pos.underlying_ref + distance):
                            pos.pending_exit = "INDEX_ATR_STOP"
                    if pos.pending_exit is None and policy.time_stop_bars and pos.bars_held >= policy.time_stop_bars:
                        pos.pending_exit = "TIME_STOP"
                    if pos.pending_exit is None and policy.against(pos.side, reading.label):
                        pos.pending_exit = "REGIME_FLIP"

        # 4. Entry decision at this bar's close.
        if position is None and pending_entry is None and not last_of_session:
            fill_clock = (moment + timedelta(minutes=features.bar_minutes)).time()
            if (entries_today < config.max_entries_per_session and fill_clock < config.last_entry_ist
                    and fill_clock < policy.force_exit_ist and features.readings[i].ready):
                ctx = BarContext(i=i, features=features, params=params, state=state, trades_today=entries_today)
                signal = candidate.entry(ctx)
                if signal is not None:
                    pending_entry = (i, signal)
                    entries_today += 1

    return SimResult(candidate=candidate.name, params=dict(params), trades=trades, skipped=skipped)


def _trail_level(pos: _Position, policy: ExitPolicy) -> Optional[float]:
    if not policy.trail_giveback_pct or not pos.trail.armed:
        return None
    return pos.entry_fill + pos.trail.peak - pos.entry_fill * policy.trail_giveback_pct / 100.0


# -------------------------------------------------------------- walk-forward --

def add_months(day: date, months: int) -> date:
    month_index = day.month - 1 + months
    year = day.year + month_index // 12
    month = month_index % 12 + 1
    for candidate_day in (day.day, 30, 29, 28):
        try:
            return date(year, month, candidate_day)
        except ValueError:
            continue
    raise ValueError("unreachable")


@dataclass
class Window:
    train_start: date
    train_end: date
    test_start: date
    test_end: date
    partial_test: bool

    def to_dict(self) -> Dict[str, Any]:
        return {"train": [self.train_start.isoformat(), self.train_end.isoformat()],
                "test": [self.test_start.isoformat(), self.test_end.isoformat()],
                "partial_test": self.partial_test}


def make_windows(first: date, last: date, train_months: int = 3, test_months: int = 1,
                 step_months: int = 1) -> List[Window]:
    """
    Rolling calendar windows over [first, last]. A test window running past
    `last` is cut at `last` and flagged partial; one with fewer than 10
    calendar days is dropped.
    """
    windows = []
    train_start = first
    while True:
        test_start = add_months(train_start, train_months)
        if test_start > last:
            break
        train_end = test_start - timedelta(days=1)
        test_end = add_months(test_start, test_months) - timedelta(days=1)
        partial = test_end > last
        if partial:
            test_end = last
            if (test_end - test_start).days + 1 < 10:
                break
        windows.append(Window(train_start, train_end, test_start, test_end, partial))
        train_start = add_months(train_start, step_months)
    return windows


def grid_combinations(candidate: Candidate) -> List[Dict[str, Any]]:
    if not candidate.grid:
        return [candidate.params()]
    keys = list(candidate.grid)
    out = []
    for values in itertools.product(*(candidate.grid[k] for k in keys)):
        out.append(candidate.params(dict(zip(keys, values))))
    return out


@dataclass
class Fold:
    window: Window
    chosen_params: Dict[str, Any]
    choice_reason: str
    grid: List[Dict[str, Any]]
    train_summary: Dict[str, Any]
    test: SimResult
    baseline_test: Optional[SimResult]


@dataclass
class WalkForward:
    candidate: str
    folds: List[Fold]
    notes: List[str]

    @property
    def test_trades(self) -> List[Trade]:
        return [t for f in self.folds for t in f.test.trades]

    @property
    def baseline_trades(self) -> List[Trade]:
        return [t for f in self.folds if f.baseline_test for t in f.baseline_test.trades]


def walk_forward(candidate: Candidate, features: Features, book: PremiumBook, config: SimConfig,
                 baseline: Optional[Candidate] = None, train_months: int = 3, test_months: int = 1,
                 min_train_trades: int = 8) -> WalkForward:
    ready_sessions = sorted({r.session for r in features.readings if r.ready})
    notes: List[str] = []
    if not ready_sessions:
        return WalkForward(candidate.name, [], ["no session has a ready regime reading"])
    first, last = date.fromisoformat(ready_sessions[0]), date.fromisoformat(ready_sessions[-1])
    windows = make_windows(first, last, train_months, test_months)
    if not windows:
        notes.append(f"not enough history for a {train_months}m/{test_months}m walk-forward: ready sessions run "
                     f"{first} to {last}")
    folds: List[Fold] = []
    combos = grid_combinations(candidate)
    for window in windows:
        scored = []
        for params in combos:
            result = simulate(candidate, params, features, book, config, window.train_start, window.train_end)
            s = result.summary
            scored.append({"params": params, "trades": s["trades"], "net": s["net"],
                           "expectancy_r": s["expectancy_r"], "win_rate": s["win_rate"]})
        eligible = [row for row in scored if row["trades"] >= min_train_trades]
        if eligible:
            best = max(eligible, key=lambda row: (row["net"], row["params"] == candidate.params()))
            chosen = best["params"]
            reason = f"best train net Rs among {len(eligible)}/{len(scored)} sets with >= {min_train_trades} trades"
        else:
            chosen = candidate.params()
            reason = f"defaults: no parameter set reached {min_train_trades} train trades"
        train_row = next((row for row in scored if row["params"] == chosen), None)
        if train_row is None:  # defaults outside the grid
            s = simulate(candidate, chosen, features, book, config, window.train_start, window.train_end).summary
            train_row = {"params": chosen, "trades": s["trades"], "net": s["net"],
                         "expectancy_r": s["expectancy_r"], "win_rate": s["win_rate"]}
        train_summary = {k: v for k, v in train_row.items() if k != "params"}
        test = simulate(candidate, chosen, features, book, config, window.test_start, window.test_end)
        base = None
        if baseline is not None and baseline.name != candidate.name:
            base = simulate(baseline, baseline.params(), features, book, config, window.test_start,
                            window.test_end)
        folds.append(Fold(window, chosen, reason, scored, train_summary, test, base))
    return WalkForward(candidate.name, folds, notes)


# ------------------------------------------------------------ signal census --

@dataclass
class CensusRow:
    session: str
    signal_ist: str
    side: str
    reason: str
    regime: Optional[str]
    against_regime: bool
    index_points_to_exit: Optional[float]


def signal_census(candidate: Candidate, params: Mapping[str, Any], features: Features,
                  config: SimConfig, cooldown_bars: int = 12,
                  start: Optional[date] = None, end: Optional[date] = None) -> List[CensusRow]:
    """
    Where and in which regime a candidate would have signalled, WITHOUT
    premiums - for when `option_history_bars` is not available yet.

    Same eligibility as `simulate` (ready regime, entry cutoff, entries per
    session, no signal on the session's last bar); instead of a position a
    signal starts a cooldown of `cooldown_bars`. `index_points_to_exit` is the
    index move from the fill bar's open to the forced-exit bar's open, signed
    in the signal's direction: a direction check only. It ignores stops and is
    NOT option P&L.
    """
    policy = candidate.exits(params)
    rows: List[CensusRow] = []
    bars = features.bars
    state: Dict[str, Any] = {}
    current = None
    entries = 0
    cooldown_until = -1
    lo_key = start.isoformat() if start else None
    hi_key = end.isoformat() if end else None
    for i in range(len(bars)):
        session = features.sessions[i]
        if lo_key and session < lo_key:
            continue
        if hi_key and session > hi_key:
            break
        if session != current:
            current, state, entries = session, {}, 0
        if features.is_last_of_session(i) or i <= cooldown_until:
            continue
        moment = features.moments[i]
        fill_clock = (moment + timedelta(minutes=features.bar_minutes)).time()
        if (entries >= config.max_entries_per_session or fill_clock >= config.last_entry_ist
                or fill_clock >= policy.force_exit_ist or not features.readings[i].ready):
            continue
        signal = candidate.entry(BarContext(i=i, features=features, params=params, state=state,
                                            trades_today=entries))
        if signal is None:
            continue
        entries += 1
        cooldown_until = i + cooldown_bars
        label = features.readings[i].label
        against = (signal.side == CE and label == TREND_DOWN) or (signal.side == PE and label == TREND_UP)
        exit_i = None
        j = i + 1
        while j < len(bars) and features.sessions[j] == session:
            exit_i = j
            if features.moments[j].time() >= policy.force_exit_ist:
                break
            j += 1
        points = None
        if exit_i is not None and exit_i > i + 1:
            move = float(bars[exit_i].open) - float(bars[i + 1].open)
            points = move if signal.side == CE else -move
        rows.append(CensusRow(session, _hhmm(moment), signal.side, signal.reason, label, against, points))
    return rows
