import _bootstrap  # noqa: F401

import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

from sentinel.agents.logs import LogsAgent, _may_hold_secret, normalise
from sentinel.engine import SentinelEngine
from sentinel.model import Severity
from sentinel.notify import redact
from sentinel.store import MemoryIncidentStore
from _sentinel_fakes import RecordingNotifier, clock_ticks, make_context

NOW = datetime(2026, 9, 24, 6, 0, tzinfo=timezone.utc)  # Thursday 11:30 IST, market open
B = "      "  # the .NET console logger's body indent


def info(category, text):
    return f"info: {category}[0]\n{B}{text}\n"


def warn(category, text):
    return f"warn: {category}[0]\n{B}{text}\n"


REG = "AlgoTrading.Api.Services.StrategyProcessRegistry"
CTRL = "AlgoTrading.Api.Services.StrategyRunControl"
FEEDS = "AlgoTrading.Api.Services.FeedSupervisor"
LOGIN_429 = "logs:rate-limited:/api/userauth/login"


def died(run, strategy, underlying, reason):
    """The API's two lines for a run that exited on its own (StrategyProcessRegistry, then StrategyRunControl)."""
    return (warn(REG, f"Strategy 688130555 ({strategy}) run {run} on {underlying} exited on its own: {reason}")
            + info(CTRL, f"Strategy 688130555 ({strategy}) run {run} on {underlying} stopped: {reason} "
                         f"(by runner, flattened 0)"))

# 24 Sep, run 215: the runner's traceback arrives one API log entry per line,
# interleaved with another runner's status line.
RUN_215_DIES = (
    info(REG, "[strategy:Fulcrum:NIFTY] WARNING: could not load parameters for run 215: 429 Client Error: Too Many "
              "Requests for url: http://localhost:5025/api/UserAuth/login. Using strategy defaults.")
    + warn(REG, "[strategy:Fulcrum:NIFTY:err] Traceback (most recent call last):")
    + warn(REG, '[strategy:Fulcrum:NIFTY:err]   File "/srv/strategies/execution_runner.py", line 529, in <module>')
    + warn(REG, "[strategy:Fulcrum:NIFTY:err]     expiries = api.get_expiries(args.underlying)")
    + info(REG, "[strategy:GhostTangentCrossings:SENSEX] [STATUS] SENSEX spot=74205.28 atm=74200 open_groups=0")
    + warn(REG, '[strategy:Fulcrum:NIFTY:err]   File "/srv/core/api_client.py", line 126, in request')
    + warn(REG, '[strategy:Fulcrum:NIFTY:err]     headers["Authorization"] = f"Bearer {self._provider.get()}"')
    + warn(REG, "[strategy:Fulcrum:NIFTY:err] requests.exceptions.HTTPError: 429 Client Error: Too Many Requests "
                "for url: http://localhost:5025/api/UserAuth/login")
    + warn(REG, "Strategy 688130555 (Fulcrum) run 215 on NIFTY exited on its own: Runner exited (code 1): "
                "requests.exceptions.HTTPError: 429 Client Error: Too Many Requests for url: "
                "http://localhost:5025/api/UserAuth/login")
    + info(CTRL, "Strategy 688130555 (Fulcrum) run 215 on NIFTY stopped: Runner exited (code 1): "
                 "requests.exceptions.HTTPError: 429 Client Error: Too Many Requests for url: "
                 "http://localhost:5025/api/UserAuth/login (by runner, flattened 0)")
)

# 24 Sep 11:27, logs/engine/dhan-feed-2096838.log
DHAN_LOOP = """[dhan] connected: wss://api-feed.dhan.co
[dhan] SUBSCRIBED: 321 symbol(s), 198 beyond the watchlist
[dhan] error: Connection to remote host was lost.
[dhan] disconnected: connection dropped without a reason
[dhan] WATCHDOG: socket down for 20s — forcing a full reconnect.
[dhan] closing the connection
[dhan] 2 reconnect(s) carried no ticks — waiting 10s before the next attempt so the vendor does not block the account.
[dhan] connecting ...
"""

EF_VALUE_TOO_LONG = (
    "fail: Microsoft.EntityFrameworkCore.Database.Command[20102]\n"
    f"{B}Failed executing DbCommand (3ms) [Parameters=[@p0='?'], CommandType='Text', CommandTimeout='30']\n"
    f'{B}INSERT INTO alert_events ("DedupeKey", "Message") VALUES (@p0, @p1)\n'
    "fail: Microsoft.EntityFrameworkCore.Update[10000]\n"
    f"{B}An exception occurred in the database while saving changes for context type 'TradingDbContext'.\n"
    f"{B}Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.\n"
    f"{B} ---> Npgsql.PostgresException (0x80004005): 22001: value too long for type character varying(1000)\n"
    f"{B}   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)\n"
    "fail: AlgoTrading.Api.Services.AlertSubscriberService[0]\n"
    f"{B}Failed to process alert event from Redis.\n"
    f"{B}Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.\n"
    f"{B} ---> Npgsql.PostgresException (0x80004005): 22001: value too long for type character varying(1000)\n"
    f"{B}   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)\n"
)

WATCHLIST_RACE = (
    "fail: Microsoft.EntityFrameworkCore.Update[10000]\n"
    f"{B}An exception occurred in the database while saving changes for context type 'TradingDbContext'.\n"
    f"{B}Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.\n"
    f'{B} ---> Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint '
    f'"IX_live_watchlist_Symbol"\n'
)


class LogsAgentTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.logs = self.tmp / "logs"
        (self.logs / "engine").mkdir(parents=True)
        self.clock = [NOW]
        self.ctx = make_context(self.tmp)
        self.ctx.clock = lambda: self.clock[0]
        self.agent = LogsAgent()

    def write(self, name, text=""):
        (self.logs / name).write_text(text, encoding="utf-8")

    def append(self, name, text):
        with open(self.logs / name, "a", encoding="utf-8") as fh:
            fh.write(text)

    def check(self):
        self.ctx.fresh_cycle()
        return {f.fingerprint: f for f in self.agent.check(self.ctx)}

    def advance(self, seconds):
        self.clock[0] = self.clock[0] + timedelta(seconds=seconds)

    def idle(self, seconds, step=300):
        """Let time pass with Sentinel watching (a check every ``step`` s); the last check's findings."""
        found = {}
        while seconds > 0:
            self.advance(min(step, seconds))
            seconds -= step
            found = self.check()
        return found

    def start_watching(self, *names):
        """Files exist before Sentinel's first look, which starts at their end."""
        for name in names:
            if not (self.logs / name).exists():
                self.write(name, "")
        self.assertEqual({}, self.check())


