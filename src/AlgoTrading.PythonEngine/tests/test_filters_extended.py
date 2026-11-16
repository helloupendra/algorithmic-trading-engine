"""
The market-context gate's newer rules: several trading windows, weekday and
expiry-day rules, the opening gap, INDIA VIX, and the trend readings a signal
may not fight (EMA order, Supertrend, ADX, the move from the open, the opening
range, the last few bars, and a vote of them all), plus the RSI cross and the
strong-candle rule an owner's momentum strategy needs, and the indicator rules
(MACD, Parabolic SAR, Heikin-Ashi, Donchian midline, Bollinger expansion, CPR,
RSI side).

Each case is built so the answer is obvious by hand: a rule blocks, or it stands
aside because its input is missing.
"""

import contextlib
import io
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


def from_closes(values):
    """Bars that open at the previous close and close at each value."""
    out, previous = [], values[0]
    for i, close in enumerate(values):
        out.append(bar(i, previous, max(previous, close) + 0.5, min(previous, close) - 0.5, close))
        previous = close
    return out


def straddle():
    return StrategySignal(strategy_name="T", signal_type="OPEN_GROUP", timestamp_utc="2026-09-08T04:00:00Z",
                          reason="test", metadata={},
                          legs=[{"symbol": "NSE:BANKNIFTY26SEP56800CE", "side": "BUY"},
                                {"symbol": "NSE:BANKNIFTY26SEP56800PE", "side": "BUY"}])


def gate_signal(raw, bars, sig):
    return signal_filters.evaluate(signal_filters.parse_filters({"filters": raw}), sig, make_input(bars), False)


#: A rise that keeps accelerating (MACD histogram about +2.6) and its mirror (about -2.6).
SPEEDING_UP = from_closes([100 + 0.05 * i * i for i in range(60)])
SPEEDING_DOWN = from_closes([300 - 0.05 * i * i for i in range(60)])


