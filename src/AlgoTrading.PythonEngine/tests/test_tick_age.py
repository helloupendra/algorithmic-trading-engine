"""
Reading a tick's age (core/tick_age.py).

This replaced an inline strptime whose format ended in a literal "Z". Every real
tick on this feed carries "+00:00" instead, so the parse raised on every tick
into a bare `except: pass` and the REDIS_LAG gauge was never set. The first test
below is that exact string, taken off the live Redis stream.
"""

import unittest
from datetime import datetime, timezone

import _bootstrap  # noqa: F401

from core.tick_age import parse_tick_timestamp, tick_age_seconds


class TickAgeTests(unittest.TestCase):
    def test_the_shape_real_ticks_actually_carry(self):
        """Copied verbatim from market:ticks. The old parser raised on this."""
        parsed = parse_tick_timestamp("2026-09-04T10:00:44.629503+00:00")
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed.tzinfo, timezone.utc)
        self.assertEqual(parsed.year, 2026)
        self.assertEqual(parsed.second, 44)

    def test_the_z_spelling_is_the_same_instant(self):
        a = parse_tick_timestamp("2026-09-04T10:00:44.629503+00:00")
        b = parse_tick_timestamp("2026-09-04T10:00:44.629503Z")
        self.assertEqual(a, b)

    def test_whole_seconds_without_a_fraction(self):
        self.assertIsNotNone(parse_tick_timestamp("2026-09-04T10:00:44Z"))
        self.assertIsNotNone(parse_tick_timestamp("2026-09-04T10:00:44+00:00"))

    def test_milliseconds_rather_than_microseconds(self):
        self.assertIsNotNone(parse_tick_timestamp("2026-06-09T08:31:05.200Z"))

    def test_a_naive_timestamp_is_read_as_utc(self):
        """Everything on this feed is UTC; guessing local time would shift the age by hours."""
        parsed = parse_tick_timestamp("2026-09-04T10:00:44.629503")
        self.assertEqual(parsed.tzinfo, timezone.utc)

    def test_unreadable_input_returns_none_rather_than_raising(self):
        for bad in (None, "", "   ", "not a timestamp", "2026-13-45T99:99:99Z"):
            self.assertIsNone(parse_tick_timestamp(bad), f"{bad!r} should be unreadable")
            self.assertIsNone(tick_age_seconds(bad, 1_000_000.0))

    def test_age_is_measured_from_the_given_clock(self):
        at = datetime(2026, 9, 4, 10, 0, 0, tzinfo=timezone.utc)
        age = tick_age_seconds("2026-09-04T10:00:00+00:00", at.timestamp() + 45)
        self.assertAlmostEqual(age, 45.0, places=3)

    def test_a_producer_clock_running_ahead_is_reported_not_hidden(self):
        """A negative age means someone's clock is wrong. Clamping it would hide that."""
        at = datetime(2026, 9, 4, 10, 0, 0, tzinfo=timezone.utc)
        age = tick_age_seconds("2026-09-04T10:00:10+00:00", at.timestamp())
        self.assertLess(age, 0)

    def test_the_old_parser_really_did_fail_on_these(self):
        """Guards the regression: if someone reinstates strptime, this explains why not."""
        real = "2026-09-04T10:00:44.629503+00:00"
        fmt = "%Y-%m-%dT%H:%M:%S.%fZ" if "." in real else "%Y-%m-%dT%H:%M:%SZ"
        with self.assertRaises(ValueError):
            datetime.strptime(real, fmt)
        self.assertIsNotNone(parse_tick_timestamp(real))


if __name__ == "__main__":
    unittest.main()
