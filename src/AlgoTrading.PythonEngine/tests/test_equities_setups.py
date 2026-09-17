"""
Stock-picker setups on 5-minute bars: charges match the blueprint's worked
example, signals fill on the next bar, the stop wins a bar that touches both
levels, and the portfolio pass enforces its limits in time order.
"""

import unittest

import numpy as np

from research.equities import setups as st


def session(rows, symbol="AAA", day="2025-03-03", start="09:15"):
    """rows: (open, high, low, close[, volume]) per 5-minute bar from `start`."""
    h, m = map(int, start.split(":"))
    times = []
    for k in range(len(rows)):
        total = h * 60 + m + 5 * k
        times.append(f"{total // 60:02d}:{total % 60:02d}")
    arr = np.array([list(r) + [1000.0] * (5 - len(r)) for r in rows], dtype=float)
    return st.SessionBars(symbol, day, times, arr[:, 0], arr[:, 1], arr[:, 2], arr[:, 3], arr[:, 4])


def flat(n, price=100.0):
    return [(price, price + 0.2, price - 0.2, price)] * n


class CostTests(unittest.TestCase):
    def test_blueprint_worked_example_one_lakh_each_side(self):
        self.assertAlmostEqual(82.445, st.EquityCosts().charges(100_000, 100_000), places=2)

    def test_small_orders_pay_percentage_brokerage_not_the_cap(self):
        # Rs 10,000 a side: brokerage 0.03% = Rs 3 per order.
        c = st.EquityCosts()
        self.assertLess(c.charges(10_000, 10_000), 12.0)


class OrbTests(unittest.TestCase):
    def test_breakout_fills_next_bar_open_with_the_range_as_stop_and_2r_target(self):
        bars = session([(100, 101, 99, 100), (100, 101, 99, 100.5), (100.5, 101, 99.5, 100.8),
                        (100.8, 101.6, 100.7, 101.5),   # 09:30 closes above 101
                        (101.6, 101.9, 101.2, 101.8)] + flat(40, 101.8))
        c = st.orb(bars, st.Context(100, 101, 99, 2.0, rvol15=3.0))
        self.assertEqual(("09:30", "09:35", 1), (c.signal_time, c.entry_time, c.side))
        self.assertEqual(101.6, c.entry_price)
        self.assertEqual(99.0, c.stop)
        self.assertAlmostEqual(101.6 + 2 * 2.6, c.target)

    def test_no_trade_without_relative_volume(self):
        bars = session([(100, 101, 99, 100)] * 3 + [(100.8, 101.6, 100.7, 101.5)] + flat(5, 101.8))
        self.assertIsNone(st.orb(bars, st.Context(100, 101, 99, 2.0, rvol15=1.2)))
        self.assertIsNone(st.orb(bars, st.Context(100, 101, 99, 2.0, rvol15=None)))


class WalkTests(unittest.TestCase):
    def test_a_bar_touching_stop_and_target_counts_as_the_stop(self):
        bars = session(flat(3) + [(100, 100.1, 99.9, 100), (100, 104, 98, 100)] + flat(80))
        c = st.Candidate("AAA", "2025-03-03", "orb", 1, "09:30", "09:35", 4, 100.0, 99.0, 102.0)
        i, price, reason = st.walk(bars, c)
        self.assertEqual((4, 99.0, "stop"), (i, price, reason))

    def test_a_gap_through_the_stop_fills_at_the_open(self):
        bars = session(flat(4) + [(97.0, 97.5, 96.5, 97.0)] + flat(80, 97))
        c = st.Candidate("AAA", "2025-03-03", "orb", 1, "09:30", "09:35", 4, 100.0, 99.0, 102.0)
        self.assertEqual((4, 97.0, "stop"), st.walk(bars, c))

    def test_open_positions_close_at_the_1510_bar(self):
        bars = session(flat(75))
        c = st.Candidate("AAA", "2025-03-03", "orb", -1, "09:30", "09:35", 4, 100.0, 105.0, 90.0)
        i, price, reason = st.walk(bars, c)
        self.assertEqual(("15:10", "time"), (bars.times[i], reason))

    def test_short_trade_pnl_and_charges(self):
        bars = session(flat(4) + [(100.0, 100.2, 97.9, 98.0)] + flat(80, 98))
        c = st.Candidate("AAA", "2025-03-03", "orb", -1, "09:30", "09:35", 4, 100.0, 101.0, 98.0)
        t = st.settle(bars, c, 100, st.EquityCosts(slippage_pct=0.0), 100.0)
        self.assertEqual("target", t.reason)
        self.assertAlmostEqual(200.0, t.gross)
        self.assertAlmostEqual(t.gross - t.charges, t.net)


