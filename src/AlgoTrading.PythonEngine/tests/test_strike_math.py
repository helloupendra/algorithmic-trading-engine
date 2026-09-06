"""
Strike arithmetic across underlyings (strategies/strike_math.py).

The point of this module is that one strategy can run on a 57,000 index, a
24,000 index and a ₹150 stock and mean the same thing on each. The tests that
matter most are therefore the two ends of that range, plus a regression pin that
BANKNIFTY still behaves exactly as it did when the numbers were literals.
"""

import math
import unittest

import _bootstrap  # noqa: F401

from strategies.strike_math import (
    DEFAULT_HEDGE_PCT,
    hedge_strike,
    neighbour_strike,
    round_down_to_step,
    round_to_step,
    round_up_to_step,
    steps_to_points,
)


class RoundingTests(unittest.TestCase):
    def test_banknifty_grid(self):
        self.assertEqual(round_down_to_step(57342, 100), 57300)
        self.assertEqual(round_up_to_step(57342, 100), 57400)
        self.assertEqual(round_to_step(57342, 100), 57300)
        self.assertEqual(round_to_step(57360, 100), 57400)

    def test_nifty_grid(self):
        self.assertEqual(round_down_to_step(24387, 50), 24350)
        self.assertEqual(round_up_to_step(24387, 50), 24400)

    def test_a_price_already_on_the_grid_is_unchanged(self):
        for fn in (round_to_step, round_up_to_step, round_down_to_step):
            self.assertEqual(fn(57300, 100), 57300)

    def test_fractional_stock_grids_keep_their_fraction(self):
        """A 2.5 grid gives 102.5. int() would trade a contract that does not exist."""
        self.assertEqual(round_down_to_step(103.4, 2.5), 102.5)
        self.assertEqual(round_up_to_step(103.4, 2.5), 105)

    def test_whole_results_come_back_whole(self):
        self.assertIsInstance(round_to_step(57342, 100), int)

    def test_a_missing_or_broken_step_falls_back_without_raising(self):
        for bad in (None, 0, -100, "abc"):
            self.assertIsInstance(round_to_step(24387, bad), (int, float))


class ThresholdTests(unittest.TestCase):
    def test_step_multiples_reproduce_the_old_banknifty_literals(self):
        """0.2 steps IS the old 20 points; 1.75 steps IS the old 175."""
        self.assertEqual(steps_to_points(0.2, 100), 20)
        self.assertEqual(steps_to_points(0.5, 100), 50)
        self.assertEqual(steps_to_points(0.7, 100), 70)
        self.assertEqual(steps_to_points(0.9, 100), 90)
        self.assertEqual(steps_to_points(1.75, 100), 175)
        self.assertEqual(steps_to_points(0.1, 100), 10)

    def test_the_same_multiple_scales_to_niftys_grid(self):
        self.assertEqual(steps_to_points(0.2, 50), 10)
        self.assertEqual(steps_to_points(1.75, 50), 87.5)

    def test_a_neighbour_is_one_grid_position(self):
        self.assertEqual(neighbour_strike(57300, -1, 100), 57200)
        self.assertEqual(neighbour_strike(57300, +1, 100), 57400)
        self.assertEqual(neighbour_strike(24350, +1, 50), 24400)


class HedgeTests(unittest.TestCase):
    def test_banknifty_hedges_land_where_they_always_did(self):
        """
        Regression pin. The old code was floor((spot - 2000)/500)*500 for the put
        wing; 3.5% of a 57,000 spot is 1,995, which snaps to the same strike.
        """
        spot = 57000
        old_pe = int(math.floor((spot - 2000) / 500.0) * 500)
        old_ce = int(math.ceil((spot + 2000) / 500.0) * 500)
        self.assertEqual(hedge_strike(spot, "PE", 100), old_pe)
        self.assertEqual(hedge_strike(spot, "CE", 100), old_ce)

    def test_hedges_are_always_further_out_of_the_money(self):
        """A rounding error must make the wing cheaper, never accidentally near."""
        for spot in (57342, 24387, 81250, 150.5):
            step = 100 if spot > 1000 else 2.5
            self.assertLess(hedge_strike(spot, "PE", step), spot)
            self.assertGreater(hedge_strike(spot, "CE", step), spot)

    def test_the_same_percentage_means_the_same_moneyness_everywhere(self):
        """This is the whole reason hedges are a share of spot and not points."""
        for spot, step in ((57000, 100), (24000, 50), (81000, 100), (200.0, 2.5)):
            ce = hedge_strike(spot, "CE", step)
            distance_pct = (float(ce) - spot) / spot
            self.assertGreaterEqual(distance_pct, DEFAULT_HEDGE_PCT)
            # snapped to a coarse grid, so never more than a grid beyond target
            self.assertLess(distance_pct, DEFAULT_HEDGE_PCT + (step * 5) / spot)

    def test_the_old_fixed_points_rule_would_have_been_wrong_on_nifty(self):
        """Documents the bug this replaces: 2000 points is 8.3% of NIFTY."""
        self.assertAlmostEqual(2000 / 24000, 0.0833, places=3)
        nifty_ce = hedge_strike(24000, "CE", 50)
        self.assertLess(float(nifty_ce) - 24000, 2000, "must be far nearer than the old 2000")

    def test_a_stock_hedge_is_a_sane_distance(self):
        """20 strike steps on a ₹150 stock would be 67% away — the rule this replaces."""
        ce = hedge_strike(150.0, "CE", 2.5)
        self.assertLess(float(ce) - 150.0, 150.0 * 0.10)


if __name__ == "__main__":
    unittest.main()
