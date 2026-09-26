import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

import _bootstrap  # noqa: F401

from sentinel import engine as engine_module
from sentinel.agents.base import Agent
from sentinel.engine import SEND_PER_ROUND, SentinelEngine
from sentinel.model import Finding, Severity
from sentinel.notify import Notifier, redact
from sentinel.store import MemoryIncidentStore
from _sentinel_fakes import RecordingNotifier, clock_ticks, make_context


class ScriptedAgent(Agent):
    """Returns the next list of findings from a script on every check."""

    name = "scripted"
    interval_seconds = 5
    resolve_after = 2

    def __init__(self, script):
        self.script = list(script)

    def check(self, ctx):
        item = self.script.pop(0) if self.script else []
        if isinstance(item, Exception):
            raise item
        return item


def finding(sev=Severity.HIGH, fp="scripted:feed-silent:NSE", title="NSE feed silent", **extra):
    return Finding(agent="scripted", rule="feed-silent", severity=sev, title=title,
                   summary="No NSE tick for 120 s.", fingerprint=fp, where="Redis market:ticks",
                   evidence=["newest NSE tick 11:27:35 IST"], suggestion="Switch to the FYERS feed.", extra=extra)


class SwitchStore(MemoryIncidentStore):
    """A memory store whose database can be taken down and brought back."""

    def __init__(self, down=False):
        super().__init__()
        self.down = down

    def _up(self):
        if self.down:
            raise ConnectionError("database is down")

    def upsert(self, finding, now_utc):
        self._up()
        return super().upsert(finding, now_utc)

    def live_for_agent(self, agent):
        self._up()
        return super().live_for_agent(agent)

    def resolve(self, incident_id, now_utc):
        self._up()
        return super().resolve(incident_id, now_utc)

    def mark_notified(self, incident_id, now_utc):
        self._up()
        super().mark_notified(incident_id, now_utc)

    def heartbeat(self, now_utc):
        self._up()
        super().heartbeat(now_utc)

    def unnotified_live(self, limit):
        self._up()
        return super().unnotified_live(limit)


class Telegram(Notifier):
    """A notifier that can be down (unreachable, or a 429 with its wait), refuse, or raise."""

    def __init__(self):
        self.up = True
        self.wait = None
        self.refuse = False
        self.raise_once = None
        self.calls = 0
        self.delivered = []

    def send(self, text):
        self.calls += 1
        self.retry_after, self.refused = None, False
        if self.raise_once is not None:
            exc, self.raise_once = self.raise_once, None
            raise exc
        if self.refuse:
            self.refused = True
            return False
        if not self.up:
            self.retry_after = self.wait
            return False
        self.delivered.append(text)
        return True


class Clock:
    """A monotonic clock the test moves."""

    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t


class EngineTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.store = MemoryIncidentStore()
        self.notifier = RecordingNotifier()

    def engine(self, agent):
        return SentinelEngine([agent], self.store, self.notifier, make_context(self.tmp), monotonic=clock_ticks())

    def test_a_problem_seen_twice_is_one_incident_and_one_message(self):
        e = self.engine(ScriptedAgent([[finding()], [finding()]]))
        e.run_due()
        e.run_due()

        rows = self.store.rows()
        self.assertEqual(1, len(rows))
        self.assertEqual(2, rows[0]["occurrences"])
        self.assertEqual(1, len(self.notifier.sent))
        self.assertIn("NEW [HIGH] NSE feed silent", self.notifier.sent[0])

    def test_a_worse_sighting_escalates_and_says_so(self):
        e = self.engine(ScriptedAgent([[finding(Severity.MEDIUM)], [finding(Severity.CRITICAL)]]))
        e.run_due()
        e.run_due()

        self.assertEqual(Severity.CRITICAL, self.store.rows()[0]["severity"])
        self.assertEqual(2, len(self.notifier.sent))
        self.assertIn("ESCALATED [CRITICAL]", self.notifier.sent[1])

    def test_a_milder_sighting_never_lowers_the_severity(self):
        e = self.engine(ScriptedAgent([[finding(Severity.CRITICAL)], [finding(Severity.LOW)]]))
        e.run_due()
        e.run_due()
        self.assertEqual(Severity.CRITICAL, self.store.rows()[0]["severity"])
        self.assertEqual(1, len(self.notifier.sent))

    def test_it_resolves_after_consecutive_clean_checks_not_the_first(self):
        e = self.engine(ScriptedAgent([[finding()], [], [], []]))
        e.run_due()
        e.run_due()  # first clean check: still open
        self.assertEqual("open", self.store.rows()[0]["status"])
        e.run_due()  # second clean check: resolved
        self.assertEqual("resolved", self.store.rows()[0]["status"])
        self.assertTrue(self.notifier.sent[-1].startswith("✅ RESOLVED"))

    def test_a_flicker_resets_the_clean_count(self):
        e = self.engine(ScriptedAgent([[finding()], [], [finding()], [], []]))
        for _ in range(4):
            e.run_due()
        self.assertEqual("open", self.store.rows()[0]["status"])
        e.run_due()
        self.assertEqual("resolved", self.store.rows()[0]["status"])

    def test_a_problem_that_returns_after_resolving_opens_a_new_incident(self):
        e = self.engine(ScriptedAgent([[finding()], [], [], [finding()]]))
        for _ in range(4):
            e.run_due()
        rows = self.store.rows()
        self.assertEqual(2, len(rows))
        self.assertEqual(["resolved", "open"], [r["status"] for r in rows])

    def test_a_crashing_agent_is_reported_not_swallowed(self):
        e = self.engine(ScriptedAgent([RuntimeError("redis went away")]))
        e.run_due()
        row = self.store.rows()[0]
        self.assertEqual("agent-crashed", row["rule"])
        self.assertIn("redis went away", row["summary"])

    def test_the_same_fingerprint_twice_in_one_check_counts_once(self):
        e = self.engine(ScriptedAgent([[finding(), finding()]]))
        e.run_due()
        self.assertEqual(1, self.store.rows()[0]["occurrences"])

    def test_agents_are_not_run_before_their_interval(self):
        agent = ScriptedAgent([[finding()], [finding()]])
        agent.interval_seconds = 60
        e = SentinelEngine([agent], self.store, self.notifier, make_context(self.tmp),
                           monotonic=clock_ticks(step=10))
        e.run_due()
        e.run_due()  # only 10 s later
        self.assertEqual(1, self.store.rows()[0]["occurrences"])


