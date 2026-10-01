"""The market replay's player: a recorded day played in order, at its speed, to the book before the stream."""

import os
import sys
import unittest
from datetime import date, datetime, time, timedelta, timezone

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from market_data.replay import desk_replay as dr  # noqa: E402
from market_data.replay import run_replay  # noqa: E402

DAY = date(2026, 9, 30)


def at(hh, mm, ss=0.0):
    """An IST moment of the replayed day, as UTC."""
    return datetime.combine(DAY, time(hh, mm), dr.IST).astimezone(timezone.utc) + timedelta(seconds=ss)


def row(n, symbol, received, exchange=None, ltp=100.0):
    return (n, symbol, "symbolUpdate", received, exchange or received, ltp, ltp - 0.5, ltp + 0.5, 75, 75,
            None, None, None, 99.0, 1000 + n)


def plan(speed=1.0, start=time(9, 15), runs=(7,)):
    return dr.ReplayPlan(day=DAY, speed=speed, start=start, session=3, runs=list(runs))


class Clock:
    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t

    def sleep(self, seconds):
        self.t += max(seconds, 0.001)


class Desk:
    """Everything the player talks to, recorded."""

    def __init__(self, rows, listening=lambda run: True, control=lambda: "", stop_after=None, prime=None):
        self.rows = rows
        self.events = []
        self.reports = []
        self.clock = Clock()
        self.listening = listening
        self.control = control
        self.stop_after = stop_after
        self.prime = prime or []
        self.post = self._post

    def read(self, since, until):
        return [r for r in self.rows if since <= r[3] < until]

    def _post(self, ticks):
        self.events.append(("post", self.clock.t, [t["symbol"] for t in ticks]))

    def publish(self, ticks):
        self.events.append(("publish", self.clock.t, [t["symbol"] for t in ticks]))

    def stop(self):
        return self.stop_after is not None and len(self.kinds("publish")) >= self.stop_after

    def kinds(self, kind):
        return [e for e in self.events if e[0] == kind]

    def player(self, the_plan, sleep=None, **kw):
        return dr.Player(the_plan, read=self.read, prime=lambda p: self.prime, post=self.post, publish=self.publish,
                         report=self.reports.append, control=lambda: self.control(), listening=self.listening,
                         stop_requested=self.stop, clock=self.clock, sleep=sleep or self.clock.sleep,
                         log=lambda line: None, **kw)