class SessionLevelTests(unittest.TestCase):
    def test_previous_day_levels_come_from_the_bars_themselves(self):
        # Close of the last bar, and the session's extremes, whatever the bhavcopy printed: Dhan's bars are
        # adjusted for later bonuses and splits, so the bhavcopy's levels can be twice the bars' prices.
        prev = session([(100, 104, 99, 101), (101, 108, 100, 107), (107, 107.5, 95, 96)])
        self.assertEqual((96.0, 108.0, 95.0), st.session_levels(prev))

    def test_prev_day_break_on_bar_levels_fires_where_bhavcopy_levels_would_not(self):
        prev = session([(100, 104, 99, 101), (101, 105, 100, 102)])
        close, high, low = st.session_levels(prev)
        day = session(flat(3, 103.0) + [(104.0, 106.0, 103.8, 105.5), (105.5, 106.2, 105.4, 106.0)] + flat(60, 106.0))
        on_bars = st.prev_day_break(day, st.Context(close, high, low, atr_pct=2.0))
        # The same stock's unadjusted bhavcopy (a 1:1 bonus later): every level doubled.
        on_bhavcopy = st.prev_day_break(day, st.Context(close * 2, high * 2, low * 2, atr_pct=2.0))
        self.assertIsNotNone(on_bars)
        self.assertEqual(1, on_bars.side)
        self.assertTrue(on_bhavcopy is None or on_bhavcopy.side != 1)


class OtherSetupTests(unittest.TestCase):
    def test_prev_day_break_needs_an_open_below_the_level(self):
        rows = [(99, 99.5, 98.8, 99.2)] * 3 + [(99.2, 100.6, 99.1, 100.5), (100.5, 100.8, 100.2, 100.6)] + flat(40, 100.6)
        ctx = st.Context(prev_close=99.0, prev_high=100.0, prev_low=97.0, atr_pct=2.0)
        c = st.prev_day_break(session(rows), ctx)
        self.assertEqual((1, "09:30", 100.5), (c.side, c.signal_time, c.entry_price))
        self.assertAlmostEqual(100.0 - 0.3 * 1.98, c.stop)
        opened_above = [(100.5, 101, 100.2, 100.6)] * 3 + rows[3:]
        self.assertIsNone(st.prev_day_break(session(opened_above), ctx))

    def test_gap_drive_requires_the_gap_to_hold_half(self):
        held = [(102, 102.6, 101.5, 102.4)] * 3 + flat(40, 102.5)
        c = st.gap_drive(session(held), st.Context(prev_close=100, prev_high=100.5, prev_low=99, atr_pct=2))
        self.assertEqual((1, "09:30", 101.0), (c.side, c.entry_time, c.stop))
        filled = [(102, 102.6, 100.9, 101.2)] + held[1:]
        self.assertIsNone(st.gap_drive(session(filled), st.Context(100, 100.5, 99, 2)))


class PortfolioTests(unittest.TestCase):
    def test_limits_one_trade_per_stock_max_open_and_sizing(self):
        bars_of, cands = {}, []
        for k, sym in enumerate(["A", "B", "C", "D"]):
            bars = session(flat(75), symbol=sym)
            bars_of[(sym, "2025-03-03")] = bars
            cands.append(st.Candidate(sym, "2025-03-03", "orb", 1, "09:30", f"09:{35 + k}", 4, 100.0, 99.0, 150.0))
        cands.append(st.Candidate("A", "2025-03-03", "orb", -1, "09:30", "10:00", 9, 100.0, 101.0, 50.0))
        trades = st.run_portfolio(cands, bars_of, st.Portfolio(), st.EquityCosts())
        self.assertEqual(["A", "B", "C"], [t.symbol for t in trades])   # D: 3 already open; A again: one a day
        self.assertEqual(2500, trades[0].qty)                            # Rs 2,500 risk / Rs 1 stop

    def test_daily_loss_limit_blocks_later_entries(self):
        bars_of, cands = {}, []
        crash = session(flat(4) + [(100, 100, 90, 90)] + flat(70, 90), symbol="A")
        bars_of[("A", "2025-03-03")] = crash
        cands.append(st.Candidate("A", "2025-03-03", "orb", 1, "09:30", "09:35", 4, 100.0, 99.0, 150.0))
        for sym in ("B", "C"):
            bars_of[(sym, "2025-03-03")] = session(flat(75), symbol=sym)
            cands.append(st.Candidate(sym, "2025-03-03", "orb", 1, "10:25", "10:30", 15, 100.0, 97.0, 150.0))
        book = st.Portfolio(daily_loss_pct=0.4)   # Rs 2,000: the first stop (about Rs 2,500) breaches it
        trades = st.run_portfolio(cands, bars_of, book, st.EquityCosts())
        self.assertEqual(["A"], [t.symbol for t in trades])


if __name__ == "__main__":
    unittest.main()
