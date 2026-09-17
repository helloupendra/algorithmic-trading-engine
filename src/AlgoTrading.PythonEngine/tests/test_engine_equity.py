"""
An equity backtest: the run trades the instrument itself in shares, with no
expiry, no strike and no option contracts anywhere in the loop.
"""

import unittest
from datetime import date, datetime, time, timedelta
from typing import Any, Dict, List

import _bootstrap  # noqa: F401

from backtest.timeutil import IST, iso_utc, ist_day_end_utc, ist_day_start_utc
from strategies.base_strategy import StrategySignal
from test_engine import EngineRunner, FakeApi

SYMBOL = "NSE:RELIANCE-EQ"
DAY = date(2026, 8, 19)


def bars(prices: List[float]) -> List[Dict[str, Any]]:
    rows = []
    cursor = datetime.combine(DAY, time(9, 15), tzinfo=IST)
    for close in prices:
        rows.append({"symbol": SYMBOL, "resolution": "5", "timestampUtc": iso_utc(cursor),
                     "open": close, "high": close, "low": close, "close": close, "volume": 1000})
        cursor += timedelta(minutes=5)
    return rows


def equity_run(**params) -> Dict[str, Any]:
    import json
    merged = {"lots": 10, "underlying": "RELIANCE", "resolution": "5m", "instrument_kind": "equity",
              "eod_square_off_ist": "15:15", "charges_per_lot": 0}
    merged.update(params)
    return {"id": 77, "mode": "OfflineReplay", "symbol": SYMBOL, "resolution": "5",
            "strategyName": "Scripted", "fromUtc": iso_utc(ist_day_start_utc(DAY)),
            "toUtc": iso_utc(ist_day_end_utc(DAY)), "parametersJson": json.dumps(merged),
            "initialCapital": 200_000, "userId": 1}


def buy():
    def action(inp, strategy):
        return [StrategySignal(strategy_name="Scripted", signal_type="BUY", timestamp_utc=inp.timestamp_utc,
                               reason="scripted long", legs=[], metadata={})]
    return action


def close_all(group: str):
    def action(inp, strategy):
        return [StrategySignal(strategy_name="Scripted", signal_type="CLOSE_GROUP", timestamp_utc=inp.timestamp_utc,
                               reason="scripted exit", legs=[{"symbol": SYMBOL, "side": "SELL", "quantity": 10}],
                               metadata={"group_id": group})]
    return action


class EquityBacktestTests(EngineRunner, unittest.TestCase):
    def test_a_buy_signal_holds_shares_and_prices_them_from_the_same_series(self):
        prices = [1000.0, 1005.0, 1010.0, 1020.0, 1015.0, 1030.0]
        api = FakeApi({(SYMBOL, "5"): bars(prices)})

        outcome, strategy = self.run_engine(api, {1: buy()}, equity_run())

        self.assertEqual(outcome.status, "Completed")
        opened = [s for s in api.signals if s["signalType"] == "OPEN_GROUP"]
        self.assertEqual(len(opened), 1)
        leg = opened[0]["legs"][0]
        self.assertEqual((leg["symbol"], leg["side"], leg["quantity"]), (SYMBOL, "BUY", 10))
        self.assertEqual(leg["price"], 1005.0)                       # the bar it was signalled on
        # No option machinery was consulted: no contract lookups, no expiries needed.
        self.assertEqual(api.contract_calls, 0)
        # Squared off at the end of the range: 10 shares from 1,005 to 1,030.
        self.assertAlmostEqual(outcome.ledger.total_pnl(), (1030.0 - 1005.0) * 10, places=2)
        self.assertEqual(outcome.summary["lotSize"], 1)
        self.assertEqual(outcome.summary["lotSizeSource"], "shares")

    def test_the_strategy_sees_no_strike_and_no_contracts(self):
        api = FakeApi({(SYMBOL, "5"): bars([100.0] * 5)})
        outcome, strategy = self.run_engine(api, {}, equity_run())
        self.assertEqual(outcome.status, "Completed")
        first = strategy.inputs[0]
        self.assertIsNone(first.atm_strike)
        self.assertEqual(first.contracts, {})
        self.assertEqual(first.lot_size, 1)

    def test_a_short_sale_is_allowed_and_books_the_move_the_other_way(self):
        prices = [500.0, 495.0, 490.0, 480.0]
        api = FakeApi({(SYMBOL, "5"): bars(prices)})

        def sell():
            def action(inp, strategy):
                return [StrategySignal(strategy_name="Scripted", signal_type="SELL",
                                       timestamp_utc=inp.timestamp_utc, reason="scripted short",
                                       legs=[], metadata={})]
            return action

        outcome, _ = self.run_engine(api, {1: sell()}, equity_run())
        self.assertAlmostEqual(outcome.ledger.total_pnl(), (495.0 - 480.0) * 10, places=2)


if __name__ == "__main__":
    unittest.main()
