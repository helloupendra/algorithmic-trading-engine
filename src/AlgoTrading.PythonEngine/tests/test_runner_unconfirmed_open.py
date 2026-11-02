"""
An OPEN_GROUP whose answer is lost (strategies/signal_booking.py).

The 2 Nov review's finding: an OPEN whose answer was lost twice counted as not
booked, so the strategy was put back and opened a second group under a new id,
which nothing in the strategy would ever exit. An unknown outcome is not "not
booked": the state stays, and the same id is posted until the API says which.
"""

import functools
import socket
import unittest
from datetime import datetime, timedelta, timezone
from typing import Any, Dict, List, Optional
from unittest import mock

import requests

import _bootstrap  # noqa: F401

from strategies.base_strategy import BarFrame, OptionContract, StrategyInput, StrategySignal
from strategies.bullish.crude_momentum_strategy import CrudeMomentumStrategy
from strategies.signal_booking import (
    OPEN_REFUSAL_COOLDOWN_SECONDS,
    UNCONFIRMED_LOG_SECONDS,
    UNCONFIRMED_RETRY_SECONDS,
    SignalBooker,
    report,
    run_tick,
    settle_tick,
)
from test_live_runner_main import RUN_ID, live_harness
from test_runner_signal_booking import CALL, PUT, RATE_LIMITED, Clock, FakeApi, ToyStrategy, booker, leg, signal


class SlowApi(FakeApi):
    """A lost answer costs the post's whole timeout, as a real ReadTimeout does."""

    def __init__(self, clock: Clock, *script: Any) -> None:
        super().__init__(*script)
        self.clock = clock

    def create_simulation_signal(self, payload: Dict[str, Any], timeout: Optional[float] = None) -> Dict[str, Any]:
        if self.script and self.script[0] in ("lost", "lost-too"):
            self.clock.now += timeout or 10.0
        return super().create_simulation_signal(payload, timeout)


def run_ticks(state: Dict[str, Any], evaluate: Any, book: SignalBooker, clock: Clock, ticks: int):
    """The runner's loop: an unconfirmed OPEN is asked about first, and the strategy is not evaluated until it is answered."""
    tick = None
    evaluated = 0

    def counted(tick_state: Dict[str, Any]) -> List[StrategySignal]:
        nonlocal evaluated
        evaluated += 1
        return evaluate(tick_state)

    for _ in range(ticks):
        if tick is not None and tick.unconfirmed:
            tick = settle_tick(tick, book.recheck, book.book)
            state = tick.state
            if tick.unconfirmed:
                clock.now += 1.0
                continue
        tick = run_tick(state, counted, book.book)
        state = tick.state
        clock.now += 1.0
    return tick, state, evaluated


def crude_momentum():
    """The reviewers' reproduction (review 2, appendix A): the real CrudeMomentum, ready to enter on its first call."""
    strategy = CrudeMomentumStrategy({"lots": 1})
    start = datetime(2026, 11, 2, 3, 30, tzinfo=timezone.utc)
    bars = []
    price = 5400.0
    for i in range(31):
        at = (start + timedelta(minutes=5 * i)).isoformat().replace("+00:00", "Z")
        bars.append(BarFrame("MCX:CRUDEOIL26NOVFUT", "5m", at, price, price + 4, price - 1, price + 3, 100.0))
        price += 3
    call = OptionContract("MCX:CRUDEOIL26NOV5500CE", "CRUDEOIL", "2026-11-17", 5500.0, "CE")

    def on_bar(state: Dict[str, Any]) -> List[StrategySignal]:
        return strategy.on_bar(state, StrategyInput(
            mode="LivePaper", timestamp_utc=bars[-1].timestamp_utc, underlying="CRUDEOIL", spot_price=price,
            atm_strike=5500, strike_step=50, lot_size=100, contracts={"atm_ce": call},
            bars={"5m": {"index": bars}}, metadata={"source": "live-api"}))

    return strategy.initialize_state(), on_bar


