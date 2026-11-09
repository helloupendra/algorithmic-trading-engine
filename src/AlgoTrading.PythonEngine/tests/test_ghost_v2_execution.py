"""V2 safety across live candles, restarts, external exits and failed bookings."""

import json
import unittest
from dataclasses import replace
from datetime import date, datetime, timedelta
from unittest.mock import patch

import _bootstrap  # noqa: F401

from strategies.ghost_tangent_crossings import GhostTangentCrossingsStrategy
from strategies.ghost_tangent_crossings_v2 import GhostTangentCrossingsV2Strategy as Strategy
from strategies.position_snapshot import PositionSnapshot
from strategies.signal_booking import Booking, run_tick
from test_ghost_v2 import CE, Frame, evaluate, found, inp, session, stamp, up_leg_candles, zigzag


def live(frame, bars=None, **kwargs):
    step = inp(frame, mode="LivePaper", bars=bars, open_groups=[], **kwargs)
    return replace(step, timestamp_utc=(datetime.fromisoformat(frame.timestamp_utc.replace("Z", "+00:00"))
                                       + timedelta(minutes=5)).isoformat())


class ParameterTests(unittest.TestCase):
    def test_invalid_config_is_rejected_before_any_order(self):
        for params in ({"pivot_forward": 0}, {"pivot_forward": 251}, {"pivot_forward": 1.5},
                       {"max_signal_age_bars": -1}, {"min_leg_bars": -2}, {"line_life": float("nan")},
                       {"line_life": -1}, {"lots": 0}, {"lots": 1.5}, {"on_opposite": "typo"},
                       {"stop_at_pivot": "no"}, {"pivot_type": "typo"}, {"smc_filter": "typo"},
                       {"timeframe": "0m"}, {"contract": {"flip": True}}, {"contract": {"strike_offset": 2}}):
            with self.subTest(params=params), self.assertRaises(ValueError):
                Strategy(params)

    def test_string_false_does_not_enable_risk_parameter(self):
        strategy = Strategy({"stop_at_pivot": "false", "use_ghost_signals": "false"})
        self.assertFalse(strategy.stop_at_pivot)
        self.assertFalse(strategy.use_ghost_signals)

    def test_configured_timeframe_is_requested_live_and_catalogue_has_default(self):
        self.assertEqual(Strategy({"timeframe": "15m"}).get_data_requirements()[0].resolution, "15m")
        self.assertEqual(Strategy({"timeframe": "D"}).get_data_requirements()[0].resolution, "1D")
        self.assertEqual(Strategy.get_data_requirements()[0].resolution, "5m")


