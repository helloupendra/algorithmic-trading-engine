"""Index exits (strategies/index_exits.py) and TrendRider's timeframe and index_exits parameters."""

import unittest
from datetime import datetime, timedelta, timezone

from strategies.base_strategy import BarFrame, OptionContract, StrategyInput
from strategies.directional.trend_rider import TrendRiderStrategy
from strategies.index_exits import IndexExitRules
from tests.test_trend_rider import FIRST_DAY, IST, flat_day, session

T0 = datetime(2026, 3, 16, 9, 15, tzinfo=IST)


def bars_from(closes, step_minutes=5, spread=2.0):
    out, prev = [], closes[0]
    for i, c in enumerate(closes):
        t = (T0 + timedelta(minutes=step_minutes * i)).astimezone(timezone.utc).isoformat()
        out.append(BarFrame("NSE:NIFTY50-INDEX", f"{step_minutes}m", t, prev, max(prev, c) + spread, min(prev, c) - spread, c))
        prev = c
    return out


class ParseTest(unittest.TestCase):
    def test_nothing_set_is_none(self):
        # "null" is what the console's parameter grid sends back for the catalogue's null default.
        for raw in (None, "", {}, {"stop_adr": 0}, "null", " NULL ", "None", "{}", False, "false"):
            self.assertIsNone(IndexExitRules.parse(raw), raw)

    def test_the_catalogue_defaults_round_trip_as_the_console_sends_them(self):
        """The console sends the catalogue's defaults back: objects as JSON text, null as the text "null"."""
        import json
        from strategies.directional.trend_rider_v2 import TrendRiderV2Strategy
        stop_and_trail = {"stop_adr": 0.5, "trail_arm_adr": 0.5, "trail_giveback_adr": 0.35}
        for cls in (TrendRiderStrategy, TrendRiderV2Strategy):
            defaults = json.loads(json.dumps(cls.default_params))
            as_text = {k: ("null" if v is None else json.dumps(v) if isinstance(v, dict) else v) for k, v in defaults.items()}
            # V2 also sells at 15:15 (square_off_ist "auto" on NSE), as every backtest does (20 Nov 2026).
            expected = {**stop_and_trail, "exit_after_ist": "15:15"} if cls is TrendRiderV2Strategy else stop_and_trail
            for params in (defaults, as_text):
                strategy = cls(params)
                self.assertEqual(strategy.bar_minutes, 5)
                self.assertEqual(strategy.index_exits.describe(), expected, cls.__name__)
        # "null" (or None) switches TrendRider's default stop and trail off.
        for off in ("null", None):
            self.assertIsNone(TrendRiderStrategy({"index_exits": off}).index_exits)
        # V2's own stop stays its own: index_stop_adr is not overridden by a default.
        self.assertEqual(TrendRiderV2Strategy({"index_stop_adr": 0.4}).index_exits.stop_adr, 0.4)

    def test_dict_json_and_shorthands(self):
        rules = IndexExitRules.parse('{"stop_adr": 0.5, "supertrend": true, "chandelier": [22, 3], "psar": {"step": 0.01}}')
        self.assertEqual(rules.stop_adr, 0.5)
        self.assertEqual(rules.supertrend, (10, 3.0))
        self.assertEqual(rules.chandelier, (22.0, 3.0))
        self.assertEqual(rules.psar, (0.01, 0.2))
        self.assertTrue(rules.bar_rules)

    def test_bad_settings_are_refused(self):
        for raw in ({"stop": 1}, {"stop_adr": -1}, {"trail_arm_adr": 0.5}, {"exit_after_ist": "2 pm"},
                    {"chandelier": [14]}, "not json"):
            with self.assertRaises(ValueError):
                IndexExitRules.parse(raw)

    def test_merged_keeps_the_rest(self):
        rules = IndexExitRules(stop_adr=0.5, trail_arm_adr=0.5, trail_giveback_adr=0.35).merged(ema_close=21)
        self.assertEqual((rules.stop_adr, rules.ema_close), (0.5, 21))


class TickTest(unittest.TestCase):
    def test_stop_trail_and_time(self):
        rules = IndexExitRules(stop_adr=0.5, trail_arm_adr=0.5, trail_giveback_adr=0.35, exit_after_ist="15:00")
        at = datetime(2026, 3, 16, 11, 0, tzinfo=IST)
        put = {"direction": "SELL", "entry_spot": 20000.0, "adr": 200.0}
        self.assertIsNone(rules.check_tick(put, 20099, at))
        self.assertIn("index stop", rules.check_tick(put, 20100, at))
        call = {"direction": "BUY", "entry_spot": 20000.0, "adr": 200.0}
        self.assertIsNone(rules.check_tick(call, 20120, at))      # +120: armed (100)
        self.assertIsNone(rules.check_tick(call, 20051, at))      # gave back 69 < 70
        self.assertIn("index trail", rules.check_tick(call, 20050, at))
        self.assertIn("time exit", rules.check_tick({"direction": "BUY"}, 20000, at.replace(hour=15)))