class TheReviewersReproductions(unittest.TestCase):
    def test_two_lost_answers_book_one_group_with_the_real_crude_momentum(self):
        # Review 2's script printed "True 2 2": restored, two rows, two groups.
        state, on_bar = crude_momentum()
        clock = Clock()
        api = SlowApi(clock, "lost", "lost")
        book = booker(api, clock)

        first = run_tick(state, on_bar, book.book)

        self.assertTrue(first.unconfirmed)
        self.assertFalse(first.restored)
        self.assertFalse(first.bookings[-1].booked)
        self.assertEqual(1, first.state["trades_this_session"], "the strategy still believes it entered")

        tick, _, _ = run_ticks(first.state, on_bar, book, clock, ticks=12)

        self.assertEqual(1, len(api.booked))
        self.assertEqual(1, len({row["groupId"] for row in api.booked.values()}))
        self.assertEqual(1, len({post["clientSignalId"] for post in api.posts}), "the same id every time")
        self.assertFalse(tick.unconfirmed)

    def test_a_timeout_then_a_503_is_still_unknown(self):
        # Review 1 printed: "restored after lost+503: True | OPENs booked: 2".
        state, on_bar = crude_momentum()
        clock = Clock()
        api = SlowApi(clock, "lost", 503)
        book = booker(api, clock)

        first = run_tick(state, on_bar, book.book)

        self.assertTrue(first.unconfirmed)
        self.assertFalse(first.restored)

        run_ticks(first.state, on_bar, book, clock, ticks=12)

        self.assertEqual(1, len(api.booked))
        self.assertEqual(1, len({post["clientSignalId"] for post in api.posts}))