class IndicatorRuleTests(unittest.TestCase):
    def assertBlocked(self, verdict, key, *phrases):
        self.assertFalse(verdict.allowed, verdict.reason)
        self.assertEqual(verdict.blocked_by, key)
        for phrase in phrases:
            self.assertIn(phrase, verdict.reason)

    def test_macd_blocks_a_signal_against_the_histogram(self):
        self.assertTrue(gate({"macd": True}, SPEEDING_UP).allowed)
        self.assertBlocked(gate({"macd": True}, SPEEDING_UP, direction="bearish"),
                           "macd", "MACD(12,26,9) histogram 2.61", "bearish")
        self.assertTrue(gate({"macd": True}, SPEEDING_DOWN, direction="bearish").allowed)
        verdict = gate({"macd": True}, SPEEDING_DOWN)
        self.assertBlocked(verdict, "macd", "histogram -2.61 is against this bullish signal")
        self.assertAlmostEqual(verdict.details["macd_histogram"], -2.6083, places=3)

    def test_macd_stands_aside_without_enough_bars_or_a_direction(self):
        self.assertTrue(gate({"macd": True}, SPEEDING_DOWN[:33]).allowed)          # 12/26/9 needs 34
        self.assertFalse(gate({"macd": True}, SPEEDING_DOWN[:34]).allowed)
        self.assertTrue(gate_signal({"macd": True}, SPEEDING_DOWN, straddle()).allowed)
        # A steady trend's histogram is 0 up to float noise: no reading, no block.
        self.assertTrue(gate({"macd": True}, rising(), direction="bearish").allowed)

    def test_psar_blocks_a_signal_on_the_wrong_side_of_the_sar(self):
        self.assertTrue(gate({"psar": True}, rising()).allowed)
        self.assertBlocked(gate({"psar": True}, rising(), direction="bearish"),
                           "psar", "close 139.00 is above the Parabolic SAR(0.02,0.2) 134.50")
        self.assertTrue(gate({"psar": True}, falling(), direction="bearish").allowed)
        verdict = gate({"psar": {"step": 0.02, "max": 0.2}}, falling())
        self.assertBlocked(verdict, "psar", "close 101.00 is below", "105.50", "bullish")
        self.assertEqual(verdict.details["psar"], 105.5)

    def test_psar_stands_aside_on_two_bars(self):
        self.assertTrue(gate({"psar": True}, falling(2)).allowed)
        self.assertFalse(gate({"psar": True}, falling(3)).allowed)

    def test_heikin_ashi_blocks_a_signal_against_the_candle_colour(self):
        self.assertTrue(gate({"heikin_ashi": True}, rising(10)).allowed)
        self.assertBlocked(gate({"heikin_ashi": True}, rising(10), direction="bearish"),
                           "heikin_ashi", "is green (open 107.00, close 109.00)")
        self.assertTrue(gate({"heikin_ashi": True}, falling(10), direction="bearish").allowed)
        self.assertBlocked(gate({"heikin_ashi": True}, falling(10)),
                           "heikin_ashi", "is red (open 133.00, close 131.00), against this bullish signal")

    def test_heikin_ashi_stands_aside_on_one_bar(self):
        self.assertTrue(gate({"heikin_ashi": True}, falling(1)).allowed)

    def test_donchian_midline_is_the_previous_bars_range(self):
        # rising(20): the 10 bars before the last span 108.5 to 118.5, midline 113.5; the last close is 119.
        self.assertTrue(gate({"donchian_side": 10}, rising(20)).allowed)
        verdict = gate({"donchian_side": 10}, rising(20), direction="bearish")
        self.assertBlocked(verdict, "donchian_side", "close 119.00 is above 113.50",
                           "previous 10 bars' range 108.50-118.50")
        self.assertEqual(verdict.details["donchian_middle"], 113.5)
        self.assertTrue(gate({"donchian_side": 10}, falling(20), direction="bearish").allowed)
        self.assertBlocked(gate({"donchian_side": 10}, falling(20)), "donchian_side", "below", "bullish")

    def test_donchian_stands_aside_until_n_bars_precede_the_signal_bar(self):
        self.assertTrue(gate({"donchian_side": 10}, falling(10)).allowed)
        self.assertFalse(gate({"donchian_side": 10}, falling(11)).allowed)

    def test_bollinger_expansion_blocks_either_direction_while_the_bands_are_not_widening(self):
        breakout = from_closes([100.0] * 30 + [105.0, 110.0, 115.0])         # width 0 three bars ago
        self.assertTrue(gate({"bollinger_expanding": True}, breakout).allowed)
        self.assertTrue(gate({"bollinger_expanding": True}, breakout, direction="bearish").allowed)
        settling = from_closes([100.0 + (5 if i % 2 else -5) for i in range(20)] + [100.0] * 10)
        for sig in (signal("bullish"), signal("bearish"), straddle()):
            verdict = gate_signal({"bollinger_expanding": True}, settling, sig)
            self.assertBlocked(verdict, "bollinger_expanding",
                               "Bollinger(20,2) width 14.14 is not above its 16.09 of 3 bars ago")
        # A steady trend keeps a constant width, and "not wider" is enough to block.
        self.assertBlocked(gate({"bollinger_expanding": {"period": 20, "lookback": 3}}, rising()),
                           "bollinger_expanding")

    def test_bollinger_expansion_stands_aside_until_period_plus_lookback_bars(self):
        self.assertTrue(gate({"bollinger_expanding": True}, rising(22)).allowed)
        self.assertFalse(gate({"bollinger_expanding": True}, rising(23)).allowed)

    def cpr_day(self, *closes):
        """Yesterday high 110, low 100, close 108 (BC 105, TC 107, width 1.887%), then today's closes."""
        yesterday = [bar(i, 105, h, l, c, base=SESSION_OPEN_UTC - timedelta(days=1))
                     for i, (h, l, c) in enumerate([(110, 104, 106), (107, 100, 103), (108, 102, 108)])]
        return yesterday + [bar(i, c, c + 0.5, c - 0.5, c) for i, c in enumerate(closes)]

    def test_cpr_width_ceiling_blocks_any_signal_on_a_wide_cpr_day(self):
        for sig in (signal("bullish"), signal("bearish"), straddle()):
            verdict = gate_signal({"cpr_max_width_pct": 1.0}, self.cpr_day(108), sig)
            self.assertBlocked(verdict, "cpr_max_width_pct", "today's CPR is 1.887% wide",
                               "BC 105.00 to TC 107.00, pivot 106.00, from 2026-09-07", "above the 1.0% ceiling")
        verdict = gate({"cpr_max_width_pct": 2.0}, self.cpr_day(108))
        self.assertTrue(verdict.allowed)
        self.assertEqual(verdict.details["cpr"]["tc"], 107.0)

    def test_cpr_side_wants_a_bullish_close_above_tc_and_a_bearish_one_below_bc(self):
        rules = {"cpr_side": True}
        self.assertTrue(gate(rules, self.cpr_day(108)).allowed)
        self.assertBlocked(gate(rules, self.cpr_day(108), direction="bearish"),
                           "cpr_side", "close 108.00 not below the CPR bottom (BC) 105.00")
        self.assertTrue(gate(rules, self.cpr_day(104), direction="bearish").allowed)
        self.assertBlocked(gate(rules, self.cpr_day(104)),
                           "cpr_side", "close 104.00 not above the CPR top (TC) 107.00")
        # Inside the range neither side is clear.
        self.assertFalse(gate(rules, self.cpr_day(106)).allowed)
        self.assertFalse(gate(rules, self.cpr_day(106), direction="bearish").allowed)
        self.assertTrue(gate_signal(rules, self.cpr_day(106), straddle()).allowed)

    def test_cpr_rules_stand_aside_without_a_previous_session(self):
        self.assertTrue(gate({"cpr_side": True, "cpr_max_width_pct": 0.01}, falling(20)).allowed)

    def test_rsi_side_mirrors_the_level_for_a_bearish_signal(self):
        zigzag = from_closes([100 + sum(1.2 if j % 2 == 0 else -1.0 for j in range(i)) for i in range(41)])
        # RSI(14) of these closes is 53.0.
        self.assertTrue(gate({"rsi_side": 50}, zigzag).allowed)
        self.assertBlocked(gate({"rsi_side": 50}, zigzag, direction="bearish"),
                           "rsi_side", "RSI(14) 53.0 is above 50, against this bearish signal")
        self.assertTrue(gate({"rsi_side": 45}, zigzag, direction="bearish").allowed)     # ceiling 55
        self.assertBlocked(gate({"rsi_side": 55}, zigzag), "rsi_side", "RSI(14) 53.0 is below 55")
        self.assertBlocked(gate({"rsi_side": 50}, falling()), "rsi_side", "RSI(14) 0.0 is below 50")
        self.assertTrue(gate({"rsi_side": 50}, falling(), direction="bearish").allowed)

    def test_rsi_side_stands_aside_without_enough_bars_or_a_direction(self):
        self.assertTrue(gate({"rsi_side": 50}, falling(14)).allowed)                    # RSI(14) needs 15
        self.assertFalse(gate({"rsi_side": 50}, falling(15)).allowed)
        self.assertTrue(gate_signal({"rsi_side": 50}, falling(), straddle()).allowed)

    def test_direction_aware_rules_stand_aside_for_a_signal_without_a_view(self):
        rules = {"macd": True, "psar": True, "heikin_ashi": True, "donchian_side": 10, "cpr_side": True,
                 "rsi_side": 50}
        self.assertTrue(gate_signal(rules, falling(), straddle()).allowed)
        self.assertFalse(gate(rules, falling()).allowed)


