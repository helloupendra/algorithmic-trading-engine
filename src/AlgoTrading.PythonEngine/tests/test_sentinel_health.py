import _bootstrap  # noqa: F401

import json
import tempfile
import unittest
from datetime import datetime, time, timedelta, timezone
from pathlib import Path
from unittest import mock

import sentinel.agents.health as health
from sentinel.agents.health import HealthAgent
from sentinel.agents.trading import RUNNING_PATH
from sentinel.model import Severity
from _sentinel_fakes import make_context

GB = 1024 ** 3
NOW = datetime(2026, 9, 24, 6, 0, tzinfo=timezone.utc)   # Thursday 11:30 IST
LOCAL_HEALTH = "http://localhost:5025/health"
PUBLIC_HEALTH = "https://openfno.com/health"


def feeds(running=("dhan",)):
    rows = []
    for key in ("fyers", "truedata", "dhan"):
        on = key in running
        rows.append({"key": key, "displayName": key.title(), "isRunning": on, "managed": on,
                     "processId": 4242 if on else None, "source": "managed" if on else "none"})
    return rows


def _key(entry_id):
    ms, seq = entry_id.split("-")
    return int(ms), int(seq)


class FakeStream:
    """market:ticks with just enough of redis-py's XREVRANGE."""

    def __init__(self, error=None):
        self.entries = []
        self.error = error
        self.calls = 0

    def add(self, at, exchange, symbol=None, source="dhan", replay=False, received=True):
        ms = int(at.timestamp() * 1000)
        seq = sum(1 for e in self.entries if _key(e[0])[0] == ms)
        symbol = symbol or f"{exchange}:TEST"
        payload = {"exchange": exchange, "symbol": symbol, "lastTradedPrice": 100.0,
                   "sourceKey": source, "isReplay": replay}
        if received:
            payload["receivedUtc"] = at.isoformat()
        self.entries.append((f"{ms}-{seq}", {"payload": json.dumps(payload), "symbol": symbol,
                                             "exchange": exchange, "dataType": "symbolUpdate"}))
        self.entries.sort(key=lambda e: _key(e[0]))
        return self

    def xrevrange(self, name, max="+", min="-", count=None):
        self.calls += 1
        if self.error is not None:
            raise self.error
        assert name == "market:ticks"
        upper = None if max == "+" else _key(max)
        out = [e for e in reversed(self.entries) if upper is None or _key(e[0]) <= upper]
        return out[:count] if count else out


def fresh_stream(at=NOW):
    return FakeStream().add(at - timedelta(seconds=3), "NSE", "NSE:NIFTY26SEP25000CE") \
        .add(at - timedelta(seconds=2), "MCX", "MCX:CRUDEOIL26OCTFUT")


DOCKER_OK = "algotrading_db\tUp 3 days\nalgotrading_redis\tUp 3 days\nalgotrading_grafana\tUp 3 days\n"
DESK = "/bin/bash /home/ubuntu/algorithmic-trading-engine/scripts/desk.sh --daemon"
PS_SID = ["ps", "-eo", "pid=,ppid=,sid=,etime=,args="]
PS_PLAIN = ["ps", "-eo", "pid=,ppid=,etime=,args="]
# Linux ps rows: pid, ppid, sid, etime, args. The real loop is its own session leader (setsid).
PS_ONE_DESK = ("    1     0     1 10-00:00:00 /sbin/init\n"
               f"32212     1 32212 3-02:11:05 {DESK}\n"
               "  631     1   631 10-00:00:00 /usr/bin/cloudflared --no-autoupdate tunnel run\n")
# api_start's `( cd … && nohup dotnet run … & )` run by the desk itself: a bash with
# desk.sh's argv, parented to pid 1, in the desk's session, parent of `dotnet run`.
PS_API_LAUNCHER = (f"2096600     1 32212 1-02:00:00 {DESK}\n"
                   "2096601 2096600 32212 1-02:00:00 dotnet run --project src/AlgoTrading.Api --no-launch-profile\n"
                   "2096700 2096601 32212 1-01:59:53 "
                   "/home/ubuntu/algorithmic-trading-engine/src/AlgoTrading.Api/bin/Debug/net10.0/AlgoTrading.Api\n")
PIDFILE = "/home/ubuntu/.local/state/algotrading/desk.pid"


def without_sid(table):
    """The same rows as macOS ps prints them: no session column."""
    rows = []
    for line in table.splitlines():
        pid, ppid, _sid, rest = line.split(None, 3)
        rows.append(f"{pid} {ppid} {rest}")
    return "\n".join(rows) + "\n"


