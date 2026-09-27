"""
Reading a contract out of its symbol (core/option_symbol.py).

This replaced a regex anchored to "NSE:" plus a flat seven-day time to expiry
commented "for demo". The seven days is the part that mattered: the same option
at the same price implies 20% volatility over seven days, 38% over two and 110%
on expiry morning, and IV is the number that says whether an option is dear or
cheap.
"""

import contextlib
import io
import os
import tempfile
import unittest
from datetime import date, datetime, timezone

import _bootstrap  # noqa: F401

from core.option_symbol import (
    NO_REFERENCE,
    ExpiryReference,
    load_reference,
    monthly_expiry,
    parse_option_symbol,
    years_to_expiry,
)


class ParsingTests(unittest.TestCase):
    def test_a_monthly_nse_contract(self):
        c = parse_option_symbol("NSE:BANKNIFTY26SEP57600CE")
        self.assertIsNotNone(c)
        self.assertEqual(c.underlying, "BANKNIFTY")
        self.assertEqual(c.strike, 57600.0)
        self.assertEqual(c.kind, "CE")
        # The last TUESDAY. This test used to pin the last Thursday (24 Sep).
        self.assertEqual(c.expiry, date(2026, 9, 29))

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


class MonthlyExpiryTests(unittest.TestCase):
    """
    A monthly symbol carries only its month. It was dated to the last Thursday,
    NSE's rule until SEBI's one-expiry-day-per-exchange change of 1 Sep 2025, so
    every NSE monthly was priced two days short: all 72,692 monthly options in
    the FYERS NSE master of 10 Sep 2026 disagreed with it (27 Sep 2026 review).
    Dates below are checked against that master or the exchanges' bhavcopies.
    """

    def test_nse_index_monthlies_expire_on_the_last_tuesday(self):
        for underlying in ("NIFTY", "BANKNIFTY"):
            self.assertEqual(parse_option_symbol(f"NSE:{underlying}26OCT25000CE").expiry, date(2026, 10, 27), underlying)
            self.assertEqual(parse_option_symbol(f"NSE:{underlying}26DEC25000PE").expiry, date(2026, 12, 29), underlying)

    def test_bse_monthlies_expire_on_the_last_thursday(self):
        self.assertEqual(parse_option_symbol("BSE:SENSEX26OCT81000CE").expiry, date(2026, 10, 29))
        self.assertEqual(parse_option_symbol("BSE:SENSEX26DEC81000PE").expiry, date(2026, 12, 31))
        self.assertEqual(parse_option_symbol("BSE:BANKEX26OCT60000CE").expiry, date(2026, 10, 29))

    def test_a_holiday_moves_the_expiry_to_the_previous_trading_day(self):
        # Tue 24 Nov 2026 is Guru Nanak Jayanti; the master lists NIFTY's and
        # BANKNIFTY's November contracts to Mon 23 Nov.
        self.assertEqual(parse_option_symbol("NSE:NIFTY26NOV25000CE").expiry, date(2026, 11, 23))
        # Past a weekend and a Monday holiday too.
        reference = ExpiryReference({}, {}, {"NSE": frozenset({date(2026, 11, 24), date(2026, 11, 23)})})
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2026, 11, reference), date(2026, 11, 20))
        # Another exchange's holiday does not move it.
        self.assertEqual(monthly_expiry("BSE", "SENSEX", 2026, 11, reference), date(2026, 11, 26))

    def test_before_september_2025_nse_monthlies_were_thursdays(self):
        # The weekday rule alone, by the month's date.
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2025, 8, NO_REFERENCE), date(2025, 8, 28))
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2025, 9, NO_REFERENCE), date(2025, 9, 30))
        self.assertEqual(monthly_expiry("NSE", "RELIANCE", 2024, 6, NO_REFERENCE), date(2024, 6, 27))

    def test_a_past_month_is_what_the_exchange_recorded(self):
        """
        index_option_expiries.json (the bhavcopy calendar the backtests use)
        knows what no weekday rule does.
        """
        self.assertEqual(parse_option_symbol("NSE:NIFTY25AUG24000CE").expiry, date(2025, 8, 28))
        self.assertEqual(parse_option_symbol("NSE:BANKNIFTY24JUL52000CE").expiry, date(2024, 7, 31))   # a Wednesday
        self.assertEqual(parse_option_symbol("BSE:SENSEX25MAR75000CE").expiry, date(2025, 3, 25))      # a Tuesday
        self.assertEqual(parse_option_symbol("NSE:NIFTY26MAR22000CE").expiry, date(2026, 3, 30))       # 31 Mar was a holiday

    def test_a_month_the_record_has_only_partly_read_uses_the_rule(self):
        # The record reaches 16 Sep 2026; its last NIFTY entry that month is
        # the weekly of the 15th, not the monthly.
        self.assertEqual(parse_option_symbol("NSE:NIFTY26SEP25000CE").expiry, date(2026, 9, 29))
        reference = ExpiryReference({("NIFTY", 2026, 9): date(2026, 9, 15)}, {"NSE": date(2026, 9, 16)}, {})
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2026, 9, reference), date(2026, 9, 29))
        covered = reference._replace(recorded_through={"NSE": date(2026, 9, 30)})
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2026, 9, covered), date(2026, 9, 15))

    def test_a_weekly_symbol_keeps_its_own_date(self):
        # Even on a day the calendar calls a holiday: the symbol says the day.
        reference = ExpiryReference({}, {}, {"NSE": frozenset({date(2026, 10, 6)})})
        for ref in (None, reference):
            c = parse_option_symbol("NSE:NIFTY26O0625000CE", ref)
            self.assertEqual(c.expiry, date(2026, 10, 6))

    def test_the_seed_files_are_found(self):
        """A wrong path would silently drop every holiday shift and every recorded month."""
        reference = load_reference()
        self.assertIn(date(2026, 11, 24), reference.holidays["NSE"])
        self.assertIn(date(2026, 11, 24), reference.holidays["BSE"])
        self.assertEqual(reference.recorded_monthly[("BANKNIFTY", 2024, 7)], date(2024, 7, 31))
        self.assertGreaterEqual(reference.recorded_through["NSE"], date(2026, 8, 31))

    def test_missing_seed_files_fall_back_to_the_weekday_rule_and_say_so(self):
        with tempfile.TemporaryDirectory() as empty:
            said = io.StringIO()
            with contextlib.redirect_stderr(said):
                reference = load_reference(empty)
            self.assertEqual(reference, NO_REFERENCE)
            self.assertIn(os.path.join(empty, "market_calendar.json"), said.getvalue())
        self.assertEqual(monthly_expiry("NSE", "NIFTY", 2026, 11, reference), date(2026, 11, 24))


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