class IndicatorRuleParsingTests(unittest.TestCase):
    def parse(self, raw):
        return signal_filters.parse_filters({"filters": raw})

    def test_true_takes_the_textbook_settings(self):
        config = self.parse({"macd": True, "psar": True, "heikin_ashi": True, "bollinger_expanding": True,
                             "cpr_side": True})
        self.assertEqual(config.macd, (12, 26, 9))
        self.assertEqual(config.psar, (0.02, 0.2))
        self.assertTrue(config.heikin_ashi)
        self.assertEqual(config.bollinger_expanding, (20, 2.0, 3))
        self.assertTrue(config.cpr_side)

    def test_objects_fill_missing_settings_with_the_defaults(self):
        config = self.parse({"macd": {"fast": 5, "slow": 13}, "psar": {"step": 0.01},
                             "bollinger_expanding": {"stdev": 2.5, "lookback": 5}})
        self.assertEqual(config.macd, (5, 13, 9))
        self.assertEqual(config.psar, (0.01, 0.2))
        self.assertEqual(config.bollinger_expanding, (20, 2.5, 5))

    def test_numbers_are_read_from_json_text_too(self):
        config = self.parse('{"donchian_side": 20, "cpr_max_width_pct": "0.25", "rsi_side": 50, '
                            '"macd": {"fast": 12, "slow": 26, "signal": 9}}')
        self.assertEqual((config.donchian_side, config.cpr_max_width_pct, config.rsi_side), (20, 0.25, 50.0))
        self.assertEqual(config.macd, (12, 26, 9))

    def test_each_key_alone_switches_the_gate_on(self):
        for raw in ({"macd": True}, {"psar": True}, {"heikin_ashi": True}, {"donchian_side": 20},
                    {"bollinger_expanding": True}, {"cpr_max_width_pct": 0.3}, {"cpr_side": True},
                    {"rsi_side": 50}):
            with self.subTest(raw=raw):
                self.assertIsNotNone(self.parse(raw))

    def test_off_or_absent_is_not_enforced(self):
        for raw in ({"macd": False}, {"heikin_ashi": False}, {"cpr_side": False}, {"rsi_side": None}):
            with self.subTest(raw=raw):
                self.assertIsNone(self.parse(raw))

    def test_unreadable_settings_are_refused_out_loud(self):
        for raw, key in (({"macd": {"fast": 26, "slow": 12}}, "macd"), ({"macd": "yes"}, "macd"),
                         ({"psar": {"step": 0.5, "max": 0.2}}, "psar"),
                         ({"bollinger_expanding": {"period": 1}}, "bollinger_expanding"),
                         ({"donchian_side": True}, "donchian_side"), ({"donchian_side": 0}, "donchian_side")):
            with self.subTest(raw=raw):
                printed = io.StringIO()
                with contextlib.redirect_stdout(printed):
                    self.assertIsNone(self.parse(raw))
                self.assertIn(f"[FILTER] ignoring {key}=", printed.getvalue())


