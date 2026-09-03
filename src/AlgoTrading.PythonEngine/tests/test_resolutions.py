"""Resolution string mapping (core/resolutions.py)."""

import unittest

import _bootstrap  # noqa: F401

from core.resolutions import (
    DAY_RESOLUTION_MINUTES,
    is_daily,
    minutes_of,
    resolution_label,
    same_resolution,
    to_candle_resolution,
    to_strategy_resolution,
)


class ResolutionMappingTests(unittest.TestCase):
    def test_strategy_to_candle(self):
        self.assertEqual(to_candle_resolution("5m"), "5")
        self.assertEqual(to_candle_resolution("1m"), "1")
        self.assertEqual(to_candle_resolution("15m"), "15")
        self.assertEqual(to_candle_resolution("1D"), "D")
        self.assertEqual(to_candle_resolution("D"), "D")

    def test_candle_to_strategy(self):
        self.assertEqual(to_strategy_resolution("5"), "5m")
        self.assertEqual(to_strategy_resolution("1"), "1m")
        self.assertEqual(to_strategy_resolution("D"), "1D")
        self.assertEqual(to_strategy_resolution("1D"), "1D")

    def test_idempotent_and_case_insensitive(self):
        self.assertEqual(to_candle_resolution("5"), "5")
        self.assertEqual(to_strategy_resolution("5m"), "5m")
        self.assertEqual(to_candle_resolution("5M"), "5")
        self.assertEqual(to_candle_resolution(" 1d "), "D")

    def test_minutes(self):
        self.assertEqual(minutes_of("5m"), 5)
        self.assertEqual(minutes_of("15"), 15)
        self.assertEqual(minutes_of("1D"), DAY_RESOLUTION_MINUTES)
        self.assertTrue(is_daily("D"))
        self.assertFalse(is_daily("5m"))

    def test_same_and_label(self):
        self.assertTrue(same_resolution("5", "5m"))
        self.assertFalse(same_resolution("5", "15m"))
        self.assertEqual(resolution_label("5"), "5m")

    def test_empty_rejected(self):
        with self.assertRaises(ValueError):
            to_candle_resolution("")


if __name__ == "__main__":
    unittest.main()
