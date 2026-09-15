"""
Research regime classifier: synthetic sessions for each regime, the
no-look-ahead guarantee, the session-start edge cases, and the causal indicator
series checked against strategies.indicators.
"""

import math
import unittest
from dataclasses import replace
from datetime import date, datetime, time, timedelta, timezone
from typing import Callable, List

import _bootstrap  # noqa: F401

from backtest.timeutil import IST, iso_utc
from research import regime
from research.ta import directional_series, ema_series
from strategies import indicators
from strategies.base_strategy import BarFrame

SYMBOL = "NSE:NIFTY50-INDEX"
BASE = 24000.0


def trading_days(start: date, count: int) -> List[date]:
    out, cursor = [], start
    while len(out) < count:
        if cursor.weekday() < 5:
            out.append(cursor)
        cursor += timedelta(days=1)
    return out


def session(day: date, closes: List[float], spread: float, first_open: float = None,
            start: time = time(9, 15), volume: float = 0.0) -> List[BarFrame]:
    """Bars from a close path: each bar opens at the previous close, wicks `spread` beyond its body."""
    bars = []
    previous = closes[0] if first_open is None else first_open
    cursor = datetime.combine(day, start, tzinfo=IST)
    for close in closes:
        o = previous
        bars.append(BarFrame(SYMBOL, "5m", iso_utc(cursor), o, max(o, close) + spread, min(o, close) - spread,
                             close, volume))
        previous = close
        cursor += timedelta(minutes=5)
    return bars


def path(fn: Callable[[int], float], count: int = 75) -> List[float]:
    return [fn(k) for k in range(count)]


def quiet(k: int) -> float:
    """A driftless session: a slow sine, ordinary bar sizes."""
    return BASE + 20.0 * math.sin(2 * math.pi * k / 12.0)


def warm_sessions(count: int = 6) -> List[BarFrame]:
    bars = []
    for day in trading_days(date(2026, 8, 3), count):
        bars += session(day, path(quiet), spread=5.0)
    return bars


def next_day(bars: List[BarFrame], offset: int = 1) -> date:
    last = datetime.fromisoformat(bars[-1].timestamp_utc.replace("Z", "+00:00")).astimezone(IST).date()
    return trading_days(last + timedelta(days=1), offset)[-1]


def session_readings(readings, day: date):
    key = day.isoformat()
    return [r for r in readings if r.session == key]


class RegimeFixtureTests(unittest.TestCase):
    def setUp(self):
        self.warm = warm_sessions()

    def classify_day(self, closes: List[float], spread: float):
        day = next_day(self.warm)
        bars = self.warm + session(day, closes, spread)
        readings = regime.classify(bars)
        summary = regime.summarize_sessions(bars, readings)[-1]
        return session_readings(readings, day), summary

    def test_warm_quiet_sessions_are_range(self):
        readings = regime.classify(self.warm)
        last_day = [r for r in readings if r.session == readings[-1].session]
        labels = {r.label for r in last_day if r.ready}
        self.assertEqual(labels, {regime.RANGE})

    def test_steady_climb_is_trend_up(self):
        day, summary = self.classify_day(path(lambda k: BASE + 3.0 * k + 4.0 * math.sin(k)), spread=4.0)
        self.assertEqual(summary.dominant, regime.TREND_UP)
        after_ten = [r for r in day if r.session_bar >= 12]
        self.assertTrue(all(r.label == regime.TREND_UP for r in after_ten[-40:]))
        self.assertEqual(summary.counts[regime.TREND_DOWN], 0)
        self.assertIsNotNone(summary.first_called_ist)

    def test_steady_fall_is_trend_down(self):
        day, summary = self.classify_day(path(lambda k: BASE - 3.0 * k - 4.0 * math.sin(k)), spread=4.0)
        self.assertEqual(summary.dominant, regime.TREND_DOWN)
        self.assertEqual(summary.counts[regime.TREND_UP], 0)
        self.assertEqual(day[-1].label, regime.TREND_DOWN)
        self.assertLess(day[-1].vwap_distance_atr, 0)
        self.assertTrue(day[-1].or_broke_down)

    def test_quiet_oscillation_is_range(self):
        day, summary = self.classify_day(path(quiet), spread=5.0)
        self.assertEqual(summary.dominant, regime.RANGE)
        self.assertEqual(summary.counts[regime.TREND_UP] + summary.counts[regime.TREND_DOWN], 0)
        self.assertEqual(day[-1].vol_state, regime.VOL_NORMAL)

    def test_big_bars_without_direction_are_volatile_chop(self):
        closes = path(lambda k: BASE + (30.0 if k % 2 else -30.0))
        day, summary = self.classify_day(closes, spread=10.0)
        self.assertEqual(summary.dominant, regime.VOLATILE_CHOP)
        self.assertEqual(day[-1].label, regime.VOLATILE_CHOP)
        self.assertGreaterEqual(day[-1].vol_ratio, regime.RegimeConfig().vol_expanding)
        self.assertEqual(summary.counts[regime.TREND_UP] + summary.counts[regime.TREND_DOWN], 0)

    def test_every_ready_label_is_one_of_the_four(self):
        day, _ = self.classify_day(path(lambda k: BASE + 3.0 * k), spread=4.0)
        for r in day:
            if r.ready:
                self.assertIn(r.label, regime.LABELS)
            else:
                self.assertIsNone(r.label)


