"""
Research harness: next-bar-open fills from real premium rows, following a fixed
strike through rolling-ATM data (including drift outside the offset window),
resting and close-decided exits, costs and R, skipped entries when a premium is
missing, no look-ahead, walk-forward selection on train only, metrics, the
seed candidates, and the pure data helpers.
"""

import math
import unittest
from dataclasses import replace
from datetime import date, datetime, time, timedelta
from typing import Dict, List, Optional, Tuple

import _bootstrap  # noqa: F401

from backtest.timeutil import IST, iso_utc
from research import candidates, data, harness, metrics, regime
from research.candidate import CE, PE, BarContext, Candidate, ExitPolicy, Features, Signal
from research.costs import CostModel
from research.data import PremiumBar, PremiumBook
from strategies.base_strategy import BarFrame

from test_research_regime import BASE, next_day, path, quiet, session, trading_days, warm_sessions

LOT = 65
STEP = 50.0


def stamp(day: date, hh: int, mm: int) -> str:
    return iso_utc(datetime.combine(day, time(hh, mm), tzinfo=IST))


def prow(ts: str, otype: str, strike: float, offset: int, o: float, h: float = None, l: float = None,
         c: float = None) -> PremiumBar:
    return PremiumBar(ts, "NIFTY", "WEEK", 1, "2026-08-18", offset, strike, otype, "5m", o,
                      o if h is None else h, o if l is None else l, o if c is None else c, 0, None, None, None)


class BookBuilder:
    """Rolling-ATM rows: at each bar, offsets -N..N around that bar's ATM strike."""

    def __init__(self, window: int = 2) -> None:
        self.window = window
        self.rows: Dict[Tuple[str, str, float], PremiumBar] = {}

    def fill(self, bars: List[BarFrame], atm_of, price_of) -> "BookBuilder":
        for k, bar in enumerate(bars):
            atm = atm_of(k, bar)
            for offset in range(-self.window, self.window + 1):
                strike = atm + offset * STEP
                for otype in (CE, PE):
                    ohlc = price_of(k, bar, otype, strike)
                    if ohlc is None:
                        continue
                    self.rows[(bar.timestamp_utc, otype, strike)] = prow(bar.timestamp_utc, otype, strike, offset,
                                                                          *ohlc)
        return self

    def set(self, ts: str, otype: str, strike: float, o: float, h: float, l: float, c: float) -> None:
        existing = self.rows[(ts, otype, strike)]
        self.rows[(ts, otype, strike)] = replace(existing, open=o, high=h, low=l, close=c)

    def drop(self, ts: str) -> None:
        for key in [k for k in self.rows if k[0] == ts]:
            del self.rows[key]

    def book(self) -> PremiumBook:
        return PremiumBook(self.rows.values())


def scripted(signals: Dict[Tuple[str, str], str], policy: ExitPolicy, name: str = "scripted",
             grid=None, defaults=None, entry=None) -> Candidate:
    def default_entry(ctx: BarContext) -> Optional[Signal]:
        side = signals.get((ctx.session, ctx.ist.strftime("%H:%M")))
        return Signal(side, "scripted") if side else None

    defaults = defaults or {}
    return Candidate(name=name, title=name, rules="test", defaults=defaults,
                     parameter_notes={k: "test" for k in defaults}, entry=entry or default_entry,
                     exits=lambda params: policy, grid=grid or {})


