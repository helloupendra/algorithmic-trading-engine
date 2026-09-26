import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

import _bootstrap  # noqa: F401

from sentinel.agents.trading import RUNNING_PATH, TradingAgent, history_path, today_path
from sentinel.clock import IST
from sentinel.engine import SentinelEngine
from sentinel.model import Severity
from sentinel.store import MemoryIncidentStore
from _sentinel_fakes import RecordingNotifier, clock_ticks, make_context

PLAN = """\
# the desk's plan, as config/morning-plan.txt has it
accounts: admin coderforchange

GhostTangentCrossings  BANKNIFTY,NIFTY,SENSEX  2
ChainFlowBuy           BANKNIFTY,NIFTY,SENSEX  2
SmcStructureBreak      BANKNIFTY,NIFTY,SENSEX  2
Fulcrum                BANKNIFTY,NIFTY,SENSEX  2  -
CrudeMomentum          CRUDEOIL                2  -
"""
PLAN_KEYS = [(s, u) for s in ("GhostTangentCrossings", "ChainFlowBuy", "SmcStructureBreak", "Fulcrum")
             for u in ("BANKNIFTY", "NIFTY", "SENSEX")] + [("CrudeMomentum", "CRUDEOIL")]

USER_IDS = {"admin": 1, "coderforchange": 7}
DAY = "2026-09-24"  # a Thursday
LIMITER_429 = ("Runner exited (code 1): requests.exceptions.HTTPError: 429 Client Error: Too Many Requests "
               "for url: http://localhost:5025/api/UserAuth/login")
CLOSED = "Market closed (15:30 IST)"

# A normal day's trade counts per strategy (25 Sep: Ghost 6-8, ChainFlowBuy 1-2).
NORMAL_TRADES = {"GhostTangentCrossings": 7, "ChainFlowBuy": 2, "SmcStructureBreak": 0, "Fulcrum": 120,
                 "CrudeMomentum": 2}


def ist(hour, minute, second=0, day=24):
    return datetime(2026, 9, day, hour, minute, second, tzinfo=IST).astimezone(timezone.utc)


def iso(moment):
    return moment.strftime("%Y-%m-%dT%H:%M:%S.%fZ") if moment else None


def run(run_id, user="admin", strategy="GhostTangentCrossings", underlying="NIFTY", status="Running",
        active=None, started=None, stopped=None, reason=None, by=None, trades=None, net=0.0, unrealized=0.0,
        role=None):
    started = started or ist(9, 18)
    spot = "MCX:CRUDEOIL26OCTFUT" if underlying == "CRUDEOIL" else f"NSE:{underlying}-INDEX"
    return {
        "runId": run_id, "userId": USER_IDS.get(user, 99), "userName": user, "strategyId": 1,
        "strategyName": strategy, "category": "", "underlying": underlying, "spotSymbol": spot, "lots": 2,
        "role": role, "lotSize": 75, "risk": {}, "status": status,
        "isActive": (status in ("Running", "Stopping")) if active is None else active,
        "startedUtc": iso(started), "stoppedUtc": iso(stopped), "stopReason": reason, "stoppedBy": by,
        "durationSeconds": 0, "netPnl": net, "realizedPnl": net, "unrealizedPnl": unrealized,
        "trades": NORMAL_TRADES.get(strategy, 3) if trades is None else trades, "openPositions": 0,
        "groups": 0, "chargesPerLot": 0, "capitalUsed": None,
    }


def dead(row, at, reason=LIMITER_429, by="runner"):
    return dict(row, status="Stopped", isActive=False, stoppedUtc=iso(at), stopReason=reason, stoppedBy=by,
                trades=0, netPnl=0.0)


def full_day(first_id=300):
    """All 26 planned runs, started by the morning job at 09:18 and running."""
    rows, rid = [], first_id
    for user in ("admin", "coderforchange"):
        for strategy, underlying in PLAN_KEYS:
            rows.append(run(rid, user, strategy, underlying, net=450.0 if strategy != "CrudeMomentum" else -300.0))
            rid += 1
    return rows


def without(rows, user, strategy, underlying):
    return [r for r in rows if not (r["userName"] == user and r["strategyName"] == strategy
                                    and r["underlying"] == underlying)]


def routes(running, today, day=DAY, nse=True, mcx=True, trading=True):
    def session(is_open):
        return {"isTradingDay": trading, "isMarketOpen": is_open, "isHoliday": not trading,
                "holidayName": None if trading else "Gandhi Jayanti"}
    return {
        "/api/MarketSession/check?exchange=NSE&segment=CM": session(nse),
        "/api/MarketSession/check?exchange=MCX&segment=COM": session(mcx),
        RUNNING_PATH: running,
        today_path(day): today,
    }


def sep24():
    """
    24 Sep as the API recorded it: (run row, started, stopped or None, stop reason).
    The morning job's 26 starts, 16 of them killed by the sign-in limiter within
    half a minute; two admin restarts at 11:20 that died the same way; the rest
    restarted by 11:29; the desk's stop of every run at 15:30.
    """
    events, rid = [], 205
    for i, (strategy, underlying) in enumerate(PLAN_KEYS):
        started = ist(9, 17, 36) + timedelta(seconds=2.3 * i)
        died = strategy == "Fulcrum" and underlying != "BANKNIFTY" or strategy == "CrudeMomentum"
        events.append((run(rid, "admin", strategy, underlying, trades=0), started,
                       started + timedelta(seconds=3) if died else None, LIMITER_429 if died else None))
        rid += 1
    for i, (strategy, underlying) in enumerate(PLAN_KEYS):
        started = ist(9, 18, 8) + timedelta(seconds=2.2 * i)
        events.append((run(rid, "coderforchange", strategy, underlying, trades=0), started,
                       started + timedelta(seconds=1), LIMITER_429))
        rid += 1
    for user, strategy, underlying, at, dies in (
            ("admin", "Fulcrum", "NIFTY", ist(11, 20, 24), True), ("admin", "Fulcrum", "SENSEX", ist(11, 20, 33), True),
            ("admin", "CrudeMomentum", "CRUDEOIL", ist(11, 20, 42), False),
            ("coderforchange", "GhostTangentCrossings", "BANKNIFTY", ist(11, 20, 51), False),
            ("coderforchange", "GhostTangentCrossings", "NIFTY", ist(11, 21, 0), False),
            ("admin", "Fulcrum", "NIFTY", ist(11, 28, 38), False), ("admin", "Fulcrum", "SENSEX", ist(11, 28, 42), False)):
        events.append((run(rid, user, strategy, underlying, trades=0), at, at + timedelta(seconds=1) if dies else None,
                       LIMITER_429 if dies else None))
        rid += 1
    for i, (strategy, underlying) in enumerate(PLAN_KEYS[2:]):
        at = ist(11, 28, 47) + timedelta(seconds=4 * i)
        events.append((run(rid, "coderforchange", strategy, underlying, trades=0), at, None, None))
        rid += 1
    return events


