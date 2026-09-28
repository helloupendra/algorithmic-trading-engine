import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

import _bootstrap  # noqa: F401

from sentinel.agents.trading import RUNNING_PATH, today_path
from sentinel.checkup import checks
from sentinel.checkup.agent import CheckupAgent
from sentinel.checkup.checks import Inputs, run_checks
from sentinel.checkup.model import Item, Report, State, Verdict
from sentinel.checkup.report import format_full, format_report
from sentinel.checkup.slots import CLOSE, MORNING, NIGHT, ON_REQUEST, WEEKLY, due
from sentinel.checkup.store import DONE, FAILED, REQUESTED, MemoryCheckupStore, items_json
from sentinel.clock import IST
from sentinel.notify import Notifier
from _sentinel_fakes import RecordingNotifier, make_context

DAY = "2026-09-28"   # a Monday


def ist(hour, minute=0, day=28, month=9):
    return datetime(2026, month, day, hour, minute, tzinfo=IST).astimezone(timezone.utc)


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ") if moment else None


def run_row(run_id, user="admin", strategy="Fulcrum", underlying="NIFTY", status="Running", active=True,
            trades=3, net=0.0, role=None):
    spot = "MCX:CRUDEOIL26OCTFUT" if underlying == "CRUDEOIL" else f"NSE:{underlying}-INDEX"
    return {"runId": run_id, "userId": 1, "userName": user, "strategyName": strategy, "underlying": underlying,
            "spotSymbol": spot, "status": status, "isActive": active, "startedUtc": iso(ist(8, 46)),
            "trades": trades, "netPnl": net, "role": role}


def provider(key, configured=True, connected=True, expires=None, needs=False):
    return {"key": key, "isConfigured": configured,
            "session": {"isConnected": connected, "needsReconnect": needs, "expiresUtc": iso(expires)}}


AUTO_OK = {"configured": True, "enabled": True, "missing": [], "morningFromIst": "08:00", "morningUntilIst": "08:40",
           "stoppedForToday": False, "lastAttemptUtc": iso(ist(8, 2)), "lastOk": True, "lastMessage": "ok"}


class FakeReads:
    """DeskReads with canned answers."""

    def __init__(self, **answers):
        self.answers = answers

    def __getattr__(self, name):
        if name not in self.answers:
            raise AssertionError(f"unexpected read {name}")
        value = self.answers[name]
        return lambda *args: value(*args) if callable(value) else value


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self.tmp.name)
        (self.repo / "logs").mkdir()
        (self.repo / "config").mkdir()
        # The desk keeps its day markers under HOME, outside the repo: a home
        # of the test's own, so no test reads this machine's.
        self.home = self.repo / "home"

    def tearDown(self):
        self.tmp.cleanup()

    def inputs(self, slot, api=None, reads=None, now=None, env=None):
        ctx = make_context(self.repo, api=api or {}, now=now or ist(8, 55), env={"HOME": str(self.home), **(env or {})})
        return Inputs(ctx, reads, slot)

    def one(self, items):
        self.assertEqual(1, len(items), items)
        return items[0]


class ModelTests(unittest.TestCase):
    def report(self, *states):
        return Report("morning", ist(8, 55), [Item(f"k{i}", "Desk", f"T{i}", s, "d") for i, s in enumerate(states)])

    def test_the_verdict_is_the_worst_state(self):
        self.assertIs(Verdict.OK, self.report(State.OK, State.INFO, State.SKIP).verdict)
        self.assertIs(Verdict.ATTENTION, self.report(State.OK, State.WARN).verdict)
        self.assertIs(Verdict.ACTION, self.report(State.WARN, State.FAIL).verdict)

    def test_the_headline_counts_what_needs_a_person(self):
        self.assertEqual("2 things to do before the open, 1 more worth a look",
                         self.report(State.FAIL, State.FAIL, State.WARN).headline("before the open"))
        self.assertEqual("1 thing worth a look after the close", self.report(State.WARN, State.OK).headline("after the close"))
        self.assertEqual("All clear before the open: 1 check fine, 2 not checked",
                         self.report(State.OK, State.SKIP, State.SKIP).headline("before the open"))

    def test_to_do_puts_failures_first(self):
        report = self.report(State.WARN, State.FAIL, State.OK)
        self.assertEqual(["T1", "T0"], [i.title for i in report.to_do])


class SlotTests(unittest.TestCase):
    def test_the_morning_checkup_is_due_at_0855_on_a_trading_day_once(self):
        self.assertIsNone(due(ist(8, 54), True, {}))
        self.assertIs(MORNING, due(ist(8, 55), True, {}))
        self.assertIsNone(due(ist(8, 56), True, {"morning": DAY}))
        self.assertIsNone(due(ist(8, 55), False, {}))

    def test_a_late_checkup_still_runs_inside_its_window(self):
        self.assertIs(MORNING, due(ist(10, 30), True, {"morning": "2026-09-25"}))
        self.assertIsNone(due(ist(12, 0), True, {}))
        self.assertIs(CLOSE, due(ist(16, 5), True, {}))

    def test_the_night_checkup_runs_every_day_and_the_weekly_one_on_sunday(self):
        self.assertIs(NIGHT, due(ist(0, 20, day=27), False, {}))
        self.assertIs(WEEKLY, due(ist(18, 0, day=27), False, {"night": "2026-09-27"}))
        self.assertIsNone(due(ist(18, 0, day=26), False, {}))   # Saturday


