"""
Contract requirements through a full replay: a strategy that asks for OTM legs
gets them (so a strangle enters instead of producing zero trades), the run
parameters move the strikes, bars can be requested for any requirement key, and
a strike the instrument master lacks is reported once instead of failing
silently.
"""

import unittest
from typing import Any, Callable, Dict, List, Optional

import _bootstrap  # noqa: F401

from backtest.engine import run_backtest
from backtest.timeutil import iso_utc
from strategies.base_strategy import ContractRequirement, DataRequirement, StrategySignal
from test_engine import (
    DAY1,
    LOT_SIZE,
    SPOT,
    FakeApi,
    ScriptedStrategy,
    bar_start,
    contract_symbol,
    index_rows,
    option_rows,
    quiet,
    run_row,
)

# Spot sits at 57620 all session, so the ATM strike is 57600 on the 100 grid.
ATM = 57600
OTM_CE = contract_symbol(57800, "CE")      # +2 strikes
OTM_PE = contract_symbol(57400, "PE")      # -2 strikes
FAR_CE = contract_symbol(57900, "CE")      # +3 strikes
FAR_PE = contract_symbol(57300, "PE")      # -3 strikes

STRANGLE = [
    ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2, param="otm_offset_steps"),
    ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2, param="otm_offset_steps"),
]


class CappedApi(FakeApi):
    """FakeApi whose instrument master stops at 58000, as a real chain does."""

    def get_exact_contract(self, underlying, expiry, strike, option_type):
        self.contract_calls += 1
        if float(strike) > 58000:
            return None
        self.contract_calls -= 1
        return super().get_exact_contract(underlying, expiry, strike, option_type)


def api_with(prices: Dict[str, float]) -> CappedApi:
    """One session of index candles plus a flat option series per named symbol."""
    candles = {(SPOT, "5"): index_rows([DAY1])}
    for symbol, price in prices.items():
        candles[(symbol, "5")] = option_rows(symbol, [DAY1], lambda i, p=price: p)
    return CappedApi(candles)


def open_keys(keys: List[tuple], group: str = "g1"):
    """OPEN_GROUP over requirement keys; emits nothing while a key is missing."""
    def action(inp, strategy):
        legs = []
        for key, side, lots in keys:
            contract = inp.contracts.get(key)
            if contract is None:
                return []
            legs.append({"symbol": contract.symbol, "side": side, "quantity": lots})
        return [StrategySignal(strategy_name="Scripted", signal_type="OPEN_GROUP",
                               timestamp_utc=inp.timestamp_utc, reason="scripted strangle",
                               legs=legs, metadata={"group_id": group})]
    return action


class ContractRequirementRunner:
    """Drives the engine with a scripted strategy that declares `contract_reqs`."""

    def run_engine(self, api: FakeApi, script: Dict[int, Callable], row: Dict[str, Any],
                   contract_reqs: List[ContractRequirement],
                   data_reqs: Optional[List[DataRequirement]] = None,
                   log: Callable[[str], None] = quiet):
        holder: Dict[str, ScriptedStrategy] = {}

        def factory(params=None):
            strategy = ScriptedStrategy(params, script, data_reqs)
            # The engine asks the instance, so a per-test list needs no subclass.
            strategy.get_contract_requirements = lambda _params=None: list(contract_reqs)
            holder["s"] = strategy
            return strategy

        outcome = run_backtest(api, row, factory, log=log, progress_interval=0.0)
        return outcome, holder["s"]