def as_of(events, moment, close=ist(15, 30, 30)):
    """(running, today) as the two GETs would have answered at ``moment``."""
    running, today = [], []
    for row, started, stopped, reason in events:
        if started > moment:
            continue
        row = dict(row, startedUtc=iso(started))
        end, why, by = (stopped, reason, "runner") if stopped else (close, CLOSED, "market-hours")
        if end > moment:
            row.update(status="Running", isActive=True)
            running.append(row)
        else:
            row.update(status="Stopped", isActive=False, stoppedUtc=iso(end), stopReason=why, stoppedBy=by)
        today.append(row)
    return running, today


class TradingAgentTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        (self.tmp / "config").mkdir()
        (self.tmp / "config" / "morning-plan.txt").write_text(PLAN, encoding="utf-8")
        self.agent = TradingAgent()

    def check(self, running, today=None, now=None, env=None, extra=None, **session):
        today = running if today is None else today
        api = routes(running, today, **session)
        api.update(extra or {})
        ctx = make_context(self.tmp, api=api, now=now or ist(11, 30), env=env)
        return self.agent.check(ctx)

    def rules(self, findings):
        return sorted(f.rule for f in findings)

    def only(self, findings, rule):
        return [f for f in findings if f.rule == rule]

    def state_file(self):
        return self.tmp / "logs" / "sentinel" / "state-trading.json"


class Desk:
    """The trading agent inside the real engine, with an API and a clock the test moves; counts what is sent."""

    def __init__(self, tmp, env=None):
        self.routes = routes([], [])
        self.moment = [ist(9, 10)]
        self.ctx = make_context(tmp, api=self.routes, env=env)
        self.ctx.clock = lambda: self.moment[0]
        self.store = MemoryIncidentStore()
        self.notes = RecordingNotifier()
        self.engine = SentinelEngine([TradingAgent()], self.store, self.notes, self.ctx, monotonic=clock_ticks())

    def at(self, moment, running, today=None, down=False, extra=None, **session):
        self.routes.clear()
        self.routes.update(routes(running, running if today is None else today, **session))
        self.routes.update(extra or {})
        if down:
            self.routes[RUNNING_PATH] = ConnectionError("connection refused")
        self.moment[0] = moment
        self.engine.run_due()

    def opened(self):
        return [m for m in self.notes.sent if " NEW " in m.splitlines()[0]]

    def resolved(self):
        return [m for m in self.notes.sent if m.startswith("✅ RESOLVED")]


class QuietDayTests(TradingAgentTestCase):
    def test_the_ordinary_day_is_silent(self):
        self.assertEqual([], self.check(full_day()))

    def test_the_close_of_an_ordinary_day_is_silent(self):
        closed = [dict(r, status="Stopped", isActive=False, stoppedUtc=iso(ist(15, 30, 40)),
                       stopReason=CLOSED, stoppedBy="market-hours") for r in full_day()]
        self.assertEqual([], self.check([], closed, now=ist(15, 45), nse=False, mcx=True))

    def test_nothing_is_asked_or_reported_on_a_weekend(self):
        asked = []

        def record(path):
            asked.append(path)
            return []

        ctx = make_context(self.tmp, api={"/api/Strategy/*": record}, now=datetime(2026, 9, 27, 6, 0, tzinfo=timezone.utc))
        self.assertEqual([], self.agent.check(ctx))  # no calendar answer: the weekday rule says Sunday
        self.assertEqual([], asked)

    def test_nothing_is_reported_on_an_exchange_holiday(self):
        self.assertEqual([], self.check([], [], trading=False, nse=False, mcx=False))
        self.assertTrue(self.check([], []))  # the same empty desk on a trading day is news

    def test_an_api_that_does_not_answer_with_nothing_seen_before_reports_nothing(self):
        api = routes(full_day(), full_day())
        api[RUNNING_PATH] = ConnectionError("connection refused")
        ctx = make_context(self.tmp, api=api, now=ist(11, 30))
        self.assertEqual([], self.agent.check(ctx))

    def test_an_answer_that_is_not_a_run_list_reports_nothing(self):
        self.assertEqual([], self.check({"message": "Unauthorized"}, []))

    def test_agent_identity(self):
        self.assertEqual("trading", self.agent.name)
        self.assertEqual(60, self.agent.interval_seconds)

    def test_a_broken_state_file_does_not_stop_the_checks(self):
        rows = without(full_day(), "admin", "Fulcrum", "SENSEX")
        for broken in ('["not", "a", "dict"]', '{"day": "2026-09-24", "open": 5, "done": "x", "groups": [1], '
                       '"missing": ["x"], "last": 3, "history": {"medians": {"fulcrum": "x"}}}',
                       '{"day": "2026-09-24", "groups": {"fp": {"ids": [true, "x"], "size": "big"}}, '
                       '"missing": {"admin": {"keys": 5, "mode": 1}}}', "{not json"):
            self.state_file().parent.mkdir(parents=True, exist_ok=True)
            self.state_file().write_text(broken, encoding="utf-8")
            self.assertEqual(["run-missing"], self.rules(self.check(rows)), broken)


