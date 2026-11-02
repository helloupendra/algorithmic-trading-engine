"""
The console-written strategy: each condition against bars whose answer is obvious
by hand, and the strategy that turns a full set of them into one BUY or SELL.
"""

import json
import unittest
from datetime import datetime, time, timedelta, timezone

import _bootstrap  # noqa: F401

from backtest.engine import run_backtest
from backtest.timeutil import iso_utc, ist_day_end_utc, ist_day_start_utc
from strategies.base_strategy import BarFrame, StrategyInput
from strategies.builder import conditions as cond
from strategies.builder.signal_builder import SignalBuilderStrategy
from strategies.registry import describe_strategy
from test_engine import CE, DAY1, PE, SPOT, UNDERLYING, FakeApi, index_rows, option_rows, quiet

SESSION_OPEN_UTC = datetime(2026, 9, 8, 3, 45, tzinfo=timezone.utc)   # 09:15 IST


def bar(i, o, h, l, c, v=1000.0):
    return BarFrame(symbol="NSE:NIFTY50-INDEX", resolution="5m",
                    timestamp_utc=(SESSION_OPEN_UTC + timedelta(minutes=5 * i)).isoformat().replace("+00:00", "Z"),
                    open=o, high=h, low=l, close=c, volume=v)


def rising(n=40, start=100.0, step=1.0, volume=1000.0):
    return [bar(i, start + i * step - 0.4, start + i * step + 0.5, start + i * step - 0.6,
                start + i * step, volume) for i in range(n)]


def falling(n=40, start=140.0, step=1.0):
    return [bar(i, start - i * step + 0.4, start - i * step + 0.6, start - i * step - 0.5,
                start - i * step) for i in range(n)]


