"""
scripts/pager/pager.py — the phone call that wakes the owner.

Two failures matter and they pull against each other: a real outage that never
rings, and a ring on a normal trading day. Every test below is named after the
scenario it pins; most come from the 28 Sep adversarial review of v1.1 (21
confirmed findings), which replayed each one through the real rules. Nothing
here reaches the network: the API, Redis, Telegram and CallMeBot are fakes.
"""
import contextlib
import errno
import io
import itertools
import os
import sys
import tempfile
import unittest
from datetime import datetime, time as dtime, timedelta, timezone
from pathlib import Path
from unittest import mock

import _bootstrap  # noqa: F401
import requests

# The pager lives in scripts/pager, which is not on the engine path.
PAGER_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts", "pager"))
if PAGER_DIR not in sys.path:
    sys.path.insert(0, PAGER_DIR)

import pager  # noqa: E402
from pager import (CLOSED, FOUND, HEALTHY, UNKNOWN, Api, LoginRefused, Pager, Problem, Reading,  # noqa: E402
                   RedisDown, Seen, State, StreamHead, observe, problems, read_desk, read_stream, run_failed,
                   step, tally_runs)
from sentinel.clock import IST  # noqa: E402

GB = 1024 ** 3


def ist(h, m=0, s=0, day=28, month=9, year=2026):
    return datetime(year, month, day, h, m, s, tzinfo=IST).astimezone(timezone.utc)


def at_ist(now, h, m=0):
    return datetime.combine(now.astimezone(IST).date(), dtime(h, m), IST)


def healthy(now, **kw):
    """A normal session: prices fresh in Redis and the table, the plan up, Dhan signed in, crude planned."""
    base = dict(now=now, api_up=True, trading_day=True, nse_open=True, mcx_open=True, dhan=True, fyers=False,
                quotes_known=True, newest_index_tick=now - timedelta(seconds=5),
                newest_mcx_tick=now - timedelta(seconds=5), index_priced_since_open=True,
                planned=23, plan_live=23, live=23, failed=0, mcx_planned=2, mcx_expected=True, mcx_live=2,
                mcx_failed=0, last_close=at_ist(now, 23, 30), reads_ok=6, disk_free=50 * GB,
                stream=StreamHead(newest={"NSE": now - timedelta(seconds=2), "MCX": now - timedelta(seconds=2)}))
    base.update(kw)
    return Seen(**base)


def found(seen, state=None):
    state = state if state is not None else State()
    observe(state, seen)
    return problems(seen, state).found()


def verdicts(seen, state=None):
    state = state if state is not None else State()
    observe(state, seen)
    return {k: v for k, (v, _) in problems(seen, state).verdicts.items()}


def recorder(call_ok=True, configured=True):
    texts, calls = [], []

    def call(words):
        calls.append(words)
        return call_ok
    call.configured = configured
    call.last_error = "" if call_ok else "CallMeBot answered 500"
    return texts, calls, (lambda m: texts.append(m) or True), call


def drive(passes, state=None, call_ok=True, configured=True):
    """Each Seen through observe -> problems -> step, as the loop runs them."""
    state = state if state is not None else State()
    texts, calls, text, call = recorder(call_ok, configured)
    for seen in passes:
        observe(state, seen)
        step(state, seen, problems(seen, state), text, call)
    return state, texts, calls


def minutes(start, count):
    return [start + timedelta(minutes=i) for i in range(count)]


# ------------------------------------------------------------------ a fake API

def http_error(status):
    r = requests.Response()
    r.status_code = status
    return requests.HTTPError(f"HTTP {status}", response=r)


def iso(moment):
    return moment.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def calendar_routes(now, nse_trading=True, mcx_trading=True, warning=None, holiday=None):
    t = now.astimezone(IST).time()
    nse = {"isTradingDay": nse_trading, "isMarketOpen": nse_trading and dtime(9, 15) <= t < dtime(15, 30),
           "sessionOpenUtc": iso(at_ist(now, 9, 15)), "sessionCloseUtc": iso(at_ist(now, 15, 30)),
           "isHoliday": holiday is not None, "holidayName": holiday, "calendarWarning": warning}
    mcx = {"isTradingDay": mcx_trading, "isMarketOpen": mcx_trading and dtime(9, 0) <= t < dtime(23, 30),
           "sessionOpenUtc": iso(at_ist(now, 9, 0)), "sessionCloseUtc": iso(at_ist(now, 23, 30)),
           "isHoliday": not mcx_trading, "holidayName": holiday, "calendarWarning": warning}
    return {"/api/MarketSession/check?exchange=NSE": nse, "/api/MarketSession/check?exchange=MCX": mcx}


_ids = itertools.count(1)


def run_row(user, strategy, underlying, active=True, status=None, reason=None, by=None, role=None):
    return {"runId": next(_ids), "userId": user, "userName": f"user{user}", "strategyName": strategy,
            "underlying": underlying, "isActive": active, "status": status or ("Running" if active else "Stopped"),
            "stopReason": reason, "stoppedBy": by, "role": role}


PLAN_SLOTS = [(1, "Ghost", u) for u in ("NIFTY", "BANKNIFTY", "SENSEX")] + [(1, "CrudeMomentum", "CRUDEOIL")]


def plan_body(slots=PLAN_SLOTS, live=None):
    live = len(slots) if live is None else live
    return {"planned": len(slots), "live": live,
            "runs": [{"account": f"user{u}", "userId": u, "strategy": s, "underlying": x, "isLive": i < live}
                     for i, (u, s, x) in enumerate(slots)]}


def desk_routes(now, tick_age=5, index_stamp=None, providers=None, plan=None, runs=None, **calendar):
    fresh = now - timedelta(seconds=tick_age)
    routes = calendar_routes(now, **calendar)
    routes.update({
        "/api/Providers": providers if providers is not None else [
            {"key": "dhan", "session": {"isConnected": True, "needsReconnect": False,
                                        "expiresUtc": iso(now + timedelta(hours=20))}},
            {"key": "fyers", "session": {"isConnected": False, "needsReconnect": True}}],
        "/api/LiveData/latest/all": [
            {"symbol": "NSE:NIFTY50-INDEX", "lastTradedPrice": 25000, "updatedUtc": iso(fresh),
             "exchangeTimestampUtc": iso(index_stamp or fresh)},
            {"symbol": "MCX:CRUDEOIL25OCTFUT", "lastTradedPrice": 5600, "updatedUtc": iso(fresh)}],
        "/api/Desk/plan": plan if plan is not None else plan_body(),
        "/api/Strategy/runs": runs if runs is not None else [run_row(u, s, x) for u, s, x in PLAN_SLOTS],
    })
    return routes