class RunMissingTests(TradingAgentTestCase):
    def test_a_planned_run_that_is_not_running_is_reported_by_its_key(self):
        rows = without(full_day(), "coderforchange", "GhostTangentCrossings", "NIFTY")
        findings = self.check(rows)

        self.assertEqual(["run-missing"], self.rules(findings))
        f = findings[0]
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("trading:run-missing:coderforchange:ghosttangentcrossings:NIFTY", f.fingerprint)
        self.assertIn("GhostTangentCrossings on NIFTY", f.title)
        self.assertIn("coderforchange", f.title)
        self.assertTrue(any("no run of GhostTangentCrossings on NIFTY was started today" in e for e in f.evidence))
        self.assertTrue(any("12 of 13 planned runs" in e for e in f.evidence))
        self.assertIn("logs/market-open-2026-09-24.log", f.suggestion)

    def test_the_names_are_matched_without_regard_to_case(self):
        rows = full_day()
        for r in rows:
            r["userName"] = r["userName"].upper()
            r["strategyName"] = r["strategyName"].lower()
            r["underlying"] = r["underlying"].lower()
        self.assertEqual([], self.check(rows))

    def test_the_fingerprint_is_the_same_from_one_check_to_the_next(self):
        rows = without(full_day(), "admin", "Fulcrum", "SENSEX")
        first = self.check(rows, now=ist(11, 30))
        second = self.check(rows, now=ist(12, 47))
        self.assertEqual([f.fingerprint for f in first], [f.fingerprint for f in second])

    def test_the_first_ten_minutes_after_the_open_are_a_grace_window(self):
        self.assertEqual([], self.check([], [], now=ist(9, 20)))
        self.assertEqual([], self.check([], [], now=ist(9, 24, 59)))
        self.assertEqual(["run-missing", "run-missing"], self.rules(self.check([], [], now=ist(9, 25))))

    def test_the_last_five_minutes_before_the_close_are_left_alone(self):
        rows = without(full_day(), "admin", "GhostTangentCrossings", "NIFTY")
        self.assertEqual(["run-missing"], self.rules(self.check(rows, now=ist(15, 24))))
        self.assertEqual([], self.check(rows, now=ist(15, 26)))

    def test_a_whole_account_missing_is_one_incident_not_thirteen(self):
        rows = [r for r in full_day() if r["userName"] == "admin"]
        findings = self.check(rows)

        self.assertEqual(1, len(findings))
        f = findings[0]
        self.assertEqual("run-missing", f.rule)
        self.assertEqual("trading:run-missing:coderforchange", f.fingerprint)
        self.assertEqual("None of the 13 planned runs for coderforchange is running", f.title)
        self.assertTrue(any("13 of 13 were never started today" in e for e in f.evidence))
        self.assertIn("logs/market-open-2026-09-24.log", f.suggestion)
        self.assertIn("scripts/market-open.sh", f.suggestion)

    def test_two_missing_runs_are_one_incident_for_the_account(self):
        rows = without(without(full_day(), "admin", "Fulcrum", "SENSEX"), "admin", "ChainFlowBuy", "NIFTY")
        findings = self.check(rows)

        self.assertEqual(1, len(findings))
        f = findings[0]
        self.assertEqual("trading:run-missing:admin", f.fingerprint)
        self.assertEqual("2 of 13 planned runs for admin are not running", f.title)
        self.assertTrue(any("Fulcrum SENSEX" in e and "ChainFlowBuy NIFTY" in e for e in f.evidence))
        self.assertTrue(any("11 of 13 planned runs for admin are live" in e for e in f.evidence))

    def test_mcx_lines_are_judged_by_the_mcx_session(self):
        rows = without(full_day(), "admin", "CrudeMomentum", "CRUDEOIL")
        self.assertEqual([], self.check(rows, nse=True, mcx=False))

        findings = self.check(rows, nse=True, mcx=True)
        self.assertEqual(["run-missing"], self.rules(findings))
        self.assertEqual("trading:run-missing:admin:crudemomentum:CRUDEOIL", findings[0].fingerprint)

    def test_nse_lines_are_not_judged_while_the_nse_is_shut(self):
        rows = [r for r in full_day() if r["underlying"] == "CRUDEOIL"]
        self.assertEqual([], self.check(rows, nse=False, mcx=True))
        self.assertEqual(["run-missing", "run-missing"], self.rules(self.check(rows, nse=True, mcx=True)))

    def test_a_run_its_risk_rules_or_a_person_ended_is_not_missing(self):
        for reason, by in (("Target hit: P&L ₹5,120 ≥ ₹5,000", "risk-guard"),
                           ("Stop loss hit: P&L -₹5,120 ≤ -₹5,000", "risk-guard"),
                           ("Trailing stop hit: P&L ₹4,000 fell ₹2,000 from its peak", None),
                           ("Stopped by admin", "admin"),
                           ("Stopped by coderforchange", "coderforchange")):
            rows = full_day()
            ended = [dict(r) for r in rows if r["userName"] == "admin" and r["strategyName"] == "ChainFlowBuy"
                     and r["underlying"] == "BANKNIFTY"][0]
            ended.update(status="Stopped", isActive=False, stoppedUtc=iso(ist(11, 2)), stopReason=reason, stoppedBy=by)
            running = without(rows, "admin", "ChainFlowBuy", "BANKNIFTY")
            today = running + [ended]
            self.assertEqual([], self.check(running, today), reason)

    def test_a_run_listed_as_running_without_a_runner_is_missing(self):
        rows = full_day()
        orphan = [r for r in rows if r["userName"] == "admin" and r["strategyName"] == "SmcStructureBreak"
                  and r["underlying"] == "SENSEX"][0]
        orphan["isActive"] = False
        findings = self.check(rows)

        self.assertEqual(["run-missing"], self.rules(findings))
        f = findings[0]
        self.assertTrue(any(f"run {orphan['runId']} is listed as Running but has no runner process" in e
                            for e in f.evidence))
        # No runner log on disk (the usual case): the runner's last words are in the API's log.
        self.assertIn(f'logs/api.log (search "run {orphan["runId"]} on"', f.suggestion)

    def test_the_evidence_names_the_keys_last_run_today(self):
        # Died before the open, so it is not a death in the session; it is still a missing run.
        gone = run(250, "admin", "Fulcrum", "NIFTY", status="Stopped", started=ist(9, 5), stopped=ist(9, 6),
                   reason="Runner exited (code 2): feed not configured", by="runner")
        running = without(full_day(), "admin", "Fulcrum", "NIFTY")
        (self.tmp / "logs" / "engine").mkdir(parents=True)
        (self.tmp / "logs" / "engine" / "runner-250-48213.log").write_text("x", encoding="utf-8")

        findings = self.check(running, running + [gone])

        self.assertEqual(["run-missing"], self.rules(findings))
        f = findings[0]
        self.assertTrue(any("last run today: 250, Stopped" in e for e in f.evidence))
        self.assertTrue(any("Runner exited (code 2): feed not configured" in e for e in f.evidence))
        self.assertIn("logs/engine/runner-250-48213.log", f.suggestion)

    def test_without_a_plan_file_nothing_is_called_missing(self):
        self.assertEqual(["run-missing", "run-missing"], self.rules(self.check([], [])))
        (self.tmp / "config" / "morning-plan.txt").unlink()
        self.assertEqual([], self.check([], []))

    def test_a_run_started_between_the_two_reads_is_not_missing(self):
        late = run(990, "admin", "Fulcrum", "SENSEX", started=ist(11, 29, 59))
        running = without(full_day(), "admin", "Fulcrum", "SENSEX")  # the first GET, just before it started
        self.assertEqual([], self.check(running, running + [late], now=ist(11, 30)))


