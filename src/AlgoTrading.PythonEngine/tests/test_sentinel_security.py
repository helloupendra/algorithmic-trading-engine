import gzip
import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import _bootstrap  # noqa: F401

from sentinel.agents.security import (
    GIT_PATTERNS, SecurityAgent, is_literal_password, is_test_or_doc, parse_dotnet_table, parse_npm_audit,
    parse_secret_allowlist,
)
from sentinel.clock import to_ist
from sentinel.context import READ_ONLY_TOOLS, _ALLOWED_SUBCOMMANDS
from sentinel.engine import SentinelEngine
from sentinel.model import Severity
from sentinel.store import MemoryIncidentStore
from _sentinel_fakes import RecordingNotifier, make_context

THURSDAY_MARKET = datetime(2026, 9, 24, 6, 0, tzinfo=timezone.utc)   # 11:30 IST, market open
SUNDAY = datetime(2026, 9, 27, 6, 0, tzinfo=timezone.utc)            # 11:30 IST, no market

# What the production server printed on 27 Sep 2026: ssh on every address, everything else on loopback.
SERVER_SS = """\
LISTEN 0      4096       127.0.0.1:3000  0.0.0.0:*
LISTEN 0      4096       127.0.0.1:6379  0.0.0.0:*
LISTEN 0      4096       127.0.0.1:20241 0.0.0.0:*
LISTEN 0      4096      127.0.0.54:53    0.0.0.0:*
LISTEN 0      4096       127.0.0.1:9090  0.0.0.0:*
LISTEN 0      512        127.0.0.1:5025  0.0.0.0:*
LISTEN 0      4096         0.0.0.0:22    0.0.0.0:*
LISTEN 0      4096   127.0.0.53%lo:53    0.0.0.0:*
LISTEN 0      4096       127.0.0.1:5432  0.0.0.0:*
LISTEN 0      4096       127.0.0.1:5310  0.0.0.0:*
LISTEN 0      512            [::1]:5025     [::]:*
LISTEN 0      4096            [::]:22       [::]:*
"""
SERVER_DOCKER = """\
openfno-broker-broker-1\t127.0.0.1:5310->8080/tcp
openfno-broker-postgres-1\t5432/tcp
algotrading_grafana\t127.0.0.1:3000->3000/tcp
algotrading_db\t127.0.0.1:5432->5432/tcp
algotrading_prometheus\t127.0.0.1:9090->9090/tcp
algotrading_redis\t127.0.0.1:6379->6379/tcp
"""

# `dotnet list package --vulnerable --include-transitive`, as an SDK prints it.
DOTNET_TABLE = """\

The following sources were used:
   https://api.nuget.org/v3/index.json

Project `AlgoTrading.Api` has the following vulnerable packages
   [net8.0]:
   Top-level Package               Requested   Resolved   Severity   Advisory URL
   > Microsoft.Data.SqlClient      1.1.0       1.1.0      Moderate   https://github.com/advisories/GHSA-8g2p-5pqh-5jmc
                                                          High       https://github.com/advisories/GHSA-98g6-xh36-x2p7
   > System.Text.Json              8.0.0       8.0.0      High       https://github.com/advisories/GHSA-hh2w-p6rv-4g7w

   Transitive Package                           Resolved   Severity   Advisory URL
   > Newtonsoft.Json                            10.0.1     High       https://github.com/advisories/GHSA-5crp-9r3c-p9vr
   > System.Text.RegularExpressions             4.3.0      Low        https://github.com/advisories/GHSA-cmhx-cq75-c4mj
"""

DOTNET_JSON = json.dumps({
    "version": 1, "parameters": "--vulnerable --include-transitive",
    "sources": ["https://api.nuget.org/v3/index.json"],
    "projects": [{"path": "/repo/src/AlgoTrading.Api/AlgoTrading.Api.csproj", "frameworks": [{
        "framework": "net8.0",
        "topLevelPackages": [{"id": "System.Text.Json", "requestedVersion": "8.0.0", "resolvedVersion": "8.0.0",
                              "vulnerabilities": [{"severity": "High", "advisoryurl": "https://example.org/stj"}]}],
        "transitivePackages": [{"id": "Some.Parser", "resolvedVersion": "1.0.0",
                                "vulnerabilities": [{"severity": "Moderate", "advisoryurl": "https://example.org/a"},
                                                    {"severity": "Critical", "advisoryurl": "https://example.org/b"}]},
                               {"id": "Barely.There", "resolvedVersion": "2.0.0",
                                "vulnerabilities": [{"severity": "Low", "advisoryurl": "https://example.org/c"}]}],
    }]}],
})

DOTNET_NO_ASSETS = json.dumps({
    "version": 1, "parameters": "--vulnerable --include-transitive",
    "problems": [{"project": "/repo/src/AlgoTrading.Api/AlgoTrading.Api.csproj", "level": "error",
                  "text": "No assets file was found. Please run restore before running this command."}],
    "projects": [{"path": "/repo/src/AlgoTrading.Api/AlgoTrading.Api.csproj"}],
})


def _advisory(severity, title, ghsa):
    return {"source": 1, "name": "x", "severity": severity, "title": title,
            "url": f"https://github.com/advisories/{ghsa}", "range": "<9"}


# Trimmed from web/'s real `npm audit --omit=dev --json` on 27 Sep 2026, plus a critical and a low.
NPM_AUDIT = json.dumps({
    "auditReportVersion": 2,
    "vulnerabilities": {
        "@excalidraw/excalidraw": {"name": "@excalidraw/excalidraw", "severity": "moderate", "isDirect": True,
                                   "via": ["@excalidraw/mermaid-to-excalidraw", "nanoid"], "range": ">=0.18.0",
                                   "fixAvailable": {"name": "@excalidraw/excalidraw", "version": "0.17.6",
                                                    "isSemVerMajor": True}},
        "@excalidraw/mermaid-to-excalidraw": {"severity": "moderate", "isDirect": False,
                                              "via": ["@mermaid-js/parser", "nanoid"], "range": "*",
                                              "fixAvailable": True},
        "@mermaid-js/parser": {"severity": "moderate", "isDirect": False, "via": ["langium"], "range": "<=0.6.3",
                               "fixAvailable": True},
        "langium": {"severity": "moderate", "isDirect": False, "via": ["chevrotain"], "fixAvailable": True},
        "chevrotain": {"severity": "moderate", "isDirect": False, "via": ["lodash-es"], "fixAvailable": True},
        "lodash-es": {"severity": "high", "isDirect": False, "range": ">=4.0.0 <=4.17.23", "fixAvailable": True,
                      "via": [_advisory("high", "lodash vulnerable to Code Injection", "GHSA-r5fr-rjxr-66jc"),
                              _advisory("moderate", "Prototype Pollution", "GHSA-f23m-r3pf-42rh")]},
        "nanoid": {"severity": "high", "isDirect": False, "range": "<3.3.18",
                   "fixAvailable": {"name": "@excalidraw/excalidraw", "version": "0.17.6", "isSemVerMajor": True},
                   "via": [_advisory("high", "nanoid: custom generators can loop", "GHSA-2v37-7h3g-55p8")]},
        "tiny-low": {"severity": "low", "isDirect": True, "via": [_advisory("low", "meh", "GHSA-low")],
                     "fixAvailable": True},
        "evil-critical": {"severity": "critical", "isDirect": True, "range": "<2",
                          "via": [_advisory("critical", "remote code execution", "GHSA-crit")], "fixAvailable": False},
    },
    "metadata": {"vulnerabilities": {"info": 0, "low": 1, "moderate": 5, "high": 2, "critical": 1, "total": 9}},
})


class FakeShell:
    """Stands in for ctx.run: canned output per tool, the real allowlist enforced, every call recorded."""

    def __init__(self, **outputs):
        self.outputs = {"ss": (0, SERVER_SS), "docker": (0, SERVER_DOCKER), "git": (1, ""),
                        "fail2ban-client": (255, ""), "dotnet": (127, ""), "npm": (127, "")}
        self.outputs.update({k.replace("_", "-"): v for k, v in outputs.items()})
        self.calls = []

    def __call__(self, args, timeout=20.0, cwd=None):
        assert args[0] in READ_ONLY_TOOLS, args
        allowed = _ALLOWED_SUBCOMMANDS.get(args[0])
        assert allowed is None or args[1] in allowed, args
        self.calls.append(list(args))
        out = self.outputs[args[0]]
        if isinstance(out, Exception):
            raise out
        return out(args) if callable(out) else out

    def ran(self, tool):
        return [c for c in self.calls if c[0] == tool]