class NoLookAheadTests(unittest.TestCase):
    def build(self) -> List[BarFrame]:
        bars = warm_sessions()
        day1 = next_day(bars)
        bars += session(day1, path(lambda k: BASE + 3.0 * k), spread=4.0)
        day2 = next_day(bars)
        bars += session(day2, path(lambda k: BASE + (30.0 if k % 2 else -30.0)), spread=10.0,
                        first_open=BASE + 400)
        return bars

    def test_labels_up_to_t_do_not_change_when_future_bars_are_appended(self):
        bars = self.build()
        full = regime.classify(bars)
        # Mid-session, the last opening-range bar, a session's last and first bar.
        cuts = [5, 3, 450 + 2, 450 + 3, 450 + 74, 450 + 75, 450 + 76, 450 + 110, len(bars) - 1]
        for t in cuts:
            with self.subTest(t=t):
                prefix = regime.classify(bars[:t])
                self.assertEqual(prefix, full[:t])

    def test_rewriting_the_future_leaves_the_past_alone(self):
        bars = self.build()
        full = regime.classify(bars)
        t = 450 + 30
        wild = [replace(b, open=b.open * 1.05, high=b.high * 1.1, low=b.low * 0.9, close=b.close * 0.95)
                for b in bars[t:]]
        rewritten = regime.classify(bars[:t] + wild)
        self.assertEqual(rewritten[:t], full[:t])
        self.assertNotEqual(rewritten[t:], full[t:])

    def test_volatility_baseline_uses_only_finished_sessions(self):
        bars = self.build()
        full = regime.classify(bars)
        # A monster final session must not change the ratio seen on the day before it.
        day2_start = 450 + 75
        calmer = regime.classify(bars[:day2_start])
        self.assertEqual([r.vol_ratio for r in calmer], [r.vol_ratio for r in full[:day2_start]])