class FakeApi:
    def __init__(self, routes=None, up=True, every=None):
        self.routes, self.up, self.every, self.paths = routes or {}, up, every, []

    def health(self):
        if isinstance(self.up, BaseException):
            raise self.up
        return self.up

    def begin_pass(self):
        pass

    def get(self, path):
        self.paths.append(path)
        if self.every is not None:
            raise self.every
        for prefix, value in self.routes.items():
            if path.startswith(prefix):
                if isinstance(value, BaseException):
                    raise value
                return value
        raise http_error(404)


def desk_pass(api, now, state, text, call, stream=None, disk=lambda: 50 * GB):
    seen = read_desk(api, now, state, stream, disk)
    observe(state, seen)
    reading = problems(seen, state)
    step(state, seen, reading, text, call)
    return seen, reading


# ================================================================== the rules

class Problems(unittest.TestCase):
    def test_a_healthy_session_has_none(self):
        self.assertEqual({}, found(healthy(ist(10, 0))))
        self.assertEqual({}, found(healthy(ist(20, 0))))

    def test_a_weekend_special_session_is_quiet_unless_runs_were_started(self):
        sunday = healthy(ist(10, 0, day=27), dhan=False, fyers=False, plan_live=0, live=0, newest_index_tick=None,
                         stream=None)
        self.assertEqual({}, found(sunday))
        started = healthy(ist(10, 0, day=27), plan_live=2, newest_index_tick=None, stream=None)
        self.assertIn("no-ticks", found(started))

    def test_runs_dropped_ignores_runs_stopped_on_purpose(self):
        # v1.1 switched the rule off because it could not tell a death from a stop. It now counts only deaths:
        # 21 runs stopped by the risk guard or by hand leave 2 live and nothing died.
        self.assertNotIn("runs-dropped", found(healthy(ist(11, 0), live=2, failed=0)))
        self.assertIn("runs-dropped", found(healthy(ist(11, 0), live=2, failed=21)))

    def test_not_a_trading_day_is_always_quiet(self):
        self.assertEqual({}, found(Seen(now=ist(10, 0), api_up=False, trading_day=False)))

    def test_api_down_only_in_its_window(self):
        self.assertIn("api-down", found(Seen(now=ist(10, 0), api_up=False, trading_day=True)))
        self.assertEqual({}, found(Seen(now=ist(8, 0), api_up=False, trading_day=True)))
        evening = Seen(now=ist(22, 0), api_up=False, trading_day=True, last_close=ist(23, 30))
        self.assertIn("api-down", found(evening))

    def test_no_broker_needs_both_out_and_known(self):
        self.assertIn("no-broker", found(healthy(ist(9, 1), dhan=False, fyers=False)))
        self.assertEqual({}, found(healthy(ist(9, 1), dhan=False, fyers=True)))
        self.assertEqual({}, found(healthy(ist(9, 1), dhan=False, fyers=None)))
        self.assertNotIn("no-broker", found(healthy(ist(8, 50), dhan=False, fyers=False)))

    def test_no_ticks_after_three_minutes_of_open_market(self):
        now = ist(10, 0)
        # The API's table stands in when the stream cannot be read.
        self.assertIn("no-ticks", found(healthy(now, stream=None, newest_index_tick=now - timedelta(seconds=200))))
        self.assertIn("no-ticks", found(healthy(now, stream=None, newest_index_tick=None)))
        self.assertNotIn("no-ticks", found(healthy(ist(9, 16), stream=None, newest_index_tick=None)))
        self.assertNotIn("no-ticks", found(healthy(now, nse_open=False, stream=None, newest_index_tick=None)))

    def test_runs_not_up_and_dropped(self):
        self.assertIn("runs-not-up", found(healthy(ist(9, 31), plan_live=0)))
        self.assertNotIn("runs-not-up", found(healthy(ist(9, 31), plan_live=0, planned=0)))
        # The manual book is "Running" all day; it must not hide a morning that deployed nothing.
        self.assertIn("runs-not-up", found(healthy(ist(9, 31), plan_live=0, live=1)))
        # 28 Sep: the morning job waits for the 09:15 open before it deploys a run.
        self.assertNotIn("runs-not-up", found(healthy(ist(9, 20), plan_live=0)))
        self.assertIn("runs-dropped", found(healthy(ist(11, 0), live=11, failed=12)))
        self.assertNotIn("runs-dropped", found(healthy(ist(11, 0), live=20, failed=3)))    # fewer than 4 died
        self.assertNotIn("runs-dropped", found(healthy(ist(15, 20), live=2, failed=21)))   # the day's own close


# ================================================================== escalation

