"""
Process-supervision hardening (core/safe_output.py, core/heartbeat.py):
writing after stdout became a broken pipe must never raise and must land in
the fallback log file; a periodic loop keeps going when both its step and its
error logging fail.
"""

import io
import os
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
from typing import List, Tuple

import _bootstrap  # noqa: F401

from core.heartbeat import run_forever
from core.safe_output import (
    LineLog,
    SafeStream,
    default_log_path,
    install_safe_stdio,
    is_installed,
    log_name_for_run,
)

#: "2026-09-28T03:45:01.123Z | text" — a stamped line of a runner's log.
STAMPED = re.compile(r"^(?P<at>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z) (?P<stream>[|!]) (?P<text>.*)$")


class BrokenStream(io.StringIO):
    """A text stream that starts working and then behaves like a closed pipe."""

    def __init__(self) -> None:
        super().__init__()
        self.broken = False
        self.error: type = BrokenPipeError

    def write(self, text: str) -> int:
        if self.broken:
            raise self.error(32, "Broken pipe")
        return super().write(text)

    def flush(self) -> None:
        if self.broken:
            raise self.error(32, "Broken pipe")
        super().flush()


def read(path: str) -> str:
    with open(path, encoding="utf-8") as handle:
        return handle.read()


class SafeStreamTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.log_path = os.path.join(self.tmp.name, "nested", "engine", "runner-1.log")
        self.streams: List[SafeStream] = []

    def tearDown(self) -> None:
        for stream in self.streams:
            stream.close()          # releases the log file only; never touches the wrapped stream
        self.tmp.cleanup()

    def safe(self, target, log_path=None, **kwargs) -> SafeStream:
        stream = SafeStream(target, self.log_path if log_path is None else log_path, **kwargs)
        self.streams.append(stream)
        return stream

    def test_writes_reach_the_target_while_it_works(self) -> None:
        target = BrokenStream()
        stream = self.safe(target, label="stdout")
        print("hello", file=stream, flush=True)
        self.assertEqual(target.getvalue(), "hello\n")
        self.assertFalse(stream.dead)
        self.assertFalse(os.path.exists(self.log_path))     # the log file is created only when needed

    def test_broken_pipe_never_raises_and_lands_in_the_log(self) -> None:
        target = BrokenStream()
        stream = self.safe(target, label="sys.stdout")
        stream.write("before\n")
        target.broken = True

        # Neither write nor flush may raise once the pipe is gone.
        stream.write("after pipe closed\n")
        stream.flush()
        print("printed later", file=stream, flush=True)

        self.assertTrue(stream.dead)
        self.assertEqual(target.getvalue(), "before\n")
        text = read(self.log_path)
        self.assertIn("sys.stdout lost (BrokenPipeError", text)
        self.assertIn("after pipe closed\n", text)
        self.assertIn("printed later\n", text)

    def test_closed_file_value_error_is_handled_the_same_way(self) -> None:
        target = BrokenStream()
        target.error = ValueError
        stream = self.safe(target)
        target.broken = True
        stream.write("x\n")
        self.assertTrue(stream.dead)
        self.assertIn("x\n", read(self.log_path))

    def test_unwritable_log_path_still_never_raises(self) -> None:
        target = BrokenStream()
        # A file where the directory should be: mkdir fails, the stream must swallow it.
        blocker = os.path.join(self.tmp.name, "blocker")
        with open(blocker, "w", encoding="utf-8") as handle:
            handle.write("")
        stream = self.safe(target, os.path.join(blocker, "sub", "x.log"))
        target.broken = True
        stream.write("lost\n")
        stream.flush()
        self.assertTrue(stream.dead)

    def test_log_path_can_be_repointed(self) -> None:
        target = BrokenStream()
        stream = self.safe(target)
        target.broken = True
        stream.write("first\n")
        renamed = os.path.join(self.tmp.name, "runner-77.log")
        stream.log_path = renamed
        stream.write("second\n")
        self.assertIn("first\n", read(self.log_path))
        self.assertNotIn("second\n", read(self.log_path))
        self.assertIn("second\n", read(renamed))

    def test_no_target_goes_straight_to_the_log(self) -> None:
        stream = self.safe(None)
        stream.write("only log\n")
        self.assertTrue(stream.dead)
        self.assertEqual(read(self.log_path), "only log\n")

    def test_unicode_encode_error_does_not_kill_a_healthy_pipe(self) -> None:
        # A cp1252/ascii pipe (Windows child without PYTHONIOENCODING) rejects
        # "→": the line must still reach the pipe (characters replaced), the
        # stream must NOT be marked dead and later lines must keep flowing.
        raw = io.BytesIO()
        target = io.TextIOWrapper(raw, encoding="ascii", errors="strict", write_through=True)
        stream = self.safe(target, label="sys.stdout")

        print("[CONFIG] leg → group → overall", file=stream, flush=True)
        print("[TICK] plain ascii", file=stream, flush=True)

        self.assertFalse(stream.dead)
        out = raw.getvalue().decode("ascii")
        self.assertIn("[CONFIG] leg ? group ? overall\n", out)
        self.assertIn("[TICK] plain ascii\n", out)
        self.assertFalse(os.path.exists(self.log_path))   # nothing was diverted to the log file

    def test_unicode_encode_error_then_a_real_broken_pipe_still_goes_to_the_log(self) -> None:
        class AsciiThenBroken(BrokenStream):
            def write(self, text: str) -> int:
                if not text.isascii():
                    raise UnicodeEncodeError("ascii", text, 0, 1, "ordinal not in range(128)")
                return super().write(text)

        target = AsciiThenBroken()
        stream = self.safe(target)
        stream.write("₹ line\n")
        self.assertFalse(stream.dead)
        self.assertEqual(target.getvalue(), "? line\n")

        target.broken = True
        stream.write("after the pipe closed\n")
        self.assertTrue(stream.dead)
        self.assertIn("after the pipe closed\n", read(self.log_path))

    def test_traceback_printing_survives_a_dead_stream(self) -> None:
        import traceback
        target = BrokenStream()
        stream = self.safe(target)
        target.broken = True
        try:
            raise RuntimeError("boom")
        except RuntimeError:
            traceback.print_exc(file=stream)
        self.assertIn("RuntimeError: boom", read(self.log_path))


class InstallSafeStdioTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = (sys.stdout, sys.stderr)

    def tearDown(self) -> None:
        for stream in (sys.stdout, sys.stderr):
            if isinstance(stream, SafeStream):
                stream.close()
        sys.stdout, sys.stderr = self.saved
        self.tmp.cleanup()

    def test_install_wraps_both_streams_and_is_idempotent(self) -> None:
        out, err = BrokenStream(), BrokenStream()
        sys.stdout, sys.stderr = out, err
        log_path = os.path.join(self.tmp.name, "ingestor.log")

        self.assertEqual(install_safe_stdio(log_path), log_path)
        self.assertTrue(is_installed())
        first_out, first_err = sys.stdout, sys.stderr

        # Re-installing (e.g. once the run id is known) keeps the wrappers and re-points the log.
        renamed = os.path.join(self.tmp.name, "runner-5.log")
        install_safe_stdio(renamed)
        self.assertIs(sys.stdout, first_out)
        self.assertIs(sys.stderr, first_err)
        self.assertEqual(sys.stdout.log_path, renamed)

        print("alive")
        print("warn", file=sys.stderr)
        self.assertEqual(out.getvalue(), "alive\n")
        self.assertEqual(err.getvalue(), "warn\n")

        out.broken = err.broken = True
        print("after restart", flush=True)
        print("stderr after restart", file=sys.stderr, flush=True)
        text = read(renamed)
        self.assertIn("after restart\n", text)
        self.assertIn("stderr after restart\n", text)

    def test_default_log_path_uses_name_and_pid(self) -> None:
        path = default_log_path("runner-12")
        self.assertTrue(path.endswith(f"runner-12-{os.getpid()}.log"))
        self.assertIn(os.path.join("logs", "engine"), path)
        self.assertTrue(default_log_path("a b/c").endswith(f"a-b-c-{os.getpid()}.log"))


def stamped(path: str) -> List[Tuple[str, str]]:
    """(stream, text) of every line of a runner's log; fails on a line that is not stamped."""
    lines = []
    for raw in read(path).splitlines():
        match = STAMPED.match(raw)
        if match is None:
            raise AssertionError(f"not a stamped line: {raw!r}")
        lines.append((match["stream"], match["text"]))
    return lines


