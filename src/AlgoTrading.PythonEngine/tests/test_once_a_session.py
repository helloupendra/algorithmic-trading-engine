"""
The strategies that enter once and hold (ShortStraddle, ShortStrangle,
IronButterfly, ExampleStraddle, BullCallSpread, BearPutSpread) enter again on a
new session of a backtest once the run has closed their group.

The runner makes the state once a run. A live run is one session, but a
backtest runs many, and until 31 Oct 2026 `is_invested` stayed true after the
first day's square-off: a two-year backtest of a short straddle was one trade.
"""

import unittest

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.bearish.bear_put_spread_strategy import BearPutSpreadStrategy
from strategies.bullish.bull_call_spread_strategy import BullCallSpreadStrategy
from strategies.example.example_straddle import ExampleStraddleStrategy
from strategies.neutral.butterfly_strategy import IronButterflyStrategy
from strategies.neutral.straddle_strategy import StraddleStrategy
from strategies.neutral.strangle_strategy import StrangleStrategy

STRATEGIES = [StraddleStrategy, StrangleStrategy, IronButterflyStrategy, ExampleStraddleStrategy,
              BullCallSpreadStrategy, BearPutSpreadStrategy]


def _contract(key, strike, kind):
    return OptionContract(symbol=f"NIFTY_{key}_{strike:g}", underlying="NIFTY", expiry_date="2026-11-03",
                          strike_price=strike, option_type=kind)


CONTRACTS = {
    "atm_ce": _contract("atm_ce", 24000, "CE"), "atm_pe": _contract("atm_pe", 24000, "PE"),
    "otm_ce": _contract("otm_ce", 24200, "CE"), "otm_pe": _contract("otm_pe", 23800, "PE"),
}


def _bar(strategy, state, timestamp, open_groups=None, mode="OfflineReplay"):
    metadata = {} if open_groups is None else {"open_groups": open_groups}
    return strategy.on_bar(state, StrategyInput(
        mode=mode, timestamp_utc=timestamp, underlying="NIFTY", spot_price=24010.0, atm_strike=24000.0,
        strike_step=50.0, lot_size=65, contracts=dict(CONTRACTS), metadata=metadata))


def _opens(signals):
    return [s for s in signals if s.signal_type == "OPEN_GROUP"]


class OnceASessionTests(unittest.TestCase):
    def test_a_backtest_enters_again_on_the_next_session_after_the_square_off(self):
        for cls in STRATEGIES:
            with self.subTest(strategy=cls.name):
                strategy = cls({})
                state = strategy.initialize_state()
                first = _opens(_bar(strategy, state, "2026-10-29T03:50:00+00:00", open_groups=[]))
                self.assertEqual(1, len(first))
                group = state["group_id"]
                self.assertEqual([], _bar(strategy, state, "2026-10-29T04:00:00+00:00", open_groups=[group]))
                # The run's stop or the 15:15 square-off closed it: not again the same day.
                self.assertEqual([], _bar(strategy, state, "2026-10-29T09:50:00+00:00", open_groups=[]))
                again = _opens(_bar(strategy, state, "2026-10-30T03:50:00+00:00", open_groups=[]))
                self.assertEqual(1, len(again))
                self.assertNotEqual(group, state["group_id"])

    def test_a_group_still_open_on_the_next_session_is_kept(self):
        # Daily candles carry a position overnight: the group is still open, nothing new opens.
        for cls in STRATEGIES:
            with self.subTest(strategy=cls.name):
                strategy = cls({})
                state = strategy.initialize_state()
                _bar(strategy, state, "2026-10-29T10:00:00+00:00", open_groups=[])
                group = state["group_id"]
                self.assertEqual([], _bar(strategy, state, "2026-10-30T10:00:00+00:00", open_groups=[group]))
                self.assertEqual(group, state["group_id"])

    def test_live_says_nothing_about_groups_and_nothing_changes(self):
        for cls in STRATEGIES:
            with self.subTest(strategy=cls.name):
                strategy = cls({})
                state = strategy.initialize_state()
                self.assertEqual(1, len(_opens(_bar(strategy, state, "2026-10-29T03:50:00+00:00", mode="LivePaper"))))
                self.assertEqual([], _bar(strategy, state, "2026-10-30T03:50:00+00:00", mode="LivePaper"))


if __name__ == "__main__":
    unittest.main()
