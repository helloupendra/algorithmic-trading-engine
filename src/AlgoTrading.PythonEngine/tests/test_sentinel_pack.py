import _bootstrap  # noqa: F401

import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from sentinel.agents.base import Agent
from sentinel.agents.trading import RUNNING_PATH
from sentinel.engine import SentinelEngine
from sentinel.model import CONTEXT_PREFIX, Finding, Severity
from sentinel.pack import ContextPack
from sentinel.store import MemoryIncidentStore, PostgresIncidentStore
from _sentinel_fakes import RecordingNotifier, clock_ticks, make_context

NOW = datetime(2026, 9, 24, 5, 58, 0, tzinfo=timezone.utc)   # Thursday 11:28:00 IST


def at_ist(hh, mm, ss=0):
    return datetime(2026, 9, 24, hh, mm, ss, tzinfo=timezone(timedelta(hours=5, minutes=30))).astimezone(timezone.utc)


def running(user, strategy="GhostTangentCrossings", underlying="NIFTY", role=None, run_id=1):
    return {"runId": run_id, "userId": 1, "userName": user, "strategyName": strategy, "underlying": underlying,
            "status": "Running", "isActive": True, "startedUtc": "2026-09-24T03:48:00Z", "role": role,
            "spotSymbol": f"NSE:{underlying}-INDEX", "trades": 3, "netPnl": 0.0}


class Clock:
    """A wall clock and a monotonic one the test moves together."""

    def __init__(self, at):
        self.at = at
        self.mono = 1000.0

    def move(self, seconds):
        self.at += timedelta(seconds=seconds)
        self.mono += seconds


class PackCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        (self.tmp / "logs").mkdir()
        self.clock = Clock(NOW)
        self.api = {RUNNING_PATH: [running("admin", run_id=1), running("admin", "Fulcrum", run_id=2),
                                   running("coderforchange", run_id=3),
                                   running("admin", "CandleAlerts", role="alerter", run_id=4),
                                   running("admin", "Manual", run_id=5)]}
        self.api_calls = []
        self.git = (0, "3f2a1b9 Make the morning job say when it fails\n")
        self.git_calls = []
        self.ctx = make_context(self.tmp, api={"/api/*": self._api})
        self.ctx.clock = lambda: self.clock.at
        self.ctx.run = self._run
        self.pack = ContextPack(self.ctx, monotonic=lambda: self.clock.mono)

    def _api(self, path):
        self.api_calls.append(path)
        body = self.api.get(path)
        if body is None or isinstance(body, Exception):
            raise body or ConnectionError(f"no route for {path}")
        return body

    def _run(self, args, timeout=20.0, cwd=None):
        self.git_calls.append(list(args))
        if isinstance(self.git, Exception):
            raise self.git
        return self.git

    def deploys(self, *records):
        (self.tmp / "data").mkdir(exist_ok=True)
        (self.tmp / "data" / "deploy-history.json").write_text(json.dumps(list(records)), encoding="utf-8")

    def write(self, name, text, mode="a"):
        with open(self.tmp / "logs" / name, mode, encoding="utf-8") as fh:
            fh.write(text)

    def gather(self, first_seen=None):
        return self.pack.gather(first_seen or self.clock.at, self.clock.at).lines

    def lines(self, first_seen=None, starting=""):
        return [line.removeprefix(CONTEXT_PREFIX) for line in self.gather(first_seen)
                if line.removeprefix(CONTEXT_PREFIX).startswith(starting)]


def deploy(finished, started=None, outcome="ok", to="3f2a1b9", commits=("3f2a1b9 Make the morning job say when it fails",
                                                                       "25130f4 Take Dhan when it comes late"),
           summary="API rebuilt and restarted"):
    started = started or finished - timedelta(seconds=50)
    return {"startedUtc": started.strftime("%Y-%m-%dT%H:%M:%SZ"), "finishedUtc": finished.isoformat(),
            "outcome": outcome, "summary": summary, "fromCommit": "25130f4", "toCommit": to,
            "commits": list(commits), "filesChanged": 4, "steps": [], "machine": "ip-172-31-5-9"}