class RunnerLogTests(unittest.TestCase):
    """A runner's log from its first line: every line stamped, the pipe or not."""

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "engine", "runner-215-4242.log")
        self.log = LineLog(self.path)

    def tearDown(self) -> None:
        self.log.close()
        self.tmp.cleanup()

    def test_every_line_is_stamped_with_its_time_and_stream_while_the_pipe_still_gets_it_plain(self) -> None:
        out, err = BrokenStream(), BrokenStream()
        stdout = SafeStream(out, None, tee=self.log, marker="|")
        stderr = SafeStream(err, None, tee=self.log, marker="!")

        # print() writes the text and the newline separately; a line is one line.
        print("[CONFIG] strategy=Fulcrum", file=stdout)
        stdout.write("[STATUS] NIFTY ")
        stdout.write("spot=25010\n[STATUS] second\n")
        print("SIGNAL REFUSED by the API", file=stderr)

        self.assertEqual(
            [("|", "[CONFIG] strategy=Fulcrum"), ("|", "[STATUS] NIFTY spot=25010"),
             ("|", "[STATUS] second"), ("!", "SIGNAL REFUSED by the API")],
            stamped(self.path))
        self.assertEqual("[CONFIG] strategy=Fulcrum\n[STATUS] NIFTY spot=25010\n[STATUS] second\n", out.getvalue())
        self.assertEqual("SIGNAL REFUSED by the API\n", err.getvalue())

    def test_the_log_carries_on_when_the_pipe_breaks_and_nothing_is_written_twice(self) -> None:
        out = BrokenStream()
        stdout = SafeStream(out, None, label="sys.stdout", tee=self.log, marker="|")
        print("before the restart", file=stdout)
        out.broken = True                                   # the API went away
        print("after the restart", file=stdout)
        print("and later", file=stdout)

        lines = stamped(self.path)
        self.assertEqual(("|", "before the restart"), lines[0])
        self.assertEqual("!", lines[1][0])
        self.assertIn("sys.stdout lost (BrokenPipeError", lines[1][1])
        self.assertEqual([("|", "after the restart"), ("|", "and later")], lines[2:])

    def test_an_unfinished_line_is_written_as_it_stands_when_asked(self) -> None:
        self.log.write("|", "half a line")
        self.assertFalse(os.path.exists(self.path))
        self.log.flush_pending()
        self.assertEqual([("|", "half a line")], stamped(self.path))

    def test_a_line_that_never_ends_is_not_held_forever(self) -> None:
        self.log.write("|", "x" * (LineLog.MAX_PENDING + 1))
        [(stream, text)] = stamped(self.path)
        self.assertEqual(LineLog.MAX_PENDING + 1, len(text))

    def test_the_run_id_is_read_from_the_command_line(self) -> None:
        self.assertEqual("runner-215", log_name_for_run("runner", ["execution_runner.py", "--strategy", "Fulcrum",
                                                                  "--run-id", "215", "--underlying", "NIFTY"]))
        self.assertEqual("runner-9", log_name_for_run("runner", ["execution_runner.py", "--run-id=9"]))
        self.assertEqual("runner", log_name_for_run("runner", ["execution_runner.py", "--strategy", "Fulcrum"]))
        self.assertEqual("runner", log_name_for_run("runner", ["execution_runner.py", "--run-id", "abc"]))


class InstallWithTeeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = (sys.stdout, sys.stderr)

    def tearDown(self) -> None:
        for stream in (sys.stdout, sys.stderr):
            if isinstance(stream, SafeStream):
                stream.close()
        sys.stdout, sys.stderr = self.saved
        self.tmp.cleanup()

    def test_both_streams_share_one_log_in_the_order_written(self) -> None:
        sys.stdout, sys.stderr = io.StringIO(), io.StringIO()
        path = os.path.join(self.tmp.name, "runner-215-1.log")
        install_safe_stdio(path, tee=True)

        print("[CONFIG] run 215")
        print("WARN: could not read the lot size", file=sys.stderr)
        print("[STATUS] NIFTY spot=25010")

        self.assertEqual([("|", "[CONFIG] run 215"), ("!", "WARN: could not read the lot size"),
                          ("|", "[STATUS] NIFTY spot=25010")], stamped(path))