class DhanTokenTests(Base):
    def api(self, dhan, auto=AUTO_OK):
        return {"/api/Providers": [dhan, provider("fyers", connected=False)], "/api/Dhan/auto-sign-in": auto}

    def test_signed_out_is_a_failure_that_says_what_the_automatic_sign_in_said(self):
        auto = dict(AUTO_OK, stoppedForToday=True, lastOk=False, lastMessage="Dhan refused the PIN")
        item = self.one(checks.dhan_token(self.inputs("morning", self.api(provider("dhan", connected=False), auto))))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("stopped for today: Dhan refused the PIN", item.detail)
        self.assertIn("Connectors → Dhan → Connect", item.action)

    def test_signed_out_before_the_automatic_sign_in_has_had_its_window_is_a_note(self):
        # 28 Sep, 04:00: an on-request checkup read yesterday's expired token as
        # "1 thing to do right now", four hours before the sign-in that renews it.
        auto = dict(AUTO_OK, lastAttemptUtc=None, lastOk=None, lastMessage=None)
        signed_out = self.api(provider("dhan", connected=False), auto)
        item = self.one(checks.dhan_token(self.inputs("on-request", signed_out, now=ist(4, 0))))
        self.assertIs(State.INFO, item.state)
        self.assertEqual("Signed out; the automatic sign-in takes a new token between 08:00 and 08:40 today.",
                         item.detail)
        self.assertIs(State.FAIL, self.one(checks.dhan_token(self.inputs("morning", signed_out))).state)
        switched_off = self.api(provider("dhan", connected=False), dict(auto, enabled=False))
        self.assertIs(State.FAIL, self.one(checks.dhan_token(self.inputs("on-request", switched_off, now=ist(4, 0)))).state)

    def test_a_token_that_ends_before_the_mcx_close_is_worth_a_look(self):
        item = self.one(checks.dhan_token(self.inputs("morning", self.api(provider("dhan", expires=ist(21, 10))))))
        self.assertIs(State.WARN, item.state)
        self.assertIn("ends at 21:10 today, before today's last close at 23:30", item.detail)
        self.assertIn("before 21:10", item.action)

    def test_a_good_token_says_until_when_and_who_took_it(self):
        item = self.one(checks.dhan_token(self.inputs("morning", self.api(provider("dhan", expires=ist(8, 2, day=29))))))
        self.assertIs(State.OK, item.state)
        self.assertEqual("Signed in; the token is good until 08:02 tomorrow. Taken by the automatic sign-in at 08:02.",
                         item.detail)

    def test_on_sunday_it_asks_whether_monday_will_sign_itself_in(self):
        auto = dict(AUTO_OK, configured=False, missing=["DHAN_PIN"])
        item = self.one(checks.dhan_token(self.inputs("weekly", self.api(provider("dhan"), auto), now=ist(18, 0, day=27))))
        self.assertIs(State.WARN, item.state)
        self.assertIn("missing DHAN_PIN", item.detail)

    def test_a_desk_without_dhan_is_not_checked(self):
        items = run_checks(self.inputs("morning", {"/api/Providers": [provider("dhan", configured=False)]}), ("dhan-token",))
        self.assertIs(State.SKIP, self.one(items).state)