class DeskReplayTests(unittest.TestCase):

    def test_a_recorded_row_becomes_a_replay_tick_for_the_book_and_the_stream(self):
        tick = dr.tick_from_row(row(1, "NSE:NIFTY50-INDEX", at(9, 15, 2)))
        self.assertEqual("NSE:NIFTY50-INDEX", tick["symbol"])
        self.assertEqual("2026-09-30T03:45:02.000Z", tick["exchangeTimestampUtc"])
        self.assertEqual((100.0, 99.5, 100.5, 99.0), (tick["lastTradedPrice"], tick["bidPrice"], tick["askPrice"], tick["prevClose"]))
        self.assertEqual((True, "desk-replay"), (tick["isReplay"], tick["sourceKey"]))

    def test_ticks_play_at_their_recorded_pace_book_first_then_stream(self):
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 15, 0)), row(2, "NSE:NIFTY50-INDEX", at(9, 15, 10)),
                     row(3, "NSE:NIFTY26OCT25000CE", at(9, 16, 0))])

        self.assertEqual("finished", desk.player(plan(speed=2.0)).run())

        self.assertEqual(["post", "publish"] * 3, [e[0] for e in desk.events])
        # Played from 09:14 (a minute early): 09:15:00 is due at 30 s at 2x, 09:15:10 at 35 s, 09:16:00 at 60 s.
        for got, want in zip([e[1] for e in desk.kinds("post")], [30.0, 35.0, 60.0]):
            self.assertAlmostEqual(want, got, delta=0.3)
        final = desk.reports[-1]
        self.assertEqual(("finished", 3, 1.0), (final["state"], final["ticksSent"], final["progress"]))
        self.assertEqual("2026-09-30T03:46:00.000Z", final["clockUtc"])

    def test_a_quote_stamped_with_an_earlier_days_trade_does_not_move_the_clock(self):
        yesterday = at(9, 15) - timedelta(days=1)
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 15, 5)),
                     row(2, "NSE:NIFTY26OCT26000CE", at(9, 15, 40), exchange=yesterday)])
        player = desk.player(plan())
        player.run()
        self.assertEqual(at(9, 15, 5), player.clock_utc)

    def test_nothing_plays_until_every_run_is_listening(self):
        calls = {"n": 0}

        def listening(run):
            calls["n"] += 1
            return calls["n"] > 3

        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 15))], listening=listening)
        desk.player(plan()).run()
        self.assertGreaterEqual(desk.events[0][1], 3.0)   # three one-second waits came first
        self.assertEqual("waiting", desk.reports[0]["state"])

    def test_a_run_that_never_listens_is_waited_for_only_so_long(self):
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 15))], listening=lambda run: False)
        self.assertEqual("finished", desk.player(plan(), listen_timeout=10).run())
        self.assertGreaterEqual(desk.events[0][1], 10.0)

    def test_a_pause_holds_the_replay_and_a_resume_carries_on_where_it_was(self):
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 14, 10)), row(2, "NSE:NIFTY50-INDEX", at(9, 14, 20))])
        commands = {"value": ""}
        desk.control = lambda: commands["value"]

        def sleep(seconds):
            desk.clock.sleep(seconds)
            # Paused from 5 s to 105 s of wall time.
            commands["value"] = "pause" if 5 <= desk.clock.t < 105 else ("resume" if desk.clock.t >= 105 else "")

        self.assertEqual("finished", desk.player(plan(), sleep=sleep).run())
        posts = [e[1] for e in desk.kinds("post")]
        # 09:14:10 was due at 10 s and 09:14:20 at 20 s; a 100 s pause moved both by about 100 s.
        self.assertAlmostEqual(110, posts[0], delta=1.0)
        self.assertAlmostEqual(120, posts[1], delta=1.0)
        self.assertTrue(any(r["state"] == "paused" for r in desk.reports))

    def test_a_stop_sent_over_the_control_key_ends_even_a_paused_replay(self):
        # At 08:45 the API could not stop the player's process. Paused, it posts nothing, so no 409 would end it.
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 14, 10)), row(2, "NSE:NIFTY50-INDEX", at(9, 14, 20))])
        commands = {"value": ""}
        desk.control = lambda: commands["value"]

        def sleep(seconds):
            desk.clock.sleep(seconds)
            commands["value"] = "pause" if desk.clock.t < 30 else "stop"

        self.assertEqual("stopped", desk.player(plan(), sleep=sleep).run())
        self.assertEqual([], desk.kinds("publish"))
        self.assertEqual("stopped", desk.reports[-1]["state"])

    def test_a_stop_ends_the_replay_as_stopped(self):
        desk = Desk([row(i, "NSE:NIFTY50-INDEX", at(9, 15, i)) for i in range(1, 6)], stop_after=2)
        self.assertEqual("stopped", desk.player(plan()).run())
        self.assertEqual(2, len(desk.kinds("publish")))
        self.assertEqual("stopped", desk.reports[-1]["state"])

    def test_an_api_that_has_no_replay_on_fails_the_replay_with_why(self):
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(9, 15))])

        def refuse(ticks):
            raise RuntimeError("the API has no market replay on")

        desk.post = refuse
        self.assertEqual("failed", desk.player(plan()).run())
        self.assertIn("no market replay", desk.reports[-1]["error"])
        self.assertEqual([], desk.kinds("publish"))   # nothing reached the stream unpriced

    def test_a_later_start_primes_the_book_but_not_the_stream(self):
        first = [{"symbol": "NSE:NIFTY26OCT25000CE", "lastTradedPrice": 120.0, "exchangeTimestampUtc": "x"}]
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(11, 0))], prime=first)
        later = plan(start=time(11, 0))
        self.assertEqual(at(10, 59), later.play_from_utc)
        desk.player(later).run()
        self.assertEqual(["NSE:NIFTY26OCT25000CE"], desk.events[0][2])
        self.assertEqual(["post", "post", "publish"], [e[0] for e in desk.events])

    def test_a_book_the_api_opened_again_is_sent_the_latest_price_of_every_other_symbol_played(self):
        # After an API restart its book is empty: a contract that does not tick again had no price until it did.
        rows = [row(1, "NSE:NIFTY50-INDEX", at(9, 15, 0), ltp=25000.0),
                row(2, "NSE:NIFTY26OCT25000CE", at(9, 15, 1), ltp=120.0),
                row(3, "NSE:NIFTY26OCT25000CE", at(9, 15, 2), ltp=121.0),
                row(4, "NSE:NIFTY26OCT25100PE", at(9, 15, 3), ltp=80.0),
                row(5, "NSE:NIFTY50-INDEX", at(9, 16, 0), ltp=25010.0)]
        desk = Desk(rows)
        sent = []

        def post(ticks):
            sent.append(sorted((t["symbol"], t["lastTradedPrice"]) for t in ticks))
            return len(sent) == 5    # the API restarted before the 09:16 tick: its answer says the book was opened again

        desk.post = post
        self.assertEqual("finished", desk.player(plan()).run())

        self.assertEqual(6, len(sent))
        self.assertEqual([("NSE:NIFTY50-INDEX", 25010.0)], sent[4])
        self.assertEqual([("NSE:NIFTY26OCT25000CE", 121.0), ("NSE:NIFTY26OCT25100PE", 80.0)], sent[5])
        self.assertEqual(5, len(desk.kinds("publish")))     # the runners are not sent them again

    def test_the_prices_a_later_start_primed_the_book_with_are_sent_again_too(self):
        first = [{"symbol": "NSE:NIFTY26OCT25000CE", "lastTradedPrice": 120.0, "exchangeTimestampUtc": "x"}]
        desk = Desk([row(1, "NSE:NIFTY50-INDEX", at(11, 0))], prime=first)
        sent = []

        def post(ticks):
            sent.append([t["symbol"] for t in ticks])
            return len(sent) == 2

        desk.post = post
        desk.player(plan(start=time(11, 0))).run()
        self.assertEqual([["NSE:NIFTY26OCT25000CE"], ["NSE:NIFTY50-INDEX"], ["NSE:NIFTY26OCT25000CE"]], sent)

    def test_progress_runs_from_the_open_to_the_close(self):
        p = plan()
        self.assertEqual(0.0, p.progress(None))
        self.assertEqual(0.0, p.progress(at(9, 15)))
        self.assertAlmostEqual(0.5, p.progress(at(12, 22, 30)))
        self.assertEqual(1.0, p.progress(at(15, 40)))

    def test_a_start_outside_the_session_or_a_speed_of_nothing_is_refused(self):
        with self.assertRaises(ValueError):
            plan(start=time(15, 10))
        with self.assertRaises(ValueError):
            plan(speed=0)

    def test_batches_cover_a_fifth_of_a_second_of_wall_time_at_the_speed_asked(self):
        rows = [row(i, "NSE:NIFTY50-INDEX", at(9, 15, i * 0.5)) for i in range(8)]   # every half second, 3.5 s
        groups = list(dr.batches(rows, speed=5.0))                                  # 1 s of the day per batch
        self.assertEqual([2, 2, 2, 2], [len(g) for _, g in groups])


