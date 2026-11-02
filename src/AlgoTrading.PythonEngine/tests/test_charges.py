"""
core/charges.py — what a fill costs, per exchange segment.

Crude was charged as an NSE index option until 28 Sep: STT 0.15% where MCX's
commodity transaction tax on options is 0.05%, and NSE and BSE futures and shares
until 2 Nov. The API's ChargeSchedule (OptionCharges.cs) holds the same numbers, so
live and backtest agree, and one test here reads that file to hold them to it.
"""
import re
import unittest
from pathlib import Path

import _bootstrap  # noqa: F401

from backtest.ledger import PaperLedger
from core.charges import MCX_FUTURE_RATES, MCX_OPTION_RATES, CostModel, segment_of


class SegmentTests(unittest.TestCase):
    def test_each_symbol_has_its_segment(self):
        cases = {
            "MCX:CRUDEOIL26OCT5500CE": "mcx-option",
            "mcx:crudeoil26oct5500pe": "mcx-option",
            "MCX:CRUDEOIL26OCTFUT": "mcx-future",
            "MCX:CRUDEOILM26OCTFUT": "mcx-future",
            "NSE:NIFTY26SEP25000CE": "index-option",
            "BSE:SENSEX26OCT82000PE": "index-option",
            "NSE:RELIANCE26NOV1400CE": "index-option",
            "NSE:BAJAJ-AUTO26NOV8000CE": "index-option",
            "NSE:NIFTY26NOVFUT": "nse-future",
            "nse:nifty26novfut": "nse-future",
            "NSE:RELIANCE26NOVFUT": "nse-future",
            "NSE:BAJAJ-AUTO26NOVFUT": "nse-future",
            "BSE:SENSEX26NOVFUT": "bse-future",
            "NSE:RELIANCE-EQ": "nse-equity",
            "nse:reliance-eq": "nse-equity",
            "NSE:BAJAJ-AUTO-EQ": "nse-equity",
            "NSE:M&M-EQ": "nse-equity",
            "NSE:SOMESTOCK-BE": "nse-equity",
            "BSE:RELIANCE-A": "bse-equity",
            "NSE:NIFTY50-INDEX": "index-option",
            "BSE:SENSEX-INDEX": "index-option",
            "NSE:BAJAJ-AUTO": "index-option",
            "MANUAL": "index-option",
            "": "index-option",
            None: "index-option",
        }
        for symbol, segment in cases.items():
            self.assertEqual(segment, segment_of(symbol), symbol)


class FuturesAndSharesTests(unittest.TestCase):
    """
    Until 2 Nov these were charged as index options: STT at 0.15% of notional. The
    worked figures are the C# test's too (OptionChargesTests), each line rounded to
    the paisa there.
    """

    def test_a_nifty_future_round_trip(self):
        # 65 units bought and sold at 25,000: 16,25,000 a side, two orders.
        c = CostModel().for_symbol("NSE:NIFTY26NOVFUT").charges(1_625_000, 1_625_000, orders=2)
        self.assertAlmostEqual(40.0, c["brokerage"], places=6)
        self.assertAlmostEqual(812.5, c["stt"], places=6)        # 0.05% of the sell side
        self.assertAlmostEqual(59.475, c["exchange"], places=6)  # NSE 0.00183% of both sides
        self.assertAlmostEqual(3.25, c["sebi"], places=6)
        self.assertAlmostEqual(32.5, c["stamp"], places=6)       # 0.002% of the buy side
        self.assertAlmostEqual(18.4905, c["gst"], places=6)
        self.assertAlmostEqual(966.2155, c["total"], places=4)
        # At the index-option rates it was charged at until now.
        self.assertGreater(CostModel().charges(1_625_000, 1_625_000, orders=2)["total"], 3_500)

    def test_a_sensex_future_pays_no_exchange_charge(self):
        c = CostModel().for_symbol("BSE:SENSEX26NOVFUT").charges(1_600_000, 1_600_000, orders=2)
        self.assertAlmostEqual(800.0, c["stt"], places=6)
        self.assertEqual(0.0, c["exchange"])
        self.assertAlmostEqual(882.976, c["total"], places=4)

    def test_shares_are_charged_as_intraday(self):
        # 1,000 RELIANCE at 1,400: 14,00,000 a side.
        c = CostModel().for_symbol("NSE:RELIANCE-EQ").charges(1_400_000, 1_400_000, orders=2)
        self.assertAlmostEqual(350.0, c["stt"], places=6)        # 0.025% of the sell side
        self.assertAlmostEqual(85.96, c["exchange"], places=6)   # NSE 0.00307% of both sides
        self.assertAlmostEqual(42.0, c["stamp"], places=6)       # 0.003% of the buy side
        self.assertAlmostEqual(543.9368, c["total"], places=4)

    def test_bse_shares_take_bses_exchange_charge(self):
        c = CostModel().for_symbol("BSE:RELIANCE-A").charges(1_400_000, 1_400_000, orders=2)
        self.assertAlmostEqual(105.0, c["exchange"], places=6)   # BSE 0.00375%
        self.assertAlmostEqual(566.404, c["total"], places=4)

    def test_a_backtest_fill_takes_its_own_segments_rates(self):
        costs = CostModel(slippage_pct=0.0, min_slippage=0.0)
        ledger = PaperLedger(1, 0.0, costs=costs)
        ledger.apply("OPEN_GROUP", "g1", [{"symbol": "NSE:RELIANCE-EQ", "side": "SELL", "quantity": 1000, "price": 1400.0}],
                     "2026-11-03T10:00:00Z")
        self.assertAlmostEqual(costs.for_symbol("NSE:RELIANCE-EQ").fill_charges("SELL", 1_400_000), ledger.charges, places=6)
        self.assertLess(ledger.charges, costs.fill_charges("SELL", 1_400_000) / 4)