class CandleTests(unittest.TestCase):
    def setUp(self):
        self.strategy = Strategy({"pivot_forward": 3})
        self.frames = [Frame(s, zigzag(i), high=zigzag(i) + 2, low=zigzag(i) - 2)
                       for i, s in enumerate(session(75))]

    def test_last_candle_already_closed_is_not_discarded(self):
        step = live(self.frames[2], bars=self.frames[:3])
        self.assertEqual(self.strategy._readable(self.frames[:3], step.mode, "5m", step.timestamp_utc), self.frames[:3])

    def test_forming_future_and_unreadable_candles_never_enter_geometry(self):
        bars = [*self.frames[:4], Frame("bad", 100), Frame(session(1)[0], float("nan"))]
        step = live(self.frames[1])
        self.assertEqual(self.strategy._readable(bars, step.mode, "5m", step.timestamp_utc), self.frames[:2])

    def test_warmup_reads_latest_closed_bar_and_emits_no_order(self):
        state = self.strategy.initialize_state()
        step = live(self.frames[39], bars=self.frames[:40])
        step.metadata["source"] = "warmup"
        self.assertEqual(self.strategy.on_bar(state, step), [])
        self.assertEqual(state["bar_index"], 40)
        self.assertIsNone(state["position"])
        self.assertEqual(state["opened"], 0)

    def test_daily_bar_is_closed_at_cash_close_not_midnight(self):
        frame = Frame("2026-09-09T00:00:00Z", 100)
        self.assertEqual(self.strategy._readable([frame], "LivePaper", "1D", "2026-09-09T09:59:00Z"), [])
        self.assertEqual(self.strategy._readable([frame], "LivePaper", "1D", "2026-09-09T10:00:00Z"), [frame])

    def test_reconnect_and_rolling_windows_have_same_pivots_as_every_bar(self):
        def read(batches, reload=False):
            strategy, state = Strategy({"pivot_forward": 3}), self.strategy.initialize_state()
            for index in batches:
                if reload:
                    strategy = Strategy({"pivot_forward": 3})
                    state = strategy.state_from_json(json.loads(json.dumps(strategy.state_to_json(state))))
                step = live(self.frames[index], bars=self.frames[max(0, index - 19):index + 1])
                step.metadata["source"] = "warmup"
                strategy.on_bar(state, step)
            return state
        expected = read(range(75))
        actual = read([19, 29, 44, 59, 74], reload=True)
        for key in ("bar_index", "ph_current_idx", "pl_current_idx", "last_high", "last_low", "ph_back", "pl_back"):
            self.assertEqual(actual[key], expected[key], key)
        self.assertEqual(actual["bar_index"], 75)

    def test_repeated_and_out_of_order_ticks_do_not_move_bar_counter_backwards(self):
        state = self.strategy.initialize_state()
        self.strategy.on_bar(state, live(self.frames[10], bars=self.frames[:11]))
        last = state["last_processed_bar_time"]
        for index in (10, 9, 8, 10):
            self.strategy.on_bar(state, live(self.frames[index], bars=self.frames[:index + 1]))
        self.assertEqual(state["bar_index"], 11)
        self.assertEqual(state["last_processed_bar_time"], last)

    def test_old_checkpoint_rebuilds_geometry_without_forgetting_exposure(self):
        state = self.strategy.initialize_state()
        state.pop("reading_version")
        held = {"group_id": "held", "direction": "BUY", "symbol": CE.symbol,
                "since": self.frames[30].timestamp_utc, "stop": None}
        state.update(position=held.copy(), bar_index=9000, ph_current_idx=8999)
        step = live(self.frames[30], bars=self.frames[:31])
        step.metadata["open_groups"] = ["held"]
        self.assertEqual(self.strategy.on_bar(state, step), [])
        self.assertEqual(state["position"], held)
        self.assertEqual(state["bar_index"], 31)

    def test_missing_entire_window_rebuilds_instead_of_compressing_the_gap(self):
        state = self.strategy.initialize_state()
        first = live(self.frames[10], bars=self.frames[:11])
        first.metadata["source"] = "warmup"
        self.strategy.on_bar(state, first)
        self.assertEqual(self.strategy.on_bar(state, live(self.frames[60], bars=self.frames[40:61])), [])
        self.assertEqual(state["bar_index"], 21)

    def test_stop_during_missed_bars_cannot_reopen_on_same_tick(self):
        strategy, state = Strategy(), Strategy().initialize_state()
        frames = [Frame(s, price) for s, price in zip(session(3), (100, 90, 110))]
        strategy._act(state, inp(frames[0]), frames[0], [found("BUY", b_price=99)], [])
        with patch.object(GhostTangentCrossingsStrategy, "on_bar", return_value=[found("BUY", cross_bar=12, b_price=80)]):
            signals = strategy.on_bar(state, inp(frames[-1], bars=frames, open_groups=[state["position"]["group_id"]]))
        self.assertEqual([s.signal_type for s in signals], ["CLOSE_GROUP"])
        self.assertEqual(state["pending"], [])

    def test_live_forming_tail_and_replay_emit_the_same_decisions(self):
        def read(mode):
            strategy, state, groups, decisions = Strategy({"pivot_forward": 3}), self.strategy.initialize_state(), [], []
            for index, frame in enumerate(self.frames):
                if mode == "LivePaper":
                    step = live(frame, bars=self.frames[max(0, index - 19):index + 2])
                    step.metadata["open_groups"] = list(groups)
                else:
                    step = inp(frame, bars=self.frames[:index + 1], open_groups=list(groups))
                if index < 25:
                    step.metadata["source"] = "warmup"
                if index == 40:
                    strategy = Strategy({"pivot_forward": 3})
                    state = strategy.state_from_json(json.loads(json.dumps(strategy.state_to_json(state))))
                for signal in strategy.on_bar(state, step):
                    group = signal.metadata["group_id"]
                    (groups.append if signal.signal_type == "OPEN_GROUP" else groups.remove)(group)
                    decisions.append((index, signal.signal_type, signal.legs, signal.metadata.get("ghost", {}).get("cross_time")))
            return decisions
        replay = read("OfflineReplay")
        self.assertGreater(len(replay), 2)
        self.assertEqual(read("LivePaper"), replay)

    def test_persisted_buffer_is_bounded(self):
        state, growing = self.strategy.initialize_state(), []
        for day_index in range(15):
            day = date(2026, 8, 1) + timedelta(days=day_index)
            for at in session(75, day):
                frame = Frame(at, 100)
                growing.append(frame)
                step = inp(frame, bars=growing)
                step.metadata["source"] = "warmup"
                self.strategy.on_bar(state, step)
        self.assertEqual(len(state["history"]), 1024)
        self.assertEqual(state["bar_index"], 1125)


