"""
Known-answer tests for the shared indicators and the market-context gate.

These are the numbers a strategy and a filter both trade on, so each one is
checked against a value worked out by hand rather than against whatever the
code happened to return the first time.
"""

import unittest
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401

from strategies import indicators, signal_filters
from strategies.base_strategy import BarFrame, StrategyInput, StrategySignal

SESSION_OPEN_UTC = datetime(2026, 9, 8, 3, 45, tzinfo=timezone.utc)  # 09:15 IST


def bar(i, o, h, l, c, v=1000, minutes=5, base=SESSION_OPEN_UTC):
    return BarFrame(
        symbol="NSE:NIFTYBANK-INDEX",
        resolution="5m",
        timestamp_utc=(base + timedelta(minutes=minutes * i)).isoformat().replace("+00:00", "Z"),
        open=o, high=h, low=l, close=c, volume=v,
    )


def flat(n, price=100.0, volume=1000.0):
    return [bar(i, price, price + 0.5, price - 0.5, price, volume) for i in range(n)]


def make_signal(legs, metadata=None, signal_type="OPEN_GROUP"):
    return StrategySignal(
        strategy_name="T", signal_type=signal_type,
        timestamp_utc="2026-09-08T04:00:00Z", reason="test",
        legs=legs, metadata=metadata or {},
    )


def make_input(bars, resolution="5m"):
    return StrategyInput(
        mode="LivePaper", timestamp_utc="2026-09-08T04:00:00Z",
        underlying="BANKNIFTY", spot_price=float(bars[-1].close) if bars else 0.0,
        bars={resolution: {"index": bars}}, metadata={"source": "live-api"},
    )


BULL = make_signal([{"symbol": "NSE:BANKNIFTY26SEP56800CE", "side": "BUY"}])


def gate(raw, sig, bars, forming=False):
    return signal_filters.evaluate(
        signal_filters.parse_filters({"filters": raw}), sig, make_input(bars), forming)


class IndicatorTests(unittest.TestCase):
    def test_sma_and_ema_need_a_full_window(self):
        self.assertIsNone(indicators.sma([1, 2], 3))
        self.assertIsNone(indicators.ema([1, 2], 3))

    def test_sma_is_the_mean_of_the_last_period(self):
        self.assertEqual(indicators.sma([1, 2, 3, 4], 2), 3.5)

    def test_ema_is_seeded_with_the_sma(self):
        # Exactly `period` values: the EMA is the SMA, no smoothing applied yet.
        self.assertAlmostEqual(indicators.ema([2, 4, 6], 3), 4.0)
        # One more: 8*0.5 + 4*0.5 = 6 for period 3 (multiplier 2/4).
        self.assertAlmostEqual(indicators.ema([2, 4, 6, 8], 3), 6.0)

    def test_vwap_weights_by_volume(self):
        bars = [bar(0, 10, 12, 8, 10, 100), bar(1, 20, 22, 18, 20, 300)]
        self.assertAlmostEqual(indicators.vwap(bars), 17.5)

    def test_vwap_is_none_without_volume(self):
        """An index reports no volume; an invented VWAP is worse than none."""
        self.assertIsNone(indicators.vwap([bar(0, 10, 12, 8, 10, 0)]))

    def test_true_range_includes_the_gap(self):
        yesterday = bar(0, 100, 101, 99, 100)
        gapped = bar(1, 110, 111, 109, 110)
        self.assertAlmostEqual(indicators.true_range(gapped, yesterday), 11.0)
        self.assertAlmostEqual(indicators.true_range(gapped, None), 2.0)

    def test_atr_percent_is_comparable_across_instruments(self):
        cheap = [bar(i, 100, 101, 99, 100) for i in range(20)]
        dear = [bar(i, 10000, 10100, 9900, 10000) for i in range(20)]
        self.assertAlmostEqual(indicators.atr_percent(cheap, 14),
                               indicators.atr_percent(dear, 14))

    def test_rsi_is_100_when_every_bar_rises(self):
        self.assertAlmostEqual(indicators.rsi([float(x) for x in range(1, 40)], 14), 100.0)

    def test_volume_zscore_flags_the_unusual_bar(self):
        bars = flat(21, volume=1000)
        self.assertAlmostEqual(indicators.volume_zscore(bars, 20), 0.0)
        bars[-1] = bar(20, 100, 100.5, 99.5, 100, v=5000)
        # A perfectly flat history makes any excess unbounded.
        self.assertEqual(indicators.volume_zscore(bars, 20), float("inf"))

    def test_volume_zscore_is_none_without_volume_history(self):
        self.assertIsNone(indicators.volume_zscore(flat(21, volume=0), 20))

    def test_session_bars_stop_at_the_day_boundary(self):
        yesterday = [bar(i, 100, 101, 99, 100, base=SESSION_OPEN_UTC - timedelta(days=1))
                     for i in range(3)]
        self.assertEqual(len(indicators.session_bars(yesterday + flat(4))), 4)