def _utc(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def activity_api(login_rows=(), modules=None, fail=False, queries=None, ok_rows=()):
    """
    GET /api/ActivityLog?…, filtered the way the API filters: failed sign-ins (``login_rows``) or successful
    ones (``ok_rows``) for the login query, rows per module otherwise; newer than fromUtc in both.
    """
    modules = modules or {}

    def handler(path):
        q = {k: v[0] for k, v in parse_qs(urlsplit(path).query).items()}
        if queries is not None:
            queries.append(q)
        if fail:
            raise ConnectionError("connection refused")
        if q.get("action") == "create:userauth-login":
            rows = list(ok_rows) if q.get("succeeded") == "true" else list(login_rows)
        else:
            rows = modules.get(q.get("module"), [])
        if "fromUtc" in q:
            rows = [r for r in rows if _utc(r["occurredUtc"]) >= _utc(q["fromUtc"])]
        offset = int(q.get("offset", 0))
        return {"total": len(rows), "rows": rows[offset:offset + int(q.get("limit", 200))]}

    return {"/api/ActivityLog?*": handler}


def entry(entry_id, path, method="POST", ip="49.14.173.16", when="2026-09-24T05:55:00.1234567Z", user="admin",
          ok=True, target=None, summary=None, module="users"):
    return {"id": entry_id, "occurredUtc": when, "userId": 2, "userName": user, "role": "", "module": module,
            "action": "x", "method": method, "path": path, "statusCode": 200 if ok else 400, "durationMs": 5,
            "succeeded": ok, "targetType": "user" if target else None, "targetId": target, "summary": summary,
            "ipAddress": ip}


def failed_login(entry_id, ip, minute=55, name="coderforchange@gmail.com"):
    return failed_at(entry_id, ip, f"2026-09-24T05:{minute:02d}:10Z", name=name)


def failed_at(entry_id, ip, when, name="coderforchange@gmail.com"):
    return entry(entry_id, "/api/UserAuth/login", ip=ip, when=when, user="anonymous", ok=False, target=name,
                 summary=f'Failed sign-in for "{name}".', module="auth")


def signed_in_at(entry_id, ip, when, user="admin"):
    return entry(entry_id, "/api/UserAuth/login", ip=ip, when=when, user=user, target=user, module="auth")


def _z(moment):
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def everything_said(findings):
    return " ".join(" ".join([f.title, f.summary, f.where, f.suggestion, f.fingerprint, *f.evidence])
                    for f in findings)


class SecurityTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.shell = FakeShell()

    def check(self, api=None, now=THURSDAY_MARKET, env=None, auth_logs=(), monotonic=None):
        ctx = make_context(self.tmp, api=api if api is not None else activity_api(), now=now, env=env)
        ctx.run = self.shell
        agent = SecurityAgent(auth_logs=auth_logs) if monotonic is None else \
            SecurityAgent(auth_logs=auth_logs, monotonic=monotonic)
        return agent.check(ctx)

    def engine(self, env=None, auth_logs=()):
        """
        The agent inside the real engine, with a clock and an activity log the test moves: returns
        (run(now, api=None), notifier) so a test can count the messages a person would actually get.
        ``self.store`` is the engine's store (a test resolves incidents in it, as a person does from the
        console) and ``self.ctx`` its context.
        """
        notifier = RecordingNotifier()
        box = {"now": THURSDAY_MARKET, "api": activity_api()}
        ctx = make_context(self.tmp, api={"/api/ActivityLog?*": lambda path: box["api"]["/api/ActivityLog?*"](path)},
                           env=env)
        ctx.clock = lambda: box["now"]
        ctx.run = self.shell
        self.store, self.ctx = MemoryIncidentStore(), ctx
        engine = SentinelEngine([SecurityAgent(auth_logs=auth_logs)], self.store, notifier, ctx)

        def run(now, api=None):
            box["now"] = now
            if api is not None:
                box["api"] = api
            engine.run_all_once()

        return run, notifier

    def by_rule(self, findings, rule):
        return [f for f in findings if f.rule == rule]

    def resolve(self, row, at, why=None):
        """What a person does from the console: resolve, with a root cause."""
        self.store.resolve_by_person(row["id"], at)
        if why:
            self.store.write_resolution(row["id"], why)


class QuietDayTests(SecurityTestCase):
    def test_an_ordinary_morning_on_the_real_server_says_nothing(self):
        env_file = self.tmp / ".env"
        env_file.write_text("ADMIN_PASSWORD=x\n")
        os.chmod(env_file, 0o600)
        api = activity_api(
            login_rows=[failed_login(1, "2401:4900:1c55:8ec7:cd04:ee3b:604e:6028")] * 2,   # someone mistyped twice
            modules={"auth": [entry(10, "/api/UserAuth/login", module="auth")],
                     "users": [entry(11, "/api/Users/2/revoke-sessions")]})
        self.assertEqual([], self.check(api=api))

    def test_every_command_it_runs_is_on_the_read_only_allowlist(self):
        (self.tmp / "src" / "AlgoTrading.Api").mkdir(parents=True)
        (self.tmp / "web").mkdir()
        (self.tmp / "web" / "package-lock.json").write_text("{}")
        self.check(now=SUNDAY)   # FakeShell asserts the allowlist on every call
        self.assertEqual({"ss", "docker", "git", "fail2ban-client", "dotnet", "npm"},
                         {c[0] for c in self.shell.calls})


# The owner's household on 23 Sep (UTC), as the activity log held it: a good sign-in, seven mistyped ones, a good
# one again. The times are the real ones; the /64 is made up.
HOUSEHOLD = "2401:4900:aaaa:bbbb"
SEP23_SIGNED_IN = ("2026-09-23T17:38:58.511639Z", "2026-09-23T17:52:31.088745Z")
SEP23_FAILED = ("2026-09-23T17:42:22.306381Z", "2026-09-23T17:42:27.00474Z", "2026-09-23T17:42:30.781203Z",
                "2026-09-23T17:42:36.532492Z", "2026-09-23T17:42:46.164307Z", "2026-09-23T17:42:58.651452Z",
                "2026-09-23T17:43:03.620094Z")


class LoginFailureTests(SecurityTestCase):
    def test_sixteen_failures_from_one_address_is_a_high_incident(self):
        rows = [failed_login(i, "49.14.173.16", minute=50 + i % 9) for i in range(16)]
        found = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        self.assertEqual(1, len(found))
        f = found[0]
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("security:login-failures:49.14.173.16", f.fingerprint)
        self.assertIn("49.14.173.16", f.title)
        self.assertIn("49.14.173.16: 16 failed", f.evidence[0])

    def test_fifteen_from_one_address_is_a_person_mistyping_not_an_attack(self):
        rows = [failed_login(i, "49.14.173.16") for i in range(15)]
        self.assertEqual([], self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures"))

    def test_the_owner_mistyping_on_23_sep_says_nothing(self):
        signed_in = [signed_in_at(188595 + i, f"{HOUSEHOLD}:1:2:3:{i + 1:x}", t) for i, t in enumerate(SEP23_SIGNED_IN)]
        failed = [failed_at(188597 + i, f"{HOUSEHOLD}:5d1e:{i:x}:7:8", t, name="admin")
                  for i, t in enumerate(SEP23_FAILED)]
        run, notifier = self.engine()
        for minutes in (43, 45, 50, 55, 60, 65):   # checks through the burst and after it
            now = datetime(2026, 9, 23, 17, 0, tzinfo=timezone.utc) + timedelta(minutes=minutes)
            so_far = [r for r in signed_in if _utc(r["occurredUtc"]) <= now]
            bad = [r for r in failed if _utc(r["occurredUtc"]) <= now]
            run(now, activity_api(login_rows=bad, ok_rows=so_far))
        self.assertEqual([], notifier.sent)

    def test_an_address_that_signed_in_before_its_failures_is_low_not_high(self):
        # The 23 Sep household again, but fumbling past the bar — a saved password, or a script at home.
        now = datetime(2026, 9, 23, 17, 45, tzinfo=timezone.utc)
        ok = [signed_in_at(1, f"{HOUSEHOLD}:1:2:3:4", SEP23_SIGNED_IN[0])]
        bad = [failed_at(10 + i, f"{HOUSEHOLD}:9:8:7:{i:x}", f"2026-09-23T17:{40 + i // 6:02d}:{(i * 7) % 60:02d}Z",
                         name="admin") for i in range(20)]
        found = self.by_rule(self.check(api=activity_api(login_rows=bad, ok_rows=ok), now=now), "login-failures")
        self.assertEqual([f"security:login-failures:{HOUSEHOLD}::/64"], [f.fingerprint for f in found])
        self.assertEqual(Severity.LOW, found[0].severity)
        self.assertTrue(found[0].title.startswith(
            "Repeated failed sign-ins from an address that also signed in successfully"))
        self.assertIn("23 Sep 23:08 IST", found[0].summary)

    def test_guessing_and_then_getting_in_is_critical(self):
        bad = [failed_login(i, "198.51.100.7", minute=50 + i % 5) for i in range(16)]
        ok = [signed_in_at(99, "198.51.100.7", "2026-09-24T05:57:00Z")]
        found = self.by_rule(self.check(api=activity_api(login_rows=bad, ok_rows=ok)), "login-failures")
        self.assertEqual(1, len(found))
        self.assertEqual(Severity.CRITICAL, found[0].severity)
        self.assertEqual("Someone guessed passwords from 198.51.100.7, then signed in", found[0].title)
        self.assertIn("succeeded at 24 Sep 11:27 IST", found[0].evidence[0])

    def test_an_ordinary_check_asks_one_question(self):
        queries = []
        self.check(api=activity_api(login_rows=[failed_login(1, "49.14.173.16")] * 3, queries=queries))
        self.assertEqual(1, len([q for q in queries if q.get("action") == "create:userauth-login"]))

    def test_the_names_tried_never_leave_the_machine(self):
        # People type passwords into the username box; the activity log keeps what was typed.
        rows = [failed_login(i, "49.14.173.16", name="Hunter2-typed-in-the-wrong-box") for i in range(16)]
        found = self.check(api=activity_api(login_rows=rows))
        self.assertTrue(found)
        self.assertNotIn("hunter2", everything_said(found).lower())

    def test_an_ipv6_household_is_one_address(self):
        rows = [failed_login(i, f"2401:4900:1c55:8ec7:{i:x}:ee3b:604e:6028") for i in range(1, 17)]
        found = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        self.assertEqual(["security:login-failures:2401:4900:1c55:8ec7::/64"], [f.fingerprint for f in found])

    def test_many_failures_spread_thin_are_still_reported(self):
        rows = [failed_login(i, f"203.0.113.{i}") for i in range(1, 12)]
        found = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        self.assertEqual(["security:login-failures:spread"], [f.fingerprint for f in found])
        self.assertIn("11 failed sign-ins", found[0].summary)

    def test_ten_spread_thin_is_the_ordinary_ceiling(self):
        rows = [failed_login(i, f"203.0.113.{i}") for i in range(1, 11)]
        self.assertEqual([], self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures"))

    def test_two_people_mistyping_is_not_a_distributed_guess(self):
        rows = [failed_login(i, "203.0.113.1" if i % 2 else "203.0.113.2") for i in range(14)]
        self.assertEqual([], self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures"))

    def test_failures_from_this_machine_point_at_a_runner_password(self):
        rows = [failed_login(i, "::1" if i % 2 else "127.0.0.1") for i in range(6)]
        found = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        self.assertEqual("security:login-failures:loopback", found[0].fingerprint)
        self.assertIn(".env", found[0].suggestion)

    def test_a_growing_attack_keeps_one_fingerprint(self):
        first = self.check(api=activity_api(login_rows=[failed_login(i, "198.51.100.7") for i in range(16)]))
        second = self.check(api=activity_api(login_rows=[failed_login(i, "198.51.100.7") for i in range(40)]))
        self.assertEqual([f.fingerprint for f in self.by_rule(first, "login-failures")],
                         [f.fingerprint for f in self.by_rule(second, "login-failures")])

    def test_a_guess_hovering_at_the_bar_is_one_incident_not_an_alarm_per_crossing(self):
        def burst(start, first_id):   # 16 failures, 30 s apart
            return [failed_at(first_id + i, "198.51.100.7", _z(start + timedelta(seconds=30 * i))) for i in range(16)]

        t0 = THURSDAY_MARKET
        rows = burst(t0 - timedelta(minutes=8), 0)                   # 05:52:00 – 05:59:30
        run, notifier = self.engine()
        run(t0, activity_api(login_rows=rows))                         # over the bar: one message
        run(t0 + timedelta(minutes=10), activity_api(login_rows=rows))  # 10 left in the window: held
        rows += burst(t0 + timedelta(minutes=11), 100)                 # 06:11:00 – 06:18:30
        run(t0 + timedelta(minutes=19), activity_api(login_rows=rows))  # over again: the same incident
        run(t0 + timedelta(minutes=30), activity_api(login_rows=rows))  # below: held to 06:48:30
        self.assertEqual(1, len(notifier.sent))
        self.assertIn("NEW [HIGH] Someone is guessing passwords from 198.51.100.7", notifier.sent[0])
        run(t0 + timedelta(minutes=50), activity_api(login_rows=rows))  # quiet for 30 minutes: closed
        self.assertEqual(2, len(notifier.sent))
        self.assertTrue(notifier.sent[1].startswith("✅ RESOLVED"))

    def test_a_held_incident_says_it_is_held(self):
        rows = [failed_at(i, "198.51.100.7", _z(THURSDAY_MARKET - timedelta(minutes=8, seconds=-30 * i)))
                for i in range(16)]
        self.check(api=activity_api(login_rows=rows))
        held = self.by_rule(self.check(api=activity_api(login_rows=rows), now=THURSDAY_MARKET + timedelta(minutes=10)),
                            "login-failures")
        self.assertEqual(["security:login-failures:198.51.100.7"], [f.fingerprint for f in held])
        self.assertIn("held open until 11:59 IST", held[0].summary)

    def test_it_asks_only_for_failed_sign_ins_in_the_last_fifteen_minutes(self):
        queries = []
        self.check(api=activity_api(queries=queries))
        login = [q for q in queries if q.get("action") == "create:userauth-login"][0]
        self.assertEqual("false", login["succeeded"])
        self.assertEqual("2026-09-24T05:45:00Z", login["fromUtc"])

    def test_an_unreadable_log_keeps_the_last_answer_instead_of_resolving(self):
        rows = [failed_login(i, "198.51.100.7") for i in range(16)]
        first = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        again = self.by_rule(self.check(api=activity_api(fail=True)), "login-failures")
        self.assertEqual([f.fingerprint for f in first], [f.fingerprint for f in again])


GUESSER = "198.51.100.7"


def guessing(start, first_id, n=16):
    """``n`` failed sign-ins from GUESSER, 30 s apart from ``start``: 16 is one over the bar."""
    return [failed_at(first_id + i, GUESSER, _z(start + timedelta(seconds=30 * i))) for i in range(n)]


class ResolvedWhileHeldTests(SecurityTestCase):
    """
    30 Sep: Sentinel opened security incidents again that a person had already resolved, from what the agent
    held in memory — its login-failure hold, the same failures read again while still in the 15-minute window —
    with nothing new behind them. What was read before a resolve never reopens it; what is read after does.
    """

    FINGERPRINT = f"security:login-failures:{GUESSER}"

    def test_a_held_finding_says_when_its_newest_failure_was_read(self):
        t0 = THURSDAY_MARKET
        rows = guessing(t0 - timedelta(minutes=8), 0)   # 05:52:00 – 05:59:30
        fresh = self.by_rule(self.check(api=activity_api(login_rows=rows)), "login-failures")
        self.assertEqual([None], [f.observed_utc for f in fresh], "read this check")
        reread = self.by_rule(self.check(api=activity_api(login_rows=rows), now=t0 + timedelta(minutes=5)),
                              "login-failures")
        self.assertEqual([t0], [f.observed_utc for f in reread], "all 16 still in the window: read at 06:00")
        held = self.by_rule(self.check(api=activity_api(login_rows=rows), now=t0 + timedelta(minutes=10)),
                            "login-failures")
        self.assertEqual([t0], [f.observed_utc for f in held], "held, not observed now")
        self.assertIn("held open until", held[0].summary)
        blind = self.by_rule(self.check(api=activity_api(fail=True), now=t0 + timedelta(minutes=15)),
                             "login-failures")
        self.assertEqual([t0], [f.observed_utc for f in blind], "carried while the log cannot be read")

        ctx = make_context(self.tmp, api=activity_api(login_rows=rows), now=t0 + timedelta(minutes=16))
        ctx.run = self.shell
        agent = SecurityAgent(auth_logs=())
        agent.let_go(ctx, {self.FINGERPRINT})
        self.assertEqual([], self.by_rule(agent.check(ctx), "login-failures"), "let go: nothing held")

    def test_a_guess_resolved_while_held_stays_resolved_until_it_resumes(self):
        t0 = THURSDAY_MARKET
        rows = guessing(t0 - timedelta(minutes=8), 0)                    # 05:52:00 – 05:59:30
        run, notifier = self.engine()
        run(t0, activity_api(login_rows=rows))                            # over the bar: NEW
        run(t0 + timedelta(minutes=10), activity_api(login_rows=rows))    # below it: held to 06:29:30
        first = self.store.rows()[0]
        self.resolve(first, t0 + timedelta(minutes=12), "A penetration test we ordered; not an attack.")

        for minutes in (15, 20, 25, 30):   # the hold alone would have opened it again at 06:15
            run(t0 + timedelta(minutes=minutes), activity_api(login_rows=rows))
        self.assertEqual(["resolved"], [r["status"] for r in self.store.rows()], "nothing reopened, nothing new")
        self.assertEqual(1, len(notifier.sent))
        self.assertNotIn(self.FINGERPRINT, self.ctx.state("security").data["login_hold"], "the hold is over")

        # 06:31: it resumes. Those failures are news, and by the next check only they are in the window.
        rows += guessing(t0 + timedelta(minutes=31), 100)                 # 06:31:00 – 06:38:30
        run(t0 + timedelta(minutes=40), activity_api(login_rows=rows))
        self.assertEqual(2, len(self.store.rows()))
        new = self.store.rows()[-1]
        self.assertEqual(("open", 1), (new["status"], new["occurrences"]))
        self.assertIn(f"16 failed sign-ins in the last 15 minutes from {GUESSER} (24 Sep 12:01 IST – 12:08 IST)",
                      new["summary"])
        self.assertEqual(2, len(notifier.sent))
        self.assertIn(f"NEW [HIGH] Someone is guessing passwords from {GUESSER}", notifier.sent[-1])
        self.assertIn("Last time: A penetration test we ordered; not an attack.", notifier.sent[-1])

    def test_failures_still_in_the_window_are_old_news_and_one_more_is_news(self):
        t0 = THURSDAY_MARKET
        rows = guessing(t0 - timedelta(minutes=8), 0)                    # 05:52:00 – 05:59:30
        run, notifier = self.engine()
        run(t0, activity_api(login_rows=rows))
        self.resolve(self.store.rows()[0], t0 + timedelta(minutes=1), f"Blocked {GUESSER} in the security group.")
        run(t0 + timedelta(minutes=5), activity_api(login_rows=rows))    # all 16 read again, still over the bar
        self.assertEqual(["resolved"], [r["status"] for r in self.store.rows()])
        self.assertEqual(1, len(notifier.sent))

        rows.append(failed_at(50, GUESSER, "2026-09-24T06:06:40Z"))       # 11:36 IST: the block did not hold
        run(t0 + timedelta(minutes=7), activity_api(login_rows=rows))
        self.assertEqual(2, len(self.store.rows()))
        new = self.store.rows()[-1]
        self.assertEqual(("open", 1), (new["status"], new["occurrences"]))
        self.assertIn("(24 Sep 11:22 IST – 11:36 IST)", new["summary"])
        self.assertIn(f"NEW [HIGH] Someone is guessing passwords from {GUESSER}", notifier.sent[-1])
        self.assertIn(f"Last time: Blocked {GUESSER} in the security group.", notifier.sent[-1])

    def test_a_sign_in_read_after_the_resolve_is_news_even_if_it_happened_before(self):
        # What a resolve is weighed against is when Sentinel read the evidence, not when it happened: the
        # person resolving the guess at 11:34 had not been told of the sign-in at 11:33.
        t0 = THURSDAY_MARKET
        rows = guessing(t0 - timedelta(minutes=8), 0)
        run, notifier = self.engine()
        run(t0, activity_api(login_rows=rows))                            # HIGH
        self.resolve(self.store.rows()[0], t0 + timedelta(minutes=4))
        ok = [signed_in_at(99, GUESSER, "2026-09-24T06:03:00Z")]
        run(t0 + timedelta(minutes=5), activity_api(login_rows=rows, ok_rows=ok))
        self.assertEqual(2, len(self.store.rows()))
        self.assertEqual((Severity.CRITICAL, "open"), (self.store.rows()[-1]["severity"],
                                                       self.store.rows()[-1]["status"]))
        self.assertIn(f"NEW [CRITICAL] Someone guessed passwords from {GUESSER}, then signed in", notifier.sent[-1])


class PrivilegedChangeTests(SecurityTestCase):
    def test_each_privileged_change_is_its_own_notice(self):
        api = activity_api(modules={
            "users": [entry(51139, "/api/Users/7/grants", method="PUT"),
                      entry(51140, "/api/Users/7", method="PATCH"),
                      entry(188607, "/api/Users/7/password", ip="::1"),
                      entry(188617, "/api/Users/2/revoke-sessions"),                     # not privileged
                      entry(188620, "/api/Invites")],
            "auth": [entry(51135, "/api/UserAuth/register", summary="Created the account coderforchange (Trader).",
                           module="auth"),
                     entry(51200, "/api/UserAuth/login", module="auth")],               # an ordinary sign-in
            "risk": [entry(60001, "/api/Risk/killswitch/activate", summary="Pulled the kill switch.", module="risk")],
            "other": [entry(179677, "/api/SimBroker/accounts/1", ip=None, module="other")],
        })
        found = self.by_rule(self.check(api=api), "privileged-change")
        self.assertEqual(
            ["security:privileged-change:51135", "security:privileged-change:51139",
             "security:privileged-change:51140", "security:privileged-change:60001",
             "security:privileged-change:179677", "security:privileged-change:188607",
             "security:privileged-change:188620"],
            [f.fingerprint for f in found])
        titles = {f.fingerprint.rsplit(":", 1)[1]: f.title for f in found}
        self.assertEqual("admin created a new account", titles["51135"])
        self.assertEqual("admin changed the module grants of account #7", titles["51139"])
        self.assertEqual("admin reset the password of account #7", titles["188607"])
        self.assertEqual("admin activated the global kill switch", titles["60001"])
        self.assertEqual("admin issued a simulated-broker account to account #1", titles["179677"])
        self.assertTrue(all(f.severity == Severity.MEDIUM for f in found))
        self.assertIn("Created the account coderforchange (Trader).", found[0].evidence)
        self.assertIn("this machine (loopback)", found[5].summary)

    def test_an_invite_token_in_the_path_is_never_repeated(self):
        token = "q7Zp4XkL9mB2sR8tV1wY6cN3dF5gH0jE"
        api = activity_api(modules={"users": [entry(9, f"/api/Invites/{token}/accept", user="anonymous")]})
        found = self.check(api=api)
        self.assertEqual("An invitation was used to create a new account", found[0].title)
        self.assertNotIn(token, everything_said(found))

    def test_a_change_is_reported_once_and_the_next_check_reads_on_from_the_last(self):
        queries = []
        api = activity_api(modules={"users": [entry(51139, "/api/Users/7/grants", method="PUT")]}, queries=queries)
        self.assertEqual(1, len(self.check(api=api)))
        queries.clear()
        self.assertEqual([], self.check(api=api, now=THURSDAY_MARKET + timedelta(minutes=5)))
        self.assertEqual({"2026-09-24T05:58:00Z"},
                         {q["fromUtc"] for q in queries if "module" in q})   # the last check, less two minutes
        self.assertEqual({"users", "auth", "risk", "connectors", "other"},
                         {q["module"] for q in queries if "module" in q})

    def test_while_the_api_is_down_the_window_is_kept_for_later(self):
        self.assertEqual([], self.check(api=activity_api(fail=True)))
        api = activity_api(modules={"users": [entry(5, "/api/Users/3/grants", method="PUT",
                                                    when="2026-09-24T05:50:00Z")]})
        found = self.check(api=api, now=THURSDAY_MARKET + timedelta(minutes=30))
        self.assertEqual(["security:privileged-change:5"], [f.fingerprint for f in found])

    def test_a_scripted_burst_is_rolled_up_after_fifteen(self):
        rows = [entry(1000 + i, "/api/Invites") for i in range(20)]
        found = self.check(api=activity_api(modules={"users": rows}))
        self.assertEqual(16, len(found))
        self.assertEqual("5 more privileged changes in five minutes", found[-1].title)
        self.assertEqual("security:privileged-change:burst:1015", found[-1].fingerprint)


OWNER_KEY = "SHA256:OwnerRsaKey0" + "o" * 31           # the shape sshd prints; the value made up
COLLEAGUE_KEY = "SHA256:ColleagueKey" + "c" * 31
STRANGER_KEY = "SHA256:StrangerKey0" + "s" * 31


def accepted(when, ip, key=OWNER_KEY, method="publickey", user="ubuntu", key_type="RSA", pid=900001):
    """One sshd 'Accepted' line as Ubuntu 24.04's rsyslog writes it; ``when`` is a UTC datetime."""
    stamp = to_ist(when).isoformat(timespec="microseconds")
    key_part = f" ssh2: {key_type} {key}" if method == "publickey" else " ssh2"
    return (f"{stamp} ip-172-31-20-148 sshd[{pid}]: Accepted {method} for {user} from {ip} port 51234{key_part}\n")


class SshLoginTests(SecurityTestCase):
    def setUp(self):
        super().setUp()
        self.auth = self.tmp / "auth.log"
        self.logs = [self.auth, self.tmp / "auth.log.1"]

    def log(self, *lines):
        """Append to auth.log, and date the file by its last line, as syslog would."""
        with open(self.auth, "a", encoding="utf-8") as fh:
            fh.writelines(lines)
        last = max(datetime.fromisoformat(line.split(" ", 1)[0]).timestamp() for line in lines)
        os.utime(self.auth, (last, last))

    def ssh(self, now=THURSDAY_MARKET, env=None):
        return self.by_rule(self.check(now=now, env=env, auth_logs=self.logs), "ssh-login")

    def test_a_week_of_the_owner_from_six_home_addresses_says_nothing(self):
        # 20–26 Sep on the server: 6 home-ISP addresses, one key. The address changes; the key does not.
        homes = ["223.181.42.7", "223.181.46.9", "122.168.80.11", "42.111.36.13", "182.69.183.15", "223.185.19.17"]
        start = datetime(2026, 9, 20, 3, 30, tzinfo=timezone.utc)   # Sunday 09:00 IST
        self.log(accepted(start - timedelta(minutes=1), homes[0]))   # the session that starts Sentinel
        run, notifier = self.engine(auth_logs=self.logs)
        pid = 1
        for step in range(7 * 8):   # a check every three hours for a week
            now = start + timedelta(hours=3 * step)
            if step == 28:           # the weekly rotation, mid-replay
                os.replace(self.auth, self.tmp / "auth.log.1")
            home = homes[(step // 5) % len(homes)]   # a new home address every fifteen hours
            self.log(*(accepted(now - timedelta(minutes=50 - 10 * k), home, pid=(pid := pid + 1)) for k in range(4)))
            run(now)
        self.assertEqual([], notifier.sent)

    def test_the_first_check_learns_the_keys_in_use_and_reports_none(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=1), "223.185.19.240"))   # the owner, right now
        with gzip.open(self.tmp / "auth.log.2.gz", "wt", encoding="utf-8") as fh:        # a colleague, weeks ago
            fh.write(accepted(datetime(2026, 9, 10, 4, 30, tzinfo=timezone.utc), "182.69.1.2", key=COLLEAGUE_KEY))
        self.assertEqual([], self.ssh())
        self.log(accepted(THURSDAY_MARKET + timedelta(minutes=3), "42.111.9.9", key=COLLEAGUE_KEY))
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=5)))   # a known key, new address

    def test_a_key_never_seen_before_is_a_medium_notice_once(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=1), "223.185.19.240"))
        self.ssh()
        self.log(accepted(THURSDAY_MARKET + timedelta(minutes=2), "198.51.100.44", key=STRANGER_KEY,
                          key_type="ED25519"))
        found = self.ssh(now=THURSDAY_MARKET + timedelta(minutes=5))
        self.assertEqual(["security:ssh-login:key:SHA256:StrangerKey0ssss"], [f.fingerprint for f in found])
        f = found[0]
        self.assertEqual(Severity.MEDIUM, f.severity)
        self.assertEqual({"notice": True}, f.extra)
        self.assertIn("ED25519 key " + STRANGER_KEY, f.evidence[0])
        self.assertIn("ubuntu from 198.51.100.44 at 24 Sep 11:32 IST", f.evidence[0])
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=10)))
        self.log(accepted(THURSDAY_MARKET + timedelta(minutes=12), "203.0.113.50", key=STRANGER_KEY))
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=15)))   # known from then on

    def test_a_password_login_is_high_and_reported_once(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=1), "223.185.19.240"))
        self.ssh()
        self.log(accepted(THURSDAY_MARKET + timedelta(minutes=4, seconds=30), "203.0.113.9", method="password"),
                 accepted(THURSDAY_MARKET + timedelta(minutes=4, seconds=40), "203.0.113.9",
                          method="keyboard-interactive/pam"))
        found = self.ssh(now=THURSDAY_MARKET + timedelta(minutes=5))
        self.assertEqual(["security:ssh-login:password:ubuntu:203.0.113.9"], [f.fingerprint for f in found])
        self.assertEqual(Severity.HIGH, found[0].severity)
        self.assertEqual({"notice": True}, found[0].extra)
        self.assertIn("with a password", found[0].title)
        self.assertIn("2 sessions", found[0].summary)
        self.assertIn("PasswordAuthentication no", found[0].suggestion)
        # The next check re-reads the overlap with this one; the same lines are not news twice.
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=6)))

    def test_a_password_login_in_the_minutes_before_the_first_check_is_still_reported(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=5), "203.0.113.9", method="password"),
                 accepted(THURSDAY_MARKET - timedelta(days=3), "203.0.113.10", method="password"))
        self.assertEqual(["security:ssh-login:password:ubuntu:203.0.113.9"], [f.fingerprint for f in self.ssh()])

    def test_instance_connect_listed_keys_and_allowed_ranges_are_known(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=1), "223.185.19.240"))
        self.ssh()
        self.log(accepted(THURSDAY_MARKET + timedelta(minutes=1), "13.233.177.3", key="SHA256:" + "e" * 43),
                 accepted(THURSDAY_MARKET + timedelta(minutes=2), "198.51.100.44", key=COLLEAGUE_KEY),
                 accepted(THURSDAY_MARKET + timedelta(minutes=3), "10.1.2.3", key=STRANGER_KEY))
        env = {"SENTINEL_SSH_KEYS": COLLEAGUE_KEY[len("SHA256:"):][:20], "SENTINEL_SSH_ALLOWED": "10.0.0.0/8"}
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=5), env=env))

    @unittest.skipIf(hasattr(os, "geteuid") and os.geteuid() == 0, "root reads everything")
    def test_a_log_it_may_not_read_is_one_low_finding(self):
        self.log(accepted(THURSDAY_MARKET - timedelta(minutes=1), "223.185.19.240"))
        os.chmod(self.auth, 0)
        try:
            found = self.ssh()
        finally:
            os.chmod(self.auth, 0o600)
        self.assertEqual(["security:ssh-login:unreadable"], [f.fingerprint for f in found])
        self.assertEqual(Severity.LOW, found[0].severity)
        self.assertIn("adm", found[0].suggestion)
        self.assertEqual([], self.ssh(now=THURSDAY_MARKET + timedelta(minutes=5)))   # readable again: learns, quiet

    def test_a_machine_without_the_log_is_silent(self):
        self.assertEqual([], self.ssh())


