"""
scripts/lib/morning_tally.py — the morning's plan against what is actually live.

The case it exists for is 24 Sep: 26 runs planned, 10 live at 09:18, and the
job said nothing because it compared the Running rows with nothing.
"""
import os
import sys
import unittest

import _bootstrap  # noqa: F401

# The tally lives in scripts/lib, which is not on the engine path.
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts", "lib"))

import morning_tally as mt  # noqa: E402

USERS = [{"id": 1, "userName": "admin"}, {"id": 7, "userName": "coderforchange"}]
STRATEGIES = ("GhostTangentCrossings", "ChainFlowBuy", "SmcStructureBreak", "Fulcrum")
INDICES = ("BANKNIFTY", "NIFTY", "SENSEX")


def plan(accounts=("admin", "coderforchange")) -> str:
    rows = [f"{a}|{s}|{u}" for a in accounts for s in STRATEGIES for u in INDICES]
    rows += [f"{a}|CrudeMomentum|CRUDEOIL" for a in accounts]
    return "\n".join(rows)


def run(user_id, strategy, underlying, **extra) -> dict:
    return {"userId": user_id, "strategyName": strategy, "underlying": underlying,
            "status": "Running", "isActive": True, "startedUtc": "2026-09-24T03:17:00Z", **extra}


def everything_live() -> list[dict]:
    return [run(uid, s, u) for uid in (1, 7) for s in STRATEGIES for u in INDICES] + \
           [run(uid, "CrudeMomentum", "CRUDEOIL") for uid in (1, 7)]


class TallyTests(unittest.TestCase):
    def test_full_plan_is_all_live(self):
        result = mt.tally(mt.parse_expected(plan()), USERS, everything_live(), [])
        self.assertFalse(result.short)
        self.assertEqual("Morning plan 26/26 live", result.summary())
        self.assertIn("=== 26 of 26 planned live ===", result.lines())

    def test_missing_runs_listed_with_their_stop_reason_like_24_sep(self):
        live = everything_live()
        # All 13 of coderforchange's and three of the admin's: 16 dead, 10 live.
        dead = [r for r in live if r["userId"] == 7] + [r for r in live if r["userId"] == 1][:3]
        running = [r for r in live if r not in dead]
        today = running + [dict(r, status="Stopped", isActive=False, durationSeconds=3,
                                stopReason="Runner exited (code 1): 429 Too Many Requests on /api/UserAuth/login")
                           for r in dead]

        result = mt.tally(mt.parse_expected(plan()), USERS, running, today)

        self.assertTrue(result.short)
        self.assertEqual(10, len(result.live))
        self.assertEqual(16, len(result.missing))
        summary = result.summary()
        # Named in plan order, so the Telegram line starts where the plan does.
        self.assertTrue(summary.startswith("Morning plan SHORT 10/26 live — admin GhostTangentCrossings BANKNIFTY: "), summary)
        self.assertIn("429", summary)
        self.assertIn("(after 3 s)", summary)
        self.assertIn("and 13 more", summary)
        self.assertEqual(16, sum(1 for line in result.lines() if line.startswith("  missing: ")))

    def test_a_run_counts_for_its_owner_not_whoever_started_it(self):
        # The job starts coderforchange's runs as the admin: userId is the owner.
        expected = [("coderforchange", "ChainFlowBuy", "NIFTY")]
        started_by_admin = [run(7, "ChainFlowBuy", "NIFTY", userName="admin", startedBy="admin")]
        self.assertFalse(mt.tally(expected, USERS, started_by_admin, []).short)
        # And the admin's own run of the same thing does not stand in for it.
        self.assertTrue(mt.tally(expected, USERS, [run(1, "ChainFlowBuy", "NIFTY")], []).short)

    def test_a_running_row_with_no_runner_behind_it_is_not_live(self):
        expected = [("admin", "Fulcrum", "SENSEX")]
        result = mt.tally(expected, USERS, [run(1, "Fulcrum", "SENSEX", isActive=False)], [])
        self.assertTrue(result.short)

    def test_the_latest_attempt_gives_the_reason(self):
        expected = [("admin", "Fulcrum", "NIFTY")]
        today = [run(1, "Fulcrum", "NIFTY", status="Stopped", startedUtc="2026-09-24T03:17:00Z", stopReason="Runner exited (code 1)"),
                 run(1, "Fulcrum", "NIFTY", status="Stopped", startedUtc="2026-09-24T05:50:00Z", stopReason="Stopped by admin")]
        result = mt.tally(expected, USERS, [], today)
        self.assertIn("Stopped by admin", result.missing[0][1])

    def test_a_run_that_never_started_and_an_unknown_account_say_so(self):
        expected = [("admin", "NoSuchStrategy", "NIFTY"), ("ghost-user", "Fulcrum", "NIFTY")]
        result = mt.tally(expected, USERS, [], [])
        self.assertIn("never started", result.missing[0][1])
        self.assertIn("no such active account", result.missing[1][1])

    def test_names_and_symbols_compare_without_case(self):
        expected = mt.parse_expected("Admin|ghosttangentcrossings|nifty")
        self.assertFalse(mt.tally(expected, USERS, [run(1, "GhostTangentCrossings", "NIFTY")], []).short)

    def test_parse_expected_skips_junk_and_repeats(self):
        text = "admin|Fulcrum|NIFTY\n\nnot a line\nadmin|Fulcrum|NIFTY\nadmin||NIFTY\n"
        self.assertEqual([("admin", "Fulcrum", "NIFTY")], mt.parse_expected(text))


class MainTests(unittest.TestCase):
    def run_main(self, **env) -> tuple[int, list[str]]:
        import io
        from contextlib import redirect_stdout
        from unittest import mock
        out = io.StringIO()
        with mock.patch.dict(os.environ, env, clear=False), redirect_stdout(out):
            code = mt.main()
        return code, out.getvalue().splitlines()

    def test_exit_2_when_short_and_0_when_full(self):
        import json
        full = dict(EXPECTED=plan(), USERS=json.dumps(USERS), RUNNING=json.dumps(everything_live()), TODAY="[]")
        self.assertEqual(0, self.run_main(**full)[0])
        short = dict(full, RUNNING=json.dumps(everything_live()[1:]))
        code, lines = self.run_main(**short)
        self.assertEqual(2, code)
        self.assertTrue(lines[0].startswith("Morning plan SHORT 25/26"))

    def test_an_unreadable_running_list_is_unknown_not_fine(self):
        for body in ("<html>502</html>", ""):
            code, lines = self.run_main(EXPECTED=plan(), USERS="[]", RUNNING=body, TODAY="[]")
            self.assertEqual(2, code)
            self.assertTrue(lines[0].startswith("Morning plan UNKNOWN"), body)


if __name__ == "__main__":
    unittest.main()
