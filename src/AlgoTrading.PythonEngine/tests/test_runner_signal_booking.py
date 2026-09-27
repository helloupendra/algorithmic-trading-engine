"""
Booking a live strategy's signals exactly once (strategies/signal_booking.py).

The runner used to post each signal once. A lost answer dropped the rest of the
tick, a refusal was re-raised, and in both cases the strategy went on believing
what it had just emitted: on 24 Sep runner-214 had two OPEN_GROUPs refused with
409 "RATE LIMIT EXCEEDED" and kept them as open groups it never had. These run
the booking against a fake API that behaves like the real one: it books a
`clientSignalId` once and answers a repeat with the row it already has.
"""

import copy
import unittest
from typing import Any, Dict, List, Optional

import requests

import _bootstrap  # noqa: F401

from strategies.base_strategy import StrategySignal
from strategies.signal_booking import (
    CLOSE_RETRY_SECONDS,
    OPEN_RETRY_SECONDS,
    Booking,
    SignalBooker,
    report,
    run_tick,
)

CALL = "NSE:NIFTY2692925000CE"
PUT = "NSE:NIFTY2692925000PE"
RATE_LIMITED = '{"error":"RATE LIMIT EXCEEDED: More than 50 orders placed in the last minute for run 214 (leg BUY 1)."}'


def http_error(status: int, body: str = "") -> requests.exceptions.HTTPError:
    response = requests.Response()
    response.status_code = status
    response._content = body.encode()
    return requests.exceptions.HTTPError(f"{status} error", response=response)


class FakeApi:
    """
    POST /api/Simulator/signals. Each call takes the next step of ``script``:

      None       booked (or the booked row, for an id it holds) and answered
      "lost"     booked, but the answer never arrives (ReadTimeout)
      "down"     not reached at all (ConnectionError)
      int        answered with that HTTP status, nothing booked
      (int, str) the same, with a body
    """

    def __init__(self, *script: Any) -> None:
        self.script = list(script)
        self.posts: List[Dict[str, Any]] = []
        self.timeouts: List[Optional[float]] = []
        self.booked: Dict[str, Dict[str, Any]] = {}

    def create_simulation_signal(self, payload: Dict[str, Any], timeout: Optional[float] = None) -> Dict[str, Any]:
        self.timeouts.append(timeout)
        self.posts.append(copy.deepcopy(payload))
        step = self.script.pop(0) if self.script else None

        if step == "down":
            raise requests.exceptions.ConnectionError("Connection refused")
        if isinstance(step, int):
            raise http_error(step)
        if isinstance(step, tuple):
            raise http_error(*step)

        row = self.booked.get(payload["clientSignalId"])
        if row is None:
            row = {"id": len(self.booked) + 1, "clientSignalId": payload["clientSignalId"],
                   "signalType": payload["signalType"], "groupId": payload["groupId"]}
            self.booked[payload["clientSignalId"]] = row

        if step == "lost":
            raise requests.exceptions.ReadTimeout("Read timed out. (read timeout=10)")
        return row

    def booked_of(self, signal_type: str) -> List[Dict[str, Any]]:
        return [row for row in self.booked.values() if row["signalType"] == signal_type]


class Clock:
    """Time that moves only when the booker sleeps."""

    def __init__(self) -> None:
        self.now = 0.0
        self.sleeps: List[float] = []

    def time(self) -> float:
        return self.now

    def sleep(self, seconds: float) -> None:
        self.sleeps.append(seconds)
        self.now += seconds


def booker(api: FakeApi, clock: Optional[Clock] = None, **kwargs: Any) -> SignalBooker:
    clock = clock or Clock()
    return SignalBooker(api, 214, sleep=clock.sleep, clock=clock.time, **kwargs)


def signal(kind: str, group: str, *legs: Dict[str, Any]) -> StrategySignal:
    return StrategySignal(strategy_name="Toy", signal_type=kind, timestamp_utc="2026-09-24T05:57:00+00:00",
                          reason="test", legs=list(legs), metadata={"group_id": group})


def leg(symbol: str, side: str, price: Optional[float]) -> Dict[str, Any]:
    return {"symbol": symbol, "side": side, "quantity": 1, "price": price}


class ToyStrategy:
    """Opens a group when it has none; its state says what it believes it holds."""

    def initialize_state(self) -> Dict[str, Any]:
        return {"open": None, "ticks": 0}

    def on_bar(self, state: Dict[str, Any], _inp: Any = None) -> List[StrategySignal]:
        state["ticks"] += 1
        if state["open"] is None:
            group = f"TOY_{state['ticks']}"
            state["open"] = group
            return [signal("OPEN_GROUP", group, leg(CALL, "BUY", 100.0))]
        return []