class MorningDeployTests(TradingAgentTestCase):
    """The job is still starting runs: what is not started yet is not missing yet."""

    def deploy(self, begin, skip=()):
        rows, t = [], begin
        for i, (user, (strategy, underlying)) in enumerate((u, k) for u in ("admin", "coderforchange") for k in PLAN_KEYS):
            if (user, strategy, underlying) not in skip:
                rows.append(run(400 + i, user, strategy, underlying, started=t))
            t += timedelta(seconds=2.5)
        return rows

    def test_a_deploy_still_running_at_0925_is_left_to_finish(self):
        rows = self.deploy(ist(9, 24))  # the last CRUDEOIL run starts at 09:25:02
        started = [r for r in rows if r["startedUtc"] <= iso(ist(9, 25))]
        self.assertEqual([], self.check(started, now=ist(9, 25)))
        self.assertEqual([], self.check(rows, now=ist(9, 26)))

    def test_a_run_the_deploy_never_started_is_reported_once_the_deploy_is_quiet(self):
        rows = self.deploy(ist(9, 24), skip={("coderforchange", "CrudeMomentum", "CRUDEOIL")})
        self.assertEqual([], self.check(rows, now=ist(9, 26)))
        findings = self.check(rows, now=ist(9, 28))
        self.assertEqual(["trading:run-missing:coderforchange:crudemomentum:CRUDEOIL"],
                         [f.fingerprint for f in findings])

    def test_a_recovery_after_a_dead_morning_is_one_message_each_way(self):
        """10 Sep: nothing started; the job is run again at 09:40 and starts 26 runs over a minute."""
        desk = Desk(self.tmp)
        for minute in range(25, 40):
            desk.at(ist(9, minute), [])
        self.assertEqual(2, len(desk.opened()))
        self.assertEqual({"None of the 13 planned runs for admin is running",
                          "None of the 13 planned runs for coderforchange is running"},
                         {m.splitlines()[0].split("] ", 1)[1] for m in desk.opened()})

        rows = self.deploy(ist(9, 40))
        for seconds in range(0, 300, 20):
            moment = ist(9, 40) + timedelta(seconds=seconds)
            desk.at(moment, [r for r in rows if r["startedUtc"] <= iso(moment)])

        self.assertEqual(2, len(desk.opened()))  # no per-run incident opened while the runs came back
        self.assertEqual(2, len(desk.resolved()))

    def test_a_partial_recovery_keeps_one_incident_per_account(self):
        """23 Sep: nothing at the open; three Ghost runs started by hand at 10:38."""
        desk = Desk(self.tmp)
        ghosts = [run(202 + i, "admin", "GhostTangentCrossings", u, started=ist(10, 38, 30 + 2 * i))
                  for i, u in enumerate(("BANKNIFTY", "NIFTY", "SENSEX"))]
        moment = ist(9, 25)
        while moment <= ist(12, 0):
            desk.at(moment, [g for g in ghosts if g["startedUtc"] <= iso(moment)])
            moment += timedelta(minutes=1)

        self.assertEqual(2, len(desk.opened()))
        self.assertEqual([], desk.resolved())
        admin = [r for r in desk.store.rows() if r["fingerprint"] == "trading:run-missing:admin"][0]
        self.assertEqual("10 of 13 planned runs for admin are not running", admin["title"])


