"""TrendRider V2: the entry on yesterday's high/low from 09:15, the index stop and the index trailing exit."""

import unittest
from datetime import datetime, timedelta, timezone

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.directional.trend_rider_v2 import TrendRiderV2Strategy
from tests.test_trend_rider import FIRST_DAY, IST, flat_day, session

CONTRACTS = {"buy_ce": OptionContract("NSE:NIFTY_CE", "NIFTY", "2026-03-31", 19950, "CE"),
             "buy_pe": OptionContract("NSE:NIFTY_PE", "NIFTY", "2026-03-31", 20050, "PE")}


class TrendRiderV2Test(unittest.TestCase):
    def setUp(self):
        self.days = []
        d = FIRST_DAY
        while len(self.days) < 12:
            if d.weekday() < 5:
                self.days.append(d)
            d += timedelta(days=1)
        # Eleven sessions of 100 points, the last one 50 wide (19975-20025): yesterday was narrow.
        self.bars = []
        for i, day in enumerate(self.days[:11]):
            self.bars += flat_day(day, 20000, 50 if i == 10 else 100)
        self.adr = (9 * 100 + 50) / 10

    def inp(self, strategy, state, spot, stamp):
        groups = [state["position"]["group_id"]] if state["position"] else []
        return StrategyInput(mode="OfflineReplay", timestamp_utc=stamp, underlying="NIFTY", spot_price=spot,
                             contracts=CONTRACTS, bars={"5m": {"index": list(self.bars)}},
                             metadata={"open_groups": groups})

    def until_entry(self, strategy, state, step=10):
        """Feeds a day rising `step` points a candle from a 20000 open; stops at the first signal."""
        level = 20000
        for i in range(75):
            nxt = level + step
            frame = session(self.days[11], [(level, max(level, nxt) + 2, min(level, nxt) - 2, nxt)])[0]
            t = (self.days[11].replace(hour=9, minute=15) + timedelta(minutes=5 * i)).astimezone(timezone.utc)
            frame.timestamp_utc = t.isoformat()
            self.bars.append(frame)
            sig = strategy.on_bar(state, self.inp(strategy, state, frame.close, frame.timestamp_utc))
            level = nxt
            if sig:
                return datetime.fromisoformat(frame.timestamp_utc).astimezone(IST).strftime("%H:%M"), sig, frame
        self.fail("no entry")

    def test_enters_before_0945_on_yesterdays_high(self):
        strategy = TrendRiderV2Strategy()
        state = strategy.initialize_state()
        at, sig, frame = self.until_entry(strategy, state)
        self.assertLess(at, "09:45")
        self.assertEqual(sig[0].signal_type, "OPEN_GROUP")
        self.assertEqual(sig[0].legs[0]["symbol"], "NSE:NIFTY_CE")
        ctx = sig[0].metadata["trend_rider"]
        self.assertEqual(ctx["levels"], "prev_day")
        self.assertEqual(ctx["prev_high"], 20025)
        self.assertEqual(ctx["entry_spot"], frame.close)
        self.assertTrue(sig[0].metadata["group_id"].startswith("TR2_"))

    def test_index_stop_sells_half_a_day_range_against_the_entry(self):
        strategy = TrendRiderV2Strategy()
        state = strategy.initialize_state()
        _, _, frame = self.until_entry(strategy, state)
        entry = frame.close
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, entry - 0.5 * self.adr + 1, frame.timestamp_utc)), [])
        out = strategy.on_bar(state, self.inp(strategy, state, entry - 0.5 * self.adr, frame.timestamp_utc))
        self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
        self.assertIn("stop", out[0].reason)
        self.assertIsNone(state["position"])
        self.assertTrue(state["busy_until"])

    def test_trail_keeps_the_profit_once_armed(self):
        strategy = TrendRiderV2Strategy()
        state = strategy.initialize_state()
        _, _, frame = self.until_entry(strategy, state)
        entry, ts = frame.close, frame.timestamp_utc
        # Not armed yet: a 40-point give-back from +40 does nothing (arming needs +47.5).
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, entry + 40, ts)), [])
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, entry + 1, ts)), [])
        # Armed at +100; a give-back of 33 is held, 33.25 (0.35 x 95) sells.
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, entry + 100, ts)), [])
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, entry + 67, ts)), [])
        out = strategy.on_bar(state, self.inp(strategy, state, entry + 100 - 0.35 * self.adr, ts))
        self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
        self.assertIn("trail", out[0].reason)

    def test_zero_turns_the_exits_off(self):
        strategy = TrendRiderV2Strategy({"index_stop_adr": 0, "trail_arm_adr": 0})
        state = strategy.initialize_state()
        _, _, frame = self.until_entry(strategy, state)
        self.assertEqual(strategy.on_bar(state, self.inp(strategy, state, frame.close - 500, frame.timestamp_utc)), [])
        self.assertIsNotNone(state["position"])

    def test_a_position_the_run_closed_is_not_closed_again(self):
        strategy = TrendRiderV2Strategy()
        state = strategy.initialize_state()
        _, _, frame = self.until_entry(strategy, state)
        inp = StrategyInput(mode="OfflineReplay", timestamp_utc=frame.timestamp_utc, underlying="NIFTY",
                            spot_price=frame.close - 500, contracts=CONTRACTS, bars={"5m": {"index": list(self.bars)}},
                            metadata={"open_groups": []})
        out = strategy.on_bar(state, inp)
        self.assertFalse([s for s in out if s.signal_type == "CLOSE_GROUP"])

    def test_opening_range_mode_keeps_trendriders_entry_and_adds_the_exits(self):
        strategy = TrendRiderV2Strategy({"levels": "opening_range", "first_entry": "09:45"})
        state = strategy.initialize_state()
        at, sig, frame = self.until_entry(strategy, state)
        self.assertGreaterEqual(at, "09:45")
        self.assertEqual(sig[0].metadata["trend_rider"]["entry_spot"], frame.close)
        self.assertEqual(state["position"]["adr"], sig[0].metadata["trend_rider"]["adr"])

    def wide_history(self):
        """Eleven sessions of 100 points each: yesterday was as wide as any day, so filter D would refuse."""
        self.bars = []
        for day in self.days[:11]:
            self.bars += flat_day(day, 20000, 100)

    def test_trades_after_a_wide_day_too(self):
        # Filter D (max_prev_range) is off in V2: a wide yesterday does not keep it out of a trend day.
        self.wide_history()
        strategy = TrendRiderV2Strategy()
        state = strategy.initialize_state()
        _, sig, _ = self.until_entry(strategy, state)
        self.assertEqual(sig[0].signal_type, "OPEN_GROUP")
        self.assertNotIn("yesterday_not_narrow", state["skipped"])

    def test_filter_d_can_still_be_switched_on(self):
        self.wide_history()
        strategy = TrendRiderV2Strategy({"max_prev_range": 0.7})
        state = strategy.initialize_state()
        with self.assertRaises(AssertionError):           # until_entry fails: no entry all day
            self.until_entry(strategy, state)
        self.assertIn("yesterday_not_narrow", state["skipped"])

    def test_bad_parameters_are_refused(self):
        for bad in ({"levels": "weekly"}, {"index_stop_adr": -1}, {"trail_arm_adr": 0.5, "trail_giveback_adr": 0},
                    {"index_stop_adr": "x"}):
            with self.assertRaises(ValueError):
                TrendRiderV2Strategy(bad)


if __name__ == "__main__":
    unittest.main()
