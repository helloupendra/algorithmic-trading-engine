import _bootstrap  # noqa: F401

import logging
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import sentinel.__main__ as entry
from sentinel.agents.base import Agent
from sentinel.engine import SentinelEngine
from sentinel.reload import CodeWatch, newest_source
from sentinel.store import MemoryIncidentStore
from _sentinel_fakes import RecordingNotifier, make_context


class FakeTime:
    """One clock for everything: sleeping moves it, nothing waits."""

    def __init__(self):
        self.now = 0.0
        self.slept = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds
        self.slept += seconds


class Code:
    """A source tree the test changes by hand."""

    def __init__(self):
        self.newest = 1_000.0
        self.looks = 0

    def newest_mtime(self):
        self.looks += 1
        return self.newest


class CodeWatchTests(unittest.TestCase):
    def setUp(self):
        self.time = FakeTime()
        self.code = Code()
        self.watch = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60,
                               newest=self.code.newest_mtime)

    def test_nothing_changed_is_nothing(self):
        for _ in range(5):
            self.time.sleep(61)
            self.assertIsNone(self.watch.changed())

    def test_a_newer_source_file_is_a_change(self):
        self.code.newest = 1_060.0
        self.time.sleep(61)
        self.assertIn("newer than the code this process started with", self.watch.changed())

    def test_it_looks_about_once_a_minute_not_every_round(self):
        looks = self.code.looks
        for _ in range(11):          # 55 s of five-second rounds
            self.time.sleep(5)
            self.assertIsNone(self.watch.changed())
        self.assertEqual(looks, self.code.looks)
        self.code.newest = 2_000.0
        self.time.sleep(5)
        self.assertIsNotNone(self.watch.changed())

    def test_what_cannot_be_read_is_not_a_change(self):
        self.code.newest = None
        self.time.sleep(61)
        self.assertIsNone(self.watch.changed())

    def test_an_edited_env_is_a_change(self):
        # POSTGRES_PASSWORD or TELEGRAM_BOT_TOKEN rotated: the values read at start are dead.
        env = {"mtime": 500.0}
        watch = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60, newest=self.code.newest_mtime,
                          env_mtime=lambda: env["mtime"])
        self.time.sleep(61)
        self.assertIsNone(watch.changed())
        env["mtime"] = 400.0   # put back from a copy: older, and still an edit
        self.time.sleep(61)
        self.assertIn("settings changed: the repository's .env was edited", watch.changed())

    def test_an_env_that_appears_is_a_change_and_one_that_goes_is_not(self):
        env = {"mtime": None}
        watch = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60, newest=self.code.newest_mtime,
                          env_mtime=lambda: env["mtime"])
        env["mtime"] = 700.0
        self.time.sleep(61)
        self.assertIsNotNone(watch.changed())
        gone = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60, newest=self.code.newest_mtime,
                         env_mtime=lambda: env["mtime"])
        env["mtime"] = None
        self.time.sleep(61)
        self.assertIsNone(gone.changed())

    def test_the_real_env_file_is_looked_at(self):
        tmp = Path(tempfile.mkdtemp())
        (tmp / ".env").write_text("TELEGRAM_BOT_TOKEN=old\n")
        os.utime(tmp / ".env", (100, 100))
        watch = CodeWatch(tmp, monotonic=self.time.monotonic, every=60, newest=self.code.newest_mtime,
                          env_file=tmp / ".env")
        os.utime(tmp / ".env", (200, 200))
        self.time.sleep(61)
        self.assertIsNotNone(watch.changed())

    def test_a_file_from_the_future_at_start_does_not_restart_it_in_a_loop(self):
        # The baseline is the newest time seen at start, not the clock: a clock that moved back is not a change.
        self.code.newest = 9_999_999_999.0
        watch = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60,
                          newest=self.code.newest_mtime)
        self.time.sleep(61)
        self.assertIsNone(watch.changed())


class FilesTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def test_newest_source_is_the_newest_py_file_anywhere_in_the_package(self):
        (self.tmp / "agents").mkdir()
        for name, when in (("engine.py", 100), ("agents/logs.py", 300), ("agents/notes.txt", 900)):
            (self.tmp / name).write_text("x")
            os.utime(self.tmp / name, (when, when))
        self.assertEqual(300, newest_source(self.tmp))
        self.assertIsNone(newest_source(self.tmp / "missing"))

    def test_a_deploy_elsewhere_in_the_repository_is_not_a_change(self):
        # A pull that touches the API or the console, not Sentinel: nothing to
        # restart for, and a deploy is when Sentinel should be watching.
        package = self.tmp / "sentinel"
        package.mkdir()
        (package / "engine.py").write_text("x")
        os.utime(package / "engine.py", (100, 100))
        (self.tmp / "desk.py").write_text("x")
        os.utime(self.tmp / "desk.py", (900, 900))
        self.assertEqual(100, newest_source(package))


class Ticking(Agent):
    name = "ticking"
    interval_seconds = 30

    def __init__(self):
        self.rounds = 0

    def check(self, ctx):
        self.rounds += 1
        return []