class OpenPortTests(SecurityTestCase):
    def test_the_database_published_to_the_world_is_high(self):
        self.shell.outputs["ss"] = (0, SERVER_SS + "LISTEN 0 4096 0.0.0.0:5432 0.0.0.0:*\n"
                                                   "LISTEN 0 4096 [::]:5432 [::]:*\n")
        self.shell.outputs["docker"] = (0, "algotrading_db\t0.0.0.0:5432->5432/tcp, :::5432->5432/tcp\n")
        found = self.by_rule(self.check(), "open-port")
        self.assertEqual(["security:open-port"], [f.fingerprint for f in found])
        f = found[0]
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("Port 5432 is open to the internet on the server", f.title)
        self.assertIn("tcp/5432: listening on 0.0.0.0:5432", f.evidence)
        self.assertIn("tcp/5432: listening on [::]:5432", f.evidence)
        self.assertIn('"127.0.0.1:5432:5432"', f.suggestion)

    def test_a_port_only_docker_knows_about_still_counts(self):
        self.shell.outputs["docker"] = (0, "web\t0.0.0.0:8080->80/tcp\n")
        f = self.by_rule(self.check(), "open-port")[0]
        self.assertIn("8080", f.title)
        self.assertIn("tcp/8080: container web publishes 0.0.0.0:8080->80/tcp", f.evidence)

    def test_a_listener_on_the_private_interface_counts_as_public(self):
        self.shell.outputs["ss"] = (0, "LISTEN 0 128 172.31.20.148:9000 0.0.0.0:*\n")
        self.assertEqual(["Port 9000 is open to the internet on the server"],
                         [f.title for f in self.by_rule(self.check(), "open-port")])

    def test_ports_declared_public_are_fine(self):
        self.shell.outputs["ss"] = (0, SERVER_SS + "LISTEN 0 511 0.0.0.0:443 0.0.0.0:*\n")
        self.assertEqual([], self.by_rule(self.check(env={"SENTINEL_PUBLIC_PORTS": "22, 443"}), "open-port"))

    def test_a_trading_day_of_runner_metrics_ports_is_one_incident_not_twenty(self):
        # Until 28 Sep every strategy runner served its metrics on 0.0.0.0 at the first free port of
        # 8000-8019 (core/metrics.py): a finding per port was ~20 HIGH incidents every morning.
        self.shell.outputs["ss"] = (0, SERVER_SS + "".join(f"LISTEN 0 5 0.0.0.0:{p} 0.0.0.0:*\n"
                                                           for p in range(8000, 8020)))
        found = self.by_rule(self.check(), "open-port")
        self.assertEqual(1, len(found))
        f = found[0]
        self.assertEqual("security:open-port", f.fingerprint)
        self.assertEqual("20 ports are open to the internet on the server: 8000-8019", f.title)
        self.assertLessEqual(len(f.evidence), 7)
        self.assertEqual("… and 14 more: 8006-8019", f.evidence[-1])
        self.assertIn("core/metrics.py", f.suggestion)
        self.assertIn("METRICS_BIND_ADDRESS", f.suggestion)

    def test_a_port_that_comes_and_goes_keeps_one_fingerprint(self):
        self.shell.outputs["ss"] = (0, "LISTEN 0 5 0.0.0.0:8000 0.0.0.0:*\n")
        first = self.by_rule(self.check(), "open-port")
        self.shell.outputs["ss"] = (0, "LISTEN 0 5 0.0.0.0:8000 0.0.0.0:*\nLISTEN 0 5 0.0.0.0:8001 0.0.0.0:*\n")
        second = self.by_rule(self.check(), "open-port")
        self.assertEqual([f.fingerprint for f in first], [f.fingerprint for f in second])
        self.assertIn("8000-8001", second[0].title)

    def test_a_range_of_ports_can_be_declared_public(self):
        self.shell.outputs["ss"] = (0, SERVER_SS + "".join(f"LISTEN 0 5 0.0.0.0:{p} 0.0.0.0:*\n"
                                                           for p in (8000, 8019, 9000)))
        found = self.by_rule(self.check(env={"SENTINEL_PUBLIC_PORTS": "22,8000-8019"}), "open-port")
        self.assertEqual(["Port 9000 is open to the internet on the server"], [f.title for f in found])
        self.assertIn("(22, 8000-8019)", found[0].summary)

    def test_a_malformed_public_ports_item_is_ignored_not_fatal(self):
        self.shell.outputs["ss"] = (0, "LISTEN 0 5 0.0.0.0:443 0.0.0.0:*\nLISTEN 0 5 0.0.0.0:8080 0.0.0.0:*\n")
        found = self.by_rule(self.check(env={"SENTINEL_PUBLIC_PORTS": "22, 9000-8000, abc, 443, 70000"}),
                             "open-port")
        self.assertEqual(["Port 8080 is open to the internet on the server"], [f.title for f in found])

    def test_when_ss_fails_the_open_port_stays_open(self):
        self.shell.outputs["ss"] = (0, "LISTEN 0 128 0.0.0.0:6379 0.0.0.0:*\n")
        first = self.by_rule(self.check(), "open-port")
        self.shell.outputs["ss"] = (1, "")
        again = self.by_rule(self.check(now=THURSDAY_MARKET + timedelta(minutes=5)), "open-port")
        self.assertEqual(["security:open-port"], [f.fingerprint for f in first])
        self.assertEqual([f.fingerprint for f in first], [f.fingerprint for f in again])
        # As observed when ss last answered: an incident a person resolved since is not reopened by it (30 Sep).
        self.assertEqual([None], [f.observed_utc for f in first])
        self.assertEqual([THURSDAY_MARKET], [f.observed_utc for f in again])

    def test_a_closed_port_clears_the_finding(self):
        self.shell.outputs["ss"] = (0, "LISTEN 0 128 0.0.0.0:6379 0.0.0.0:*\n")
        self.assertEqual(1, len(self.by_rule(self.check(), "open-port")))
        self.shell.outputs["ss"] = (0, SERVER_SS)
        self.assertEqual([], self.by_rule(self.check(), "open-port"))
        self.shell.outputs["ss"] = (1, "")
        self.assertEqual([], self.by_rule(self.check(), "open-port"), "nothing left to carry")