class PinnedLogNameTests(unittest.TestCase):
    """
    ENGINE_LOG_NAME: the API launching a daemon decides the log's name, and
    with it that the log is kept from the first line. On 28 Sep, twice, a feed
    adopted after an API restart had pipes nobody read and its output was lost;
    the API now tails logs/engine/<name>-<pid>.log instead, a path it computes
    from the name it set and the pid it launched.
    """

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.saved = (sys.stdout, sys.stderr)

    def tearDown(self) -> None:
        for stream in (sys.stdout, sys.stderr):
            if isinstance(stream, SafeStream):
                stream.close()
        sys.stdout, sys.stderr = self.saved
        self.tmp.cleanup()

    def test_the_pinned_name_wins_and_turns_the_tee_on(self) -> None:
        from pathlib import Path
        from unittest import mock

        sys.stdout, sys.stderr = io.StringIO(), io.StringIO()
        with mock.patch.dict(os.environ, {"ENGINE_LOG_NAME": "dhan-feed"}), \
                mock.patch("core.safe_output.ENGINE_LOG_DIR", Path(self.tmp.name)):
            path = install_safe_stdio(name="something-else")
            print("[dhan] STARTING LIVE FEED")
            # A second install (a script renaming its log) cannot move it either.
            self.assertEqual(path, install_safe_stdio(name="renamed"))
            print("[dhan] heartbeat", file=sys.stderr)

        self.assertEqual(os.path.join(self.tmp.name, f"dhan-feed-{os.getpid()}.log"), path)
        # Stamped from the first line while the pipe is alive: the tee, not the fallback.
        self.assertEqual([("|", "[dhan] STARTING LIVE FEED"), ("!", "[dhan] heartbeat")], stamped(path))

    def test_without_it_nothing_changes(self) -> None:
        sys.stdout, sys.stderr = io.StringIO(), io.StringIO()
        path = os.path.join(self.tmp.name, "ingestor-1.log")
        env = {k: v for k, v in os.environ.items() if k != "ENGINE_LOG_NAME"}
        from unittest import mock
        with mock.patch.dict(os.environ, env, clear=True):
            self.assertEqual(path, install_safe_stdio(path))
            print("[fyers] ticks flowing")

        # No tee: the file is written only once the pipe has died.
        self.assertFalse(os.path.exists(path))

    def test_the_file_carries_the_pid_the_launcher_sees(self) -> None:
        # The contract the API relies on: the pid in the name is the pid of
        # the process it started, not of anything the script does later.
        script = textwrap.dedent(f"""\
            import sys
            from pathlib import Path
            sys.path.insert(0, {_bootstrap.ENGINE_DIR!r})
            import core.safe_output as safe_output
            safe_output.ENGINE_LOG_DIR = Path({self.tmp.name!r})
            safe_output.install_safe_stdio(name="ingestor")
            print("[fyers] connected")
        """)
        env = dict(os.environ, ENGINE_LOG_NAME="ingestor", PYTHONUNBUFFERED="1")
        child = subprocess.Popen([sys.executable, "-c", script], env=env,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        out, _ = child.communicate(timeout=60)

        self.assertEqual("[fyers] connected\n", out)
        path = os.path.join(self.tmp.name, f"ingestor-{child.pid}.log")
        self.assertEqual([("|", "[fyers] connected")], stamped(path))


class ExitLineTests(unittest.TestCase):
    """
    The last line of a runner's log says how it ended. It is written from
    atexit, which only a real interpreter exit runs, so each case is its own
    Python process.
    """

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "runner-215-1.log")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def run_script(self, body: str) -> Tuple[int, List[Tuple[str, str]]]:
        script = textwrap.dedent(f"""\
            import sys
            sys.path.insert(0, {_bootstrap.ENGINE_DIR!r})
            from core.safe_output import install_exit_line, install_safe_stdio, note_exit
            install_safe_stdio({self.path!r}, tee=True)
            install_exit_line()
        """) + textwrap.dedent(body)
        done = subprocess.run([sys.executable, "-c", script], capture_output=True, text=True, timeout=60)
        return done.returncode, stamped(self.path)

    def test_a_script_that_runs_to_its_end(self) -> None:
        code, lines = self.run_script("print('working')\n")
        self.assertEqual(0, code)
        self.assertEqual([("|", "working"), ("|", "EXIT code=0 reason=finished")], lines)

    def test_sys_exit_with_a_code(self) -> None:
        code, lines = self.run_script("""\
            print('No option contracts loaded for NIFTY', file=sys.stderr)
            sys.exit(2)
        """)
        self.assertEqual(2, code)
        self.assertEqual([("!", "No option contracts loaded for NIFTY"), ("|", "EXIT code=2 reason=sys.exit(2)")], lines)

    def test_an_uncaught_exception(self) -> None:
        code, lines = self.run_script("raise KeyError('ltp')\n")
        self.assertEqual(1, code)
        self.assertIn(("!", "Traceback (most recent call last):"), lines)
        self.assertEqual(("!", "KeyError: 'ltp'"), lines[-2])
        self.assertEqual(("|", "EXIT code=1 reason=uncaught KeyError: 'ltp'"), lines[-1])

    @unittest.skipIf(sys.platform == "win32", "no SIGTERM handler to run on Windows")
    def test_a_signal_the_runner_turns_into_an_exit(self) -> None:
        # The runner's own handler: note the signal, then leave through SystemExit.
        code, lines = self.run_script("""\
            import os, signal, time
            def stop(signum, frame):
                note_exit(0, f"signal {signal.Signals(signum).name}")
                raise SystemExit(0)
            signal.signal(signal.SIGTERM, stop)
            print('[RUNNER] waiting for ticks', flush=True)
            os.kill(os.getpid(), signal.SIGTERM)
            time.sleep(10)
        """)
        self.assertEqual(0, code)
        self.assertEqual(("|", "EXIT code=0 reason=signal SIGTERM"), lines[-1])

    def test_the_exit_line_comes_after_a_half_written_line(self) -> None:
        code, lines = self.run_script("sys.stdout.write('no newline yet')\n")
        self.assertEqual([("|", "no newline yet"), ("|", "EXIT code=0 reason=finished")], lines)


