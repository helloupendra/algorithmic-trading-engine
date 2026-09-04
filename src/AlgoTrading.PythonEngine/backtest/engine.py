"""
backtest/engine.py

The replay loop: one catalog strategy, one underlying, one bar resolution,
one IST date range, driven bar-by-bar through the same `BaseStrategy.on_bar`
contract as the live runner.

  - fills at the option candle close of the signal bar (see feed.option_close_at);
  - a paper ledger in lots x lot size (backtest/ledger.py);
  - risk rules at three levels, evaluated every bar after the marks in the
    same order as the live risk guard (run_spec.RiskRules):
      leg     premium points / % of entry per leg -> closes that leg only;
      group   rupee P&L per group                 -> closes that group only;
      overall rupee TOTAL P&L (realized + unrealized - charges) -> flattens
              everything and ends the run;
    within a level the order is fixed stop-loss -> trailing stop -> target;
    trailing peaks live in `backtest/trailing.py` for the length of the session;
  - an end-of-day square-off at `eod_square_off_ist` and an end-of-range
    square-off;
  - every fill, mark, equity point and the final summary are posted to the
    Simulator with HISTORICAL timestamps so the run persists like a live one.

Nothing fails silently: an entry the feed cannot price is skipped, logged
with `[SKIP]` and listed in the summary's `skippedEntries`.
"""

from __future__ import annotations

import sys
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

from core.resolutions import to_strategy_resolution
from strategies.base_strategy import BarFrame, BaseStrategy, OptionContract, StrategyInput, StrategySignal
from strategies.contract_selector import describe_requirement, fallback_strike_step, format_strike
from strategies.signal_utils import signal_to_request, stamp_signal_metadata

from backtest.contracts import ContractResolver
from backtest.feed import HistoricalFeed
from backtest.ledger import ApplyResult, LedgerPosition, PaperLedger, pnl_percent, pnl_points
from backtest.run_spec import BacktestRun, RiskRules, parse_parameters, parse_run_row
from backtest.timeutil import compact_stamp, format_ist, in_session, iso_utc, ist_date, ist_time, parse_utc
from backtest.trailing import TrailLevels

SNAPSHOT_BATCH = 500
PROGRESS_INTERVAL_SECONDS = 2.0
WARMUP_DAYS = 15
MAX_LOGGED_SKIPS = 200

ProgressCallback = Callable[[Dict[str, Any]], None]
Logger = Callable[[str], None]

# Summary keys of the risk-rule trip counters (leg/group closes; the overall
# level ends the run and is reported as `stopReason` plus `overallTrailStop`).
RISK_COUNTERS = ("legStops", "legTargets", "legTrailStops",
                 "groupStops", "groupTargets", "groupTrailStops")

# kind returned by a level's trip check -> its counter.
LEG_COUNTERS = {"stop": "legStops", "target": "legTargets", "trail": "legTrailStops"}
GROUP_COUNTERS = {"stop": "groupStops", "target": "groupTargets", "trail": "groupTrailStops"}


def _money(value: float, plus: bool = True) -> str:
    """
    "−1,240" / "+1,240" for log lines; with `plus=False` a positive amount has
    no sign ("1,240"), which is the form the API's risk guard uses in its
    CLOSE_GROUP / RUN_STOPPED reasons — the backtest must read the same.
    """
    sign = "−" if value < 0 else ("+" if plus else "")
    return f"{sign}{abs(value):,.0f}"


def _signed(value: float, digits: int = 1, suffix: str = "") -> str:
    """+6.2 pts / −21.4 pts with a typographic minus, like `_money`."""
    sign = "−" if value < 0 else "+"
    return f"{sign}{abs(value):,.{digits}f}{suffix}"


def _rupees(value: float) -> str:
    """
    "₹3,200" / "₹−1,800": the money form the trailing reasons use at the
    overall and group levels (the fixed rules keep their older `_money` form,
    which the live guard already emits character for character).
    """
    sign = "−" if value < 0 else ""
    return f"₹{sign}{abs(value):,.0f}"


def _amount(value: float, digits: int = 1, suffix: str = "") -> str:
    """An unsigned give-back ("13.8 pts"), as the trailing reasons word it."""
    return f"{abs(value):,.{digits}f}{suffix}"


def _leg_move(points: float, percent: Optional[float]) -> str:
    text = _signed(points, 1, " pts")
    if percent is not None:
        text += f" ({_signed(percent, 1, '%')})"
    return text


@dataclass
class BacktestOutcome:
    status: str                              # Completed | Failed
    summary: Dict[str, Any]
    error: Optional[str] = None
    stop_reason: Optional[str] = None
    ledger: Optional[PaperLedger] = None
    equity_points: List[Dict[str, Any]] = field(default_factory=list)


@dataclass
class _DayState:
    day: Optional[date] = None
    squared_off: bool = False
    last_t: Optional[datetime] = None