class HealthCase(unittest.TestCase):
    """A context with an API, a stream, docker and ps that all look like an ordinary morning."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.session = {"nse": True, "mcx": True, "trading_day": True, "holiday": None}
        self.api = {
            "/api/Feeds": feeds(),
            RUNNING_PATH: [],
            "/api/MarketSession/check?exchange=NSE*": lambda _: self._market("nse"),
            "/api/MarketSession/check?exchange=MCX*": lambda _: self._market("mcx"),
        }
        self.http = {}
        self.files = {}
        self.disk = {"/": (96 * GB, 60 * GB)}
        self.cpus = 2
        self.docker = (0, DOCKER_OK)
        self.ps = (0, PS_ONE_DESK)       # what Linux ps answers with the session column
        self.ps_plain = None             # the answer without it; None: derived from self.ps
        self.run_calls = []
        self.http_calls = []
        self.stream = fresh_stream()
        self.env = {}
        self.ctx = None

    def _market(self, which):
        is_open = self.session[which]
        if which == "mcx":
            return {"isTradingDay": True, "isMarketOpen": is_open, "isHoliday": False, "holidayName": None}
        trading = self.session["trading_day"]
        return {"isTradingDay": trading, "isMarketOpen": is_open, "isHoliday": not trading,
                "holidayName": self.session["holiday"]}

    def _run(self, args, timeout=20.0, cwd=None):
        self.run_calls.append(list(args))
        if args[0] == "docker":
            return self.docker
        if list(args) == PS_SID:
            return self.ps
        if list(args) == PS_PLAIN:
            if self.ps_plain is not None:
                return self.ps_plain
            rc, out = self.ps
            return rc, (without_sid(out) if rc == 0 and out.strip() else out)
        if args[0] == "ps":
            return 0, "  900 812345 python3\n  901 402000 dotnet\n"
        return 127, ""

    def _http_status(self, url, timeout):
        self.http_calls.append(url)
        value = self.http.get(url, 200)
        if isinstance(value, Exception):
            raise value
        return value

    def agent(self):
        return HealthAgent(http_status=self._http_status, read_text=lambda p: self.files.get(p),
                           disk_usage=lambda p: self.disk.get(p), cpu_count=lambda: self.cpus,
                           device_of=lambda p: 1, read_tail=self._tail)

    def _tail(self, path):
        # Logs are keyed by name: the repository root is a fresh temporary directory.
        return self.files.get(Path(path).name) if Path(path).parent == self.tmp / "logs" else None

    def check(self, at=NOW, agent=None):
        """One check at ``at``. The context (and so the agent's state) lives across calls."""
        if self.ctx is None:
            self.ctx = make_context(self.tmp, api=self.api, now=at, redis_obj=self.stream, env=self.env)
            self.ctx.run = self._run
        self.ctx.clock = lambda: at
        self.ctx.fresh_cycle()
        self._agent = agent or getattr(self, "_agent", None) or self.agent()
        return self._agent.check(self.ctx)

    def rules(self, findings):
        return sorted(f.rule for f in findings)

    def only(self, findings, rule):
        hits = [f for f in findings if f.rule == rule]
        self.assertEqual(1, len(hits), f"expected one {rule}, got {self.rules(findings)}")
        return hits[0]


class OrdinaryDayTests(HealthCase):
    def test_an_ordinary_morning_is_silent(self):
        self.files["/proc/meminfo"] = "MemTotal: 7962896 kB\nMemAvailable: 5073572 kB\n"
        self.files["/proc/loadavg"] = "0.36 0.22 0.09 1/594 841657\n"
        for i in range(4):
            at = NOW + timedelta(seconds=30 * i)
            self.stream.add(at - timedelta(seconds=1), "NSE").add(at - timedelta(seconds=1), "MCX")
            self.assertEqual([], self.check(at))

    def test_every_finding_carries_evidence_and_a_suggestion(self):
        self.http[LOCAL_HEALTH] = ConnectionError("refused")
        self.api["/api/Feeds"] = ConnectionError("refused")
        self.files["/proc/meminfo"] = "MemTotal: 7962896 kB\nMemAvailable: 358400 kB\n"
        self.disk["/"] = (96 * GB, 2 * GB)
        self.docker = (0, "algotrading_db\tUp 3 days\n")
        self.ps = (0, "    1     0     1 10-00:00:00 /sbin/init\n")
        self.stream = FakeStream().add(NOW - timedelta(minutes=5), "NSE").add(NOW - timedelta(minutes=5), "MCX")
        for i in range(3):
            self.check(NOW + timedelta(seconds=30 * i))
        found = self.check(NOW + timedelta(seconds=90))
        self.assertIn("api-down", self.rules(found))
        self.assertGreaterEqual(len(found), 5)
        for f in found:
            self.assertTrue(1 <= len(f.evidence) <= 6, f.rule)
            self.assertTrue(f.suggestion, f.rule)
            self.assertTrue(f.fingerprint.startswith("health:"), f.fingerprint)


class ApiTests(HealthCase):
    def api_down(self):
        self.http[LOCAL_HEALTH] = ConnectionError("Connection refused")
        self.api["/api/Feeds"] = ConnectionError("Connection refused")

    def api_up(self):
        self.http.pop(LOCAL_HEALTH, None)
        self.api["/api/Feeds"] = feeds()

    def test_api_down_waits_for_90_seconds_of_failure(self):
        self.api_down()
        for i in range(3):   # 0, 30 and 60 s: the desk itself has not acted yet
            self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=30 * i))))
        f = self.only(self.check(NOW + timedelta(seconds=90)), "api-down")
        self.assertEqual(Severity.CRITICAL, f.severity)
        self.assertEqual("health:api-down", f.fingerprint)
        self.assertIn("11:30:00 IST", f.summary)
        self.assertIn("90 s", f.summary)

    def test_a_two_minute_outage_is_reported_and_keeps_its_fingerprint(self):
        self.api_down()
        found = [self.check(NOW + timedelta(seconds=30 * i)) for i in range(5)]   # 0 … 120 s
        hits = [f for batch in found for f in batch if f.rule == "api-down"]
        self.assertEqual(2, len(hits))   # at 90 s and 120 s
        self.assertEqual({"health:api-down"}, {f.fingerprint for f in hits})

    def test_the_0845_restart_is_not_an_outage(self):
        # 24 Sep: "stopping the API" 08:45:34, "API up" 08:46:14 — 40 s, straddling two checks.
        self.session.update(nse=False, mcx=False)
        morning = datetime(2026, 9, 24, 3, 15, 35, tzinfo=timezone.utc)   # 08:45:35 IST
        self.api_down()
        self.assertEqual([], self.check(morning))
        self.assertEqual([], self.check(morning + timedelta(seconds=30)))
        self.api_up()
        self.assertEqual([], self.check(morning + timedelta(seconds=60)))

    def test_a_45_second_outage_across_two_checks_is_silent(self):
        self.api_down()
        self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=5))))
        self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=35))))
        self.api_up()
        self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=65))))
        self.api_down()   # a later, separate blip starts a new count
        self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=95))))

    def test_a_slow_planned_restart_holds_until_the_desk_gives_up(self):
        # The desk began the restart before the outage did, and has not said "API up" yet.
        self.session.update(nse=False, mcx=False)
        self.files["desk.log"] = ("08:45:27  === 0845 — running market-open.sh ===\n"
                                  "08:45:34  stopping the API (pid 1692373 1692471)\n"
                                  "08:45:35  starting the API (Production, http://localhost:5025, chain: NIFTY)\n")
        start = datetime(2026, 9, 24, 3, 15, 40, tzinfo=timezone.utc)      # 08:45:40 IST
        self.api_down()
        for i in range(6):   # to 08:48:10: within 180 s of the stop
            self.assertEqual([], self.check(start + timedelta(seconds=30 * i)))
        f = self.only(self.check(start + timedelta(seconds=180)), "api-down")   # 08:48:40
        text = " ".join(f.evidence)
        self.assertIn("desk.log 08:45:35 IST: starting the API", text)
        self.assertIn("no \"API up\" after it", text)

    def test_a_restart_that_failed_is_reported_at_once(self):
        self.session.update(nse=False, mcx=False)
        start = datetime(2026, 9, 24, 3, 15, 40, tzinfo=timezone.utc)      # 08:45:40 IST
        self.files["desk.log"] = ("08:45:34  stopping the API (pid 1692373)\n"
                                  "08:45:35  starting the API (Production)\n"
                                  "08:47:35  WARN: the API did not come up within two minutes (see logs/api.log)\n")
        self.api_down()
        for i in range(4):
            found = self.check(start + timedelta(seconds=30 * i))
        f = self.only(found, "api-down")
        self.assertIn("did not come up", " ".join(f.evidence))

    def test_a_crash_the_desk_is_still_restarting_is_reported(self):
        # The outage began before the desk's restart did: the API died, it was not stopped.
        self.files["desk.log"] = ("11:30:15  WARN: API health check failed (1/3)\n"
                                  "11:30:45  WARN: API health check failed (2/3)\n"
                                  "11:31:15  API is down — checking infra, then restarting\n"
                                  "11:31:16  starting the API (Production, http://localhost:5025, chain: NIFTY)\n")
        self.api_down()
        for i in range(3):
            self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=30 * i))))
        self.assertIn("api-down", self.rules(self.check(NOW + timedelta(seconds=90))))

    def test_a_morning_job_run_by_hand_restarts_the_api_on_plan(self):
        # scripts/market-open.sh run from a terminal writes only to its own log, not desk.log: its restart of
        # the API read as a crash and paged CRITICAL api-down.
        self.session.update(nse=False, mcx=False)   # the feed is not the subject here
        self.files["market-open-2026-09-24.log"] = ("11:29:40  === market-open: Thursday 24 September 2026 ===\n"
                                                    "11:29:55  stopping the API (pid 1692373)\n"
                                                    "11:29:56  starting the API (Production, http://localhost:5025)\n")
        self.api_down()
        for i in range(6):   # to 11:32:30: within 180 s of the stop
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))
        f = self.only(self.check(NOW + timedelta(seconds=180)), "api-down")   # 11:33:00: the desk gave up
        self.assertIn("market-open-2026-09-24.log 11:29:56 IST: starting the API", " ".join(f.evidence))

    def test_a_restart_the_job_finished_does_not_hold_a_later_crash(self):
        self.session.update(nse=False, mcx=False)
        self.files["desk.log"] = "11:29:50  stopping the API (pid 1)\n11:29:51  starting the API (Production)\n"
        self.files["market-open-2026-09-24.log"] = ("11:29:50  stopping the API (pid 1)\n"
                                                    "11:29:51  starting the API (Production)\n"
                                                    "11:29:58  API up\n")
        self.api_down()
        for i in range(3):
            self.check(NOW + timedelta(seconds=30 * i))
        self.assertIn("api-down", self.rules(self.check(NOW + timedelta(seconds=90))))

    def test_yesterdays_job_log_is_not_read(self):
        self.session.update(nse=False, mcx=False)
        self.files["market-open-2026-09-23.log"] = "11:29:55  stopping the API (pid 1)\n11:29:56  starting the API\n"
        self.api_down()
        for i in range(3):
            self.check(NOW + timedelta(seconds=30 * i))
        self.assertIn("api-down", self.rules(self.check(NOW + timedelta(seconds=90))))

    def test_an_old_restart_line_does_not_hold(self):
        self.files["desk.log"] = "11:28:02  stopping the API (pid 1)\n11:28:03  starting the API (Production)\n"
        late = datetime(2026, 9, 24, 10, 0, tzinfo=timezone.utc)   # 15:30 IST: 11:28 was hours ago
        self.api_down()
        for i in range(3):
            self.check(late + timedelta(seconds=30 * i))
        self.assertIn("api-down", self.rules(self.check(late + timedelta(seconds=90))))

    def test_a_failed_health_route_with_working_signed_in_gets_is_not_down(self):
        self.http[LOCAL_HEALTH] = ConnectionError("Connection refused")
        for i in range(5):
            self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=30 * i))))

    def test_one_good_check_between_failures_restarts_the_count(self):
        self.api_down()
        self.check(NOW)
        self.check(NOW + timedelta(seconds=30))
        self.check(NOW + timedelta(seconds=60))
        self.api["/api/Feeds"] = feeds()
        self.check(NOW + timedelta(seconds=90))
        self.api["/api/Feeds"] = ConnectionError("Connection refused")
        for i in range(4, 7):
            self.assertNotIn("api-down", self.rules(self.check(NOW + timedelta(seconds=30 * i))))

    def test_a_streak_left_from_long_ago_starts_again(self):
        self.api_down()
        self.check(NOW)
        self.check(NOW + timedelta(seconds=30))
        self.check(NOW + timedelta(seconds=60))
        later = NOW + timedelta(minutes=10)
        for i in range(3):
            self.assertNotIn("api-down", self.rules(self.check(later + timedelta(seconds=30 * i))))
        self.assertIn("api-down", self.rules(self.check(later + timedelta(seconds=90))))

    def test_the_streak_survives_a_sentinel_restart(self):
        self.api_down()
        for i in range(3):
            self.check(NOW + timedelta(seconds=30 * i))
        self.ctx = None                      # a new process: state comes back from its file
        self._agent = self.agent()
        self.assertIn("api-down", self.rules(self.check(NOW + timedelta(seconds=90))))

    def test_degraded_when_health_answers_but_signed_in_gets_fail(self):
        self.api["/api/Feeds"] = RuntimeError("500 Server Error: Internal Server Error for url: "
                                              "http://localhost:5025/api/Feeds")
        self.assertNotIn("api-degraded", self.rules(self.check(NOW)))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "api-degraded")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertIn("database", f.suggestion)
        self.assertNotIn("api-down", self.rules([f]))

    def test_a_failing_runs_list_is_degraded_even_while_feeds_answer(self):
        # The trading agent reads GET /api/Strategy/runs; when that alone fails, /health and /api/Feeds
        # still answer and nothing here said so.
        self.api[RUNNING_PATH] = RuntimeError("500 Server Error: Internal Server Error for url: "
                                              "http://localhost:5025/api/Strategy/runs?status=Running&take=500")
        self.assertEqual([], self.check(NOW))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "api-degraded")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("health:api-degraded", f.fingerprint)
        self.assertIn("strategy runs", f.title)
        self.assertTrue(f.evidence[0].startswith(f"GET {RUNNING_PATH}: RuntimeError: 500"), f.evidence)
        self.api[RUNNING_PATH] = []
        self.assertEqual([], self.check(NOW + timedelta(seconds=60)))

    def test_a_runs_answer_that_is_not_a_list_counts_as_failing(self):
        self.api[RUNNING_PATH] = {"message": "Service Unavailable"}
        self.check(NOW)
        self.assertIn("unexpected body (dict)", " ".join(self.only(self.check(NOW + timedelta(seconds=30)),
                                                                   "api-degraded").evidence))

    def test_the_runs_are_not_asked_for_when_the_feeds_call_already_failed(self):
        asked = []
        self.api[RUNNING_PATH] = lambda path: asked.append(path) or []
        self.api["/api/Feeds"] = ConnectionError("Connection refused")
        self.check(NOW)
        self.assertEqual([], asked)

    def test_a_429_on_sign_in_points_at_the_limiter(self):
        self.api["/api/Feeds"] = RuntimeError("429 Client Error: Too Many Requests for url: "
                                              "http://localhost:5025/api/UserAuth/login")
        self.check(NOW)
        f = self.only(self.check(NOW + timedelta(seconds=30)), "api-degraded")
        self.assertIn("limiter", f.suggestion)

    def test_errors_are_redacted_before_they_become_evidence(self):
        self.api["/api/Feeds"] = RuntimeError("401 for Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789")
        self.check(NOW)
        f = self.only(self.check(NOW + timedelta(seconds=30)), "api-degraded")
        self.assertNotIn("abcdefghijklmnopqrstuvwxyz0123456789", " ".join(f.evidence) + f.summary)