class DeployTests(PackCase):
    def test_a_deploy_just_before_is_named_plainly(self):
        self.deploys(deploy(NOW - timedelta(minutes=4)), deploy(NOW - timedelta(days=1), to="1111111"))
        line = self.lines(starting="deployed")[0]
        self.assertTrue(line.startswith("deployed 4 min before this was first seen: 3f2a1b9, ok"), line)
        self.assertIn("Make the morning job say when it fails (+1 more)", line)
        self.assertIn("finished 11:24 IST", line)

    def test_an_older_deploy_is_only_the_last_deploy(self):
        self.deploys(deploy(NOW - timedelta(minutes=45)))
        line = self.lines(starting="last deploy")[0]
        self.assertEqual("last deploy 10:43 IST: 3f2a1b9, ok: Make the morning job say when it fails (+1 more)", line)
        self.assertEqual([], self.lines(starting="deployed"))

    def test_a_deploy_that_did_not_go_out_says_why(self):
        self.deploys(deploy(NOW - timedelta(minutes=2), outcome="failed",
                            summary="console rebuilt; API build FAILED — old API still running"))
        line = self.lines(starting="deployed")[0]
        self.assertIn("3f2a1b9, failed: console rebuilt; API build FAILED", line)

    def test_a_deploy_going_out_at_the_first_sighting(self):
        # The API restart inside a deploy is what the health agent sees before the record is written.
        self.deploys(deploy(NOW + timedelta(seconds=20), started=NOW - timedelta(seconds=70)))
        self.clock.move(30)
        line = self.lines(first_seen=NOW, starting="a deploy was going out")[0]
        self.assertIn("(11:26 IST–11:28 IST)", line)

    def test_an_escalation_names_a_deploy_after_the_first_sighting(self):
        self.deploys(deploy(NOW + timedelta(minutes=10)))
        self.clock.move(15 * 60)
        line = self.lines(first_seen=NOW, starting="deployed")[0]
        self.assertTrue(line.startswith("deployed 9 min after this was first seen"), line)

    def test_no_history_or_a_broken_one_is_a_shorter_pack(self):
        self.assertEqual([], self.lines(starting="deployed") + self.lines(starting="last deploy"))
        (self.tmp / "data").mkdir()
        (self.tmp / "data" / "deploy-history.json").write_text("{not json", encoding="utf-8")
        self.assertTrue(self.gather())   # the rest of the pack is still there
        (self.tmp / "data" / "deploy-history.json").write_text('[{"outcome": "ok"}]', encoding="utf-8")
        self.assertEqual(["last deploy ?, ok (no time recorded)"], self.lines(starting="last deploy"))


class HeadAndRunsTests(PackCase):
    def test_the_checked_out_commit_comes_from_git_log_through_the_allowlist(self):
        self.assertEqual(["checked out: 3f2a1b9 Make the morning job say when it fails"],
                         self.lines(starting="checked out"))
        self.assertEqual([["git", "log", "-1", "--format=%h %s"]], self.git_calls)

    def test_no_git_is_no_line(self):
        self.git = (127, "")
        self.assertEqual([], self.lines(starting="checked out"))
        self.pack = ContextPack(self.ctx, monotonic=lambda: self.clock.mono)
        self.git = PermissionError("not a read-only tool")
        self.assertEqual([], self.lines(starting="checked out"))

    def test_live_strategy_runs_are_counted_per_account_without_alerters_or_the_manual_book(self):
        self.assertEqual(["3 strategy runs live at 11:28 IST (admin 2, coderforchange 1)"],
                         self.lines(starting="3 strategy"))

    def test_a_burst_of_incidents_asks_the_api_once(self):
        for _ in range(5):
            self.gather()
        self.assertEqual(1, self.api_calls.count(RUNNING_PATH))
        self.assertEqual(1, len(self.git_calls))
        self.clock.move(31)
        self.gather()
        self.assertEqual(2, self.api_calls.count(RUNNING_PATH))

    def test_an_api_that_does_not_answer_is_left_alone_for_five_minutes(self):
        self.api[RUNNING_PATH] = ConnectionError("connection refused")
        self.assertEqual([], self.lines(starting="strategy"))
        self.clock.move(240)
        self.gather()
        self.assertEqual(1, self.api_calls.count(RUNNING_PATH))
        self.api[RUNNING_PATH] = [running("admin")]
        self.clock.move(61)
        self.assertEqual(["1 strategy run live at 11:33 IST (admin 1)"], self.lines(starting="1 strategy"))