class StrategyTests(Base):
    def setUp(self):
        super().setUp()
        (self.repo / "config" / "morning-plan.txt").write_text(
            "accounts: admin coderforchange\nFulcrum NIFTY,BANKNIFTY 2 @admin\nGhost NIFTY 2\n", encoding="utf-8")

    def test_every_planned_run_live_is_fine(self):
        rows = [run_row(1, "admin", "Fulcrum", "NIFTY"), run_row(2, "admin", "Fulcrum", "BANKNIFTY"),
                run_row(3, "admin", "Ghost"), run_row(4, "coderforchange", "Ghost")]
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: rows})))
        self.assertIs(State.OK, item.state)
        self.assertEqual("All 4 planned runs are live (admin 3, coderforchange 1).", item.detail)

    def marker(self, *lines):
        """Today's market-open marker, as desk.sh and the job write it (daily_job in scripts/lib/desk-common.sh)."""
        folder = self.home / ".local" / "state" / "algotrading"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / f"market-open-{DAY}").write_text("".join(f"{line}\n" for line in lines), encoding="utf-8")

    def test_at_0855_a_plan_the_morning_job_has_yet_to_deploy_is_a_note(self):
        # 28 Sep: the 08:55 checkup sent "23 of 23 planned runs are not live"
        # as a failure. market-open.sh, started at 08:45, waits for the 09:15
        # open and for the spots to be priced; the 23 went live 09:16-09:19.
        (self.repo / "config" / "morning-plan.txt").write_text(
            "accounts: admin coderforchange\n"
            "GhostTangentCrossings  BANKNIFTY,NIFTY,SENSEX  2\n"
            "ChainFlowBuy           BANKNIFTY,NIFTY,SENSEX  2\n"
            "SmcStructureBreak      BANKNIFTY,NIFTY,SENSEX  2\n"
            "Fulcrum                BANKNIFTY,NIFTY,SENSEX  2  -  @admin\n"
            "CrudeMomentum          CRUDEOIL                2  -\n", encoding="utf-8")
        self.marker("started=2026-09-28 08:45:03", "pid=41822")
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: []})))
        self.assertIs(State.INFO, item.state)
        self.assertEqual("23 of 23 planned runs are not live yet: the morning job (running since 08:45) deploys the "
                         "plan after the 09:15 open, once the plan's spots are priced.", item.detail)
        self.assertIn("From 09:25 Sentinel opens an incident", item.action)
        report = Report("morning", ist(8, 55), [item])
        self.assertEqual([], report.to_do)

    def test_before_0925_a_plan_being_deployed_is_a_note_with_or_without_the_marker(self):
        rows = [run_row(1, "admin", "Fulcrum", "NIFTY"), run_row(3, "admin", "Ghost")]
        # Sentinel on a machine where the desk keeps no marker: the clock alone.
        item = self.one(checks.plan(self.inputs("on-request", {RUNNING_PATH: rows}, now=ist(9, 17))))
        self.assertIs(State.INFO, item.state)
        self.assertTrue(item.detail.startswith("2 of 4 planned runs are not live yet: the morning job deploys"),
                        item.detail)
        self.marker("started=2026-09-28 08:45:03", "pid=41822")
        item = self.one(checks.plan(self.inputs("on-request", {RUNNING_PATH: rows}, now=ist(9, 24))))
        self.assertIs(State.INFO, item.state)

    def test_a_real_miss_at_0945_is_a_failure_that_names_the_runs(self):
        rows = [run_row(1, "admin", "Fulcrum", "NIFTY"), run_row(3, "admin", "Ghost"),
                run_row(4, "coderforchange", "Ghost", active=False)]
        self.marker("started=2026-09-28 08:45:03", "pid=41822", "done=2026-09-28 09:19:40 exit=2")
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: rows}, now=ist(9, 45))))
        self.assertIs(State.FAIL, item.state)
        self.assertEqual("2 of 4 planned runs are not live: admin Fulcrum BANKNIFTY, coderforchange Ghost NIFTY. "
                         "The morning job finished at 09:19 with exit 2.", item.detail)
        self.assertEqual(f"Start them from Trade → Runs. Why they did not start is in logs/market-open-{DAY}.log.",
                         item.action)
        # Past 09:25 a missing run is missing, marker or not, the job still running or not.
        (self.home / ".local" / "state" / "algotrading" / f"market-open-{DAY}").unlink()
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: rows}, now=ist(9, 45))))
        self.assertIs(State.FAIL, item.state)
        self.assertTrue(item.detail.endswith("coderforchange Ghost NIFTY."), item.detail)
        self.marker("started=2026-09-28 08:45:03", "pid=41822")
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: rows}, now=ist(9, 45))))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("The morning job has been running since 08:45.", item.detail)

    def test_a_morning_job_that_has_ended_deploys_nothing_more_whatever_the_time(self):
        self.marker("started=2026-09-28 08:45:03", "pid=41822", "done=2026-09-28 08:47:12 exit=1")
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: []})))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("4 of 4 planned runs are not live", item.detail)
        self.assertIn("The morning job finished at 08:47 with exit 1.", item.detail)
        self.marker("started=2026-09-28 08:45:03", "pid=41822",
                    "ended=2026-09-28 08:51:30 without finishing (killed by a signal?)")
        item = self.one(checks.plan(self.inputs("morning", {RUNNING_PATH: []})))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("The morning job was interrupted at 08:51.", item.detail)

    def test_on_request_the_plan_is_checked_only_in_session_hours(self):
        items = run_checks(self.inputs("on-request", now=ist(19, 0)), ("plan",))
        self.assertIs(State.SKIP, self.one(items).state)

    def test_an_nse_run_alive_after_the_close_is_a_failure_and_crude_is_not(self):
        rows = [run_row(1, underlying="NIFTY"), run_row(2, strategy="Crude", underlying="CRUDEOIL")]
        item = self.one(checks.runs_after_close(self.inputs("close", {RUNNING_PATH: rows}, now=ist(16, 0))))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("admin Fulcrum NIFTY (#1)", item.detail)
        item = self.one(checks.runs_after_close(self.inputs("close", {RUNNING_PATH: rows[1:]}, now=ist(16, 0))))
        self.assertIs(State.OK, item.state)
        self.assertIn("1 MCX run trades on until the MCX close at 23:30", item.detail)

    def test_the_manual_book_and_alerters_are_not_strategy_runs(self):
        rows = [run_row(1, strategy="Manual"), run_row(2, strategy="Alerter", role="alerter")]
        item = self.one(checks.runs_overnight(self.inputs("night", {RUNNING_PATH: rows}, now=ist(0, 15, day=29))))
        self.assertIs(State.OK, item.state)

    def test_the_day_result_is_net_per_account(self):
        rows = [run_row(1, net=-1200.4, trades=5), run_row(2, net=300, trades=2),
                run_row(3, "coderforchange", net=0, trades=0)]
        item = self.one(checks.day_result(self.inputs("close", {today_path(DAY): rows}, now=ist(16, 0))))
        self.assertIs(State.INFO, item.state)
        self.assertEqual("admin: 2 runs, 7 trades, net −₹900; coderforchange: 1 run, 0 trades, net ₹0. "
                         "Net is after charges; paper trading.", item.detail)


