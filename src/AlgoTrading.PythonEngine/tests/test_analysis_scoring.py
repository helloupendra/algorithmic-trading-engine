import _bootstrap  # noqa: F401

import math
import unittest
from datetime import date, timedelta

from analysis import backtest, scoring

RANGE_PREDICTION = {"median": 1.0, "low80": 0.7, "high80": 1.5, "prevClose": 24650.3,
                    "points": {"median": 246.5, "low80": 172.55, "high80": 369.75},
                    "buckets": {"quiet": 0.2, "normal": 0.5, "wild": 0.3}, "bucketEdges": [0.75, 1.25]}
RANGE_BASELINE = {"median": 1.2, "low80": 0.8, "high80": 1.9, "prevClose": 24650.3,
                  "points": {"median": 295.8, "low80": 197.2, "high80": 468.36},
                  "buckets": {"quiet": 0.33, "normal": 0.34, "wild": 0.33}, "bucketEdges": [0.75, 1.25]}


class OutcomeTests(unittest.TestCase):
    def test_outcome_of_a_session(self):
        o = scoring.outcome(24660.1, 24790.4, 24540.0, 24771.2, 24650.3, [0.75, 1.25])
        self.assertEqual(set(o), {"open", "high", "low", "close", "range", "bucket", "trendDay", "efficiency", "up"})
        self.assertAlmostEqual(o["range"], (24790.4 - 24540.0) / 24650.3 * 100, places=4)
        self.assertEqual(o["bucket"], "normal")
        self.assertAlmostEqual(o["efficiency"], (24771.2 - 24660.1) / (24790.4 - 24540.0), places=4)
        self.assertFalse(o["trendDay"])
        self.assertTrue(o["up"])

    def test_a_trend_day_is_a_body_of_at_least_sixty_percent(self):
        self.assertTrue(scoring.outcome(100, 110, 99, 109, 100)["trendDay"])     # 9 / 11
        self.assertFalse(scoring.outcome(100, 110, 90, 105, 100)["trendDay"])    # 5 / 20
        self.assertFalse(scoring.outcome(100, 100, 100, 100, 100)["trendDay"])   # never moved

    def test_bucket_edges_quiet_below_wild_at_or_above(self):
        self.assertEqual(scoring.bucket_of(0.74, [0.75, 1.25]), "quiet")
        self.assertEqual(scoring.bucket_of(0.75, [0.75, 1.25]), "normal")
        self.assertEqual(scoring.bucket_of(1.25, [0.75, 1.25]), "wild")

    def test_without_edges_or_a_previous_close_there_is_no_bucket_or_range(self):
        o = scoring.outcome(100, 110, 99, 109, None)
        self.assertIsNone(o["range"])
        self.assertIsNone(o["bucket"])
        self.assertIsNone(scoring.outcome(100, 110, 99, 109, 100)["bucket"])


