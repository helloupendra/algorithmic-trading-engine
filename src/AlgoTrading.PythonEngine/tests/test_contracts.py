"""Contract resolution: ATM rounding, expiry as-of date, logical symbols, time helpers."""

import unittest
from datetime import date, datetime, timezone

import _bootstrap  # noqa: F401

from backtest.contracts import ContractResolver
from backtest.timeutil import (
    date_range_chunks,
    in_session,
    iso_utc,
    ist_date,
    ist_day_end_utc,
    ist_day_start_utc,
    parse_hhmm,
    parse_utc,
)
from strategies.contract_selector import parse_logical_symbol, round_to_step, strike_step_from_chain


class FakeInstrumentsApi:
    def __init__(self, underlying="BANKNIFTY", expiries=None, step=100.0):
        self.underlying = underlying
        self.expiries_list = expiries or ["2026-08-25", "2026-09-29", "2026-10-27"]
        self.step = step
        self.contract_calls = 0

    def get_expiries(self, underlying):
        return [{"underlying": underlying, "expiryDate": e} for e in self.expiries_list]

    def get_option_chain(self, underlying, expiry, from_strike=None, to_strike=None):
        return [{"strikePrice": 57000 + i * self.step, "optionType": t} for i in range(5) for t in ("CE", "PE")]

    def get_exact_contract(self, underlying, expiry, strike, option_type):
        self.contract_calls += 1
        if strike >= 60000:
            return None
        token = expiry[2:4] + {"08": "AUG", "09": "SEP", "10": "OCT"}[expiry[5:7]]
        strike_text = str(int(strike)) if float(strike).is_integer() else str(strike)
        return {
            "symbol": f"NSE:{underlying}{token}{strike_text}{option_type}",
            "underlying": underlying,
            "expiryDate": expiry,
            "strikePrice": strike,
            "optionType": option_type,
        }


class AtmRoundingTests(unittest.TestCase):
    def test_whole_point_grid(self):
        self.assertEqual(round_to_step(57649.0, 100), 57600)
        self.assertEqual(round_to_step(57651.0, 100), 57700)
        self.assertEqual(round_to_step(57620.0, 100), 57600)
        self.assertIsInstance(round_to_step(57649.0, 100), int)

    def test_fractional_grid(self):
        self.assertEqual(round_to_step(101.2, 2.5), 100)
        self.assertEqual(round_to_step(101.3, 2.5), 102.5)
        self.assertEqual(round_to_step(103.7, 2.5), 102.5)
        self.assertEqual(round_to_step(104.0, 2.5), 105)

    def test_step_from_chain(self):
        chain = [{"strikePrice": 100}, {"strikePrice": 102.5}, {"strikePrice": 105}, {"strikePrice": "bad"}]
        self.assertEqual(strike_step_from_chain(chain), 2.5)
        self.assertIsNone(strike_step_from_chain([{"strikePrice": 100}]))

    def test_logical_symbol(self):
        self.assertEqual(parse_logical_symbol("BANKNIFTY_PE_50300"), ("BANKNIFTY", "PE", 50300))
        self.assertEqual(parse_logical_symbol("RELIANCE_CE_102.5"), ("RELIANCE", "CE", 102.5))
        self.assertIsNone(parse_logical_symbol("NSE:BANKNIFTY26SEP57600CE"))
        self.assertIsNone(parse_logical_symbol("BANKNIFTY_XX_100"))