class FeedTests(Base):
    def test_no_feed_before_the_open_is_a_failure(self):
        api = {"/api/Feeds": [{"key": "dhan", "displayName": "Dhan", "isRunning": False}]}
        item = self.one(checks.feeds(self.inputs("morning", api)))
        self.assertIs(State.FAIL, item.state)
        self.assertIn(f"logs/market-open-{DAY}.log", item.action)

    def test_a_feed_left_on_overnight_is_worth_a_look(self):
        api = {"/api/Feeds": [{"key": "dhan", "displayName": "Dhan", "isRunning": True}]}
        item = self.one(checks.feeds(self.inputs("night", api, now=ist(0, 15, day=29))))
        self.assertIs(State.WARN, item.state)
        self.assertIn("16 Sep", item.action)

    def test_on_request_before_the_morning_job_either_is_fine(self):
        api = {"/api/Feeds": [{"key": "dhan", "displayName": "Dhan", "isRunning": True}]}
        self.assertIs(State.OK, self.one(checks.feeds(self.inputs("on-request", api, now=ist(7, 0)))).state)


class PositionTests(Base):
    LEG = {"run": 9, "account": "admin", "symbol": "NSE:NIFTY2593025000CE", "direction": "SHORT", "quantity": 75,
           "average": 120.0, "mark": 110.0, "unrealized": 750.0, "carry": True, "stop": None, "target": None,
           "opened_utc": None, "carried_from": 41}

    def test_carried_legs_without_a_stop_say_so(self):
        reads = FakeReads(manual_book_legs=[self.LEG])
        item = self.one(checks.manual_books(self.inputs("morning", reads=reads)))
        self.assertIs(State.INFO, item.state)
        self.assertIn("1 leg held (1 ticked to carry overnight), unrealized +₹750", item.detail)
        self.assertIn("1 carried leg has no stop-loss or target", item.detail)

    def test_an_unticked_nse_leg_after_the_close_should_have_been_squared_off(self):
        reads = FakeReads(manual_book_legs=[dict(self.LEG, carry=False)])
        item = self.one(checks.manual_books(self.inputs("close", reads=reads, now=ist(16, 0))))
        self.assertIs(State.WARN, item.state)
        self.assertIn("ManualIntradaySquareOff", item.action)

    def test_stale_runs_point_at_the_cleanup_script(self):
        reads = FakeReads(stale_pending_runs=[{"run": 2, "strategy": "X", "account": "admin",
                                               "created_utc": ist(12, 0, day=17, month=8), "open_legs": 2},
                                              {"run": 23, "strategy": "Y", "account": "admin",
                                               "created_utc": ist(12, 0, day=3), "open_legs": 7}])
        item = self.one(checks.stale_runs(self.inputs("morning", reads=reads)))
        self.assertIs(State.WARN, item.state)
        self.assertIn("2 live runs never left Pending since 17 Aug, holding 9 open legs", item.detail)
        self.assertIn("./scripts/close-stale-runs.sh", item.action)

    def test_an_open_strategy_leg_after_the_close_is_a_failure(self):
        reads = FakeReads(strategy_legs_open=[{"run": 5, "strategy": "Fulcrum", "run_status": "Stopped",
                                                "account": "admin", "symbol": "NSE:NIFTY25SEP25000PE",
                                                "direction": "LONG", "quantity": 75}])
        item = self.one(checks.strategy_legs_after_close(self.inputs("close", reads=reads, now=ist(16, 0))))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("#5 Fulcrum (Stopped) NSE:NIFTY25SEP25000PE long 75", item.detail)


class AnalysisTests(Base):
    PATH = f"/api/Forecasts?from={DAY}&to={DAY}&take=1000"

    def test_no_forecast_after_0850_is_worth_a_look(self):
        item = self.one(checks.forecasts_issued(self.inputs("morning", {self.PATH: []})))
        self.assertIs(State.WARN, item.state)
        self.assertIn(f"python -m analysis issue --session {DAY}", item.action)

    def test_forecasts_are_not_judged_before_they_are_due(self):
        items = run_checks(self.inputs("morning", {self.PATH: []}, now=ist(8, 51)), ("forecasts-issued",))
        self.assertIs(State.SKIP, self.one(items).state)

    def test_scored_counts_the_ones_with_a_score(self):
        rows = [{"modelKey": "range.har", "modelVersion": "1", "scoredUtc": iso(ist(15, 50))},
                {"modelKey": "trend.logit", "modelVersion": "1", "scoredUtc": None}]
        item = self.one(checks.forecasts_scored(self.inputs("close", {self.PATH: rows}, now=ist(16, 0))))
        self.assertIs(State.WARN, item.state)
        self.assertEqual("1 of 2 of today's forecasts are scored.", item.detail)