class GateIndicatorTests(unittest.TestCase):
    """The readings behind the gate's MACD, PSAR, Heikin-Ashi, Donchian, Bollinger and CPR rules."""

    def test_macd_aligns_two_sma_seeded_emas_and_smooths_their_gap(self):
        # fast 2 (k 2/3), slow 3 (k 1/2), signal 2 on 1, 2, 3, 5, 4:
        #   EMA2 = 1.5, 2.5, 25/6, 73/18     EMA3 = 2, 3.5, 15/4   (from the 2nd and 3rd value)
        #   MACD = 1/2, 2/3, 11/36           signal = 7/12 (the SMA seed), then 43/108
        reading = indicators.macd([1, 2, 3, 5, 4], 2, 3, 2)
        self.assertAlmostEqual(reading["macd"], 11 / 36)
        self.assertAlmostEqual(reading["signal"], 43 / 108)
        self.assertAlmostEqual(reading["histogram"], -5 / 54)
        # slow + signal - 1 = 4 values: the signal line is just its seed.
        self.assertAlmostEqual(indicators.macd([1, 2, 3, 5], 2, 3, 2)["histogram"], 2 / 3 - 7 / 12)

    def test_macd_needs_enough_values_and_a_fast_ema_faster_than_the_slow(self):
        self.assertIsNone(indicators.macd([1, 2, 3], 2, 3, 2))
        self.assertIsNone(indicators.macd([float(x) for x in range(40)], 26, 12, 9))
        self.assertIsNone(indicators.macd([float(x) for x in range(33)]))      # 12/26/9 needs 34

    def test_bollinger_uses_the_population_standard_deviation(self):
        # Mean 5, population SD exactly 2 (the textbook set), so 2 SD bands are 1 and 9.
        reading = indicators.bollinger([2, 4, 4, 4, 5, 5, 7, 9], 8, 2.0)
        self.assertEqual((reading["middle"], reading["upper"], reading["lower"], reading["width"]),
                         (5.0, 9.0, 1.0, 8.0))
        # Only the last `period` values count.
        self.assertEqual(indicators.bollinger([100, 2, 4, 4, 4, 5, 5, 7, 9], 8, 2.0)["width"], 8.0)
        self.assertIsNone(indicators.bollinger([1, 2, 3], 8))

    def test_donchian_includes_the_last_bar(self):
        bars = [bar(0, 9, 10, 8, 9), bar(1, 10, 12, 9, 11), bar(2, 9, 11, 7, 8)]
        self.assertEqual(indicators.donchian(bars, 3), {"upper": 12.0, "lower": 7.0, "middle": 9.5})
        self.assertEqual(indicators.donchian(bars, 2), {"upper": 12.0, "lower": 7.0, "middle": 9.5})
        self.assertEqual(indicators.donchian(bars[:2], 2), {"upper": 12.0, "lower": 8.0, "middle": 10.0})
        self.assertIsNone(indicators.donchian(bars, 4))

    def test_heikin_ashi_carries_the_previous_body_midpoint(self):
        # HA0: close (10+12+9+11)/4 = 10.5, open (10+11)/2 = 10.5
        # HA1: open (10.5+10.5)/2 = 10.5, close (11+13+10+12)/4 = 11.5, high 13, low 10
        bars = [bar(0, 10, 12, 9, 11), bar(1, 11, 13, 10, 12)]
        self.assertEqual(indicators.heikin_ashi(bars), {"open": 10.5, "high": 13.0, "low": 10.0, "close": 11.5})
        self.assertIsNone(indicators.heikin_ashi(bars[:1]))

    def test_parabolic_sar_trails_a_rise_and_flips_on_a_break(self):
        # Rising start: SAR 9 (first low), EP 12 (second high), AF 0.02.
        #   bar 2: 9 + 0.02 x 3 = 9.06, held under the lows 10 and 9 -> 9; new high 13 -> AF 0.04
        #   bar 3: 9 + 0.04 x 4 = 9.16 (under 11 and 10); new high 14 -> AF 0.06
        bars = [bar(0, 10, 11, 9, 10), bar(1, 11, 12, 10, 11), bar(2, 12, 13, 11, 12), bar(3, 13, 14, 12, 13)]
        reading = indicators.parabolic_sar(bars)
        self.assertAlmostEqual(reading["sar"], 9.16)
        self.assertEqual(reading["direction"], "bullish")
        # bar 4 trades to 8, through 9.16 + 0.06 x 4.84 = 9.4504: the trend flips and the
        # SAR jumps to the old extreme 14 (above the bar's own high 13).
        flipped = indicators.parabolic_sar(bars + [bar(4, 13, 13, 8, 8.5)])
        self.assertEqual(flipped, {"sar": 14.0, "direction": "bearish"})
        self.assertIsNone(indicators.parabolic_sar(bars[:2]))
        self.assertIsNone(indicators.parabolic_sar(bars, step=0.3, maximum=0.2))

    def test_cpr_comes_from_the_previous_session(self):
        # Yesterday: high 110, low 100, close 108 -> P 106, BC 105, TC 2 x 106 - 105 = 107.
        yesterday = [bar(0, 104, 106, 103, 105), bar(1, 105, 110, 104, 109), bar(2, 109, 109, 100, 101),
                     bar(3, 101, 108, 101, 108)]
        yesterday = [bar(i, b.open, b.high, b.low, b.close, base=SESSION_OPEN_UTC - timedelta(days=1))
                     for i, b in enumerate(yesterday)]
        levels = indicators.cpr(yesterday + flat(3, price=200.0))   # today's own prices play no part
        self.assertEqual((levels["high"], levels["low"], levels["close"]), (110.0, 100.0, 108.0))
        self.assertEqual((levels["pivot"], levels["bc"], levels["tc"]), (106.0, 105.0, 107.0))
        self.assertAlmostEqual(levels["width_percent"], 200 / 106)
        self.assertEqual(levels["session"], "2026-09-07")

    def test_cpr_swaps_tc_and_bc_when_the_close_was_low(self):
        # Close 102 under the midpoint 105: P 104, BC 105, TC 103 -> swapped to BC 103, TC 105.
        yesterday = [bar(0, 105, 110, 100, 102, base=SESSION_OPEN_UTC - timedelta(days=1))]
        levels = indicators.cpr(yesterday + flat(2))
        self.assertEqual((levels["pivot"], levels["bc"], levels["tc"]), (104.0, 103.0, 105.0))
        self.assertAlmostEqual(levels["width_percent"], 200 / 104)

    def test_cpr_takes_the_latest_earlier_session_and_none_without_one(self):
        older = [bar(0, 500, 600, 400, 550, base=SESSION_OPEN_UTC - timedelta(days=4))]
        friday = [bar(0, 105, 110, 100, 108, base=SESSION_OPEN_UTC - timedelta(days=3))]
        self.assertEqual(indicators.cpr(older + friday + flat(2))["pivot"], 106.0)
        self.assertEqual(len(indicators.previous_session_bars(older + friday + flat(2))), 1)
        self.assertIsNone(indicators.cpr(flat(5)))
        self.assertEqual(indicators.previous_session_bars([]), [])