class NeverDyingLoopTests(unittest.TestCase):
    def test_failing_step_and_failing_log_keep_looping(self) -> None:
        calls: List[int] = []
        errors: List[BaseException] = []

        def step() -> None:
            calls.append(1)
            raise BrokenPipeError(32, "Broken pipe")           # send_heartbeat printing into a dead pipe

        def dead_print(_text: str) -> None:
            raise BrokenPipeError(32, "Broken pipe")           # the handler's own print fails too

        def record(ex: BaseException) -> None:
            errors.append(ex)
            raise ValueError("I/O operation on closed file")  # even the hook may print and die

        iterations = run_forever(step, 0.0, log=dead_print, on_error=record, sleep=lambda _s: None,
                                 max_iterations=5, label="heartbeat loop")
        self.assertEqual(iterations, 5)
        self.assertEqual(len(calls), 5)
        self.assertEqual(len(errors), 5)

    def test_unreachable_api_is_retried_every_interval(self) -> None:
        sleeps: List[float] = []
        outcomes = iter([ConnectionError("API down"), ConnectionError("API down"), None])
        beats: List[str] = []

        def step() -> None:
            outcome = next(outcomes)
            if outcome is not None:
                raise outcome
            beats.append("ok")

        logged: List[str] = []
        run_forever(step, 15, log=logged.append, sleep=sleeps.append, max_iterations=3)
        self.assertEqual(sleeps, [15, 15, 15])
        self.assertEqual(beats, ["ok"])
        self.assertEqual(len(logged), 2)
        self.assertIn("ConnectionError: API down", logged[0])

    def test_failing_sleep_does_not_end_the_loop(self) -> None:
        count = {"n": 0}

        def step() -> None:
            count["n"] += 1

        def bad_sleep(_s: float) -> None:
            raise OSError("no clock")

        self.assertEqual(run_forever(step, 1, sleep=bad_sleep, max_iterations=3), 3)
        self.assertEqual(count["n"], 3)

    def test_shutdown_signals_still_propagate(self) -> None:
        def step() -> None:
            raise SystemExit(0)

        with self.assertRaises(SystemExit):
            run_forever(step, 0, sleep=lambda _s: None, max_iterations=3)


if __name__ == "__main__":
    unittest.main()