class PublicSiteTests(HealthCase):
    def test_a_502_outside_while_the_api_answers_here_is_the_stale_tunnel(self):
        self.http[PUBLIC_HEALTH] = 502
        self.assertNotIn("public-down", self.rules(self.check(NOW)))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "public-down")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertIn("HTTP 502", " ".join(f.evidence))
        self.assertIn("cloudflared", f.suggestion)

    def test_the_public_site_is_not_judged_while_the_api_itself_is_down(self):
        self.http[LOCAL_HEALTH] = ConnectionError("refused")
        self.http[PUBLIC_HEALTH] = 502
        for i in range(3):
            self.assertNotIn("public-down", self.rules(self.check(NOW + timedelta(seconds=30 * i))))

    def test_a_timeout_counts_as_down(self):
        self.http[PUBLIC_HEALTH] = TimeoutError("read timed out")
        self.check(NOW)
        self.assertIn("public-down", self.rules(self.check(NOW + timedelta(seconds=30))))

    def test_the_url_is_configurable_and_can_be_switched_off(self):
        self.env["SENTINEL_PUBLIC_URL"] = "off"
        self.check(NOW)
        self.check(NOW + timedelta(seconds=30))
        self.assertEqual([LOCAL_HEALTH, LOCAL_HEALTH], self.http_calls)

    def test_a_custom_public_url(self):
        self.env["SENTINEL_PUBLIC_URL"] = "https://example.test/"
        self.http["https://example.test/health"] = 530
        self.check(NOW)
        self.assertIn("public-down", self.rules(self.check(NOW + timedelta(seconds=30))))