class BreadthRuleTests(unittest.TestCase):
    """The index's stocks: share above their VWAP and the heavyweights, read from metadata.breadth."""

    def gate_with(self, raw, breadth, direction="bullish"):
        inp = make_input(rising())
        if breadth is not None:
            inp.metadata["breadth"] = breadth
        return signal_filters.evaluate(signal_filters.parse_filters({"filters": raw}), signal(direction), inp, False)

    def test_breadth_share_by_direction(self):
        wide = {"above_vwap_pct": 72.0, "heavy_up": 4, "heavy_down": 1, "heavy_total": 5}
        self.assertTrue(self.gate_with({"breadth_above_vwap_pct": 60}, wide).allowed)
        v = self.gate_with({"breadth_above_vwap_pct": 60}, wide, "bearish")
        self.assertFalse(v.allowed)
        self.assertEqual(v.blocked_by, "breadth_above_vwap_pct")
        self.assertIn("72% of the stocks are above their VWAP; a bearish signal needs at most 40%", v.reason)
        narrow = {**wide, "above_vwap_pct": 35.0}
        self.assertIn("only 35% of the stocks", self.gate_with({"breadth_above_vwap_pct": 60}, narrow).reason)
        self.assertTrue(self.gate_with({"breadth_above_vwap_pct": 60}, narrow, "bearish").allowed)

    def test_heavyweights_on_the_signals_side(self):
        b = {"above_vwap_pct": 50.0, "heavy_up": 3, "heavy_down": 2, "heavy_total": 5}
        self.assertTrue(self.gate_with({"heavyweights_min": 3}, b).allowed)
        v = self.gate_with({"heavyweights_min": 4}, b)
        self.assertEqual(v.blocked_by, "heavyweights_min")
        self.assertIn("3 of 5 heavyweights are above their VWAP; a bullish signal needs 4", v.reason)
        self.assertIn("2 of 5 heavyweights are below", self.gate_with({"heavyweights_min": 3}, b, "bearish").reason)

    def test_no_breadth_data_never_blocks(self):
        for raw in ({"breadth_above_vwap_pct": 60}, {"heavyweights_min": 5}):
            self.assertTrue(self.gate_with(raw, None).allowed)
            self.assertTrue(self.gate_with(raw, {"stocks": 0}).allowed)
        self.assertIsNotNone(signal_filters.parse_filters({"filters": {"heavyweights_min": 3}}))


if __name__ == "__main__":
    unittest.main()