class LogWindowTests(PackCase):
    def test_desk_lines_within_two_minutes_that_look_like_trouble_or_a_change(self):
        self.write("desk.log", "11:20:00  WARN: dotnet build failed; the old API keeps running. See desk.log.\n"
                               "11:26:30  API health check failed (1/3)\n"
                               "11:26:40  stopping the API (pid 4411)\n"
                               "11:26:41  starting the API (Production, http://localhost:5025)\n"
                               "11:27:05  some ordinary line\n"
                               "11:27:20    API up\n")
        lines = self.lines(starting="desk.log")
        self.assertEqual(["desk.log 11:26:40 stopping the API (pid 4411)",
                          "desk.log 11:26:41 starting the API (Production, http://localhost:5025)",
                          "desk.log 11:27:20 API up"], lines)

    def test_a_deploy_is_named_whether_or_not_it_went_well(self):
        self.write("desk.log", "11:26:10  origin/main moved: 25130f4 -> 3f2a1b9\n"
                               "11:26:50  deploy: 3f2a1b9 at 11:26 — console rebuilt; API rebuilt and restarted\n"
                               "11:27:00  deploy of 4d5e6f7 deferred — 12 live runs; it goes out by itself\n")
        self.assertEqual(["desk.log 11:26:10 origin/main moved: 25130f4 -> 3f2a1b9",
                          "desk.log 11:26:50 deploy: 3f2a1b9 at 11:26 — console rebuilt; API rebuilt and restarted",
                          "desk.log 11:27:00 deploy of 4d5e6f7 deferred — 12 live runs; it goes out by itself"],
                         self.lines(starting="desk.log"))

    def test_build_output_belongs_to_the_last_stamped_line(self):
        self.write("desk.log", "11:27:00  building 25130f4 -> 3f2a1b9 (API): 4 file(s)\n"
                               "src/Foo.cs(12,5): error CS1002: ; expected\n"
                               "11:27:40  deploy: 3f2a1b9 at 11:27 — API build FAILED — old API still running\n")
        lines = self.lines(starting="desk.log")
        self.assertIn("desk.log 11:27:00 src/Foo.cs(12,5): error CS1002: ; expected", lines)
        self.assertIn("desk.log 11:27:40 deploy: 3f2a1b9 at 11:27 — API build FAILED — old API still running", lines)

    def test_api_log_is_windowed_by_where_it_ended_two_minutes_ago(self):
        self.write("api.log", "fail: Microsoft.EntityFrameworkCore.Query[10100]\n"
                              "      An old failure from an hour ago\n")
        self.pack.mark()                                   # 11:28:00
        self.clock.move(150)                               # 11:30:30
        self.write("api.log", "fail: Microsoft.EntityFrameworkCore.Database.Command[20102]\n"
                              "      Failed executing DbCommand (5,012ms)\n"
                              "      Npgsql.PostgresException (0x80004005): 57P01: terminating connection\n"
                              "         at Npgsql.Internal.NpgsqlConnector.ReadMessage()\n"
                              "info: AlgoTrading.Api.Services.StrategyProcessRegistry[0]\n"
                              "      [strategy:Fulcrum:NIFTY:err] KeyError: 'ltp'\n"
                              "info: AlgoTrading.Api.Services.StrategyProcessRegistry[0]\n"
                              "      [strategy:Fulcrum:NIFTY] 11:30:12 entry signal\n")
        lines = self.lines(starting="api.log")
        self.assertEqual(["api.log: fail Command — Failed executing DbCommand (5,012ms)",
                          "api.log: [strategy:Fulcrum:NIFTY:err] KeyError: 'ltp'"], lines)

    def test_a_rotated_api_log_is_read_from_its_start(self):
        self.write("api.log", "x" * 5000 + "\n")
        self.pack.mark()
        self.clock.move(150)
        (self.tmp / "logs" / "api.log").rename(self.tmp / "logs" / "api-until-20260924-112900.log")
        self.write("api.log", "crit: Microsoft.AspNetCore.Hosting.Diagnostics[6]\n      Application startup exception\n")
        self.assertEqual(["api.log: crit Diagnostics — Application startup exception"], self.lines(starting="api.log"))

    def test_without_a_mark_a_file_untouched_in_the_window_says_nothing(self):
        self.write("api.log", "fail: X.Y[1]\n      Old news\n")
        old = (NOW - timedelta(minutes=10)).timestamp()
        os.utime(self.tmp / "logs" / "api.log", (old, old))
        self.assertEqual([], self.lines(starting="api.log"))
        recent = (NOW - timedelta(seconds=30)).timestamp()
        os.utime(self.tmp / "logs" / "api.log", (recent, recent))
        self.pack.mark()   # the next round: its logs are read afresh
        self.assertEqual(["api.log: fail Y — Old news"], self.lines(starting="api.log"))

    def test_lines_that_may_carry_a_secret_are_dropped_whole_and_prose_about_them_is_kept(self):
        # One spec (sentinel/notify.py): a line the redactor would mask any part
        # of is dropped whole, as the logs agent drops it; a line that only
        # talks about a password or a token is context like any other.
        self.write("api.log", "      [Dhan feed] ERROR refused: access_token=eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl\n"
                              "      [notifier] ERROR sendMessage via api.telegram.org/bot1234567890:"
                              "AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0 failed\n"  # pragma: allowlist secret
                              "      [Dhan feed] ERROR sign-in failed, totp: 482913 was not accepted\n"
                              "      [Dhan feed] ERROR the broker password was refused\n")
        lines = self.lines(starting="api.log")
        self.assertEqual(["api.log: [Dhan feed] ERROR the broker password was refused"], lines)

    def test_a_repeated_line_is_one_line_with_its_count(self):
        self.write("desk.log", "".join(f"11:27:{s:02d}  WARN: the API is not answering\n" for s in range(0, 30, 10)))
        self.assertEqual(["desk.log 11:27:20 WARN: the API is not answering (×3)"], self.lines(starting="desk.log"))

    def test_eight_lines_at_most_the_newest_shared_between_the_files(self):
        self.write("desk.log", "".join(f"11:27:{s:02d}  WARN: problem number {chr(97 + s)}\n" for s in range(10)))
        self.write("api.log", "".join(f"fail: A.B{i}[1]\n      failure {chr(97 + i)}\n" for i in range(10)))
        lines = self.lines()
        desk = [line for line in lines if line.startswith("desk.log")]
        api = [line for line in lines if line.startswith("api.log")]
        self.assertEqual((4, 4), (len(desk), len(api)))
        self.assertTrue(desk[-1].endswith("problem number j"))
        self.assertTrue(api[-1].endswith("failure j"))

    def test_a_quiet_file_leaves_its_share_to_the_other(self):
        self.write("api.log", "".join(f"fail: A.B{i}[1]\n      failure {chr(97 + i)}\n" for i in range(10)))
        self.assertEqual(8, len(self.lines(starting="api.log")))