class FeedTests(HealthCase):
    def test_a_silent_nse_feed_is_critical_and_names_the_running_feed(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=120), "NSE", "NSE:NIFTY26SEP25000CE") \
            .add(NOW - timedelta(seconds=1), "MCX")
        f = self.only(self.check(NOW), "feed-silent")
        self.assertEqual(Severity.CRITICAL, f.severity)
        self.assertEqual("health:feed-silent:NSE", f.fingerprint)
        text = " ".join(f.evidence)
        self.assertIn("11:28:00 IST", text)
        self.assertIn("NSE:NIFTY26SEP25000CE", text)
        self.assertIn("dhan (source managed, pid 4242)", text)
        self.assertIn("/api/Feeds/dhan/stop", f.suggestion)
        self.assertIn("/api/Feeds/fyers/start", f.suggestion)
        self.assertIn("120 s", f.summary)

    def test_the_fingerprint_does_not_change_as_the_silence_grows(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=120), "NSE").add(NOW, "MCX")
        first = self.only(self.check(NOW), "feed-silent")
        self.stream.add(NOW + timedelta(seconds=30), "MCX")
        second = self.only(self.check(NOW + timedelta(seconds=30)), "feed-silent")
        self.assertEqual(first.fingerprint, second.fingerprint)
        self.assertEqual(first.title, second.title)

    def test_ticks_inside_the_threshold_are_quiet(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=80), "NSE").add(NOW - timedelta(seconds=85), "MCX")
        self.assertEqual([], self.check(NOW))

    def test_bse_ticks_keep_the_nse_group_alive(self):
        self.stream = FakeStream().add(NOW - timedelta(minutes=5), "NSE").add(NOW - timedelta(seconds=5), "BSE") \
            .add(NOW, "MCX")
        self.assertEqual([], self.check(NOW))

    def test_nothing_is_said_outside_the_session(self):
        self.session.update(nse=False, mcx=False)
        self.stream = FakeStream().add(NOW - timedelta(hours=12), "NSE")
        self.api["/api/Feeds"] = feeds(running=())
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_the_evening_watches_mcx_only(self):
        evening = datetime(2026, 9, 24, 14, 0, tzinfo=timezone.utc)   # 19:30 IST
        self.session.update(nse=False, mcx=True)
        self.stream = FakeStream().add(evening - timedelta(hours=4), "NSE").add(evening - timedelta(seconds=200), "MCX")
        f = self.only(self.check(evening), "feed-silent")
        self.assertEqual("health:feed-silent:MCX", f.fingerprint)

    def test_only_one_group_silent_says_the_feed_itself_is_alive(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=300), "MCX").add(NOW, "NSE")
        f = self.only(self.check(NOW), "feed-silent")
        self.assertIn("subscription", f.summary)
        self.assertIn("still flowing: NSE/BSE", f.evidence)

    def test_the_silence_clock_starts_at_the_open_not_at_yesterdays_last_tick(self):
        yesterday_close = datetime(2026, 9, 23, 9, 59, 59, tzinfo=timezone.utc)   # 15:29:59 IST
        just_open = datetime(2026, 9, 24, 3, 45, 40, tzinfo=timezone.utc)         # 09:15:40 IST
        self.stream = FakeStream().add(yesterday_close, "NSE").add(just_open - timedelta(seconds=1), "MCX")
        self.assertEqual([], self.check(just_open))
        later = datetime(2026, 9, 24, 3, 47, 0, tzinfo=timezone.utc)              # 09:17:00 IST
        self.stream.add(later - timedelta(seconds=1), "MCX")
        f = self.only(self.check(later), "feed-silent")
        self.assertIn("23 Sep 15:29:59 IST", " ".join(f.evidence))

    def test_a_market_seen_closed_restarts_the_clock_for_a_special_session(self):
        # Muhurat trading: NSE opens at 18:00 IST; the last NSE tick was the 15:29 close.
        self.session.update(nse=False, mcx=False)
        self.stream = FakeStream().add(datetime(2026, 9, 24, 9, 59, tzinfo=timezone.utc), "NSE")
        self.check(datetime(2026, 9, 24, 12, 29, 50, tzinfo=timezone.utc))       # 17:59:50, closed
        self.session.update(nse=True)
        self.assertEqual([], self.check(datetime(2026, 9, 24, 12, 30, 30, tzinfo=timezone.utc)))
        self.assertIn("feed-silent", self.rules(self.check(datetime(2026, 9, 24, 12, 31, 30, tzinfo=timezone.utc))))

    def test_replayed_ticks_do_not_count_as_live(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=200), "NSE") \
            .add(NOW - timedelta(seconds=1), "NSE", replay=True, source="truedata").add(NOW, "MCX")
        self.assertEqual("health:feed-silent:NSE", self.only(self.check(NOW), "feed-silent").fingerprint)

    def test_the_stream_id_time_stands_in_when_the_payload_has_no_received_time(self):
        self.stream = FakeStream().add(NOW - timedelta(seconds=150), "NSE", received=False).add(NOW, "MCX")
        self.assertIn("150 s", self.only(self.check(NOW), "feed-silent").summary)
        self.stream = FakeStream().add(NOW - timedelta(seconds=10), "NSE", received=False).add(NOW, "MCX")
        self.ctx = None
        self.assertEqual([], self.check(NOW))

    def test_an_empty_stream_during_the_session_is_silent(self):
        self.stream = FakeStream()
        found = self.check(NOW)
        self.assertEqual(["feed-silent", "feed-silent"], self.rules(found))
        self.assertIn("anywhere", " ".join(found[0].evidence))

    def test_a_group_missing_from_a_window_that_reaches_past_the_threshold_is_silent(self):
        self.stream = FakeStream().add(NOW - timedelta(minutes=30), "NSE")
        for i in range(200):
            self.stream.add(NOW - timedelta(seconds=i), "MCX")
        with mock.patch.object(health, "STREAM_PAGE", 50):
            f = self.only(self.check(NOW), "feed-silent")
        self.assertEqual("health:feed-silent:NSE", f.fingerprint)
        self.assertIn("reach back to", " ".join(f.evidence))

    def test_a_stream_too_busy_to_tell_within_the_bound_says_nothing(self):
        self.stream = FakeStream().add(NOW - timedelta(minutes=30), "NSE")
        for i in range(100):
            self.stream.add(NOW - timedelta(milliseconds=200 * i), "MCX")
        with mock.patch.object(health, "STREAM_PAGE", 10), mock.patch.object(health, "STREAM_MAX_PAGES", 2):
            self.assertEqual([], self.check(NOW))
        self.assertEqual(2, self.stream.calls)

    def test_reading_stops_once_every_open_group_is_seen(self):
        for i in range(500):
            self.stream.add(NOW - timedelta(seconds=10 + i), "NSE")
        with mock.patch.object(health, "STREAM_PAGE", 10):
            self.assertEqual([], self.check(NOW))
        self.assertEqual(1, self.stream.calls)

    def test_an_unreadable_stream_is_reported_once_as_redis_unreachable(self):
        self.stream = FakeStream(error=ConnectionError("Error 111 connecting to localhost:6379"))
        f = self.only(self.check(NOW), "redis-unreachable")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertNotIn("feed-silent", self.rules(self.check(NOW + timedelta(seconds=30))))

    def test_a_stopped_redis_container_is_left_to_container_down(self):
        self.stream = FakeStream(error=ConnectionError("Error 111 connecting to localhost:6379"))
        self.docker = (0, "algotrading_db\tUp 3 days\n")
        self.assertEqual([], self.check(NOW))
        self.assertEqual(["container-down"], self.rules(self.check(NOW + timedelta(seconds=30))))

    def test_fyers_running_gets_the_token_advice(self):
        self.api["/api/Feeds"] = feeds(running=("fyers",))
        self.stream = FakeStream().add(NOW - timedelta(seconds=120), "NSE", source="fyers").add(NOW, "MCX")
        f = self.only(self.check(NOW), "feed-silent")
        self.assertIn("10 Sep", f.suggestion)
        self.assertIn("fyers (source managed", " ".join(f.evidence))

    def test_an_unanswering_feeds_endpoint_is_said_in_the_evidence(self):
        self.api["/api/Feeds"] = ConnectionError("refused")
        self.stream = FakeStream().add(NOW - timedelta(seconds=120), "NSE").add(NOW, "MCX")
        f = self.only(self.check(NOW), "feed-silent")
        self.assertIn("did not answer", " ".join(f.evidence))


