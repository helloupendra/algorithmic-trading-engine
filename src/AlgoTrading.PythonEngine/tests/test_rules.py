"""
Run rules: how often a run may trade, when a day is over, where a moving stop
sits, which contract a bare BUY/SELL takes, and what the run's blocks read like.
"""

import unittest
from datetime import date, datetime, timedelta, timezone

from backtest.rules import (
    ContractRules,
    ExitManager,
    Exits,
    Limits,
    RunRules,
    TradeLimiter,
    parse_rules,
    parse_windows,
    window_index,
)

IST = timezone(timedelta(hours=5, minutes=30))


def at(hhmm: str, day: int = 8) -> datetime:
    hour, minute = (int(x) for x in hhmm.split(":"))
    return datetime(2026, 9, day, hour, minute, tzinfo=IST)


class WindowTests(unittest.TestCase):
    def test_one_or_many_windows_are_read_and_matched(self):
        self.assertEqual(parse_windows(["09:20", "15:05"]), [("09:20", "15:05")])
        windows = parse_windows([["09:20", "11:00"], ["13:00", "15:15"]])
        self.assertEqual(len(windows), 2)
        self.assertEqual(window_index(windows, at("09:20")), 0)
        self.assertEqual(window_index(windows, at("11:00")), 0)   # inclusive
        self.assertIsNone(window_index(windows, at("11:01")))
        self.assertEqual(window_index(windows, at("15:15")), 1)
        self.assertEqual(parse_windows([["9", "bad"]]), [])


class LimitTests(unittest.TestCase):
    def limiter(self, **kwargs) -> TradeLimiter:
        limiter = TradeLimiter(Limits(**kwargs), parse_windows([["09:20", "11:00"], ["13:00", "15:15"]]))
        limiter.start_day(date(2026, 9, 8))
        return limiter

    def test_one_trade_a_day_and_one_per_window(self):
        day = self.limiter(max_trades_per_day=2, max_trades_per_window=1)
        self.assertIsNone(day.allow(at("09:30"), "bullish", 0))
        day.opened(at("09:30"), "bullish")
        blocked = day.allow(at("10:30"), "bullish", 0)
        self.assertEqual(blocked[0], "max_trades_per_window")
        self.assertIsNone(day.allow(at("13:30"), "bullish", 0))       # the second window is free
        day.opened(at("13:30"), "bullish")
        self.assertEqual(day.allow(at("14:00"), "bullish", 0)[0], "max_trades_per_day")

    def test_one_position_at_a_time(self):
        day = self.limiter(max_open_groups=1)
        self.assertIsNone(day.allow(at("09:30"), None, 0))
        self.assertEqual(day.allow(at("09:35"), None, 1)[0], "max_open_groups")

    def test_cooldown_and_a_direction_that_already_lost(self):
        day = self.limiter(cooldown_after_loss_minutes=30, block_direction_after_loss=True)
        day.opened(at("09:30"), "bullish")
        day.closed(at("09:50"), "bullish", realized=-1200.0)
        self.assertEqual(day.allow(at("10:10"), "bearish", 0)[0], "cooldown_after_loss")
        self.assertIsNone(day.allow(at("10:25"), "bearish", 0))       # cooldown over
        self.assertEqual(day.allow(at("10:25"), "bullish", 0)[0], "block_direction_after_loss")
        self.assertIsNone(day.allow(at("10:25"), None, 0))            # no view: the rule stands aside

    def test_the_day_ends_after_losses_or_a_rupee_loss(self):
        day = self.limiter(stop_after_losses=2)
        self.assertIsNone(day.closed(at("09:50"), "bullish", -500.0))
        self.assertIn("2 losing trade", day.closed(at("10:50"), "bearish", -700.0))
        self.assertEqual(day.allow(at("11:00"), "bullish", 0)[0], "day_closed")

        money = self.limiter(stop_after_day_loss=3000)
        self.assertIsNone(money.closed(at("09:50"), "bullish", -1000.0))
        self.assertIn("−₹3,000", money.closed(at("10:50"), "bullish", -2100.0))
        money.start_day(date(2026, 9, 9))
        self.assertIsNone(money.allow(at("09:30", day=9), "bullish", 0))   # tomorrow starts clean