class DeskTests(Base):
    def test_an_unhandled_high_incident_is_a_failure(self):
        reads = FakeReads(live_incidents=[
            {"id": 7, "severity": "medium", "status": "open", "title": "Feed slow", "first_seen_utc": ist(8, 0),
             "acknowledged_utc": None},
            {"id": 9, "severity": "high", "status": "open", "title": "Feed silent", "first_seen_utc": ist(8, 30),
             "acknowledged_utc": None}])
        item = self.one(checks.incidents(self.inputs("morning", reads=reads)))
        self.assertIs(State.FAIL, item.state)
        self.assertTrue(item.detail.startswith("2 open incidents: #9 [HIGH] Feed silent, #7 [MEDIUM] Feed slow"))

    def test_an_incident_acknowledged_long_ago_is_worth_a_look(self):
        reads = FakeReads(live_incidents=[
            {"id": 9, "severity": "high", "status": "acknowledged", "title": "Feed silent",
             "first_seen_utc": ist(8, 0, day=26), "acknowledged_utc": ist(9, 0, day=26)}])
        item = self.one(checks.incidents(self.inputs("morning", reads=reads)))
        self.assertIs(State.WARN, item.state)
        self.assertIn("1 was acknowledged more than a day ago", item.detail)

    def write_history(self, *records):
        (self.repo / "data").mkdir(exist_ok=True)
        (self.repo / "data" / "deploy-history.json").write_text(json.dumps(list(records)), encoding="utf-8")

    def test_a_failed_last_deploy_is_a_failure(self):
        self.write_history({"startedUtc": "2026-09-27T11:00:00Z", "outcome": "ok", "toCommit": "aaa"},
                           {"startedUtc": "2026-09-28T02:00:00Z", "outcome": "failed", "fromCommit": "aaa",
                            "toCommit": "bbb", "summary": "build failed"})
        item = self.one(checks.deploy(self.inputs("morning")))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("(aaa → bbb, 07:30 today) failed: build failed", item.detail)

    def test_commits_waiting_for_the_gate_are_noted(self):
        self.write_history({"startedUtc": "2026-09-27T11:00:00Z", "outcome": "ok", "toCommit": "aaa"})
        inp = self.inputs("close", now=ist(16, 0))
        inp.ctx.run = lambda args, **_: (0, "ccc Fix a thing\nddd Fix another\n")
        item = self.one(checks.deploy(inp))
        self.assertIs(State.INFO, item.state)
        self.assertIn("2 newer commits on GitHub wait for the deploy gate", item.detail)

    def test_a_supervisor_older_than_its_code_owes_a_restart(self):
        for rel in checks.DESK_CODE:
            (self.repo / rel).parent.mkdir(parents=True, exist_ok=True)
            (self.repo / rel).write_text("#", encoding="utf-8")
        now = ist(16, 0)
        os.utime(self.repo / "scripts/lib/desk-common.sh", (now.timestamp() - 3600, now.timestamp() - 3600))
        os.utime(self.repo / "scripts/desk.sh", (now.timestamp() - 86400 * 3, now.timestamp() - 86400 * 3))
        ps = ("  PID  PPID     ELAPSED COMMAND\n"
              "    1     0 10-00:00:00 /sbin/init\n"
              "  100     1  1-02:00:00 /bin/bash /srv/repo/scripts/desk.sh --daemon\n"
              "  200   100       30:00 /bin/bash /srv/repo/scripts/desk.sh --daemon\n")
        inp = self.inputs("close", now=now)
        inp.ctx.run = lambda args, **_: (0, ps)
        item = self.one(checks.desk_code(inp))
        self.assertIs(State.WARN, item.state)
        self.assertIn("Running since 14:00 yesterday, but scripts/lib/desk-common.sh changed at 15:00 today", item.detail)
        self.assertIn("sudo systemctl restart algotrading-desk", item.action)

    def test_disk_space_below_twenty_percent_is_worth_a_look(self):
        inp = self.inputs("morning")
        inp.ctx.run = lambda args, **_: (0, "Filesystem 1024-blocks Used Available Capacity Mounted on\n"
                                             "/dev/root 104857600 88080384 15728640 85% /\n")
        item = self.one(checks.disk(inp))
        self.assertIs(State.WARN, item.state)
        self.assertEqual("15% free (15 GB of 100 GB).", item.detail)

    def archive_log(self, day, text):
        (self.repo / "logs" / f"archive-{day}.log").write_text(text, encoding="utf-8")

    def test_the_archive_is_read_from_its_own_log(self):
        self.archive_log(DAY, "06:05:10  archive to Drive: ok, 12 file(s) verified; disk free 60G -> 60G\n")
        self.assertEqual("Ran today: 12 files copied and verified.",
                         self.one(checks.archive(self.inputs("morning"))).detail)
        self.archive_log(DAY, "06:05:10  archive to Drive: ok, 3 file(s)\n07:00:00  archive to Drive FAILED — see\n")
        self.assertIs(State.FAIL, self.one(checks.archive(self.inputs("morning"))).state)

    def test_a_week_of_failures_caught_up_by_a_later_run_is_a_note(self):
        for day in ("2026-09-23", "2026-09-24"):
            self.archive_log(day, "06:05:10  archive to Drive FAILED — see log\n")
        self.archive_log("2026-09-27", "06:05:10  archive to Drive: ok, 12 file(s) verified\n")
        inp = self.inputs("weekly", now=ist(18, 0, day=27))
        item = self.one(checks.archive(inp))
        self.assertIs(State.INFO, item.state)
        self.assertIn("failed on 2026-09-23, 2026-09-24", item.detail)
        self.assertIn("the run on 2026-09-27 caught up", item.detail)
        self.archive_log("2026-09-26", "06:05:10  archive to Drive FAILED — see log\n")
        self.archive_log("2026-09-27", "06:05:10  archive to Drive FAILED — see log\n")
        self.assertIs(State.WARN, self.one(checks.archive(inp)).state)

    def test_a_missing_archive_says_when_it_last_ran(self):
        self.archive_log("2026-09-25", "archive to Drive: ok, 1 file(s)\n")
        item = self.one(checks.archive(self.inputs("morning")))
        self.assertIs(State.WARN, item.state)
        self.assertIn("the last run was on 2026-09-25", item.detail)

    def test_before_the_0600_run_the_archive_is_judged_by_yesterday_s(self):
        self.archive_log("2026-09-27", "06:05:10  archive to Drive: ok, 12 file(s) verified\n")
        item = self.one(checks.archive(self.inputs("night", now=ist(0, 15))))
        self.assertIs(State.OK, item.state)
        self.assertEqual("Ran yesterday: 12 files copied and verified. The next run is at 06:00.", item.detail)
        self.archive_log("2026-09-27", "06:05:10  archive to Drive FAILED — see log\n")
        self.assertEqual("Yesterday's archive failed.", self.one(checks.archive(self.inputs("night", now=ist(0, 15)))).detail)
        self.assertEqual("It has not run today; the last run was on 2026-09-27.",
                         self.one(checks.archive(self.inputs("on-request", now=ist(6, 45)))).detail)

    def test_the_failover_reports_its_mode_and_what_it_would_have_done(self):
        api_dir = self.repo / "src" / "AlgoTrading.Api"
        api_dir.mkdir(parents=True)
        (api_dir / "appsettings.json").write_text(json.dumps({"FeedFailover": {"Enabled": True, "DryRun": True}}))
        reads = FakeReads(failover_events={"would_switch": 2, "switched": 0})
        item = self.one(checks.failover(self.inputs("close", reads=reads, now=ist(16, 0))))
        self.assertIs(State.INFO, item.state)
        self.assertIn("would have switched Dhan → FYERS 2 times today", item.detail)
        self.assertIn("FeedFailover:DryRun to false", item.action)
        (api_dir / "appsettings.Local.json").write_text(json.dumps({"FeedFailover": {"DryRun": False}}))
        item = self.one(checks.failover(self.inputs("close", reads=reads, now=ist(16, 0))))
        self.assertIs(State.OK, item.state)