class OutageTests(unittest.TestCase):
    """The database not taking writes: Telegram hears of each problem once, not of each sighting."""

    def setUp(self):
        self.store = SwitchStore(down=True)
        self.notes = RecordingNotifier()

    def engine(self, script, resolve_after=2):
        agent = ScriptedAgent(script)
        agent.resolve_after = resolve_after
        return SentinelEngine([agent], self.store, self.notes, make_context(Path(tempfile.mkdtemp())),
                              monotonic=clock_ticks())

    def test_one_message_per_problem_not_per_sighting(self):
        e = self.engine([[finding()]] * 10)
        with self.assertLogs("sentinel.engine", level="ERROR") as logs:
            for _ in range(10):
                e.run_due()
        self.assertEqual(1, len(self.notes.sent))
        self.assertIn("NEW [HIGH] NSE feed silent", self.notes.sent[0])
        self.assertIn("not stored", self.notes.sent[0])
        self.assertEqual(1, sum("could not store" in line for line in logs.output))

    def test_a_worse_sighting_is_messaged_again(self):
        e = self.engine([[finding(Severity.MEDIUM)], [finding(Severity.MEDIUM)], [finding(Severity.HIGH)],
                         [finding(Severity.HIGH)]])
        for _ in range(4):
            e.run_due()
        self.assertEqual(2, len(self.notes.sent))
        self.assertIn("ESCALATED [HIGH]", self.notes.sent[1])

    def test_a_problem_already_open_is_not_messaged_again_when_the_database_drops(self):
        self.store.down = False
        e = self.engine([[finding()]] * 5)
        e.run_due()
        self.store.down = True
        for _ in range(4):
            e.run_due()
        self.assertEqual(1, len(self.notes.sent))

    def test_what_was_told_is_not_told_again_when_the_database_comes_back(self):
        e = self.engine([[finding()]] * 3)
        e.run_due()
        self.store.down = False
        e.run_due()
        e.run_due()
        self.assertEqual(1, len(self.notes.sent))
        [row] = self.store.rows()
        self.assertIsNotNone(row["notified"], "the stored incident is marked sent: nothing re-sends it on restart")

    def test_a_problem_that_was_never_stored_and_stops_is_resolved_once(self):
        e = self.engine([[finding()], [], [], [], []])
        for _ in range(5):
            e.run_due()
        self.assertEqual(2, len(self.notes.sent))
        self.assertTrue(self.notes.sent[1].startswith("✅ RESOLVED (never stored) [HIGH] NSE feed silent"))

    def test_a_low_finding_the_database_did_not_take_is_not_messaged(self):
        e = self.engine([[finding(Severity.LOW)]] * 3)
        for _ in range(3):
            e.run_due()
        self.assertEqual([], self.notes.sent)