class SecretFilePermissionTests(SecurityTestCase):
    def test_a_world_readable_env_is_high(self):
        env_file = self.tmp / ".env"
        env_file.write_text("POSTGRES_PASSWORD=do-not-print-me\n")
        os.chmod(env_file, 0o644)
        found = self.by_rule(self.check(), "secret-file-permissions")
        self.assertEqual(["security:secret-file-permissions:.env"], [f.fingerprint for f in found])
        self.assertIn("mode 0644", found[0].evidence)
        self.assertIn("others can read", found[0].summary)
        self.assertNotIn("do-not-print-me", everything_said(found))

    def test_regenerated_local_settings_are_checked_too(self):
        settings = self.tmp / "src" / "AlgoTrading.Api" / "appsettings.Local.json"
        settings.parent.mkdir(parents=True)
        settings.write_text("{}")
        os.chmod(settings, 0o664)
        found = self.by_rule(self.check(), "secret-file-permissions")
        self.assertEqual(Severity.HIGH, found[0].severity)
        self.assertIn("_gen_local_settings.py", found[0].suggestion)

    def test_owner_only_and_missing_files_are_fine(self):
        env_file = self.tmp / ".env"
        env_file.write_text("x=1\n")
        os.chmod(env_file, 0o600)
        self.assertEqual([], self.by_rule(self.check(), "secret-file-permissions"))


