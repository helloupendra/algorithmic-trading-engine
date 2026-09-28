"""
Option greeks: said when they cannot be computed, and priced to the contract's
own expiry.

The pricing library's import sat in `try: ... except ImportError: pass`. When it
failed, calculate_greeks raised NameError on every call, its own `except
Exception` read that as "IV did not converge", and every option carried null
greeks with nothing anywhere saying why. DataEngine's chain priced every
contract to a flat seven days.
"""

import contextlib
import io
import unittest
from datetime import date, datetime, timezone
from unittest import mock

import _bootstrap  # noqa: F401

import core.greeks_calculator as greeks_calculator


class GreeksLibraryTests(unittest.TestCase):
    def test_the_library_the_requirements_install_is_importable_and_prices(self):
        """The regression that mattered: greeks silently None on every tick."""
        self.assertIsNone(greeks_calculator.unavailable_reason())
        greeks = greeks_calculator.calculate_greeks(spot=24000, strike=24000, tte_years=5 / 365,
                                                    option_type="CE", option_price=120)
        self.assertIsNotNone(greeks)
        self.assertGreater(greeks.iv, 0)

    def test_a_missing_library_is_said_once_and_prices_nothing(self):
        stderr = io.StringIO()
        with mock.patch.object(greeks_calculator, "IMPORT_ERROR", "No module named 'vollib'"), \
             mock.patch.object(greeks_calculator, "_unavailable_reported", False), \
             contextlib.redirect_stderr(stderr):
            first = greeks_calculator.calculate_greeks(24000, 24000, 5 / 365, "CE", 120)
            second = greeks_calculator.calculate_greeks(24000, 24100, 5 / 365, "PE", 180)
            self.assertEqual("No module named 'vollib'", greeks_calculator.unavailable_reason())

        self.assertIsNone(first)
        self.assertIsNone(second)
        self.assertEqual(1, stderr.getvalue().count("GREEKS UNAVAILABLE"))
        self.assertIn("No module named 'vollib'", stderr.getvalue())


class ChainTimeToExpiryTests(unittest.TestCase):
    def test_the_chain_prices_each_contract_to_its_own_expiry_not_a_flat_week(self):
        from core.data_engine import DataEngine
        from core.data_models import TickData

        engine = DataEngine()
        engine.get_latest_quote = lambda symbol: TickData(
            symbol=symbol, market_type="INDEX", timestamp=datetime.now(timezone.utc), last_traded_price=25000.0,
            last_traded_qty=0, average_trade_price=0.0, volume=0)

        class Fyers:
            def quotes(self, request):
                return {"s": "ok", "d": [{"n": s, "v": {"lp": 120.0}} for s in request["symbols"].split(",")]}

        engine._get_fyers_client = lambda: Fyers()
        asked = []

        def years(expiry, now=None):
            asked.append(expiry)
            return 2 / 365

        with mock.patch("core.data_engine.years_to_expiry", side_effect=years):
            chain = engine.get_option_chain("NSE:NIFTY50-INDEX", "26930")

        contract = chain.contracts["NSE:NIFTY2693025000CE"]
        # A weekly symbol: the old regex read "2693025000" as the strike.
        self.assertEqual(25000.0, contract.strike)
        self.assertEqual(date(2026, 9, 30), contract.expiry)
        self.assertTrue(asked)
        self.assertTrue(all(e == date(2026, 9, 30) for e in asked))
        expected = greeks_calculator.calculate_greeks(25000.0, 25000.0, 2 / 365, "CE", 120.0)
        self.assertAlmostEqual(expected.iv, contract.greeks.iv)


if __name__ == "__main__":
    unittest.main()