class ConditionTests(unittest.TestCase):
    def test_price_against_a_moving_average_and_an_ema_pair(self):
        up = rising()
        self.assertTrue(cond.evaluate('close>ema:9', up))
        self.assertFalse(cond.evaluate('close<ema:9', up))
        self.assertTrue(cond.evaluate('ema:9>ema:21', up))
        self.assertTrue(cond.evaluate('ema:9<ema:21', falling()))

    def test_vwap_needs_volume_so_an_index_without_it_never_qualifies(self):
        self.assertTrue(cond.evaluate('close>vwap', rising(volume=5000.0)))
        self.assertFalse(cond.evaluate('close>vwap', rising(volume=0.0)))   # no volume: not true by default

    def test_rsi_levels_and_the_cross_on_this_bar(self):
        bars = falling(30) + [bar(30, 110, 131, 109, 130), bar(31, 130, 151, 129, 150)]
        self.assertTrue(cond.evaluate('rsi_cross_up:60', bars))
        self.assertFalse(cond.evaluate('rsi_cross_up:60', bars + [bar(32, 150, 152, 149, 151)]))
        self.assertTrue(cond.evaluate('rsi>60', bars))
        self.assertTrue(cond.evaluate('rsi<40', falling(40)))

    def test_candle_shape_and_direction(self):
        strong = rising(5) + [bar(5, 100, 111, 99, 110)]     # body 10 of a 12-point range
        weak = rising(5) + [bar(5, 100, 160, 40, 101)]
        self.assertTrue(cond.evaluate('body>=0.5', strong))
        self.assertFalse(cond.evaluate('body>=0.5', weak))
        self.assertTrue(cond.evaluate('green', strong))
        self.assertTrue(cond.evaluate('red', falling(5)))
        self.assertTrue(cond.evaluate('above_open', rising(10)))
        self.assertTrue(cond.evaluate('below_open', falling(10)))

    def test_supertrend_the_opening_range_and_adx(self):
        self.assertTrue(cond.evaluate('supertrend:bullish', rising()))
        self.assertTrue(cond.evaluate('supertrend:bearish', falling()))
        self.assertTrue(cond.evaluate('break_high:15', rising(30)))
        self.assertTrue(cond.evaluate('break_low:15', falling(30)))
        self.assertTrue(cond.evaluate('adx>20', falling(60)))

    def test_an_unreadable_condition_is_refused_when_the_run_starts(self):
        with self.assertRaises(cond.ConditionError):
            cond.evaluate('close is high', rising())
        with self.assertRaises(cond.ConditionError):
            cond.compile_all(['close>'])

    def test_a_misspelt_name_is_refused_rather_than_always_false(self):
        for typo in ['closee>vwap', 'macd>0', 'close>emaa:9', 'close>vwapp', 'gapp>0.5', 'rsi_cross_upp:60',
                     'rsi_cross_sideways:60', 'break_highh:15', 'gren']:
            with self.subTest(typo), self.assertRaises(cond.ConditionError):
                cond.compile_all([typo])

    def test_a_quantity_with_the_wrong_argument_is_refused(self):
        for wrong in ['ema>close', 'close>sma', 'close:9>open', 'ema:0>close', 'ema:-9>close', 'ema:9.5>close', 'vwap:5<close']:
            with self.subTest(wrong), self.assertRaises(cond.ConditionError):
                cond.compile_all([wrong])

    def test_every_documented_condition_is_still_read(self):
        cond.compile_all(['close>vwap', 'close<ema:20', 'close>ema:9', 'ema:9>ema:21', 'rsi>60', 'rsi<40', 'rsi:7>50',
                          'rsi_cross_up:60', 'rsi_cross_down:40', 'body>=0.5', 'green', 'red', 'supertrend:bullish',
                          'supertrend:bearish', 'adx>20', 'adx:10>20', 'break_high:15', 'break_low:15', 'above_open',
                          'below_open', 'gap>0.5', 'gap<0.5', 'volume_z>1.5', 'atr_percent>0.1', 'sma:20<close',
                          'move_from_open>0.2', 'close>session_open', 'high>low', 'open<close'])

    def test_a_typo_stops_the_run_being_built(self):
        with self.assertRaises(cond.ConditionError):
            SignalBuilderStrategy({"long_conditions": ["closee>vwap"]})

    def test_conditions_are_read_from_a_list_or_one_line(self):
        self.assertEqual(cond.parse_all("close>vwap, body>=0.5"), ['close>vwap', 'body>=0.5'])
        self.assertEqual(cond.parse_all(['Close>VWAP']), ['close>vwap'])
        self.assertEqual(cond.parse_all(None), [])


def make_input(bars, mode="OfflineReplay", source="backtest"):
    return StrategyInput(mode=mode, timestamp_utc=bars[-1].timestamp_utc, underlying="NIFTY",
                         spot_price=float(bars[-1].close), atm_strike=23500, strike_step=50, lot_size=65,
                         bars={"5m": {"index": bars}}, metadata={"source": source})


def fifteen_minute_backtest(api, **params):
    merged = {"lots": 1, "underlying": UNDERLYING, "resolution": "15m", "eod_square_off_ist": "15:15",
              "charges_per_lot": 0, "long_conditions": ["green"], "short_conditions": ["red"]}
    merged.update(params)
    row = {"id": 42, "mode": "OfflineReplay", "symbol": SPOT, "resolution": "15", "strategyName": "SignalBuilder",
           "fromUtc": iso_utc(ist_day_start_utc(DAY1)), "toUtc": iso_utc(ist_day_end_utc(DAY1)),
           "parametersJson": json.dumps(merged), "initialCapital": 1_000_000, "userId": 1}
    return run_backtest(api, row, SignalBuilderStrategy, log=quiet, progress_interval=0.0)