class SecretInGitTests(SecurityTestCase):
    FAKE_AWS = "AKIA" + "Z" * 16   # assembled at run time, so this file never matches its own scan

    def git(self, hits):
        """git grep -z output for each pattern, from {kind: [(path, line, text)]}."""
        def run(args):
            pattern = args[args.index("-e") + 1]
            kind = next(p.kind for p in GIT_PATTERNS if p.ere == pattern)
            records = hits.get(kind, [])
            return (0, "".join(f"{p}\0{n}\0{t}\n" for p, n, t in records)) if records else (1, "")
        return run

    def test_a_committed_key_is_critical_and_named_by_file_and_line_only(self):
        self.shell.outputs["git"] = self.git({"aws-key": [("deploy/aws.json", 12, f'"key": "{self.FAKE_AWS}"')]})
        found = self.by_rule(self.check(), "secret-in-git")
        self.assertEqual(["security:secret-in-git:aws-key:deploy/aws.json"], [f.fingerprint for f in found])
        self.assertEqual(Severity.CRITICAL, found[0].severity)
        self.assertEqual(["deploy/aws.json:12"], found[0].evidence)
        self.assertNotIn(self.FAKE_AWS, everything_said(found))

    def test_passwords_count_only_as_literals_outside_tests_and_docs(self):
        self.shell.outputs["git"] = self.git({"password": [
            ("src/AlgoTrading.Api/appsettings.json", 4, '"TradingDb": "Host=x;Username=postgres;Password=CHANGE_ME"'),
            ("src/AlgoTrading.PythonEngine/core/api_client.py", 80, 'password = os.getenv("ENGINE_PASSWORD") or ""'),
            ("tests/AlgoTrading.UnitTests/X.cs", 26, '"Host=localhost;Password=testpass1"'),
            ("docs/setup.md", 9, "ADMIN_PASSWORD=S0meRealLookingValue"),
            (".env.example", 26, "POSTGRES_PASSWORD=change-me-to-a-long-random-password"),
            ("deploy/prod.json", 7, '"TradingDb": "Host=db;Username=postgres;Password=Xq9long-value"'),
            ("scripts/tool.py", 12, 'client.login(password="Summer2026x!")'),
        ]})
        found = self.by_rule(self.check(), "secret-in-git")
        self.assertEqual(["deploy/prod.json:7", "scripts/tool.py:12"], sorted(f.evidence[0] for f in found))
        said = everything_said(found)
        self.assertNotIn("Xq9long-value", said)
        self.assertNotIn("Summer2026x!", said)

    def test_it_scans_once_a_day_and_holds_the_result_in_between(self):
        self.shell.outputs["git"] = self.git({"private-key": [("certs/server.key", 1, "<pem header>")]})
        first = self.by_rule(self.check(), "secret-in-git")
        scans = len(self.shell.ran("git"))
        again = self.by_rule(self.check(now=THURSDAY_MARKET + timedelta(hours=3)), "secret-in-git")
        self.assertEqual(scans, len(self.shell.ran("git")))
        self.assertEqual([f.fingerprint for f in first], [f.fingerprint for f in again])
        self.assertEqual([None], [f.observed_utc for f in first])
        self.assertEqual([THURSDAY_MARKET], [f.observed_utc for f in again], "replayed: as observed at the scan")
        self.shell.outputs["git"] = (1, "")   # fixed overnight
        tomorrow = self.check(now=THURSDAY_MARKET + timedelta(days=1))
        self.assertGreater(len(self.shell.ran("git")), scans)
        self.assertEqual([], self.by_rule(tomorrow, "secret-in-git"))

    def test_a_secret_a_person_resolved_is_not_reopened_by_the_days_replay(self):
        # 30 Sep: a scan's result, replayed every five minutes until the next scan, opened again what a person
        # had resolved. Only the next scan that still finds it is news.
        self.shell.outputs["git"] = self.git({"private-key": [("certs/server.key", 1, "<pem header>")]})
        run, notifier = self.engine()
        run(THURSDAY_MARKET)
        scans = len(self.shell.ran("git"))
        self.assertEqual(1, len(notifier.sent))
        self.resolve(self.store.rows()[0], THURSDAY_MARKET + timedelta(minutes=2),
                     "Rotated; the file is a revoked test certificate.")
        for minutes in (5, 10, 180):
            run(THURSDAY_MARKET + timedelta(minutes=minutes))
        self.assertEqual(scans, len(self.shell.ran("git")), "replayed, not rescanned")
        self.assertEqual(["resolved"], [r["status"] for r in self.store.rows()])
        self.assertEqual(1, len(notifier.sent))
        self.assertEqual([], self.ctx.state("security").data["git"]["findings"], "let go")

        run(THURSDAY_MARKET + timedelta(days=1))   # tomorrow's scan still finds it
        self.assertGreater(len(self.shell.ran("git")), scans)
        self.assertEqual(["resolved", "open"], [r["status"] for r in self.store.rows()])
        self.assertIn("NEW [CRITICAL] A private key is committed to git in certs/server.key", notifier.sent[-1])

    def test_git_failing_is_not_a_finding_and_is_retried(self):
        self.shell.outputs["git"] = (128, "")
        self.assertEqual([], self.by_rule(self.check(), "secret-in-git"))
        self.shell.outputs["git"] = self.git({"aws-key": [("a.py", 3, "x")]})
        self.assertEqual(1, len(self.by_rule(self.check(now=THURSDAY_MARKET + timedelta(minutes=5)),
                                             "secret-in-git")))

    # Assembled at run time, like FAKE_AWS: a literal would make this very file a finding once committed.
    FAKE_JWT = ".".join(["eyJ" + "hdr0" * 4, "eyJ" + "pay0" * 4, "sig0" * 4])
    FAKE_BOT = "1234567890" + ":" + "AbC-_dEf" * 4 + "xyz"

    def test_tokens_in_tests_and_docs_are_critical_too(self):
        # A Dhan or FYERS access token is a JWT, and a doc example or a fixture is where one gets pasted;
        # docs/ is published at openfno.com/docs.
        self.shell.outputs["git"] = self.git({
            "jwt": [("docs/brokers/dhan.md", 40, f"access-token: {self.FAKE_JWT}"),
                    ("src/AlgoTrading.PythonEngine/tests/test_dhan_feed.py", 12, f'TOKEN = "{self.FAKE_JWT}"')],
            "telegram-token": [("tests/AlgoTrading.UnitTests/NotifierTests.cs", 7, f'bot = "{self.FAKE_BOT}";')],
        })
        found = self.by_rule(self.check(), "secret-in-git")
        self.assertEqual({"security:secret-in-git:jwt:docs/brokers/dhan.md",
                          "security:secret-in-git:jwt:src/AlgoTrading.PythonEngine/tests/test_dhan_feed.py",
                          "security:secret-in-git:telegram-token:tests/AlgoTrading.UnitTests/NotifierTests.cs"},
                         {f.fingerprint for f in found})
        self.assertTrue(all(f.severity == Severity.CRITICAL for f in found))
        said = everything_said(found)
        self.assertNotIn(self.FAKE_JWT, said)
        self.assertNotIn(self.FAKE_BOT, said)

    def test_a_marked_or_listed_fixture_is_not_reported(self):
        self.shell.outputs["git"] = self.git({"jwt": [
            ("tests/a.py", 3, f'JWT = "{self.FAKE_JWT}"  # pragma: allowlist secret'),
            ("tests/b.py", 9, f'JWT = "{self.FAKE_JWT}"'),
            ("tests/b.py", 10, f'OTHER = "{self.FAKE_JWT}"'),
            ("web/src/lib/c.test.ts", 1, f"const jwt = '{self.FAKE_JWT}'"),
        ]})
        env = {"SENTINEL_SECRET_ALLOW": "tests/b.py:9, ./web/src/lib/c.test.ts"}
        found = self.by_rule(self.check(env=env), "secret-in-git")
        self.assertEqual(["tests/b.py:10"], [e for f in found for e in f.evidence])

    def test_allowlist_parser(self):
        self.assertEqual(({("tests/b.py", 9)}, {"web/x.ts", "docs/y.md", ".env.example"}),
                         parse_secret_allowlist("tests/b.py:9, ./web/x.ts docs/y.md,.env.example"))

    def test_path_and_literal_rules(self):
        self.assertTrue(is_test_or_doc("src/AlgoTrading.PythonEngine/tests/test_sentinel_engine.py"))
        self.assertTrue(is_test_or_doc("web/src/lib/format.test.ts"))
        self.assertTrue(is_test_or_doc("tests/AlgoTrading.UnitTests/ServiceLifetimeTests.cs"))
        self.assertTrue(is_test_or_doc("web/README.md"))
        self.assertFalse(is_test_or_doc("src/AlgoTrading.Api/Program.cs"))
        self.assertFalse(is_literal_password("x.cs", "            password = \"(unchanged)\";"))
        self.assertFalse(is_literal_password("x.ps1", "$loginBody = @{ password = $adminPassword }"))
        self.assertFalse(is_literal_password("x.tsx", "const resetPassword = useResetPassword()"))
        self.assertTrue(is_literal_password("appsettings.Production.json", '"Password": "k8s-Pr0d-value"'))
        self.assertTrue(is_literal_password("deploy/.env.prod", "DB_PASSWORD=k8sPr0dValue"))