class DeliveryTests(unittest.TestCase):
    """A message Telegram does not take waits and goes out later; the checks never wait for it."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.store = MemoryIncidentStore()
        self.telegram = Telegram()
        self.clock = Clock()
        self.ctx = make_context(self.tmp)

    def engine(self, script, monotonic=None):
        return SentinelEngine([ScriptedAgent(script)], self.store, self.telegram, self.ctx,
                              monotonic=monotonic or self.clock)

    def run_at(self, e, t):
        self.clock.t = t
        e.run_due()

    def test_a_message_telegram_did_not_take_goes_out_on_a_later_round(self):
        e = self.engine([[finding()]] * 5)
        self.telegram.up = False
        self.run_at(e, 0)
        self.assertEqual([], self.telegram.delivered)
        self.assertIsNone(self.store.rows()[0]["notified"])
        self.telegram.up = True
        self.run_at(e, 20)
        self.assertEqual(1, len(self.telegram.delivered))
        self.assertIn("NEW [HIGH] NSE feed silent", self.telegram.delivered[0])
        self.assertIsNotNone(self.store.rows()[0]["notified"])
        self.run_at(e, 40)
        self.assertEqual(1, len(self.telegram.delivered), "delivered once, not again")

    def test_it_waits_before_trying_again_and_honours_telegram_s_wait(self):
        e = self.engine([[finding()]] * 50)
        self.telegram.up, self.telegram.wait = False, 120
        self.run_at(e, 0)
        self.assertEqual(1, self.telegram.calls)
        for t in (10, 60, 115):
            self.run_at(e, t)
        self.assertEqual(1, self.telegram.calls, "not before the 120 s Telegram asked for")
        self.telegram.up = True
        self.run_at(e, 125)
        self.assertEqual(2, self.telegram.calls)
        self.assertEqual(1, len(self.telegram.delivered))

    def test_the_pause_grows_while_telegram_stays_down(self):
        e = self.engine([[finding()]] * 100)
        self.telegram.up = False
        tries = []
        for t in range(0, 200, 5):
            before = self.telegram.calls
            self.run_at(e, t)
            if self.telegram.calls > before:
                tries.append(t)
        self.assertEqual([0, 15, 45, 105], tries[:4])

    def test_a_backlog_drains_in_order_a_few_per_round(self):
        many = [finding(fp=f"scripted:x:{i}", title=f"problem {i}") for i in range(8)]
        e = self.engine([many] + [many] * 5)
        self.telegram.up = False
        self.run_at(e, 0)
        self.telegram.up = True
        self.run_at(e, 20)
        self.assertEqual(SEND_PER_ROUND, len(self.telegram.delivered))
        self.run_at(e, 25)
        self.assertEqual([f"problem {i}" for i in range(8)],
                         [m.splitlines()[0].split("] ", 1)[1] for m in self.telegram.delivered])

    def test_a_message_refused_as_such_is_not_tried_forever(self):
        e = self.engine([[finding()]] * 5)
        self.telegram.refuse = True
        with self.assertLogs("sentinel.engine", level="ERROR"):
            self.run_at(e, 0)
        self.run_at(e, 400)
        self.assertEqual(1, self.telegram.calls)

    def test_a_notifier_that_raises_is_a_failed_send_not_a_lost_round(self):
        e = self.engine([[finding()]] * 5)
        self.telegram.raise_once = ValueError("a proxy answered with HTML")
        self.run_at(e, 0)
        self.assertIsNotNone(self.store.last_check, "the round finished and said so")
        self.run_at(e, 20)
        self.assertEqual(1, len(self.telegram.delivered))

    def test_a_message_delivered_late_says_when_it_was_due(self):
        at = {"now": datetime(2026, 9, 28, 5, 58, tzinfo=timezone.utc)}   # 11:28 IST
        self.ctx.clock = lambda: at["now"]
        e = self.engine([[finding()]] * 5)
        self.telegram.up = False
        self.run_at(e, 0)
        at["now"] += timedelta(minutes=12)
        self.telegram.up = True
        self.run_at(e, 700)
        self.assertIn("Sent late: this was due at 11:28 IST", self.telegram.delivered[0])

    def test_an_escalation_replaces_its_opening_while_both_wait(self):
        e = self.engine([[finding(Severity.MEDIUM)], [finding(Severity.CRITICAL)], []])
        self.telegram.up = False
        self.run_at(e, 0)
        self.run_at(e, 5)
        self.telegram.up = True
        self.run_at(e, 400)
        self.assertEqual(1, len(self.telegram.delivered))
        self.assertIn("[CRITICAL]", self.telegram.delivered[0])

    def test_on_start_a_live_incident_whose_message_never_went_out_is_sent(self):
        # The last process stored it, then Telegram failed and the process restarted.
        self.store.upsert(finding(), self.ctx.now())
        self.store.upsert(finding(Severity.LOW, fp="scripted:low"), self.ctx.now())
        e = self.engine([[finding()], [finding()]])
        self.run_at(e, 0)
        self.run_at(e, 10)
        self.assertEqual(1, len(self.telegram.delivered))
        self.assertIn("NEW [HIGH] NSE feed silent", self.telegram.delivered[0])
        self.assertIn("#1 · scripted/feed-silent", self.telegram.delivered[0])
        self.assertIsNotNone(self.store.rows()[0]["notified"])


class CrashTests(unittest.TestCase):
    def setUp(self):
        self.store = MemoryIncidentStore()
        self.notes = RecordingNotifier()

    def engine(self, *agents):
        return SentinelEngine(list(agents), self.store, self.notes, make_context(Path(tempfile.mkdtemp())),
                              monotonic=clock_ticks())

    def feed(self):
        return [r for r in self.store.rows() if r["fingerprint"] == "scripted:feed-silent:NSE"]

    def test_a_crashed_check_resolves_nothing_it_did_not_get_to(self):
        e = self.engine(ScriptedAgent([[finding()], RuntimeError("bug"), RuntimeError("bug"), [finding()]]))
        for _ in range(4):
            e.run_due()
        [feed] = self.feed()
        self.assertEqual(("open", 2), (feed["status"], feed["occurrences"]))
        self.assertFalse(any(m.startswith("✅ RESOLVED") for m in self.notes.sent), self.notes.sent)

    def test_a_malformed_result_is_reported_and_resolves_nothing(self):
        bad_severity = Finding(agent="scripted", rule="r", severity="high", title="t", summary="s",
                               fingerprint="scripted:r")
        for broken in (None, [finding(), "not a finding"], [bad_severity]):
            with self.subTest(broken=broken):
                self.store = MemoryIncidentStore()
                e = self.engine(ScriptedAgent([[finding()], broken, broken, [finding()]]))
                for _ in range(4):
                    e.run_due()
                [feed] = self.feed()
                self.assertEqual("open", feed["status"])
                crashed = [r for r in self.store.rows() if r["rule"] == "agent-crashed"]
                self.assertEqual(1, len(crashed))
                self.assertIn(crashed[0]["fingerprint"], ("scripted:agent-crashed:TypeError",
                                                          "scripted:agent-crashed:MalformedFinding"))

    def test_one_agent_failing_after_its_check_does_not_stop_the_others(self):
        class Broken(MemoryIncidentStore):
            def live_for_agent(self, agent):
                return [None] if agent == "broken" else super().live_for_agent(agent)

        broken = ScriptedAgent([[]])
        broken.name = "broken"
        other = ScriptedAgent([[finding()]])
        self.store = Broken()
        e = self.engine(broken, other)
        with self.assertLogs("sentinel.engine", level="ERROR"):
            e.run_due()
        self.assertEqual(1, len(self.store.rows()), "the agent after it still ran and was recorded")
        self.assertIsNotNone(self.store.last_check)

    def test_a_round_that_raises_does_not_end_the_watch(self):
        e = SentinelEngine([ScriptedAgent([])], self.store, self.notes, make_context(Path(tempfile.mkdtemp())),
                           monotonic=lambda: 0.0)
        rounds = {"n": 0}

        def run_due():
            rounds["n"] += 1
            if rounds["n"] <= 3:
                raise RuntimeError("a bug in the engine")
            return 0

        with mock.patch.object(e, "run_due", side_effect=run_due), \
                self.assertLogs("sentinel.engine", level="ERROR") as logs:
            e.run_forever(should_stop=lambda: rounds["n"] >= 5, sleep=lambda s: None)
        self.assertEqual(5, rounds["n"])
        self.assertEqual(1, len(logs.output), "the same failure is logged once, not every five seconds")


class QuietTests(unittest.TestCase):
    """Notices close without a message; low findings are the console's only."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.store = MemoryIncidentStore()
        self.notes = RecordingNotifier()

    def engine(self, script, resolve_after=1):
        agent = ScriptedAgent(script)
        agent.resolve_after = resolve_after
        return SentinelEngine([agent], self.store, self.notes, make_context(self.tmp), monotonic=clock_ticks())

    def test_a_notice_opens_with_a_message_and_closes_without_one(self):
        e = self.engine([[finding(notice=True)], []])
        e.run_due()
        e.run_due()
        self.assertEqual("resolved", self.store.rows()[0]["status"])
        self.assertEqual(1, len(self.notes.sent))
        self.assertIn("NEW [HIGH]", self.notes.sent[0])

    def test_a_notice_still_open_across_a_restart_closes_quietly(self):
        self.engine([[finding(notice=True)]]).run_due()
        self.engine([[]]).run_due()   # Sentinel restarted on a deploy of its own code
        self.assertEqual("resolved", self.store.rows()[0]["status"])
        self.assertEqual(1, len(self.notes.sent))

    def test_a_low_finding_is_the_console_s_only(self):
        e = self.engine([[finding(Severity.LOW)], [finding(Severity.LOW)], []])
        for _ in range(3):
            e.run_due()
        self.assertEqual("resolved", self.store.rows()[0]["status"])
        self.assertEqual([], self.notes.sent)

    def test_a_low_finding_that_gets_worse_is_sent_and_so_is_its_end(self):
        e = self.engine([[finding(Severity.LOW)], [finding(Severity.MEDIUM)], []])
        for _ in range(3):
            e.run_due()
        self.assertEqual(2, len(self.notes.sent))
        self.assertIn("ESCALATED [MEDIUM]", self.notes.sent[0])
        self.assertTrue(self.notes.sent[1].startswith("✅ RESOLVED #1 [MEDIUM]"))

    def test_a_resolve_that_changed_nothing_is_not_announced(self):
        class Raced(MemoryIncidentStore):
            def resolve(self, incident_id, now_utc):
                return False   # a person resolved it from the console a moment before

        self.store = Raced()
        e = self.engine([[finding()], [], []], resolve_after=2)
        for _ in range(3):
            e.run_due()
        self.assertEqual(1, len(self.notes.sent))


