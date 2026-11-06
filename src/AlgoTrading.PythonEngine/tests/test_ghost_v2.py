"""
GhostTangentCrossingsV2: which crossings it trades, what it records about each,
how it holds one position at a time, and that a replay through the engine books
what it says.

The gate tests hand `_evaluate_zig_zag` a leg and candles placed on the
original's own line (computed with the class's `_generate_ellipse` /
`_ellipse_slope`), so each crossing lands on a known bar. The geometry itself is
the original's; one test pins that, with every gate off, both find the same
crossings on the same candles.
"""

import json
import unittest
from datetime import date, datetime, time, timedelta, timezone
from typing import Any, Dict, List, Optional

import _bootstrap  # noqa: F401

from backtest.engine import run_backtest
from backtest.timeutil import iso_utc
from strategies.base_strategy import OptionContract, StrategyInput, StrategySignal
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.ghost_tangent_crossings_v2 import GhostTangentCrossingsV2Strategy
from strategies.registry import load_strategy_factories
from test_engine import CE as ENGINE_CE, PE as ENGINE_PE, DAY1, DAY2, SPOT, FakeApi, quiet, run_row, session_bars

IST = timezone(timedelta(hours=5, minutes=30))
UNDERLYING = "NIFTY"
CE = OptionContract(symbol="NSE:NIFTY26SEP25000CE", underlying=UNDERLYING, expiry_date="2026-09-29",
                    strike_price=25000, option_type="CE")
PE = OptionContract(symbol="NSE:NIFTY26SEP25000PE", underlying=UNDERLYING, expiry_date="2026-09-29",
                    strike_price=25000, option_type="PE")

#: Gates off: the original's crossings, nothing dropped.
EVERY_GATE_OFF = {"max_signal_age_bars": 0, "line_life": 0, "min_leg_bars": 0}


class Frame:
    """A BarFrame as the engine hands one to a strategy."""

    def __init__(self, stamp: str, close: float, high: Optional[float] = None, low: Optional[float] = None):
        self.timestamp_utc = stamp
        self.open = close
        self.close = close
        self.high = close if high is None else high
        self.low = close if low is None else low


def stamp(day: date, hh: int, mm: int) -> str:
    return datetime.combine(day, time(hh, mm), tzinfo=IST).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def session(n: int, day: date = date(2026, 9, 9)) -> List[str]:
    """n consecutive 5-minute stamps from 09:15 IST."""
    start = datetime.combine(day, time(9, 15), tzinfo=IST)
    return [(start + timedelta(minutes=5 * i)).astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            for i in range(n)]


def inp(newest: Any, mode: str = "OfflineReplay", open_groups: Optional[List[str]] = None,
        contracts: Optional[Dict[str, OptionContract]] = None, bars: Optional[List[Any]] = None) -> StrategyInput:
    metadata: Dict[str, Any] = {"resolution": "5m"}
    if open_groups is not None:
        metadata["open_groups"] = open_groups
    return StrategyInput(mode=mode, timestamp_utc=newest.timestamp_utc, underlying=UNDERLYING,
                         spot_price=newest.close, atm_strike=25000, strike_step=50, lot_size=65,
                         contracts={"atm_ce": CE, "atm_pe": PE} if contracts is None else contracts,
                         bars={"5m": {"index": bars or [newest]}}, metadata=metadata)


# --- a leg on known candles -------------------------------------------------------

#: An up-leg: the earlier pivot (a low) at bar 1, the later one (a high) at bar 7.
A_BAR, A_PRICE, B_BAR, B_PRICE = 1, 100.0, 7, 160.0


def line_of(strategy: GhostTangentCrossingsStrategy, a_bar=A_BAR, b_bar=B_BAR, a_price=A_PRICE, b_price=B_PRICE):
    """The original's trigger line for the leg, as a function of the bar number."""
    # The original's argument order: earlier bar, later bar, LATER price, earlier price.
    points = strategy._generate_ellipse(a_bar, b_bar, b_price, a_price)
    tangent, slope = strategy._ellipse_slope(a_bar, b_bar, b_price, a_price, points)
    return lambda x: tangent["price"] + slope * (x - tangent["index"])


