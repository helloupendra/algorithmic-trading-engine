"""
Process-supervision hardening (core/safe_output.py, core/heartbeat.py):
writing after stdout became a broken pipe must never raise and must land in
the fallback log file; a periodic loop keeps going when both its step and its
error logging fail.
"""

import io
import os
import sys
import tempfile
import unittest
from typing import List

import _bootstrap  # noqa: F401

from core.heartbeat import run_forever
from core.safe_output import SafeStream, default_log_path, install_safe_stdio, is_installed


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