class AskingAgainTests(unittest.TestCase):
    def test_a_post_that_never_landed_is_booked_by_the_next_one(self):
        strategy = ToyStrategy()
        clock = Clock()
        api = SlowApi(clock, "lost-too", "lost-too")
        book = booker(api, clock)

        _, state, _ = run_ticks(strategy.initialize_state(), strategy.on_bar, book, clock, ticks=10)

        self.assertEqual(1, len(api.booked))
        self.assertEqual("TOY_1", state["open"])
        self.assertEqual(1, len({post["clientSignalId"] for post in api.posts}))

    def test_a_repost_goes_out_unpriced_and_nothing_is_evaluated_until_it_is_answered(self):
        strategy = ToyStrategy()
        clock = Clock()
        api = SlowApi(clock, "lost", "lost", "lost", "lost", "lost", None)
        book = booker(api, clock)

        tick, state, evaluated = run_ticks(strategy.initialize_state(), strategy.on_bar, book, clock, ticks=4)

        self.assertTrue(tick.unconfirmed)
        self.assertEqual(1, evaluated, "the strategy waits behind the unknown")
        self.assertEqual(1, state["ticks"])
        self.assertTrue(all(post["legs"][0]["price"] is None for post in api.posts[1:]))

    def test_the_posts_are_paced_and_the_log_says_so_about_once_a_minute(self):
        strategy = ToyStrategy()
        clock = Clock()
        api = FakeApi(*["down"] * 500)
        book = booker(api, clock)
        lines: List[str] = []

        tick = run_tick(strategy.initialize_state(), strategy.on_bar, book.book)
        report(tick, lines.append)
        self.assertEqual(1, len(lines))
        self.assertTrue(lines[0].startswith("SIGNAL UNCONFIRMED: OPEN_GROUP TOY_1 — no answer to "))
        self.assertIn("(ConnectionError)", lines[0])
        posted = len(api.posts)

        # Not before its time.
        self.assertIsNone(book.recheck())
        clock.now += UNCONFIRMED_RETRY_SECONDS[0] - 0.1
        self.assertIsNone(book.recheck())
        self.assertEqual(posted, len(api.posts))

        # An hour away, a tick a second: no limit, the same id, and never booked twice.
        for _ in range(3600):
            clock.now += 1.0
            tick = settle_tick(tick, book.recheck, book.book)
            if tick.last_answer is not None:
                report(tick, lines.append)

        self.assertTrue(tick.unconfirmed)
        self.assertEqual("TOY_1", tick.state["open"], "not forgotten")
        self.assertEqual(1, len({post["clientSignalId"] for post in api.posts}))
        self.assertEqual({}, api.booked)
        expected_posts = 3600 / UNCONFIRMED_RETRY_SECONDS[-1]
        self.assertLess(abs(len(api.posts) - posted - expected_posts), 5)
        self.assertLess(abs(len(lines) - 3600 / UNCONFIRMED_LOG_SECONDS), 30)
        self.assertTrue(all(line.startswith("SIGNAL UNCONFIRMED") for line in lines))

    def test_a_booked_row_settles_it_however_late(self):
        strategy = ToyStrategy()
        clock = Clock()
        api = FakeApi("lost", *["down"] * 30)
        book = booker(api, clock)
        lines: List[str] = []

        tick = run_tick(strategy.initialize_state(), strategy.on_bar, book.book)
        self.assertTrue(tick.unconfirmed)
        api.script = [503, "down", 500, None]
        for _ in range(120):
            clock.now += 1.0
            tick = settle_tick(tick, book.recheck, book.book) if tick.unconfirmed else tick
            if tick.last_answer is not None and tick.confirmed is None:
                report(tick, lines.append)

        self.assertFalse(tick.unconfirmed)
        self.assertFalse(tick.restored)
        self.assertEqual("TOY_1", tick.state["open"])
        self.assertEqual(1, len(api.booked))
        self.assertTrue(tick.bookings[-1].booked)

        lines.clear()
        report(tick, lines.append)
        self.assertRegex(lines[-1], r"^SIGNAL CONFIRMED: OPEN_GROUP TOY_1 is in the book; the API answered after \d+ s")

    def test_a_refusal_after_the_lookup_puts_the_strategy_back_and_the_next_open_is_a_new_id(self):
        for status in (400, 409):
            with self.subTest(status=status):
                strategy = ToyStrategy()
                clock = Clock()
                api = SlowApi(clock, "lost-too", "lost-too", (status, '{"message":"MARKET NOT OPEN: opens 09:15 IST."}'))
                book = booker(api, clock)
                lines: List[str] = []

                tick = run_tick(strategy.initialize_state(), strategy.on_bar, book.book)
                clock.now += UNCONFIRMED_RETRY_SECONDS[0]
                tick = settle_tick(tick, book.recheck, book.book)
                report(tick, lines.append)

                self.assertFalse(tick.unconfirmed)
                self.assertTrue(tick.restored)
                self.assertEqual({"open": None, "ticks": 0}, tick.state)
                self.assertEqual({}, api.booked)
                self.assertFalse(book.has_unconfirmed)
                self.assertIn(f"refused by the API (HTTP {status})", " ".join(lines))
                self.assertIn("back to where it was before this tick", lines[-1])

                # The refusal starts the usual hold, then the strategy asks again: one group.
                clock.now += OPEN_REFUSAL_COOLDOWN_SECONDS + 1
                again = run_tick(tick.state, strategy.on_bar, book.book)
                self.assertTrue(again.bookings[-1].booked)
                self.assertEqual(1, len(api.booked))

    def test_only_the_routes_own_refusals_answer_for_the_id(self):
        # A 401, a 403 or a 429 comes from in front of the id lookup, and a 500 can
        # come after a commit: none of them says the lost post is not in the book.
        for status in (401, 403, 404, 429, 500):
            with self.subTest(status=status):
                strategy = ToyStrategy()
                clock = Clock()
                api = SlowApi(clock, "lost", "lost", status)
                book = booker(api, clock)

                tick = run_tick(strategy.initialize_state(), strategy.on_bar, book.book)
                clock.now += UNCONFIRMED_RETRY_SECONDS[0]
                tick = settle_tick(tick, book.recheck, book.book)

                self.assertTrue(tick.unconfirmed)
                self.assertEqual("TOY_1", tick.state["open"])
                self.assertEqual(1, len(api.booked))