class PatternTests(unittest.TestCase):
    def test_bullish_engulfing(self):
        bars = [bar(0, 110, 111, 104, 105), bar(1, 104, 116, 103, 115)]
        self.assertTrue(indicators.is_bullish_engulfing(bars))
        self.assertFalse(indicators.is_bearish_engulfing(bars))

    def test_bearish_engulfing_is_the_mirror(self):
        bars = [bar(0, 105, 111, 104, 110), bar(1, 111, 112, 103, 104)]
        self.assertTrue(indicators.is_bearish_engulfing(bars))
        self.assertFalse(indicators.is_bullish_engulfing(bars))

    def test_hammer_needs_a_long_lower_wick(self):
        self.assertTrue(indicators.is_hammer([bar(0, 108, 110, 100, 109)]))
        self.assertFalse(indicators.is_hammer([bar(0, 100, 110, 99, 109)]))

    def test_marubozu_is_mostly_body(self):
        self.assertTrue(indicators.is_marubozu([bar(0, 100, 110.2, 99.8, 110)]))
        self.assertFalse(indicators.is_marubozu([bar(0, 100, 120, 90, 101)]))


class DirectionTests(unittest.TestCase):
    def test_metadata_wins(self):
        self.assertEqual(
            signal_filters.signal_direction(make_signal([], {"direction": "SELL"})), "bearish")

    def test_inferred_from_a_single_leg(self):
        cases = [
            ("NSE:BANKNIFTY26SEP56800CE", "BUY", "bullish"),
            ("NSE:BANKNIFTY26SEP56800PE", "BUY", "bearish"),
            ("NSE:BANKNIFTY26SEP56800CE", "SELL", "bearish"),
            ("NSE:BANKNIFTY26SEP56800PE", "SELL", "bullish"),
        ]
        for symbol, side, expected in cases:
            with self.subTest(symbol=symbol, side=side):
                sig = make_signal([{"symbol": symbol, "side": side}])
                self.assertEqual(signal_filters.signal_direction(sig), expected)

    def test_a_straddle_has_no_direction(self):
        """Legs that disagree express no view, so direction rules stand aside."""
        sig = make_signal([
            {"symbol": "NSE:BANKNIFTY26SEP56800CE", "side": "BUY"},
            {"symbol": "NSE:BANKNIFTY26SEP56800PE", "side": "BUY"},
        ])
        self.assertIsNone(signal_filters.signal_direction(sig))