class EntryTests(unittest.TestCase):
    def setUp(self):
        self.strategy = Strategy()
        self.state = self.strategy.initialize_state()
        self.frame = Frame(session(1)[0], 100)

    def act(self, step, sig=None):
        out = []
        self.strategy._act(self.state, step, self.frame, [sig or found("BUY", b_price=99)], out)
        return out

    def test_unavailable_book_and_untracked_exposure_block_new_live_entries(self):
        for groups in (None, ["existing-after-lost-checkpoint"]):
            with self.subTest(groups=groups):
                step = live(self.frame)
                step.metadata["open_groups"] = groups
                self.assertEqual(self.act(step), [])
                self.assertIsNone(self.state["position"])

    def test_external_risk_exit_clears_known_position_without_waiting_for_tomorrow(self):
        self.act(live(self.frame))
        self.assertIsNotNone(self.state["position"])
        self.strategy._sync(self.state, live(self.frame), self.frame)
        self.assertIsNone(self.state["position"])

    def test_stale_candles_cannot_open_on_a_fresh_live_tick(self):
        step = replace(live(self.frame), timestamp_utc=stamp(date(2026, 9, 9), 10, 0))
        self.assertEqual(self.act(step), [])
        self.assertEqual(self.state["dropped"]["stale_data"], 1)

    def test_out_of_order_tick_cannot_trade_a_candle_that_closed_later(self):
        step = replace(live(self.frame), timestamp_utc=self.frame.timestamp_utc)
        self.assertEqual(self.act(step), [])

    def test_expired_wrong_side_or_wrong_underlying_contract_cannot_open(self):
        for contract in (replace(CE, expiry_date="2026-09-08"), replace(CE, option_type="PE"),
                         replace(CE, underlying="BANKNIFTY"), replace(CE, expiry_date="unknown")):
            with self.subTest(contract=contract):
                self.assertEqual(self.act(inp(self.frame, contracts={"atm_ce": contract})), [])

    def test_signal_already_beyond_pivot_or_back_across_line_is_not_traded(self):
        self.assertEqual(self.act(inp(self.frame), found("BUY", b_price=101)), [])
        sig = found("BUY", cross_bar=12, b_price=99)
        sig.metadata["ghost"]["trigger_now"] = 102
        self.assertEqual(self.act(inp(self.frame), sig), [])

    def test_live_price_beyond_pivot_blocks_entry_even_if_last_close_was_valid(self):
        self.assertEqual(self.act(replace(live(self.frame), spot_price=98)), [])

    def test_missing_contract_does_not_consume_fresh_crossing(self):
        self.assertEqual(self.act(inp(self.frame, contracts={})), [])
        self.assertIsNone(self.state["last_signal_idx"])
        self.assertEqual([s.signal_type for s in self.act(inp(self.frame))], ["OPEN_GROUP"])

    def test_geometry_does_not_consume_candidate_before_conflict_or_contract_checks(self):
        frames = up_leg_candles(self.strategy, cross_at=9, newest=9)
        first, _ = evaluate(self.strategy, frames, self.state)
        again, _ = evaluate(self.strategy, frames, self.state)
        self.assertEqual(first.metadata, again.metadata)
        self.strategy._consume(self.state, first)
        self.assertIsNone(evaluate(self.strategy, frames, self.state)[0])

    def test_close_failure_or_lost_answer_prevents_reversal_and_restores_position(self):
        self.act(inp(self.frame))
        original = dict(self.state["position"])
        for failure in (Booking("CLOSE_GROUP", original["group_id"], "id", booked=False), RuntimeError("lost answer")):
            calls = []
            def evaluate_opposite(state):
                out = []
                self.strategy._act(state, inp(self.frame), self.frame, [found("SELL", cross_bar=12)], out)
                return out
            def post(signal):
                calls.append(signal.signal_type)
                if isinstance(failure, Exception):
                    raise failure
                return failure
            outcome = run_tick(self.state, evaluate_opposite, post)
            self.state = outcome.state
            self.assertEqual(calls, ["CLOSE_GROUP"])
            self.assertTrue(outcome.restored)
            self.assertEqual(self.state["position"], original)


class SnapshotTests(unittest.TestCase):
    def test_cached_book_is_invalidated_after_own_booking_and_never_reused_after_failure(self):
        now, rows, calls = [10.0], [[]], []
        def fetch():
            calls.append(1)
            if isinstance(rows[0], Exception):
                raise rows[0]
            return rows[0]
        snapshot = PositionSnapshot(fetch, clock=lambda: now[0])
        self.assertEqual(snapshot.groups(), [])
        rows[0] = [{"status": "Open", "groupId": "new"}]
        self.assertEqual(snapshot.groups(), [])
        snapshot.invalidate()
        self.assertEqual(snapshot.groups(), ["new"])
        rows[0], now[0] = RuntimeError("offline"), 16.0
        self.assertIsNone(snapshot.groups())
        self.assertIsNone(snapshot.groups())
        self.assertEqual(len(calls), 3)

    def test_malformed_book_is_unknown_not_flat(self):
        for rows in ({}, [None], [{"status": "unexpected"}], [{"status": "Open"}]):
            self.assertIsNone(PositionSnapshot(lambda: rows).groups())

    def test_only_open_rows_belong_to_the_run_book(self):
        rows = [{"status": "Open", "groupId": "held"}, {"status": "Open", "groupId": "held"},
                {"status": "Closed", "groupId": "closed"}, {"status": "Carried", "groupId": "moved"}]
        self.assertEqual(PositionSnapshot(lambda: rows).groups(), ["held"])


if __name__ == "__main__":
    unittest.main()