class LaterLinesTests(PackCase):
    """The two minutes after a sighting, read once they are written."""

    def test_what_the_logs_said_next_starts_where_the_pack_stopped_reading(self):
        self.write("desk.log", "11:27:30  stopping the API (pid 4411)\n")
        self.write("api.log", "fail: Npgsql.Connection[1]\n      terminating connection due to administrator command\n")
        pack = self.pack.gather(NOW, NOW)
        self.assertIn(CONTEXT_PREFIX + "desk.log 11:27:30 stopping the API (pid 4411)", pack.lines)

        self.clock.move(130)
        (self.tmp / "logs" / "api.log").rename(self.tmp / "logs" / "api-until-20260924-112810.log")
        self.write("api.log", "crit: Microsoft.AspNetCore.Hosting.Diagnostics[6]\n"
                              "      Application startup exception\n")
        self.write("desk.log", "11:28:40  WARN: the API did not come up — restart FAILED\n"
                               "11:31:10  WARN: a line from after the two minutes\n")
        later = self.pack.later(pack)
        self.assertEqual([CONTEXT_PREFIX + "then desk.log 11:28:40 WARN: the API did not come up — restart FAILED",
                          CONTEXT_PREFIX + "then api.log: crit Diagnostics — Application startup exception"], later)

    def test_nothing_new_is_nothing(self):
        self.write("desk.log", "11:27:30  stopping the API (pid 4411)\n")
        pack = self.pack.gather(NOW, NOW)
        self.clock.move(130)
        self.assertEqual([], self.pack.later(pack))

    def test_the_engine_adds_them_quietly_two_minutes_on(self):
        self.write("desk.log", "11:27:30  stopping the API (pid 4411)\n")
        store, notes = MemoryIncidentStore(), RecordingNotifier()
        agent = ScriptedAgent([[finding()]] * 10)
        engine = SentinelEngine([agent], store, notes, self.ctx, monotonic=clock_ticks(), pack=self.pack)
        engine.run_due()
        self.write("desk.log", "11:28:20    API up\n")
        self.clock.move(60)
        engine.run_due()
        self.assertFalse(any("then " in e for e in store.rows()[0]["evidence"]), "not before two minutes")
        self.clock.move(70)
        engine.run_due()
        evidence = store.rows()[0]["evidence"]
        self.assertEqual("newest NSE tick 11:26:30 IST", evidence[0])
        self.assertIn(CONTEXT_PREFIX + "desk.log 11:27:30 stopping the API (pid 4411)", evidence)
        self.assertIn(CONTEXT_PREFIX + "then desk.log 11:28:20 API up", evidence)
        self.assertEqual(1, len(notes.sent))   # the console learns it; nobody is messaged again
        self.clock.move(200)
        engine.run_due()
        self.assertEqual(1, sum(e.startswith(CONTEXT_PREFIX + "then ") for e in store.rows()[0]["evidence"]))