class GateTests(unittest.TestCase):
    def test_no_filters_configured_allows_everything(self):
        self.assertTrue(signal_filters.evaluate(None, BULL, make_input(flat(3)), False).allowed)

    def test_close_signals_are_never_blocked(self):
        """Refusing to let a strategy OUT of a position is how a filter loses money."""
        close = make_signal([{"symbol": "NSE:BANKNIFTY26SEP56800CE", "side": "SELL"}],
                            signal_type="CLOSE_GROUP")
        self.assertTrue(gate({"min_atr_percent": 99.0}, close, flat(30)).allowed)

    def test_trade_window(self):
        bars = flat(3)  # 09:15, 09:20, 09:25 IST
        self.assertEqual(gate({"trade_window_ist": ["09:30", "15:05"]}, BULL, bars).blocked_by,
                         "trade_window_ist")
        self.assertTrue(gate({"trade_window_ist": ["09:00", "15:05"]}, BULL, bars).allowed)

    def test_block_open_minutes_counts_from_the_session_open(self):
        bars = flat(3)  # last bar is 10 minutes into the session
        self.assertEqual(gate({"block_open_minutes": 30}, BULL, bars).blocked_by,
                         "block_open_minutes")
        self.assertTrue(gate({"block_open_minutes": 5}, BULL, bars).allowed)

    def test_volume_rule_stands_aside_when_there_is_no_volume(self):
        """An index has none - the rule must not reject every signal forever."""
        verdict = gate({"min_volume_zscore": 2.0}, BULL, flat(30, volume=0))
        self.assertTrue(verdict.allowed)
        self.assertIsNone(verdict.details["volume_zscore"])

    def test_volume_rule_blocks_a_quiet_bar(self):
        self.assertEqual(gate({"min_volume_zscore": 1.0}, BULL, flat(30)).blocked_by,
                         "min_volume_zscore")

    def test_atr_floor_blocks_a_dead_market(self):
        self.assertEqual(gate({"min_atr_percent": 5.0}, BULL, flat(30)).blocked_by,
                         "min_atr_percent")
        self.assertTrue(gate({"min_atr_percent": 0.001}, BULL, flat(30)).allowed)

    def test_vwap_side(self):
        below = [bar(i, 110, 110, 110, 110, 100) for i in range(10)]
        below.append(bar(10, 90, 90, 90, 90, 100))
        self.assertEqual(gate({"require_vwap_side": True}, BULL, below).blocked_by,
                         "require_vwap_side")

        above = [bar(i, 90, 90, 90, 90, 100) for i in range(10)]
        above.append(bar(10, 130, 130, 130, 130, 100))
        self.assertTrue(gate({"require_vwap_side": True}, BULL, above).allowed)

    def test_ema_side_blocks_the_wrong_side(self):
        falling = [bar(i, 200 - i, 200 - i, 200 - i, 200 - i, 100) for i in range(30)]
        self.assertEqual(
            gate({"ema_period": 20, "require_ema_side": True}, BULL, falling).blocked_by,
            "require_ema_side")

    def test_pattern_auto_requires_a_confirming_candle(self):
        self.assertEqual(gate({"require_pattern": "auto"}, BULL, flat(5)).blocked_by,
                         "require_pattern")
        engulfing = flat(5) + [bar(5, 110, 111, 104, 105), bar(6, 104, 116, 103, 115)]
        self.assertTrue(gate({"require_pattern": "auto"}, BULL, engulfing).allowed)

    def test_forming_bar_is_dropped_before_judging(self):
        """
        The live loop always holds a bar that has not closed. Judging it would
        use a candle that has not happened - the classic way a backtest
        flatters itself - so the caller says so and the gate trims.
        """
        bars = flat(4)  # 09:15, 09:20, 09:25, 09:30 IST
        window = signal_filters.parse_filters(
            {"filters": {"trade_window_ist": ["09:20", "09:27"]}})
        # Forming: judged on 09:25, inside the window.
        self.assertTrue(signal_filters.evaluate(window, BULL, make_input(bars), True).allowed)
        # Closed: judged on 09:30, outside it.
        self.assertTrue(signal_filters.evaluate(window, BULL, make_input(bars), False).blocked)

    def test_no_closed_bars_blocks_rather_than_guesses(self):
        verdict = signal_filters.evaluate(
            signal_filters.parse_filters({"filters": {"min_atr_percent": 0.1}}),
            BULL, make_input([]), False)
        self.assertEqual(verdict.blocked_by, "no_bars")

    def test_blocked_verdict_explains_itself(self):
        verdict = gate({"min_atr_percent": 5.0}, BULL, flat(30))
        self.assertIn("5.0%", verdict.reason)
        self.assertIsNotNone(verdict.details["atr_percent"])


if __name__ == "__main__":
    unittest.main()