class NoFeedTests(HealthCase):
    def test_no_running_feed_while_open_is_critical_on_the_second_check(self):
        self.api["/api/Feeds"] = feeds(running=())
        self.stream = FakeStream().add(NOW - timedelta(minutes=10), "NSE").add(NOW - timedelta(minutes=10), "MCX")
        self.assertEqual([], self.check(NOW))
        found = self.check(NOW + timedelta(seconds=30))
        f = self.only(found, "no-feed-running")
        self.assertEqual(Severity.CRITICAL, f.severity)
        self.assertNotIn("feed-silent", self.rules(found))
        self.assertIn("dhan: not running (source none)", f.evidence)

    def test_ticks_arriving_without_a_supervised_feed_is_only_high(self):
        self.api["/api/Feeds"] = feeds(running=())
        self.check(NOW)
        self.stream.add(NOW + timedelta(seconds=29), "NSE").add(NOW + timedelta(seconds=29), "MCX")
        f = self.only(self.check(NOW + timedelta(seconds=30)), "no-feed-running")
        self.assertEqual(Severity.HIGH, f.severity)

    def test_no_feed_after_the_close_is_ordinary(self):
        self.session.update(nse=False, mcx=False)
        self.api["/api/Feeds"] = feeds(running=())
        self.check(NOW)
        self.assertEqual([], self.check(NOW + timedelta(seconds=30)))


class DeskScheduleTests(HealthCase):
    """The feeds are watched on the desk's schedule, not only the exchange calendar's."""

    CLOSE_2335 = datetime(2026, 9, 25, 18, 5, 0, tzinfo=timezone.utc)   # Friday 23:35:00 IST

    def mcx_evening(self):
        # The API's calendar keeps MCX open to 23:55; market-close.sh stopped every feed
        # at 23:35:12 and the last closing-price tick reached Redis at 23:35:11. The desk
        # closed at 23:35 then; since 27 Sep that takes MARKET_CLOSE_AT=2335.
        self.session.update(nse=False, mcx=True)
        self.env["MARKET_CLOSE_AT"] = "2335"
        self.stream = FakeStream().add(self.CLOSE_2335 + timedelta(seconds=11), "MCX", "MCX:CRUDEOILM26OCTFUT")

    def test_mcx_is_not_watched_after_the_desks_close(self):
        self.mcx_evening()
        self.api["/api/Feeds"] = feeds(running=())
        for minutes in (1, 1.5, 2, 2.5, 15, 15.5, 19.5):      # 23:36 … 23:54:30
            self.assertEqual([], self.check(self.CLOSE_2335 + timedelta(minutes=minutes)), minutes)

    def test_the_watch_holds_right_up_to_the_desks_close(self):
        self.mcx_evening()
        self.stream = FakeStream()
        self.api["/api/Feeds"] = feeds(running=())
        self.check(self.CLOSE_2335 - timedelta(seconds=60))
        self.assertIn("no-feed-running", self.rules(self.check(self.CLOSE_2335 - timedelta(seconds=30))))
        self.assertEqual([], self.check(self.CLOSE_2335))
        self.assertEqual([], self.check(self.CLOSE_2335 + timedelta(seconds=30)))
        streaks = self.ctx.state("health").data.get("streaks", {})
        self.assertNotIn("no-feed-running", streaks)   # cleared, not left to go stale

    def test_the_close_comes_from_market_close_at(self):
        self.mcx_evening()
        self.stream = FakeStream()
        self.api["/api/Feeds"] = feeds(running=())
        self.env["MARKET_CLOSE_AT"] = "2300"
        early = datetime(2026, 9, 25, 17, 40, tzinfo=timezone.utc)   # 23:10 IST
        self.assertEqual([], self.check(early))
        self.assertEqual([], self.check(early + timedelta(seconds=30)))

    def test_by_default_a_winter_mcx_evening_is_watched_to_its_close(self):
        # Monday 7 Dec 2026: MCX trades to 23:55, and the desk's market-close.sh now runs
        # at 23:58, so a feed missing at 23:40 is a crude run trading blind — not, as
        # with the old 23:35 default, the desk's own close.
        self.session.update(nse=False, mcx=True)
        self.stream = FakeStream()
        self.api["/api/Feeds"] = feeds(running=())
        winter = datetime(2026, 12, 7, 18, 10, tzinfo=timezone.utc)   # 23:40 IST
        self.check(winter)
        self.assertIn("no-feed-running", self.rules(self.check(winter + timedelta(seconds=30))))
        self.assertEqual(time(23, 58), health._parse_close_at(None))

    def test_a_malformed_market_close_at_falls_back_to_the_default(self):
        self.mcx_evening()
        self.stream = FakeStream()
        self.api["/api/Feeds"] = feeds(running=())
        self.env["MARKET_CLOSE_AT"] = "23:00"
        early = datetime(2026, 9, 25, 17, 40, tzinfo=timezone.utc)   # 23:10 IST
        self.check(early)
        self.assertIn("no-feed-running", self.rules(self.check(early + timedelta(seconds=30))))

    def dussehra_evening(self):
        # 20 Oct 2026: NSE closed all day, MCX keeps its evening session; market-open.sh
        # said "Exchange holiday — NSE is closed today (Dussehra). Nothing to start."
        self.session.update(nse=False, mcx=True, trading_day=False, holiday="Dussehra")
        self.stream = FakeStream().add(datetime(2026, 10, 19, 18, 5, tzinfo=timezone.utc), "MCX")
        return datetime(2026, 10, 20, 13, 0, tzinfo=timezone.utc)   # 18:30 IST

    def test_an_nse_holiday_with_an_mcx_evening_is_silent(self):
        at = self.dussehra_evening()
        self.api["/api/Feeds"] = feeds(running=())
        for i in range(6):
            self.assertEqual([], self.check(at + timedelta(seconds=30 * i)))

    def test_an_nse_holiday_is_silent_when_the_feeds_endpoint_does_not_answer(self):
        at = self.dussehra_evening()
        self.api["/api/Feeds"] = RuntimeError("500 Server Error")
        for i in range(3):
            self.assertNotIn("feed-silent", self.rules(self.check(at + timedelta(seconds=30 * i))))

    SATURDAY_SESSION = datetime(2026, 9, 26, 5, 0, tzinfo=timezone.utc)   # 10:30 IST, a special live session

    def test_a_weekend_special_session_is_not_a_missed_morning(self):
        # The calendar says NSE is trading; market-open.sh said "Weekend — Nothing to do."
        self.session.update(nse=True, mcx=False, trading_day=True)
        self.api["/api/Feeds"] = feeds(running=())
        self.stream = FakeStream().add(datetime(2026, 9, 25, 18, 4, tzinfo=timezone.utc), "MCX")
        for i in range(6):
            self.assertEqual([], self.check(self.SATURDAY_SESSION + timedelta(seconds=30 * i)))

    def test_a_feed_started_by_hand_for_a_weekend_session_is_watched(self):
        self.session.update(nse=True, mcx=False, trading_day=True)
        self.api["/api/Feeds"] = feeds(running=())
        self.stream = FakeStream()
        self.assertEqual([], self.check(self.SATURDAY_SESSION))
        self.api["/api/Feeds"] = feeds(running=("fyers",))
        self.assertEqual([], self.check(self.SATURDAY_SESSION + timedelta(seconds=30)))
        f = self.only(self.check(self.SATURDAY_SESSION + timedelta(seconds=150)), "feed-silent")
        self.assertEqual("health:feed-silent:NSE", f.fingerprint)

    def test_a_feed_started_by_hand_on_an_nse_holiday_is_watched(self):
        at = self.dussehra_evening()
        self.api["/api/Feeds"] = feeds(running=())
        self.assertEqual([], self.check(at))
        self.api["/api/Feeds"] = feeds(running=("dhan",))      # someone started it on purpose
        self.assertEqual([], self.check(at + timedelta(seconds=30)))   # the clock starts now
        f = self.only(self.check(at + timedelta(seconds=150)), "feed-silent")
        self.assertEqual("health:feed-silent:MCX", f.fingerprint)


