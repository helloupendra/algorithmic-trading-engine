"""
SmcStructureBreak: what it trades, when it stays out, and what it does when
something else closes its position.

The candles are the schematic from tests/test_market_structure.py, so the
structure behind every signal here is the one that test pins.
"""

import unittest
from typing import Any, Dict, List

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.directional.smc_structure_break import SmcStructureBreakStrategy
from test_market_structure import bars, schematic

UNDERLYING = "NIFTY"
CE = OptionContract(symbol="NSE:NIFTY26SEP25000CE", underlying=UNDERLYING, expiry_date="2026-09-29",
                    strike_price=25000, option_type="CE")
PE = OptionContract(symbol="NSE:NIFTY26SEP25000PE", underlying=UNDERLYING, expiry_date="2026-09-29",
                    strike_price=25000, option_type="PE")


class Frame:
    """A BarFrame as the engine hands one to a strategy."""

    def __init__(self, bar):
        self.timestamp_utc = bar.time_utc
        self.open, self.high, self.low, self.close = bar.open, bar.high, bar.low, bar.close


def run(strategy: SmcStructureBreakStrategy, bars=None, mode="OfflineReplay",
        open_groups_from=None) -> List[Dict[str, Any]]:
    """
    Steps the strategy through the candles one at a time, as the engine does,
    and returns every signal with the index of the candle it fired on.
    """
    frames = [Frame(b) for b in (bars or schematic())]
    state = strategy.initialize_state()
    fired: List[Dict[str, Any]] = []
    open_groups: List[str] = []
    for i in range(1, len(frames) + 1):
        visible = frames[:i]
        metadata: Dict[str, Any] = {"source": "backtest", "resolution": "5m"}
        if open_groups_from is not None:
            # The replay tells a strategy which groups are still open; this test
            # can empty that list to mean "something else closed your position".
            metadata["open_groups"] = [] if i >= open_groups_from else list(open_groups)
        inp = StrategyInput(mode=mode, timestamp_utc=visible[-1].timestamp_utc, underlying=UNDERLYING,
                            spot_price=visible[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                            contracts={"atm_ce": CE, "atm_pe": PE},
                            bars={"5m": {"index": visible}}, metadata=metadata)
        for signal in strategy.on_bar(state, inp) or []:
            group = (signal.metadata or {}).get("group_id")
            if signal.signal_type == "OPEN_GROUP":
                open_groups.append(group)
            elif group in open_groups:
                open_groups.remove(group)
            fired.append({"bar": i - 1, "type": signal.signal_type, "legs": signal.legs,
                          "reason": signal.reason, "group": group})
    return fired


class EntryTests(unittest.TestCase):
    def test_a_break_of_structure_buys_the_call(self):
        fired = run(SmcStructureBreakStrategy())

        first = fired[0]
        self.assertEqual(first["type"], "OPEN_GROUP")
        self.assertEqual(first["bar"], 9)                 # the candle that closed through 109
        self.assertEqual(first["legs"][0]["symbol"], CE.symbol)
        self.assertEqual(first["legs"][0]["side"], "BUY")
        self.assertIn("109", first["reason"])

    def test_one_position_at_a_time(self):
        fired = run(SmcStructureBreakStrategy())

        # The second bullish break (bar 19) comes while the first position is
        # still open, so it is not doubled up on.
        self.assertEqual([f["bar"] for f in fired if f["type"] == "OPEN_GROUP"], [9])

    def test_a_change_of_character_against_the_position_closes_it(self):
        fired = run(SmcStructureBreakStrategy())

        closes = [f for f in fired if f["type"] == "CLOSE_GROUP"]
        self.assertEqual([f["bar"] for f in closes], [23])
        self.assertEqual(closes[0]["legs"][0]["side"], "SELL")
        self.assertEqual(closes[0]["group"], fired[0]["group"])
        self.assertIn("protected", closes[0]["reason"])

    def test_reversals_can_be_traded_instead_of_continuations(self):
        fired = run(SmcStructureBreakStrategy({"trade": "choch"}))

        # The schematic's only change of character is bearish, at bar 23.
        opens = [f for f in fired if f["type"] == "OPEN_GROUP"]
        self.assertEqual([f["bar"] for f in opens], [23])
        self.assertEqual(opens[0]["legs"][0]["symbol"], PE.symbol)

    def test_a_retest_entry_waits_for_price_to_come_back_to_the_level(self):
        fired = run(SmcStructureBreakStrategy({"entry": "retest", "retest_bars": 8}))

        opens = [f for f in fired if f["type"] == "OPEN_GROUP"]
        # Bar 9 closed through 109; bar 10 traded back down to it, and that is
        # where the position is taken rather than at the break's close.
        self.assertEqual([f["bar"] for f in opens], [10])
        self.assertIn("retest", opens[0]["reason"])

    def test_a_retest_that_never_comes_back_is_dropped(self):
        # The same break at bar 9, and then a market that runs away from the
        # level: the entry is given up rather than chased.
        candles = schematic()[:10] + bars(
            (111, 114, 111, 113), (113, 116, 112, 115), (115, 118, 114, 117), (117, 120, 116, 119))

        fired = run(SmcStructureBreakStrategy({"entry": "retest", "retest_bars": 2}), bars=candles)

        self.assertEqual([f for f in fired if f["type"] == "OPEN_GROUP"], [])


class PositionTests(unittest.TestCase):
    def test_a_position_closed_by_the_run_is_not_remembered(self):
        # The replay says every group is closed from bar 12 on: the strategy has
        # to be free to take the next break rather than think it is still in.
        fired = run(SmcStructureBreakStrategy(), open_groups_from=12)

        self.assertEqual([f["bar"] for f in fired if f["type"] == "OPEN_GROUP"], [9, 19])

    def test_live_holds_back_the_forming_candle(self):
        # Live is handed the candle that is still forming, so every mark lands
        # one candle later than in the replay.
        fired = run(SmcStructureBreakStrategy(), mode="LivePaper")

        self.assertEqual([f["bar"] for f in fired if f["type"] == "OPEN_GROUP"], [10])


class ShapeTests(unittest.TestCase):
    def test_it_asks_for_the_option_it_trades(self):
        keys = {r.key: r for r in SmcStructureBreakStrategy.get_contract_requirements({})}

        self.assertEqual(set(keys), {"atm_ce", "atm_pe"})
        self.assertEqual(keys["atm_ce"].moneyness, "atm")
        self.assertEqual(
            {r.key: r.steps for r in SmcStructureBreakStrategy.get_contract_requirements({"strike_steps": 2})},
            {"atm_ce": 2.0, "atm_pe": 2.0})

    def test_a_longer_run_is_read_on_its_own_candles(self):
        strategy = SmcStructureBreakStrategy()
        inp = StrategyInput(mode="OfflineReplay", timestamp_utc="2026-09-18T03:45:00Z", underlying=UNDERLYING,
                            spot_price=25000, bars={}, metadata={"resolution": "15m"})

        self.assertEqual(strategy.chart(inp), "15m")
        self.assertEqual(strategy.chart(StrategyInput(mode="LivePaper", timestamp_utc="x", underlying=UNDERLYING,
                                                      spot_price=1, bars={}, metadata={})), "5m")


if __name__ == "__main__":
    unittest.main()