class Fixture:
    def __init__(self, closes=None) -> None:
        warm = warm_sessions()
        self.day = next_day(warm)
        self.today = session(self.day, closes or path(quiet), spread=5.0)
        self.bars = warm + self.today
        self.readings = regime.classify(self.bars)
        self.features = Features(self.bars, self.readings)
        self.config = harness.SimConfig(underlying="NIFTY", lot_size=LOT)

    def at(self, hh: int, mm: int) -> str:
        return stamp(self.day, hh, mm)

    def index_of(self, hh: int, mm: int) -> int:
        ts = self.at(hh, mm)
        return next(i for i, b in enumerate(self.bars) if b.timestamp_utc == ts)

    def flat_book(self, atm_of=None, window: int = 2) -> BookBuilder:
        today_keys = {b.timestamp_utc for b in self.today}
        builder = BookBuilder(window)
        return builder.fill(self.today, atm_of or (lambda k, bar: 24000.0),
                            lambda k, bar, otype, strike: (100.0, 100.0, 100.0, 100.0)
                            if bar.timestamp_utc in today_keys else None)

    def run(self, candidate: Candidate, book: PremiumBook, features: Features = None, **kwargs):
        return harness.simulate(candidate, candidate.params(), features or self.features, book, self.config,
                                **kwargs)


class FillTests(unittest.TestCase):
    def setUp(self):
        self.fx = Fixture()
        self.signal = {(self.fx.day.isoformat(), "10:00"): CE}

    def test_entry_fills_at_the_next_bar_open_of_the_offset_row(self):
        builder = self.fx.flat_book()
        builder.set(self.fx.at(10, 0), CE, 24000.0, 90, 90, 90, 90)
        builder.set(self.fx.at(10, 5), CE, 24000.0, 104, 106, 103, 105)
        result = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book())
        trade = result.trades[0]
        self.assertEqual(trade.entry_utc, self.fx.at(10, 5))
        self.assertEqual(trade.entry_premium, 104)
        self.assertAlmostEqual(trade.entry_fill, 104 + 0.52)
        self.assertEqual(trade.strike, 24000.0)
        self.assertEqual(trade.signal_ist, "10:00")

    def test_a_fixed_strike_is_followed_when_atm_moves(self):
        moved = lambda k, bar: 24000.0 if bar.timestamp_utc < self.fx.at(10, 30) else 24100.0  # noqa: E731
        builder = self.fx.flat_book(atm_of=moved)
        for bar in self.fx.today:
            if bar.timestamp_utc >= self.fx.at(10, 30):
                builder.set(bar.timestamp_utc, CE, 24000.0, 120, 120, 120, 120)   # the held contract
                builder.set(bar.timestamp_utc, CE, 24100.0, 50, 50, 50, 50)       # the new ATM
        trade = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book()).trades[0]
        self.assertEqual(trade.exit_reason, "FORCED_EXIT")
        self.assertEqual(trade.exit_utc, self.fx.at(15, 15))
        self.assertEqual(trade.exit_premium, 120)
        self.assertEqual(trade.strike, 24000.0)
        self.assertEqual(trade.unpriced_bars, 0)

    def test_a_strike_that_drifts_outside_the_window_exits_stale_at_the_last_known_premium(self):
        moved = lambda k, bar: 24000.0 if bar.timestamp_utc < self.fx.at(11, 0) else 24150.0  # noqa: E731
        builder = self.fx.flat_book(atm_of=moved)
        builder.set(self.fx.at(10, 55), CE, 24000.0, 100, 140, 99, 137)
        trade = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book()).trades[0]
        self.assertTrue(trade.stale_exit)
        self.assertEqual(trade.exit_reason, "FORCED_EXIT_UNPRICED")
        self.assertEqual(trade.exit_premium, 137)
        self.assertEqual(trade.unpriced_bars, 54)   # 11:00 .. 15:25

    def test_a_pending_exit_waits_for_the_strike_to_be_priced_again(self):
        def moved(k, bar):
            return 24150.0 if self.fx.at(11, 0) <= bar.timestamp_utc < self.fx.at(15, 20) else 24000.0
        builder = self.fx.flat_book(atm_of=moved)
        builder.set(self.fx.at(15, 20), CE, 24000.0, 131, 131, 131, 131)
        trade = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book()).trades[0]
        self.assertFalse(trade.stale_exit)
        self.assertEqual(trade.exit_reason, "FORCED_EXIT")
        self.assertEqual(trade.exit_utc, self.fx.at(15, 20))
        self.assertEqual(trade.exit_premium, 131)

    def test_missing_premium_at_the_fill_bar_is_a_skip_never_a_made_up_price(self):
        builder = self.fx.flat_book()
        builder.drop(self.fx.at(10, 5))
        result = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book())
        self.assertEqual(result.trades, [])
        self.assertEqual(len(result.skipped), 1)
        self.assertIn("no CE premium", result.skipped[0].reason)

    def test_an_empty_book_trades_nothing(self):
        result = self.fx.run(scripted(self.signal, ExitPolicy()), PremiumBook([]))
        self.assertEqual(result.trades, [])
        self.assertEqual(len(result.skipped), 1)

    def test_no_entry_after_the_cutoff_or_on_the_last_bar(self):
        late = {(self.fx.day.isoformat(), "14:30"): CE, (self.fx.day.isoformat(), "15:25"): PE}
        result = self.fx.run(scripted(late, ExitPolicy()), self.fx.flat_book().book())
        self.assertEqual(result.trades, [])
        self.assertEqual(result.skipped, [])