class RecorderTests(Base):
    PATH = "/api/MarketIntelligence/status"

    def status(self, **news):
        recorder = {"name": "news", "enabled": True, "lastSuccessUtc": iso(ist(8, 50)), "lastErrorUtc": None,
                    "lastError": None, "failingSources": [], "rowsToday": 412, "overdue": False}
        recorder.update(news)
        return {"recorders": [recorder, {"name": "breadth", "enabled": True, "overdue": False, "failingSources": []},
                              {"name": "news-scoring", "enabled": False, "overdue": True}],
                "backfills": [{"dataset": "breadth", "enabled": True, "missingSessions": 0, "lastError": None}]}

    def test_recorders_on_schedule_are_fine(self):
        item = self.one(checks.recorders(self.inputs("morning", {self.PATH: self.status()})))
        self.assertIs(State.OK, item.state)
        self.assertEqual("2 recorders on schedule; 412 headlines today.", item.detail)

    def test_a_stalled_news_recorder_is_to_do(self):
        body = self.status(overdue=True, lastError="all feeds timed out", lastErrorUtc=iso(ist(8, 51)))
        item = self.one(checks.recorders(self.inputs("morning", {self.PATH: body})))
        self.assertIs(State.FAIL, item.state)
        self.assertIn("news (all feeds timed out)", item.detail)
        self.assertIn("cannot be fetched later", item.action)

    def test_a_backfill_day_it_could_not_read_is_a_note_not_a_stall(self):
        body = self.status()
        body["backfills"][0]["lastError"] = "2020-07-13: CM bhavcopy date does not parse"
        item = self.one(checks.recorders(self.inputs("morning", {self.PATH: body})))
        self.assertIs(State.INFO, item.state)
        self.assertIn("2 recorders on schedule", item.detail)
        self.assertIn("gaps it could not read: breadth (2020-07-13", item.detail)

    def test_a_running_backfill_is_a_note(self):
        body = self.status()
        body["backfills"][0]["missingSessions"] = 1738
        item = self.one(checks.recorders(self.inputs("morning", {self.PATH: body})))
        self.assertIs(State.INFO, item.state)
        self.assertIn("breadth 1,738 sessions to go", item.detail)


class CalendarTests(Base):
    def test_holidays_in_the_week_ahead_are_listed_and_next_year_is_asked_for_in_november(self):
        body = {"holidays": [{"date": "2026-11-10", "exchange": "NSE", "name": "Diwali"},
                             {"date": "2026-11-10", "exchange": "BSE", "name": "Diwali"},
                             {"date": "2026-11-10", "exchange": "MCX", "name": "Diwali", "closure": "MorningSession"},
                             {"date": "2026-12-25", "exchange": "NSE", "name": "Christmas"}],
                "exchanges": [{"exchange": "NSE", "yearsLoaded": [2026]},
                              {"exchange": "MCX", "yearsLoaded": [2026, 2027]}]}
        items = checks.calendar_week(self.inputs("weekly", {"/api/MarketCalendar?year=2026": body},
                                                 now=ist(18, 0, day=8, month=11)))
        self.assertEqual(["holidays", "next-year-holidays"], [i.key for i in items])
        self.assertEqual("Tue 10 Nov Diwali (BSE, MCX morning closed, NSE).", items[0].detail)
        self.assertEqual("2027's holidays are not loaded for NSE.", items[1].detail)
        self.assertIn("SeedData/market_calendar.json", items[1].action)


