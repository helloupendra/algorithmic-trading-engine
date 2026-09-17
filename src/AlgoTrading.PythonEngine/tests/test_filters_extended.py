"""
The market-context gate's newer rules: several trading windows, weekday and
expiry-day rules, the opening gap, INDIA VIX, and the trend readings a signal
may not fight (EMA order, Supertrend, ADX, the move from the open, the opening
range, the last few bars, and a vote of them all), plus the RSI cross and the
strong-candle rule an owner's momentum strategy needs.

Each case is built so the answer is obvious by hand: a rule blocks, or it stands
aside because its input is missing.
"""

import unittest
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401

from strategies import signal_filters
from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal

SESSION_OPEN_UTC = datetime(2026, 9, 8, 3, 45, tzinfo=timezone.utc)   # Tuesday 8 Sep 2026, 09:15 IST


def bar(i, o, h, l, c, v=1000, minutes=5, base=SESSION_OPEN_UTC, symbol="NSE:NIFTYBANK-INDEX"):
    return BarFrame(symbol=symbol, resolution="5m",
                    timestamp_utc=(base + timedelta(minutes=minutes * i)).isoformat().replace("+00:00", "Z"),
                    open=o, high=h, low=l, close=c, volume=v)


def rising(n=40, start=100.0, step=1.0):
    return [bar(i, start + i * step, start + i * step + 0.5, start + i * step - 0.5, start + i * step)
            for i in range(n)]


def falling(n=40, start=140.0, step=1.0):
    return [bar(i, start - i * step, start - i * step + 0.5, start - i * step - 0.5, start - i * step)
            for i in range(n)]


def signal(direction="bullish", signal_type="OPEN_GROUP"):
    symbol = "NSE:BANKNIFTY26SEP56800CE" if direction == "bullish" else "NSE:BANKNIFTY26SEP56800PE"
    return StrategySignal(strategy_name="T", signal_type=signal_type, timestamp_utc="2026-09-08T04:00:00Z",
                          reason="test", legs=[{"symbol": symbol, "side": "BUY"}], metadata={})


def make_input(bars, vix=None, expiry=None, resolution="5m"):
    series = {"index": bars}
    if vix is not None:
        series["vix"] = vix
    metadata = {"source": "live-api"}
    if expiry:
        metadata["expiry_date"] = expiry
    return StrategyInput(mode="LivePaper", timestamp_utc=bars[-1].timestamp_utc, underlying="BANKNIFTY",
                         spot_price=float(bars[-1].close), bars={resolution: series}, metadata=metadata)


def gate(raw, bars, direction="bullish", vix=None, expiry=None):
    return signal_filters.evaluate(signal_filters.parse_filters({"filters": raw}),
                                   signal(direction), make_input(bars, vix=vix, expiry=expiry), False)


class WhenToTradeTests(unittest.TestCase):
    def test_two_windows_let_a_signal_through_in_either(self):
        rules = {"windows_ist": [["09:20", "11:00"], ["13:00", "15:15"]]}
        bars = rising(12)                                   # last bar starts 10:10 IST
        self.assertTrue(gate(rules, bars).allowed)
        late = [bar(i, 100, 101, 99, 100, base=SESSION_OPEN_UTC + timedelta(hours=2, minutes=10))
                for i in range(3)]                          # 11:25, 11:30, 11:35 IST
        verdict = gate(rules, late)
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "trade_window_ist")
        self.assertIn("09:20-11:00 or 13:00-15:15", verdict.reason)

    def test_weekdays_are_named_and_matched(self):
        self.assertTrue(gate({"weekdays": ["Tue", "Thu"]}, rising(5)).allowed)
        verdict = gate({"weekdays": ["Mon"]}, rising(5))
        self.assertFalse(verdict.allowed)
        self.assertIn("Tue", verdict.reason)

    def test_expiry_days_can_be_the_only_ones_or_none_of_them(self):
        bars = rising(5)
        self.assertTrue(gate({"expiry_day": "only"}, bars, expiry="2026-09-08").allowed)
        self.assertFalse(gate({"expiry_day": "skip"}, bars, expiry="2026-09-08").allowed)
        self.assertTrue(gate({"expiry_day": "skip"}, bars, expiry="2026-09-10").allowed)
        # Without an expiry the rule cannot judge, so it stands aside.
        self.assertTrue(gate({"expiry_day": "only"}, bars).allowed)

    def test_an_expiry_day_can_be_cut_off_at_a_time(self):
        afternoon = [bar(i, 100, 101, 99, 100, base=SESSION_OPEN_UTC + timedelta(hours=4)) for i in range(3)]
        verdict = gate({"expiry_cutoff_ist": "13:00"}, afternoon, expiry="2026-09-08")
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "expiry_cutoff_ist")
        self.assertTrue(gate({"expiry_cutoff_ist": "13:00"}, afternoon, expiry="2026-09-10").allowed)