class TimedShell(FakeShell):
    """A FakeShell whose audits take time on a clock the agent reads: a timed-out call used all of its timeout."""

    def __init__(self, clock, **outputs):
        super().__init__(**outputs)
        self.clock = clock
        self.timeouts = []

    def __call__(self, args, timeout=20.0, cwd=None):
        out = super().__call__(args, timeout, cwd)
        if args[0] in ("dotnet", "npm"):
            self.timeouts.append((args[0], timeout))
            self.clock[0] += timeout if out[0] == 124 else 5.0
        return out


NPM_CLEAN = json.dumps({"auditReportVersion": 2, "vulnerabilities": {},
                        "metadata": {"vulnerabilities": {"total": 0}}})
MONDAY_MARKET = datetime(2026, 9, 28, 5, 30, tzinfo=timezone.utc)   # 11:00 IST


class DependencyTests(SecurityTestCase):
    def setUp(self):
        super().setUp()
        (self.tmp / "src" / "AlgoTrading.Api").mkdir(parents=True)
        (self.tmp / "web").mkdir()
        (self.tmp / "web" / "package-lock.json").write_text("{}")
        (self.tmp / "src" / "AlgoTrading.Api" / "AlgoTrading.Api.csproj").write_text("<Project />")
        self.touch("web/package-lock.json", SUNDAY - timedelta(days=1))
        self.touch("src/AlgoTrading.Api/AlgoTrading.Api.csproj", SUNDAY - timedelta(days=1))
        self.shell.outputs["dotnet"] = (0, DOTNET_JSON)
        self.shell.outputs["npm"] = (1, NPM_AUDIT)   # npm audit exits 1 when it finds anything

    def touch(self, rel, moment):
        """The file changed at ``moment`` (a pull, a deploy): its mtime, on the test's clock."""
        os.utime(self.tmp / rel, (moment.timestamp(), moment.timestamp()))

    def npm_packages(self, findings):
        return sorted(f.fingerprint.split(":npm:")[1] for f in findings if ":npm:" in f.fingerprint)

    def test_never_during_market_hours(self):
        self.check(now=THURSDAY_MARKET)
        self.assertEqual([], self.shell.ran("dotnet") + self.shell.ran("npm"))

    def test_one_finding_per_vulnerable_package(self):
        found = self.by_rule(self.check(now=SUNDAY), "vulnerable-dependency")
        fps = {f.fingerprint.split("vulnerable-dependency:")[1]: f for f in found}
        self.assertEqual({"nuget:System.Text.Json", "nuget:Some.Parser", "npm:lodash-es", "npm:nanoid",
                          "npm:evil-critical"}, set(fps))
        self.assertEqual(Severity.MEDIUM, fps["nuget:System.Text.Json"].severity)
        self.assertEqual(Severity.HIGH, fps["nuget:Some.Parser"].severity)          # one advisory is Critical
        self.assertEqual(Severity.HIGH, fps["npm:evil-critical"].severity)
        self.assertIn("pulled in by @excalidraw/excalidraw", fps["npm:nanoid"].summary)
        self.assertIn("major version", fps["npm:nanoid"].suggestion)
        self.assertIn("--no-restore", self.shell.ran("dotnet")[0])

    def test_the_weekly_result_stands_until_the_next_scan(self):
        first = self.by_rule(self.check(now=SUNDAY), "vulnerable-dependency")
        later = self.by_rule(self.check(now=SUNDAY + timedelta(days=3)), "vulnerable-dependency")
        self.assertEqual(1, len(self.shell.ran("dotnet")))
        self.assertEqual({f.fingerprint for f in first}, {f.fingerprint for f in later})
        self.assertEqual({None}, {f.observed_utc for f in first})
        self.assertEqual({SUNDAY}, {f.observed_utc for f in later}, "replayed: as observed at the scan")
        self.check(now=SUNDAY + timedelta(days=7))
        self.assertEqual(2, len(self.shell.ran("dotnet")))

    def test_a_vulnerability_a_person_resolved_is_not_reopened_by_the_weekly_replay(self):
        # 30 Sep: the week's result, replayed until the next scan, opened again what a person had resolved.
        run, notifier = self.engine()
        run(SUNDAY)
        lodash = next(r for r in self.store.rows() if r["fingerprint"].endswith(":npm:lodash-es"))
        sent = len(notifier.sent)
        self.resolve(lodash, SUNDAY + timedelta(minutes=30), "Not reachable from the console; accepted until 4 Oct.")
        run(SUNDAY + timedelta(hours=1))
        run(SUNDAY + timedelta(days=2))
        self.assertEqual(1, len(self.shell.ran("dotnet")), "replayed, not rescanned")
        episodes = [r for r in self.store.rows() if r["fingerprint"] == lodash["fingerprint"]]
        self.assertEqual(["resolved"], [r["status"] for r in episodes])
        self.assertEqual(sent, len(notifier.sent))
        self.assertEqual(4, len([r for r in self.store.rows() if r["status"] == "open"]), "the others stand")
        kept = self.ctx.state("security").data["deps"]["findings"]
        self.assertNotIn(lodash["fingerprint"], {d["fingerprint"] for d in kept}, "let go")

        run(SUNDAY + timedelta(days=7))   # the next weekly scan still finds it
        self.assertEqual(2, len(self.shell.ran("dotnet")))
        episodes = [r for r in self.store.rows() if r["fingerprint"] == lodash["fingerprint"]]
        self.assertEqual(["resolved", "open"], [r["status"] for r in episodes])
        self.assertIn("Last time: Not reachable from the console; accepted until 4 Oct.", notifier.sent[-1])

    def test_a_scan_that_cannot_run_is_not_a_finding(self):
        self.shell.outputs["dotnet"] = (1, DOTNET_NO_ASSETS)
        self.shell.outputs["npm"] = (1, json.dumps({"error": {"code": "ENOTFOUND", "summary": "offline"}}))
        self.assertEqual([], self.by_rule(self.check(now=SUNDAY), "vulnerable-dependency"))
        self.check(now=SUNDAY + timedelta(hours=1))
        self.assertEqual(1, len(self.shell.ran("dotnet")))           # not hammered every five minutes
        self.check(now=SUNDAY + timedelta(hours=7))
        self.assertEqual(2, len(self.shell.ran("dotnet")))

    def test_a_part_that_is_not_there_is_a_clean_audit_not_a_failed_one(self):
        (self.tmp / "web" / "package-lock.json").unlink()
        self.check(now=SUNDAY)
        self.check(now=SUNDAY + timedelta(hours=7))
        self.assertEqual([], self.shell.ran("npm"))
        self.assertEqual(1, len(self.shell.ran("dotnet")))   # the week's scan counted as done

    def test_an_sdk_without_json_output_is_read_from_the_table(self):
        self.shell.outputs["dotnet"] = lambda args: ((1, "Unrecognized command or argument '--format'")
                                                     if "--format" in args else (0, DOTNET_TABLE))
        found = self.by_rule(self.check(now=SUNDAY), "vulnerable-dependency")
        self.assertIn("security:vulnerable-dependency:nuget:Microsoft.Data.SqlClient", {f.fingerprint for f in found})

    def test_table_parser(self):
        parsed = parse_dotnet_table(DOTNET_TABLE)
        self.assertEqual({"Microsoft.Data.SqlClient", "System.Text.Json", "Newtonsoft.Json",
                          "System.Text.RegularExpressions"}, set(parsed))
        sql = parsed["Microsoft.Data.SqlClient"]
        self.assertEqual(("1.1.0", True, "high", 2), (sql["version"], sql["direct"], sql["severity"],
                                                     len(sql["advisories"])))
        self.assertFalse(parsed["Newtonsoft.Json"]["direct"])
        self.assertEqual("low", parsed["System.Text.RegularExpressions"]["severity"])

    def test_npm_carriers_are_folded_into_the_package_with_the_advisory(self):
        parsed = parse_npm_audit(NPM_AUDIT)
        self.assertNotIn("chevrotain", parsed)
        self.assertEqual(["@excalidraw/excalidraw"], parsed["lodash-es"]["via"])

    def test_a_fix_that_is_pulled_is_rescanned_at_the_next_check_not_a_week_later(self):
        # 27 Sep: the morning scan found lodash-es and nanoid; f545822 pinned patched versions (npm audit: 0)
        # the same day, and the two incidents would have stayed open until 4 Oct.
        before = self.by_rule(self.check(now=SUNDAY), "vulnerable-dependency")
        self.assertEqual(["evil-critical", "lodash-es", "nanoid"], self.npm_packages(before))
        self.shell.outputs["npm"] = (0, NPM_CLEAN)
        self.touch("web/package-lock.json", SUNDAY + timedelta(hours=2))
        after = self.by_rule(self.check(now=SUNDAY + timedelta(hours=2, minutes=5)), "vulnerable-dependency")
        self.assertEqual(2, len(self.shell.ran("npm")))
        self.assertEqual([], self.npm_packages(after))
        self.assertIn("security:vulnerable-dependency:nuget:System.Text.Json", {f.fingerprint for f in after})
        self.check(now=SUNDAY + timedelta(hours=2, minutes=10))
        self.assertEqual(2, len(self.shell.ran("npm")), "the same checkout is not scanned twice")

    def test_a_changed_project_file_is_rescanned_too(self):
        self.check(now=SUNDAY)
        self.touch("src/AlgoTrading.Api/AlgoTrading.Api.csproj", SUNDAY + timedelta(hours=3))
        self.check(now=SUNDAY + timedelta(hours=3, minutes=5))
        self.assertEqual(2, len(self.shell.ran("dotnet")))

    def test_a_change_during_market_hours_waits_for_the_close(self):
        self.check(now=SUNDAY)
        self.touch("web/package-lock.json", MONDAY_MARKET - timedelta(minutes=30))
        self.check(now=MONDAY_MARKET)
        self.assertEqual(1, len(self.shell.ran("npm")))
        self.check(now=MONDAY_MARKET + timedelta(hours=5, minutes=30))   # 16:30 IST
        self.assertEqual(2, len(self.shell.ran("npm")))

    def test_no_scan_while_the_morning_job_runs(self):
        # 08:45 is when the API and the feeds restart: nothing may hold the engine then.
        self.check(now=SUNDAY)
        monday_0840 = datetime(2026, 9, 28, 3, 10, tzinfo=timezone.utc)
        self.touch("web/package-lock.json", monday_0840 - timedelta(minutes=5))
        self.check(now=monday_0840)
        self.assertEqual(1, len(self.shell.ran("npm")))

    def test_a_scan_recorded_before_this_rule_is_rescanned_once(self):
        # The server's state from the 27 Sep scan has no record of the files it read.
        state = self.tmp / "logs" / "sentinel" / "state-security.json"
        state.parent.mkdir(parents=True, exist_ok=True)
        state.write_text(json.dumps({"deps": {"last_success": SUNDAY.isoformat(), "last_attempt": SUNDAY.isoformat(),
                                              "findings": []}}))
        self.check(now=SUNDAY + timedelta(hours=7))
        self.check(now=SUNDAY + timedelta(hours=7, minutes=5))
        self.assertEqual(1, len(self.shell.ran("dotnet")))

    def test_a_hung_dotnet_is_not_run_a_second_time_for_its_table(self):
        clock = [1000.0]
        self.shell = TimedShell(clock, dotnet=(124, ""), npm=(1, NPM_AUDIT))
        found = self.by_rule(self.check(now=SUNDAY, monotonic=lambda: clock[0]), "vulnerable-dependency")
        self.assertEqual(1, len(self.shell.ran("dotnet")))
        self.assertEqual(["evil-critical", "lodash-es", "nanoid"], self.npm_packages(found))

    def test_the_whole_scan_is_held_to_its_budget(self):
        clock = [1000.0]
        self.shell = TimedShell(clock, dotnet=(124, ""), npm=(124, ""))
        self.check(now=SUNDAY, monotonic=lambda: clock[0])
        self.assertLessEqual(clock[0] - 1000.0, 240.0)
        self.assertLessEqual(sum(t for _, t in self.shell.timeouts), 240.0)
        self.assertEqual(["dotnet", "npm"], [tool for tool, _ in self.shell.timeouts])

    def test_a_part_with_too_little_budget_left_is_not_started(self):
        clock = [1000.0]
        self.shell = TimedShell(clock, npm=(1, NPM_AUDIT))
        self.shell.outputs["dotnet"] = lambda args: (clock.__setitem__(0, clock[0] + 225.0), (0, DOTNET_JSON))[1]
        self.check(now=SUNDAY, monotonic=lambda: clock[0])
        self.assertEqual([], self.shell.ran("npm"))