class WhatCountsAsUnknownTests(unittest.TestCase):
    def test_a_500_on_the_first_posts_is_unknown(self):
        # It can come after the commit.
        strategy = ToyStrategy()
        tick = run_tick(strategy.initialize_state(), strategy.on_bar, booker(FakeApi(*[500] * 50)).book)

        self.assertTrue(tick.unconfirmed)
        self.assertEqual("TOY_1", tick.state["open"])

    def test_a_refusal_from_in_front_of_the_route_on_a_first_post_is_definite(self):
        # Nothing was lost before it, so nothing can be in the book.
        for status in (401, 403, 429):
            with self.subTest(status=status):
                strategy = ToyStrategy()
                tick = run_tick(strategy.initialize_state(), strategy.on_bar, booker(FakeApi(status)).book)

                self.assertFalse(tick.unconfirmed)
                self.assertTrue(tick.restored)

    def test_an_unreadable_answer_to_a_post_is_not_read_as_not_booked(self):
        class BrokenBody(FakeApi):
            def create_simulation_signal(self, payload, timeout=None):
                row = super().create_simulation_signal(payload, timeout)
                if len(self.posts) == 1:
                    raise requests.exceptions.JSONDecodeError("Expecting value", "", 0)
                return row

        strategy = ToyStrategy()
        api = BrokenBody()

        tick = run_tick(strategy.initialize_state(), strategy.on_bar, booker(api).book)

        self.assertEqual("TOY_1", tick.state["open"])
        self.assertEqual(1, len(api.booked))
        self.assertEqual(1, len({post["clientSignalId"] for post in api.posts}))

    def test_what_was_never_sent_is_definitely_not_booked_and_goes_back_as_before(self):
        for script in (["refused"] * 30, [503] * 30):
            with self.subTest(step=script[0]):
                strategy = ToyStrategy()
                book = booker(FakeApi(*script))

                tick = run_tick(strategy.initialize_state(), strategy.on_bar, book.book)

                self.assertFalse(tick.unconfirmed)
                self.assertTrue(tick.restored)
                self.assertIsNone(tick.state["open"])
                self.assertFalse(book.has_unconfirmed)

        def unserialisable(_payload, timeout=None):
            raise requests.exceptions.InvalidJSONError("Object of type set is not JSON serializable")

        api = FakeApi()
        api.create_simulation_signal = unserialisable
        booking = booker(api).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))
        self.assertFalse(booking.booked)
        self.assertFalse(booking.unconfirmed)

    def test_a_real_connection_refused_is_told_from_a_lost_answer(self):
        # The shape requests really raises, from a real closed port.
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            port = probe.getsockname()[1]

        class RealPost(FakeApi):
            def create_simulation_signal(self, payload, timeout=None):
                return requests.post(f"http://127.0.0.1:{port}/api/Simulator/signals", json=payload, timeout=2).json()

        booking = booker(RealPost()).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))

        self.assertFalse(booking.booked)
        self.assertFalse(booking.unconfirmed, booking.detail)

    def test_a_lost_answer_on_a_close_is_not_unconfirmed(self):
        # A close emitted again closes nothing twice, so it needs no such care.
        clock = Clock()
        api = SlowApi(clock, "lost", "lost")
        book = booker(api, clock, close_retry_seconds=5.0)

        booking = book.book(signal("CLOSE_GROUP", "G1", leg(CALL, "SELL", 120.0)))

        self.assertFalse(booking.booked)
        self.assertFalse(booking.unconfirmed)
        self.assertFalse(book.has_unconfirmed)


class ThroughTheRunnerTests(unittest.TestCase):
    """execution_runner.py itself, with an OPEN's answers lost: one id, one booking, and the state kept."""

    @staticmethod
    def run_with(script: List[Any], tick_count: int):
        run = live_harness(tick_count)
        run.api.signal_script = list(script)
        run.extra_patches = [
            # No waiting in a test: give up on the first pass, and ask again on the next tick.
            mock.patch("strategies.signal_booking.SignalBooker", functools.partial(SignalBooker, open_retry_seconds=0.0)),
            mock.patch("strategies.signal_booking.UNCONFIRMED_RETRY_SECONDS", (0.0,)),
        ]
        return run.run()

    def test_it_is_asked_about_with_the_same_id_and_the_strategy_waits_for_the_answer(self):
        # Tick 1 posts twice and is lost; tick 2 asks and is lost, and the strategy is
        # not evaluated; tick 3 asks and gets the row, and the strategy goes on.
        run = self.run_with(["lost", "lost", "lost"], tick_count=3)

        posts = run.api.named("POST", "/api/Simulator/signals")
        self.assertEqual(4, len(posts), run.output)
        self.assertEqual(1, len({p[3]["clientSignalId"] for p in posts}))
        self.assertEqual(2, len(run.live_inputs()), "not evaluated on tick 2")
        self.assertIn("SIGNAL UNCONFIRMED: OPEN_GROUP probe-1", run.output)
        self.assertIn("SIGNAL CONFIRMED: OPEN_GROUP probe-1 is in the book", run.output)
        self.assertIn("PERSISTED SIGNAL:", run.output)
        self.assertNotIn("SIGNAL NOT BOOKED", run.output)
        # The dashboard copy goes out once, when the book has it.
        self.assertEqual(1, len(run.api.named("POST", f"/api/Strategy/runs/{RUN_ID}/signals")))

    def test_it_is_not_forgotten_when_the_runner_ends_without_an_answer(self):
        run = self.run_with(["lost"] * 20, tick_count=2)

        self.assertIn("SIGNAL UNCONFIRMED: OPEN_GROUP probe-1 — the runner is ending and the API never answered",
                      run.output)
        self.assertEqual(1, len({p[3]["clientSignalId"] for p in run.api.named("POST", "/api/Simulator/signals")}))