class Answer:
    def __init__(self, status_code, body=None, text=""):
        self.status_code = status_code
        self._body = body
        self.text = text

    def json(self):
        if self._body is None:
            raise ValueError("no JSON body")
        return self._body


class Http:
    """The API as the player's poster sees it: one answer per POST, in order."""

    def __init__(self, *answers):
        self.answers = list(answers)
        self.batches = []

    def post(self, url, json=None, verify=None, timeout=None):
        self.batches.append(json)
        answer = self.answers.pop(0)
        if isinstance(answer, Exception):
            raise answer
        return answer


class BookPosterTests(unittest.TestCase):

    def poster(self, http):
        return run_replay.book_poster(http, "http://api/api/Replay/ticks", True, log=lambda line: None, sleep=lambda s: None)

    def test_the_apis_answer_that_it_opened_its_book_again_is_passed_on(self):
        http = Http(Answer(200, {"taken": 1, "reopened": True}), Answer(200, {"taken": 1, "reopened": False}))
        post = self.poster(http)
        self.assertTrue(post([{"symbol": "A"}]))
        self.assertFalse(post([{"symbol": "B"}]))

    def test_an_answer_without_the_flag_or_without_a_body_is_not_a_reopened_book(self):
        http = Http(Answer(200, {"taken": 1}), Answer(200, None), Answer(400, None, text="bad"))
        post = self.poster(http)
        self.assertFalse(post([{"symbol": "A"}]))
        self.assertFalse(post([{"symbol": "A"}]))
        self.assertFalse(post([{"symbol": "A"}]))

    def test_a_large_batch_goes_in_parts_and_any_part_can_say_the_book_was_opened_again(self):
        http = Http(Answer(200, {"reopened": False}), Answer(200, {"reopened": True}))
        ticks = [{"symbol": f"S{i}"} for i in range(run_replay.POST_LIMIT + 1)]
        self.assertTrue(self.poster(http)(ticks))
        self.assertEqual([run_replay.POST_LIMIT, 1], [len(b) for b in http.batches])

    def test_an_api_down_for_a_moment_is_tried_again_and_a_409_still_fails_the_replay(self):
        http = Http(ConnectionError("refused"), Answer(503), Answer(200, {"reopened": True}), Answer(409, {"error": "No market replay is on."}))
        post = self.poster(http)
        self.assertTrue(post([{"symbol": "A"}]))
        with self.assertRaises(RuntimeError):
            post([{"symbol": "A"}])


if __name__ == "__main__":
    unittest.main()