class Fail2banTests(SecurityTestCase):
    STATUS = ("Status for the jail: sshd\n|- Filter\n|  |- Currently failed:\t3\n|  |- Total failed:\t900\n"
              "|  `- File list:\t/var/log/auth.log\n`- Actions\n   |- Currently banned:\t{cur}\n"
              "   |- Total banned:\t{total}\n   `- Banned IP list:\t203.0.113.1 203.0.113.2\n")

    def test_without_root_it_is_silent(self):
        self.shell.outputs["fail2ban-client"] = (255, "")
        self.assertEqual([], self.by_rule(self.check(), "fail2ban"))

    def test_a_jump_in_bans_is_a_low_heads_up(self):
        self.shell.outputs["fail2ban-client"] = (0, self.STATUS.format(cur=2, total=100))
        self.assertEqual([], self.by_rule(self.check(), "fail2ban"))           # the first reading is a baseline
        self.shell.outputs["fail2ban-client"] = (0, self.STATUS.format(cur=5, total=110))
        self.assertEqual([], self.by_rule(self.check(), "fail2ban"))
        self.shell.outputs["fail2ban-client"] = (0, self.STATUS.format(cur=30, total=140))
        found = self.by_rule(self.check(), "fail2ban")
        self.assertEqual(["security:fail2ban:sshd"], [f.fingerprint for f in found])
        self.assertEqual(Severity.LOW, found[0].severity)
        self.assertEqual({"notice": True}, found[0].extra)
        self.assertIn("total bans 110 → 140", found[0].evidence)