class ExitTests(unittest.TestCase):
    def setUp(self):
        self.fx = Fixture()
        self.signal = {(self.fx.day.isoformat(), "10:00"): CE}

    def run_with(self, policy: ExitPolicy, bars: Dict[Tuple[int, int], Tuple[float, float, float, float]],
                 features: Features = None):
        builder = self.fx.flat_book()
        for (hh, mm), ohlc in bars.items():
            builder.set(self.fx.at(hh, mm), CE, 24000.0, *ohlc)
        return self.fx.run(scripted(self.signal, policy), builder.book(), features=features).trades[0]

    def test_premium_stop_fills_at_its_level_inside_the_bar(self):
        trade = self.run_with(ExitPolicy(stop_premium_pct=20), {(10, 10): (95, 96, 79, 85)})
        self.assertEqual(trade.exit_reason, "PREMIUM_STOP")
        self.assertAlmostEqual(trade.exit_premium, 100.5 * 0.8)
        self.assertAlmostEqual(trade.exit_fill, 100.5 * 0.8 * 0.995)

    def test_a_gap_through_the_stop_fills_at_the_open(self):
        trade = self.run_with(ExitPolicy(stop_premium_pct=20), {(10, 10): (70, 72, 60, 65)})
        self.assertEqual(trade.exit_premium, 70)

    def test_stop_is_assumed_before_target_when_a_bar_touches_both(self):
        trade = self.run_with(ExitPolicy(stop_premium_pct=20, target_premium_pct=50), {(10, 10): (100, 200, 50, 150)})
        self.assertEqual(trade.exit_reason, "PREMIUM_STOP")

    def test_target_fills_at_its_level(self):
        trade = self.run_with(ExitPolicy(target_premium_pct=50), {(10, 10): (110, 160, 108, 150)})
        self.assertEqual(trade.exit_reason, "TARGET")
        self.assertAlmostEqual(trade.exit_premium, 100.5 * 1.5)

    def test_trailing_stop_arms_on_one_bar_and_trips_on_a_later_one(self):
        policy = ExitPolicy(trail_arm_pct=20, trail_giveback_pct=10)
        trade = self.run_with(policy, {(10, 10): (110, 130, 101, 128), (10, 15): (125, 126, 115, 118)})
        self.assertEqual(trade.exit_reason, "TRAIL_STOP")
        self.assertEqual(trade.exit_utc, self.fx.at(10, 15))
        self.assertAlmostEqual(trade.exit_premium, 100.5 + (130 - 100.5) - 10.05)

    def test_time_stop_exits_at_the_open_after_n_bars(self):
        trade = self.run_with(ExitPolicy(time_stop_bars=3), {})
        self.assertEqual(trade.exit_reason, "TIME_STOP")
        self.assertEqual(trade.exit_utc, self.fx.at(10, 20))
        self.assertEqual(trade.bars_held, 3)

    def test_regime_flip_exits_at_the_next_open(self):
        readings = list(self.fx.readings)
        i = self.fx.index_of(11, 0)
        readings[i] = replace(readings[i], label=regime.TREND_DOWN)
        trade = self.run_with(ExitPolicy(exit_on_regime_flip=True), {}, Features(self.fx.bars, readings))
        self.assertEqual(trade.exit_reason, "REGIME_FLIP")
        self.assertEqual(trade.exit_utc, self.fx.at(11, 5))

    def test_range_is_not_a_flip(self):
        readings = list(self.fx.readings)
        i = self.fx.index_of(11, 0)
        readings[i] = replace(readings[i], label=regime.RANGE)
        trade = self.run_with(ExitPolicy(exit_on_regime_flip=True), {}, Features(self.fx.bars, readings))
        self.assertEqual(trade.exit_reason, "FORCED_EXIT")

    def test_index_atr_stop_is_decided_at_the_close(self):
        bars = list(self.fx.bars)
        i_signal, i_drop = self.fx.index_of(10, 0), self.fx.index_of(10, 30)
        atr = self.fx.readings[i_signal].atr
        bars[i_drop] = replace(bars[i_drop], close=bars[i_signal].close - 2.0 * atr)
        trade = self.run_with(ExitPolicy(stop_underlying_atr=1.5), {}, Features(bars, self.fx.readings))
        self.assertEqual(trade.exit_reason, "INDEX_ATR_STOP")
        self.assertEqual(trade.exit_utc, self.fx.at(10, 35))

    def test_session_data_ending_before_the_forced_exit_closes_at_the_last_close(self):
        cut = self.fx.index_of(13, 0)
        bars = self.fx.bars[:cut + 1]
        builder = self.fx.flat_book()
        builder.set(self.fx.at(13, 0), CE, 24000.0, 100, 100, 100, 111)
        trade = self.fx.run(scripted(self.signal, ExitPolicy()), builder.book(),
                            features=Features(bars, self.fx.readings[:cut + 1])).trades[0]
        self.assertEqual(trade.exit_reason, "SESSION_END")
        self.assertEqual(trade.exit_premium, 111)