class RunStoppedEarlyTests(TradingAgentTestCase):
    def check_on_a_full_day(self, today, running=(), **kwargs):
        """The ordinary 26 runs running, plus the runs under test."""
        base = full_day()
        return self.check(base + list(running), base + list(today), **kwargs)

    def test_a_restarted_run_is_finished_with_and_never_reported_again(self):
        rows = full_day()
        gone = [r for r in rows if r["userName"] == "admin" and r["strategyName"] == "GhostTangentCrossings"
                and r["underlying"] == "NIFTY"][0]
        gone.update(status="Stopped", isActive=False, stoppedUtc=iso(ist(11, 0)), stopReason="Runner exited (code 1): "
                    "KeyError: 'ltp'", stoppedBy="runner")
        running = [r for r in rows if r is not gone]

        first = self.check(running, running + [gone], now=ist(11, 1))
        self.assertEqual(["run-stopped-early"], self.rules(first))
        self.assertEqual(f"trading:run-stopped-early:{gone['runId']}", first[0].fingerprint)

        restarted = run(900, "admin", "GhostTangentCrossings", "NIFTY", started=ist(11, 5))
        self.assertEqual([], self.check(running + [restarted], running + [gone, restarted], now=ist(11, 6)))

        # The restarted run dies too: that is a new run and a new incident; the first stays finished.
        restarted_dead = dict(restarted, status="Stopped", isActive=False, stoppedUtc=iso(ist(11, 20)),
                              stopReason="Runner exited (code 1): KeyError: 'ltp'", stoppedBy="runner")
        again = self.check(running, running + [gone, restarted_dead], now=ist(11, 21))
        self.assertEqual(["trading:run-stopped-early:900"], [f.fingerprint for f in again])

    def test_the_memory_of_a_reported_run_survives_a_sentinel_restart(self):
        rows = [run(700, "admin", "IronButterfly", "BANKNIFTY", status="Stopped", stopped=ist(10, 0),
                    reason="Runner exited (code 1): boom", by="runner")]
        self.assertEqual(1, len(self.check_on_a_full_day(rows, now=ist(10, 1))))
        newer = run(701, "admin", "IronButterfly", "BANKNIFTY", started=ist(10, 3))
        self.assertEqual([], self.check_on_a_full_day(rows + [newer], [newer], now=ist(10, 4)))
        # Each check above built a fresh context and a fresh agent: only the state file on disk remembers.
        self.agent = TradingAgent()
        self.assertEqual([], self.check_on_a_full_day(rows, now=ist(10, 30)))

    def test_ordinary_endings_are_not_deaths(self):
        endings = [(CLOSED, "market-hours"), ("MCX closed", "market-hours"),
                   ("Target hit: P&L ₹5,120 ≥ ₹5,000", "risk-guard"), ("Stop loss hit: -₹5,120", "risk-guard"),
                   ("Trailing stop hit: P&L ₹4,000 fell ₹2,000", "risk-guard"),
                   ("Trailing stop hit: P&L ₹4,000 fell ₹2,000", None),  # the stop signal's "by" was not saved
                   ("Stopped by admin", "admin"), ("Stopped by admin (runner process was not found)", "admin"),
                   ("Stopped by coderforchange", "coderforchange")]
        today = [run(800 + i, "admin", "IronButterfly", f"U{i}", status="Stopped", stopped=ist(11, i),
                     reason=reason, by=by) for i, (reason, by) in enumerate(endings)]
        self.assertEqual([], self.check_on_a_full_day(today, now=ist(11, 30)))

    def test_a_runner_that_never_started_points_at_the_api_log(self):
        today = [run(201, "admin", "BearPutSpread", "NIFTY", status="Failed", started=ist(9, 24), stopped=ist(9, 24),
                     reason="Runner failed to start.")]
        findings = self.check_on_a_full_day(today, now=ist(9, 30))
        self.assertEqual(["run-stopped-early"], self.rules(findings))
        self.assertIn("api.log", findings[0].suggestion)
        self.assertIn("failed to start at 09:24", findings[0].title)

    def test_a_run_lost_across_an_api_restart_points_at_the_desk_log(self):
        today = [run(74, "admin", "IronButterfly", "SENSEX", status="Stopped", stopped=ist(12, 40),
                     reason="API restarted; runner not found", by="api")]
        findings = self.check_on_a_full_day(today, now=ist(12, 41))
        self.assertEqual(["run-stopped-early"], self.rules(findings))
        self.assertIn("desk.log", findings[0].suggestion)

    def test_a_run_that_ended_with_no_reason_at_all_is_a_death(self):
        today = [run(75, "admin", "IronButterfly", "SENSEX", status="Stopped", stopped=ist(12, 40))]
        findings = self.check_on_a_full_day(today, now=ist(12, 41))
        self.assertEqual(["run-stopped-early"], self.rules(findings))
        self.assertIn("no reason was recorded", findings[0].summary)
        self.assertIn('logs/api.log (search "run 75 on"', findings[0].suggestion)

    def test_a_death_outside_market_hours_is_not_reported(self):
        before_open = run(60, "admin", "IronButterfly", "NIFTY", status="Stopped", started=ist(8, 50),
                          stopped=ist(8, 51), reason="Runner exited (code 1): boom", by="runner")
        self.assertEqual([], self.check_on_a_full_day([before_open], now=ist(11, 0)))
        in_session = dict(before_open, stoppedUtc=iso(ist(10, 51)))
        self.assertEqual(["run-stopped-early"], self.rules(self.check_on_a_full_day([in_session], now=ist(11, 0))))

    def test_nothing_is_reported_once_the_desk_has_stopped_every_run(self):
        today = [run(61, "admin", "IronButterfly", "NIFTY", status="Stopped", stopped=ist(15, 10),
                     reason="Runner exited (code 1): boom", by="runner")]
        self.assertEqual(["run-stopped-early"], self.rules(self.check_on_a_full_day(today, now=ist(15, 11))))
        self.assertEqual([], self.check_on_a_full_day(today, now=ist(15, 40), nse=False))

    def test_an_evening_mcx_run_is_watched_until_the_mcx_close(self):
        today = [run(63, "admin", "CrudeBreakout", "CRUDEOIL", status="Stopped", started=ist(20, 53),
                     stopped=ist(21, 10), reason="Runner exited (code 1): boom", by="runner")]
        findings = self.check_on_a_full_day(today, now=ist(21, 11), nse=False, mcx=True)
        self.assertEqual(["run-stopped-early"], self.rules(findings))
        self.assertIn("stopped unexpectedly at 21:10", findings[0].title)
        self.assertEqual([], self.check_on_a_full_day(today, now=ist(23, 35), nse=False, mcx=False))

    def test_alerter_runs_and_the_manual_book_are_not_strategy_runs(self):
        today = [run(124, "admin", "LogicEngine", "BANKNIFTY", status="Stopped", stopped=ist(11, 0),
                     reason="API stop requested", by="api", role="alerts"),
                 run(98, "admin", "Manual", "MANUAL", status="Stopped", stopped=ist(11, 0))]
        self.assertEqual([], self.check_on_a_full_day(today, now=ist(11, 30)))
        today[0]["role"] = None
        self.assertEqual(["run-stopped-early"], self.rules(self.check_on_a_full_day(today, now=ist(11, 30))))

    def test_no_secret_in_a_stop_reason_reaches_the_incident(self):
        secrets = {
            "abc123secretXYZ987": "HTTPError 401 for url: https://api.broker.example/v3/funds?access_token=abc123secretXYZ987",
            "hunter2hunter2": "login failed password=hunter2hunter2",
            "S3cr3tPg": "psycopg2.OperationalError: postgresql://algo:S3cr3tPg@db:5432/trading refused",
            "Pg0nlyP4ss": "Npgsql: Host=db;Username=algo;Password=Pg0nlyP4ss;Database=trading",
            "AAHfakeTelegramTokenPart0123456789": "ConnectionError: https://api.telegram.org/"
                                                 "bot123456789:AAHfakeTelegramTokenPart0123456789/sendMessage",
            "q9Zk2Lm8Xw": "KeyError while reading secretKey=q9Zk2Lm8Xw",
        }
        for i, (secret, detail) in enumerate(secrets.items()):
            today = [run(62 + i, "admin", "IronButterfly", f"U{i}", status="Stopped", stopped=ist(11, 0),
                         reason=f"Runner exited (code 1): {detail}", by="runner")]
            findings = self.check_on_a_full_day(today, now=ist(11, 1 + i))
            self.assertEqual(1, len(findings), detail)
            text = " ".join([findings[0].title, findings[0].summary, findings[0].suggestion, *findings[0].evidence])
            self.assertNotIn(secret, text, detail)
        self.assertIn("https://api.broker.example/v3/funds", " ".join(self.check_on_a_full_day(
            [run(61, "admin", "IronButterfly", "NIFTY", status="Stopped", stopped=ist(11, 0),
                 reason="Runner exited (code 1): " + secrets["abc123secretXYZ987"], by="runner")],
            now=ist(11, 30))[0].evidence))