class RunnerTests(Base):
    def test_an_api_that_does_not_answer_is_one_item_not_one_per_check(self):
        items = run_checks(self.inputs("close", now=ist(16, 0)), ("runs-after-close", "day-result", "dhan-token"))
        self.assertEqual([State.SKIP, State.SKIP, State.SKIP, State.FAIL], [i.state for i in items])
        self.assertEqual("api", items[-1].key)
        self.assertIn("so 3 checks could not be made", items[-1].detail)

    def test_an_api_error_on_one_endpoint_is_worth_a_look(self):
        items = run_checks(self.inputs("morning", {"/api/Feeds": ValueError("500")}), ("feeds",))
        self.assertIs(State.WARN, self.one(items).state)
        self.assertIn("answered /api/Feeds with an error (ValueError: 500)", items[0].detail)

    def test_a_database_that_does_not_answer_is_one_item(self):
        class OperationalError(Exception):
            __module__ = "psycopg2"

        def down(*_):
            raise OperationalError("connection refused")

        reads = FakeReads(stale_pending_runs=down, orphaned_legs=down)
        items = run_checks(self.inputs("morning", reads=reads), ("stale-runs", "orphaned-legs"))
        self.assertEqual(["stale-runs", "orphaned-legs", "database"], [i.key for i in items])
        self.assertIs(State.FAIL, items[-1].state)
        self.assertIn("so 2 checks could not be made", items[-1].detail)

    def test_a_bug_in_one_check_costs_only_that_check(self):
        reads = FakeReads(stale_pending_runs=lambda *_: [{"oops": 1}], orphaned_legs=[])
        items = run_checks(self.inputs("morning", reads=reads), ("stale-runs", "orphaned-legs"))
        self.assertEqual([State.WARN, State.OK], [i.state for i in items])
        self.assertIn("A bug in Sentinel's checkup", items[0].action)


class ReportTests(unittest.TestCase):
    def report(self):
        return Report("morning", ist(8, 55), [
            Item("dhan-token", "Brokers & data", "Dhan token", State.FAIL, "Dhan is signed out.", "Sign in."),
            Item("carried", "Positions", "Manual books", State.INFO, "1 leg held."),
            Item("disk", "Desk", "Disk space", State.OK, "60% free."),
            Item("fyers", "Brokers & data", "FYERS backup", State.SKIP, "Not checked: FYERS is not set up here."),
        ], completed_utc=ist(8, 55))

    def test_the_message_leads_with_what_to_do(self):
        text = format_report(self.report(), MORNING, "https://openfno.com")
        lines = text.splitlines()
        self.assertEqual("🔴 Desk checkup · Before the open · Mon 28 Sep, 08:55", lines[0])
        self.assertEqual("1 thing to do before the open", lines[1])
        self.assertIn("✗ Dhan token — Dhan is signed out.", text)   # a "token: …" would have been masked
        self.assertIn("  → Sign in.", text)
        self.assertIn("· Manual books — 1 leg held.", text)
        self.assertIn("✓ Fine: Disk space.", text)
        self.assertIn("Not checked: FYERS backup (FYERS is not set up here).", text)
        self.assertTrue(text.endswith("https://openfno.com/system/checkups"))

    def test_the_message_is_redacted_and_capped(self):
        report = self.report()
        report.items.append(Item("x", "Desk", "Leak", State.WARN, "Authorization: Bearer abcdefghijklmnopqrstuvwxyz"
                                 + " filler" * 1000))
        text = format_report(report, MORNING)
        self.assertNotIn("abcdefghijklmnop", text)
        self.assertLessEqual(len(text), 3900)
        self.assertTrue(text.endswith("the rest is on System → Checkup."))

    def test_the_terminal_report_lists_every_item(self):
        text = format_full(self.report(), MORNING)
        self.assertIn("[      TO DO] Brokers & data · Dhan token — Dhan is signed out.", text)
        self.assertIn("[         ok] Desk · Disk space — 60% free.", text)

    def test_stored_items_are_redacted(self):
        report = self.report()
        report.items.append(Item("x", "Desk", "Leak", State.WARN, "password=hunter2"))
        self.assertNotIn("hunter2", items_json(report))
        self.assertEqual("fail", json.loads(items_json(report))[0]["state"])


class FailingNotifier(Notifier):
    def __init__(self):
        self.tries = 0

    def send(self, text):
        self.tries += 1
        return False