class CostTests(unittest.TestCase):
    def test_charges_follow_the_documented_formula(self):
        costs = CostModel()
        c = costs.charges(10000.0, 12000.0)
        self.assertAlmostEqual(c["brokerage"], 40.0)
        self.assertAlmostEqual(c["stt"], 18.0)
        self.assertAlmostEqual(c["exchange"], 22000 * 0.0003503)
        self.assertAlmostEqual(c["stamp"], 0.3)
        self.assertAlmostEqual(c["gst"], (40.0 + 22000 * 0.0003503 + 22000 * 0.000001) * 0.18)
        self.assertAlmostEqual(c["total"], sum(v for k, v in c.items() if k != "total"))

    def test_slippage_has_a_floor(self):
        costs = CostModel()
        self.assertAlmostEqual(costs.buy_fill(2.0), 2.05)
        self.assertAlmostEqual(costs.sell_fill(0.02), 0.0)

    def test_trade_net_and_r(self):
        fx = Fixture()
        builder = fx.flat_book()
        for bar in fx.today:
            if bar.timestamp_utc >= fx.at(11, 0):
                builder.set(bar.timestamp_utc, CE, 24000.0, 130, 130, 130, 130)
        policy = ExitPolicy(stop_premium_pct=25)
        trade = fx.run(scripted({(fx.day.isoformat(), "10:00"): CE}, policy), builder.book()).trades[0]
        entry_fill, exit_fill = 100.5, 130 * 0.995
        self.assertAlmostEqual(trade.gross_pnl, (exit_fill - entry_fill) * LOT)
        charges = CostModel().charges(entry_fill * LOT, exit_fill * LOT)["total"]
        self.assertAlmostEqual(trade.net_pnl, trade.gross_pnl - charges)
        self.assertAlmostEqual(trade.r_multiple, trade.net_pnl / (entry_fill * 0.25 * LOT))