class BestEffortTests(PackCase):
    def test_every_line_is_prefixed_and_short(self):
        self.deploys(deploy(NOW - timedelta(minutes=1), commits=("3f2a1b9 " + "a very long subject " * 30,)))
        pack = self.gather()
        self.assertTrue(pack)
        for line in pack:
            self.assertTrue(line.startswith(CONTEXT_PREFIX))
            self.assertLessEqual(len(line), len(CONTEXT_PREFIX) + 200)

    def test_everything_failing_is_an_empty_pack_not_an_error(self):
        self.git = RuntimeError("git exploded")
        self.api[RUNNING_PATH] = RuntimeError("500")
        (self.tmp / "logs").rmdir()
        self.assertEqual([], self.gather())


class ScriptedAgent(Agent):
    name = "scripted"
    interval_seconds = 5
    resolve_after = 2

    def __init__(self, script):
        self.script = list(script)

    def check(self, ctx):
        return self.script.pop(0) if self.script else []


def finding(sev=Severity.HIGH, evidence=("newest NSE tick 11:26:30 IST",)):
    return Finding(agent="scripted", rule="feed-silent", severity=sev, title="NSE ticks have stopped",
                   summary="No NSE tick for 90 s.", fingerprint="scripted:feed-silent:NSE", where="Redis",
                   evidence=list(evidence), suggestion="Switch to FYERS.")


class EngineWithPackTests(PackCase):
    def engine(self, script, store=None, pack=None):
        self.store = store or MemoryIncidentStore()
        self.notes = RecordingNotifier()
        return SentinelEngine([ScriptedAgent(script)], self.store, self.notes, self.ctx, monotonic=clock_ticks(),
                              pack=pack or self.pack)

    def test_a_new_incident_carries_the_pack_in_its_evidence_and_its_message(self):
        self.deploys(deploy(NOW - timedelta(minutes=3)))
        self.engine([[finding()]]).run_due()
        evidence = self.store.rows()[0]["evidence"]
        self.assertEqual("newest NSE tick 11:26:30 IST", evidence[0])
        self.assertTrue(any(e.startswith("context: deployed 3 min before this was first seen") for e in evidence))
        message = self.notes.sent[0]
        self.assertIn("Around then:\n• deployed 3 min before this was first seen", message)
        self.assertIn("• 3 strategy runs live", message)

    def test_the_pack_survives_later_sightings(self):
        e = self.engine([[finding()], [finding(evidence=("newest NSE tick 11:26:30 IST", "still silent"))]])
        e.run_due()
        self.clock.move(30)
        e.run_due()
        evidence = self.store.rows()[0]["evidence"]
        self.assertEqual(["newest NSE tick 11:26:30 IST", "still silent"], evidence[:2])
        self.assertTrue(any(line.startswith(CONTEXT_PREFIX + "checked out") for line in evidence))

    def test_an_escalation_to_high_brings_a_newer_pack(self):
        e = self.engine([[finding(Severity.MEDIUM)], [finding(Severity.CRITICAL)]])
        e.run_due()
        self.api[RUNNING_PATH] = [running("admin")]
        self.clock.move(60)
        e.run_due()
        evidence = self.store.rows()[0]["evidence"]
        self.assertIn(CONTEXT_PREFIX + "1 strategy run live at 11:29 IST (admin 1)", evidence)
        self.assertFalse(any("3 strategy runs" in line for line in evidence))
        self.assertIn("Around then:", self.notes.sent[1])

    def test_a_low_incident_turning_medium_is_not_looked_around_again(self):
        e = self.engine([[finding(Severity.LOW)], [finding(Severity.MEDIUM)]])
        e.run_due()
        self.clock.move(60)
        e.run_due()
        self.assertEqual(1, len(self.notes.sent))   # the low opening is the console's; the escalation is sent
        self.assertIn("ESCALATED [MEDIUM]", self.notes.sent[0])
        self.assertNotIn("Around then:", self.notes.sent[0])
        self.assertEqual(1, self.api_calls.count(RUNNING_PATH))

    def test_a_pack_that_fails_loses_nothing_but_itself(self):
        class Broken(ContextPack):
            def gather(self, first_seen, at):
                raise RuntimeError("bug in the pack")

            def mark(self):
                raise RuntimeError("bug in the pack")

        with self.assertLogs("sentinel.engine", level="WARNING") as logs:
            self.engine([[finding()]], pack=Broken(self.ctx)).run_due()
        self.assertIn("could not gather the context pack: bug in the pack", logs.output[0])
        self.assertEqual(1, len(self.store.rows()))
        self.assertEqual(1, len(self.notes.sent))
        self.assertNotIn("Around then:", self.notes.sent[0])

    def test_a_store_that_is_down_still_sends_the_pack(self):
        class Down(MemoryIncidentStore):
            def upsert(self, finding, now_utc):
                raise ConnectionError("database is down")

        with self.assertLogs("sentinel.engine", level="ERROR"):
            self.engine([[finding()]], store=Down()).run_due()
        self.assertIn("Around then:", self.notes.sent[0])