class AgentTests(Base):
    def agent(self, notifier=None, store=None):
        self.store = store or MemoryCheckupStore()
        self.notifier = notifier or RecordingNotifier()
        return CheckupAgent(store=self.store, reads=FakeReads(live_incidents=[], failover_events={"would_switch": 0,
                                                                                                  "switched": 0}),
                            notifier=self.notifier, host="test-box")

    CALENDAR = {"/api/MarketSession/check*": {"isMarketOpen": False, "isTradingDay": True, "isHoliday": False}}

    def ctx(self, now, runs=(), calendar=True):
        api = {"/api/Strategy/runs*": list(runs), "/api/Feeds": [], **(self.CALENDAR if calendar else {})}
        return make_context(self.repo, api=api, now=now)

    def test_a_scheduled_checkup_runs_once_is_kept_and_sent(self):
        agent = self.agent()
        self.assertEqual([], agent.check(self.ctx(ist(0, 20, day=29))))
        # All clear at night: kept, not sent.
        self.assertEqual(DONE, self.store.rows[0]["status"])
        self.assertEqual("night", self.store.rows[0]["slot"])
        self.assertEqual([], self.notifier.sent)
        agent.check(self.ctx(ist(0, 21, day=29)))
        self.assertEqual(1, len(self.store.rows))

    def test_a_night_with_something_wrong_is_sent(self):
        agent = self.agent()
        agent.check(self.ctx(ist(0, 20, day=29), runs=[run_row(1)]))
        self.assertEqual(1, len(self.notifier.sent))
        self.assertIn("Runs overnight", self.notifier.sent[0])
        self.assertIsNotNone(self.store.rows[0].get("notified_utc"))

    def test_a_requested_checkup_is_run_for_the_console_and_not_sent(self):
        agent = self.agent()
        self.store.request(ist(19, 0, day=27), by="upendra")
        agent.check(self.ctx(ist(19, 0, day=27)))
        row = self.store.rows[0]
        self.assertEqual((DONE, "on-request", "test-box"), (row["status"], row["slot"], row["host"]))
        self.assertTrue(row["items"])
        self.assertEqual([], [t for t in self.notifier.sent if "On request" in t])

    def test_a_message_telegram_did_not_take_is_tried_again(self):
        failing = FailingNotifier()
        agent = self.agent(notifier=failing)
        ctx = self.ctx(ist(0, 20, day=29), runs=[run_row(1)])
        agent.check(ctx)
        self.assertEqual(1, len(ctx.state("checkup").data["unsent"]))
        agent._notifier = RecordingNotifier()
        later = self.ctx(ist(0, 25, day=29))
        agent.check(later)
        self.assertEqual(1, len(agent._notifier.sent))
        self.assertEqual([], later.state("checkup").data["unsent"])
        self.assertIsNotNone(self.store.rows[0].get("notified_utc"))

    def test_a_checkup_that_crashes_is_recorded_and_not_repeated_every_minute(self):
        agent = self.agent()
        ctx = self.ctx(ist(0, 20, day=29))
        original = checks.run_checks
        try:
            import sentinel.checkup.agent as agent_module
            agent_module.run_checks = lambda *a: (_ for _ in ()).throw(RuntimeError("boom"))
            with self.assertRaises(RuntimeError):
                agent.check(ctx)
        finally:
            agent_module.run_checks = original
        self.assertEqual(FAILED, self.store.rows[0]["status"])
        agent.check(self.ctx(ist(0, 21, day=29)))
        self.assertEqual(1, len(self.store.rows))


    def test_a_scheduled_checkup_waits_while_the_api_restarts(self):
        agent = self.agent()
        agent.check(self.ctx(ist(0, 20, day=29), calendar=False))
        self.assertEqual([], self.store.rows)
        agent.check(self.ctx(ist(0, 21, day=29)))
        self.assertEqual(["night"], [r["slot"] for r in self.store.rows])

    def test_near_the_end_of_its_window_it_runs_whatever_the_api_says(self):
        agent = self.agent()
        agent.check(make_context(self.repo, api={}, now=ist(2, 46, day=29)))   # nothing answers
        self.assertEqual(DONE, self.store.rows[0]["status"])
        self.assertEqual(1, len(self.notifier.sent))
        self.assertIn("Desk API", self.notifier.sent[0])

    def test_a_scheduled_checkup_waits_until_it_can_be_recorded(self):
        class NoTable(MemoryCheckupStore):
            ready = False

            def start(self, slot, now_utc, host):
                return super().start(slot, now_utc, host) if self.ready else None

        agent = self.agent(store=NoTable())
        agent.check(self.ctx(ist(0, 20, day=29)))
        self.assertEqual([], self.store.rows)
        self.store.ready = True
        agent.check(self.ctx(ist(0, 22, day=29)))
        self.assertEqual(["night"], [r["slot"] for r in self.store.rows])


    def test_what_is_not_handed_in_is_wired_from_the_env(self):
        agent = CheckupAgent(store=MemoryCheckupStore(), notifier=RecordingNotifier(), host="box")
        agent._wire(make_context(self.repo, env={"POSTGRES_PASSWORD": "x"}))
        self.assertIsNotNone(agent._reads)   # the terminal's report reads the desk like the service


class StoreTests(unittest.TestCase):
    def test_a_request_is_claimed_once(self):
        store = MemoryCheckupStore()
        store.request(ist(9, 0))
        self.assertEqual(1, store.claim_request(ist(9, 1), "box"))
        self.assertIsNone(store.claim_request(ist(9, 2), "box"))
        self.assertEqual(REQUESTED, "requested")


if __name__ == "__main__":
    unittest.main()