class NoLookAheadHarnessTests(unittest.TestCase):
    def test_a_candidate_cannot_ask_for_a_future_bar(self):
        fx = Fixture()

        def peeking(ctx: BarContext):
            return Signal(CE, "peek") if ctx.ema(21, back=-1) else None

        with self.assertRaises(ValueError):
            fx.run(scripted({}, ExitPolicy(), entry=peeking), fx.flat_book().book())

    def test_trades_do_not_change_when_a_future_session_is_appended(self):
        fx = Fixture()
        book_rows = dict(fx.flat_book().rows)
        signal = {(fx.day.isoformat(), "10:00"): CE, (fx.day.isoformat(), "12:00"): PE}
        candidate = scripted(signal, ExitPolicy(time_stop_bars=6, exit_on_regime_flip=True))
        before = fx.run(candidate, PremiumBook(book_rows.values())).trades

        later = next_day(fx.bars)
        future = session(later, [c + 300 for c in path(quiet)], spread=40.0)
        bars = fx.bars + future
        readings = regime.classify(bars)
        after = harness.simulate(candidate, {}, Features(bars, readings), PremiumBook(book_rows.values()),
                                 fx.config).trades
        self.assertEqual([t.to_dict() for t in before], [t.to_dict() for t in after])


class WalkForwardTests(unittest.TestCase):
    def test_windows_roll_by_calendar_month(self):
        windows = harness.make_windows(date(2026, 1, 5), date(2026, 5, 20))
        self.assertEqual(len(windows), 2)
        self.assertEqual((windows[0].train_start, windows[0].train_end), (date(2026, 1, 5), date(2026, 4, 4)))
        self.assertEqual((windows[0].test_start, windows[0].test_end), (date(2026, 4, 5), date(2026, 5, 4)))
        self.assertFalse(windows[0].partial_test)
        self.assertEqual((windows[1].test_start, windows[1].test_end), (date(2026, 5, 5), date(2026, 5, 20)))
        self.assertTrue(windows[1].partial_test)

    def test_too_little_history_has_no_windows(self):
        self.assertEqual(harness.make_windows(date(2026, 6, 4), date(2026, 9, 3)), [])

    def test_parameters_are_chosen_on_train_and_judged_on_test(self):
        days = trading_days(date(2026, 1, 1), 110)
        bars: List[BarFrame] = []
        for day in days:
            bars += session(day, path(quiet), spread=5.0)
        features = Features(bars, regime.classify(bars))
        rows = []
        switch = date(2026, 4, 1)
        for day in days:
            good_side = CE if day < switch else PE
            for hh, mm, ce, pe in ((10, 5, 100, 100), (10, 10, 100, 100), (10, 15, None, None)):
                ts = stamp(day, hh, mm)
                if ce is None:
                    ce, pe = (110, 90) if good_side == CE else (90, 110)
                rows.append(prow(ts, CE, 24000.0, 0, ce))
                rows.append(prow(ts, PE, 24000.0, 0, pe))
        book = PremiumBook(rows)

        def entry(ctx: BarContext):
            if ctx.ist.strftime("%H:%M") == "10:00":
                return Signal(ctx.params["side"], "10:00")
            return None

        candidate = scripted({}, ExitPolicy(time_stop_bars=2), name="side_picker", defaults={"side": CE},
                             grid={"side": [CE, PE]}, entry=entry)
        config = harness.SimConfig(underlying="NIFTY", lot_size=LOT)
        wf = harness.walk_forward(candidate, features, book, config, train_months=2, test_months=1,
                                  min_train_trades=5)
        first = wf.folds[0]
        # First ready session is 8 Jan (five finished sessions build the volatility baseline).
        self.assertEqual(first.window.test_start, date(2026, 3, 8))
        self.assertEqual(first.chosen_params["side"], CE)
        march = [t for t in first.test.trades if t.session < "2026-04-01"]
        april = [t for t in first.test.trades if t.session >= "2026-04-01"]
        self.assertTrue(all(t.net_pnl > 0 for t in march))
        self.assertTrue(april and all(t.net_pnl < 0 for t in april))
        later = [f for f in wf.folds if f.window.train_start >= date(2026, 4, 1)]
        for fold in later:
            self.assertEqual(fold.chosen_params["side"], PE)