class BookerTests(unittest.TestCase):
    def test_connection_error_on_close_retried_and_booked_once(self):
        # The close landed; only its answer was lost. The retry carries the
        # same id, so the API answers with the row it has and books nothing.
        api = FakeApi("lost")
        clock = Clock()

        booking = booker(api, clock).book(signal("CLOSE_GROUP", "G1", leg(CALL, "SELL", 120.0)))

        self.assertTrue(booking.booked)
        self.assertEqual(booking.attempts, 2)
        self.assertEqual(len(api.booked_of("CLOSE_GROUP")), 1)
        self.assertEqual({p["clientSignalId"] for p in api.posts}, {booking.client_signal_id})
        self.assertEqual(booking.result["id"], 1)
        # A close is not re-priced: the leg keeps the price the runner saw.
        self.assertEqual(api.posts[1]["legs"][0]["price"], 120.0)

    def test_open_retry_sent_unpriced(self):
        api = FakeApi("down")

        booking = booker(api).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0), leg(PUT, "BUY", 80.0)))

        self.assertTrue(booking.booked)
        first, retry = api.posts
        self.assertEqual([x["price"] for x in first["legs"]], [100.0, 80.0])
        self.assertEqual([x["price"] for x in retry["legs"]], [None, None])
        self.assertEqual([x["symbol"] for x in retry["legs"]], [CALL, PUT])
        self.assertEqual(first["clientSignalId"], retry["clientSignalId"])

    def test_a_refusal_is_not_retried(self):
        api = FakeApi((409, RATE_LIMITED))

        booking = booker(api).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))

        self.assertFalse(booking.booked)
        self.assertTrue(booking.refused)
        self.assertEqual(booking.status, 409)
        self.assertEqual(booking.detail, RATE_LIMITED)
        self.assertEqual(len(api.posts), 1)
        # Each post has its own short timeout, so a hung API cannot hold the tick loop for long.
        self.assertEqual(api.timeouts, [10.0])

    def test_an_open_is_given_up_after_about_five_seconds(self):
        api = FakeApi(*[503] * 50)
        clock = Clock()

        booking = booker(api, clock).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))

        self.assertFalse(booking.booked)
        self.assertFalse(booking.refused)
        self.assertAlmostEqual(clock.now, OPEN_RETRY_SECONDS)
        self.assertIn("HTTP 503", booking.detail)
        self.assertEqual(api.booked, {})

    def test_a_close_keeps_trying_for_about_a_minute(self):
        api = FakeApi(*[503] * 14)
        clock = Clock()

        booking = booker(api, clock).book(signal("CLOSE_GROUP", "G1", leg(CALL, "SELL", 120.0)))

        self.assertTrue(booking.booked)
        self.assertEqual(booking.attempts, 15)
        self.assertLess(clock.now, CLOSE_RETRY_SECONDS)
        self.assertGreater(clock.now, 45)

    def test_an_answer_lost_at_the_deadline_is_asked_about_once_more(self):
        # The last post in the budget may have landed. Giving up there would
        # put the strategy back while the book holds the group, and the
        # strategy would open it again; one more post with the same id finds out.
        api = FakeApi(503, 503, 503, 503, "lost")
        clock = Clock()

        booking = booker(api, clock).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))

        self.assertTrue(booking.booked)
        self.assertEqual(booking.attempts, 6)
        self.assertEqual(clock.now, OPEN_RETRY_SECONDS, "the fifth post went at the deadline")
        self.assertEqual(clock.sleeps[-1], 0.0)
        self.assertEqual(len(api.booked), 1)

    def test_an_api_that_never_answers_is_asked_once_past_the_deadline_then_left(self):
        api = FakeApi(*["down"] * 50)
        clock = Clock()

        booking = booker(api, clock).book(signal("OPEN_GROUP", "G1", leg(CALL, "BUY", 100.0)))

        self.assertFalse(booking.booked)
        self.assertEqual(clock.sleeps[-1], 0.0, "the confirming post goes at once")
        self.assertEqual(len(api.posts), booking.attempts)

    def test_housekeeping_runs_between_attempts(self):
        calls = []
        api = FakeApi(503, 503)

        booker(api, on_wait=lambda: calls.append(1)).book(signal("CLOSE_GROUP", "G1", leg(CALL, "SELL", 1.0)))

        self.assertEqual(len(calls), 2)