class ContractResolverTests(unittest.TestCase):
    def test_expiry_as_of_date(self):
        resolver = ContractResolver(FakeInstrumentsApi(), "BANKNIFTY", log=lambda _: None)
        self.assertEqual(resolver.expiry_for(date(2026, 8, 19)), "2026-08-25")
        self.assertEqual(resolver.expiry_for("2026-08-25"), "2026-08-25")   # expiry day itself
        self.assertEqual(resolver.expiry_for(date(2026, 8, 26)), "2026-09-29")
        self.assertIsNone(resolver.expiry_for(date(2026, 11, 1)))

    def test_step_and_contracts_cached(self):
        api = FakeInstrumentsApi()
        resolver = ContractResolver(api, "BANKNIFTY", log=lambda _: None)
        self.assertEqual(resolver.step, 100.0)
        self.assertEqual(resolver.atm(57649.0), 57600)
        contracts = resolver.atm_contracts("2026-09-29", 57600)
        self.assertEqual(contracts["atm_ce"].symbol, "NSE:BANKNIFTY26SEP57600CE")
        self.assertEqual(contracts["atm_pe"].symbol, "NSE:BANKNIFTY26SEP57600PE")
        resolver.atm_contracts("2026-09-29", 57600)
        self.assertEqual(api.contract_calls, 2)
        self.assertIsNone(resolver.contract("2026-09-29", 60000, "CE"))
        self.assertEqual(resolver.atm_contracts("2026-09-29", 60000), {})

    def test_resolve_logical(self):
        resolver = ContractResolver(FakeInstrumentsApi(), "BANKNIFTY", log=lambda _: None)
        self.assertEqual(resolver.resolve_logical("BANKNIFTY_PE_57500", "2026-09-29"), "NSE:BANKNIFTY26SEP57500PE")
        self.assertEqual(resolver.resolve_logical("NSE:BANKNIFTY26SEP57500PE", "2026-09-29"), "NSE:BANKNIFTY26SEP57500PE")
        self.assertIsNone(resolver.resolve_logical("BANKNIFTY_CE_60000", "2026-09-29"))

    def test_fallback_step_when_chain_is_thin(self):
        api = FakeInstrumentsApi()
        api.get_option_chain = lambda *a, **k: [{"strikePrice": 57000}]
        resolver = ContractResolver(api, "NIFTY", log=lambda _: None)
        self.assertEqual(resolver.step, 50.0)


class TimeHelperTests(unittest.TestCase):
    def test_parse_and_format(self):
        dt = parse_utc("2026-08-19T03:45:00Z")
        self.assertEqual(dt.tzinfo, timezone.utc)
        self.assertEqual(iso_utc(dt), "2026-08-19T03:45:00Z")
        self.assertEqual(iso_utc(parse_utc("2026-08-19T03:45:00+00:00")), "2026-08-19T03:45:00Z")
        self.assertEqual(iso_utc(parse_utc("2026-08-19T09:15:00+05:30")), "2026-08-19T03:45:00Z")
        self.assertEqual(iso_utc(parse_utc("2026-08-19T03:45:00")), "2026-08-19T03:45:00Z")

    def test_ist_days(self):
        self.assertEqual(ist_date(parse_utc("2026-08-18T18:30:00Z")), date(2026, 8, 19))
        self.assertEqual(ist_date(parse_utc("2026-08-18T18:29:59Z")), date(2026, 8, 18))
        self.assertEqual(iso_utc(ist_day_start_utc(date(2026, 8, 19))), "2026-08-18T18:30:00Z")
        self.assertEqual(iso_utc(ist_day_end_utc(date(2026, 8, 19))), "2026-08-19T18:29:59Z")

    def test_session(self):
        self.assertTrue(in_session(parse_utc("2026-08-19T03:45:00Z")))    # 09:15 IST
        self.assertTrue(in_session(parse_utc("2026-08-19T09:55:00Z")))    # 15:25 IST
        self.assertFalse(in_session(parse_utc("2026-08-19T10:00:00Z")))   # 15:30 IST
        self.assertFalse(in_session(parse_utc("2026-08-19T03:40:00Z")))   # 09:10 IST

    def test_hhmm_and_chunks(self):
        self.assertEqual(parse_hhmm("15:15").hour, 15)
        self.assertIsNone(parse_hhmm(""))
        with self.assertRaises(ValueError):
            parse_hhmm("25:00")
        chunks = date_range_chunks(date(2026, 1, 1), date(2026, 3, 15), 30)
        self.assertEqual(chunks[0], (date(2026, 1, 1), date(2026, 1, 30)))
        self.assertEqual(chunks[-1][1], date(2026, 3, 15))
        self.assertEqual(len(chunks), 3)
        self.assertEqual(date_range_chunks(date(2026, 1, 2), date(2026, 1, 1)), [])


if __name__ == "__main__":
    unittest.main()
