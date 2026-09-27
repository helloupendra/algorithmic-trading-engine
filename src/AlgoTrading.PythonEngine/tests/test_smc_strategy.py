"""
SmcStructureBreak: what it trades, when it stays out, and what it does when
something else closes its position.

The candles are the schematic from tests/test_market_structure.py, so the
structure behind every signal here is the one that test pins.
"""

import json
import unittest
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List

import _bootstrap  # noqa: F401

from strategies.base_strategy import OptionContract, StrategyInput
from strategies.directional.smc_structure_break import SmcStructureBreakStrategy
from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.market_structure import BOS, Bar, MarketStructure, stamp_key
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


def saved_and_reloaded(strategy: Any, state: Dict[str, Any]) -> Dict[str, Any]:
    """The state as a live runner saves it to Redis and a restarted one reads it back."""
    return strategy.state_from_json(json.loads(json.dumps(strategy.state_to_json(state))))


def run(strategy: SmcStructureBreakStrategy, bars=None, mode="OfflineReplay",
        open_groups_from=None, reload_at=None) -> List[Dict[str, Any]]:
    """
    Steps the strategy through the candles one at a time, as the engine does,
    and returns every signal with the index of the candle it fired on. With
    ``reload_at``, the state goes through a save and a reload before that step.
    """
    frames = [Frame(b) for b in (bars or schematic())]
    state = strategy.initialize_state()
    fired: List[Dict[str, Any]] = []
    open_groups: List[str] = []
    for i in range(1, len(frames) + 1):
        if i == reload_at:
            state = saved_and_reloaded(strategy, state)
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


class BiasTests(unittest.TestCase):
    """The higher timeframe as a gate, read by a second reader."""

    def bias_bars(self, kind):
        """A daily series whose structure is bullish, bearish, or not yet set."""
        if kind == "bullish":
            rows = [(100, 105, 99, 104), (104, 110, 103, 109), (109, 109, 100, 101),
                    (101, 108, 100, 107), (107, 115, 106, 114)]
        elif kind == "bearish":
            rows = [(110, 112, 105, 106), (106, 108, 104, 105), (105, 106, 98, 99),
                    (99, 104, 98, 103), (103, 107, 100, 101), (101, 102, 95, 96)]
        else:
            rows = [(100, 101, 99, 100)]
        return [Frame(b) for b in bars(*rows)]

    def run_with_bias(self, params, kind):
        frames = [Frame(b) for b in schematic()]
        daily = self.bias_bars(kind)
        strategy = SmcStructureBreakStrategy(params)
        state = strategy.initialize_state()
        opened = []
        for i in range(1, len(frames) + 1):
            visible = frames[:i]
            inp = StrategyInput(mode="OfflineReplay", timestamp_utc=visible[-1].timestamp_utc,
                                underlying=UNDERLYING, spot_price=visible[-1].close, atm_strike=25000,
                                strike_step=50, lot_size=75, contracts={"atm_ce": CE, "atm_pe": PE},
                                bars={"5m": {"index": visible}, "1D": {"index": daily}},
                                metadata={"resolution": "5m"})
            for signal in strategy.on_bar(state, inp) or []:
                if signal.signal_type == "OPEN_GROUP":
                    opened.append(signal.reason)
        return opened

    def test_with_the_higher_timeframe_takes_only_breaks_it_agrees_with(self):
        # The schematic's first break is bullish.
        self.assertEqual(len(self.run_with_bias({"bias": "with"}, "bullish")), 1)
        self.assertEqual(self.run_with_bias({"bias": "with"}, "bearish"), [])

    def test_against_the_higher_timeframe_takes_only_the_ones_it_disagrees_with(self):
        self.assertEqual(len(self.run_with_bias({"bias": "against"}, "bearish")), 1)
        self.assertEqual(self.run_with_bias({"bias": "against"}, "bullish"), [])

    def test_a_higher_timeframe_with_no_structure_yet_blocks_nothing_and_allows_nothing(self):
        # "with" needs agreement, "against" needs disagreement; neither is true
        # of a timeframe that has not set a trend.
        self.assertEqual(self.run_with_bias({"bias": "with"}, "none"), [])
        self.assertEqual(self.run_with_bias({"bias": "against"}, "none"), [])

    def test_the_reason_says_what_the_higher_timeframe_was_doing(self):
        reasons = self.run_with_bias({"bias": "with"}, "bullish")

        self.assertIn("with the 1D structure (bullish)", reasons[0])

    def test_it_asks_the_engine_for_the_bias_candles(self):
        # The runners ask the strategy they are about to run, so the second feed
        # can depend on that run's parameters.
        wanted = {r.resolution for r in SmcStructureBreakStrategy({"bias": "with"}).get_data_requirements()}
        self.assertEqual(wanted, {"5m", "1D"})
        self.assertEqual({r.resolution for r in SmcStructureBreakStrategy().get_data_requirements()}, {"5m"})


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