class MetricsTests(unittest.TestCase):
    class T:
        def __init__(self, net, r=None, exit_utc="x", bars_held=1):
            self.net_pnl, self.r_multiple, self.exit_utc, self.bars_held = net, r, exit_utc, bars_held

    def test_summary_numbers(self):
        trades = [self.T(100, 1.0), self.T(-50, -0.5), self.T(-30, -0.3), self.T(200, 2.0), self.T(-40, -0.4)]
        s = metrics.summarize(trades)
        self.assertEqual(s["trades"], 5)
        self.assertAlmostEqual(s["win_rate"], 0.4)
        self.assertAlmostEqual(s["avg_win"], 150)
        self.assertAlmostEqual(s["avg_loss"], -40)
        self.assertAlmostEqual(s["expectancy"], 36)
        self.assertAlmostEqual(s["expectancy_r"], 0.36)
        self.assertAlmostEqual(s["profit_factor"], 300 / 120)
        self.assertAlmostEqual(s["max_drawdown"], 80)
        self.assertEqual(s["longest_losing_streak"], 2)

    def test_empty_summary_has_no_invented_numbers(self):
        s = metrics.summarize([])
        self.assertEqual(s["trades"], 0)
        self.assertIsNone(s["win_rate"])
        self.assertIsNone(s["profit_factor"])

    def test_time_buckets(self):
        self.assertEqual(metrics.time_bucket("09:40"), "09:15-10:00")
        self.assertEqual(metrics.time_bucket("13:30"), "13:30-15:30")


class SeedCandidateTests(unittest.TestCase):
    def test_every_candidate_has_at_most_four_documented_parameters(self):
        self.assertEqual(set(candidates.names()),
                         {"baseline_ema_trend", "orb_breakout_buy", "vwap_pullback_buy", "regime_momentum_buy"})
        for candidate in candidates.CANDIDATES.values():
            self.assertLessEqual(len(candidate.defaults), 4)
            self.assertEqual(set(candidate.defaults), set(candidate.parameter_notes))
            candidate.exits(candidate.params())
            for params in harness.grid_combinations(candidate):
                candidate.exits(params)

    def test_regime_candidates_do_not_buy_calls_on_a_down_trend_day(self):
        fx = Fixture(closes=path(lambda k: BASE - 3.0 * k - 4.0 * math.sin(k)))
        total = 0
        for name in ("orb_breakout_buy", "vwap_pullback_buy", "regime_momentum_buy"):
            candidate = candidates.get(name)
            rows = harness.signal_census(candidate, candidate.params(), fx.features, fx.config,
                                         start=fx.day, end=fx.day)
            total += len(rows)
            for row in rows:
                self.assertEqual(row.side, PE, name)
                self.assertFalse(row.against_regime, name)
        self.assertGreater(total, 0)

    def test_baseline_ignores_the_regime(self):
        fx = Fixture(closes=path(lambda k: BASE + 25.0 * math.sin(2 * math.pi * k / 10.0)))
        candidate = candidates.get("baseline_ema_trend")
        rows = harness.signal_census(candidate, candidate.params(), fx.features, fx.config, cooldown_bars=0,
                                     start=fx.day, end=fx.day)
        self.assertEqual({r.side for r in rows}, {CE, PE})


