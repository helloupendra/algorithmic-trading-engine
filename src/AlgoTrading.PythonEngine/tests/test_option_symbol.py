"""
Reading a contract out of its symbol (core/option_symbol.py).

This replaced a regex anchored to "NSE:" plus a flat seven-day time to expiry
commented "for demo". The seven days is the part that mattered: the same option
at the same price implies 20% volatility over seven days, 38% over two and 110%
on expiry morning, and IV is the number that says whether an option is dear or
cheap.
"""

import unittest
from datetime import date, datetime, timezone

import _bootstrap  # noqa: F401

from core.option_symbol import parse_option_symbol, years_to_expiry


class ParsingTests(unittest.TestCase):
    def test_a_monthly_nse_contract(self):
        c = parse_option_symbol("NSE:BANKNIFTY26SEP57600CE")
        self.assertIsNotNone(c)
        self.assertEqual(c.underlying, "BANKNIFTY")
        self.assertEqual(c.strike, 57600.0)
        self.assertEqual(c.kind, "CE")
        self.assertEqual(c.expiry.year, 2026)
        self.assertEqual(c.expiry.month, 9)
        self.assertEqual(c.expiry.weekday(), 3, "monthlies expire on a Thursday")

    def test_a_bse_contract_is_read_too(self):
        """The old regex was anchored to NSE, so SENSEX options got no greeks at all."""
        c = parse_option_symbol("BSE:SENSEX26SEP81000PE")
        self.assertIsNotNone(c)
        self.assertEqual(c.exchange, "BSE")
        self.assertEqual(c.underlying, "SENSEX")
        self.assertEqual(c.strike, 81000.0)
        self.assertEqual(c.kind, "PE")

    def test_a_weekly_contract(self):
        c = parse_option_symbol("NSE:NIFTY2690124500CE")
        self.assertIsNotNone(c)
        self.assertEqual(c.underlying, "NIFTY")
        self.assertEqual(c.expiry, date(2026, 9, 1))
        self.assertEqual(c.strike, 24500.0)

    def test_the_letter_months_of_the_weekly_format(self):
        """A single digit only reaches 9, so Oct/Nov/Dec are O, N and D."""
        for code, month in (("O", 10), ("N", 11), ("D", 12)):
            c = parse_option_symbol(f"NSE:NIFTY26{code}0724000CE")
            self.assertIsNotNone(c, code)
            self.assertEqual(c.expiry.month, month)

    def test_a_fractional_stock_strike(self):
        c = parse_option_symbol("NSE:ABCAPITAL26SEP102.5CE")
        self.assertIsNotNone(c)
        self.assertEqual(c.strike, 102.5)

    def test_things_that_are_not_options(self):
        for symbol in ("NSE:NIFTYBANK-INDEX", "NSE:SBIN-EQ", "NSE:BANKNIFTY26SEPFUT", "", None, "rubbish"):
            self.assertIsNone(parse_option_symbol(symbol), symbol)


class TimeToExpiryTests(unittest.TestCase):
    def test_a_week_out_is_about_a_week(self):
        now = datetime(2026, 9, 1, 10, 0, tzinfo=timezone.utc)
        self.assertAlmostEqual(years_to_expiry(date(2026, 9, 8), now), 7 / 365, places=4)

    def test_expiry_morning_is_hours_not_days(self):
        """
        The case the old hardcode got most wrong. Six hours before the bell is
        0.0007 years, not 0.019 — a factor of twenty-eight.
        """
        now = datetime(2026, 9, 8, 4, 0, tzinfo=timezone.utc)
        tte = years_to_expiry(date(2026, 9, 8), now)
        self.assertGreater(tte, 0)
        self.assertLess(tte, 1 / 365, "less than a day")
        self.assertNotAlmostEqual(tte, 7 / 365, places=4)

    def test_after_the_bell_is_not_positive(self):
        """Callers read a non-positive answer as 'do not price this'."""
        now = datetime(2026, 9, 8, 11, 0, tzinfo=timezone.utc)
        self.assertLessEqual(years_to_expiry(date(2026, 9, 8), now), 0)

    def test_a_naive_clock_is_read_as_utc(self):
        naive = datetime(2026, 9, 1, 10, 0)
        aware = datetime(2026, 9, 1, 10, 0, tzinfo=timezone.utc)
        self.assertAlmostEqual(
            years_to_expiry(date(2026, 9, 8), naive),
            years_to_expiry(date(2026, 9, 8), aware),
        )

    def test_the_hardcode_this_replaced_was_materially_wrong(self):
        """Documents the size of the error, so nobody reinstates it."""
        now = datetime(2026, 9, 6, 10, 0, tzinfo=timezone.utc)
        real = years_to_expiry(date(2026, 9, 8), now)
        self.assertAlmostEqual(real, 2 / 365, places=4)
        self.assertAlmostEqual(7 / 365 / real, 3.5, places=1)


if __name__ == "__main__":
    unittest.main()