def up_leg_candles(strategy, cross_at: int, newest: int, b_bar: int = B_BAR) -> List[Frame]:
    """
    Bars 1..newest (bars[k] is bar k+1). The leg rises from A to B; every close
    after B stays above the rising line until `cross_at`, which closes under it.
    """
    line = line_of(strategy, b_bar=b_bar)
    stamps = session(newest)
    frames = []
    for x in range(1, newest + 1):
        if x <= b_bar:
            close = A_PRICE + (B_PRICE - A_PRICE) * (x - A_BAR) / (b_bar - A_BAR)
        elif x < cross_at:
            close = line(x) + 5
        elif x == cross_at:
            close = line(x) - 5
        else:
            close = line(x) - 10
        frames.append(Frame(stamps[x - 1], close))
    return frames


def evaluate(strategy, frames: List[Frame], state: Optional[Dict[str, Any]] = None, b_bar: int = B_BAR):
    state = strategy.initialize_state() if state is None else state
    signal = strategy._evaluate_zig_zag(state, frames, len(frames), A_BAR, b_bar, B_PRICE, A_PRICE, True,
                                        inp(frames[-1], bars=frames))
    return signal, state


class GateTests(unittest.TestCase):
    def test_a_fresh_crossing_is_traded_with_the_leg_and_line_it_came_from(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        frames = up_leg_candles(strategy, cross_at=9, newest=9)

        signal, state = evaluate(strategy, frames)

        self.assertEqual(signal.signal_type, "SELL")          # an up-leg's break buys the put
        ghost = signal.metadata["ghost"]
        self.assertEqual(ghost["leg"], "up")
        self.assertEqual(ghost["a"], {"bar": A_BAR, "price": A_PRICE})
        self.assertEqual(ghost["b"], {"bar": B_BAR, "price": B_PRICE})
        self.assertEqual(ghost["leg_bars"], 6)
        self.assertEqual(ghost["cross_bar"], 9)
        self.assertEqual(ghost["signal_age_bars"], 0)
        self.assertEqual(ghost["cross_time"], frames[8].timestamp_utc)
        self.assertAlmostEqual(ghost["cross_close"], round(frames[8].close, 2))
        self.assertAlmostEqual(ghost["trigger_at_cross"], round(line_of(strategy)(9), 2))
        self.assertLess(ghost["cross_close"], ghost["trigger_at_cross"])
        self.assertEqual(state["last_signal_idx"], 9)
        json.dumps(signal.metadata)                           # it has to reach MetadataJson

    def test_a_stale_crossing_is_not_traded_and_is_counted_once(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        frames = up_leg_candles(strategy, cross_at=9, newest=12)  # the close under the line is 3 bars old

        signal, state = evaluate(strategy, frames)
        again, state = evaluate(strategy, frames, state)          # the next tick finds it again

        self.assertIsNone(signal)
        self.assertIsNone(again)
        self.assertEqual(state["dropped"]["stale"], 1)
        self.assertIsNone(state["last_signal_idx"])

    def test_the_original_trades_the_same_stale_crossing(self):
        strategy = GhostTangentCrossingsV2Strategy({"max_signal_age_bars": 0})
        frames = up_leg_candles(strategy, cross_at=9, newest=12)

        signal, _ = evaluate(strategy, frames)

        self.assertEqual(signal.signal_type, "SELL")
        self.assertEqual(signal.metadata["ghost"]["signal_age_bars"], 3)

    def test_a_crossing_past_the_lines_life_is_not_traded(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        # Six-bar leg; the first close under the line comes 7 bars after B.
        frames = up_leg_candles(strategy, cross_at=B_BAR + 7, newest=B_BAR + 7)

        signal, state = evaluate(strategy, frames)

        self.assertIsNone(signal)
        self.assertEqual(state["dropped"]["line_expired"], 1)

    def test_a_longer_line_life_lets_it_through(self):
        strategy = GhostTangentCrossingsV2Strategy({"line_life": 1.5})
        frames = up_leg_candles(strategy, cross_at=B_BAR + 7, newest=B_BAR + 7)

        signal, _ = evaluate(strategy, frames)

        self.assertEqual(signal.signal_type, "SELL")

    def test_a_short_leg_is_not_traded(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        frames = up_leg_candles(strategy, cross_at=4, newest=4, b_bar=3)   # a two-bar leg

        signal, state = evaluate(strategy, frames, b_bar=3)

        self.assertIsNone(signal)
        self.assertEqual(state["dropped"]["short_leg"], 1)

    def test_a_crossing_already_signalled_is_not_counted_as_stale(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        state = strategy.initialize_state()
        state["last_signal_idx"] = 9

        signal, state = evaluate(strategy, up_leg_candles(strategy, cross_at=9, newest=12), state)

        self.assertIsNone(signal)
        self.assertEqual(state["dropped"]["stale"], 0)


class ReadingTests(unittest.TestCase):
    def test_live_reads_closed_candles_inside_the_session_only(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        day = date(2026, 9, 9)
        pre_open = Frame(stamp(day, 9, 0), 23500, low=23036.85)        # the 9 Sep spike
        early = Frame(stamp(day, 9, 10), 23500)                        # FYERS's stray 09:10 candle
        first, second = Frame(stamp(day, 9, 15), 23510), Frame(stamp(day, 9, 20), 23520)
        forming = Frame(stamp(day, 9, 25), 23530)

        readable = strategy._readable([pre_open, early, first, second, forming], "LivePaper", "5m")

        self.assertEqual(readable, [first, second])

    def test_a_replay_keeps_its_newest_candle(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        frames = [Frame(s, 100) for s in session(3)]

        self.assertEqual(strategy._readable(frames, "OfflineReplay", "5m"), frames)

    def test_a_replay_reads_a_growing_list_as_a_fresh_read_would(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        day = date(2026, 9, 9)
        pre_open = Frame(stamp(day, 9, 0), 23500)
        frames = [Frame(s, 100) for s in session(3)]
        grown: List[Any] = [pre_open]

        for frame in frames:
            grown.append(frame)                                   # the engine's list, growing in place
            self.assertEqual(strategy._readable(grown, "OfflineReplay", "5m"), frames[:frames.index(frame) + 1])
        # Another list is read from its own start, not continued from the last one.
        self.assertEqual(strategy._readable(frames[:1], "OfflineReplay", "5m"), frames[:1])

    def test_a_daily_chart_is_not_cut_to_the_session(self):
        strategy = GhostTangentCrossingsV2Strategy({})
        daily = [Frame("2026-09-08T00:00:00Z", 100), Frame("2026-09-09T00:00:00Z", 101)]   # 05:30 IST stamps

        self.assertEqual(strategy._readable(daily, "OfflineReplay", "1D"), daily)


def found(direction: str, cross_bar: int = 10, b_price: float = 100.0) -> StrategySignal:
    """A crossing as the geometry returns one."""
    return StrategySignal(strategy_name="GhostTangentCrossingsV2", signal_type=direction,
                          timestamp_utc="", reason=f"Ghost Tangent Break (Ghost) detected at bar {cross_bar}",
                          metadata={"ghost": {"cross_bar": cross_bar, "b": {"bar": cross_bar - 2, "price": b_price}}})


class TradingTests(unittest.TestCase):
    def setUp(self):
        self.strategy = GhostTangentCrossingsV2Strategy({"lots": 2})
        self.state = self.strategy.initialize_state()
        self.stamps = session(10)

    def act(self, signals: List[StrategySignal], at: int = 0, **kwargs) -> List[StrategySignal]:
        newest = Frame(self.stamps[at], 100)
        out: List[StrategySignal] = []
        self.strategy._act(self.state, inp(newest, **kwargs), newest, signals, out)
        return out

    def stop(self, close: float, at: int, **kwargs) -> List[StrategySignal]:
        newest = Frame(self.stamps[at], close)
        out: List[StrategySignal] = []
        self.strategy._sync(self.state, inp(newest, **kwargs), newest)
        self.strategy._stop(self.state, inp(newest, **kwargs), newest, out)
        return out

    def test_a_signal_buys_the_atm_option_of_its_direction(self):
        out = self.act([found("BUY", b_price=99.0)])

        self.assertEqual([s.signal_type for s in out], ["OPEN_GROUP"])
        self.assertEqual(out[0].legs, [{"symbol": CE.symbol, "side": "BUY", "quantity": 2}])
        self.assertTrue(out[0].metadata["group_id"].startswith("GTC2_"))
        self.assertEqual(out[0].metadata["direction"], "BUY")
        self.assertEqual(out[0].metadata["ghost"]["cross_bar"], 10)   # the context travels with the order
        self.assertEqual(self.state["position"]["stop"], 99.0)

    def test_one_position_at_a_time(self):
        self.act([found("BUY", cross_bar=10)])

        out = self.act([found("BUY", cross_bar=12)], at=2)

        self.assertEqual(out, [])
        self.assertEqual(self.state["dropped"]["same_direction"], 1)

    def test_an_opposite_signal_reverses_the_position(self):
        first = self.act([found("BUY")])[0]

        out = self.act([found("SELL", cross_bar=14)], at=4)

        self.assertEqual([s.signal_type for s in out], ["CLOSE_GROUP", "OPEN_GROUP"])
        self.assertEqual(out[0].metadata["group_id"], first.metadata["group_id"])
        self.assertEqual(out[0].legs, [{"symbol": CE.symbol, "side": "SELL", "quantity": 2}])
        self.assertEqual(out[1].legs[0]["symbol"], PE.symbol)
        self.assertNotEqual(out[1].metadata["group_id"], first.metadata["group_id"])
        self.assertEqual(self.state["position"]["direction"], "SELL")

    def test_on_opposite_close_only_closes(self):
        self.strategy = GhostTangentCrossingsV2Strategy({"on_opposite": "close"})
        self.state = self.strategy.initialize_state()
        self.act([found("BUY")])

        out = self.act([found("SELL", cross_bar=14)], at=4)

        self.assertEqual([s.signal_type for s in out], ["CLOSE_GROUP"])
        self.assertIsNone(self.state["position"])

    def test_on_opposite_ignore_keeps_the_position(self):
        self.strategy = GhostTangentCrossingsV2Strategy({"on_opposite": "ignore"})
        self.state = self.strategy.initialize_state()
        self.act([found("BUY")])

        out = self.act([found("SELL", cross_bar=14)], at=4)

        self.assertEqual(out, [])
        self.assertEqual(self.state["position"]["direction"], "BUY")
        self.assertEqual(self.state["dropped"]["opposite_ignored"], 1)

    def test_a_close_below_the_pivot_stops_a_call(self):
        self.act([found("BUY", b_price=100.0)])

        out = self.stop(99.5, at=1)

        self.assertEqual([s.signal_type for s in out], ["CLOSE_GROUP"])
        self.assertIn("below the leg's later pivot 100", out[0].reason)
        self.assertIsNone(self.state["position"])

    def test_a_close_above_the_pivot_stops_a_put(self):
        self.act([found("SELL", b_price=200.0)])

        self.assertEqual([s.signal_type for s in self.stop(200.5, at=1)], ["CLOSE_GROUP"])

    def test_a_close_inside_the_pivot_keeps_it(self):
        self.act([found("BUY", b_price=100.0)])

        self.assertEqual(self.stop(100.5, at=1), [])

    def test_the_entry_candle_never_stops_its_own_position(self):
        self.act([found("BUY", b_price=100.0)], at=3)

        self.assertEqual(self.stop(90.0, at=3), [])

    def test_stop_at_pivot_off_keeps_it(self):
        self.strategy = GhostTangentCrossingsV2Strategy({"stop_at_pivot": False})
        self.state = self.strategy.initialize_state()
        self.act([found("BUY", b_price=100.0)])

        self.assertEqual(self.stop(90.0, at=1), [])

    def test_a_position_the_replay_closed_is_forgotten(self):
        self.act([found("BUY")])

        self.stop(100.5, at=1, open_groups=[])      # a run rule or the square-off closed it

        self.assertIsNone(self.state["position"])
        self.assertEqual([s.signal_type for s in self.act([found("BUY", cross_bar=12)], at=2)], ["OPEN_GROUP"])

    def test_live_forgets_a_position_on_a_new_session(self):
        self.act([found("BUY")], mode="LivePaper")
        tomorrow = Frame(stamp(date(2026, 9, 10), 9, 15), 100.5)

        self.strategy._sync(self.state, inp(tomorrow, mode="LivePaper"), tomorrow)

        self.assertIsNone(self.state["position"])

    def test_no_contract_no_position(self):
        out = self.act([found("BUY")], contracts={})

        self.assertEqual(out, [])
        self.assertIsNone(self.state["position"])
        self.assertEqual(self.state["dropped"]["no_contract"], 1)

    def test_one_candle_crossing_both_lines_trades_nothing(self):
        out = self.act([found("SELL", cross_bar=10), found("BUY", cross_bar=10)])

        self.assertEqual(out, [])
        self.assertEqual(self.state["dropped"]["conflict"], 1)


def zigzag(n: int) -> float:
    """A triangle wave, period 24 bars, +-40 around 57600, so the engine's ATM strike stays 57600."""
    k = n % 24
    return 57600 + (-40 + 80 * k / 12 if k < 12 else 40 - 80 * (k - 12) / 12) + (n % 5) * 0.7


def candle_rows(symbol: str, days: List[date], price) -> List[Dict[str, Any]]:
    rows = []
    for d, day in enumerate(days):
        for i, start in enumerate(session_bars(day)):
            close = price(d * 75 + i)
            rows.append({"symbol": symbol, "resolution": "5", "timestampUtc": iso_utc(start),
                         "open": close - 1, "high": close + 2, "low": close - 2, "close": close, "volume": 0})
    return rows


class ReplayTests(unittest.TestCase):
    def replay(self, **params):
        days = [DAY1, DAY2]
        api = FakeApi({
            (SPOT, "5"): candle_rows(SPOT, days, zigzag),
            (ENGINE_CE, "5"): candle_rows(ENGINE_CE, days, lambda n: 100 + (zigzag(n) - 57600) * 0.5),
            (ENGINE_PE, "5"): candle_rows(ENGINE_PE, days, lambda n: 100 - (zigzag(n) - 57600) * 0.5),
        })
        row = run_row(DAY1, DAY2, pivot_forward=3, **params)
        row["strategyName"] = "GhostTangentCrossingsV2"
        outcome = run_backtest(api, row, load_strategy_factories()["GhostTangentCrossingsV2"], log=quiet,
                               progress_interval=0.0)
        return outcome, api

    def test_a_replay_through_the_engine_holds_one_position_at_a_time(self):
        outcome, api = self.replay()

        self.assertEqual(outcome.status, "Completed")
        opens = [s for s in api.signals if s["signalType"] == "OPEN_GROUP"]
        self.assertGreater(len(opens), 2)
        held = set()
        for signal in api.signals:
            group = signal["groupId"]
            if signal["signalType"] == "OPEN_GROUP":
                self.assertEqual(held, set(), f"{group} opened while {held} was held")
                self.assertTrue(group.startswith("GTC2_"))
                ghost = json.loads(signal["metadataJson"])["ghost"]
                self.assertLessEqual(ghost["signal_age_bars"], 1)
                held.add(group)
            else:
                self.assertIn(group, held, f"{group} closed but was not open")
                held.discard(group)
        # The only entries the engine refused are the ones after its 15:15 square-off.
        for skipped in outcome.summary["skippedEntries"]:
            self.assertIn("EOD square-off", json.dumps(skipped))

    def test_the_geometry_is_the_originals(self):
        """With every gate off, both find the same crossings on the same candles."""
        original = GhostTangentCrossingsStrategy({"pivot_forward": 3})
        fixed = GhostTangentCrossingsV2Strategy({"pivot_forward": 3, **EVERY_GATE_OFF})
        s1, s2 = original.initialize_state(), fixed.initialize_state()
        frames = [Frame(s, zigzag(i), high=zigzag(i) + 2, low=zigzag(i) - 2) for i, s in enumerate(session(75))]
        crossings_1, crossings_2 = [], []
        for i in range(1, len(frames) + 1):
            step = inp(frames[i - 1], bars=frames[:i])
            crossings_1 += [(i, s.signal_type, s.reason) for s in original.on_bar(s1, step)]
            # The parent's on_bar with the V2 geometry: the crossings before any position rule.
            crossings_2 += [(i, s.signal_type, s.reason)
                            for s in GhostTangentCrossingsStrategy.on_bar(fixed, s2, step)]

        self.assertGreater(len(crossings_1), 2)
        self.assertEqual(crossings_1, crossings_2)

    def test_the_state_survives_a_save_and_reload(self):
        frames = [Frame(s, zigzag(i), high=zigzag(i) + 2, low=zigzag(i) - 2) for i, s in enumerate(session(75))]

        def run(reload_at: Optional[int]) -> List[tuple]:
            strategy = GhostTangentCrossingsV2Strategy({"pivot_forward": 3})
            state, fired, open_groups = strategy.initialize_state(), [], []
            for i in range(1, len(frames) + 1):
                if i == reload_at:
                    state = strategy.state_from_json(json.loads(json.dumps(strategy.state_to_json(state))))
                for s in strategy.on_bar(state, inp(frames[i - 1], bars=frames[:i], open_groups=list(open_groups))):
                    group = s.metadata["group_id"]
                    (open_groups.append if s.signal_type == "OPEN_GROUP" else open_groups.remove)(group)
                    fired.append((i, s.signal_type, group))
            return fired

        straight = run(None)
        self.assertGreater(len(straight), 2)
        self.assertEqual(run(40), straight)


if __name__ == "__main__":
    unittest.main()