class ExitTests(unittest.TestCase):
    def test_a_position_is_closed_after_its_time_is_up(self):
        manager = ExitManager(Exits(time_exit_minutes=30))
        key = ("G1", "NSE:NIFTY2691523500CE")
        manager.opened(key, at("09:30"))
        self.assertIsNone(manager.check(key, at("09:55"), 5.0, 10.0, "bullish"))
        rule, reason = manager.check(key, at("10:00"), 5.0, 10.0, "bullish")
        self.assertEqual(rule, "time_exit")
        self.assertIn("30m", reason)

    def test_the_stop_moves_to_entry_and_then_climbs_with_the_profit(self):
        manager = ExitManager(Exits(breakeven_after_percent=15, step_trail_step_percent=5))
        key = ("G1", "CE")
        manager.opened(key, at("09:30"))
        self.assertIsNone(manager.check(key, at("09:35"), None, 10.0, "bullish"))    # not armed yet
        self.assertIsNone(manager.check(key, at("09:40"), None, 16.0, "bullish"))    # armed at +15%
        rule, reason = manager.check(key, at("09:45"), None, -2.0, "bullish")
        self.assertEqual(rule, "moving_stop")
        self.assertIn("entry", reason)

        stepped = ExitManager(Exits(breakeven_after_percent=15, step_trail_step_percent=5))
        stepped.opened(key, at("09:30"))
        stepped.check(key, at("09:40"), None, 27.0, "bullish")                       # peak +27% -> stop +10%
        self.assertIsNone(stepped.check(key, at("09:45"), None, 12.0, "bullish"))
        rule, reason = stepped.check(key, at("09:50"), None, 9.0, "bullish")
        self.assertEqual(rule, "moving_stop")
        self.assertIn("+10%", reason)

    def test_the_index_turning_against_the_position_closes_it_only_with_a_view(self):
        manager = ExitManager(Exits(exit_on_vwap_cross=True))
        key = ("G1", "CE")
        manager.opened(key, at("09:30"))
        self.assertIsNone(manager.check(key, at("09:35"), 1.0, 1.0, "bullish", index_view="bullish"))
        rule, _ = manager.check(key, at("09:40"), 1.0, 1.0, "bullish", index_view="bearish")
        self.assertEqual(rule, "index_turned")
        self.assertIsNone(manager.check(key, at("09:40"), 1.0, 1.0, None, index_view="bearish"))


class ContractTests(unittest.TestCase):
    def test_a_bare_signal_can_be_flipped_and_moved_in_or_out_of_the_money(self):
        plain = ContractRules()
        self.assertEqual(plain.side_for("BUY"), "CE")
        self.assertEqual(plain.strike_for(23500, "CE", 50), 23500)

        itm = ContractRules(strike_offset=1)
        self.assertEqual(itm.strike_for(23500, "CE", 50), 23450)     # a call is ITM below the spot
        self.assertEqual(itm.strike_for(23500, "PE", 50), 23550)

        otm = ContractRules(strike_offset=-2)
        self.assertEqual(otm.strike_for(23500, "CE", 50), 23600)

        flipped = ContractRules(flip=True)
        self.assertEqual(flipped.side_for("BUY"), "PE")
        self.assertEqual(flipped.side_for("SELL"), "CE")


class ParseTests(unittest.TestCase):
    def test_a_run_without_blocks_enforces_nothing(self):
        rules = parse_rules({"lots": 2})
        self.assertFalse(rules.is_set)
        self.assertEqual(rules.describe(), "none")

    def test_every_block_is_read_from_the_run_parameters(self):
        rules = parse_rules({
            "filters": {"windows_ist": [["09:20", "11:00"], ["13:00", "15:15"]]},
            "limits": {"max_trades_per_day": 1, "cooldown_after_loss_minutes": 30},
            "exits": {"time_exit_minutes": 45, "breakeven_after_percent": 15,
                      "step_trail_step_percent": 5, "exit_on_supertrend": [10, 3]},
            "contract": {"strike_offset": 1, "flip": True},
            "costs": {"slippage_percent": 0.25},
        })
        self.assertTrue(rules.is_set)
        self.assertEqual(len(rules.windows), 2)
        self.assertEqual(rules.limits.max_trades_per_day, 1)
        self.assertEqual(rules.exits.exit_on_supertrend, (10, 3.0))
        self.assertTrue(rules.exits.needs_bars)
        self.assertEqual(rules.contract.side_for("BUY"), "PE")
        self.assertEqual(rules.costs.slippage_pct, 0.25)
        self.assertEqual(rules.costs.brokerage_per_order, 20.0)      # untouched defaults stay
        self.assertIn("at most 1 trade(s) a day", rules.describe())


if __name__ == "__main__":
    unittest.main()