class CalendarOutageTests(HealthCase):
    """An API that stops answering does not turn a holiday back into a weekday."""

    GANDHI_JAYANTI = datetime(2026, 10, 2, 5, 30, tzinfo=timezone.utc)   # Friday 11:00 IST, NSE and MCX shut

    def holiday(self):
        self.session.update(nse=False, mcx=False, trading_day=False, holiday="Gandhi Jayanti")
        self.api["/api/MarketSession/check?exchange=MCX*"] = lambda _: {
            "isTradingDay": False, "isMarketOpen": False, "isHoliday": True, "holidayName": "Gandhi Jayanti"}
        self.api["/api/Feeds"] = feeds(running=())
        # Yesterday's last ticks, before market-close.sh stopped the feeds.
        self.stream = FakeStream().add(datetime(2026, 10, 1, 18, 4, tzinfo=timezone.utc), "MCX") \
            .add(datetime(2026, 10, 1, 10, 0, tzinfo=timezone.utc), "NSE")

    def api_down(self):
        # A deploy restarting the API: nothing answers, the calendar included.
        self.http[LOCAL_HEALTH] = ConnectionError("connection refused")
        for route in list(self.api):
            self.api[route] = ConnectionError("connection refused")

    def test_a_holiday_outage_reports_the_api_and_no_silent_feed(self):
        self.holiday()
        self.assertEqual([], self.check(self.GANDHI_JAYANTI))
        self.api_down()
        seen = set()
        for i in range(1, 11):   # five minutes of outage
            seen |= set(self.rules(self.check(self.GANDHI_JAYANTI + timedelta(seconds=30 * i))))
        self.assertIn("api-down", seen)
        self.assertNotIn("feed-silent", seen)
        self.assertNotIn("redis-unreachable", seen)

    def test_it_is_still_a_holiday_after_sentinel_restarts_mid_outage(self):
        self.holiday()
        self.check(self.GANDHI_JAYANTI)
        self.api_down()
        self.ctx, self._agent = None, None    # a deploy restarted Sentinel too
        for i in range(1, 7):
            self.assertNotIn("feed-silent", self.rules(self.check(self.GANDHI_JAYANTI + timedelta(seconds=30 * i))))

    def test_a_trading_day_outage_still_watches_the_feed_and_says_how_it_knows(self):
        self.check(NOW)                         # the calendar answered: a trading day
        self.api_down()                         # and the stream stops where it was
        found = [self.check(NOW + timedelta(seconds=30 * i)) for i in range(1, 6)]
        silent = [f for batch in found for f in batch if f.rule == "feed-silent"]
        self.assertTrue(silent)
        self.assertIn("NSE/BSE session open (exchange calendar as it answered earlier today; it is not answering "
                      "now)", silent[0].evidence)

    def test_what_the_calendar_said_yesterday_is_not_todays_answer(self):
        self.holiday()
        self.check(self.GANDHI_JAYANTI)
        self.api_down()
        monday = datetime(2026, 10, 5, 5, 30, tzinfo=timezone.utc)   # 11:00 IST, a trading day
        found = [self.check(monday + timedelta(seconds=30 * i)) for i in range(3)]
        self.assertIn("feed-silent", {f.rule for batch in found for f in batch})   # the weekday rule, as before


