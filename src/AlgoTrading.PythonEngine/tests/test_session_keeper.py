"""
scripts/lib/session_keeper.py — what keeps the morning plan trading through the session.

The owner, 17 Nov 2026: until Dhan's data plan is back, NIFTY must not stop under any
condition. A silent or dead feed is restarted; a planned run that ended by itself is
started again; a run a person stopped is left alone.
"""
import io
import json
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401

# The keeper lives in scripts/lib, which is not on the engine path.
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts", "lib"))

import session_keeper as sk  # noqa: E402

IST = timezone(timedelta(hours=5, minutes=30))
NOW = datetime(2026, 11, 18, 11, 0, tzinfo=IST).astimezone(timezone.utc)
USERS = [{"id": 2, "userName": "admin"}]
PLAN = [("admin", "TrendRiderV2", "NIFTY")]
FYERS_ON = [{"key": "fyers", "isRunning": True}, {"key": "angel", "isRunning": False}]
ANGEL_ON = [{"key": "fyers", "isRunning": False}, {"key": "angel", "isRunning": True}]
NONE_ON = [{"key": "fyers", "isRunning": False}, {"key": "angel", "isRunning": False}]


def quote(symbol, seconds_ago, now=NOW):
    at = (now - timedelta(seconds=seconds_ago)).isoformat().replace("+00:00", "Z")
    return {"symbol": symbol, "lastTradedPrice": 25400.0, "updatedUtc": at, "exchangeTimestampUtc": at}


def run(strategy="TrendRiderV2", underlying="NIFTY", status="Stopped", started="2026-11-18T03:48:00Z", **extra):
    row = {"userId": 2, "userName": "admin", "strategyName": strategy, "underlying": underlying,
           "status": status, "isActive": status == "Running", "startedUtc": started}
    row.update(extra)
    return row


class FeedTests(unittest.TestCase):
    def decide(self, quotes, feeds=FYERS_ON, day="trading", underlyings=("NIFTY",), preferred="fyers"):
        return sk.feed_decision(feeds, quotes, list(underlyings), NOW, day, preferred)

    def test_with_no_feed_running_the_last_one_seen_is_started(self):
        self.assertEqual(("start", "fyers"), self.decide([], feeds=NONE_ON)[:2])
        self.assertEqual(("start", "angel"), self.decide([], feeds=NONE_ON, preferred="angel")[:2])

    def test_no_status_from_the_api_is_not_a_reason_to_act(self):
        self.assertEqual("unknown", self.decide([], feeds=None)[0])
        self.assertEqual("unknown", self.decide(None)[0])

    def test_a_spot_pricing_now_is_left_alone(self):
        self.assertEqual(("ok", "fyers"), self.decide([quote("NSE:NIFTY50-INDEX", 20)])[:2])

    def test_five_minutes_without_a_price_restarts_the_feed_that_is_running(self):
        action, key, why = self.decide([quote("NSE:NIFTY50-INDEX", 400)])
        self.assertEqual(("restart", "fyers"), (action, key))
        self.assertIn("NIFTY", why)

    def test_once_switched_to_angel_angel_is_the_one_judged_and_restarted(self):
        # The owner's 18 Nov plan: Angel from 09:15, FYERS back by hand if it misbehaves.
        self.assertEqual(("ok", "angel"), self.decide([quote("NSE:NIFTY50-INDEX", 20)], feeds=ANGEL_ON)[:2])
        self.assertEqual(("restart", "angel"), self.decide([quote("NSE:NIFTY50-INDEX", 400)], feeds=ANGEL_ON)[:2])

    def test_under_five_minutes_is_left_to_the_feeds_own_watchdog(self):
        self.assertEqual("ok", self.decide([quote("NSE:NIFTY50-INDEX", 200)])[0])

    def test_only_the_plans_spots_count(self):
        quotes = [quote("NSE:NIFTY50-INDEX", 10), quote("NSE:NIFTYBANK-INDEX", 3000)]
        self.assertEqual("ok", self.decide(quotes)[0])

    def test_a_spot_never_priced_on_a_trading_day_restarts_the_feed(self):
        self.assertEqual("restart", self.decide([])[0])

    def test_nothing_since_the_open_on_an_unverified_day_waits(self):
        # Probably a holiday: restarting the feed would bring pages, not prices.
        yesterday = quote("NSE:NIFTY50-INDEX", 20 * 3600)
        self.assertEqual("wait", self.decide([yesterday], day="unknown")[0])

    def test_a_feed_that_priced_today_and_stopped_is_restarted_even_on_an_unverified_day(self):
        self.assertEqual("restart", self.decide([quote("NSE:NIFTY50-INDEX", 900)], day="unknown")[0])

    def test_a_plan_without_an_nse_or_bse_spot_is_not_judged(self):
        self.assertEqual("ok", self.decide([], underlyings=("CRUDEOIL",))[0])


