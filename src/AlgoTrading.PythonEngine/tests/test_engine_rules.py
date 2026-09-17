"""
The run's own rules inside the replay: how many entries a day may take, the
exits the run adds (the clock and the moving stop), which contract a bare
BUY/SELL takes, and what a fill costs. Uses the engine smoke-test harness.
"""

import json
import unittest
from datetime import time
from typing import Any, Dict, List

import _bootstrap  # noqa: F401

from strategies.base_strategy import StrategySignal
from test_engine import (  # the shared fake API, scripted strategy and helpers
    CE,
    DAY1,
    DAY2,
    EngineRunner,
    PE,
    close_ce,
    contract_symbol,
    make_api,
    open_ce,
    option_rows,
    reason_of,
    run_row,
)


def buy_signal(group: str = "b1"):
    """A bare BUY, as Ghost emits: the engine turns it into an option leg."""
    def action(inp, strategy):
        return [StrategySignal(strategy_name="Scripted", signal_type="BUY", timestamp_utc=inp.timestamp_utc,
                               reason="scripted view", legs=[], metadata={"group_id": group})]
    return action


def summary_of(api) -> Dict[str, Any]:
    return api.completed[-1]["summary"]


def opened_symbols(api) -> List[str]:
    out = []
    for signal in api.signals:
        if signal["signalType"] == "OPEN_GROUP":
            out.extend(leg["symbol"] for leg in signal["legs"])
    return out


class LimitTests(EngineRunner, unittest.TestCase):
    def test_only_the_first_entry_of_the_day_is_taken(self):
        api = make_api()
        script = {2: open_ce(1, "g1"), 5: open_ce(1, "g2"), 8: open_ce(1, "g3")}
        outcome, _ = self.run_engine(api, script, run_row(DAY1, DAY1, limits={"max_trades_per_day": 1}))

        self.assertEqual(len(opened_symbols(api)), 1)
        summary = summary_of(api)
        self.assertEqual(summary["limitBlocks"], {"max_trades_per_day": 2})
        self.assertEqual(len(summary["skippedEntries"]), 2)
        self.assertIn("max_trades_per_day", summary["skippedEntries"][0]["reason"])
        self.assertIn("Run limits refused 2 entry(ies)", " ".join(summary["dataNotes"]))

    def test_a_second_position_waits_until_the_first_is_closed(self):
        api = make_api()
        script = {2: open_ce(1, "g1"), 4: open_ce(1, "g2"), 6: close_ce(1, "g1"), 8: open_ce(1, "g3")}
        self.run_engine(api, script, run_row(DAY1, DAY1, limits={"max_open_groups": 1}))

        summary = summary_of(api)
        self.assertEqual(summary["limitBlocks"], {"max_open_groups": 1})
        self.assertEqual(len(opened_symbols(api)), 2)          # g1 and, after g1 closed, g3

    def test_a_loss_starts_a_cooldown(self):
        # The CE falls, so closing at bar 4 books a loss; the next entry is inside the cooldown.
        api = make_api(lambda i: 100.0 - i)
        script = {1: open_ce(1, "g1"), 4: close_ce(1, "g1"), 5: open_ce(1, "g2"), 20: open_ce(1, "g3")}
        self.run_engine(api, script, run_row(DAY1, DAY1, limits={"cooldown_after_loss_minutes": 30}))

        summary = summary_of(api)
        self.assertEqual(summary["limitBlocks"], {"cooldown_after_loss": 1})
        self.assertEqual(len(opened_symbols(api)), 2)          # g1, then g3 after the cooldown

    def test_two_losses_end_the_day_and_tomorrow_starts_clean(self):
        api = make_api(lambda i: 100.0 - i, days=[DAY1, DAY2])
        script = {1: open_ce(1, "g1"), 3: close_ce(1, "g1"), 5: open_ce(1, "g2"), 7: close_ce(1, "g2"),
                  9: open_ce(1, "g3"), 80: open_ce(1, "g4")}
        self.run_engine(api, script, run_row(DAY1, DAY2, limits={"stop_after_losses": 2}))

        summary = summary_of(api)
        self.assertEqual(summary["limitDayCloses"], 1)
        self.assertEqual(summary["limitBlocks"], {"day_closed": 1})
        self.assertIn("ended early by a run limit", " ".join(summary["dataNotes"]))
        self.assertEqual(len(opened_symbols(api)), 3)          # two on day 1, then day 2 opens again