class BacktestSession:
    """One replay; `execute()` drives it end to end. See run_backtest for the entry point."""

    def __init__(self, api: Any, run: BacktestRun, strategy: BaseStrategy, *,
                 on_progress: Optional[ProgressCallback] = None, log: Logger = print,
                 broker_linked: Optional[bool] = None, lot_size: Optional[int] = None,
                 lot_size_source: str = "", warmup_days: int = WARMUP_DAYS,
                 progress_interval: float = PROGRESS_INTERVAL_SECONDS,
                 snapshot_batch: int = SNAPSHOT_BATCH) -> None:
        self.api = api
        self.run = run
        self.strategy = strategy
        self.on_progress = on_progress
        self.log = log
        self.warmup_days = warmup_days
        self.progress_interval = progress_interval
        self.snapshot_batch = max(1, min(int(snapshot_batch), 5000))

        self.state: Dict[str, Any] = {}
        self.lots = self._resolve_lots()
        self.broker_linked = self._resolve_broker_linked(broker_linked)
        self.lot_size, self.lot_size_source = self._resolve_lot_size(lot_size, lot_size_source)

        self.feed = HistoricalFeed(api, run, broker_linked=self.broker_linked, warmup_days=warmup_days, log=log)
        self.resolver = ContractResolver(api, run.underlying, log=log)
        self.ledger = PaperLedger(self.lot_size, run.charges_per_lot)

        self.requirements = list(self.strategy.get_data_requirements() or [])
        # The option contracts the strategy wants each bar (ATM / OTM / ITM),
        # resolved once: only the strikes they land on move with the underlying.
        self.contract_requirements = list(self.strategy.get_contract_requirements(run.params) or [])
        self.contract_keys = {req.key for req in self.contract_requirements}
        # (key, strike) already reported as missing, so a moving underlying does
        # not add the same skip line on every bar.
        self._missing_contracts: Set[Tuple[str, Any]] = set()
        required = [to_strategy_resolution(r.resolution) for r in self.requirements if r.resolution]
        self.resolutions: List[str] = [run.resolution] + [r for r in dict.fromkeys(required) if r != run.resolution]
        # Index resolutions the strategy cannot run without (a missing one fails the run).
        self.index_resolutions: List[str] = [
            to_strategy_resolution(r.resolution) for r in self.requirements
            if r.resolution and r.symbol_type == "index"
        ]
        self.warms_up = any(r.symbol_type == "index" for r in self.requirements)

        self.sessions: Set[date] = set()
        self.bars_processed = 0
        self.total_bars = 0
        self.skipped_entries: List[Dict[str, Any]] = []
        self.skipped_after_eod = 0
        self.eod_square_offs = 0
        self.stop_reason: Optional[str] = None
        self.equity_points: List[Dict[str, Any]] = []
        self._pending_snapshots: List[Dict[str, Any]] = []
        self._last_marks: Dict[str, float] = {}
        self._last_progress_at = 0.0
        self._signals_posted = 0
        self._ghost_counter = 0
        self._day = _DayState()
        # (group_id, logical leg symbol) -> broker symbol it was opened with, so a
        # CLOSE_GROUP for "BANKNIFTY_PE_57600" finds the position even after the
        # expiry has rolled over since the open.
        self._opened_as: Dict[Tuple[str, str], str] = {}
        self.data_notes: List[str] = []
        self.warmup_bars_used = 0
        self.step: float = fallback_strike_step(run.underlying)
        self.risk: RiskRules = run.risk
        self.risk_counts: Dict[str, int] = {key: 0 for key in RISK_COUNTERS}
        # Trailing peaks for the three levels; a replay has neither an API
        # restart nor a live rule change, so they last the whole session.
        self.trails = TrailLevels()
        self.overall_trail_stop = False

    # --- configuration ------------------------------------------------------

    def _resolve_lots(self) -> int:
        strategy_lots = getattr(self.strategy, "lots", None)
        if isinstance(strategy_lots, int) and strategy_lots >= 1:
            return strategy_lots
        return self.run.lots

    def _resolve_broker_linked(self, explicit: Optional[bool]) -> bool:
        if explicit is not None:
            return bool(explicit)
        getter = getattr(self.api, "get_broker_session", None)
        if getter is None:
            return False
        try:
            session = getter() or {}
            return bool(session.get("isAuthenticated"))
        except Exception as ex:
            self.log(f"[CONFIG] WARN: broker session unknown ({ex}); assuming not linked")
            return False

    def _resolve_lot_size(self, explicit: Optional[int], source: str) -> tuple:
        if explicit is not None and int(explicit) >= 1:
            return int(explicit), source or "given"
        # The API freezes the lot size into the run at start ("lot_size") and
        # books every fill with it, so the ledger must use the same number.
        try:
            frozen = int(self.run.params.get("lot_size") or 0)
        except (TypeError, ValueError):
            frozen = 0
        if frozen >= 1:
            return frozen, str(self.run.params.get("lot_size_source") or "run")
        getter = getattr(self.api, "get_fno_underlyings", None)
        if getter is not None:
            try:
                for row in getter() or []:
                    if str(row.get("underlying") or "").upper() == self.run.underlying:
                        size = int(row.get("lotSize") or 0)
                        if size >= 1:
                            return size, str(row.get("lotSizeSource") or "master")
            except Exception as ex:
                self.log(f"[CONFIG] WARN: could not read lot size for {self.run.underlying}: {ex}")
        self.data_notes.append(
            f"Lot size for {self.run.underlying} is unknown to the platform; P&L was computed with lot size 1."
        )
        return 1, "unknown"

    # --- helpers ------------------------------------------------------------

    def _note(self, text: str) -> None:
        if text not in self.data_notes:
            self.data_notes.append(text)

    def _skip(self, t_iso: str, symbol: str, reason: str) -> None:
        self.skipped_entries.append({"atUtc": t_iso, "symbol": symbol, "reason": reason})
        if len(self.skipped_entries) <= MAX_LOGGED_SKIPS:
            self.log(f"[SKIP] {format_ist(parse_utc(t_iso))} IST {symbol}: {reason}")

    def _post_signal(self, sig: StrategySignal, inp_spot: float, inp_atm: Any, result: ApplyResult) -> None:
        sig.legs = [
            {"symbol": leg["symbol"], "side": leg["side"], "quantity": int(leg["quantity"]), "price": float(leg["price"])}
            for leg in result.legs
        ]
        if sig.metadata is None:
            sig.metadata = {}
        sig.metadata["reason"] = sig.reason
        sig.metadata["spot_price"] = inp_spot
        sig.metadata["atm_strike"] = inp_atm
        sig.metadata["group_id"] = result.group_id
        self.api.create_simulation_signal(signal_to_request(self.run.run_id, sig))
        self._signals_posted += 1
        legs_text = ", ".join(f"{l['side']} {l['quantity']}x {l['symbol']} @ {l['price']:.2f}" for l in sig.legs)
        realized = f" realized {_money(result.realized_delta)}" if result.closed else ""
        self.log(f"[FILL] {format_ist(parse_utc(sig.timestamp_utc))} IST {sig.signal_type} {result.group_id}: {legs_text}{realized} - {sig.reason}")

    def _flush_snapshots(self, force: bool = False) -> None:
        while self._pending_snapshots and (force or len(self._pending_snapshots) >= self.snapshot_batch):
            batch, self._pending_snapshots = self._pending_snapshots[:self.snapshot_batch], self._pending_snapshots[self.snapshot_batch:]
            self.api.post_equity_snapshots(self.run.run_id, batch)

    def _progress_payload(self, t_iso: str, message: str) -> Dict[str, Any]:
        percent = 100.0 if self.total_bars == 0 else round(100.0 * self.bars_processed / self.total_bars, 1)
        return {
            "percent": min(100.0, percent),
            "barsProcessed": self.bars_processed,
            "totalBars": self.total_bars,
            "currentUtc": t_iso,
            "trades": self.ledger.trades,
            "message": message,
        }

    def _report_progress(self, t_iso: str, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now - self._last_progress_at < self.progress_interval:
            return
        self._last_progress_at = now
        message = (
            f"{format_ist(parse_utc(t_iso), '%d %b %H:%M')} IST · {len(self.ledger.open_positions())} open · "
            f"P&L {_money(self.ledger.total_pnl())}"
        )
        payload = self._progress_payload(t_iso, message)
        self.log(f"[PROGRESS] {payload['percent']:.1f}% bars={self.bars_processed}/{self.total_bars} trades={payload['trades']} {message}")
        if self.on_progress is not None:
            try:
                self.on_progress(payload)
            except Exception as ex:
                self.log(f"[PROGRESS] WARN: could not report progress: {ex}")

    # --- market context -----------------------------------------------------

    def _resolve_contracts(self, expiry: Optional[str], atm: Any, t: datetime) -> Dict[str, OptionContract]:
        """
        The contracts the strategy declared, for this bar's ATM strike.

        A key the instrument master cannot satisfy is left out — the strategy
        sees the absence and simply does not enter — and recorded once per
        (key, strike) as a skipped entry, so a strangle that never traded says
        which strikes were missing instead of completing with zero trades and
        no explanation. `optional` requirements are not reported, and neither is
        a lookup the next bar will retry (the resolver logs and counts those).
        """
        contracts, missing = self.resolver.contracts_for(
            self.contract_requirements, expiry, atm, self.step, self.run.params
        )
        for gap in missing:
            if gap["optional"] or gap["failed"]:
                continue
            marker = (gap["key"], gap["strike"])
            if marker in self._missing_contracts:
                continue
            self._missing_contracts.add(marker)
            label = f"{gap['key']} {format_strike(float(gap['strike']))} {gap['optionType']}"
            self._skip(iso_utc(t), label, gap["reason"])
            self._note(
                f"{self.run.strategy_name} needs {gap['key']} ({gap['optionType']} "
                f"{format_strike(float(gap['strike']))}): {gap['reason']}."
            )
        return contracts

    def _build_input(self, bar: BarFrame, t: datetime, contracts: Dict[str, OptionContract],
                     atm_strike: Any, source: str) -> StrategyInput:
        bars: Dict[str, Dict[str, List[BarFrame]]] = {}
        for resolution in self.resolutions:
            bars[resolution] = {"index": self.feed.bars_upto(resolution, t)}
        for req in self.requirements:
            resolution = to_strategy_resolution(req.resolution) if req.resolution else self.run.resolution
            kind = req.symbol_type
            if kind == "index":
                continue
            # A requirement key resolves to the contract handed to the strategy
            # this bar; anything else is taken as an exact broker symbol.
            symbol = None
            contract = contracts.get(kind)
            if contract is not None:
                symbol = contract.symbol
            elif kind and kind not in self.contract_keys:
                symbol = kind
            if symbol:
                bars.setdefault(resolution, {})[kind] = self.feed.option_bars_upto(symbol, resolution, t)
        return StrategyInput(
            mode="OfflineReplay",
            timestamp_utc=bar.timestamp_utc,
            underlying=self.run.underlying,
            spot_price=bar.close,
            atm_strike=atm_strike,
            contracts=contracts,
            bars=bars,
            metadata={"source": source},
        )

    # --- square-off / risk --------------------------------------------------

    def _price_at(self, t: datetime) -> Callable[[str], Optional[float]]:
        """Exit price lookup for bar t: the contract's candle close (the ledger falls back to the last mark)."""
        return lambda symbol: self.feed.option_close_at(symbol, t)

    def _apply_close_signals(self, signals: List[StrategySignal], t: datetime, spot: Optional[float],
                             atm: Any, tag: str) -> int:
        """
        Fill and post reduce-only CLOSE_GROUP signals built by the ledger
        (`flatten_all` / `close_positions`). Returns the number of positions
        closed; legs priced off a fallback (no candle at t) are logged.
        """
        t_iso = iso_utc(t)
        closed = 0
        for sig in signals:
            group_id = sig.metadata.get("group_id", "")
            for leg in sig.legs:
                if leg.get("price_source") != "candle":
                    self.log(f"[{tag}] {leg['symbol']}: no candle at {format_ist(t)} IST, using {leg['price_source']} {leg['price']:.2f}")
            result = self.ledger.apply("CLOSE_GROUP", group_id, sig.legs, t_iso, sig.reason)
            if result.applied:
                closed += len(result.closed)
                self._post_signal(sig, spot, atm, result)
        return closed

    def _square_off(self, t: datetime, reason: str, spot: Optional[float] = None, atm: Any = None) -> int:
        """Close every open position at the last known close of its contract at t."""
        signals = self.ledger.flatten_all(self._price_at(t), iso_utc(t), reason, self.run.strategy_name)
        return self._apply_close_signals(signals, t, spot, atm, "SQUARE-OFF")

    def _leg_trip(self, pos: LedgerPosition) -> Optional[Tuple[str, str]]:
        """
        (kind, reason) when the leg rules trip for this open position at its
        last mark, else None. Adverse move = BUY: entry − mark, SELL: mark −
        entry; the order is fixed stop-loss, then the trailing stop, then the
        target, and within each points before percent — so when both a points
        and a percent rule are set, the one that trips first (at the smaller
        move) is the one reported.

        The trailing peaks are updated here even when nothing trips: this is
        the one place that sees every open leg on every sweep.
        """
        rules = self.risk.leg
        if not rules.is_set or pos.last_mark is None:
            return None
        points = pnl_points(pos)
        percent = pnl_percent(pos)
        if points is None:
            return None
        adverse, adverse_pct = -points, (None if percent is None else -percent)
        name = self.resolver.display_name(pos.symbol)
        move = _leg_move(points, percent)

        if rules.stop_loss_points is not None and adverse >= rules.stop_loss_points:
            return "stop", f"Leg stop-loss hit: {name} {move} ≤ −{rules.stop_loss_points:g} pts"
        if rules.stop_loss_percent is not None and adverse_pct is not None and adverse_pct >= rules.stop_loss_percent:
            return "stop", f"Leg stop-loss hit: {name} {move} ≤ −{rules.stop_loss_percent:g}%"

        # Trailing stops: points and percent keep separate peaks, each against
        # its own trail and its own (optional) arming trigger.
        key = (pos.group_id, pos.symbol)
        trip = self.trails.leg_points.evaluate(key, points, rules.trail_stop_loss_points, rules.trail_trigger_points)
        if trip is not None:
            return "trail", (
                f"Leg trailing stop hit: {name} {_signed(points, 1, ' pts')} fell "
                f"{_amount(trip.drawdown, 1, ' pts')} from peak {_signed(trip.peak, 1, ' pts')} "
                f"(trail {rules.trail_stop_loss_points:g} pts)"
            )
        trip = self.trails.leg_percent.evaluate(key, percent, rules.trail_stop_loss_percent,
                                                rules.trail_trigger_percent)
        if trip is not None:
            return "trail", (
                f"Leg trailing stop hit: {name} {_signed(trip.value, 1, '%')} fell "
                f"{_amount(trip.drawdown, 1, '%')} from peak {_signed(trip.peak, 1, '%')} "
                f"(trail {rules.trail_stop_loss_percent:g}%)"
            )

        # Reason strings match the API risk guard (StrategyRiskGuardService)
        # character for character: thresholds carry no "+".
        if rules.target_points is not None and points >= rules.target_points:
            return "target", f"Leg target hit: {name} {move} ≥ {rules.target_points:g} pts"
        if rules.target_percent is not None and percent is not None and percent >= rules.target_percent:
            return "target", f"Leg target hit: {name} {move} ≥ {rules.target_percent:g}%"
        return None

    def _apply_leg_rules(self, t: datetime, spot: float, atm: Any) -> int:
        """Level 1: close every open leg whose own SL/target tripped (that leg only)."""
        if not self.risk.leg.is_set:
            return 0
        trips: List[Tuple[Tuple[str, str], str, str]] = []
        for pos in self.ledger.open_positions():
            trip = self._leg_trip(pos)
            if trip is not None:
                trips.append(((pos.group_id, pos.symbol), trip[0], trip[1]))
        closed = 0
        for key, kind, reason in trips:
            self.log(f"[RISK] {format_ist(t)} IST {reason}")
            signals = self.ledger.close_positions([key], self._price_at(t), iso_utc(t), reason,
                                                  self.run.strategy_name, metadata={"risk_rule": "leg"})
            count = self._apply_close_signals(signals, t, spot, atm, "RISK")
            if count:
                closed += count
                self.risk_counts[LEG_COUNTERS[kind]] += 1
        return closed

    def _group_trip(self, group_id: str, pnl: float) -> Optional[Tuple[str, str]]:
        """(kind, reason) when the group rules trip at `pnl`: stop-loss, then trailing stop, then target."""
        rules = self.risk.group
        if rules.stop_loss is not None and pnl <= -rules.stop_loss:
            return "stop", f"Group stop-loss hit: {group_id} P&L {_money(pnl, plus=False)} ≤ −{rules.stop_loss:,.0f}"
        trip = self.trails.group.evaluate(group_id, pnl, rules.trail_stop_loss, rules.trail_trigger)
        if trip is not None:
            return "trail", (
                f"Group trailing stop hit: {group_id} P&L {_rupees(trip.value)} fell "
                f"{_rupees(trip.drawdown)} from peak {_rupees(trip.peak)} "
                f"(trail {_rupees(trip.trail)})"
            )
        if rules.target is not None and pnl >= rules.target:
            return "target", f"Group target hit: {group_id} P&L {_money(pnl, plus=False)} ≥ {rules.target:,.0f}"
        return None

    def _apply_group_rules(self, t: datetime, spot: float, atm: Any) -> int:
        """Level 2: close every open leg of a group whose P&L tripped (that group only)."""
        rules = self.risk.group
        if not rules.is_set:
            return 0
        trips: List[Tuple[str, str, str]] = []
        for group_id in self.ledger.open_groups():
            trip = self._group_trip(group_id, self.ledger.group_pnl(group_id))
            if trip is not None:
                trips.append((group_id, trip[0], trip[1]))
        closed = 0
        for group_id, kind, reason in trips:
            self.log(f"[RISK] {format_ist(t)} IST {reason}")
            keys = [(group_id, pos.symbol) for pos in self.ledger.group_open_positions(group_id)]
            signals = self.ledger.close_positions(keys, self._price_at(t), iso_utc(t), reason,
                                                  self.run.strategy_name, metadata={"risk_rule": "group"})
            count = self._apply_close_signals(signals, t, spot, atm, "RISK")
            if count:
                closed += count
                self.risk_counts[GROUP_COUNTERS[kind]] += 1
        return closed

    def _check_overall(self, t: datetime, spot: float, atm: Any) -> bool:
        """
        Level 3: total P&L through the overall stop-loss, trailing stop or
        target flattens everything and ends the run.
        """
        rules = self.risk.overall
        total = self.ledger.total_pnl()
        trip = None
        if rules.stop_loss is not None and total <= -rules.stop_loss:
            self.stop_reason = f"Stop loss hit: P&L {_money(total, plus=False)} ≤ −{rules.stop_loss:,.0f}"
        elif (trip := self.trails.overall.evaluate(TrailLevels.RUN, total, rules.trail_stop_loss,
                                                   rules.trail_trigger)) is not None:
            self.overall_trail_stop = True
            self.stop_reason = (
                f"Trailing stop hit: P&L {_rupees(trip.value)} fell {_rupees(trip.drawdown)} "
                f"from peak {_rupees(trip.peak)} (trail {_rupees(trip.trail)})"
            )
        elif rules.target is not None and total >= rules.target:
            self.stop_reason = f"Target hit: P&L {_money(total, plus=False)} ≥ {rules.target:,.0f}"
        else:
            return False
        self.log(f"[STOP] {format_ist(t)} IST {self.stop_reason}")
        self._square_off(t, self.stop_reason, spot, atm)
        return True

    def _check_risk(self, t: datetime, spot: float, atm: Any) -> bool:
        """
        One risk sweep at bar t, in the guard's order: leg rules, then group
        rules over what is still open, then the overall rule on the resulting
        total. Returns True when the overall rule ended the run.

        Peaks of legs and groups that are gone are dropped first, so a group id
        the strategy reuses starts a fresh trail instead of inheriting the
        previous position's best.
        """
        self.trails.prune_legs(self.ledger.open_keys())
        self.trails.prune_groups(self.ledger.open_groups())
        self._apply_leg_rules(t, spot, atm)
        self._apply_group_rules(t, spot, atm)
        return self._check_overall(t, spot, atm)

    def _mark(self, t: datetime) -> None:
        symbols = self.ledger.open_symbols()
        if not symbols:
            return
        prices: Dict[str, float] = {}
        for symbol in symbols:
            price = self.feed.option_close_at(symbol, t)
            if price is not None:
                prices[symbol] = price
        applied = self.ledger.mark(prices, iso_utc(t))
        if not applied:
            return
        marks = self.ledger.mark_prices()
        if marks != self._last_marks:
            self.api.post_marks(self.run.run_id, iso_utc(t), [{"symbol": s, "price": p} for s, p in marks.items()])
            self._last_marks = dict(marks)

    # --- signals ------------------------------------------------------------

    def _ghost_to_open_group(self, sig: StrategySignal, t: datetime, contracts: Dict[str, OptionContract],
                             expiry: Optional[str], atm: Any) -> Optional[StrategySignal]:
        direction = sig.signal_type
        key = "atm_ce" if direction == "BUY" else "atm_pe"
        contract = contracts.get(key)
        if contract is None:
            side = key[-2:].upper()
            self._skip(iso_utc(t), f"ATM {side}", f"ATM {self.resolver.missing_reason(expiry, atm, side)}")
            return None
        self._ghost_counter += 1
        sig.signal_type = "OPEN_GROUP"
        sig.metadata = dict(sig.metadata or {})
        sig.metadata["group_id"] = f"GTC_{compact_stamp(t)}_{self._ghost_counter:03d}"
        sig.metadata["direction"] = direction
        sig.legs = [{"symbol": contract.symbol, "side": "BUY", "quantity": self.lots}]
        return sig

    def _open_symbol_for(self, group_id: str, symbol: str) -> Optional[str]:
        """
        The broker symbol a CLOSE leg refers to, taken from the group's open
        book: a logical symbol maps to the contract it was opened with (the
        expiry may have rolled over since), a broker symbol to itself.
        """
        if ":" in symbol:
            return symbol if self.ledger.position(group_id, symbol) is not None else None
        opened = self._opened_as.get((group_id, symbol.upper()))
        if opened and self.ledger.position(group_id, opened) is not None:
            return opened
        return None

    def _resolve_legs(self, sig: StrategySignal, expiry: Optional[str], t: datetime,
                      group_id: str) -> Optional[List[Dict[str, Any]]]:
        """
        Logical -> broker symbols; None (after recording a skip) when an OPEN
        leg cannot be resolved. CLOSE legs are matched against the group's
        open positions first, then against the current expiry.
        """
        resolved: List[Dict[str, Any]] = []
        for leg in sig.legs or []:
            symbol = str(leg.get("symbol") or "")
            real = self._open_symbol_for(group_id, symbol) if sig.signal_type == "CLOSE_GROUP" else None
            if real is None:
                real = self.resolver.resolve_logical(symbol, expiry) if expiry else (symbol if ":" in symbol else None)
            if not real:
                why = self.resolver.logical_missing_reason(symbol, expiry) if expiry else "no option expiry in the instrument master"
                if sig.signal_type == "OPEN_GROUP":
                    self._skip(iso_utc(t), symbol, why)
                    return None
                self.log(f"[IGNORE] close leg {symbol}: {why}")
                self.ledger.ignored_legs += 1
                continue
            try:
                quantity = int(leg.get("quantity") or 0)
            except (TypeError, ValueError):
                quantity = 0
            resolved.append({
                "symbol": real,
                "side": str(leg.get("side") or "").upper(),
                "quantity": quantity if quantity > 0 else self.lots,
                "price": leg.get("price"),
                "logical": symbol,
            })
        return resolved

    def _handle_signal(self, sig: StrategySignal, t: datetime, inp: StrategyInput,
                       contracts: Dict[str, OptionContract], expiry: Optional[str]) -> None:
        t_iso = iso_utc(t)
        if sig.signal_type in ("BUY", "SELL") and not sig.legs:
            converted = self._ghost_to_open_group(sig, t, contracts, expiry, inp.atm_strike)
            if converted is None:
                return
            sig = converted

        if sig.signal_type not in ("OPEN_GROUP", "CLOSE_GROUP"):
            self.log(f"[SKIP] {format_ist(t)} IST unsupported signal type {sig.signal_type!r} ignored")
            return

        sig.metadata = dict(sig.metadata or {})
        group_id = str(sig.metadata.get("group_id") or "")
        if not group_id:
            group_id = f"{self.strategy.name}_{compact_stamp(t)}"
            sig.metadata["group_id"] = group_id
        sig.timestamp_utc = t_iso

        if sig.signal_type == "OPEN_GROUP" and self._day.squared_off:
            self.skipped_after_eod += 1
            symbols = ", ".join(str(l.get("symbol")) for l in sig.legs) or group_id
            self._skip(t_iso, symbols, f"entry after the EOD square-off ({self.run.eod_square_off_ist} IST)")
            return

        legs = self._resolve_legs(sig, expiry, t, group_id)
        if legs is None:
            return

        if sig.signal_type == "OPEN_GROUP":
            missing = []
            for leg in legs:
                price = self.feed.option_close_at(leg["symbol"], t)
                if price is None:
                    missing.append(leg["symbol"])
                leg["price"] = price
            if missing:
                self._skip(t_iso, ", ".join(missing), f"no premium history for {', '.join(missing)}")
                return
        else:
            for leg in legs:
                price = self.feed.option_close_at(leg["symbol"], t)
                if price is None:
                    position = self.ledger.position(group_id, leg["symbol"])
                    if position is not None:
                        price = position.last_mark if position.last_mark is not None else position.avg_price
                        self.log(f"[CLOSE] {leg['symbol']}: no candle at {format_ist(t)} IST, using last mark {price:.2f}")
                leg["price"] = price

        result = self.ledger.apply(sig.signal_type, group_id, legs, t_iso, sig.reason)
        for leg, why in result.ignored:
            self.log(f"[IGNORE] {format_ist(t)} IST {sig.signal_type} {group_id} {leg.get('side')} {leg.get('symbol')}: {why}")
        if not result.applied:
            if sig.signal_type == "OPEN_GROUP":
                self._skip(t_iso, group_id, "no leg could be filled")
            return
        if sig.signal_type == "OPEN_GROUP":
            filled = {leg["symbol"] for leg in result.legs}
            for leg in legs:
                if leg["symbol"] in filled and ":" not in leg["logical"]:
                    self._opened_as[(group_id, leg["logical"].upper())] = leg["symbol"]
        self._post_signal(sig, inp.spot_price, inp.atm_strike, result)

    # --- main loop ----------------------------------------------------------

    def _warm_up(self) -> None:
        if not self.warms_up:
            return
        bars = [b for b in self.feed.warmup_bars(self.run.resolution_code)
                if self.run.resolution_code == "D" or in_session(parse_utc(b.timestamp_utc))]
        if not bars:
            self.log(f"[WARMUP] no {self.run.resolution} index candles stored before {self.run.from_date.isoformat()}; strategy starts cold")
            return
        for bar in bars:
            t = parse_utc(bar.timestamp_utc)
            inp = self._build_input(bar, t, {}, self.resolver.atm(bar.close, self.step), "warmup")
            self.strategy.on_bar(self.state, inp)
        self.warmup_bars_used = len(bars)
        self.log(f"[WARMUP] fed {len(bars)} {self.run.resolution} index candles before {self.run.from_date.isoformat()}")
        self._note(f"Strategy warm-up used {len(bars)} {self.run.resolution} index candles stored before {self.run.from_date.isoformat()}.")

    def _start_day(self, day: date, t: datetime) -> None:
        previous = self._day
        if previous.day is not None and previous.last_t is not None and self.ledger.has_open() \
                and self.run.eod_square_off is not None and not previous.squared_off:
            reason = f"End-of-day square-off {self.run.eod_square_off_ist} IST"
            self.log(f"[EOD] {previous.day.isoformat()} ended with open positions before {self.run.eod_square_off_ist} IST; squaring off at the last close")
            closed = self._square_off(previous.last_t, reason)
            if closed:
                self.eod_square_offs += 1
        self._day = _DayState(day=day, squared_off=False, last_t=None)
        self.sessions.add(day)

    def _eod_check(self, t: datetime, spot: float, atm: Any) -> None:
        eod = self.run.eod_square_off
        if eod is None or self._day.squared_off or ist_time(t) < eod:
            return
        if self.ledger.has_open():
            self.log(f"[EOD] {format_ist(t)} IST square-off")
            closed = self._square_off(t, f"End-of-day square-off {self.run.eod_square_off_ist} IST", spot, atm)
            if closed:
                self.eod_square_offs += 1
        self._day.squared_off = True

    def execute(self) -> BacktestOutcome:
        run = self.run
        self.log(
            f"[CONFIG] {run.describe()} lot_size={self.lot_size} ({self.lot_size_source}) "
            f"strategy_lots={self.lots} broker_linked={'yes' if self.broker_linked else 'no'} "
            f"resolutions={','.join(self.resolutions)} "
            f"(risk rules enforced by the engine every bar: leg → group → overall on total P&L)"
        )
        self.state = self.strategy.initialize_state()
        self.feed.load(self.resolutions)

        driver = self.feed.driver_bars()
        self.total_bars = len(driver)
        if not driver:
            message = (
                f"No {run.underlying} {run.resolution} candles between {run.from_date.isoformat()} and "
                f"{run.to_date.isoformat()} — backfill first"
            )
            self.log(f"[ERROR] {message}")
            return self._finish("Failed", error=message)

        self.log(f"[FEED] {self.total_bars} driver bars {format_ist(parse_utc(driver[0].timestamp_utc), '%d %b %Y %H:%M')}"
                 f"..{format_ist(parse_utc(driver[-1].timestamp_utc), '%d %b %Y %H:%M')} IST")

        # Every other resolution the strategy needs must be stored too; feeding
        # it an empty series bar after bar would "complete" with zero trades
        # and no explanation, which is exactly the silent failure the run must
        # never produce.
        for resolution in self.resolutions:
            if resolution == run.resolution or len(self.feed.ensure_index(resolution)) > 0:
                continue
            message = (
                f"{run.strategy_name} needs {resolution} index candles and none are stored for "
                f"{run.spot_symbol} in {run.from_date.isoformat()}..{run.to_date.isoformat()} — backfill {resolution} first"
            )
            if resolution in self.index_resolutions:
                self.log(f"[ERROR] {message}")
                self._note(message)
                return self._finish("Failed", error=message)
            self.log(f"[FEED] WARN: {message}")
            self._note(f"No {resolution} index candles stored for {run.spot_symbol} in range; bars[{resolution!r}]['index'] stayed empty.")

        if self.resolver.expiries:
            self.step = self.resolver.step_for(self.resolver.expiry_for(run.from_date))
        else:
            self.step = fallback_strike_step(run.underlying)
            self._note(f"No option contracts for {run.underlying} in the instrument master; every entry was skipped.")

        self.log(
            f"[CONFIG] contracts (strike step {format_strike(self.step)}): "
            + ("; ".join(describe_requirement(req, self.step, run.params) for req in self.contract_requirements)
               or "none declared")
        )

        self._warm_up()
        self._last_progress_at = 0.0

        stopped = False
        last_t: Optional[datetime] = None
        last_bar: Optional[BarFrame] = None
        for bar in driver:
            t = parse_utc(bar.timestamp_utc)
            day = ist_date(t)
            if self._day.day != day:
                self._start_day(day, t)

            spot = bar.close
            expiry = self.resolver.expiry_for(day) if self.resolver.expiries else None
            atm = self.resolver.atm(spot, self.step)

            self._eod_check(t, spot, atm)

            # A day-rollover or EOD square-off books realized P&L (and exit
            # charges) before the strategy sees this bar; if that pushed total
            # P&L through the stop-loss / target the run ends here, exactly as
            # the live guard would, instead of letting a new entry through.
            stopped = self._check_risk(t, spot, atm)
            if not stopped:
                contracts = self._resolve_contracts(expiry, atm, t)
                inp = self._build_input(bar, t, contracts, atm, "backtest")
                signals = self.strategy.on_bar(self.state, inp) or []
                for sig in signals:
                    self._handle_signal(sig, t, inp, contracts, expiry)

                self._mark(t)
                stopped = self._check_risk(t, spot, atm)

            self.bars_processed += 1
            self._day.last_t = t
            last_t, last_bar = t, bar
            self.equity_points.append(self.ledger.snapshot(bar.timestamp_utc))
            self._pending_snapshots.append(self.equity_points[-1])
            self._flush_snapshots()
            self._report_progress(bar.timestamp_utc, force=stopped)
            if stopped:
                break

        if last_t is not None and self.ledger.has_open():
            self._square_off(last_t, "End of backtest", last_bar.close if last_bar else None, None)
            self.equity_points.append(self.ledger.snapshot(iso_utc(last_t)))
            self._pending_snapshots.append(self.equity_points[-1])

        self._flush_snapshots(force=True)
        if last_t is not None:
            self._report_progress(iso_utc(last_t), force=True)
        return self._finish("Completed")

    # --- completion ---------------------------------------------------------

    def _risk_note(self) -> Optional[str]:
        """
        "Risk rules closed 2 legs (stop-loss), 1 group (trailing stop)." — or
        None when no rule tripped all run.
        """
        counts = self.risk_counts
        parts: List[str] = []
        for key, noun, kind in (("legStops", "leg", "stop-loss"), ("legTrailStops", "leg", "trailing stop"),
                                ("legTargets", "leg", "target"),
                                ("groupStops", "group", "stop-loss"), ("groupTrailStops", "group", "trailing stop"),
                                ("groupTargets", "group", "target")):
            n = counts[key]
            if n:
                parts.append(f"{n} {noun}{'s' if n != 1 else ''} ({kind})")
        if not parts and not self.overall_trail_stop:
            return None
        if parts:
            text = f"Risk rules closed {', '.join(parts)}"
            if self.overall_trail_stop:
                text += " and ended the run on the overall trailing stop"
        else:
            text = "Risk rules ended the run on the overall trailing stop"
        return f"{text}; rules: {self.risk.describe()}."

    def _summary(self) -> Dict[str, Any]:
        notes = list(self.data_notes)
        if self.resolver.lookups or self.feed.no_data or self.feed.synced:
            notes.append(
                "Option premiums come from FYERS history for contracts that still exist; "
                "expired contracts have no history and were skipped."
            )
        # Skipped entries are reported per reason by the API's run view (it
        # groups `skippedEntries`), and the lot-size note is added there too,
        # so neither is repeated here.
        if self.ledger.ignored_legs:
            notes.append(f"{self.ledger.ignored_legs} close legs ignored: no matching open position.")
        if self.resolver.failed_lookups:
            notes.append(
                f"{self.resolver.failed_lookups} contract lookup(s) failed with an API error and were retried on the next bar; "
                "entries skipped at those bars are listed as 'contract lookup failed'."
            )
        if not self.broker_linked:
            notes.append("Broker not linked: only contracts already stored could be priced.")
        notes.extend(self.feed.sync_failures)
        risk_note = self._risk_note()
        if risk_note:
            notes.append(risk_note)
        return {
            "totalBars": self.total_bars,
            "barsProcessed": self.bars_processed,
            "sessions": len(self.sessions),
            "trades": self.ledger.trades,
            "skippedEntries": list(self.skipped_entries),
            "eodSquareOffs": self.eod_square_offs,
            "stopReason": self.stop_reason,
            "dataNotes": notes,
            "risk": self.risk.to_dict(),
            **{key: self.risk_counts[key] for key in RISK_COUNTERS},
            "overallTrailStop": self.overall_trail_stop,
            "charges": round(self.ledger.charges, 4),
            "realizedPnl": round(self.ledger.realized_pnl(), 4),
            "signalsPosted": self._signals_posted,
            "equityPoints": len(self.equity_points),
            "lotSize": self.lot_size,
            "lotSizeSource": self.lot_size_source,
            "warmupBars": self.warmup_bars_used,
            "syncedSymbols": list(self.feed.synced),
        }

    def _finish(self, status: str, error: Optional[str] = None) -> BacktestOutcome:
        summary = self._summary()
        risk_text = " ".join(f"{key}={self.risk_counts[key]}" for key in RISK_COUNTERS if self.risk_counts[key])
        self.log(
            f"[SUMMARY] status={status} bars={self.bars_processed}/{self.total_bars} sessions={summary['sessions']} "
            f"trades={summary['trades']} skipped={len(self.skipped_entries)} eod_square_offs={self.eod_square_offs} "
            f"pnl={_money(self.ledger.total_pnl())} charges={self.ledger.charges:,.2f}"
            f"{' ' + risk_text if risk_text else ''}"
            f"{' stop=' + self.stop_reason if self.stop_reason else ''}{' error=' + error if error else ''}"
        )
        self.api.complete_run(self.run.run_id, status, summary, error)
        return BacktestOutcome(status=status, summary=summary, error=error, stop_reason=self.stop_reason,
                               ledger=self.ledger, equity_points=self.equity_points)

    def fail(self, error: str) -> BacktestOutcome:
        """Best-effort Failed completion after an unexpected exception."""
        try:
            self._flush_snapshots(force=True)
        except Exception as ex:
            self.log(f"[ERROR] could not flush equity snapshots: {ex}")
        try:
            return self._finish("Failed", error=error)
        except Exception as ex:
            self.log(f"[ERROR] could not mark run {self.run.run_id} failed: {ex}")
            return BacktestOutcome(status="Failed", summary=self._summary(), error=error,
                                   stop_reason=self.stop_reason, ledger=self.ledger, equity_points=self.equity_points)


def run_backtest(api: Any, run_row: Dict[str, Any], strategy_factory: Callable[..., BaseStrategy],
                 on_progress: Optional[ProgressCallback] = None, *, log: Logger = print,
                 broker_linked: Optional[bool] = None, lot_size: Optional[int] = None,
                 lot_size_source: str = "", warmup_days: int = WARMUP_DAYS,
                 progress_interval: float = PROGRESS_INTERVAL_SECONDS,
                 snapshot_batch: int = SNAPSHOT_BATCH) -> BacktestOutcome:
    """
    Replay `run_row` (a SimulationRun with Mode "OfflineReplay") with the
    strategy built by `strategy_factory(params)`. Posts fills, marks, equity
    snapshots, progress and the completion summary through `api`.

    Returns the outcome; a `Failed` outcome carries the error text. SIGTERM
    (SystemExit) is not swallowed: the API marks such runs Stopped itself.
    """
    params = parse_parameters(run_row.get("parametersJson"))
    strategy = strategy_factory(params)
    run = parse_run_row(run_row, default_lots=getattr(strategy, "default_lots", 1))
    session = BacktestSession(
        api, run, strategy, on_progress=on_progress, log=log, broker_linked=broker_linked,
        lot_size=lot_size, lot_size_source=lot_size_source, warmup_days=warmup_days,
        progress_interval=progress_interval, snapshot_batch=snapshot_batch,
    )
    try:
        return session.execute()
    except SystemExit:
        try:
            session._flush_snapshots(force=True)
        except Exception:
            pass
        raise
    except Exception as ex:
        traceback.print_exc(file=sys.stderr)
        error = f"{type(ex).__name__}: {ex}"
        log(f"[ERROR] backtest failed: {error}")
        return session.fail(error)