class HeartbeatTests(unittest.TestCase):
    def test_a_finished_round_is_recorded_even_when_nothing_is_wrong(self):
        store = MemoryIncidentStore()
        e = SentinelEngine([ScriptedAgent([[]])], store, RecordingNotifier(),
                           make_context(Path(tempfile.mkdtemp())), monotonic=clock_ticks())
        e.run_due()
        self.assertIsNotNone(store.last_check)
        self.assertEqual([], store.rows())

    def test_no_round_no_heartbeat(self):
        store = MemoryIncidentStore()
        agent = ScriptedAgent([[]])
        agent.interval_seconds = 60
        e = SentinelEngine([agent], store, RecordingNotifier(), make_context(Path(tempfile.mkdtemp())),
                           monotonic=clock_ticks(step=1))
        e.run_due()
        store.last_check = None
        e.run_due()  # one second later: nothing is due, so nothing is claimed
        self.assertIsNone(store.last_check)


class PlanTests(unittest.TestCase):
    """The plan Sentinel checks is read exactly as the morning job deploys it."""

    def test_an_at_list_limits_a_line_to_those_accounts(self):
        from sentinel.context import load_plan
        root = Path(tempfile.mkdtemp())
        (root / "config").mkdir()
        (root / "config" / "morning-plan.txt").write_text(
            "accounts: admin coderforchange\n"
            "GhostTangentCrossings BANKNIFTY,NIFTY 2\n"
            "Fulcrum BANKNIFTY,NIFTY,SENSEX 2 - @admin\n", encoding="utf-8")
        runs = load_plan(root).expected_runs()
        self.assertEqual(4 + 3, len(runs))
        self.assertIn(("admin", "Fulcrum", "SENSEX"), runs)
        self.assertNotIn(("coderforchange", "Fulcrum", "SENSEX"), runs)

    def test_the_real_plan_file_matches_the_morning_job(self):
        from sentinel.context import load_plan
        repo = Path(__file__).resolve().parents[3]
        plan = load_plan(repo)
        self.assertIsNotNone(plan)
        by_account = {}
        for account, _, _ in plan.expected_runs():
            by_account[account] = by_account.get(account, 0) + 1
        # scripts/tests/market-open-plan.test.sh asserts the same two numbers.
        self.assertEqual({"admin": 13, "coderforchange": 10}, by_account)


class RedactionTests(unittest.TestCase):
    def test_tokens_and_passwords_never_leave_the_machine(self):
        text = ('Authorization: Bearer abcdefghijklmnopqrstuvwxyz123 password=hunter2hunter2 '
                'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N '  # pragma: allowlist secret
                '1234567890:ABCdefGhIJKlmNoPQRsTUVwxyZ1234567890ab')
        out = redact(text)
        for secret in ("abcdefghijklmnopqrstuvwxyz123", "hunter2hunter2", "dozjgNryP4J3jVmNHl0w5N",
                       "ABCdefGhIJKlmNoPQRsTUVwxyZ1234567890ab"):
            self.assertNotIn(secret, out)

    def test_ordinary_text_is_left_alone(self):
        text = "NSE feed silent for 120 s; newest tick 11:27:35 IST on NSE:NIFTY50-INDEX"
        self.assertEqual(text, redact(text))


if __name__ == "__main__":
    unittest.main()