class WatchLoopTests(unittest.TestCase):
    def setUp(self):
        self.time = FakeTime()
        self.code = Code()
        self.agent = Ticking()
        self.store = MemoryIncidentStore()
        self.engine = SentinelEngine([self.agent], self.store, RecordingNotifier(),
                                     make_context(Path(tempfile.mkdtemp())), monotonic=self.time.monotonic)
        self.watch = CodeWatch(Path("."), monotonic=self.time.monotonic, every=60,
                               newest=self.code.newest_mtime)

    def test_new_code_ends_the_loop_between_rounds_without_waiting_for_real(self):
        def sleep(seconds):
            self.time.sleep(seconds)
            if self.time.now >= 200:
                self.code.newest = 2_000.0   # the desk pulled a Sentinel change

        why = entry.watch(self.engine, self.watch, {"flag": False}, sleep=sleep)
        self.assertIn("newer than the code this process started with", why)
        self.assertLessEqual(self.time.now, 200 + 60 + 5)     # noticed within about a minute
        self.assertGreaterEqual(self.agent.rounds, 7)          # every 30 s until then
        self.assertIsNotNone(self.store.last_check)            # the last round finished and said so

    def test_a_stop_signal_is_not_a_code_change(self):
        stopping = {"flag": False}

        def sleep(seconds):
            self.time.sleep(seconds)
            stopping["flag"] = self.time.now >= 100

        self.assertIsNone(entry.watch(self.engine, self.watch, stopping, sleep=sleep))

    def test_main_exits_cleanly_and_says_why(self):
        code = self.code

        def watch_factory(package_dir, env_file=None):
            # The real package directory is what main() watches, and the repository's .env.
            self.assertTrue((package_dir / "reload.py").is_file())
            self.assertEqual(entry.REPO_ROOT / ".env", env_file)
            return CodeWatch(package_dir, monotonic=self.time.monotonic, every=60,
                             newest=code.newest_mtime, env_mtime=lambda: 1.0)

        def sleep(seconds):
            self.time.sleep(seconds)
            code.newest = 5_000.0

        with mock.patch.object(entry, "build", return_value=self.engine), \
                mock.patch.object(entry, "watch_lock", return_value=None), \
                mock.patch.object(entry, "CodeWatch", side_effect=watch_factory), \
                mock.patch.object(entry.time, "sleep", side_effect=sleep), \
                mock.patch.object(entry.signal, "signal"), \
                mock.patch.object(entry.logging, "basicConfig"), \
                self.assertLogs("sentinel", level=logging.INFO) as logs:
            self.assertEqual(0, entry.main([]))
        self.assertTrue(any("code changed" in line and "exiting so the service restarts it" in line
                            for line in logs.output), logs.output)

    def test_a_second_watcher_exits_without_running_a_check(self):
        class Taken:
            def held_elsewhere(self):
                return True

        with mock.patch.object(entry, "build", return_value=self.engine) as build, \
                mock.patch.object(entry, "watch_lock", return_value=Taken()), \
                mock.patch.object(entry, "CodeWatch"), \
                mock.patch.object(entry.logging, "basicConfig"), \
                self.assertLogs("sentinel", level="ERROR") as logs:
            self.assertNotEqual(0, entry.main([]))
        self.assertEqual(0, self.agent.rounds)
        build.assert_not_called()
        self.assertIn("another Sentinel is already watching", logs.output[0])

    def test_trying_it_next_to_the_service_takes_no_lock(self):
        with mock.patch.object(entry, "build", return_value=self.engine), \
                mock.patch.object(entry, "watch_lock", side_effect=AssertionError("locked in --once")), \
                mock.patch.object(entry.logging, "basicConfig"):
            self.assertEqual(0, entry.main(["--once", "--dry-run"]))
            self.assertEqual(0, entry.main(["--once"]))

    def test_a_watcher_that_loses_the_lock_to_another_stops(self):
        class Lost:
            def __init__(self):
                self.looks = 0

            def held_elsewhere(self):
                self.looks += 1
                return self.looks >= 2   # the database restarted and the other one took it

        lock = Lost()
        why = entry.watch(self.engine, self.watch, {"flag": False}, sleep=self.time.sleep, lock=lock,
                          monotonic=self.time.monotonic)
        self.assertIn("another Sentinel is watching now", why)
        self.assertLessEqual(self.time.now, 2 * entry.LOCK_CHECK_SECONDS + 5)
        self.assertEqual(2, lock.looks, "looked at about once a minute, not every round")

    def test_once_does_not_watch(self):
        with mock.patch.object(entry, "build", return_value=self.engine), \
                mock.patch.object(entry.logging, "basicConfig"), \
                mock.patch.object(entry, "CodeWatch", side_effect=AssertionError("watched in --once")):
            self.assertEqual(0, entry.main(["--once"]))
        self.assertEqual(1, self.agent.rounds)


if __name__ == "__main__":
    unittest.main()