class OptionAndMcxFiguresDidNotMoveTests(unittest.TestCase):
    def test_the_defaults_and_the_mcx_tables_are_as_they_were(self):
        model = CostModel()
        self.assertEqual((20.0, 0.15, 0.03503, 0.0001, 0.003, 18.0),
                         (model.brokerage_per_order, model.stt_sell_pct, model.exchange_txn_pct, model.sebi_fee_pct,
                          model.stamp_buy_pct, model.gst_pct))
        self.assertEqual({"stt_sell_pct": 0.05, "exchange_txn_pct": 0.0418, "stamp_buy_pct": 0.003}, MCX_OPTION_RATES)
        self.assertEqual({"stt_sell_pct": 0.01, "exchange_txn_pct": 0.0021, "stamp_buy_pct": 0.002}, MCX_FUTURE_RATES)


class TheApisScheduleIsTheSameTests(unittest.TestCase):
    """Reads the C# itself: a "change one, change both" comment is not what keeps two tables equal."""

    SOURCE = Path(__file__).resolve().parents[3] / "src" / "AlgoTrading.Application" / "Risk" / "OptionCharges.cs"
    SCHEDULE = re.compile(r'new\("(?P<name>[^"]+)",\s*(?P<brokerage>[\d.]+)m,\s*(?P<stt>[\d.]+)m,\s*(?P<exchange>[\d.]+)m,'
                          r'\s*(?P<sebi>[\d.]+)m,\s*(?P<stamp>[\d.]+)m,\s*(?P<gst>[\d.]+)m\)')
    SEGMENTS = {
        "index options": "NSE:NIFTY26SEP25000CE",
        "MCX options": "MCX:CRUDEOIL26OCT5500CE",
        "MCX futures": "MCX:CRUDEOIL26OCTFUT",
        "NSE futures": "NSE:NIFTY26NOVFUT",
        "BSE futures": "BSE:SENSEX26NOVFUT",
        "NSE shares (intraday)": "NSE:RELIANCE-EQ",
        "BSE shares (intraday)": "BSE:RELIANCE-A",
    }

    def test_every_schedule_matches_rate_for_rate(self):
        found = {m["name"]: m for m in self.SCHEDULE.finditer(self.SOURCE.read_text(encoding="utf-8"))}

        self.assertEqual(set(self.SEGMENTS), set(found), "a schedule here and not there, or the other way round")
        for name, symbol in self.SEGMENTS.items():
            rates = CostModel().for_symbol(symbol)
            with self.subTest(schedule=name):
                self.assertEqual(float(found[name]["brokerage"]), rates.brokerage_per_order)
                self.assertEqual(float(found[name]["stt"]), rates.stt_sell_pct)
                self.assertEqual(float(found[name]["exchange"]), rates.exchange_txn_pct)
                self.assertEqual(float(found[name]["sebi"]), rates.sebi_fee_pct)
                self.assertEqual(float(found[name]["stamp"]), rates.stamp_buy_pct)
                self.assertEqual(float(found[name]["gst"]), rates.gst_pct)


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