class SessionStartTests(unittest.TestCase):
    def test_first_bar_of_a_session(self):
        bars = warm_sessions()
        day = next_day(bars)
        bars += session(day, path(quiet), spread=5.0)
        readings = regime.classify(bars)
        first = session_readings(readings, day)[0]

        self.assertEqual(first.session_bar, 0)
        self.assertFalse(first.ready)
        self.assertIsNone(first.label)
        self.assertIn("opening range", first.reason)
        self.assertEqual(first.or_state, regime.OR_FORMING)
        self.assertIsNone(first.or_high)
        bar = bars[-75]
        self.assertAlmostEqual(first.vwap, (bar.high + bar.low + bar.close) / 3.0)
        self.assertEqual(first.votes["vwap_side"], 0)
        self.assertEqual(first.votes["opening_range"], 0)

    def test_opening_range_completes_on_the_0925_bar(self):
        bars = warm_sessions()
        day = next_day(bars)
        bars += session(day, path(quiet), spread=5.0)
        today = session_readings(regime.classify(bars), day)
        self.assertEqual([r.or_state for r in today[:2]], [regime.OR_FORMING] * 2)
        self.assertEqual(today[2].or_state, regime.OR_INSIDE)
        self.assertTrue(today[2].ready)
        opening = bars[-75:-72]
        self.assertEqual(today[2].or_high, max(b.high for b in opening))
        self.assertEqual(today[2].or_low, min(b.low for b in opening))

    def test_the_overnight_gap_is_not_read_as_volatility(self):
        base = warm_sessions()
        day = next_day(base)
        flat = base + session(day, path(quiet), spread=5.0, first_open=quiet(0))
        gapped = base + session(day, [c + 500 for c in path(quiet)], spread=5.0, first_open=quiet(0) + 500)
        a = session_readings(regime.classify(flat), day)
        b = session_readings(regime.classify(gapped), day)
        self.assertAlmostEqual(a[0].atr, b[0].atr)
        self.assertAlmostEqual(a[0].adx, b[0].adx)

    def test_very_first_bars_of_the_data_are_not_ready(self):
        bars = session(date(2026, 8, 3), path(quiet), spread=5.0)
        readings = regime.classify(bars)
        self.assertTrue(all(not r.ready and r.label is None for r in readings))
        self.assertIn("volatility baseline", readings[-1].reason)
        summary = regime.summarize_sessions(bars, readings)[0]
        self.assertIsNone(summary.dominant)

    def test_a_session_that_starts_after_the_opening_window(self):
        bars = warm_sessions()
        day = next_day(bars)
        bars += session(day, path(quiet, 60), spread=5.0, start=time(10, 0))
        today = session_readings(regime.classify(bars), day)
        self.assertEqual(today[0].or_state, regime.OR_UNAVAILABLE)
        self.assertTrue(today[0].ready)
        self.assertIn(today[0].label, regime.LABELS)

    def test_empty_input(self):
        self.assertEqual(regime.classify([]), [])


class VwapWeightingTests(unittest.TestCase):
    def test_index_vwap_is_time_weighted_even_when_volume_is_reported(self):
        bars = session(date(2026, 8, 3), [100.0, 110.0], spread=1.0, volume=0.0)
        bars[0] = replace(bars[0], volume=1000)
        bars[1] = replace(bars[1], volume=1)
        readings = regime.classify(bars)
        typical = [(b.high + b.low + b.close) / 3.0 for b in bars]
        self.assertAlmostEqual(readings[1].vwap, sum(typical) / 2.0)
        self.assertEqual(readings[1].vwap_weighting, "time")

    def test_volume_mode_matches_strategies_indicators_vwap(self):
        bars = [replace(b, symbol="NSE:RELIANCE-EQ", volume=100.0 * (i + 1))
                for i, b in enumerate(session(date(2026, 8, 3), path(quiet, 20), spread=2.0))]
        readings = regime.classify(bars)
        for i in (0, 5, 19):
            self.assertAlmostEqual(readings[i].vwap, indicators.vwap(bars[:i + 1]))
            self.assertEqual(readings[i].vwap_weighting, "volume")


class SeriesTests(unittest.TestCase):
    def test_ema_series_matches_strategies_indicators_at_every_bar(self):
        values = [BASE + 10 * math.sin(k / 3.0) + k for k in range(80)]
        series = ema_series(values, 21)
        for i in range(80):
            expected = indicators.ema(values[:i + 1], 21)
            if expected is None:
                self.assertIsNone(series[i])
            else:
                self.assertAlmostEqual(series[i], expected, places=9)

    def test_rising_bars_have_plus_di_above_minus_di_and_a_high_adx(self):
        bars = session(date(2026, 8, 3), path(lambda k: 100.0 + 2.0 * k, 40), spread=0.5)
        points = directional_series(bars, 14)
        self.assertIsNone(points[25].adx)
        self.assertIsNotNone(points[26].adx)
        self.assertGreater(points[-1].plus_di, points[-1].minus_di)
        self.assertGreater(points[-1].adx, 50)

    def test_constant_range_bars_have_that_range_as_atr(self):
        bars = [BarFrame(SYMBOL, "5m", iso_utc(datetime(2026, 8, 3, 3, 45, tzinfo=timezone.utc)
                                               + timedelta(minutes=5 * k)), 100, 102, 98, 100) for k in range(30)]
        points = directional_series(bars, 14)
        self.assertIsNone(points[12].atr)
        self.assertAlmostEqual(points[13].atr, 4.0)
        self.assertAlmostEqual(points[-1].atr, 4.0)
        self.assertEqual(points[-1].adx, 0.0)


if __name__ == "__main__":
    unittest.main()