class MassDeathTests(TradingAgentTestCase):
    """Many runs of one account dying of one cause are one incident, not one per run."""

    def limiter_morning(self, at=ist(9, 18, 30)):
        """24 Sep: every coderforchange runner died at 09:18 signing in (429)."""
        rows = full_day()
        running = [r for r in rows if r["userName"] == "admin"]
        return running, running + [dead(r, at) for r in rows if r["userName"] == "coderforchange"]

    def test_the_24_sep_limiter_deaths_are_one_incident_per_account(self):
        running, today = as_of(sep24(), ist(9, 19))
        findings = self.check(running, today, now=ist(9, 19))

        self.assertEqual(["run-stopped-early"] * 2, self.rules(findings))
        by_fp = {f.fingerprint: f for f in findings}
        self.assertEqual({"trading:run-stopped-early:admin:429-login", "trading:run-stopped-early:coderforchange:429-login"},
                         set(by_fp))
        f = by_fp["trading:run-stopped-early:coderforchange:429-login"]
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("13 coderforchange runs died at 09:18: sign-in limiter (429)", f.title)
        self.assertEqual("3 admin runs died at 09:18: sign-in limiter (429)",
                         by_fp["trading:run-stopped-early:admin:429-login"].title)
        self.assertLessEqual(len(f.evidence), 6)
        self.assertTrue(any("429 Client Error" in e and "stopped by: runner" in e for e in f.evidence))
        listed = " ".join(f.evidence)
        for run_id in range(218, 231):
            self.assertIn(str(run_id), listed)
        self.assertIn("GhostTangentCrossings: 218 BANKNIFTY, 219 NIFTY, 220 SENSEX", f.evidence)
        self.assertIn("24 Sep", f.suggestion)
        self.assertIn("scripts/market-open.sh", f.suggestion)
        self.assertNotIn("Live runner", f.suggestion)  # every one of them is in the plan

    def test_a_planned_run_that_died_is_not_also_missing(self):
        running, today = self.limiter_morning()
        early = self.check(running, today, now=ist(9, 19))
        later = self.check(running, today, now=ist(9, 40))

        self.assertEqual(["run-stopped-early"], self.rules(later))  # no run-missing on top
        self.assertEqual([f.fingerprint for f in early], [f.fingerprint for f in later])

    def test_the_group_holds_while_its_runs_come_back_one_by_one(self):
        running, today = self.limiter_morning()
        self.check(running, today, now=ist(9, 19))
        restarted = [run(600 + i, "coderforchange", s, u, started=ist(9, 45, i))
                     for i, (s, u) in enumerate(PLAN_KEYS[:12])]
        findings = self.check(running + restarted, today + restarted, now=ist(9, 46))

        self.assertEqual(["trading:run-stopped-early:coderforchange:429-login"], [f.fingerprint for f in findings])
        self.assertEqual("13 coderforchange runs died at 09:18: sign-in limiter (429)", findings[0].title)
        self.assertIn("1 of them is still not running", findings[0].summary)

        last = run(612, "coderforchange", "CrudeMomentum", "CRUDEOIL", started=ist(9, 47))
        everything = running + restarted + [last]
        self.assertEqual([], self.check(everything, today + restarted + [last], now=ist(9, 48)))
        # The dead runs are finished with: seen again without their replacements, they are not news again.
        self.assertEqual([], self.only(self.check(running, today, now=ist(9, 50)), "run-stopped-early"))

    def test_a_death_just_after_the_group_joins_it_quietly(self):
        running, today = self.limiter_morning()
        self.check(running, today, now=ist(9, 19))
        straggler = dead(run(640, "coderforchange", "IronButterfly", "NIFTY", started=ist(9, 20)), ist(9, 21))
        findings = self.check(running, today + [straggler], now=ist(9, 23))
        self.assertEqual(["trading:run-stopped-early:coderforchange:429-login"], [f.fingerprint for f in findings])
        self.assertTrue(findings[0].title.startswith("14 coderforchange runs died"))
        self.assertIn("Run 640 — not in the morning plan — must be started again from the Live runner",
                      findings[0].suggestion)

    def test_a_pair_seen_in_the_middle_of_a_burst_waits_for_the_rest(self):
        desk = Desk(self.tmp)
        events = sep24()
        for moment in (ist(9, 18, 10), ist(9, 19), ist(9, 20), ist(9, 21)):
            desk.at(moment, *as_of(events, moment))
        self.assertEqual(2, len(desk.opened()))  # one per account, not 16
        self.assertEqual([], desk.resolved())

    def test_two_deaths_far_from_any_group_are_a_run_each(self):
        rows = full_day()
        pair = [dead(r, ist(11, 0, 10 * i), reason="Runner exited (code 1): KeyError: 'ltp'")
                for i, r in enumerate(r for r in rows if r["userName"] == "admin" and r["strategyName"] == "Fulcrum")][:2]
        pair_ids = {p["runId"] for p in pair}
        running = [r for r in rows if r["runId"] not in pair_ids]
        self.assertEqual([], self.check(running, running + pair, now=ist(11, 0, 30)))  # a burst may be starting
        findings = self.check(running, running + pair, now=ist(11, 2))
        self.assertEqual(sorted(f"trading:run-stopped-early:{i}" for i in pair_ids),
                         sorted(f.fingerprint for f in findings))

    def test_an_api_restart_that_loses_an_account_is_one_incident(self):
        rows = full_day()
        running = [r for r in rows if r["userName"] == "coderforchange"]
        lost = [dead(r, ist(12, 40, 2), reason="API restarted; runner not found", by="api")
                for r in rows if r["userName"] == "admin"]
        findings = self.check(running, running + lost, now=ist(12, 41))
        self.assertEqual(["trading:run-stopped-early:admin:api-restarted"], [f.fingerprint for f in findings])
        self.assertEqual("13 admin runs died at 12:40: lost in an API restart", findings[0].title)
        self.assertIn("desk.log", findings[0].suggestion)
        self.assertIn("scripts/market-open.sh", findings[0].suggestion)

    def test_the_whole_24_sep_session_through_the_engine(self):
        """Before: 34 messages (16 NEW + 16 RESOLVED for one cause, and the pair at 11:20). Now: 8."""
        desk = Desk(self.tmp)
        events = sep24()
        moment = ist(9, 15)
        while moment <= ist(15, 45):
            session = {"nse": ist(9, 15) <= moment < ist(15, 30)}
            desk.at(moment, *as_of(events, moment), **session)
            moment += timedelta(minutes=1)

        titles = [m.splitlines()[0] for m in desk.opened()]
        self.assertEqual(4, len(titles), titles)
        self.assertTrue(any("13 coderforchange runs died at 09:18: sign-in limiter (429)" in t for t in titles))
        self.assertTrue(any("3 admin runs died at 09:18: sign-in limiter (429)" in t for t in titles))
        self.assertEqual(2, sum("Fulcrum on" in t and "stopped unexpectedly at 11:20" in t for t in titles))
        self.assertEqual(4, len(desk.resolved()))
        self.assertEqual([], [r for r in desk.store.rows() if r["status"] != "resolved"])