class MachineTests(HealthCase):
    def meminfo(self, available_mb):
        self.files["/proc/meminfo"] = (f"MemTotal:        7962896 kB\nMemFree:  100000 kB\n"
                                       f"MemAvailable:    {available_mb * 1024} kB\n"
                                       "SwapTotal:       0 kB\nSwapFree:        0 kB\n")

    def test_under_400_mb_is_critical_on_the_second_reading(self):
        # One reading under 400 MB can be a dotnet build during a deploy; two in a row are the OOM killer's.
        self.meminfo(350)
        self.assertEqual([], self.check(NOW))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "memory-low")
        self.assertEqual(Severity.CRITICAL, f.severity)
        self.assertIn("MemAvailable 350 MB of 7776 MB", f.evidence)
        self.assertTrue(any("python3" in e for e in f.evidence))

    def test_a_dip_under_400_mb_then_600_is_high_not_critical(self):
        self.meminfo(350)
        self.check(NOW)
        self.meminfo(600)
        self.assertEqual(Severity.HIGH, self.only(self.check(NOW + timedelta(seconds=30)), "memory-low").severity)

    def test_memory_hovering_at_the_line_is_one_incident(self):
        # Reported, it stays reported until more than 875 MB is free: 690/710 MB alternating is one problem.
        self.session.update(nse=False, mcx=False)   # minutes pass; the feed is not the subject here
        seen = []
        for i, mb in enumerate((690, 690, 710, 690, 720, 860, 700)):
            self.meminfo(mb)
            seen.append(bool([f for f in self.check(NOW + timedelta(seconds=30 * i)) if f.rule == "memory-low"]))
        self.assertEqual([False, True, True, True, True, True, True], seen)
        self.meminfo(710)
        f = self.only(self.check(NOW + timedelta(seconds=210)), "memory-low")
        self.assertIn("stays open until more than 875 MB", f.summary)
        self.meminfo(900)
        self.assertEqual([], self.check(NOW + timedelta(seconds=240)))
        self.meminfo(710)
        self.assertEqual([], self.check(NOW + timedelta(seconds=270)), "cleared: 710 MB is above the line")

    def test_under_700_mb_is_high_on_the_second_check(self):
        self.meminfo(600)
        self.assertEqual([], self.check(NOW))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "memory-low")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("health:memory-low", f.fingerprint)

    def test_plenty_of_memory_is_quiet(self):
        self.meminfo(4900)
        self.check(NOW)
        self.assertEqual([], self.check(NOW + timedelta(seconds=30)))

    def test_no_proc_means_no_machine_rules(self):
        # macOS: no /proc/meminfo, no /proc/loadavg
        self.assertEqual([], self.check(NOW))
        self.assertFalse(any(c[0] == "ps" and "--sort=-rss" in c for c in self.run_calls))

    def test_less_than_5_gb_free_is_high(self):
        self.disk["/"] = (96 * GB, 4 * GB)
        f = self.only(self.check(NOW), "disk-low")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertEqual("health:disk-low:/", f.fingerprint)
        self.assertIn("4.0 GB free of 96 GB", f.evidence[0])

    def test_less_than_ten_percent_free_is_high_even_above_5_gb(self):
        self.disk["/"] = (200 * GB, 15 * GB)
        self.only(self.check(NOW), "disk-low")

    def test_enough_disk_is_quiet(self):
        self.disk["/"] = (96 * GB, 20 * GB)
        self.assertEqual([], self.check(NOW))

    def test_a_repository_on_its_own_disk_is_checked_too(self):
        agent = HealthAgent(http_status=self._http_status, read_text=lambda p: None,
                            disk_usage=lambda p: (96 * GB, 60 * GB) if p == "/" else (50 * GB, 1 * GB),
                            cpu_count=lambda: 2, device_of=lambda p: 1 if p == "/" else 2)
        f = self.only(self.check(NOW, agent=agent), "disk-low")
        self.assertEqual(f"health:disk-low:{self.tmp}", f.fingerprint)

    def test_high_load_needs_three_checks_in_a_row(self):
        self.files["/proc/loadavg"] = "5.10 5.00 4.00 3/600 12345\n"
        self.assertEqual([], self.check(NOW))
        self.assertEqual([], self.check(NOW + timedelta(seconds=30)))
        f = self.only(self.check(NOW + timedelta(seconds=60)), "load-high")
        self.assertEqual(Severity.MEDIUM, f.severity)
        self.assertIn("on 2 CPU(s)", f.evidence[0])

    def test_load_under_twice_the_cpus_is_quiet(self):
        self.session.update(nse=False, mcx=False)   # minutes pass; the feed is not the subject here
        self.files["/proc/loadavg"] = "3.90 3.90 3.00 3/600 12345\n"
        for i in range(4):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_a_load_hovering_at_the_line_is_one_incident(self):
        # Line 4.0 on 2 CPUs; reported, it clears only below 3.2.
        self.session.update(nse=False, mcx=False)
        seen = []
        for i, value in enumerate(("4.5", "4.5", "4.5", "3.9", "4.1", "3.5", "3.3")):
            self.files["/proc/loadavg"] = f"4.0 {value} 4.0 3/600 1\n"
            seen.append(bool([f for f in self.check(NOW + timedelta(seconds=30 * i)) if f.rule == "load-high"]))
        self.assertEqual([False, False, True, True, True, True, True], seen)
        self.files["/proc/loadavg"] = "4.0 3.3 4.0 3/600 1\n"
        f = self.only(self.check(NOW + timedelta(seconds=210)), "load-high")
        self.assertIn("below 3.2", f.summary)
        self.files["/proc/loadavg"] = "4.0 3.1 4.0 3/600 1\n"
        self.assertEqual([], self.check(NOW + timedelta(seconds=240)))
        self.files["/proc/loadavg"] = "4.0 3.9 4.0 3/600 1\n"
        self.assertEqual([], self.check(NOW + timedelta(seconds=270)), "cleared: 3.9 is under the line")

    def test_a_dip_in_load_restarts_the_count(self):
        self.session.update(nse=False, mcx=False)
        for i, value in enumerate(("5.0", "5.0", "1.0", "5.0", "5.0")):
            self.files["/proc/loadavg"] = f"5.0 {value} 4.0 3/600 1\n"
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))


class ContainerTests(HealthCase):
    def test_a_missing_redis_container_is_critical_on_the_second_check(self):
        self.docker = (0, "algotrading_db\tUp 3 days\nalgotrading_grafana\tUp 3 days\n")
        self.assertNotIn("container-down", self.rules(self.check(NOW)))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "container-down")
        self.assertEqual(Severity.CRITICAL, f.severity)
        self.assertEqual("health:container-down:algotrading_redis", f.fingerprint)
        self.assertIn("algotrading_redis: not in `docker ps`", f.evidence)

    def test_a_restarting_container_counts_as_down(self):
        self.docker = (0, "algotrading_db\tRestarting (1) 4 seconds ago\nalgotrading_redis\tUp 3 days\n")
        self.check(NOW)
        f = self.only(self.check(NOW + timedelta(seconds=30)), "container-down")
        self.assertEqual("health:container-down:algotrading_db", f.fingerprint)

    def test_no_docker_means_nothing_to_say(self):
        for result in ((127, ""), (1, "")):
            self.docker = result
            self.ctx = None
            self.check(NOW)
            self.assertEqual([], self.check(NOW + timedelta(seconds=30)))

    def test_the_container_names_come_from_the_environment(self):
        self.env["SENTINEL_CONTAINERS"] = "algotrading_db, openfno-broker-postgres-1"
        self.check(NOW)
        f = self.only(self.check(NOW + timedelta(seconds=30)), "container-down")
        self.assertEqual("health:container-down:openfno-broker-postgres-1", f.fingerprint)