class DataHelperTests(unittest.TestCase):
    def test_rollup_aligns_buckets_to_the_session_open(self):
        day = date(2026, 8, 3)
        minutes = [BarFrame("X", "1m", stamp(day, 9, 15 + k), 100 + k, 101 + k, 99 + k, 100.5 + k, 10)
                   for k in range(10)]
        rolled = data.rollup(minutes, 5)
        self.assertEqual([b.timestamp_utc for b in rolled], [stamp(day, 9, 15), stamp(day, 9, 20)])
        self.assertEqual((rolled[0].open, rolled[0].high, rolled[0].low, rolled[0].close, rolled[0].volume),
                         (100, 105, 99, 104.5, 50))

    def test_research_bars_stop_at_the_1515_bar(self):
        day = date(2026, 9, 10)
        at = lambda hh, mm: datetime.combine(day, time(hh, mm), tzinfo=IST)  # noqa: E731
        self.assertTrue(data.in_nse_session(at(9, 15)))
        self.assertTrue(data.in_nse_session(at(15, 15)))
        self.assertFalse(data.in_nse_session(at(15, 20)))
        self.assertFalse(data.in_nse_session(at(9, 10)))
        self.assertTrue(data.in_nse_session(at(15, 25), time(15, 29)))
        self.assertEqual(data.expected_bars(5), 73)
        self.assertEqual(data.expected_bars(5, time(15, 25)), 75)
        self.assertEqual(data.expected_bars(1, time(15, 29)), 375)

    def test_premium_rollup_groups_by_strike_not_offset(self):
        day = date(2026, 8, 3)
        rows = [prow(stamp(day, 9, 15), CE, 24000.0, 0, 100), prow(stamp(day, 9, 16), CE, 24000.0, 0, 102),
                prow(stamp(day, 9, 17), CE, 24050.0, 0, 80), prow(stamp(day, 9, 17), CE, 24000.0, -1, 104)]
        rolled = data.rollup_premiums(rows, 5)
        by_strike = {r.strike: r for r in rolled}
        self.assertEqual(set(by_strike), {24000.0, 24050.0})
        self.assertEqual((by_strike[24000.0].open, by_strike[24000.0].close), (100, 104))
        self.assertEqual(by_strike[24000.0].strike_offset, 0)      # its offset at the bar's open
        self.assertTrue(by_strike[24000.0].opens_bar)
        self.assertFalse(by_strike[24050.0].opens_bar)             # joined at 09:17: no entry price
        book = PremiumBook(rolled)
        self.assertEqual(book.at_offset(stamp(day, 9, 15), CE, 0).strike, 24000.0)
        self.assertEqual(book.at_strike(stamp(day, 9, 15), CE, 24050.0).close, 80)

    def test_stitched_native_bars_are_counted(self):
        day = date(2026, 8, 3)
        rows = [prow(stamp(day, 9, 15), CE, 24000.0, 0, 100), prow(stamp(day, 9, 20), CE, 24000.0, 0, 101),
                prow(stamp(day, 9, 25), CE, 24050.0, 0, 90), prow(stamp(day, 9, 25), PE, 24050.0, 0, 90)]
        self.assertEqual(data.stitched_bars(rows, 5), 1)

    def test_book_lookups(self):
        ts = stamp(date(2026, 8, 3), 10, 0)
        book = PremiumBook([prow(ts, CE, 24000.0, 0, 100), prow(ts, CE, 23950.0, -1, 130)])
        self.assertEqual(book.at_offset(ts, "ce", -1).strike, 23950.0)
        self.assertEqual(book.at_strike(ts, CE, 24000.0).open, 100)
        self.assertIsNone(book.at_strike(ts, CE, 24000.0, expiry_date="2026-08-25"))
        self.assertEqual(book.offset_window, (-1, 0))

    def test_missing_table_is_a_clear_error(self):
        class Cursor:
            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def execute(self, sql, params=None):
                self.sql = sql

            def fetchone(self):
                return (None,)

        class Conn:
            def cursor(self):
                return Cursor()

        with self.assertRaises(data.PremiumDataUnavailable) as caught:
            data.premium_series("NIFTY", 0, CE, date(2026, 8, 1), date(2026, 8, 31), conn=Conn())
        self.assertIn("does not exist", str(caught.exception))
        self.assertIn("never estimated from the index", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