class MarketStateTests(unittest.TestCase):
    def test_the_opening_gap_is_measured_against_the_previous_close(self):
        yesterday = [bar(i, 100, 100.5, 99.5, 100, base=SESSION_OPEN_UTC - timedelta(days=1)) for i in range(5)]
        today = [bar(i, 101, 101.5, 100.5, 101) for i in range(5)]          # +1% gap
        self.assertFalse(gate({"max_gap_percent": 0.5}, yesterday + today).allowed)
        self.assertTrue(gate({"min_gap_percent": 0.5}, yesterday + today).allowed)
        verdict = gate({"min_gap_percent": 2.0}, yesterday + today)
        self.assertEqual(verdict.blocked_by, "min_gap_percent")

    def test_india_vix_is_read_as_a_level_and_as_a_move(self):
        bars = rising(10)
        calm = [bar(i, 12, 12.1, 11.9, 12.0) for i in range(10)]
        self.assertTrue(gate({"max_vix": 14}, bars, vix=calm).allowed)
        self.assertFalse(gate({"min_vix": 14}, bars, vix=calm).allowed)
        spiking = [bar(i, 12, 12.1, 11.9, 12.0 + i * 0.2) for i in range(10)]   # +15% on the day
        self.assertTrue(gate({"min_vix_change_percent": 3}, bars, vix=spiking).allowed)
        self.assertFalse(gate({"max_vix_change_percent": 3}, bars, vix=spiking).allowed)
        # No VIX series loaded: the rule stands aside instead of blocking everything.
        self.assertTrue(gate({"min_vix": 14}, bars).allowed)


class TrendTests(unittest.TestCase):
    def test_a_signal_may_not_fight_the_ema_order(self):
        self.assertTrue(gate({"ema_fast": 9, "ema_slow": 21}, rising()).allowed)
        verdict = gate({"ema_fast": 9, "ema_slow": 21}, falling())
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "ema_order")
        self.assertTrue(gate({"ema_fast": 9, "ema_slow": 21}, falling(), direction="bearish").allowed)

    def test_supertrend_and_adx_block_a_counter_trend_signal(self):
        self.assertFalse(gate({"supertrend": [10, 3]}, falling()).allowed)
        self.assertTrue(gate({"supertrend": [10, 3]}, falling(), direction="bearish").allowed)
        verdict = gate({"adx_min": 20}, falling(60))
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "adx_min")

    def test_the_days_move_the_opening_range_and_the_last_bars_each_have_a_view(self):
        self.assertFalse(gate({"max_move_from_open_percent": 0.5}, falling(30)).allowed)
        self.assertTrue(gate({"max_move_from_open_percent": 0.5}, falling(30), direction="bearish").allowed)
        self.assertFalse(gate({"opening_range_minutes": 15}, falling(30)).allowed)
        self.assertFalse(gate({"against_return_bars": 6, "against_return_percent": 0.2}, falling(30)).allowed)
        self.assertTrue(gate({"against_return_bars": 6, "against_return_percent": 50}, falling(30)).allowed)

    def test_the_vote_blocks_only_when_enough_readings_disagree(self):
        # The vote reads VWAP, the EMA, Supertrend, the opening range and the move from
        # the open by itself; none of them is switched on as a blocking rule here.
        rules = {"vote_min_against": 3}
        self.assertTrue(gate(rules, falling(40), direction="bearish").allowed)
        verdict = gate(rules, falling(40))
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "vote_min_against")
        self.assertGreaterEqual(verdict.details["votes_against"], 3)
        # A high bar: the same readings are not enough.
        self.assertTrue(gate({**rules, "vote_min_against": 9}, falling(40)).allowed)


class MomentumTests(unittest.TestCase):
    def test_rsi_must_cross_the_level_on_the_signal_bar(self):
        bars = falling(30) + [bar(30, 110, 130, 109, 129), bar(31, 129, 150, 128, 149)]
        self.assertTrue(gate({"rsi_cross_up": 60}, bars).allowed)          # RSI crossed up on the last bar
        self.assertFalse(gate({"rsi_cross_up": 60}, bars + [bar(32, 149, 151, 148, 150)]).allowed)
        self.assertTrue(gate({"rsi_cross_down": 40}, falling(40), direction="bearish").allowed is False)

    def test_a_strong_candle_is_mostly_body(self):
        strong = rising(5) + [bar(5, 100, 111, 99, 110)]                    # body 10 of a range of 12
        weak = rising(5) + [bar(5, 100, 130, 70, 101)]                      # body 1 of a range of 60
        self.assertTrue(gate({"min_candle_body_ratio": 0.5}, strong).allowed)
        verdict = gate({"min_candle_body_ratio": 0.5}, weak)
        self.assertFalse(verdict.allowed)
        self.assertEqual(verdict.blocked_by, "min_candle_body_ratio")


if __name__ == "__main__":
    unittest.main()