class FakeCursor:
    """Just enough of a psycopg2 cursor to see what the Postgres store sends."""

    def __init__(self, rows):
        self.rows = list(rows)
        self.executed = []

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def execute(self, sql, params=None):
        self.executed.append((sql, params))

    def fetchone(self):
        return self.rows.pop(0) if self.rows else None


class FakeConnection:
    closed = False

    def __init__(self, cursor):
        self._cursor = cursor

    def cursor(self):
        return self._cursor

    def commit(self):
        pass

    def rollback(self):
        pass


class PostgresPackTests(unittest.TestCase):
    def store(self, rows):
        cur = FakeCursor(rows)
        store = PostgresIncidentStore("dbname=x", Path(tempfile.mkdtemp()) / "unstored.jsonl")
        store._connection = lambda: FakeConnection(cur)
        return store, cur

    def test_a_sighting_keeps_the_pack_and_reports_when_it_was_first_seen(self):
        first = datetime(2026, 9, 24, 5, 57)   # the column comes back naive: it is UTC
        before = json.dumps(["old evidence", "context: checked out: 3f2a1b9 Subject"])
        store, cur = self.store([(41, "high", 3, before, first)])
        result = store.upsert(finding(), NOW)
        self.assertEqual((41, False, False), (result.incident_id, result.is_new, result.escalated))
        self.assertEqual(first.replace(tzinfo=timezone.utc), result.first_seen_utc)
        sql, params = cur.executed[1]
        self.assertTrue(sql.startswith("UPDATE incidents SET"))
        self.assertEqual(["newest NSE tick 11:26:30 IST", "context: checked out: 3f2a1b9 Subject"],
                         json.loads(params[3]))

    def test_attaching_a_pack_replaces_the_old_one_only(self):
        before = json.dumps(["newest NSE tick", "context: old deploy line"])
        store, cur = self.store([(before,)])
        store.attach_context(41, ["context: new deploy line", "context: checked out: 3f2a1b9"])
        sql, params = cur.executed[1]
        self.assertEqual('UPDATE incidents SET "EvidenceJson" = %s WHERE "Id" = %s', sql)
        self.assertEqual(["newest NSE tick", "context: new deploy line", "context: checked out: 3f2a1b9"],
                         json.loads(params[0]))
        self.assertEqual(41, params[1])

    def test_an_unreadable_evidence_column_is_no_evidence(self):
        store, cur = self.store([("not json",)])
        store.attach_context(7, ["context: x"])
        self.assertEqual(["context: x"], json.loads(cur.executed[1][1][0]))


if __name__ == "__main__":
    unittest.main()