class Escalation(unittest.TestCase):
    def run_minutes(self, count, found_for, start=ist(10, 0), state=None, call_ok=True, configured=True):
        state = state if state is not None else State()
        texts, calls, text, call = recorder(call_ok, configured)
        for i, now in enumerate(minutes(start, count)):
            got = found_for(i)
            step(state, healthy(now), got if isinstance(got, Reading) else Reading.of(got), text, call)
        return state, texts, calls

    def test_one_slow_minute_pages_nobody(self):
        _, texts, calls = self.run_minutes(5, lambda i: {"no-ticks": "x"} if i == 1 else {})
        self.assertEqual(([], []), (texts, calls))

    def test_a_lasting_problem_calls_three_times_then_only_texts(self):
        state, texts, calls = self.run_minutes(90, lambda i: {"no-ticks": "old prices"})
        self.assertEqual(3, len(calls))
        self.assertTrue(texts[0].startswith("🚨 PAGE"))
        self.assertTrue(any(t.startswith("🚨 STILL") for t in texts))
        self.assertEqual(3, state.calls_today["no-ticks"])

    def test_calls_are_ten_minutes_apart(self):
        state, _, calls = self.run_minutes(15, lambda i: {"api-down": "x"})
        self.assertEqual(2, len(calls))   # at 3 min held and 10 min later

    def test_recovery_texts_once(self):
        _, texts, _ = self.run_minutes(12, lambda i: {"no-ticks": "x"} if i < 6 else {})
        self.assertEqual(1, sum(t.startswith("✅ RESOLVED") for t in texts))

    def test_a_problem_that_cleared_before_its_grace_says_nothing(self):
        _, texts, calls = self.run_minutes(6, lambda i: {"no-ticks": "x"} if i < 2 else {})
        self.assertEqual(([], []), (texts, calls))

    def test_state_survives_a_restart(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "state.json"
            state, _, _ = self.run_minutes(5, lambda i: {"no-ticks": "x"})
            state.save(path)
            again = State.load(path)
            self.assertEqual(state.calls_today, again.calls_today)
            self.assertIn("no-ticks", again.open)
            self.assertEqual(state.open["no-ticks"], again.open["no-ticks"])

    # -- C9: a second outage of the same kind on the same day never rang

    def test_a_second_outage_of_the_same_kind_the_same_day_still_rings(self):
        state = State()
        _, _, first = self.run_minutes(30, lambda i: {"no-ticks": "x"}, start=ist(10, 0), state=state)
        self.run_minutes(10, lambda i: {}, start=ist(10, 30), state=state)
        _, texts, second = self.run_minutes(60, lambda i: {"no-ticks": "x"}, start=ist(13, 0), state=state)
        self.assertEqual(3, len(first))
        self.assertGreaterEqual(len(second), 1)
        self.assertIn("Calling the owner.", texts[0])

    def test_the_daily_ceiling_stops_a_flood_of_episodes(self):
        state, total = State(), 0
        for hour in (9, 10, 11, 12, 13):
            _, _, calls = self.run_minutes(30, lambda i: {"api-down": "x"}, start=ist(hour, 0), state=state)
            self.run_minutes(5, lambda i: {}, start=ist(hour, 30), state=state)
            total += len(calls)
        self.assertEqual(pager.DAILY_CALL_CEILING, total)
        _, texts, calls = self.run_minutes(10, lambda i: {"api-down": "x"}, start=ist(14, 0), state=state)
        self.assertEqual([], calls)
        self.assertIn("No call", texts[0])

    # -- C10: one clean pass reset the grace, so a flapping failure never paged

    def test_a_problem_present_two_passes_in_three_still_pages(self):
        _, texts, calls = self.run_minutes(30, lambda i: {"api-down": "x"} if i % 3 != 2 else {})
        self.assertTrue(texts and texts[0].startswith("🚨 PAGE"))
        self.assertGreaterEqual(len(calls), 1)

    def test_two_slow_minutes_a_few_passes_apart_page_nobody(self):
        _, texts, calls = self.run_minutes(12, lambda i: {"api-down": "x"} if i in (0, 3) else {})
        self.assertEqual(([], []), (texts, calls))

    # -- C13 / C17: a failed call was invisible, and never escalated

    def test_a_failed_call_is_counted_and_texted_and_still_texts_follow(self):
        state, texts, calls = self.run_minutes(70, lambda i: {"no-ticks": "x"}, call_ok=False)
        self.assertEqual(3, len(calls))                         # attempts count toward the cap
        self.assertEqual(3, state.open["no-ticks"].attempts)
        self.assertEqual(0, state.open["no-ticks"].placed)
        failed = [t for t in texts if t.startswith("📵 CALL FAILED")]
        self.assertEqual(1, len(failed))                        # rate-limited to one per half hour
        self.assertIn("CallMeBot answered 500", failed[0])
        self.assertGreaterEqual(sum(t.startswith("🚨 STILL") for t in texts), 1)

    def test_the_page_text_claims_a_call_only_when_one_will_be_tried(self):
        _, texts, calls = self.run_minutes(5, lambda i: {"no-ticks": "x"}, configured=False)
        self.assertEqual([], calls)
        self.assertIn("No call: PAGER_CALLMEBOT_USER is not set.", texts[0])
        self.assertNotIn("Calling the owner", texts[0])
        _, texts, calls = self.run_minutes(5, lambda i: {"no-ticks": "x"})
        self.assertIn("Calling the owner.", texts[0])
        self.assertEqual(1, len(calls))

    def test_call_sender_without_a_user_says_it_cannot_call(self):
        call = pager.call_sender(None)
        self.assertFalse(call.configured)
        with mock.patch.object(pager.requests, "get") as net:
            self.assertFalse(call("x"))
            net.assert_not_called()
        self.assertIn("PAGER_CALLMEBOT_USER", call.last_error)

    # -- C16: "could not tell" and a window's end were announced as RESOLVED

    def test_could_not_tell_holds_an_open_problem_without_resolving_it(self):
        state = State()
        self.run_minutes(6, lambda i: {"no-ticks": "x"}, state=state)
        _, texts, _ = self.run_minutes(10, lambda i: Reading.of({"api-down": "x"}, rest=UNKNOWN),
                                       start=ist(10, 6), state=state)
        self.assertFalse(any("RESOLVED" in t and "no-ticks" in t for t in texts))
        self.assertTrue(state.open["no-ticks"].paged)
        _, texts, _ = self.run_minutes(3, lambda i: {"no-ticks": "x"}, start=ist(10, 16), state=state)
        self.assertFalse(any(t.startswith("🚨 PAGE") and "no-ticks" in t for t in texts))   # same episode

    def test_a_window_ending_is_not_announced_as_resolved(self):
        state = State()
        self.run_minutes(6, lambda i: {"no-ticks": "x"}, start=ist(15, 20), state=state)
        _, texts, _ = self.run_minutes(1, lambda i: Reading.of({}, rest=CLOSED), start=ist(15, 29), state=state)
        self.assertEqual(1, len(texts))
        self.assertIn("NOT verified fixed", texts[0])
        self.assertNotIn("RESOLVED", texts[0])
        self.assertNotIn("no-ticks", state.open)

    # -- C20: yesterday's open problem paged on the first reading after a restart

    def test_a_problem_left_open_across_a_pager_restart_gets_a_fresh_grace(self):
        state = State(day="2026-09-28")
        state.open["runs-dropped"] = Problem(since=ist(15, 4).isoformat(), last_seen=ist(15, 4).isoformat(), hits=1)
        _, texts, calls = self.run_minutes(1, lambda i: {"runs-dropped": "x"}, start=ist(9, 25, day=29), state=state)
        self.assertEqual(([], []), (texts, calls))
        self.assertEqual(ist(9, 25, day=29).isoformat(), state.open["runs-dropped"].since)

    def test_a_short_restart_keeps_the_grace_it_had(self):
        state = State()
        self.run_minutes(2, lambda i: {"no-ticks": "x"}, state=state)
        _, texts, _ = self.run_minutes(3, lambda i: {"no-ticks": "x"}, start=ist(10, 3), state=state)
        self.assertTrue(texts and texts[0].startswith("🚨 PAGE"))


# ================================================================== the findings, one scenario each

class WeekendsAndHolidays(unittest.TestCase):
    # C0 / C19b: a weekend special session counted as a trading day
    def test_a_sunday_muhurat_session_with_both_brokers_signed_out_calls_nobody(self):
        sunday = [healthy(now, dhan=False, fyers=False, plan_live=0, live=0, stream=None,
                          newest_index_tick=ist(15, 29, day=25))
                  for now in minutes(ist(9, 0, day=27), 7 * 60)]
        _, texts, calls = drive(sunday)
        self.assertEqual(([], []), (texts, calls))

    def test_a_weekend_special_session_with_the_api_down_calls_nobody(self):
        texts, calls, text, call = recorder()
        state = State()
        desk_pass(FakeApi(calendar_routes(ist(9, 0, day=27))), ist(9, 0, day=27), state, text, call)
        for now in minutes(ist(9, 1, day=27), 30):
            desk_pass(FakeApi(up=False), now, state, text, call)
        self.assertEqual(([], []), (texts, calls))

    # C3 / C19a: the API down on a weekday holiday fell back to "a weekday trades"
    def test_gandhi_jayanti_with_the_api_down_calls_nobody(self):
        texts, calls, text, call = recorder()
        state = State()
        day = dict(day=2, month=10)
        desk_pass(FakeApi(calendar_routes(ist(8, 0, **day), nse_trading=False, mcx_trading=False,
                                          holiday="Mahatma Gandhi Jayanti")), ist(8, 0, **day), state, text, call)
        for now in minutes(ist(10, 0, **day), 60) + minutes(ist(22, 0, **day), 30):
            seen, _ = desk_pass(FakeApi(up=False), now, state, text, call)
            self.assertFalse(seen.trading_day)
        self.assertEqual(([], []), (texts, calls))

    def test_api_down_since_midnight_on_a_seeded_holiday_calls_nobody(self):
        seen = read_desk(FakeApi(up=False), ist(10, 0, day=2, month=10), State())
        self.assertFalse(seen.trading_day)
        self.assertIn("seeded holiday", seen.calendar)
        self.assertIn("api-down", found(read_desk(FakeApi(up=False), ist(10, 0), State())))

    def test_a_closed_mcx_does_not_stretch_the_day_to_the_night(self):
        seen = read_desk(FakeApi(desk_routes(ist(10, 0), mcx_trading=False)), ist(10, 0), State())
        self.assertIsNone(seen.last_close)
        seen = read_desk(FakeApi(desk_routes(ist(10, 0))), ist(10, 0), State())
        self.assertEqual(at_ist(ist(10, 0), 23, 30), seen.last_close)

    # C1: a holiday the calendar did not know called "the feed is down"
    def warning_day(self, warning, priced=False, day=26, month=1, year=2027):
        stale = ist(15, 29, day=25, month=1, year=2027)
        return [healthy(now, calendar_warning=warning, day_unknown=True, plan_live=0, live=0, stream=None,
                        newest_index_tick=stale, index_priced_since_open=priced)
                for now in minutes(ist(9, 15, day=day, month=month, year=year), 75)]

    def test_a_calendar_warning_day_with_nothing_priced_since_the_open_texts_once_and_never_calls(self):
        _, texts, calls = drive(self.warning_day("No NSE holiday calendar is loaded for 2027; only weekends are "
                                                 "known to be closed."))
        self.assertEqual([], calls)
        self.assertEqual(1, len(texts))
        self.assertIn("probably a holiday", texts[0])

    def test_a_feed_dead_from_the_open_on_a_day_the_calendar_vouches_for_still_rings(self):
        # Case 1 of the finding: nothing tells an unrecorded closure from the 10 Sep dead-token morning, and
        # the dead feed must ring. Only a day the calendar cannot vouch for is let off.
        stale = ist(15, 29, day=25)
        passes = [healthy(now, stream=None, newest_index_tick=stale, index_priced_since_open=False)
                  for now in minutes(ist(9, 15), 10)]
        _, texts, calls = drive(passes)
        self.assertTrue(any("no-ticks" in c or "index prices" in c for c in calls))

    def test_once_an_index_prices_after_the_open_a_warning_day_is_watched_normally(self):
        state = State()
        drive(self.warning_day("No NSE holiday calendar is loaded for 2027.", priced=True)[:3], state=state)
        stale = ist(9, 16, day=26, month=1, year=2027)
        later = [healthy(now, calendar_warning="x", day_unknown=True, stream=None, newest_index_tick=stale,
                         index_priced_since_open=False)
                 for now in minutes(ist(10, 0, day=26, month=1, year=2027), 6)]
        _, texts, calls = drive(later, state=state)
        self.assertTrue(calls)

    def test_december_notice_about_next_years_list_leaves_today_known(self):
        now = ist(10, 0, day=15, month=12)
        routes = desk_routes(now, warning="The NSE holiday calendar for 2027 is not loaded yet.")
        self.assertFalse(read_desk(FakeApi(routes), now, State()).day_unknown)
        routes = desk_routes(now, warning="No NSE holiday calendar is loaded for 2026; only weekends are known.")
        self.assertTrue(read_desk(FakeApi(routes), now, State()).day_unknown)

    def test_a_calendar_warning_is_remembered_through_an_api_outage(self):
        now = ist(9, 0, day=26, month=1, year=2027)
        state = State()
        observe(state, read_desk(FakeApi(desk_routes(now, warning="No NSE holiday calendar is loaded for 2027.")),
                                 now, state))
        self.assertTrue(read_desk(FakeApi(up=False), now + timedelta(minutes=5), state).day_unknown)


class Brokers(unittest.TestCase):
    # C4 / C18: a still-valid Dhan token from yesterday was read as signed out
    def test_a_still_valid_dhan_token_from_yesterday_counts_as_signed_in(self):
        now = ist(9, 5)
        providers = [{"key": "dhan", "session": {"isConnected": True, "needsReconnect": True,
                                                 "expiresUtc": iso(ist(14, 0))}},
                     {"key": "fyers", "session": {"isConnected": False, "needsReconnect": True}}]
        seen = read_desk(FakeApi(desk_routes(now, providers=providers)), now, State())
        self.assertTrue(seen.dhan)
        self.assertNotIn("no-broker", found(seen))
        providers[0]["session"]["expiresUtc"] = iso(ist(9, 0))
        self.assertFalse(read_desk(FakeApi(desk_routes(now, providers=providers)), now, State()).dhan)

    def test_a_dhan_token_ending_before_the_close_with_fyers_out_is_a_text_not_a_call(self):
        passes = [healthy(now, dhan_expires=ist(14, 0)) for now in minutes(ist(9, 5), 30)]
        _, texts, calls = drive(passes)
        self.assertEqual([], calls)
        self.assertEqual(1, len(texts))
        self.assertIn("Dhan's token ends at 14:00", texts[0])


class Runs(unittest.TestCase):
    # C5 / C14: the manual book counted as a live run forever
    def test_the_manual_book_and_alerters_never_count_as_live_runs(self):
        rows = [run_row(1, "Manual", "NIFTY"), run_row(1, "LogicEngine", "NIFTY", role="alerts")]
        live, died = tally_runs(rows, None)
        self.assertEqual((set(), set()), (live, died))
        self.assertIn("runs-not-up", found(healthy(ist(10, 0), plan_live=0, live=1)))

    def test_a_half_started_morning_pages_runs_not_up(self):
        passes = [healthy(now, plan_live=8, live=8) for now in minutes(ist(9, 18), 30)]
        state, texts, calls = drive(passes)
        page = next(t for t in texts if t.startswith("🚨 PAGE"))
        self.assertIn("at most 8 have been live", page)
        self.assertTrue(page.startswith("🚨 PAGE 09:4"))
        self.assertTrue(calls)

    def test_runs_that_came_up_and_later_stopped_on_their_own_rules_are_not_short(self):
        state = State()
        drive([healthy(ist(9, 20))], state=state)
        self.assertNotIn("runs-not-up", found(healthy(ist(11, 0), plan_live=5, live=5), state))

    def test_a_plan_edited_mid_day_to_ask_for_more_does_not_read_as_short(self):
        state = State()
        drive([healthy(ist(9, 20))], state=state)
        self.assertNotIn("runs-not-up", found(healthy(ist(11, 0), planned=53, plan_live=23), state))

    def test_a_missing_plan_file_is_texted_once_and_never_called(self):
        texts, calls, text, call = recorder()
        state = State()
        for moment in minutes(ist(9, 30), 20):
            routes = desk_routes(moment)
            routes["/api/Desk/plan"] = http_error(404)
            seen, reading = desk_pass(FakeApi(routes), moment, state, text, call)
        self.assertTrue(seen.plan_missing)
        self.assertEqual(CLOSED, reading.verdict("runs-not-up"))
        self.assertEqual([], calls)
        self.assertEqual(1, len(texts))
        self.assertIn("no morning plan file", texts[0])

    # C2: runs-dropped treated a stop anyone meant as a failure
    ENDED = [
        ("Stop loss hit: day loss 2000", "risk-guard", "Stopped", False),
        ("Target hit", "risk-guard", "Stopped", False),
        ("Market closed (15:30 IST)", "market-hours", "Stopped", False),
        ("Stopped by admin", "admin", "Stopped", False),
        ("Stopped by coderforchange", "coderforchange", "Stopped", False),
        ("Runner exited (code 1): 429 on /api/UserAuth/login", "runner", "Stopped", True),
        ("Runner exited (adopted after API restart; exit code unknown)", "runner", "Stopped", True),
        ("Runner exited (code 1)", "admin", "Stopped", True),
        ("API restarted; runner not found", "api", "Stopped", True),
        ("Runner failed to start.", None, "Failed", True),
        (None, None, "Stopped", True),
        ("Not recorded", None, "Stopped", True),
    ]

    def test_a_stop_anyone_meant_never_counts_as_a_death(self):
        for reason, by, status, died in self.ENDED:
            with self.subTest(reason=reason, by=by):
                row = run_row(1, "Ghost", "NIFTY", active=False, status=status, reason=reason, by=by)
                self.assertEqual(died, run_failed(row))
        self.assertFalse(run_failed(run_row(1, "Ghost", "NIFTY", active=False, status="Running")))

    def test_the_pager_reads_stop_reasons_the_way_sentinel_does(self):
        from sentinel.agents.trading import _parse_runs, ended_on_purpose
        for reason, by, status, _ in self.ENDED + [("Trailing stop hit", "", "Stopped", False),
                                                   ("something odd", "runner", "Stopped", True)]:
            with self.subTest(reason=reason, by=by):
                row = run_row(1, "Ghost", "NIFTY", active=False, status=status, reason=reason, by=by)
                self.assertEqual(not ended_on_purpose(_parse_runs([row])[0]), run_failed(row))

    def test_the_owner_stopping_most_runs_by_hand_pages_nobody(self):
        slots = [(1 + i % 2, "Ghost", f"U{i}") for i in range(23)]
        rows = [run_row(u, s, x, active=i >= 13, reason=None if i >= 13 else "Stopped by admin",
                        by=None if i >= 13 else "admin") for i, (u, s, x) in enumerate(slots)]
        live, died = tally_runs(rows, {(u, s.lower(), x) for u, s, x in slots})
        self.assertEqual((10, 0), (len(live), len(died)))

    def test_a_crash_wave_of_runners_pages_runs_dropped(self):
        now = ist(11, 0)
        slots = [(1, "Ghost", f"U{i}") for i in range(20)]
        rows = [run_row(u, s, x, active=i >= 12, reason=None if i >= 12 else "Runner exited (code 1)",
                        by=None if i >= 12 else "runner") for i, (u, s, x) in enumerate(slots)]
        routes = desk_routes(now, plan=plan_body(slots, live=8), runs=rows)
        seen = read_desk(FakeApi(routes), now, State())
        self.assertEqual((8, 12), (seen.live, seen.failed))
        self.assertIn("runs-dropped", found(seen))

    def test_a_restarted_run_replaces_its_dead_one(self):
        rows = [run_row(1, "Ghost", "NIFTY"),
                run_row(1, "Ghost", "NIFTY", active=False, reason="Runner exited (code 1)", by="runner")]
        live, died = tally_runs(rows, None)
        self.assertEqual((1, 0), (len(live), len(died)))

    def test_runs_outside_the_plan_do_not_move_the_peak(self):
        slots = {(1, "ghost", "NIFTY")}
        rows = [run_row(1, "Ghost", "NIFTY"), run_row(7, "Adhoc", "NIFTY"),
                run_row(7, "Adhoc", "BANKNIFTY", active=False, reason="Runner exited (code 1)", by="runner")]
        live, died = tally_runs(rows, slots)
        self.assertEqual(({(1, "ghost", "NIFTY")}, set()), (live, died))


class DegradedApi(unittest.TestCase):
    # C6 / C15: /health is the SPA fallback; the database down read as "all fine"
    def test_a_database_outage_behind_a_live_health_page_pages_api_degraded(self):
        texts, calls, text, call = recorder()
        state = State()
        for now in minutes(ist(11, 0), 5):
            seen, reading = desk_pass(FakeApi(every=http_error(500)), now, state, text, call)
        self.assertTrue(seen.api_up)
        self.assertEqual(FOUND, reading.verdict("api-degraded"))
        self.assertIn("HTTP 500", reading.found()["api-degraded"])
        self.assertEqual(UNKNOWN, reading.verdict("no-ticks"))   # unreadable is not "no prices"
        self.assertTrue(any("api-degraded" in t for t in texts))
        self.assertEqual(1, len(calls))

    def test_an_unreadable_quotes_answer_is_neither_a_fresh_tick_nor_no_prices(self):
        now = ist(11, 0)
        routes = desk_routes(now)
        routes["/api/LiveData/latest/all"] = http_error(500)
        seen = read_desk(FakeApi(routes), now, State())
        self.assertFalse(seen.quotes_known)
        self.assertIsNone(seen.newest_index_tick)
        self.assertEqual(UNKNOWN, verdicts(seen)["no-ticks"])

    def test_one_read_failing_for_five_minutes_pages_api_degraded(self):
        now = ist(11, 0)
        routes = desk_routes(now)
        routes["/api/Strategy/runs"] = requests.Timeout()
        state = State()
        texts, calls, text, call = recorder()
        got = []
        for moment in minutes(now, 9):
            _, reading = desk_pass(FakeApi(routes), moment, state, text, call)
            got.append(reading.verdict("api-degraded"))
        self.assertEqual([HEALTHY] * 4 + [FOUND] * 5, got)
        self.assertIn("GET /api/Strategy/runs -> Timeout", reading.found()["api-degraded"])
        self.assertTrue(calls)

    def test_a_refused_admin_sign_in_texts_that_the_pager_is_blind_and_never_calls(self):
        texts, calls, text, call = recorder()
        state = State()
        api = FakeApi(desk_routes(ist(11, 0)), every=LoginRefused("admin sign-in refused (HTTP 401)"))
        for now in minutes(ist(11, 0), 30):
            _, reading = desk_pass(api, now, state, text, call)
        self.assertEqual([], calls)
        self.assertEqual(1, len(texts))
        self.assertIn("Pager blind", texts[0])
        self.assertEqual(UNKNOWN, reading.verdict("api-degraded"))

    def test_the_admin_sign_in_is_tried_once_a_pass_and_a_refusal_is_its_own_error(self):
        class Session:
            posts = 0

            def post(self, url, timeout, json):
                Session.posts += 1
                r = requests.Response()
                r.status_code, r.url = 401, url
                return r

        api = Api("http://127.0.0.1:9", "admin", "wrong")
        api.s = Session()
        api.begin_pass()
        for _ in range(3):
            with self.assertRaises(LoginRefused):
                api.get("/api/Providers")
        self.assertEqual(1, Session.posts)
        api.begin_pass()
        with self.assertRaises(LoginRefused):
            api.get("/api/Providers")
        self.assertEqual(2, Session.posts)


class Stream(unittest.TestCase):
    # C7: the strategies read Redis; the pager read only the table
    def test_a_dead_stream_with_a_fresh_table_pages_no_ticks(self):
        now = ist(11, 0)
        dead = StreamHead(newest={"NSE": now - timedelta(minutes=5)}, scanned=500)
        detail = found(healthy(now, stream=dead))["no-ticks"]
        self.assertIn("Redis", detail)

    def test_redis_refusing_connections_pages_redis_down_not_no_ticks(self):
        got = verdicts(healthy(ist(11, 0), stream=None, redis_error="Redis unreachable (ConnectionError)"))
        self.assertEqual((FOUND, UNKNOWN), (got["redis-down"], got["no-ticks"]))
        passes = [healthy(now, stream=None, redis_error="Redis unreachable (ConnectionError)")
                  for now in minutes(ist(11, 0), 5)]
        _, texts, calls = drive(passes)
        self.assertEqual(1, len(calls))
        self.assertIn("Redis is unreachable", calls[0])

    def test_a_redis_restart_shorter_than_the_grace_pages_nobody(self):
        passes = [healthy(now, stream=None, redis_error="x") if i < 2 else healthy(now)
                  for i, now in enumerate(minutes(ist(11, 0), 8))]
        _, texts, calls = drive(passes)
        self.assertEqual(([], []), (texts, calls))

    def test_the_stream_head_is_read_the_way_sentinel_reads_it(self):
        now = ist(11, 0)
        ms = int(now.timestamp() * 1000)

        def entry(offset_s, exchange, symbol, **payload):
            body = {"symbol": symbol, "receivedUtc": iso(now - timedelta(seconds=offset_s)), **payload}
            return (f"{ms - offset_s * 1000}-0", {"exchange": exchange, "symbol": symbol,
                                                  "payload": pager.json.dumps(body)})

        class FakeRedis:
            def xrevrange(self, name, max, min, count):
                self.args = (name, max, min, count)
                return [entry(1, "NSE", "NSE:NIFTY50-INDEX", isReplay=True),
                        entry(3, "NFO", "NSE:NIFTY25SEP25000CE"),
                        entry(9, "MCX", "MCX:CRUDEOIL25OCTFUT"),
                        entry(20, "", "BSE:SENSEX-INDEX")]

        client = FakeRedis()
        head = read_stream(client, "market:ticks")
        self.assertEqual(("market:ticks", "+", "-", pager.STREAM_COUNT), client.args)
        self.assertEqual(now - timedelta(seconds=3), head.newest["NSE"])    # the replay is skipped
        self.assertEqual(now - timedelta(seconds=9), head.newest["MCX"])
        self.assertTrue(head.exhausted)

    def test_a_busy_stream_that_cannot_tell_leaves_it_to_the_table(self):
        now = ist(11, 0)
        busy = StreamHead(newest={"MCX": now}, scanned=500, oldest=now - timedelta(seconds=30))
        stale_table = healthy(now, stream=busy, newest_index_tick=now - timedelta(minutes=10))
        self.assertIn("API's table", found(stale_table)["no-ticks"])
        self.assertNotIn("no-ticks", found(healthy(now, stream=busy)))

    def test_a_connection_error_from_redis_py_becomes_redis_down(self):
        import redis

        client = mock.Mock()
        client.xrevrange.side_effect = redis.exceptions.ConnectionError("refused")
        with mock.patch("redis.Redis", return_value=client):
            read = pager.redis_stream_reader({"REDIS_HOST": "127.0.0.1", "REDIS_PORT": "9"})
        with self.assertRaises(RedisDown):
            read()
        seen = read_desk(FakeApi(desk_routes(ist(11, 0))), ist(11, 0), State(), stream=read)
        self.assertIn("ConnectionError", seen.redis_error)


class DiskAndSaves(unittest.TestCase):
    # C8: a failed save crash-looped the pager and reset every grace
    def test_a_full_disk_neither_kills_the_pager_nor_stops_its_grace(self):
        texts, calls, text, call = recorder()
        stale = ist(9, 0)
        routes = desk_routes(ist(10, 0))
        routes["/api/LiveData/latest/all"] = [{"symbol": "NSE:NIFTY50-INDEX", "updatedUtc": iso(stale)}]
        with tempfile.TemporaryDirectory() as d:
            p = Pager(Path(d) / "state.json", FakeApi(routes), text, call, disk=lambda: 1 * GB)
            full = OSError(errno.ENOSPC, "No space left on device")
            with mock.patch.object(State, "save", side_effect=full):
                for now in minutes(ist(10, 0), 6):
                    self.assertIsNotNone(p.run_pass(now))
            self.assertEqual(1, sum("cannot write its state" in t for t in texts))
            self.assertTrue(any("no-ticks" in t for t in texts if t.startswith("🚨 PAGE")))
            self.assertTrue(any("disk-low" in t for t in texts if t.startswith("🚨 PAGE")))
            self.assertTrue(calls)
            p.run_pass(ist(10, 6))   # the disk has room again: saved, and no second warning
            self.assertTrue((Path(d) / "state.json").is_file())
            self.assertEqual(1, sum("cannot write its state" in t for t in texts))

    def test_disk_low_pages_only_in_the_desks_hours(self):
        self.assertIn("disk-low", found(healthy(ist(11, 0), disk_free=2 * GB)))
        self.assertNotIn("disk-low", found(healthy(ist(11, 0), disk_free=10 * GB)))
        self.assertNotIn("disk-low", found(healthy(ist(3, 0), disk_free=1 * GB)))


class CrashLoops(unittest.TestCase):
    # C10: an API that crash-loops, each outage shorter than the grace
    def api_passes(self, pattern, start):
        return [healthy(now) if up else Seen(now=now, api_up=False, trading_day=True, nse_open=True)
                for now, up in zip(minutes(start, len(pattern)), pattern)]

    def test_an_api_crash_loop_pages_even_when_each_outage_is_short(self):
        # Down one or two checks, up three or four: never three minutes down in a row.
        pattern = ([False, True, True, True] + [False, False, True, True, True]) * 4
        _, texts, calls = drive(self.api_passes(pattern, ist(10, 0)))
        page = next(t for t in texts if t.startswith("🚨 PAGE"))
        self.assertIn("api-down", page)
        self.assertTrue(calls)

    def test_the_0845_restart_alone_pages_nobody(self):
        pattern = [True] * 5 + [False] + [True] * 40
        _, texts, calls = drive(self.api_passes(pattern, ist(8, 40)))
        self.assertEqual(([], []), (texts, calls))

    def test_deploys_before_the_gate_shuts_do_not_add_up_to_a_crash_loop(self):
        pattern = [False, True, True, True, True, False, True, True, True, True, True, True, True, True, True,
                   False] + [True] * 20
        _, texts, calls = drive(self.api_passes(pattern, ist(8, 30)))
        self.assertEqual(([], []), (texts, calls))

    def test_api_down_passes_leave_the_other_problems_open(self):
        stale = ist(10, 50)
        passes = [healthy(now, stream=None, newest_index_tick=stale) for now in minutes(ist(10, 58), 7)]
        passes += [Seen(now=now, api_up=False, trading_day=True, nse_open=True) for now in minutes(ist(11, 5), 2)]
        passes += [healthy(now, stream=None, newest_index_tick=stale) for now in minutes(ist(11, 7), 3)]
        state, texts, _ = drive(passes)
        self.assertEqual(1, sum(t.startswith("🚨 PAGE") and "no-ticks" in t for t in texts))
        self.assertFalse(any("RESOLVED" in t and "no-ticks" in t for t in texts))


class McxEvening(unittest.TestCase):
    # C11: crude runs to 23:30 and only api-down watched the evening
    def test_a_silent_mcx_evening_pages_when_crude_runs_are_planned(self):
        now = ist(18, 0)
        dead = StreamHead(newest={"MCX": now - timedelta(minutes=8)}, scanned=500)
        self.assertIn("mcx-no-ticks", found(healthy(now, nse_open=False, stream=dead)))
        self.assertEqual({}, found(healthy(now, nse_open=False)))

    def test_no_evening_rules_without_mcx_runs_in_the_plan(self):
        now = ist(20, 0)
        got = verdicts(healthy(now, nse_open=False, mcx_planned=0, mcx_expected=False, stream=StreamHead(),
                               dhan=False, fyers=False))
        self.assertEqual({CLOSED}, set(got.values()))
        state = State(day="2026-09-28", mcx_expected=False)
        self.assertEqual({}, found(Seen(now=now, api_up=False, trading_day=True, last_close=ist(23, 30)), state))

    def test_crude_runners_that_died_in_the_evening_page_but_stopped_ones_do_not(self):
        now = ist(19, 0)
        self.assertIn("mcx-runs-dropped", found(healthy(now, nse_open=False, mcx_live=0, mcx_failed=2)))
        self.assertNotIn("mcx-runs-dropped", found(healthy(now, nse_open=False, mcx_live=0, mcx_failed=0)))

    def test_both_brokers_signed_out_in_the_evening_pages_when_crude_is_planned(self):
        self.assertIn("no-broker", found(healthy(ist(19, 0), nse_open=False, dhan=False, fyers=False)))

    def test_the_evening_rules_stop_five_minutes_before_the_mcx_close(self):
        got = verdicts(healthy(ist(23, 26), nse_open=False, mcx_live=0, mcx_failed=2, stream=StreamHead()))
        self.assertEqual(CLOSED, got["mcx-no-ticks"])
        self.assertEqual(CLOSED, got["mcx-runs-dropped"])
        self.assertEqual(HEALTHY, got["api-down"])


class Watcher(unittest.TestCase):
    # C12: nothing watched the watcher
    def test_the_heartbeat_is_pinged_in_the_desks_hours_and_never_raises(self):
        ping = pager.heartbeat_sender("https://hc-ping.example/uuid")
        with mock.patch.object(pager.requests, "get", side_effect=RuntimeError("boom")) as net:
            self.assertFalse(ping(ist(10, 0)))
            self.assertEqual(1, net.call_count)
            ping(ist(7, 59))
            ping(ist(10, 0, day=26))   # a Saturday
            self.assertEqual(1, net.call_count)
        ok = mock.Mock(ok=True)
        with mock.patch.object(pager.requests, "get", return_value=ok) as net:
            self.assertTrue(ping(ist(23, 50)))
            net.assert_called_once_with("https://hc-ping.example/uuid", timeout=10)
        self.assertIsNone(pager.heartbeat_sender(""))

    def test_each_completed_pass_pings_and_a_failed_pass_resolves_nothing_and_does_not(self):
        texts, calls, text, call = recorder()
        pings = []
        with tempfile.TemporaryDirectory() as d:
            p = Pager(Path(d) / "state.json", FakeApi(desk_routes(ist(10, 0))), text, call,
                      heartbeat=lambda now: pings.append(now) or True)
            p.state.open["no-ticks"] = Problem(since=ist(9, 50).isoformat(), last_seen=ist(9, 59).isoformat(),
                                               hits=9, paged=True, attempts=1, placed=1)
            p.api = FakeApi(up=RuntimeError("a bug in a read"))
            self.assertIsNone(p.run_pass(ist(10, 0)))
            self.assertEqual([], pings)
            self.assertIn("no-ticks", p.state.open)
            self.assertEqual([], texts)
            p.api = FakeApi(desk_routes(ist(10, 1)))
            self.assertIsNotNone(p.run_pass(ist(10, 1)))
            self.assertEqual([ist(10, 1)], pings)


class Restarts(unittest.TestCase):
    # C20: yesterday's peak was compared with this morning's first reading
    def test_yesterdays_peak_is_not_compared_with_today(self):
        state = State(day="2026-09-28", peak_runs=23, peak_plan_live=23, min_planned=23)
        self.assertEqual({}, found(healthy(ist(9, 25, day=29), plan_live=8, live=8, failed=0), state))
        self.assertEqual(8, state.peak_runs)

    def test_a_v1_state_file_still_loads(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "state.json"
            path.write_text(pager.json.dumps({
                "day": "2026-09-28", "day_max_live": 24, "last_close": "2026-09-28T18:00:00+00:00",
                "calls_today": {"no-ticks": 2},
                "open": {"no-ticks": {"since": ist(10, 0).isoformat(), "paged": True, "calls": 2,
                                      "last_call": ist(10, 13).isoformat(), "last_text": ist(10, 3).isoformat()}}}))
            state = State.load(path)
        self.assertEqual({"no-ticks": 2}, state.calls_today)
        self.assertEqual((2, 2, True), (state.open["no-ticks"].attempts, state.open["no-ticks"].placed,
                                        state.open["no-ticks"].paged))

    def test_a_corrupt_state_file_starts_fresh(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "state.json"
            path.write_text("{not json")
            self.assertEqual(State(), State.load(path))
            # A wrong type would fail every pass after the load, so it is refused at the load.
            path.write_text('{"open": {"no-ticks": "x"}, "calls_today": 5}')
            self.assertEqual(State(), State.load(path))
            path.write_text('{"day": "2026-09-28", "open": {"no-ticks": {"since": "2026-09-28T04:30:00+00:00", '
                            '"hits": "3"}}}')
            self.assertEqual(State(), State.load(path))
            fine = State(day="2026-09-28", min_planned=23, mcx_expected=True, kept={"day": "2026-09-28"})
            fine.save(path)
            self.assertEqual(fine, State.load(path))


class Once(unittest.TestCase):
    def test_once_with_no_api_reachable_prints_and_pages_nothing(self):
        env = {"API_BASE_URL": "http://127.0.0.1:9", "REDIS_HOST": "127.0.0.1", "REDIS_PORT": "9"}
        out = io.StringIO()
        with mock.patch.object(pager, "load_env", return_value=env), \
                mock.patch.object(pager.logging, "basicConfig"), \
                mock.patch.object(pager.requests, "post") as post, \
                mock.patch.object(pager.State, "save") as save, \
                contextlib.redirect_stdout(out):
            self.assertEqual(0, pager.main(["--once"]))
        post.assert_not_called()
        save.assert_not_called()
        self.assertIn('"api_up": false', out.getvalue())
        self.assertIn("problems:", out.getvalue())


if __name__ == "__main__":
    unittest.main()