class LiveWindowTests(unittest.TestCase):
    """
    The live runner hands a strategy a window capped at 500 bars that slides,
    with the forming candle last. Fed by position, the reader stopped at 499 and
    read nothing for the rest of the day (22–25 Sep: fifteen runs, no orders).
    """

    def _step_live(self, strategy, frames, window=500):
        state = strategy.initialize_state()
        for i in range(2, len(frames) + 1):
            visible = frames[max(0, i - (window + 1)):i]   # the last one is the forming candle
            inp = StrategyInput(mode="LivePaper", timestamp_utc=visible[-1].timestamp_utc, underlying=UNDERLYING,
                                spot_price=visible[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                                contracts={"atm_ce": CE, "atm_pe": PE},
                                bars={"5m": {"index": visible}}, metadata={"resolution": "5m"})
            strategy.on_bar(state, inp)
        return state

    def test_the_reader_keeps_reading_after_the_window_stops_growing(self):
        rows = [(100 + (i % 7), 101 + (i % 7), 99 + (i % 7), 100 + (i % 5)) for i in range(640)]
        frames = [Frame(b) for b in bars(*rows)]
        state = self._step_live(SmcStructureBreakStrategy(), frames)

        # Every candle but the forming one was read: 639, not the old 500.
        self.assertEqual(639, state["fed"])
        self.assertEqual(state["structure_candles"], state["fed"])
        self.assertTrue(state["seen"].startswith(frames[-2].timestamp_utc[:16]))

    def test_the_same_candle_twice_is_read_once(self):
        frames = [Frame(b) for b in schematic()]
        strategy = SmcStructureBreakStrategy()
        state = strategy.initialize_state()
        for _ in range(3):  # the same window handed over three times, as between two ticks
            inp = StrategyInput(mode="LivePaper", timestamp_utc=frames[-1].timestamp_utc, underlying=UNDERLYING,
                                spot_price=frames[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                                contracts={"atm_ce": CE, "atm_pe": PE},
                                bars={"5m": {"index": frames}}, metadata={"resolution": "5m"})
            strategy.on_bar(state, inp)
        self.assertEqual(len(frames) - 1, state["fed"])

    def test_live_and_replay_see_the_same_breaks(self):
        """A break in a long live day is traded, as the replay of the same candles trades it."""
        warmup = [(100, 100.5, 99.5, 100)] * 520
        frames = [Frame(b) for b in bars(*(warmup + [(b.open, b.high, b.low, b.close) for b in schematic()]))]
        strategy = SmcStructureBreakStrategy()
        state = strategy.initialize_state()
        opened = 0
        for i in range(2, len(frames) + 2):
            visible = frames[max(0, i - 501):i] + ([frames[-1]] if i > len(frames) else [])
            visible = visible[-501:]
            inp = StrategyInput(mode="LivePaper", timestamp_utc=visible[-1].timestamp_utc, underlying=UNDERLYING,
                                spot_price=visible[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                                contracts={"atm_ce": CE, "atm_pe": PE},
                                bars={"5m": {"index": visible}}, metadata={"resolution": "5m"})
            opened += sum(1 for sig in (strategy.on_bar(state, inp) or []) if sig.signal_type == "OPEN_GROUP")
        replay = [f for f in run(SmcStructureBreakStrategy()) if f["type"] == "OPEN_GROUP"]
        self.assertGreaterEqual(len(replay), 1)
        self.assertGreaterEqual(opened, 1, "the live window never traded the break the replay trades")


def rows(candles) -> list:
    return [(b.open, b.high, b.low, b.close) for b in candles]


def at(first_utc: datetime, candle_rows) -> List[Frame]:
    """Frames five minutes apart, the first one starting at first_utc."""
    return [Frame(Bar((first_utc + timedelta(minutes=5 * i)).isoformat().replace("+00:00", "Z"), o, h, l, c))
            for i, (o, h, l, c) in enumerate(candle_rows)]


def live_tick(strategy, state, window, when=None, contracts=True) -> List[Any]:
    """One call as the live runner makes it: the window ends with the forming candle."""
    forming = window[-1].timestamp_utc
    inp = StrategyInput(mode="LivePaper", timestamp_utc=when or forming, underlying=UNDERLYING,
                        spot_price=window[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                        contracts={"atm_ce": CE, "atm_pe": PE} if contracts else {},
                        bars={"5m": {"index": window}}, metadata={"source": "live-api"})
    return strategy.on_bar(state, inp) or []


def warm_up(strategy, history) -> Dict[str, Any]:
    """execution_runner's warm-up: the stored candles one at a time, no contracts, the last one held back."""
    state = strategy.initialize_state()
    for i in range(1, len(history) + 1):
        live_tick(strategy, state, history[:i], contracts=False)
    return state


def step_live(strategy, frames, ticks=1, state=None) -> List[Dict[str, Any]]:
    """
    Every candle in turn as the forming one, `ticks` calls each, a few seconds
    apart inside it. Returns each signal with the index of the forming candle
    and which tick of it fired.
    """
    state = state if state is not None else strategy.initialize_state()
    fired = []
    for j in range(1, len(frames)):
        start = stamp_key(frames[j].timestamp_utc)
        for t in range(ticks):
            when = (start + timedelta(seconds=2 + 5 * t)).isoformat()
            for signal in live_tick(strategy, state, frames[:j + 1], when=when):
                fired.append({"forming": j, "tick": t, "type": signal.signal_type,
                              "symbol": signal.legs[0]["symbol"], "reason": signal.reason})
    return fired


class LateReadTests(unittest.TestCase):
    """
    One call can hand the reader many candles, and only a break on the newest,
    read in its own session, may open a position. Live warm-up holds back the
    last stored candle, so yesterday's 15:25 break was first read at today's
    09:15 tick and bought then; a run started after the open read the whole
    morning in one call and bought its oldest break, even after the market had
    turned (27 Sep review: 3 of 40 synthetic 08:45 starts, 16 of 40 at 11:00).
    """

    TODAY_0915 = datetime(2026, 9, 18, 3, 45, tzinfo=timezone.utc)

    def test_a_break_on_yesterdays_last_candle_is_not_bought_at_todays_open(self):
        # The schematic up to its first break, placed so that the breaking candle
        # is yesterday's 15:25 IST — the one warm-up holds back.
        yesterday = at(datetime(2026, 9, 17, 9, 10, tzinfo=timezone.utc), rows(schematic()[:10]))
        today = at(self.TODAY_0915, [(110, 111, 109.5, 110.5)])
        strategy = SmcStructureBreakStrategy()
        state = warm_up(strategy, yesterday)

        fired = live_tick(strategy, state, yesterday + today, when="2026-09-18T03:45:02+00:00")

        self.assertEqual([s.signal_type for s in fired if s.signal_type == "OPEN_GROUP"], [])
        # The break was read all the same: the structure knows it.
        last = state["reader"].last_event
        self.assertEqual((last.kind, last.level), (BOS, 109))

    def test_a_late_start_buys_the_break_on_the_newest_candle_not_the_mornings_first(self):
        # A run started at 10:55: the morning's candles arrive in one call. Bar 9
        # broke 109 at 10:00; bar 19, the newest closed candle, broke 114.
        morning = at(self.TODAY_0915, rows(schematic()[:21]))
        strategy = SmcStructureBreakStrategy()
        state = strategy.initialize_state()

        opens = [s for s in live_tick(strategy, state, morning) if s.signal_type == "OPEN_GROUP"]

        self.assertEqual(len(opens), 1)
        self.assertIn("through 114", opens[0].reason)

    def test_a_late_start_does_not_buy_a_morning_break_the_market_has_turned_from(self):
        # Trading both kinds of break, the morning went up (bars 9, 19) and then
        # turned down (bar 23, the newest). The old code bought the call on bar 9.
        morning = at(self.TODAY_0915, rows(schematic()) + [(102, 103, 100, 101)])
        strategy = SmcStructureBreakStrategy({"trade": "both"})
        state = strategy.initialize_state()

        opens = [s for s in live_tick(strategy, state, morning) if s.signal_type == "OPEN_GROUP"]

        self.assertEqual([s.legs[0]["symbol"] for s in opens], [PE.symbol])

    def test_a_late_start_whose_newest_candle_broke_nothing_buys_nothing(self):
        morning = at(self.TODAY_0915, rows(schematic()[:23]))   # bars 0-21 closed, 22 forming
        strategy = SmcStructureBreakStrategy()
        state = strategy.initialize_state()

        self.assertEqual(live_tick(strategy, state, morning), [])
        self.assertEqual(len(state["reader"].events), 2)        # both of the morning's breaks were read

    def test_one_candle_per_tick_still_buys_each_break_as_it_closes(self):
        # The ordinary live day: three ticks a candle. Each break is bought once,
        # on the first tick after its candle closed — the call on bar 9, and the
        # put on the change of character at bar 23, which also closes the call.
        frames = at(self.TODAY_0915, rows(schematic()) + [(102, 103, 100, 101)])

        fired = step_live(SmcStructureBreakStrategy({"trade": "both"}), frames, ticks=3)

        opens = [(f["forming"], f["tick"], f["symbol"]) for f in fired if f["type"] == "OPEN_GROUP"]
        self.assertEqual(opens, [(10, 0, CE.symbol), (24, 0, PE.symbol)])
        self.assertEqual([(f["forming"], f["tick"]) for f in fired if f["type"] == "CLOSE_GROUP"], [(24, 0)])

    def test_an_old_turn_in_a_batch_still_closes_the_position(self):
        # The call is bought on bar 9; then the runner misses ticks for an hour
        # and one call reads bars 10-25. The turn at bar 23 is not the newest
        # candle, so it opens nothing — but it is still a turn against the call.
        frames = at(self.TODAY_0915, rows(schematic()) + [(102, 103, 100, 101), (101, 102, 99, 100),
                                                          (100, 101, 98, 99)])
        strategy = SmcStructureBreakStrategy({"trade": "both"})
        state = strategy.initialize_state()
        self.assertEqual([f["type"] for f in step_live(strategy, frames[:11], state=state)], ["OPEN_GROUP"])

        fired = live_tick(strategy, state, frames)

        self.assertEqual([s.signal_type for s in fired], ["CLOSE_GROUP"])
        self.assertIn("turned bearish", fired[0].reason)


class LiveRetestTests(unittest.TestCase):
    """
    entry="retest" live. on_bar runs on every tick; the retest used to be judged
    on each of them, against the breaking candle itself, and counted in ticks.
    """

    def test_the_breaking_candles_own_wick_is_not_the_retest(self):
        # Bar 9 closes through 109 with a low of 105, so its own wick is below
        # the level. The retest is bar 10 coming back to 109, as in the replay.
        frames = [Frame(b) for b in schematic()]

        fired = step_live(SmcStructureBreakStrategy({"entry": "retest", "retest_bars": 8}), frames, ticks=3)

        opens = [(f["forming"], f["tick"]) for f in fired if f["type"] == "OPEN_GROUP"]
        self.assertEqual(opens, [(11, 0)])                      # first tick after bar 10 closed
        self.assertIn("retest of 109", fired[0]["reason"])

    def test_the_retest_waits_candles_not_ticks(self):
        # The break at bar 9 leaves its low above 109; bar 10 stays up; bar 11
        # comes back. With retest_bars 2 that is in time, however many ticks
        # each candle had.
        candles = bars(*(rows(schematic()[:9]) + [(106, 111, 109.5, 110), (110, 113, 110, 112),
                                                  (112, 113, 108.5, 109), (109, 110, 108, 109)]))
        replayed = run(SmcStructureBreakStrategy({"entry": "retest", "retest_bars": 2}), bars=candles)
        self.assertEqual([f["bar"] for f in replayed if f["type"] == "OPEN_GROUP"], [11])

        fired = step_live(SmcStructureBreakStrategy({"entry": "retest", "retest_bars": 2}),
                          [Frame(b) for b in candles], ticks=4)

        self.assertEqual([(f["forming"], f["tick"]) for f in fired if f["type"] == "OPEN_GROUP"], [(12, 0)])


class UnreadableTimeTests(unittest.TestCase):
    """
    A candle whose time cannot be read is never fed. The live runner stamps a
    frame with str(row.get("barStartUtc", "")), so an API that renamed the field
    would hand over a window with no readable time at all; with nothing ever
    recorded as seen, the whole window was fed again on every tick.
    """

    def window(self):
        frames = [Frame(b) for b in schematic()]
        for frame in frames:
            frame.timestamp_utc = ""
        return frames

    def test_the_strategy_reads_nothing_rather_than_the_window_again(self):
        strategy = SmcStructureBreakStrategy()
        state = strategy.initialize_state()
        fired = []
        for _ in range(3):
            fired += live_tick(strategy, state, self.window(), when="2026-09-18T05:45:02Z")

        self.assertEqual(fired, [])
        self.assertEqual(state["fed"], 0)
        self.assertEqual(state["reader"].bars, [])

    def test_ghosts_structure_filter_reads_nothing_rather_than_the_window_again(self):
        strategy = GhostTangentCrossingsStrategy({"smc_filter": "with"})
        state = strategy.initialize_state()
        for _ in range(3):
            inp = StrategyInput(mode="LivePaper", timestamp_utc="2026-09-18T05:45:02Z", underlying=UNDERLYING,
                                spot_price=100, atm_strike=25000, strike_step=50, lot_size=75,
                                contracts={"atm_ce": CE, "atm_pe": PE}, bars={"5m": {"index": self.window()}},
                                metadata={"source": "live-api"})
            strategy.on_bar(state, inp)

        self.assertEqual(state["smc_fed"], 0)
        self.assertEqual(state["smc"].bars, [])


class SavedStateTests(unittest.TestCase):
    """
    A live runner saves the strategy's state to Redis as JSON. Until 28 Sep the
    structure reader went in as its repr and came back a string, and every tick
    after a reload raised AttributeError.
    """

    def test_state_round_trip_through_json(self):
        # A restart before the entry, between the entry and the exit, and after
        # both: the reloaded strategy trades exactly as the one that never stopped.
        straight = run(SmcStructureBreakStrategy())
        self.assertTrue(straight)
        for reload_at in (5, 12, 20, 24):
            with self.subTest(reload_at=reload_at):
                self.assertEqual(run(SmcStructureBreakStrategy(), reload_at=reload_at), straight)

    def test_the_saved_state_is_plain_json_and_reads_back_the_same_structure(self):
        strategy = SmcStructureBreakStrategy({"bias": "with", "inducement": "first"})
        state = strategy.initialize_state()
        for i in range(1, 16):
            frames = [Frame(b) for b in schematic()[:i]]
            strategy.on_bar(state, StrategyInput(
                mode="OfflineReplay", timestamp_utc=frames[-1].timestamp_utc, underlying=UNDERLYING,
                spot_price=frames[-1].close, atm_strike=25000, strike_step=50, lot_size=75,
                contracts={"atm_ce": CE, "atm_pe": PE}, bars={"5m": {"index": frames}, "1D": {"index": frames}},
                metadata={"source": "backtest", "resolution": "5m"}))

        text = json.dumps(strategy.state_to_json(state))    # no default=: nothing may need one
        back = strategy.state_from_json(json.loads(text))

        for key in ("reader", "bias"):
            self.assertIsInstance(back[key], MarketStructure)
            self.assertEqual(back[key].inducement_mode, "first")
            self.assertEqual(back[key].bars, state[key].bars)
            self.assertEqual(back[key].swings, state[key].swings)
            self.assertEqual(back[key].events, state[key].events)
            self.assertEqual(back[key].describe(), state[key].describe())
        self.assertIsInstance(state["reader"], MarketStructure, "saving leaves the live state alone")

    def test_a_reader_saved_as_text_is_not_a_state_to_go_on_from(self):
        strategy = SmcStructureBreakStrategy()
        before_the_fix = json.loads(json.dumps(strategy.initialize_state(), default=str))

        self.assertIsInstance(before_the_fix["reader"], str)
        self.assertIsNone(strategy.state_from_json(before_the_fix))

    def test_ghosts_structure_filter_survives_a_save_and_reload(self):
        strategy = GhostTangentCrossingsStrategy({"smc_filter": "with"})
        state = strategy.initialize_state()
        frames = [Frame(b) for b in schematic()]
        strategy._read_structure(state, frames, "OfflineReplay")

        back = saved_and_reloaded(strategy, state)

        self.assertIsInstance(back["smc"], MarketStructure)
        self.assertEqual(back["smc"].describe(), state["smc"].describe())
        self.assertEqual(back["smc_seen"], state["smc_seen"])
        self.assertIsNone(strategy.state_from_json(json.loads(json.dumps(strategy.initialize_state(), default=str))))


if __name__ == "__main__":
    unittest.main()
