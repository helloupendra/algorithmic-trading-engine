import tempfile
import unittest
from pathlib import Path

import _bootstrap  # noqa: F401

from sentinel.agents.base import Agent
from sentinel.engine import SentinelEngine
from sentinel.model import Finding, Severity
from sentinel.notify import redact
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


def finding(sev=Severity.HIGH, fp="scripted:feed-silent:NSE", title="NSE feed silent"):
    return Finding(agent="scripted", rule="feed-silent", severity=sev, title=title,
                   summary="No NSE tick for 120 s.", fingerprint=fp, where="Redis market:ticks",
                   evidence=["newest NSE tick 11:27:35 IST"], suggestion="Switch to the FYERS feed.")


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