class RunsTests(unittest.TestCase):
    def decide(self, running, today, expected=PLAN, users=USERS):
        return sk.runs_decision(list(expected), users, running, today)

    def test_a_live_run_is_left_alone(self):
        action, keys, _ = self.decide([run(status="Running")], [run(status="Running")])
        self.assertEqual(("ok", []), (action, keys))

    def test_a_runner_that_exited_is_started_again(self):
        ended = run(stopReason="Runner exited (code 1): MemoryError", stoppedBy="runner")
        action, keys, why = self.decide([], [ended])
        self.assertEqual("redeploy", action)
        self.assertEqual(PLAN, keys)
        self.assertIn("Runner exited", why)

    def test_a_run_an_api_restart_lost_is_started_again(self):
        lost = run(stopReason="API restarted; runner not found", stoppedBy=None)
        self.assertEqual("redeploy", self.decide([], [lost])[0])

    def test_a_planned_run_not_started_today_is_started(self):
        # The morning job failed its feed check, so nothing was started.
        self.assertEqual("redeploy", self.decide([], [])[0])

    def test_a_run_a_person_stopped_stays_stopped(self):
        stopped = run(stopReason="Stopped by admin", stoppedBy="admin")
        action, keys, why = self.decide([], [stopped])
        self.assertEqual(("hold", []), (action, keys))
        self.assertIn("Stopped by admin", why)

    def test_the_market_close_and_the_risk_guard_are_decisions_too(self):
        for by, reason in (("market-hours", "Market closed (15:30 IST)"), ("risk-guard", "Day stop-loss hit")):
            self.assertEqual("hold", self.decide([], [run(stopReason=reason, stoppedBy=by)])[0], by)

    def test_the_latest_run_of_the_day_decides(self):
        crashed = run(stopReason="Runner exited (code 1)", stoppedBy="runner", started="2026-11-18T03:48:00Z")
        stopped = run(stopReason="Stopped by admin", stoppedBy="admin", started="2026-11-18T05:00:00Z")
        self.assertEqual("hold", self.decide([], [crashed, stopped])[0])
        self.assertEqual("redeploy", self.decide([], [stopped, dict(crashed, startedUtc="2026-11-18T06:00:00Z")])[0])

    def test_only_the_run_that_ended_by_itself_is_started_again(self):
        plan = PLAN + [("admin", "TrendRider", "NIFTY")]
        today = [run(stopReason="Runner exited (code 1)", stoppedBy="runner"),
                 run(strategy="TrendRider", stopReason="Stopped by admin", stoppedBy="admin")]
        action, keys, _ = self.decide([], today, expected=plan)
        self.assertEqual("redeploy", action)
        self.assertEqual([("admin", "TrendRiderV2", "NIFTY")], keys)

    def test_a_running_row_without_a_runner_is_not_live(self):
        orphan = run(status="Running", isActive=False)
        self.assertEqual("redeploy", self.decide([orphan], [])[0])

    def test_no_running_list_from_the_api_is_not_a_reason_to_act(self):
        self.assertEqual("unknown", self.decide(None, [])[0])

    def test_the_runs_account_is_its_owner(self):
        other = dict(run(status="Running"), userId=9, userName="admin")
        users = USERS + [{"id": 9, "userName": "coderforchange"}]
        self.assertEqual("redeploy", self.decide([other], [], users=users)[0])


class CommandLineTests(unittest.TestCase):
    def test_the_script_prints_one_feed_line_and_one_runs_line(self):
        with tempfile.TemporaryDirectory() as folder:
            def save(name, body):
                with open(os.path.join(folder, name), "w", encoding="utf-8") as handle:
                    json.dump(body, handle)
            save("feeds.json", ANGEL_ON)
            save("quotes.json", [quote("NSE:NIFTY50-INDEX", 15)])
            save("users.json", USERS)
            save("running.json", [])
            save("today.json", [run(stopReason="Runner exited (code 1)", stoppedBy="runner")])
            expected = os.path.join(folder, "expected.txt")
            with open(expected, "w", encoding="utf-8") as handle:
                handle.write("admin|TrendRiderV2|NIFTY\n")
            out = io.StringIO()
            with redirect_stdout(out):
                sk.main(["--dir", folder, "--expected", expected, "--day-state", "trading",
                         "--now", NOW.isoformat()])
        lines = out.getvalue().splitlines()
        self.assertEqual(2, len(lines))
        self.assertTrue(lines[0].startswith("feed ok angel "))
        self.assertTrue(lines[1].startswith("runs redeploy admin|TrendRiderV2|NIFTY "))

    def test_a_missing_answer_reads_as_unknown(self):
        with tempfile.TemporaryDirectory() as folder:
            expected = os.path.join(folder, "expected.txt")
            with open(expected, "w", encoding="utf-8") as handle:
                handle.write("admin|TrendRiderV2|NIFTY\n")
            out = io.StringIO()
            with redirect_stdout(out):
                sk.main(["--dir", folder, "--expected", expected, "--now", NOW.isoformat()])
        self.assertEqual(["feed unknown", "runs unknown"], [" ".join(l.split()[:2]) for l in out.getvalue().splitlines()])


if __name__ == "__main__":
    unittest.main()