class OtmContractTests(ContractRequirementRunner, unittest.TestCase):
    def test_a_strangle_enters_on_its_otm_legs(self):
        api = api_with({OTM_CE: 90.0, OTM_PE: 70.0})
        outcome, strategy = self.run_engine(
            api, {0: open_keys([("otm_ce", "SELL", 1), ("otm_pe", "SELL", 1)])},
            run_row(DAY1, DAY1), STRANGLE)

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(outcome.summary["skippedEntries"], [])
        self.assertEqual(strategy.inputs[0].contracts["otm_ce"].symbol, OTM_CE)
        self.assertEqual(strategy.inputs[0].contracts["otm_ce"].strike_price, 57800)
        self.assertEqual(strategy.inputs[0].contracts["otm_pe"].strike_price, 57400)
        self.assertNotIn("atm_ce", strategy.inputs[0].contracts)     # only what was declared

        open_sig = api.signals[0]
        self.assertEqual(open_sig["signalType"], "OPEN_GROUP")
        self.assertEqual({(l["symbol"], l["price"]) for l in open_sig["legs"]},
                         {(OTM_CE, 90.0), (OTM_PE, 70.0)})
        self.assertEqual(outcome.summary["trades"], 2)               # both legs squared off at 15:15

    def test_the_run_parameter_moves_the_strikes(self):
        api = api_with({OTM_CE: 90.0, OTM_PE: 70.0, FAR_CE: 60.0, FAR_PE: 45.0})
        outcome, strategy = self.run_engine(
            api, {0: open_keys([("otm_ce", "SELL", 1), ("otm_pe", "SELL", 1)])},
            run_row(DAY1, DAY1, otm_offset_steps=3), STRANGLE)

        self.assertEqual(strategy.inputs[0].contracts["otm_ce"].symbol, FAR_CE)
        self.assertEqual(strategy.inputs[0].contracts["otm_pe"].symbol, FAR_PE)
        self.assertEqual({l["symbol"] for l in api.signals[0]["legs"]}, {FAR_CE, FAR_PE})
        self.assertEqual(outcome.summary["skippedEntries"], [])

    def test_a_four_leg_butterfly_gets_body_and_wings(self):
        wings = [
            ContractRequirement(key="atm_ce", option_type="CE"),
            ContractRequirement(key="atm_pe", option_type="PE"),
            ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=2,
                                param="wing_offset_steps"),
            ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2,
                                param="wing_offset_steps"),
        ]
        atm_ce, atm_pe = contract_symbol(ATM, "CE"), contract_symbol(ATM, "PE")
        api = api_with({atm_ce: 120.0, atm_pe: 110.0, OTM_CE: 90.0, OTM_PE: 70.0})
        outcome, _ = self.run_engine(
            api, {0: open_keys([("atm_ce", "SELL", 1), ("atm_pe", "SELL", 1),
                                ("otm_ce", "BUY", 1), ("otm_pe", "BUY", 1)])},
            run_row(DAY1, DAY1), wings)

        self.assertEqual(len(api.signals[0]["legs"]), 4)
        self.assertEqual({l["symbol"] for l in api.signals[0]["legs"]}, {atm_ce, atm_pe, OTM_CE, OTM_PE})
        self.assertEqual(outcome.summary["trades"], 4)

    def test_an_itm_requirement_resolves_on_the_other_side_of_the_atm_strike(self):
        itm_ce = contract_symbol(57400, "CE")
        api = api_with({itm_ce: 260.0})
        _, strategy = self.run_engine(
            api, {}, run_row(DAY1, DAY1),
            [ContractRequirement(key="itm_ce", option_type="CE", moneyness="itm", steps=2)])
        self.assertEqual(strategy.inputs[0].contracts["itm_ce"].symbol, itm_ce)

    def test_bars_can_be_requested_for_any_requirement_key(self):
        api = api_with({OTM_CE: 90.0, OTM_PE: 70.0})
        _, strategy = self.run_engine(
            api, {}, run_row(DAY1, DAY1), STRANGLE,
            data_reqs=[DataRequirement(symbol_type="index", resolution="5m"),
                       DataRequirement(symbol_type="otm_ce", resolution="5m")])
        bars = strategy.inputs[10].bars["5m"]
        self.assertEqual(len(bars["otm_ce"]), 11)
        self.assertEqual(bars["otm_ce"][0].symbol, OTM_CE)
        self.assertEqual(bars["otm_ce"][10].timestamp_utc, iso_utc(bar_start(DAY1, 10, 5)))
        self.assertEqual(bars["otm_ce"][3].close, 90.0)

    def test_a_strike_the_master_lacks_is_reported_once(self):
        # 30 strikes out lands on 60600, which the fake master does not carry.
        far = [ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=30),
               ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2)]
        api = api_with({OTM_PE: 70.0})
        outcome, strategy = self.run_engine(
            api, {0: open_keys([("otm_ce", "SELL", 1), ("otm_pe", "SELL", 1)])},
            run_row(DAY1, DAY1), far)

        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(api.signals, [])                            # the entry never fired
        self.assertNotIn("otm_ce", strategy.inputs[0].contracts)
        self.assertIn("otm_pe", strategy.inputs[0].contracts)
        skipped = outcome.summary["skippedEntries"]
        self.assertEqual(len(skipped), 1)                            # once, not once per bar
        self.assertEqual(skipped[0]["symbol"], "otm_ce 60600 CE")
        self.assertIn("no CE 60600 contract in the instrument master", skipped[0]["reason"])
        self.assertEqual(skipped[0]["atUtc"], iso_utc(bar_start(DAY1, 9, 15)))
        self.assertTrue(any("needs otm_ce" in n for n in outcome.summary["dataNotes"]))

    def test_an_optional_requirement_is_not_reported_as_a_skip(self):
        far = [ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=30, optional=True),
               ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm", steps=2)]
        api = api_with({OTM_PE: 70.0})
        outcome, _ = self.run_engine(api, {}, run_row(DAY1, DAY1), far)
        self.assertEqual(outcome.summary["skippedEntries"], [])
        self.assertFalse(any("needs otm_ce" in n for n in outcome.summary["dataNotes"]))

    def test_the_config_line_names_every_requirement_and_its_distance(self):
        api = api_with({OTM_CE: 90.0, OTM_PE: 70.0})
        logs: List[str] = []
        self.run_engine(api, {}, run_row(DAY1, DAY1), STRANGLE, log=logs.append)
        line = [l for l in logs if l.startswith("[CONFIG] contracts")][0]
        self.assertIn("strike step 100", line)
        self.assertIn("otm_ce: OTM CE +2 strikes (+200 pts) [param otm_offset_steps]", line)
        self.assertIn("otm_pe: OTM PE -2 strikes (-200 pts) [param otm_offset_steps]", line)

    def test_declared_keys_without_a_contract_do_not_fall_back_to_a_symbol_lookup(self):
        # bars["5m"]["otm_ce"] must stay absent rather than being read as the
        # literal symbol "otm_ce" (which would silently return nothing).
        far = [ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm", steps=30)]
        api = api_with({})
        _, strategy = self.run_engine(
            api, {}, run_row(DAY1, DAY1), far,
            data_reqs=[DataRequirement(symbol_type="otm_ce", resolution="5m")])
        self.assertNotIn("otm_ce", strategy.inputs[0].contracts)
        self.assertNotIn("otm_ce", strategy.inputs[0].bars.get("5m", {}))

    def test_an_exact_symbol_in_a_data_requirement_still_works(self):
        api = api_with({OTM_CE: 90.0, OTM_PE: 70.0})
        _, strategy = self.run_engine(
            api, {}, run_row(DAY1, DAY1), STRANGLE,
            data_reqs=[DataRequirement(symbol_type=OTM_PE, resolution="5m")])
        bars = strategy.inputs[5].bars["5m"]
        self.assertEqual(len(bars[OTM_PE]), 6)
        self.assertEqual(bars[OTM_PE][0].symbol, OTM_PE)


class CatalogStrategyTests(unittest.TestCase):
    """
    The four catalog strategies that read OTM keys now enter, instead of
    completing with zero trades because only ATM contracts existed.
    """

    def replay(self, name: str, contracts: List[tuple], **params) -> tuple:
        candles = {(SPOT, "5"): index_rows([DAY1])}
        for strike, option_type, price in contracts:
            symbol = contract_symbol(strike, option_type)
            candles[(symbol, "5")] = option_rows(symbol, [DAY1], lambda i, p=price: p)
        api = FakeApi(candles)
        row = run_row(DAY1, DAY1, **params)
        row["strategyName"] = name
        from strategies.registry import load_strategy_factories
        outcome = run_backtest(api, row, load_strategy_factories()[name], log=quiet, progress_interval=0.0)
        return outcome, api

    def legs_of_first_signal(self, api: FakeApi) -> set:
        return {(l["symbol"], l["side"]) for l in api.signals[0]["legs"]}

    def test_short_strangle_sells_both_otm_legs(self):
        outcome, api = self.replay("ShortStrangle", [(57800, "CE", 90.0), (57400, "PE", 70.0)])
        self.assertEqual(outcome.status, "Completed")
        self.assertEqual(outcome.summary["skippedEntries"], [])
        self.assertEqual(self.legs_of_first_signal(api),
                         {(contract_symbol(57800, "CE"), "SELL"), (contract_symbol(57400, "PE"), "SELL")})
        self.assertEqual(outcome.summary["trades"], 2)

    def test_short_strangle_honours_the_offset_parameter(self):
        outcome, api = self.replay("ShortStrangle", [(57900, "CE", 80.0), (57300, "PE", 60.0)],
                                   otm_offset_steps=3)
        self.assertEqual(self.legs_of_first_signal(api),
                         {(contract_symbol(57900, "CE"), "SELL"), (contract_symbol(57300, "PE"), "SELL")})
        self.assertEqual(outcome.summary["trades"], 2)

    def test_iron_butterfly_opens_four_legs(self):
        outcome, api = self.replay("IronButterfly", [(57600, "CE", 120.0), (57600, "PE", 110.0),
                                                     (58000, "CE", 50.0), (57200, "PE", 40.0)])
        self.assertEqual(self.legs_of_first_signal(api), {
            (contract_symbol(57600, "CE"), "SELL"), (contract_symbol(57600, "PE"), "SELL"),
            (contract_symbol(58000, "CE"), "BUY"), (contract_symbol(57200, "PE"), "BUY"),
        })
        self.assertEqual(outcome.summary["trades"], 4)

    def test_the_two_vertical_spreads_open_their_atm_and_otm_legs(self):
        _, bull = self.replay("BullCallSpread", [(57600, "CE", 120.0), (57800, "CE", 90.0)])
        self.assertEqual(self.legs_of_first_signal(bull),
                         {(contract_symbol(57600, "CE"), "BUY"), (contract_symbol(57800, "CE"), "SELL")})
        _, bear = self.replay("BearPutSpread", [(57600, "PE", 110.0), (57400, "PE", 70.0)])
        self.assertEqual(self.legs_of_first_signal(bear),
                         {(contract_symbol(57600, "PE"), "BUY"), (contract_symbol(57400, "PE"), "SELL")})


class LotSizeSanityTests(ContractRequirementRunner, unittest.TestCase):
    def test_pnl_still_uses_lots_times_lot_size_on_otm_legs(self):
        candles = {(SPOT, "5"): index_rows([DAY1])}
        candles[(OTM_CE, "5")] = option_rows(OTM_CE, [DAY1], lambda i: 90.0 - i)
        api = FakeApi(candles)
        outcome, _ = self.run_engine(api, {0: open_keys([("otm_ce", "SELL", 2)])}, run_row(DAY1, DAY1, lots=2),
                                     [STRANGLE[0]])
        # Short from 90, squared off at 15:15 (bar 72) at 18: +72 points x 2 lots x 30.
        self.assertAlmostEqual(outcome.ledger.realized_pnl(), 72.0 * 2 * LOT_SIZE)
        self.assertEqual(outcome.summary["trades"], 1)


if __name__ == "__main__":
    unittest.main()