class BarTest(unittest.TestCase):
    def test_ema_exit_on_a_close_below(self):
        rules = IndexExitRules(ema_close=5)
        up = bars_from([100 + i for i in range(20)])
        self.assertIsNone(rules.check_bar({"direction": "BUY"}, up))
        self.assertIn("EMA exit", rules.check_bar({"direction": "BUY"}, up + bars_from([100])[:1]))

    def test_supertrend_exit_when_it_turns(self):
        rules = IndexExitRules(supertrend=(10, 3.0))
        rise = [100 + 2 * i for i in range(40)]
        self.assertIsNone(rules.check_bar({"direction": "BUY"}, bars_from(rise)))
        crash = bars_from(rise + [rise[-1] - 10 * i for i in range(1, 15)])
        self.assertIn("supertrend exit", rules.check_bar({"direction": "BUY"}, crash))
        self.assertIsNone(rules.check_bar({"direction": "SELL"}, crash))

    def test_chandelier_from_the_best_close(self):
        rules = IndexExitRules(chandelier=(5, 2.0))
        position = {"direction": "BUY"}
        rise = bars_from([100 + 3 * i for i in range(20)])
        self.assertIsNone(rules.check_bar(position, rise))
        self.assertEqual(position["best_close"], rise[-1].close)
        self.assertIn("chandelier exit", rules.check_bar(position, rise + bars_from([100])[:1]))

    def test_psar_exit_when_price_crosses(self):
        rules = IndexExitRules(psar=(0.02, 0.2))
        rise = [100 + 2 * i for i in range(30)]
        self.assertIsNone(rules.check_bar({"direction": "BUY"}, bars_from(rise)))
        self.assertIn("PSAR exit", rules.check_bar({"direction": "BUY"}, bars_from(rise + [rise[-1] - 30, rise[-1] - 60])))


class TrendRiderParametersTest(unittest.TestCase):
    def test_timeframe(self):
        self.assertEqual(TrendRiderStrategy.get_data_requirements()[0].resolution, "5m")
        one = TrendRiderStrategy({"timeframe": "1m"})
        self.assertEqual((one.get_data_requirements()[0].resolution, one.or_bars, one.session_last, one.warmup_bars),
                         ("1m", 30, "15:29", 375 * 12))
        self.assertEqual(TrendRiderStrategy({"timeframe": "15m"}).session_last, "15:15")
        for bad in ({"timeframe": "4m"}, {"timeframe": "1h"}, {"timeframe": "10m", "or_minutes": 25}):
            with self.assertRaises(ValueError):
                TrendRiderStrategy(bad)

    def test_index_stop_on_trendrider(self):
        days, d = [], FIRST_DAY
        while len(days) < 12:
            if d.weekday() < 5:
                days.append(d)
            d += timedelta(days=1)
        bars = []
        for i, day in enumerate(days[:11]):
            bars += flat_day(day, 20000, 50 if i == 10 else 100)
        strategy = TrendRiderStrategy({"index_exits": {"stop_adr": 0.5}})
        state = strategy.initialize_state()
        contracts = {"buy_ce": OptionContract("NSE:NIFTY_CE", "NIFTY", "2026-03-31", 19950, "CE"),
                     "buy_pe": OptionContract("NSE:NIFTY_PE", "NIFTY", "2026-03-31", 20050, "PE")}
        level, opened = 20000, None
        prices = [(20000, 20010, 19990, 20000)] * 6
        for _ in range(30):
            prices.append((level, level + 12, level - 2, level + 10))
            level += 10
        for frame in session(days[11], prices):
            bars.append(frame)
            groups = [state["position"]["group_id"]] if state["position"] else []
            out = strategy.on_bar(state, StrategyInput(mode="OfflineReplay", timestamp_utc=frame.timestamp_utc,
                                                       underlying="NIFTY", spot_price=frame.close, contracts=contracts,
                                                       bars={"5m": {"index": list(bars)}}, metadata={"open_groups": groups}))
            if out and out[0].signal_type == "OPEN_GROUP":
                opened = frame
                break
        self.assertIsNotNone(opened)
        self.assertEqual(state["position"]["entry_spot"], opened.close)
        adr = state["position"]["adr"]
        out = strategy.on_bar(state, StrategyInput(mode="OfflineReplay", timestamp_utc=opened.timestamp_utc, underlying="NIFTY",
                                                   spot_price=opened.close - 0.5 * adr, contracts=contracts,
                                                   bars={"5m": {"index": list(bars)}},
                                                   metadata={"open_groups": [state["position"]["group_id"]]}))
        self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
        self.assertIn("index stop", out[0].reason)


if __name__ == "__main__":
    unittest.main()