class DeskTests(HealthCase):
    def setUp(self):
        super().setUp()
        self.session.update(nse=False, mcx=False)   # minutes pass; the feed is not the subject here

    def pidfile(self, pid):
        self.env["HOME"] = "/home/ubuntu"
        self.files[PIDFILE] = f"{pid}\n"

    def fresh(self):
        """A new machine for a sub-test: no state, no files, no environment."""
        self.tmp = Path(tempfile.mkdtemp())
        self.ctx, self.files, self.env = None, {}, {}
        self._agent = self.agent()

    def test_no_desk_is_high_on_the_second_check(self):
        self.ps = (0, "    1     0     1 10-00:00:00 /sbin/init\n")
        self.assertEqual([], self.check(NOW))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "desk-down")
        self.assertEqual(Severity.HIGH, f.severity)
        self.assertIn("desk.sh --headless", f.suggestion)
        self.assertIn("no desk.pid found", f.evidence)

    def test_subshells_and_mentions_of_desk_sh_are_not_desks(self):
        self.ps = (0, PS_ONE_DESK
                   + f"41000 32212 32212 00:42 {DESK}\n"                        # $(...) subshell during a build
                   + f"41001 41000 32212 00:40 {DESK}\n"                        # a subshell of the subshell
                   + "41100 41099 41000 00:01 bash -c pgrep -af desk.sh\n"      # someone looking for it
                   + "41200 41199 41200 05:00 vim scripts/desk.sh\n"
                   + "41300 41299 41300 00:01 python3 -c import os,sys; os.setsid() /bin/bash scripts/desk.sh "
                     "--daemon\n")
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_only_a_stop_command_is_not_a_running_desk(self):
        self.ps = (0, "  500     1   500 00:01 bash ./scripts/desk.sh --stop\n")
        self.check(NOW)
        self.assertIn("desk-down", self.rules(self.check(NOW + timedelta(seconds=30))))

    def test_two_desk_loops_twice_in_a_row_is_duplicated(self):
        self.pidfile(32212)
        self.ps = (0, PS_ONE_DESK + f"32299     1 32299 00:05 {DESK}\n")
        self.assertEqual([], self.check(NOW))
        f = self.only(self.check(NOW + timedelta(seconds=30)), "desk-duplicated")
        self.assertEqual(Severity.MEDIUM, f.severity)
        text = " ".join(f.evidence)
        self.assertIn("pid 32212", text)
        self.assertIn("pid 32299", text)
        self.assertIn("desk.pid names 32212", text)
        self.assertIn("never a bash that is the parent of `dotnet run`", f.suggestion)

    def test_a_duplicate_that_dies_within_seconds_is_not_reported(self):
        self.ps = (0, PS_ONE_DESK + f"32299     1 32299 00:05 {DESK}\n")
        self.check(NOW)
        self.ps = (0, PS_ONE_DESK)
        self.assertEqual([], self.check(NOW + timedelta(seconds=30)))

    def test_the_apis_launcher_shell_is_not_a_second_desk(self):
        # The desk restarted the API itself (24 Sep 11:28): its api_start left a bash with
        # desk.sh's argv, ppid 1, as the parent of `dotnet run` until the next restart.
        for pid in (32212, None):
            with self.subTest(pidfile=pid):
                self.fresh()
                if pid:
                    self.pidfile(pid)
                self.ps = (0, PS_ONE_DESK + PS_API_LAUNCHER)
                for i in range(4):
                    self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_a_dead_desk_is_seen_past_the_apis_launcher(self):
        for pid in (32212, None):
            with self.subTest(pidfile=pid):
                self.fresh()
                if pid:
                    self.pidfile(pid)                 # left behind: the desk died without its trap
                self.ps = (0, "    1     0     1 10-00:00:00 /sbin/init\n" + PS_API_LAUNCHER)
                self.assertEqual([], self.check(NOW))
                f = self.only(self.check(NOW + timedelta(seconds=30)), "desk-down")
                text = " ".join(f.evidence)
                if pid:
                    self.assertIn("desk.pid names 32212, which is not a running desk.sh", text)
                self.assertIn("pid 2096600: carries desk.sh's command line but is the parent of `dotnet run`", text)
                self.assertNotIn("kill", f.suggestion)

    def test_the_launcher_is_recognised_without_session_ids_on_macos(self):
        # macOS ps has no "sid" keyword: it exits 1 and prints the table without the column.
        self.ps = (1, without_sid("    1     0     1 10-00:00:00 /sbin/launchd\n" + PS_API_LAUNCHER))
        self.ps_plain = (0, self.ps[1])
        self.check(NOW)
        f = self.only(self.check(NOW + timedelta(seconds=30)), "desk-down")
        self.assertIn("pid 2096600", " ".join(f.evidence))
        self.ps = (1, without_sid(PS_ONE_DESK + PS_API_LAUNCHER))
        self.ps_plain = (0, self.ps[1])
        for i in range(2, 5):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_a_daemon_that_is_not_its_own_session_leader_is_not_a_loop(self):
        self.ps = (0, PS_ONE_DESK + f"2096600     1 32212 1-02:00:00 {DESK}\n")
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_the_desk_building_the_api_is_still_the_desk(self):
        # deploy_if_behind runs `dotnet build` straight from the loop: not api_start's launcher.
        self.ps = (0, PS_ONE_DESK
                   + "41900 32212 32212 00:20 dotnet build src/AlgoTrading.Api -v q --nologo\n")
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_the_servers_process_table_of_27_sep_is_quiet(self):
        # Read from the server: the desk, and market-open.sh's own leftover launcher of 25 Sep.
        self.pidfile(32212)
        self.ps = (0, "      1       0       1 12-01:00:00 /sbin/init\n"
                      f"  32212       1   32212    17:55:09 {DESK}\n"
                      "3202214       1 2000020  1-16:07:51 bash ./scripts/market-open.sh\n"
                      "3202215 3202214 2000020  1-16:07:51 dotnet run --project src/AlgoTrading.Api "
                      "--no-launch-profile\n"
                      "3202350 3202215 2000020  1-16:07:44 /home/ubuntu/algorithmic-trading-engine/src/"
                      "AlgoTrading.Api/bin/Debug/net10.0/AlgoTrading.Api\n")
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_a_foreground_desk_in_a_terminal_is_a_desk(self):
        # Not a session leader (the terminal's shell is): only --daemon copies must be.
        self.pidfile(5100)
        self.ps = (0, "    1     0     1 10-00:00:00 /sbin/init\n"
                      " 5000     1  5000 02:00:00 -bash\n"
                      " 5100  5000  5000 01:00:00 bash ./scripts/desk.sh\n")
        for i in range(3):
            self.assertEqual([], self.check(NOW + timedelta(seconds=30 * i)))

    def test_no_ps_means_nothing_to_say(self):
        self.ps = (1, "")
        self.check(NOW)
        self.assertEqual([], self.check(NOW + timedelta(seconds=30)))


class RobustnessTests(HealthCase):
    def test_a_broken_rule_is_reported_and_the_others_still_run(self):
        def broken(_path):
            raise RuntimeError("statvfs exploded")
        agent = HealthAgent(http_status=self._http_status, read_text=lambda p: self.files.get(p),
                            disk_usage=broken, cpu_count=lambda: 2, device_of=lambda p: 1)
        self.files["/proc/meminfo"] = "MemTotal: 7962896 kB\nMemAvailable: 102400 kB\n"
        with self.assertLogs("sentinel.agents.health", level="ERROR"):
            self.check(NOW, agent=agent)
            found = self.check(NOW + timedelta(seconds=30))
        self.assertEqual(["check-failed", "memory-low"], self.rules(found))
        failed = self.only(found, "check-failed")
        self.assertEqual("health:check-failed:disk", failed.fingerprint)
        self.assertIn("statvfs exploded", failed.summary)
        self.assertTrue(any("broken" in e for e in failed.evidence), failed.evidence)

    def test_it_only_ever_runs_read_only_commands(self):
        self.files["/proc/meminfo"] = "MemTotal: 7962896 kB\nMemAvailable: 102400 kB\n"
        self.files["/proc/loadavg"] = "9.0 9.0 9.0 1/1 1\n"
        for i in range(3):
            self.check(NOW + timedelta(seconds=30 * i))
        for args in self.run_calls:
            self.assertIn(args[:2], (["docker", "ps"], ["ps", "-eo"]), args)


if __name__ == "__main__":
    unittest.main()