class ResolutionTests(unittest.TestCase):
    def test_the_data_it_asks_for_is_the_runs_own_resolution(self):
        for resolution in ("15m", "1h", "1D"):
            wanted = SignalBuilderStrategy({"resolution": resolution}).get_data_requirements()
            self.assertEqual([("index", resolution)], [(r.symbol_type, r.resolution) for r in wanted])

    def test_with_no_resolution_named_it_is_the_default(self):
        self.assertEqual("5m", SignalBuilderStrategy({}).get_data_requirements()[0].resolution)

    def test_the_catalogue_demands_no_fixed_resolution_and_shows_no_error(self):
        entry = describe_strategy("SignalBuilder", SignalBuilderStrategy)
        self.assertEqual([], entry["dataRequirements"])
        self.assertNotIn("failed", entry["description"])

    def test_a_fifteen_minute_backtest_needs_no_five_minute_candles(self):
        # Only 15-minute candles are stored. Asked for 5m it would fail for want of them.
        def fifteen(rows):
            return [{**row, "resolution": "15"} for row in rows if int(row["timestampUtc"][14:16]) % 15 == 0]
        api = FakeApi({
            (SPOT, "15"): fifteen(index_rows([DAY1])),
            (CE, "15"): fifteen(option_rows(CE, [DAY1], lambda i: 100.0 + i)),
            (PE, "15"): fifteen(option_rows(PE, [DAY1], lambda i: 80.0 - i * 0.5)),
        })

        outcome = fifteen_minute_backtest(api)

        self.assertEqual("Completed", outcome.status, outcome.error)
        self.assertTrue([s for s in api.signals if s["signalType"] == "OPEN_GROUP"], "the setup was judged and traded")


class LiveTests(unittest.TestCase):
    def test_the_catalogue_says_it_is_for_backtests_only(self):
        self.assertTrue(describe_strategy("SignalBuilder", SignalBuilderStrategy)["backtestOnly"])

    def test_other_strategies_are_not_marked(self):
        from strategies.bullish.crude_momentum_strategy import CrudeMomentumStrategy
        self.assertFalse(describe_strategy("CrudeMomentum", CrudeMomentumStrategy)["backtestOnly"])


class StrategyTests(unittest.TestCase):
    def strategy(self, **params):
        return SignalBuilderStrategy({"lots": 2, **params})

    def test_a_full_long_setup_emits_one_buy(self):
        s = self.strategy(long_conditions=["close>ema:9", "green"], short_conditions=["close<ema:9", "red"])
        state = s.initialize_state()
        signals = s.on_bar(state, make_input(rising()))
        self.assertEqual(len(signals), 1)
        self.assertEqual(signals[0].signal_type, "BUY")
        self.assertIn("close>ema:9 and green", signals[0].reason)
        # The same bar again is not a second signal.
        self.assertEqual([], s.on_bar(state, make_input(rising())))

    def test_the_mirror_set_emits_a_sell(self):
        s = self.strategy(long_conditions=["close>ema:9"], short_conditions=["close<ema:9"])
        signals = s.on_bar(s.initialize_state(), make_input(falling()))
        self.assertEqual(signals[0].signal_type, "SELL")

    def test_a_side_with_no_conditions_never_fires(self):
        s = self.strategy(long_conditions=[], short_conditions=["close<ema:9"])
        self.assertEqual([], s.on_bar(s.initialize_state(), make_input(rising())))

    def test_a_bar_that_argues_both_ways_is_left_alone(self):
        s = self.strategy(long_conditions=["green"], short_conditions=["green"])
        self.assertEqual([], s.on_bar(s.initialize_state(), make_input(rising())))

    def test_warm_up_bars_are_ignored_and_live_judges_closed_bars_only(self):
        s = self.strategy(long_conditions=["close>ema:9"], short_conditions=[])
        self.assertEqual([], s.on_bar(s.initialize_state(), make_input(rising(), source="warmup")))
        live = s.on_bar(s.initialize_state(), make_input(rising(), mode="LivePaper", source="live-api"))
        self.assertEqual(len(live), 1)
        # The forming bar was dropped, so the signal names the one before it.
        self.assertEqual(live[0].metadata["signal_bar_utc"], rising()[-2].timestamp_utc)

    def test_an_equity_run_declares_no_contracts(self):
        self.assertEqual([], SignalBuilderStrategy.get_contract_requirements({"instrument_kind": "equity"}))
        self.assertTrue(SignalBuilderStrategy.get_contract_requirements({}))


if __name__ == "__main__":
    unittest.main()