class TickTests(unittest.TestCase):
    def test_rate_limit_409_restores_state_and_reemits(self):
        strategy = ToyStrategy()
        api = FakeApi((409, RATE_LIMITED))
        book = booker(api)
        state = strategy.initialize_state()
        lines: List[str] = []

        tick = run_tick(state, strategy.on_bar, book.book)
        report(tick, lines.append)

        self.assertTrue(tick.restored)
        self.assertIsNone(tick.state["open"], "the strategy no longer believes in the refused group")
        self.assertEqual(lines[0], f"SIGNAL REFUSED by the API: {RATE_LIMITED}")
        self.assertEqual(lines[1], "SIGNAL NOT BOOKED: OPEN_GROUP TOY_1 — refused by the API (HTTP 409).")
        self.assertIn("back to where it was before this tick", lines[2])

        # The next tick, the limit has passed: the strategy asks again, and this
        # time the group is booked — once.
        tick = run_tick(tick.state, strategy.on_bar, book.book)

        self.assertFalse(tick.not_booked)
        self.assertEqual(tick.state["open"], "TOY_1")
        self.assertEqual(len(api.booked_of("OPEN_GROUP")), 1)

    def test_a_booked_open_earlier_in_the_tick_is_never_undone(self):
        def two_opens(state):
            state["groups"] = ["A", "B"]
            return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0)),
                    signal("OPEN_GROUP", "B", leg(PUT, "BUY", 80.0))]

        api = FakeApi(None, (409, RATE_LIMITED))
        lines: List[str] = []

        tick = run_tick({"groups": []}, two_opens, booker(api).book)
        report(tick, lines.append)

        self.assertFalse(tick.restored)
        self.assertEqual(tick.state["groups"], ["A", "B"])
        self.assertIn("was NOT put back: OPEN_GROUP A was booked earlier in this tick", lines[-1])
        self.assertIn("The strategy believes OPEN_GROUP B happened; the book does not.", lines[-1])

    def test_a_booked_close_earlier_in_the_tick_does_not_stop_the_state_going_back(self):
        # Emitted again next tick, the close finds nothing open: reduce-only.
        def roll(state):
            state["open"] = "B"
            return [signal("CLOSE_GROUP", "A", leg(CALL, "SELL", 90.0)),
                    signal("OPEN_GROUP", "B", leg(PUT, "BUY", 80.0))]

        tick = run_tick({"open": "A"}, roll, booker(FakeApi(None, (409, RATE_LIMITED))).book)

        self.assertTrue(tick.restored)
        self.assertEqual(tick.state, {"open": "A"})

    def test_signals_after_one_that_was_not_booked_are_not_sent(self):
        def open_and_close(state):
            state["seen"] = True
            return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0)),
                    signal("CLOSE_GROUP", "Z", leg(PUT, "SELL", 80.0))]

        api = FakeApi((409, RATE_LIMITED))
        lines: List[str] = []

        tick = run_tick({}, open_and_close, booker(api).book)
        report(tick, lines.append)

        self.assertTrue(tick.restored)
        self.assertEqual(tick.unsent, 1)
        self.assertEqual(len(api.posts), 1)
        self.assertEqual(tick.state, {})
        self.assertIn("1 later signal(s) of this tick were not sent", lines[-1])

    def test_a_signal_that_fails_on_the_way_is_not_booked(self):
        def emit(state):
            state["open"] = "A"
            return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0))]

        def post(_sig):
            raise RuntimeError("could not resolve the contract")

        tick = run_tick({"open": None}, emit, post)

        self.assertTrue(tick.restored)
        self.assertEqual(tick.state, {"open": None})
        self.assertIn("could not resolve the contract", tick.not_booked[0].detail)

    def test_signals_that_are_not_for_the_book_change_nothing(self):
        def emit(state):
            state["open"] = "A"
            return [signal("ATM_SHIFT", "A")]

        tick = run_tick({"open": None}, emit, lambda _sig: None)

        self.assertFalse(tick.restored)
        self.assertEqual(tick.state, {"open": "A"})
        self.assertEqual(tick.bookings, [])

    def test_the_state_before_the_tick_is_a_real_copy(self):
        def mutate(state):
            state["nested"]["n"] += 1
            return [signal("OPEN_GROUP", "A", leg(CALL, "BUY", 100.0))]

        state = {"nested": {"n": 1}}
        tick = run_tick(state, mutate, lambda _sig: Booking("OPEN_GROUP", "A", "id", booked=False))

        self.assertEqual(tick.state, {"nested": {"n": 1}})


if __name__ == "__main__":
    unittest.main()