class TailingTests(LogsAgentTestCase):
    def test_the_first_check_starts_at_the_end_and_then_reads_what_is_appended(self):
        self.write("api.log", RUN_215_DIES)
        self.assertEqual({}, self.check(), "history is not replayed as incidents")

        self.append("api.log", RUN_215_DIES.replace("215", "216"))
        found = self.check()

        self.assertEqual(["run 216 (Fulcrum on NIFTY)"], found[LOGIN_429].extra["runs"])

    def test_a_partly_written_line_waits_for_its_newline(self):
        self.start_watching("desk.log")
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; noth")
        self.assertEqual({}, self.check())
        self.append("desk.log", "ing was started.\n")
        found = self.check()
        [finding] = [f for f in found.values() if f.rule == "job-failed"]
        self.assertIn("nothing was started", finding.evidence[0])

    def test_a_rotated_api_log_is_finished_and_the_new_one_read_from_its_start(self):
        self.start_watching("api.log")
        self.append("api.log", RUN_215_DIES)
        os.rename(self.logs / "api.log", self.logs / "api-until-20260924-112803.log")
        self.write("api.log", RUN_215_DIES.replace("215", "216"))

        runs = self.check()[LOGIN_429].extra["runs"]

        self.assertIn("run 215 (Fulcrum on NIFTY)", runs, "the old file's last lines were read")
        self.assertIn("run 216 (Fulcrum on NIFTY)", runs, "the new file was read from its start")

    def test_a_truncated_file_is_read_again_from_its_start(self):
        self.write("desk.log", "08:00:00  desk is fine\n" * 50)
        self.check()
        self.write("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        self.assertEqual(["job-failed"], [f.rule for f in self.check().values()])

    def test_a_file_that_appears_while_watching_is_read_from_its_start(self):
        self.start_watching("api.log")
        self.write("engine/runner-300-4242.log", "[NIFTY] FEED STALLED — no ticks for 95s while the market is open.\n")
        self.assertIn("logs:feed-stalled", self.check())

    def test_the_market_open_log_of_today_is_watched(self):
        self.start_watching("api.log")
        self.write("market-open-2026-09-23.log", "09:12:10  FAILED: yesterday's problem\n")
        self.write("market-open-2026-09-24.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        [finding] = self.check().values()
        self.assertIn("no FYERS sign-in", finding.title)
        self.assertEqual("logs/market-open-2026-09-24.log · market-open.sh", finding.where)

    def test_at_most_the_newest_two_megabytes_are_read_in_one_check(self):
        self.start_watching("desk.log")
        with mock.patch("sentinel.agents.logs.MAX_READ_BYTES", 400):
            self.append("desk.log", "08:00:00  FAILED: an old failure nobody will hear about\n"
                        + "09:00:00  an ordinary line of the morning job\n" * 40
                        + "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
            titles = [f.title for f in self.check().values()]
        self.assertEqual(1, len(titles))
        self.assertIn("no FYERS sign-in", titles[0])

    def test_a_missing_logs_directory_is_not_an_error(self):
        empty = make_context(Path(tempfile.mkdtemp()))
        self.assertEqual([], LogsAgent().check(empty))

    def test_it_runs_every_30_seconds_and_a_burst_is_one_incident(self):
        self.assertEqual(30, LogsAgent.interval_seconds)
        self.assertEqual(4, LogsAgent.resolve_after)

        store, notifier = MemoryIncidentStore(), RecordingNotifier()
        engine = SentinelEngine([self.agent], store, notifier, self.ctx, monotonic=clock_ticks())
        self.write("api.log")
        engine.run_due()
        self.append("api.log", RUN_215_DIES)
        self.advance(30)
        engine.run_due()
        self.append("api.log", RUN_215_DIES)  # the same burst again: the same incident
        self.advance(30)
        engine.run_due()
        [row] = store.rows()
        self.assertEqual(("rate-limited", 2), (row["rule"], row["occurrences"]))
        self.assertEqual(1, len(notifier.sent))

    def test_a_desk_failure_stays_open_until_it_is_dealt_with_not_two_minutes(self):
        store, notifier = MemoryIncidentStore(), RecordingNotifier()
        engine = SentinelEngine([self.agent], store, notifier, self.ctx, monotonic=clock_ticks())
        self.clock[0] = datetime(2026, 9, 24, 3, 40, tzinfo=timezone.utc)  # 09:10 IST
        self.write("desk.log")
        engine.run_due()
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        for _ in range(2 * 120):  # two hours of checks
            self.advance(30)
            engine.run_due()
        [row] = store.rows()
        self.assertEqual("open", row["status"], "nothing has been started: it is not resolved")
        self.assertEqual(1, len(notifier.sent))

        # The owner signs in and runs the morning job again.
        self.append("desk.log", "11:15:00  === market-open: Thursday 24 September 2026 ===\n")
        for _ in range(5):
            self.advance(30)
            engine.run_due()
        self.assertEqual("resolved", store.rows()[0]["status"])

    def test_a_desk_failure_nobody_deals_with_is_let_go_at_the_close(self):
        self.clock[0] = datetime(2026, 9, 24, 3, 40, tzinfo=timezone.utc)  # 09:10 IST
        self.start_watching("desk.log")
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        self.assertEqual(["job-failed"], [f.rule for f in self.check().values()])
        self.assertEqual(["job-failed"], [f.rule for f in self.idle(6 * 3600).values()], "15:10 IST: still true")
        self.assertEqual({}, self.idle(1800), "15:40 IST: the session is over")

    def test_lines_written_while_another_agent_held_the_engine_are_news(self):
        # The engine is single-threaded: a weekly dependency scan could hold it 13 minutes, and the
        # morning's failure written meanwhile was learned as history — never reported.
        self.start_watching("desk.log")
        self.advance(13 * 60)
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        self.assertEqual(["job-failed"], [f.rule for f in self.check().values()])

    def test_the_same_gap_after_a_restart_is_history(self):
        self.start_watching("desk.log")
        self.advance(13 * 60)
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        self.agent = LogsAgent()   # a new process: Sentinel was down
        self.assertEqual({}, self.check())

    def test_lines_written_while_sentinel_was_down_are_history_not_news(self):
        self.start_watching("api.log", "desk.log")
        self.advance(6 * 3600)  # Sentinel was down; meanwhile:
        self.append("api.log", info(REG, "[strategy:Fulcrum:BANKNIFTY] [BANKNIFTY] FEED STALLED — no ticks for 91s "
                                         "while the market is open."))
        self.append("desk.log", "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n")
        os.rename(self.logs / "api.log", self.logs / "api-until-20260924-120000.log")
        self.write("api.log", RUN_215_DIES)
        self.assertEqual({}, self.check(), "six hours late is not news")

        self.advance(30)
        self.append("desk.log", "12:00:30  FAILED: no FYERS sign-in by 12:00 IST; nothing was started.\n")
        self.assertEqual(["job-failed"], [f.rule for f in self.check().values()], "watching again")


class SignatureTests(LogsAgentTestCase):
    def test_the_sign_in_limiter_kills_a_runner(self):
        self.start_watching("api.log")
        self.append("api.log", RUN_215_DIES)
        found = self.check()

        # The run's death is the trading agent's; the logs report its cause, once.
        [limited] = found.values()
        self.assertEqual(LOGIN_429, limited.fingerprint)
        self.assertEqual(Severity.HIGH, limited.severity)
        self.assertIn("It killed 1 run: run 215 (Fulcrum on NIFTY).", limited.summary)
        self.assertIn("d70d1c9", limited.suggestion)
        self.assertEqual("in logs/api.log", limited.evidence[-1])
        self.assertLessEqual(len(limited.evidence), 5)

    def test_the_24_sep_boot_is_one_incident_not_one_per_run(self):
        # 08:49–08:51: the sign-in limiter killed 16 runs of two accounts.
        self.start_watching("api.log")
        runs = [(str(200 + i), ("Fulcrum", "Ghost", "SmcStructureBreak", "ChainFlowBuy")[i % 4],
                 ("NIFTY", "BANKNIFTY", "SENSEX", "CRUDEOIL")[i % 4]) for i in range(16)]
        for run, strategy, underlying in runs:
            self.append("api.log", RUN_215_DIES.replace("215", run).replace("Fulcrum", strategy)
                        .replace("NIFTY", underlying))
        [finding] = self.check().values()
        self.assertEqual(LOGIN_429, finding.fingerprint)
        self.assertEqual(16, len(finding.extra["runs"]))
        self.assertIn("It killed 16 runs: run 200 (Fulcrum on NIFTY)", finding.summary)
        self.assertIn("and 8 more", finding.summary)

        self.append("api.log", RUN_215_DIES.replace("215", "299"))  # the next check: the same incident
        self.advance(30)
        self.assertEqual([LOGIN_429], list(self.check()))
        self.assertEqual(17, len(self.check()[LOGIN_429].extra["runs"]), "held, it remembers the runs")
        self.assertEqual(normalise("05:59:29 | runner exited with code 1"),
                         normalise("06:01:02 | runner exited with code 137"))

    def test_a_run_killed_by_a_python_exception_is_one_incident_with_its_traceback(self):
        self.start_watching("api.log")
        err = "[strategy:Ghost:NIFTY:err]"
        self.append("api.log",
                    warn(REG, f"{err} Traceback (most recent call last):")
                    + warn(REG, f'{err}   File "/srv/strategies/ghost.py", line 88, in on_tick')
                    + warn(REG, f"{err}     ltp = quote['ltp']")
                    + warn(REG, f"{err} KeyError: 'ltp'")
                    + died(300, "Ghost", "NIFTY", "Runner exited (code 1): KeyError: 'ltp'"))
        [finding] = self.check().values()
        self.assertEqual(("python-traceback", Severity.HIGH), (finding.rule, finding.severity))
        self.assertEqual("Python error in Ghost on NIFTY: KeyError", finding.title)
        self.assertIn("It killed 1 run: run 300 (Ghost on NIFTY).", finding.summary)

        self.advance(30)
        [again] = self.check().values()  # the traceback, finished on the quiet check, is the same incident
        self.assertEqual(finding.fingerprint, again.fingerprint)
        self.assertIn('File "/srv/strategies/ghost.py", line 88, in on_tick', again.evidence)
        self.assertIn("run 300 (Ghost on NIFTY)", again.summary)

    def test_runs_that_die_of_an_unknown_cause_are_one_incident_per_cause(self):
        self.start_watching("api.log")
        self.append("api.log", died(301, "Fulcrum", "NIFTY", "Runner exited (code 137)")
                    + died(302, "Fulcrum", "SENSEX", "Runner exited (code 137)"))
        [finding] = self.check().values()
        self.assertEqual(("runner-crashed", Severity.HIGH), (finding.rule, finding.severity))
        self.assertEqual("Strategy runners died on their own: exit code 137", finding.title)
        self.assertEqual(["run 301 (Fulcrum on NIFTY)", "run 302 (Fulcrum on SENSEX)"], finding.extra["runs"])
        self.assertIn("out-of-memory", finding.suggestion)

        self.assertIn(finding.fingerprint, self.idle(3600), "a dead run stays dead: held, not resolved")

        self.append("api.log", died(303, "Ghost", "NIFTY", "Runner exited (code 1): [NIFTY] FATAL: no expiry"))
        self.assertEqual(2, len(self.idle(30)), "another cause is another incident")

    def test_a_run_that_died_because_the_api_was_restarting_is_not_the_logs_to_report(self):
        self.start_watching("api.log")
        self.append("api.log", died(304, "Ghost", "NIFTY", "Runner exited (code 1): requests.exceptions."
                                    "ConnectionError: HTTPConnectionPool(host='localhost', port=5025): Max retries "
                                    "exceeded with url: /api/Strategy/runs"))
        self.assertEqual({}, self.check())

    def test_a_clean_exit_is_not_a_crash(self):
        self.start_watching("api.log")
        self.append("api.log", warn(REG, "Strategy 1 (Ghost) run 300 on NIFTY exited on its own: Runner exited (code 0)"))
        self.assertEqual({}, self.check())

    def test_the_dhan_reconnect_loop_is_critical_while_a_market_is_open(self):
        self.start_watching("api.log")
        self.write("engine/dhan-feed-2096838.log", DHAN_LOOP)
        finding = self.check()["logs:feed-reconnect-loop:dhan"]
        self.assertEqual(Severity.CRITICAL, finding.severity)
        self.assertIn("/api/Feeds/fyers/start", finding.suggestion)
        self.assertTrue(any("carried no ticks" in e for e in finding.evidence))

    def test_three_lost_connections_in_one_check_are_a_loop_even_without_the_backoff_line(self):
        self.start_watching("api.log")
        lost = info("AlgoTrading.Api.Services.FeedSupervisor",
                    "[Dhan feed] [dhan] error: Connection to remote host was lost.")
        self.append("api.log", lost * 2)
        self.assertEqual({}, self.check(), "two drops that reconnect are ordinary")
        self.append("api.log", lost * 3)
        self.assertIn("logs:feed-reconnect-loop:dhan", self.check())

    def test_a_reconnect_loop_with_every_market_shut_is_only_medium(self):
        self.clock[0] = datetime(2026, 9, 26, 6, 0, tzinfo=timezone.utc)  # Saturday
        self.start_watching("api.log")
        # 16 Sep: yesterday's feed on a dead token, reconnecting until Dhan blocked the account.
        self.write("engine/dhan-feed-2096838.log", DHAN_LOOP.replace("2 reconnect(s)", "3 reconnect(s)"))
        finding = self.check()["logs:feed-reconnect-loop:dhan"]
        self.assertEqual(Severity.MEDIUM, finding.severity)
        self.assertIn("No market is open right now", finding.summary)

    def test_a_tickless_reconnect_or_two_with_every_market_shut_is_the_markets_silence(self):
        self.clock[0] = datetime(2026, 9, 25, 18, 2, tzinfo=timezone.utc)  # Friday 23:32 IST, MCX just shut
        self.start_watching("engine/dhan-feed-2096838.log")
        for n in (1, 2):
            self.append("engine/dhan-feed-2096838.log",
                        f"[dhan] {n} reconnect(s) carried no ticks — waiting {5 * 2 ** (n - 1)}s before the next "
                        "attempt so the vendor does not block the account.\n")
            self.advance(30)
            self.assertEqual({}, self.check())
        self.append("engine/dhan-feed-2096838.log",
                    "[dhan] 3 reconnect(s) carried no ticks — waiting 20s before the next attempt so the vendor "
                    "does not block the account.\n")
        self.advance(30)
        self.assertEqual(Severity.MEDIUM, self.check()["logs:feed-reconnect-loop:dhan"].severity)

    def test_with_a_market_open_the_second_tickless_reconnect_in_a_row_is_a_loop(self):
        # Until 27 Sep one tickless reconnect in market hours paged CRITICAL "reconnecting in a loop".
        self.start_watching("engine/dhan-feed-2096838.log")
        self.append("engine/dhan-feed-2096838.log",
                    "[dhan] 1 reconnect(s) carried no ticks — waiting 5s before the next attempt so the vendor does "
                    "not block the account.\n")
        self.assertEqual({}, self.check(), "one is a hiccup; health's feed-silent covers a real outage at 90 s")
        self.append("engine/dhan-feed-2096838.log",
                    "[dhan] 2 reconnect(s) carried no ticks — waiting 10s before the next attempt so the vendor does "
                    "not block the account.\n")
        self.advance(30)
        self.assertEqual(Severity.CRITICAL, self.check()["logs:feed-reconnect-loop:dhan"].severity)

    def test_a_single_tickless_reconnect_that_recovers_is_never_reported(self):
        store, notifier = MemoryIncidentStore(), RecordingNotifier()
        engine = SentinelEngine([self.agent], store, notifier, self.ctx, monotonic=clock_ticks())
        self.write("engine/dhan-feed-2096838.log")
        engine.run_due()
        self.append("engine/dhan-feed-2096838.log",
                    "[dhan] WATCHDOG: socket down for 21s — forcing a full reconnect.\n"
                    "[dhan] 1 reconnect(s) carried no ticks — waiting 5s before the next attempt so the vendor does "
                    "not block the account.\n[dhan] connecting ...\n[dhan] connected: wss://api-feed.dhan.co\n")
        for _ in range(6):
            self.advance(30)
            engine.run_due()
        self.assertEqual([], notifier.sent)

    def test_a_feed_loop_is_held_open_through_its_announced_wait(self):
        self.start_watching("engine/dhan-feed-1.log")
        self.append("engine/dhan-feed-1.log",
                    "[dhan] 7 reconnect(s) carried no ticks — waiting 300s before the next attempt so the vendor "
                    "does not block the account.\n")
        self.assertIn("logs:feed-reconnect-loop:dhan", self.check())
        self.advance(240)
        self.assertIn("logs:feed-reconnect-loop:dhan", self.check(), "still inside the 300 s wait")
        self.advance(240)
        self.assertEqual({}, self.check())

    def test_a_flapping_feed_is_one_incident_that_closes_an_hour_after_the_last_stall(self):
        self.start_watching("api.log")
        stalled = info(REG, "[strategy:Fulcrum:BANKNIFTY] [BANKNIFTY] FEED STALLED — no ticks for 100s while "
                            "the market is open.")
        recovered = info(REG, "[strategy:Fulcrum:BANKNIFTY] [BANKNIFTY] FEED RECOVERED after 0s.")
        self.append("api.log", stalled + stalled.replace("Fulcrum", "Ghost"))
        finding = self.check()["logs:feed-stalled"]
        self.assertEqual(Severity.HIGH, finding.severity)
        self.assertIn("BANKNIFTY", finding.summary)
        self.assertIn("Data → Feeds", finding.suggestion)

        self.advance(300)
        self.assertIn("logs:feed-stalled", self.check(), "runners repeat themselves only every 10 minutes")

        self.append("api.log", recovered)  # 25 Sep: back in two minutes, gone again twenty minutes later
        self.advance(30)
        self.assertIn("logs:feed-stalled", self.check())
        self.idle(1200)
        self.append("api.log", stalled + recovered)
        self.assertIn("recovered", self.check()["logs:feed-stalled"].summary)

        self.assertIn("logs:feed-stalled", self.idle(3000))
        self.assertEqual({}, self.idle(700))

    def test_a_vendor_rejecting_the_token_is_reported_per_vendor(self):
        self.start_watching("api.log")
        self.append("api.log", info(
            "AlgoTrading.Api.Services.FeedSupervisor",
            "[Dhan feed] [dhan] universe: 209 symbol(s) from the API; warnings: [\"Dhan's last prices were not "
            "available (Dhan rejected the access token or client id (808: Authentication Failed - Client ID or "
            "Token invalid).\"]"))
        self.write("engine/ingestor-77.log", "[fyers] {'type': 'cn', 'code': -99, 'message': 'Token is expired'}\n")
        found = self.check()
        self.assertEqual(Severity.HIGH, found["logs:vendor-auth:dhan"].severity)
        self.assertIn("24 hours", found["logs:vendor-auth:dhan"].suggestion)
        self.assertIn("10 Sep", found["logs:vendor-auth:fyers"].suggestion)

    def test_the_desks_own_401_is_not_a_vendor(self):
        self.start_watching("api.log")
        self.append("api.log", info(REG, "[strategy:Ghost:NIFTY] 401 Client Error: Unauthorized for url: "
                                         "http://localhost:5025/api/Strategy/runs"))
        self.assertNotIn("vendor-auth", {f.rule for f in self.check().values()})

    def test_the_order_rate_limit_is_one_finding_per_run_not_an_api_error(self):
        self.start_watching("api.log")
        self.append("api.log", (
            "fail: Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware[1]\n"
            f"{B}An unhandled exception has occurred while executing the request.\n"
            f"{B}AlgoTrading.Application.Exceptions.RiskViolationException: RATE LIMIT EXCEEDED: More than 50 orders "
            "placed in the last minute for run 246 (leg SELL 2).\n"
            f"{B}   at AlgoTrading.Infrastructure.Services.RiskManagementService.RejectOrderAsync()\n")
            + info(REG, '[strategy:Fulcrum:NIFTY] SIGNAL REFUSED by the API: {"error":"RATE LIMIT EXCEEDED: More '
                        'than 50 orders placed in the last minute for run 246 (leg SELL 2)."}'))
        found = self.check()
        self.assertEqual(["logs:order-rate-limit"], list(found))
        self.assertIn("25 Sep", found["logs:order-rate-limit"].suggestion)
        self.assertIn("from run 246.", found["logs:order-rate-limit"].summary)

        self.append("api.log", info(REG, '[strategy:Fulcrum:SENSEX] SIGNAL REFUSED by the API: {"error":"RATE LIMIT '
                                         'EXCEEDED: More than 50 orders placed in the last minute for run 236."}'))
        self.advance(30)
        [churn] = self.check().values()  # another run of the same churn: the same incident
        self.assertIn("from run 246, 236.", churn.summary)

    def test_a_daemon_that_exits_on_its_own_is_reported_but_a_stopped_one_is_not(self):
        self.start_watching("api.log")
        self.append("api.log", info("AlgoTrading.Api.Services.IngestorSupervisor", "ingestor pid 1675369 exited with code 137.")
                    + info(FEEDS, "Dhan feed pid 2096838 exited with code 0."))
        self.assertEqual({}, self.check())
        self.append("api.log", info(FEEDS, "Dhan feed pid 2096838 exited with code 1."))
        self.assertIn("logs:process-exited:dhan-feed", self.check())

    def test_a_daemon_that_exited_is_held_until_it_starts_again(self):
        self.start_watching("api.log")
        self.append("api.log", info(FEEDS, "Dhan feed pid 2096838 exited with code 1."))
        self.check()
        self.assertIn("logs:process-exited:dhan-feed", self.idle(1800), "not running is still true")
        self.append("api.log", info(FEEDS, "[ingestor] [fyers] STARTING LIVE FEED (live) — source python-live-ingestor"))
        self.assertIn("logs:process-exited:dhan-feed", self.idle(30), "another daemon starting changes nothing")
        self.append("api.log", info(FEEDS, "[Dhan feed] [dhan] STARTING LIVE FEED (live) — source python-dhan-feed"))
        self.assertEqual({}, self.idle(30))

    def test_telegram_refusing_the_desks_alerts_is_reported(self):
        # 24–26 Sep: 394 of these; the HttpClient's own "- 429" lines are info and say it no better.
        self.start_watching("api.log")
        refused = (info("System.Net.Http.HttpClient.telegram.ClientHandler",
                        "Received HTTP response headers after 302.7478ms - 429")
                   + warn("AlgoTrading.Infrastructure.Services.TelegramSender", "Telegram refused a message: HTTP 429."))
        self.append("api.log", refused * 3)
        [finding] = self.check().values()
        self.assertEqual(("logs:rate-limited:telegram", Severity.HIGH), (finding.fingerprint, finding.severity))
        self.assertIn("alerts are being lost", finding.title)
        self.assertIn("candle-pattern", finding.suggestion)
        self.assertEqual(["Telegram refused a message: HTTP 429.", "in logs/api.log"], finding.evidence)
        self.assertIn("logs:rate-limited:telegram", self.idle(3 * 3600), "bursts all day are one incident")
        self.assertEqual({}, self.idle(2 * 3600), "15:30 IST: let go at the close")


class DeskTests(LogsAgentTestCase):
    def test_desk_failures_and_warnings(self):
        self.start_watching("desk.log")
        self.append("desk.log",
                    "08:45:39  WARN: the Dhan token ends at 09:21 IST, before the evening session closes\n"
                    "09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n"
                    "09:12:10  --- stopping here; nothing further was started ---\n"
                    "09:30:00  WARN: API health check failed (1/3)\n")
        found = self.check()
        rules = sorted(f.rule for f in found.values())
        self.assertEqual(["desk-warning", "job-failed"], rules)

        failed = next(f for f in found.values() if f.rule == "job-failed")
        self.assertEqual(Severity.HIGH, failed.severity)
        self.assertIn("at 09:12 IST", failed.summary, "the desk's own IST time, not when it was read")
        self.assertIn("Sign in to FYERS", failed.suggestion)

        warning = next(f for f in found.values() if f.rule == "desk-warning")
        self.assertEqual(Severity.MEDIUM, warning.severity)
        self.assertIn("Dhan token", warning.suggestion)

    def test_the_same_warning_in_desk_log_and_the_market_open_log_is_one_finding(self):
        self.start_watching("desk.log")
        line = "08:45:39  WARN: the Dhan token ends at 09:21 IST, before the evening session closes\n"
        self.append("desk.log", line)
        self.write("market-open-2026-09-24.log", line)
        [finding] = self.check().values()
        self.assertEqual("in logs/desk.log, logs/market-open-2026-09-24.log", finding.evidence[-1])
        self.assertEqual(2, len(finding.evidence), "the same line twice is one piece of evidence")

    def test_desk_sh_started_several_times_within_seconds(self):
        self.start_watching("desk.log")
        self.append("desk.log", "".join(
            f"06:58:0{s}  === desk started (pid 3{s}672, background) — API http://localhost:5025, open at 0845 ===\n"
            f"06:58:0{s}  infra ...\n" for s in (1, 5, 8)))
        finding = self.check()["logs:desk-relaunched"]
        self.assertEqual(Severity.MEDIUM, finding.severity)
        self.assertEqual(4, len(finding.evidence))  # three launches + the file

    def test_dated_desk_lines_are_read_like_the_old_ones(self):
        # say() stamps "%F %T" since 28 Sep 2026; the rules read the text after the stamp either way.
        self.start_watching("desk.log")
        self.append("desk.log",
                    "2026-09-24 09:12:10  FAILED: no FYERS sign-in by 09:12 IST; nothing was started.\n"
                    "".join(f"2026-09-24 06:58:0{s}  === desk started (pid 3{s}672, background) ===\n"
                            for s in (1, 5)))
        found = self.check()
        failed = next(f for f in found.values() if f.rule == "job-failed")
        self.assertIn("at 09:12 IST", failed.summary)
        self.assertIn("Sign in to FYERS", failed.suggestion)
        self.assertIn("logs:desk-relaunched", found)

    def test_one_desk_start_is_ordinary(self):
        self.start_watching("desk.log")
        self.append("desk.log", "06:58:01  === desk started (pid 31672, background) — API http://localhost:5025 ===\n")
        self.assertEqual({}, self.check())
        self.idle(3600)
        self.append("desk.log", "07:58:01  === desk started (pid 41672, background) — API http://localhost:5025 ===\n")
        self.assertEqual({}, self.check())

    def test_the_nightly_archive_failing_is_one_incident_held_until_the_next_run(self):
        # logs/archive-2026-09-26.log, verbatim: cron sends its output to /dev/null, so this is the only record.
        self.start_watching("desk.log")
        self.write("archive-2026-09-24.log",
                   "06:00:07  rclone cannot reach openfno-drive:openfno-archive: 2026/09/26 06:00:07 Failed to "
                   "create file system for \"openfno-drive:openfno-archive\": couldn't find root directory ID: Get "
                   "\"https://www.googleapis.com/drive/v3/files/root?alt=json&fields=id&prettyPrin (set up the "
                   "rclone remote first)\n"
                   "06:00:07  WARN: archive to Drive FAILED — see /home/ubuntu/algorithmic-trading-engine/logs/"
                   "archive-2026-09-24.log\n")
        [finding] = self.check().values()
        self.assertEqual(("job-failed", Severity.HIGH), (finding.rule, finding.severity))
        self.assertEqual("logs/archive-2026-09-24.log · archive-to-drive.sh", finding.where)
        self.assertIn("rclone's Drive authorisation needs renewing", finding.suggestion)
        self.assertIn("couldn't find root directory ID", finding.summary, "the reason is in the one incident")
        self.assertEqual(3, len(finding.evidence))  # the FAILED line, the rclone line, the file

        self.assertIn(finding.fingerprint, self.idle(20 * 3600, step=600), "still broken at the next evening")

    def test_a_good_archive_run_ends_the_failure(self):
        self.clock[0] = datetime(2026, 9, 24, 0, 25, tzinfo=timezone.utc)  # 05:55 IST
        self.start_watching("desk.log")
        self.write("archive-2026-09-24.log", "06:00:07  WARN: archive to Drive FAILED — see logs/archive-2026-09-24.log\n")
        [fingerprint] = self.check()
        self.clock[0] = datetime(2026, 9, 24, 18, 25, tzinfo=timezone.utc)  # 23:55 IST
        self.check()
        self.clock[0] = datetime(2026, 9, 25, 0, 25, tzinfo=timezone.utc)  # the next morning
        self.assertIn(fingerprint, self.check())
        self.write("archive-2026-09-25.log", "06:05:14  done: 0 failure(s)\n"
                                             "06:05:14  archive to Drive: ok, 3 file(s) verified; disk free 58G -> 58G\n")
        self.assertEqual({}, self.idle(30))

    def test_a_failed_build_is_held_until_a_clean_deploy(self):
        self.start_watching("desk.log")
        self.append("desk.log", "23:44:40  WARN: web build FAILED — the previous bundle is still being served\n")
        [fingerprint] = self.check()
        self.append("desk.log", "23:48:52  deploy: b2eee72 at 23:48 — console build FAILED;API rebuilt and restarted\n")
        self.assertIn(fingerprint, self.idle(3600), "a deploy that failed again ends nothing")
        self.append("desk.log", "23:52:01  deploy: 3c1d2e4 at 23:52 — console rebuilt\n")
        self.assertNotIn(fingerprint, self.idle(30))

    def test_a_setting_that_never_reached_the_api(self):
        self.start_watching("api.log")
        self.append("api.log", warn("AlgoTrading.Api.Services.TelegramSender",
                                    "Telegram is not configured (Telegram:BotToken / Telegram:ChatId)."))
        finding = next(iter(self.check().values()))
        self.assertEqual("not-configured", finding.rule)
        self.assertIn("appsettings.Local.json", finding.suggestion)


class ErrorGroupingTests(LogsAgentTestCase):
    def test_a_python_traceback_is_grouped_by_what_was_raised(self):
        self.start_watching("api.log")
        tb = ("Traceback (most recent call last):\n"
              '  File "/srv/strategies/fulcrum.py", line {n}, in on_tick\n'
              "    ltp = quote['ltp']\n"
              "KeyError: 'ltp'\n")
        self.write("engine/runner-300-1.log", tb.format(n=10) + "[STATUS] fine\n" + tb.format(n=99))
        found = self.check()
        self.assertEqual(1, len(found))
        finding = next(iter(found.values()))
        self.assertEqual("python-traceback", finding.rule)
        self.assertEqual(Severity.MEDIUM, finding.severity)
        self.assertEqual("Python error in run 300: KeyError", finding.title)
        self.assertIn("KeyError: 'ltp'", finding.evidence)

    def test_a_runners_own_stamped_log_is_read_stream_by_stream(self):
        # From 28 Sep a runner keeps its log from the first line, each line
        # stamped with a UTC time and the console's "|" / "!" stream marks,
        # and ends it with an EXIT line that restates the crash.
        self.start_watching("api.log")
        self.write("engine/runner-302-1.log",
                   "2026-09-28T04:00:00.000Z | [STATUS] NIFTY spot=25010 atm=25000 open_groups=1\n"
                   "2026-09-28T04:00:01.000Z ! Traceback (most recent call last):\n"
                   "2026-09-28T04:00:01.001Z | [STATUS] NIFTY spot=25012 atm=25000 open_groups=1\n"
                   '2026-09-28T04:00:01.002Z !   File "/srv/strategies/fulcrum.py", line 10, in on_tick\n'
                   "2026-09-28T04:00:01.003Z !     ltp = quote['ltp']\n"
                   "2026-09-28T04:00:01.004Z ! KeyError: 'ltp'\n"
                   "2026-09-28T04:00:01.005Z | EXIT code=1 reason=uncaught KeyError: 'ltp'\n")
        # The EXIT line is on stdout, so it does not close the stderr traceback:
        # a chained one may still follow, as on the API's console.
        self.assertEqual({}, self.check())
        [finding] = self.check().values()
        self.assertEqual("python-traceback", finding.rule)
        self.assertEqual("Python error in run 302: KeyError", finding.title)

    def test_interleaved_tracebacks_from_two_runners_are_put_back_together(self):
        self.start_watching("api.log")
        a, b = "[strategy:Fulcrum:NIFTY:err]", "[strategy:Ghost:SENSEX:err]"
        self.append("api.log",
                    warn(REG, f"{a} Traceback (most recent call last):")
                    + warn(REG, f"{b} Traceback (most recent call last):")
                    + warn(REG, f'{a}   File "/srv/a.py", line 1, in f')
                    + warn(REG, f'{b}   File "/srv/b.py", line 2, in g')
                    + warn(REG, f"{b} ZeroDivisionError: division by zero")
                    + warn(REG, f"{a} ValueError: bad strike 23250"))
        self.assertEqual({}, self.check(), "a chained traceback may still follow: wait one quiet check")
        titles = sorted(f.title for f in self.check().values())
        self.assertEqual(["Python error in Fulcrum on NIFTY: ValueError",
                          "Python error in Ghost on SENSEX: ZeroDivisionError"], titles)

    def test_a_chained_traceback_reports_what_was_finally_raised(self):
        self.start_watching("api.log")
        self.write("engine/chain-poller-5.log",
                   "Traceback (most recent call last):\n"
                   '  File "/srv/x.py", line 1, in f\n'
                   "KeyError: 'oi'\n"
                   "\n"
                   "During handling of the above exception, another exception occurred:\n"
                   "\n"
                   "Traceback (most recent call last):\n"
                   '  File "/srv/x.py", line 9, in g\n'
                   "RuntimeError: chain snapshot incomplete\n")
        self.check()
        [finding] = self.check().values()
        self.assertIn("RuntimeError", finding.title)

    def test_output_interleaved_into_a_traceback_does_not_close_it(self):
        self.start_watching("api.log")
        self.write("engine/dhan-feed-5.log",
                   "Traceback (most recent call last):\n"
                   '  File "/srv/core/live/feed_runner.py", line 334, in sync_watchlist\n'
                   "[dhan] SUBSCRIBED: 321 symbol(s), 198 beyond the watchlist\n"
                   "    watchlist, extra = self.desired_symbols()\n"
                   "TypeError: 'NoneType' object is not iterable\n")
        self.check()
        [finding] = self.check().values()
        self.assertEqual("Python error in Dhan feed: TypeError", finding.title)

    def test_a_traceback_split_across_checks_is_put_back_together(self):
        self.start_watching("engine/runner-301-1.log")
        self.append("engine/runner-301-1.log", "KeyError: 'oi'\n".join([
            'Traceback (most recent call last):\n  File "/srv/a.py", line 1, in f\n', ""]))
        self.assertEqual({}, self.check())
        self.append("engine/runner-301-1.log", "\nThe above exception was the direct cause of the following exception:\n")
        self.assertEqual({}, self.check())
        self.append("engine/runner-301-1.log",
                    'Traceback (most recent call last):\n  File "/srv/b.py", line 2, in g\n'
                    "IndexError: list index out of range\n")
        self.assertEqual({}, self.check())
        [finding] = self.check().values()
        self.assertEqual("Python error in run 301: IndexError", finding.title, "the cause is not reported")

    def test_one_database_error_is_one_api_error_however_many_blocks_log_it(self):
        self.start_watching("api.log")
        self.append("api.log",
                    "fail: Microsoft.EntityFrameworkCore.Database.Connection[20004]\n"
                    f"{B}An error occurred using the connection to database 'algotrading' on server 'tcp://localhost:5432'.\n"
                    + EF_VALUE_TOO_LONG)
        [finding] = self.check().values()
        self.assertEqual("api-error", finding.rule)
        self.assertEqual(Severity.MEDIUM, finding.severity)
        self.assertIn("value too long", finding.title)
        self.assertIn("widen the column", finding.suggestion)

    def test_a_recurring_api_error_stays_one_incident(self):
        self.start_watching("api.log")
        self.append("api.log", EF_VALUE_TOO_LONG)
        first = self.check()
        self.advance(600)  # ten quiet minutes
        self.assertEqual(set(first), set(self.check()))
        self.append("api.log", EF_VALUE_TOO_LONG)
        self.assertEqual(set(first), set(self.check()))
        self.advance(30)
        self.check()  # the last entry of the burst is finished on the next quiet check
        self.advance(3 * 3600)
        held = self.check()
        self.assertEqual(set(first), set(held))
        self.assertIn("held open", next(iter(held.values())).summary)
        self.advance(3600 + 60)
        self.assertEqual({}, self.check())

    def test_the_process_level_unhandled_exception_is_high(self):
        self.start_watching("api.log")
        self.append("api.log", "Unhandled exception. System.InvalidOperationException: Redis is gone\n"
                               "   at AlgoTrading.Api.Program.Main()\n")
        [finding] = self.check().values()
        self.assertEqual(("api-error", Severity.HIGH), (finding.rule, finding.severity))
        self.assertTrue(finding.title.startswith("The API process crashed"))

    def test_a_new_error_line_is_reported_once_then_remembered(self):
        self.start_watching("api.log")
        self.append("api.log", info(REG, "[strategy:Ghost:NIFTY] ERROR PROCESSING TICK: strike 23250 missing from chain"))
        [finding] = self.check().values()
        self.assertEqual(("new-error", Severity.LOW), (finding.rule, finding.severity))

        self.append("api.log", info(REG, "[strategy:Ghost:SENSEX] ERROR PROCESSING TICK: strike 74200 missing from chain"))
        self.assertEqual({}, self.check(), "same shape, different numbers: already seen")

        restarted = LogsAgent()  # what it has seen survives a restart
        self.append("api.log", info(REG, "[strategy:Ghost:NIFTY] ERROR PROCESSING TICK: strike 1 missing from chain"))
        self.assertEqual([], restarted.check(make_context(self.tmp, now=NOW)))

    def test_error_lines_already_in_the_history_are_not_new(self):
        old = info(REG, "[strategy:Ghost:NIFTY] ERROR PROCESSING TICK: 409 Client Error: Conflict")
        self.write("api.log", old)
        self.assertEqual({}, self.check())
        self.append("api.log", old)
        self.assertEqual({}, self.check())
        self.append("api.log", info(REG, "[strategy:Ghost:NIFTY] something else failed entirely"))
        self.assertEqual(["new-error"], [f.rule for f in self.check().values()])

    def test_the_memory_of_seen_lines_is_capped(self):
        self.start_watching("api.log")
        with mock.patch("sentinel.agents.logs.SEEN_CAP", 3):
            for word in ("alpha", "beta", "gamma", "delta"):
                self.append("api.log", info(REG, f"[strategy:G:NIFTY] error {word}"))
                self.check()
        seen = self.ctx.state("logs").data["seen"]
        self.assertEqual(3, len(seen))
        self.assertNotIn("error alpha", seen)


# 28 Sep 13:05:38: Postgres restarted on purpose. What the API logged about it for the next minute.
POSTGRES_RESTART = (
    "fail: AlgoTrading.Api.Services.AlertSubscriberService[0]\n"
    f"{B}Failed to process alert event from Redis.\n"
    f"{B}Npgsql.PostgresException (0x80004005): 53300: sorry, too many clients already\n"
    "fail: AlgoTrading.Api.Services.FeedSupervisor[0]\n"
    f"{B}Feed status poll failed.\n"
    f"{B}Npgsql.PostgresException (0x80004005): 57P01: terminating connection due to administrator command\n"
    "fail: AlgoTrading.Api.Services.StrategyRunControl[0]\n"
    f"{B}Could not reach the database.\n"
    f"{B}Npgsql.NpgsqlException (0x80004005): Failed to connect to 127.0.0.1:5432\n"
    f"{B} ---> System.Net.Sockets.SocketException (61): Connection refused\n"
    "fail: AlgoTrading.Infrastructure.Patterns.CandlePatternAlertService[0]\n"
    f"{B}Candle pattern scan failed.\n"
    f"{B}System.IO.EndOfStreamException: Attempted to read past the end of the stream.\n"
)
TOO_MANY_CLIENTS = POSTGRES_RESTART.split("fail: AlgoTrading.Api.Services.FeedSupervisor")[0]


class ResolvedWhileHeldTests(LogsAgentTestCase):
    """
    28 Sep: the operator resolved the restart's four API errors at 13:08 (#119, #126, #127, #128) and at
    13:11 Sentinel opened them again as #134-#137, "18 matching line(s) since 13:05 IST, the last at 13:06
    IST": their four-hour holds, not new lines. What was read before a resolve never reopens it.
    """

    def at(self, hour, minute, second=0):
        self.clock[0] = datetime(2026, 9, 28, hour, minute, second, tzinfo=timezone.utc) - timedelta(hours=5,
                                                                                                    minutes=30)

    def desk(self):
        store, notifier = MemoryIncidentStore(), RecordingNotifier()
        return store, notifier, SentinelEngine([self.agent], store, notifier, self.ctx, monotonic=clock_ticks())

    def test_the_28_sep_restart_resolved_by_hand_stays_resolved_until_a_new_line(self):
        store, notifier, engine = self.desk()
        self.at(13, 5, 10)
        self.write("api.log")
        engine.run_due()
        for second, burst in ((40, True), (70, True), (100, False), (130, False), (160, False)):
            self.at(13, 5 + second // 60, second % 60)   # 13:05:40, a second burst at 13:06:10, then quiet
            if burst:
                self.append("api.log", POSTGRES_RESTART)
            engine.run_due()
        rows = store.rows()
        self.assertEqual(4, len(rows))
        self.assertEqual({"api-error"}, {r["rule"] for r in rows})
        self.assertEqual(4, len(notifier.sent))
        self.assertTrue(any("too many clients" in r["title"] for r in rows))

        self.at(13, 8)
        for row in rows:   # the operator, from the console, with a root cause for each
            store.resolve_by_person(row["id"], self.clock[0])
            store.write_resolution(row["id"], "Deliberate Postgres restart at 13:05:38; not a fault.")

        for second in range(10, 4 * 60, 30):   # 13:08:10 … 13:11:40: the holds still run for hours
            self.at(13, 8 + second // 60, second % 60)
            engine.run_due()
        self.assertEqual(4, len(store.rows()), "nothing reopened, nothing new")
        self.assertEqual(["resolved"] * 4, [r["status"] for r in store.rows()])
        self.assertEqual(4, len(notifier.sent))
        held = self.ctx.state("logs").data.get("active", {})
        self.assertFalse(any(r["fingerprint"] in held for r in rows), "the holds are over")

        # 13:20: the database refuses a connection again. That line is news, and only it is counted.
        self.at(13, 20)
        self.append("api.log", TOO_MANY_CLIENTS)
        engine.run_due()
        self.at(13, 20, 30)
        engine.run_due()
        self.assertEqual(5, len(store.rows()))
        new = store.rows()[-1]
        self.assertEqual(("open", 1), (new["status"], new["occurrences"]))
        self.assertIn("too many clients", new["title"])
        self.assertIn("1 matching line(s) at 13:20 IST", new["summary"])
        self.assertEqual(5, len(notifier.sent))
        self.assertIn("NEW [MEDIUM] API error", notifier.sent[-1].splitlines()[0])
        self.assertIn("Last time: Deliberate Postgres restart at 13:05:38; not a fault.", notifier.sent[-1])

    def test_a_held_finding_says_when_its_last_line_was_read(self):
        self.start_watching("api.log")
        self.append("api.log", EF_VALUE_TOO_LONG)
        fresh = self.check()
        self.assertEqual({None}, {f.observed_utc for f in fresh.values()}, "seen this check")
        self.advance(30)
        self.check()   # the last entry of the burst is finished on the next quiet check
        last_read = self.clock[0]
        self.advance(600)
        held = self.check()
        self.assertEqual({last_read}, {f.observed_utc for f in held.values()})
        self.assertTrue(all("held open" in f.summary for f in held.values()))

        self.agent.let_go(self.ctx, set(held))
        self.advance(30)
        self.assertEqual({}, self.check(), "let go: nothing held")
        self.append("api.log", EF_VALUE_TOO_LONG)
        again = self.check()
        self.assertEqual(set(held), set(again))
        self.assertTrue(all("1 matching line(s)" in f.summary for f in again.values()), "counted afresh")


class QuietTests(LogsAgentTestCase):
    def test_known_benign_lines_are_muted(self):
        self.start_watching("api.log", "desk.log")
        self.append("api.log",
                    WATCHLIST_RACE
                    + info(REG, "[strategy:Fulcrum:NIFTY] [NIFTY] Failed to start metrics server (port auto): no free "
                                "metrics port in 9100..9199. Continuing without metrics.")
                    + info("AlgoTrading.Api.Services.FeedSupervisor",
                           "[Dhan feed] [dhan] TICK BATCH HTTP ERROR: HTTPConnectionPool(host='localhost', port=5025):"
                           " Max retries exceeded with url: /api/LiveData/ticks/upsert-batch (Caused by "
                           "NewConnectionError('Failed to establish a new connection: [Errno 111] Connection refused'))"))
        self.write("engine/dhan-feed-9.log",
                   "[safe_output] sys.stdout lost (BrokenPipeError: [Errno 32] Broken pipe); output continues in "
                   "this file only\n"
                   "Traceback (most recent call last):\n"
                   '  File "/srv/core/live/feed_runner.py", line 266, in read_watchlist\n'
                   "requests.exceptions.ConnectionError: HTTPConnectionPool(host='localhost', port=5025): Max "
                   "retries exceeded with url: /api/LiveData/watchlist\n"
                   "\n"
                   "During handling of the above exception, another exception occurred:\n")
        self.append("desk.log", "    0 Error(s)\n"
                                "/srv/Api.csproj : warning NU1701: Package 'WebSocketSharp' was restored\n"
                                "dist/assets/ErrorBoundary-D3Ib7_Hf.js   4.42 kB\n"
                                "09:30:00  WARN: API health check failed (1/3)\n")
        self.assertEqual({}, self.check())
        self.assertEqual({}, self.check())

    def test_lines_that_may_carry_a_secret_never_reach_a_finding(self):
        self.start_watching("api.log", "desk.log")
        secret_lines = [
            info("System.Net.Http.HttpClient.telegram.LogicalHandler",
                 "Start processing HTTP request POST https://api.telegram.org/bot123456789:ABCdefGhIJKlmNoPQRsTUVw"
                 "xyZ1234567890ab/sendMessage failed"),
            info(REG, "[strategy:G:NIFTY] ERROR login failed with password=hunter2hunter2"),
            info(REG, "[strategy:G:NIFTY] error: Authorization: Bearer abcdefghijklmnopqrstuvwxyz123"),
            info(REG, "[strategy:G:NIFTY] exception token=abcdef123456 rejected"),
        ]
        self.append("api.log", "".join(secret_lines))
        self.append("desk.log", "09:00:00  WARN: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N\n")  # pragma: allowlist secret
        self.assertEqual({}, self.check())

    # The shared spec's cases (notify.redact, the API's IncidentRedaction, the console's maskSecrets, and this).
    CARRIES_A_SECRET = [
        "FYERS_SECRET_KEY=abc123",                    # no leading word boundary: the desk's own .env keys
        "POSTGRES_PASSWORD=hunter2hunter2",
        "DHAN_PIN=1234",
        "trading_pin: 1234",
        "JWT_SECRET_KEY=x",
        "TELEGRAM_BOT_TOKEN=123",
        "dhan_totp: 123456",
        "accessToken: abc",
        'config {"api_key": "sk-live-1"}',
        "X-Api-Key=abc",
        "private-key = abc",
        "ENGINE_SERVICE_PASSWORD\t= x",
        "refresh_token='abc'",
        "passwd:x",
        "db pwd = y",
        "postgres://algo:s3cret@localhost:5432/trading",
        "redis://:s3cret@localhost:6379",
        "Authorization: Bearer abcdefghijklmnopqrstuvwxyz123",
        "authorization=basic dXNlcjpwYXNz",
        '"Authorization": "Bearer abc"',
        "sent with Bearer abcdefghijklmnop",
        "POST https://api.telegram.org/bot123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ1234567890ab/sendMessage",
        "chat 123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ1234567890ab",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N",  # pragma: allowlist secret
    ]
    CARRIES_NONE = [
        "spinning up the Dhan feed",
        "fetched 5 tokens from the pool",
        "tokens: 5 refreshed",
        "Skipping: NIFTY has no expiry today",
        "[RUNNER] stopping: SIGTERM",
        "if token == expected:",
        "ERROR the admin password was refused",
        "Authorization failed for user admin",
        "token expired for FYERS",
        "a pin\n= not on the same line",
        "run 1234567890123:ABCdefGhIJKlmNoPQRsTUVwxyZ1234567890ab",   # 13 digits: not a bot id
        "eyJhbGciOiJIUzI1NiJ9.notapayload1.dozjgNryP4J3jVmNHl0w5N",
    ]

    def test_the_shared_secret_spec(self):
        self.assertEqual([], [line for line in self.CARRIES_A_SECRET if not _may_hold_secret(line)])
        self.assertEqual([], [line for line in self.CARRIES_NONE if _may_hold_secret(line)])

    def test_a_line_is_dropped_exactly_when_the_redactor_would_mask_it(self):
        # One spec in every layer: change notify.redact and this filter together, or this fails.
        for line in self.CARRIES_A_SECRET + self.CARRIES_NONE:
            self.assertEqual(redact(line) != line, _may_hold_secret(line), line)

    def test_prose_about_passwords_and_tokens_is_still_read(self):
        # The old markers dropped any line with "password" in it, so an error that only mentioned one
        # never reached a rule.
        self.start_watching("api.log")
        self.append("api.log", info(REG, "[strategy:G:NIFTY] ERROR the broker password was refused by FYERS"))
        [finding] = self.check().values()
        self.assertEqual("new-error", finding.rule)
        self.assertIn("password was refused", finding.title)

    def test_a_desk_env_line_is_dropped_whole(self):
        self.start_watching("desk.log")
        self.append("desk.log", "09:00:00  FAILED: could not start with FYERS_SECRET_KEY=abcdef0123 set\n"
                                "09:00:01  WARN: DHAN_PIN=4321 was rejected\n")
        self.assertEqual({}, self.check())

    def test_a_finding_carries_no_credential_even_from_a_traceback(self):
        self.start_watching("api.log")
        self.append("api.log", RUN_215_DIES)
        for finding in self.check().values():
            text = " ".join([finding.title, finding.summary, finding.where, finding.suggestion, *finding.evidence])
            self.assertNotIn("Bearer", text)
            self.assertNotIn("Authorization", text)
            for line in finding.evidence:
                self.assertLessEqual(len(line), 240)

    def test_sentinels_own_logs_are_never_read_back(self):
        self.start_watching("api.log")
        self.write("engine/sentinel-1.log", "NOTIFY NEW [HIGH] error error FAILED Traceback\n")
        self.assertEqual({}, self.check())

    def test_old_engine_logs_are_left_alone(self):
        self.start_watching("api.log")
        path = self.logs / "engine" / "runner-100-1.log"
        path.write_text("Traceback (most recent call last):\n  File \"/srv/a.py\", line 1, in f\nKeyError: 'ltp'\n",
                        encoding="utf-8")
        two_days_ago = NOW.timestamp() - 2 * 86400
        os.utime(path, (two_days_ago, two_days_ago))
        self.assertEqual({}, self.check())


if __name__ == "__main__":
    unittest.main()