class ApiOutageTests(TradingAgentTestCase):
    """The API not answering is the health agent's news; it must not close and reopen trading incidents."""

    def test_an_outage_neither_resolves_nor_reopens_what_is_open(self):
        desk = Desk(self.tmp)
        running, today = MassDeathTests.limiter_morning(self)
        desk.at(ist(9, 19), running, today)
        self.assertEqual(1, len(desk.notes.sent))

        for minute in (20, 21, 22, 23):
            desk.at(ist(9, minute), running, today, down=True)
        desk.at(ist(9, 24), running, today)
        desk.at(ist(9, 25), running, today)

        self.assertEqual(1, len(desk.notes.sent), desk.notes.sent)
        self.assertEqual(1, len(desk.store.rows()))

    def test_what_is_carried_is_the_last_answer(self):
        running, today = MassDeathTests.limiter_morning(self)
        seen = self.check(running, today, now=ist(9, 30))
        api = routes(running, today)
        api[today_path(DAY)] = {"message": "Service Unavailable"}  # not a run list either
        carried = self.agent.check(make_context(self.tmp, api=api, now=ist(9, 40)))
        self.assertEqual([(f.fingerprint, f.title, f.severity, f.evidence) for f in seen],
                         [(f.fingerprint, f.title, f.severity, f.evidence) for f in carried])

    def test_a_long_outage_stops_carrying(self):
        running, today = MassDeathTests.limiter_morning(self)
        self.check(running, today, now=ist(9, 30))
        api = routes(running, today)
        api[RUNNING_PATH] = ConnectionError("connection refused")
        self.assertEqual(1, len(self.agent.check(make_context(self.tmp, api=api, now=ist(9, 45)))))
        self.assertEqual([], self.agent.check(make_context(self.tmp, api=api, now=ist(9, 46))))

    def test_nothing_is_carried_into_the_next_day(self):
        running, today = MassDeathTests.limiter_morning(self)
        self.check(running, today, now=ist(15, 20))
        api = routes(running, today, day="2026-09-25")
        api[RUNNING_PATH] = ConnectionError("connection refused")
        self.assertEqual([], self.agent.check(make_context(self.tmp, api=api, now=ist(9, 30, day=25))))


def history(*days, fulcrum=(270, 344, 388), ghost=(7, 8, 6)):
    """Earlier days' runs as the history GET answers them: both accounts, every Fulcrum and Ghost book."""
    rows, rid = [], 100
    for day in days:
        for user in ("admin", "coderforchange"):
            for strategy, counts in (("Fulcrum", fulcrum), ("GhostTangentCrossings", ghost)):
                for underlying, trades in zip(("BANKNIFTY", "NIFTY", "SENSEX"), counts):
                    rows.append(run(rid, user, strategy, underlying, status="Stopped", started=ist(9, 18, day=day),
                                    stopped=ist(15, 30, day=day), reason=CLOSED, by="market-hours", trades=trades))
                    rid += 1
    return rows


HISTORY_PATH = history_path("2026-09-15", "2026-09-23")


