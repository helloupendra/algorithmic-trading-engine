"""
core/charges.py — what a fill costs, per exchange segment.

Crude was charged as an NSE index option until 28 Sep: STT 0.15% where MCX's
commodity transaction tax on options is 0.05%. The API's ChargeSchedule
(OptionCharges.cs) pins the same numbers, so live and backtest agree.
"""
import unittest

import _bootstrap  # noqa: F401

from backtest.ledger import PaperLedger
from core.charges import CostModel, segment_of


class SegmentTests(unittest.TestCase):
    def test_each_symbol_has_its_segment(self):
        cases = {
            "MCX:CRUDEOIL26OCT5500CE": "mcx-option",
            "mcx:crudeoil26oct5500pe": "mcx-option",
            "MCX:CRUDEOIL26OCTFUT": "mcx-future",
            "MCX:CRUDEOILM26OCTFUT": "mcx-future",
            "NSE:NIFTY26SEP25000CE": "index-option",
            "BSE:SENSEX26OCT82000PE": "index-option",
            "": "index-option",
            None: "index-option",
        }
        for symbol, segment in cases.items():
            self.assertEqual(segment, segment_of(symbol), symbol)


class McxChargesTests(unittest.TestCase):
    def test_crude_options_match_the_api(self):
        # 2 lots (100 barrels) bought at 150 and sold at 180; the C# test gives
        # 98.74 with each line rounded to the paisa.
        c = CostModel().for_symbol("MCX:CRUDEOIL26OCT5500CE").charges(30_000, 36_000, orders=2)
        self.assertAlmostEqual(18.0, c["stt"], places=6)
        self.assertAlmostEqual(27.588, c["exchange"], places=6)
        self.assertAlmostEqual(0.9, c["stamp"], places=6)
        self.assertAlmostEqual(98.7317, c["total"], places=3)

    def test_crude_futures_have_their_own_rates(self):
        c = CostModel().for_symbol("MCX:CRUDEOIL26OCTFUT").charges(600_000, 610_000, orders=2)
        self.assertAlmostEqual(61.0, c["stt"], places=6)
        self.assertAlmostEqual(25.41, c["exchange"], places=6)
        self.assertAlmostEqual(12.0, c["stamp"], places=6)

    def test_index_options_are_unchanged_and_the_runs_own_slippage_is_kept(self):
        model = CostModel(slippage_pct=1.0, brokerage_per_order=10.0)
        self.assertIs(model, model.for_symbol("NSE:NIFTY26SEP25000CE"))
        mcx = model.for_symbol("MCX:CRUDEOIL26OCT5500CE")
        self.assertEqual((1.0, 10.0), (mcx.slippage_pct, mcx.brokerage_per_order))
        self.assertEqual(0.05, mcx.stt_sell_pct)


class LedgerChargesTests(unittest.TestCase):
    def test_a_backtest_fill_in_crude_is_charged_at_mcx_rates(self):
        costs = CostModel(slippage_pct=0.0, min_slippage=0.0)
        charged = {}
        for symbol in ("NSE:NIFTY26SEP25000CE", "MCX:CRUDEOIL26OCT5500CE"):
            ledger = PaperLedger(100, 0.0, costs=costs)
            ledger.apply("OPEN_GROUP", "g1", [{"symbol": symbol, "side": "SELL", "quantity": 2, "price": 180.0}],
                         "2026-09-28T10:00:00Z")
            charged[symbol] = ledger.charges
        # The same 36,000 of premium sold: STT 54 at index rates, CTT 18 on MCX.
        self.assertAlmostEqual(costs.fill_charges("SELL", 36_000), charged["NSE:NIFTY26SEP25000CE"], places=6)
        self.assertAlmostEqual(costs.for_symbol("MCX:CRUDEOIL26OCT5500CE").fill_charges("SELL", 36_000),
                               charged["MCX:CRUDEOIL26OCT5500CE"], places=6)
        self.assertGreater(charged["NSE:NIFTY26SEP25000CE"] - charged["MCX:CRUDEOIL26OCT5500CE"], 30.0)


if __name__ == "__main__":
    unittest.main()