class TheRestOfTheTickTests(unittest.TestCase):
    @staticmethod
    def emit(state):
        state["open"] = ["A", "B"]
        return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0)),
                signal("OPEN_GROUP", "B", leg(PUT, "BUY", 80.0)),
                signal("CLOSE_GROUP", "Z", leg(PUT, "SELL", 70.0))]

    def test_it_waits_behind_the_open_and_goes_out_in_order(self):
        clock = Clock()
        api = SlowApi(clock, "lost", "lost")
        book = booker(api, clock)

        tick = run_tick({"open": []}, self.emit, book.book)

        self.assertTrue(tick.unconfirmed)
        self.assertEqual({"A"}, {post["groupId"] for post in api.posts})
        self.assertEqual(2, len(tick.remaining))

        clock.now += UNCONFIRMED_RETRY_SECONDS[0]
        tick = settle_tick(tick, book.recheck, book.book)

        self.assertFalse(tick.unconfirmed)
        self.assertEqual(["A", "A", "A", "B", "Z"], [post["groupId"] for post in api.posts])
        self.assertEqual(["OPEN_GROUP", "OPEN_GROUP", "CLOSE_GROUP"], [row["signalType"] for row in api.booked.values()])
        self.assertEqual(["A", "B"], tick.state["open"])

    def test_when_the_open_is_refused_the_later_opens_go_with_it_and_a_close_still_goes_out(self):
        clock = Clock()
        api = SlowApi(clock, "lost-too", "lost-too", (409, RATE_LIMITED))
        book = booker(api, clock)

        tick = run_tick({"open": []}, self.emit, book.book)
        clock.now += UNCONFIRMED_RETRY_SECONDS[0]
        tick = settle_tick(tick, book.recheck, book.book)

        self.assertTrue(tick.restored)
        self.assertEqual({"open": []}, tick.state)
        self.assertEqual(1, tick.unsent)
        self.assertEqual(["CLOSE_GROUP"], [row["signalType"] for row in api.booked.values()])

    def test_an_open_booked_earlier_in_the_tick_still_cannot_be_undone(self):
        def two_opens(state):
            state["groups"] = ["A", "B"]
            return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0)),
                    signal("OPEN_GROUP", "B", leg(PUT, "BUY", 80.0))]

        clock = Clock()
        api = SlowApi(clock, None, "lost-too", "lost-too", (409, RATE_LIMITED))
        book = booker(api, clock)
        lines: List[str] = []

        tick = run_tick({"groups": []}, two_opens, book.book)
        self.assertTrue(tick.unconfirmed)
        clock.now += UNCONFIRMED_RETRY_SECONDS[0]
        tick = settle_tick(tick, book.recheck, book.book)
        report(tick, lines.append)

        self.assertFalse(tick.restored)
        self.assertEqual(["A", "B"], tick.state["groups"])
        self.assertIn("was NOT put back: OPEN_GROUP A was booked earlier in this tick", lines[-1])

    def test_a_second_open_is_never_posted_while_one_is_unconfirmed_and_a_close_still_is(self):
        clock = Clock()
        api = SlowApi(clock, "lost", "lost")
        book = booker(api, clock)

        self.assertTrue(book.book(signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0))).unconfirmed)
        posted = len(api.posts)

        second = book.book(signal("OPEN_GROUP", "B", leg(PUT, "BUY", 80.0)))

        self.assertTrue(second.held)
        self.assertEqual(posted, len(api.posts))
        self.assertIn("OPEN_GROUP A is not confirmed yet", second.detail)
        self.assertTrue(book.book(signal("CLOSE_GROUP", "Z", leg(PUT, "SELL", 70.0))).booked)


if __name__ == "__main__":
    unittest.main()
