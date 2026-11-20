"""TrendRiderV2OB: the order-block stop, half the lots after a wide day, and V2 BE's rules left as they were."""

import unittest
from datetime import timedelta, timezone

import _bootstrap  # noqa: F401
from strategies.base_strategy import BarFrame, StrategyInput
from strategies.directional.trend_rider_v2_be import TrendRiderV2BEStrategy
from strategies.directional.trend_rider_v2_ob import TrendRiderV2OBStrategy
from tests.test_trend_rider import flat_day
from tests.test_trend_rider_v2_be import CONTRACTS, weekdays


class V2OBTest(unittest.TestCase):

    def build(self, yesterday_range=50):
        """Ten sessions of 100 points, then yesterday's (19975-20025 by default, as in the V2 BE tests)."""
        self.days = weekdays(12)
        self.bars = []
        for i, day in enumerate(self.days[:11]):
            self.bars += flat_day(day, 20000, yesterday_range if i == 10 else 100)
        self.adr = (9 * 100 + yesterday_range) / 10
        self.prev_high = 20000 + yesterday_range / 2
        self.n = 0

    def setUp(self):
        self.build()

    def inp(self, state, spot, stamp):
        groups = [state["position"]["group_id"]] if state["position"] else []
        return StrategyInput(mode="OfflineReplay", timestamp_utc=stamp, underlying="NIFTY", spot_price=spot,
                             contracts=CONTRACTS, bars={"5m": {"index": list(self.bars)}}, metadata={"open_groups": groups})

    def candle(self, strategy, state, o, h, l, c):
        t = (self.days[11].replace(hour=9, minute=15) + timedelta(minutes=5 * self.n)).astimezone(timezone.utc)
        self.n += 1
        frame = BarFrame("NSE:NIFTY50-INDEX", "5m", t.isoformat(), o, h, l, c)
        self.bars.append(frame)
        return strategy.on_bar(state, self.inp(state, c, frame.timestamp_utc)), frame

    def rise_to_entry(self, strategy, state, level=20000):
        """Up-candles of 10 points from `level` until the strategy buys."""
        for _ in range(60):
            sig, frame = self.candle(strategy, state, level, level + 12, level - 2, level + 10)
            level += 10
            if sig:
                return sig, frame
        self.fail("no entry")

    # ------------------------------------------------------------ the stop --
    def test_the_stop_sits_at_the_last_down_candles_low(self):
        strategy = TrendRiderV2OBStrategy({"lots": 2})
        state = strategy.initialize_state()
        self.candle(strategy, state, 20000, 20003, 19985, 19990)            # a down candle: its low is the block
        sig, frame = self.rise_to_entry(strategy, state, 19990)
        entry = frame.close
        self.assertGreater(entry, self.prev_high)
        ctx = sig[0].metadata["trend_rider"]
        self.assertEqual(ctx["ob_stop"], 19985)
        self.assertEqual(ctx["index_exits"]["stop_adr"], round((entry - 19985) / self.adr, 4))
        self.assertTrue(sig[0].reason.startswith("Trend Rider V2 OB: "))
        ts = frame.timestamp_utc
        self.assertEqual(strategy.on_bar(state, self.inp(state, 19985.5, ts)), [])
        out = strategy.on_bar(state, self.inp(state, 19985, ts))
        self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
        self.assertTrue(out[0].reason.startswith("TrendRiderV2OB index stop (order block)"))
        self.assertEqual(out[0].legs, [{"symbol": "NSE:NIFTY_CE", "side": "SELL", "quantity": 2}])
        self.assertIsNone(state["position"])

    def test_the_stop_is_clamped_to_015_and_05_of_the_range(self):
        for low, want in ((19900, 0.5), (20018, 0.15)):
            with self.subTest(low=low):
                self.build()
                strategy = TrendRiderV2OBStrategy()
                state = strategy.initialize_state()
                if low < 20000:                       # far below: the stop stays 0.5 x ADR away
                    self.candle(strategy, state, 20000, 20003, low, 19990)
                    _, frame = self.rise_to_entry(strategy, state, 19990)
                else:                                 # just under the entry: pushed out to 0.15 x ADR
                    self.candle(strategy, state, 20000, 20012, 19998, 20010)
                    self.candle(strategy, state, 20010, 20022, 20008, 20020)
                    self.candle(strategy, state, 20020, 20024, low, 20019)
                    sig, frame = self.candle(strategy, state, 20019, 20035, 20017, 20030)
                    self.assertTrue(sig)
                level = state["position"]["ob_stop"]
                self.assertAlmostEqual(frame.close - level, want * self.adr)

    def test_with_ob_stop_off_it_is_v2_be(self):
        strategy, be = TrendRiderV2OBStrategy({"ob_stop": False}), TrendRiderV2BEStrategy()
        self.assertEqual(strategy.index_exits.describe(), be.index_exits.describe())
        state = strategy.initialize_state()
        self.candle(strategy, state, 20000, 20003, 19985, 19990)
        _, frame = self.rise_to_entry(strategy, state, 19990)
        self.assertNotIn("ob_stop", state["position"])
        self.assertEqual(strategy.on_bar(state, self.inp(state, 19985, frame.timestamp_utc)), [])
        out = strategy.on_bar(state, self.inp(state, frame.close - 0.5 * self.adr, frame.timestamp_utc))
        self.assertIn("index stop", out[0].reason)
        self.assertNotIn("order block", out[0].reason)

    def test_breakeven_and_trail_are_v2_bes(self):
        strategy = TrendRiderV2OBStrategy()
        state = strategy.initialize_state()
        _, frame = self.rise_to_entry(strategy, state)
        entry, ts = frame.close, frame.timestamp_utc
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry + 0.25 * self.adr, ts)), [])
        out = strategy.on_bar(state, self.inp(state, entry, ts))
        self.assertIn("breakeven stop", out[0].reason)
        self.build()
        strategy = TrendRiderV2OBStrategy()
        state = strategy.initialize_state()
        _, frame = self.rise_to_entry(strategy, state)
        entry, ts = frame.close, frame.timestamp_utc
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry + 100, ts)), [])
        out = strategy.on_bar(state, self.inp(state, entry + 100 - 0.35 * self.adr, ts))
        self.assertIn("index trail", out[0].reason)

    # ------------------------------------------------------------ the size --
    def test_half_the_lots_after_a_wide_day_only_when_asked(self):
        # Yesterday 110 points: ADR 101, so yesterday was 1.09 x ADR (at least 1.0, under V2's 1.3 skip).
        # Off by default (the owner trades a fixed 2 lots); wide_prev_adr 1.0 is the setting that was tested.
        on = {"wide_prev_adr": 1.0}
        for params, bought in (({"lots": 2}, 2), ({"lots": 4}, 4),
                               ({"lots": 2, **on}, 1), ({"lots": 4, **on}, 2), ({"lots": 1, **on}, 1),
                               ({"lots": 3, **on}, 1), ({"lots": 2, "wide_prev_adr": 1.2}, 2)):
            with self.subTest(params=params):
                self.build(yesterday_range=110)
                strategy = TrendRiderV2OBStrategy(params)
                state = strategy.initialize_state()
                sig, frame = self.rise_to_entry(strategy, state)
                ctx = sig[0].metadata["trend_rider"]
                self.assertEqual(ctx["prev_range_adr"], round(110 / 101, 3))
                self.assertEqual(sig[0].legs[0]["quantity"], bought)
                self.assertEqual(ctx["lots"], bought)
                self.assertEqual("size_rule" in ctx, bought < params["lots"])
                out = strategy.on_bar(state, self.inp(state, frame.close - 0.5 * self.adr, frame.timestamp_utc))
                self.assertEqual(out[0].legs[0]["quantity"], bought)       # sells what it bought

    def test_full_lots_after_a_normal_day(self):
        strategy = TrendRiderV2OBStrategy({"lots": 2})
        state = strategy.initialize_state()
        sig, _ = self.rise_to_entry(strategy, state)
        self.assertEqual(sig[0].metadata["trend_rider"]["prev_range_adr"], round(50 / 95, 3))
        self.assertEqual(sig[0].legs[0]["quantity"], 2)

    # ------------------------------------------------------------ settings --
    def test_defaults_are_v2_bes_plus_four(self):
        be, ob = TrendRiderV2BEStrategy.default_params, TrendRiderV2OBStrategy.default_params
        added = {"ob_stop": True, "ob_stop_min_adr": 0.15, "wide_prev_adr": 0.0, "wide_prev_size": 0.5}
        self.assertEqual({k: v for k, v in ob.items() if k not in added}, be)
        self.assertEqual({k: ob[k] for k in added}, added)
        self.assertEqual(TrendRiderV2OBStrategy().index_exits.describe(), TrendRiderV2BEStrategy().index_exits.describe())

    def test_bad_parameters_are_refused(self):
        for bad in ({"ob_stop_min_adr": 0.6}, {"ob_stop_min_adr": -0.1}, {"ob_stop_min_adr": "x"},
                    {"wide_prev_adr": -1}, {"wide_prev_size": 0}, {"wide_prev_size": 1.5}, {"index_stop_adr": 0}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                TrendRiderV2OBStrategy(bad)


if __name__ == "__main__":
    unittest.main()