class NoticeTests(SecurityTestCase):
    def test_one_off_events_are_notices_and_standing_conditions_are_not(self):
        # A notice closes on the next check; the engine should close it without a second message.
        self.shell.outputs["ss"] = (0, "LISTEN 0 128 0.0.0.0:6379 0.0.0.0:*\n")
        api = activity_api(login_rows=[failed_login(i, "198.51.100.7") for i in range(16)],
                           modules={"users": [entry(51139, "/api/Users/7/grants", method="PUT")]})
        found = {f.rule: f for f in self.check(api=api)}
        self.assertEqual({"notice": True}, found["privileged-change"].extra)
        self.assertEqual({}, found["login-failures"].extra)
        self.assertEqual({}, found["open-port"].extra)


class IsolationTests(SecurityTestCase):
    def test_one_broken_rule_does_not_blind_the_others(self):
        self.shell.outputs["ss"] = RuntimeError("ss exploded")
        env_file = self.tmp / ".env"
        env_file.write_text("x=1\n")
        os.chmod(env_file, 0o644)
        found = self.check()
        self.assertEqual({"rule-crashed", "secret-file-permissions"}, {f.rule for f in found})
        crashed = self.by_rule(found, "rule-crashed")[0]
        self.assertEqual("security:rule-crashed:open-port:RuntimeError", crashed.fingerprint)

    def test_it_runs_every_five_minutes_and_notices_close_after_one_clean_check(self):
        self.assertEqual(300, SecurityAgent.interval_seconds)
        self.assertEqual(1, SecurityAgent.resolve_after)


if __name__ == "__main__":
    unittest.main()
