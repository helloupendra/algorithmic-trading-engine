"""
Contract resolution: ATM rounding, ATM/OTM/ITM requirements, expiry as-of date,
logical symbols, time helpers.
"""

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
from strategies.base_strategy import BaseStrategy, ContractRequirement
from strategies.contract_selector import (
    describe_requirement,
    parse_logical_symbol,
    requirement_distance,
    round_to_step,
    strike_for_requirement,
    strike_step_from_chain,
)


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


class ContractRequirementTests(unittest.TestCase):
    """strike_for_requirement: the ATM/OTM/ITM grid every runner and the replay share."""

    def test_atm_ignores_any_distance(self):
        req = ContractRequirement(key="atm_ce", option_type="CE", steps=3)
        self.assertEqual(strike_for_requirement(req, 57600, 100), 57600)
        self.assertEqual(strike_for_requirement(req, 57649, 100), 57600)     # snapped to the grid

    def test_otm_and_itm_directions_per_option_type(self):
        ce = ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2)
        pe = ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2)
        self.assertEqual(strike_for_requirement(ce, 57600, 100), 57800)
        self.assertEqual(strike_for_requirement(pe, 57600, 100), 57400)

        ce_itm = ContractRequirement(key="itm_ce", option_type="CE", moneyness="itm", steps=2)
        pe_itm = ContractRequirement(key="itm_pe", option_type="PE", moneyness="itm", steps=2)
        self.assertEqual(strike_for_requirement(ce_itm, 57600, 100), 57400)
        self.assertEqual(strike_for_requirement(pe_itm, 57600, 100), 57800)

    def test_points_win_over_steps_and_a_param_wins_over_both(self):
        req = ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2, points=340,
                                  param="otm_offset_steps")
        # points (340) beats steps, then snaps back onto the 100-grid: 57940 -> 57900.
        self.assertEqual(strike_for_requirement(req, 57600, 100), 57900)
        # The run parameter beats both, and is read as strikes: 4 x 100.
        self.assertEqual(strike_for_requirement(req, 57600, 100, {"otm_offset_steps": 4}), 58000)
        # 0 / negative / garbage leave the declared value in charge.
        for value in (0, -3, "abc", None, True):
            self.assertEqual(strike_for_requirement(req, 57600, 100, {"otm_offset_steps": value}), 57900)

    def test_a_param_named_points_is_read_as_points(self):
        req = ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2,
                                  param="otm_offset_points")
        self.assertEqual(strike_for_requirement(req, 57600, 100, {"otm_offset_points": 500}), 57100)
        self.assertEqual(requirement_distance(req, 100, {"otm_offset_points": 500}), 500.0)
        self.assertEqual(requirement_distance(req, 100, {}), 200.0)

    def test_fractional_grids_keep_landing_on_real_strikes(self):
        req = ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2)
        self.assertEqual(strike_for_requirement(req, 102.5, 2.5), 107.5)
        self.assertEqual(strike_for_requirement(req, 102.5, 2.5, {"x": 1}), 107.5)

    def test_default_requirements_are_the_atm_pair(self):
        keys = [(r.key, r.option_type, r.moneyness) for r in BaseStrategy.get_contract_requirements()]
        self.assertEqual(keys, [("atm_ce", "CE", "atm"), ("atm_pe", "PE", "atm")])

    def test_describe_names_the_direction_and_the_distance(self):
        req = ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2,
                                  param="otm_offset_steps")
        self.assertEqual(describe_requirement(req, 100),
                         "otm_ce: OTM CE +2 strikes (+200 pts) [param otm_offset_steps]")
        pe = ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2)
        self.assertEqual(describe_requirement(pe, 100), "otm_pe: OTM PE -2 strikes (-200 pts)")
        self.assertEqual(describe_requirement(ContractRequirement(key="atm_ce", option_type="CE"), 100),
                         "atm_ce: ATM CE")


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

    def test_contracts_for_resolves_every_requirement_key(self):
        api = FakeInstrumentsApi()
        resolver = ContractResolver(api, "BANKNIFTY", log=lambda _: None)
        requirements = [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2,
                                param="otm_offset_steps"),
            ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2,
                                param="otm_offset_steps"),
        ]
        contracts, missing = resolver.contracts_for(requirements, "2026-09-29", 57600, 100.0,
                                                    {"otm_offset_steps": 3})
        self.assertEqual(missing, [])
        self.assertEqual(contracts["atm_ce"].symbol, "NSE:BANKNIFTY26SEP57600CE")
        self.assertEqual(contracts["otm_ce"].symbol, "NSE:BANKNIFTY26SEP57900CE")
        self.assertEqual(contracts["otm_pe"].symbol, "NSE:BANKNIFTY26SEP57300PE")

    def test_contracts_for_reports_a_strike_the_master_lacks(self):
        resolver = ContractResolver(FakeInstrumentsApi(), "BANKNIFTY", log=lambda _: None)
        requirements = [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=30),
            ContractRequirement(key="wing_ce", option_type="CE", moneyness="otm", steps=30, optional=True),
        ]
        contracts, missing = resolver.contracts_for(requirements, "2026-09-29", 57600, 100.0)
        self.assertEqual(list(contracts), ["atm_ce"])
        self.assertEqual([(m["key"], m["strike"], m["optional"], m["failed"]) for m in missing],
                         [("otm_ce", 60600, False, False), ("wing_ce", 60600, True, False)])
        self.assertIn("no CE 60600 contract in the instrument master", missing[0]["reason"])

    def test_contracts_for_marks_a_transient_lookup_failure(self):
        api = FakeInstrumentsApi()

        def boom(underlying, expiry, strike, option_type):
            raise RuntimeError("502 Bad Gateway")

        api.get_exact_contract = boom
        resolver = ContractResolver(api, "BANKNIFTY", log=lambda _: None)
        _, missing = resolver.contracts_for([ContractRequirement(key="atm_ce", option_type="CE")],
                                            "2026-09-29", 57600, 100.0)
        self.assertTrue(missing[0]["failed"])
        self.assertIn("lookup failed", missing[0]["reason"])

    def test_contracts_for_without_an_expiry_reports_everything(self):
        resolver = ContractResolver(FakeInstrumentsApi(), "BANKNIFTY", log=lambda _: None)
        contracts, missing = resolver.contracts_for([ContractRequirement(key="atm_ce", option_type="CE")],
                                                    None, 57600, 100.0)
        self.assertEqual(contracts, {})
        self.assertEqual(missing[0]["reason"], "no option expiry in the instrument master")

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