class OvertradingTests(TradingAgentTestCase):
    def fulcrum_day(self, counts=(270, 344, 388)):
        """25 Sep: Fulcrum's three books in each account at 270, 344 and 388 trades."""
        rows = full_day()
        for r in rows:
            if r["strategyName"] == "Fulcrum":
                r["trades"] = dict(zip(("BANKNIFTY", "NIFTY", "SENSEX"), counts))[r["underlying"]]
        return rows

    def test_a_churning_strategy_is_one_incident_across_its_books(self):
        first = self.check(self.fulcrum_day())
        second = self.check(self.fulcrum_day((290, 360, 402)), now=ist(12, 30))

        self.assertEqual(["overtrading"], self.rules(first))
        f = first[0]
        self.assertEqual(Severity.MEDIUM, f.severity)
        self.assertEqual("trading:overtrading:fulcrum:2026-09-24", f.fingerprint)
        self.assertIn("388 trades", f.title)
        self.assertIn("6 books past 150", f.title)
        self.assertEqual("limit 150 trades a day per book (the default)", f.evidence[-1])
        self.assertIn("SENTINEL_MAX_TRADES_FULCRUM", f.suggestion)
        self.assertEqual(f.fingerprint, second[0].fingerprint)
        self.assertIn("402 trades", second[0].title)

    def test_at_the_limit_is_not_over_it(self):
        self.assertEqual([], self.check(self.fulcrum_day((150, 150, 150))))
        self.assertEqual(["overtrading"], self.rules(self.check(self.fulcrum_day((150, 150, 151)))))

    def test_a_strategy_is_judged_against_its_own_recent_days(self):
        asked = []

        def answer(path):
            asked.append(path)
            return history(21, 22, 23)

        for minute in (30, 31, 32):
            self.assertEqual([], self.check(self.fulcrum_day(), now=ist(11, minute), extra={HISTORY_PATH: answer}))
        self.assertEqual([HISTORY_PATH], asked)  # once a day, not once a check

    def test_a_strategy_far_past_its_own_days_is_reported_with_where_the_line_came_from(self):
        rows = full_day()
        rows[0]["trades"] = 60  # admin Ghost BANKNIFTY; its ordinary day is 6-8
        findings = self.check(rows, extra={HISTORY_PATH: history(21, 22, 23)})

        self.assertEqual(["trading:overtrading:ghosttangentcrossings:2026-09-24"], [f.fingerprint for f in findings])
        self.assertEqual("limit 30 trades a day per book (3× its median of 7 a day over its last 3 trading days)",
                         findings[0].evidence[-1])

    def test_the_history_is_not_asked_for_when_nothing_is_busy(self):
        def fail(path):
            raise AssertionError(f"asked {path}")

        rows = full_day()
        rows[0]["trades"] = 25
        self.assertEqual([], self.check(rows, extra={HISTORY_PATH: fail}))

    def test_a_strategy_can_carry_its_own_limit(self):
        self.assertEqual([], self.check(self.fulcrum_day(), env={"SENTINEL_MAX_TRADES_FULCRUM": "500"}))
        self.assertEqual([], self.check(self.fulcrum_day(), env={"SENTINEL_MAX_TRADES_FULCRUM": "0"}))
        self.assertEqual([], self.check(self.fulcrum_day(), env={"SENTINEL_MAX_TRADES": "0"}))

        ghost = self.check(full_day(), env={"SENTINEL_MAX_TRADES_GHOSTTANGENTCROSSINGS": "5"})
        self.assertEqual(["overtrading"], self.rules(ghost))  # 7 trades on each of 6 Ghost books: one incident
        self.assertIn("6 books past 5", ghost[0].title)
        self.assertEqual("limit 5 trades a day per book (SENTINEL_MAX_TRADES_GHOSTTANGENTCROSSINGS)", ghost[0].evidence[-1])

        fallback = self.check(self.fulcrum_day(), env={"SENTINEL_MAX_TRADES": "300"})
        self.assertEqual("limit 300 trades a day per book (SENTINEL_MAX_TRADES)", fallback[0].evidence[-1])

    def test_a_restart_does_not_reset_the_days_count(self):
        rows = self.fulcrum_day((100, 100, 100))
        earlier = run(960, "admin", "Fulcrum", "NIFTY", status="Stopped", started=ist(9, 18), stopped=ist(10, 0),
                      reason="Stopped by admin", by="admin", trades=90)
        findings = self.check(rows, rows + [earlier])
        self.assertEqual(["overtrading"], self.rules(findings))
        self.assertIn("190 trades", findings[0].title)

    def test_it_closes_at_the_close_not_at_midnight(self):
        desk = Desk(self.tmp)
        closed = [dict(r, status="Stopped", isActive=False, stoppedUtc=iso(ist(15, 30, 30)), stopReason=CLOSED,
                       stoppedBy="market-hours") for r in self.fulcrum_day()]
        desk.at(ist(15, 28), self.fulcrum_day())
        desk.at(ist(15, 31), [], closed, nse=False)
        desk.at(ist(15, 32), [], closed, nse=False)

        self.assertEqual(1, len(desk.opened()))
        self.assertEqual(1, len(desk.resolved()))


class AccountLossTests(TradingAgentTestCase):
    def losing_day(self, booked_each, open_on_first=0.0):
        rows = full_day()
        admin = [r for r in rows if r["userName"] == "admin"]
        for r in admin:
            r["netPnl"] = r["realizedPnl"] = booked_each
        admin[0]["unrealizedPnl"] = open_on_first
        return rows

    def test_an_account_past_the_day_loss_line_is_reported(self):
        findings = self.check(self.losing_day(-3500.0, open_on_first=-8000.0))  # 13 x -3,500 - 8,000 = -53,500

        self.assertEqual(["account-loss"], self.rules(findings))
        f = findings[0]
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("trading:account-loss:admin:2026-09-24", f.fingerprint)
        self.assertIn("₹53,500", f.title)
        self.assertTrue(any("-₹45,500 booked" in e and "-₹8,000 open" in e for e in f.evidence))
        self.assertEqual("line: -₹50,000 (the default)", f.evidence[-1])

    def test_a_loss_inside_the_line_is_quiet(self):
        self.assertEqual([], self.check(self.losing_day(-3000.0)))  # -39,000

    def test_the_line_comes_from_the_environment(self):
        findings = self.check(self.losing_day(-3000.0), env={"SENTINEL_MAX_DAY_LOSS": "30000"})
        self.assertEqual(["account-loss"], self.rules(findings))
        self.assertEqual("line: -₹30,000 (SENTINEL_MAX_DAY_LOSS)", findings[0].evidence[-1])

    def test_the_fingerprint_does_not_move_with_the_loss(self):
        a = self.check(self.losing_day(-4000.0))
        b = self.check(self.losing_day(-6000.0), now=ist(13, 0))
        self.assertEqual(a[0].fingerprint, b[0].fingerprint)

    def test_a_booked_loss_after_the_close_is_not_an_open_incident_until_midnight(self):
        closed = [dict(r, status="Stopped", isActive=False, stoppedUtc=iso(ist(15, 30, 30)), stopReason=CLOSED,
                       stoppedBy="market-hours") for r in self.losing_day(-4000.0)]
        self.assertEqual(["account-loss"], self.rules(self.check(self.losing_day(-4000.0), now=ist(15, 29))))
        self.assertEqual([], self.check([], closed, now=ist(15, 31), nse=False, mcx=True))


if __name__ == "__main__":
    unittest.main()