class ExitTests(EngineRunner, unittest.TestCase):
    def test_a_position_is_closed_when_its_time_is_up(self):
        api = make_api()
        outcome, _ = self.run_engine(api, {1: open_ce(1)}, run_row(DAY1, DAY1, exits={"time_exit_minutes": 20}))

        closes = [s for s in api.signals if s["signalType"] == "CLOSE_GROUP"]
        self.assertEqual(len(closes), 1)
        self.assertIn("Run exit (time exit)", reason_of(closes[0]))
        self.assertEqual(summary_of(api)["runExits"], {"time_exit": 1})

    def test_the_stop_moves_to_entry_and_closes_the_giveback(self):
        # Premium runs 100 -> 130 (bar 8) and falls back: the +15% stop at entry closes it.
        prices = [100, 105, 112, 120, 126, 130, 128, 120, 108, 99, 98, 97]
        api = make_api(lambda i: float(prices[i]) if i < len(prices) else 97.0)
        self.run_engine(api, {0: open_ce(1)},
                        run_row(DAY1, DAY1, exits={"breakeven_after_percent": 15, "step_trail_step_percent": 5}))

        closes = [s for s in api.signals if s["signalType"] == "CLOSE_GROUP"]
        self.assertEqual(len(closes), 1)
        reason = reason_of(closes[0])
        self.assertIn("Run exit (moving stop)", reason)
        self.assertEqual(summary_of(api)["runExits"], {"moving_stop": 1})


class ContractTests(EngineRunner, unittest.TestCase):
    def test_a_bare_buy_takes_the_atm_call(self):
        api = make_api()
        self.run_engine(api, {2: buy_signal()}, run_row(DAY1, DAY1))
        self.assertEqual(opened_symbols(api), [CE])

    def test_the_run_can_flip_the_side_and_move_the_strike(self):
        api = make_api()
        self.run_engine(api, {2: buy_signal()}, run_row(DAY1, DAY1, contract={"flip": True}))
        self.assertEqual(opened_symbols(api), [PE])

        # The ATM is 57600 on a 100-point grid, so one strike in the money is 57500.
        itm = contract_symbol(57500, "CE")
        api = make_api()
        api.candles[(itm, "5")] = option_rows(itm, [DAY1], lambda i: 150.0 + i)
        self.run_engine(api, {2: buy_signal()}, run_row(DAY1, DAY1, contract={"strike_offset": 1}))
        self.assertEqual(opened_symbols(api), [itm])


class CostTests(EngineRunner, unittest.TestCase):
    def test_slippage_moves_the_fill_and_the_charges_are_booked(self):
        api = make_api(lambda i: 100.0)
        self.run_engine(api, {1: open_ce(1), 4: close_ce(1)},
                        run_row(DAY1, DAY1, costs={"slippage_percent": 0.5}))

        legs = [s["legs"][0] for s in api.signals]
        self.assertAlmostEqual(legs[0]["price"], 100.5, places=2)     # the buy pays 0.5% more
        self.assertAlmostEqual(legs[1]["price"], 99.5, places=2)      # the sell receives 0.5% less
        summary = summary_of(api)
        self.assertAlmostEqual(summary["slippage"], 0.5 * 30 * 2, places=2)   # 0.5 points x 30 x two fills
        self.assertGreater(summary["charges"], 40.0)                  # two orders of brokerage alone
        self.assertIn("slippage a side", " ".join(summary["dataNotes"]))


if __name__ == "__main__":
    unittest.main()