class ScoreTests(unittest.TestCase):
    def test_range_log_loss_coverage_brier_and_calibration(self):
        outcome = {"range": 1.3, "bucket": "wild", "trendDay": False, "up": True}
        s = scoring.score("range", RANGE_PREDICTION, RANGE_BASELINE, outcome)
        self.assertAlmostEqual(s["loss"], abs(math.log(1.3) - math.log(1.0)), places=6)
        self.assertAlmostEqual(s["baselineLoss"], abs(math.log(1.3) - math.log(1.2)), places=6)
        self.assertTrue(s["metrics"]["covered80"])
        self.assertAlmostEqual(s["metrics"]["brier"], 0.2 ** 2 + 0.5 ** 2 + 0.7 ** 2, places=6)
        self.assertAlmostEqual(s["metrics"]["baselineBrier"], 0.33 ** 2 + 0.34 ** 2 + 0.67 ** 2, places=6)
        self.assertEqual(s["calibration"], [{"p": 0.2, "y": 0}, {"p": 0.5, "y": 0}, {"p": 0.3, "y": 1}])

    def test_outside_the_interval_is_not_covered(self):
        s = scoring.score("range", RANGE_PREDICTION, RANGE_BASELINE, {"range": 1.6, "bucket": "wild"})
        self.assertFalse(s["metrics"]["covered80"])

    def test_a_range_forecast_cannot_be_scored_without_a_range(self):
        with self.assertRaises(ValueError):
            scoring.score("range", RANGE_PREDICTION, RANGE_BASELINE, {"range": None, "bucket": None})

    def test_trend_and_direction_are_brier_scored(self):
        outcome = {"trendDay": True, "up": False}
        t = scoring.score("trend", {"p": 0.38}, {"p": 0.3}, outcome)
        self.assertAlmostEqual(t["loss"], (0.38 - 1) ** 2)
        self.assertAlmostEqual(t["baselineLoss"], (0.3 - 1) ** 2)
        self.assertEqual(t["metrics"], {"brier": t["loss"], "baselineBrier": t["baselineLoss"]})
        self.assertEqual(t["calibration"], [{"p": 0.38, "y": 1}])
        d = scoring.score("direction", {"p": 0.55}, {"p": 0.52}, outcome)
        self.assertAlmostEqual(d["loss"], 0.55 ** 2)
        self.assertEqual(d["calibration"], [{"p": 0.55, "y": 0}])

    def test_scores_carry_the_contract_keys(self):
        for target, pred, base in (("range", RANGE_PREDICTION, RANGE_BASELINE), ("trend", {"p": 0.4}, {"p": 0.3}),
                                   ("direction", {"p": 0.4}, {"p": 0.5})):
            s = scoring.score(target, pred, base, {"range": 1.0, "bucket": "normal", "trendDay": True, "up": True})
            self.assertEqual(set(s), {"loss", "baselineLoss", "metrics", "calibration"})
            self.assertIn("brier", s["metrics"])
            self.assertIn("baselineBrier", s["metrics"])

    def test_calibration_bins_are_tenths(self):
        bins = scoring.calibration_bins([(0.05, 0), (0.31, 1), (0.35, 0), (0.39, 1), (1.0, 1)])
        self.assertEqual([(b["from"], b["to"], b["n"]) for b in bins], [(0.0, 0.1, 1), (0.3, 0.4, 3), (0.9, 1.0, 1)])
        self.assertAlmostEqual(bins[1]["meanP"], 0.35, places=4)
        self.assertAlmostEqual(bins[1]["hitRate"], 2 / 3, places=4)


class SplitAndIntervalTests(unittest.TestCase):
    def test_walk_forward_split_dates(self):
        self.assertEqual(backtest.split_of(date(2021, 8, 4)), "design")
        self.assertEqual(backtest.split_of(date(2024, 12, 31)), "design")
        self.assertEqual(backtest.split_of(date(2025, 1, 1)), "validation")
        self.assertEqual(backtest.split_of(date(2025, 12, 31)), "validation")
        self.assertEqual(backtest.split_of(date(2026, 1, 1)), "holdout")

    def test_the_interval_resamples_days_and_is_repeatable(self):
        start = date(2025, 1, 1)
        diffs = [(start + timedelta(days=k), 0.02 + 0.01 * ((k * 7) % 5 - 2)) for k in range(300)]
        # three indices a day, moving together
        tripled = [(d, v) for d, v in diffs for _ in range(3)]
        lo, hi = backtest.bootstrap_by_day(tripled)
        self.assertEqual((lo, hi), backtest.bootstrap_by_day(tripled))
        mean = sum(v for _, v in diffs) / len(diffs)
        self.assertLess(lo, mean)
        self.assertGreater(hi, mean)
        # the three copies are one draw: the interval is the one-a-day interval, not a third narrower
        lo1, hi1 = backtest.bootstrap_by_day(diffs)
        self.assertAlmostEqual(hi - lo, hi1 - lo1, delta=0.1 * (hi1 - lo1))


if __name__ == "__main__":
    unittest.main()
