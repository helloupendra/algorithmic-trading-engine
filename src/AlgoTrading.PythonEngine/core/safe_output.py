"""
core/safe_output.py

Console output that survives the parent process dying.

The API launches the engine's processes (ingestor, strategy runners, backtest
runners) with redirected stdout/stderr. When the API is restarted or crashes
the pipe closes and the child's next `print()` raises BrokenPipeError — in a
thread that prints inside its own exception handler that kills the thread
(the ingestor's heartbeat loop died exactly this way) while the rest of the
process keeps running blind.

`install_safe_stdio()` wraps `sys.stdout` / `sys.stderr` in a writer that

  (a) tries the original stream first,
  (b) on OSError / BrokenPipeError / ValueError ("I/O operation on closed
      file") marks the stream dead and from then on writes ONLY to a
      line-buffered log file (`logs/engine/<name>-<pid>.log`, directories
      created on demand),
  (c) never raises from `write()` / `flush()`,
  (d) treats UnicodeEncodeError (a ValueError subclass raised by a cp1252
      pipe on Windows when a line carries "→" or "₹") as an encoding problem,
      not a dead pipe: the line is re-written with the unencodable characters
      replaced and the stream stays in use.

Install it first thing in every long-running entry point:

    from core.safe_output import install_safe_stdio
    install_safe_stdio(name="ingestor")           # logs/engine/ingestor-<pid>.log
    install_safe_stdio(name=f"runner-{run_id}")   # re-install renames the log

It also registers SIGPIPE as ignored where the platform has it (Python does
so already; keeping it explicit documents the intent).

A strategy runner keeps the file from its FIRST line instead (`tee=True`,
see `LineLog`): every line, stamped with its time and stream, whether or not
the pipe is alive. After an API restart the API adopts a runner with no pipes
at all, and that file is the only place its output exists. `install_exit_line()`
ends that output with one line saying how the process ended.

A daemon the API launches (a live feed, the chain poller) is told to do the
same through the environment: `ENGINE_LOG_NAME=<name>` turns the tee on and
pins the file to `logs/engine/<name>-<pid>.log`, whatever name the script
itself asks for. The API tails that file into its own log, so a feed it adopts
after a restart keeps being logged; on 28 Sep, twice, an adopted feed's pipes
had no reader and everything it said until the next restart was lost.
"""

from __future__ import annotations

import atexit
import os
import signal
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import IO, Optional, Sequence, TextIO, Tuple

# core/ -> AlgoTrading.PythonEngine/ -> src/ -> <repo root>
REPO_ROOT = Path(__file__).resolve().parents[3]
ENGINE_LOG_DIR = REPO_ROOT / "logs" / "engine"

_BROKEN = (OSError, ValueError)   # BrokenPipeError is an OSError; ValueError = closed file

#: Set by the API's daemon supervisor (PythonDaemonSupervisor) on a process it
#: launches. The API computes the file's path from this name and the pid it
#: launched, so the name the script passes cannot be allowed to differ from it.
LOG_NAME_ENV = "ENGINE_LOG_NAME"


def pinned_log_name() -> Optional[str]:
    """The log name the launching API pinned (`ENGINE_LOG_NAME`), or None."""
    value = (os.environ.get(LOG_NAME_ENV) or "").strip()
    return value or None


def default_log_path(name: Optional[str] = None) -> str:
    """`logs/engine/<name>-<pid>.log` (name defaults to the running script's stem)."""
    if not name:
        script = Path(sys.argv[0]).stem if sys.argv and sys.argv[0] else ""
        name = script or "engine"
    safe = "".join(ch if ch.isalnum() or ch in "-_." else "-" for ch in str(name)).strip("-") or "engine"
    return str(ENGINE_LOG_DIR / f"{safe}-{os.getpid()}.log")


def log_name_for_run(prefix: str, argv: Sequence[str]) -> str:
    """
    `<prefix>-<run id>` from a `--run-id N` (or `--run-id=N`) argument, or
    just `<prefix>` without one. A runner knows its run id from its command
    line before it has printed anything, so its log can carry the run id from
    the first line rather than be renamed once argparse has run.
    """
    for i, arg in enumerate(argv):
        value = None
        if arg == "--run-id" and i + 1 < len(argv):
            value = argv[i + 1]
        elif arg.startswith("--run-id="):
            value = arg.split("=", 1)[1]
        if value is not None and value.isdigit():
            return f"{prefix}-{value}"
    return prefix


def utc_stamp(now: Optional[datetime] = None) -> str:
    """ISO 8601 in UTC to the millisecond: 2026-09-28T03:45:01.123Z."""
    moment = now or datetime.now(timezone.utc)
    return moment.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


class LineLog:
    """
    A process's output log kept from its first line: every line written to
    stdout or stderr, stamped with the UTC time it was completed and the
    stream it came from, "|" for stdout and "!" for stderr:

        2026-09-28T03:45:01.123Z | [NIFTY] Using expiry: 2026-09-30
        2026-09-28T03:45:02.456Z ! KeyError: 'ltp'

    Those are the marks the API's run console already uses, so the API reads
    this file into a run's console line for line. It does so for every runner,
    launched or adopted: an adopted runner has no pipes, and on 24 Sep, with
    all 13 live runs adopted after a restart, the console showed nothing but
    the API's own notes — warnings and "SIGNAL REFUSED" lines were nowhere.

    One LineLog serves both streams, so their lines keep the order they were
    written in. It never raises: a log that cannot be written is given up on,
    and the pipe carries on.
    """

    #: An unfinished line longer than this is written as it stands, so a
    #: process printing without newlines cannot grow the buffer without bound.
    MAX_PENDING = 64 * 1024

    def __init__(self, path: str) -> None:
        self._path = path
        self._file: Optional[IO[str]] = None
        self._failed = False
        self._pending = {"|": "", "!": ""}
        self._lock = threading.RLock()

    @property
    def path(self) -> str:
        return self._path

    @path.setter
    def path(self, value: str) -> None:
        with self._lock:
            if value == self._path:
                return
            # An unfinished line stays in the buffer and ends up in the new file.
            self.close()
            self._path = value
            self._failed = False

    def write(self, marker: str, text: str) -> None:
        with self._lock:
            buffered = self._pending.get(marker, "") + text
            *lines, rest = buffered.split("\n")
            if len(rest) > self.MAX_PENDING:
                lines.append(rest)
                rest = ""
            self._pending[marker] = rest
            for line in lines:
                self._emit(marker, line)

    def flush_pending(self) -> None:
        """Writes every unfinished line as it stands: the process is ending."""
        with self._lock:
            for marker, rest in list(self._pending.items()):
                if rest:
                    self._pending[marker] = ""
                    self._emit(marker, rest)

    def flush(self) -> None:
        with self._lock:
            if self._file is not None:
                try:
                    self._file.flush()
                except Exception:
                    pass

    def close(self) -> None:
        with self._lock:
            if self._file is not None:
                try:
                    self._file.close()
                except Exception:
                    pass
                self._file = None

    def _emit(self, marker: str, line: str) -> None:
        handle = self._open()
        if handle is None:
            return
        try:
            handle.write(f"{utc_stamp()} {marker} {line.rstrip(chr(13))}\n")
        except Exception:
            self.close()
            self._failed = True

    def _open(self) -> Optional[IO[str]]:
        if self._file is not None:
            return self._file
        if self._failed or not self._path:
            return None
        try:
            Path(self._path).parent.mkdir(parents=True, exist_ok=True)
            self._file = open(self._path, "a", buffering=1, encoding="utf-8", errors="replace")
        except Exception:
            self._failed = True
            self._file = None
        return self._file


class SafeStream:
    """
    A text stream wrapper that never raises. Writes go to the wrapped stream
    until it breaks; from then on they go to the fallback log file only.
    Thread-safe: the heartbeat thread, the tick executor and the main loop
    all print concurrently.

    With a `tee` (a `LineLog`) every write goes to that log as well, from the
    first line, marked with `marker`; the pipe breaking then changes nothing
    about the log, and nothing is written to it twice.
    """

    def __init__(self, target: Optional[TextIO], log_path: Optional[str], label: str = "",
                 tee: Optional[LineLog] = None, marker: str = "|") -> None:
        self._target = target
        self._log_path = log_path
        self._label = label
        self._dead = target is None
        self._log: Optional[IO[str]] = None
        self._log_failed = False
        self._lock = threading.RLock()
        self._tee = tee
        self._marker = marker

    # --- state ------------------------------------------------------------

    @property
    def target(self) -> Optional[TextIO]:
        return self._target

    @property
    def tee(self) -> Optional[LineLog]:
        return self._tee

    @property
    def log_path(self) -> Optional[str]:
        return self._tee.path if self._tee is not None else self._log_path

    @log_path.setter
    def log_path(self, value: Optional[str]) -> None:
        with self._lock:
            if self._tee is not None:
                if value:
                    self._tee.path = value
                return
            if value == self._log_path:
                return
            self._close_log()
            self._log_path = value
            self._log_failed = False

    @property
    def dead(self) -> bool:
        """True once the wrapped stream has failed and output goes to the log file only."""
        return self._dead

    @property
    def encoding(self) -> str:
        return getattr(self._target, "encoding", None) or "utf-8"

    @property
    def errors(self) -> str:
        return getattr(self._target, "errors", None) or "replace"

    def isatty(self) -> bool:
        try:
            return bool(self._target is not None and not self._dead and self._target.isatty())
        except Exception:
            return False

    def fileno(self) -> int:
        if self._target is None:
            raise OSError("stream has no file descriptor")
        return self._target.fileno()

    def writable(self) -> bool:
        return True

    def readable(self) -> bool:
        return False

    def seekable(self) -> bool:
        return False

    @property
    def closed(self) -> bool:
        return False

    # --- log file -----------------------------------------------------------

    def _open_log(self) -> Optional[IO[str]]:
        if self._log is not None:
            return self._log
        if self._log_failed or not self._log_path:
            return None
        try:
            Path(self._log_path).parent.mkdir(parents=True, exist_ok=True)
            self._log = open(self._log_path, "a", buffering=1, encoding="utf-8", errors="replace")
        except Exception:
            self._log_failed = True
            self._log = None
        return self._log

    def _close_log(self) -> None:
        if self._log is not None:
            try:
                self._log.close()
            except Exception:
                pass
            self._log = None

    def _write_log(self, text: str) -> None:
        """Text the pipe did not take. With a tee it is in the log already."""
        if self._tee is not None:
            return
        log = self._open_log()
        if log is None:
            return
        try:
            log.write(text)
        except Exception:
            self._close_log()
            self._log_failed = True

    def _mark_dead(self, why: BaseException) -> None:
        self._dead = True
        note = (f"[safe_output] {self._label or 'stream'} lost ({type(why).__name__}: {why}); "
                f"output continues in this file only\n")
        if self._tee is not None:
            self._tee.write("!", note)
        else:
            self._write_log(note)

    def _encodable(self, text: str, failure: UnicodeEncodeError) -> str:
        """
        `text` with every character the wrapped stream cannot encode replaced,
        so a "→" printed into a cp1252 pipe (Windows, no PYTHONIOENCODING) still
        goes out as "?" instead of raising UnicodeEncodeError. The codec is the
        one the failure names (what the stream really used), falling back to
        the stream's declared encoding, then to ASCII.
        """
        for encoding in (getattr(failure, "encoding", None), self.encoding):
            if not encoding:
                continue
            try:
                return text.encode(encoding, "replace").decode(encoding, "replace")
            except LookupError:
                continue
        return text.encode("ascii", "replace").decode("ascii")

    # --- stream API ---------------------------------------------------------

    def write(self, text: str) -> int:
        if not isinstance(text, str):
            text = str(text)
        with self._lock:
            if self._tee is not None:
                self._tee.write(self._marker, text)
            if not self._dead and self._target is not None:
                try:
                    self._target.write(text)
                    return len(text)
                except UnicodeEncodeError as encoding_failure:
                    # An encoding failure is NOT a dead pipe (UnicodeEncodeError
                    # is a ValueError, so it must be caught before _BROKEN):
                    # retry once with the unencodable characters replaced and
                    # keep the stream alive.
                    try:
                        self._target.write(self._encodable(text, encoding_failure))
                        return len(text)
                    except UnicodeEncodeError:
                        # Still not encodable (a stream with a broken codec):
                        # this line goes to the log only; the pipe stays in use.
                        self._write_log(text)
                        return len(text)
                    except _BROKEN as ex:
                        self._mark_dead(ex)
                    except Exception as ex:
                        self._mark_dead(ex)
                except _BROKEN as ex:
                    self._mark_dead(ex)
                except Exception as ex:   # anything else: treat the same, never propagate
                    self._mark_dead(ex)
            self._write_log(text)
            return len(text)

    def writelines(self, lines) -> None:
        for line in lines:
            self.write(line)

    def flush(self) -> None:
        with self._lock:
            if not self._dead and self._target is not None:
                try:
                    self._target.flush()
                except UnicodeEncodeError:
                    # Buffered text the codec rejected: the pipe itself is fine.
                    pass
                except _BROKEN as ex:
                    self._mark_dead(ex)
                except Exception as ex:
                    self._mark_dead(ex)
            if self._log is not None:
                try:
                    self._log.flush()
                except Exception:
                    pass
            if self._tee is not None:
                self._tee.flush()

    def close(self) -> None:
        """Never closes the wrapped stream; only releases the log file (reopened on the next write)."""
        with self._lock:
            self._close_log()
            if self._tee is not None:
                self._tee.close()

    def __getattr__(self, item: str):
        # Anything else (buffer, name, mode, ...) comes from the wrapped stream.
        target = self.__dict__.get("_target")
        if target is None:
            raise AttributeError(item)
        return getattr(target, item)


def _ignore_sigpipe() -> None:
    sigpipe = getattr(signal, "SIGPIPE", None)
    if sigpipe is None:
        return
    try:
        signal.signal(sigpipe, signal.SIG_IGN)
    except (ValueError, OSError):
        # Not the main thread / unsupported: Python's default already ignores it.
        pass


def install_safe_stdio(log_path: Optional[str] = None, *, name: Optional[str] = None, tee: bool = False) -> str:
    """
    Wrap sys.stdout / sys.stderr (idempotent). `log_path` names the fallback
    log file; without it the file is `logs/engine/<name>-<pid>.log`. Calling
    it again only re-points the log file (a runner installs it before it
    knows its run id, then renames once it does). Returns the log path.

    With `tee=True` the file is kept from the first line, every line stamped
    (`LineLog`), instead of only once the pipe has died. It takes effect on
    the first install; a later call only re-points the file.

    With `ENGINE_LOG_NAME` in the environment both are decided by the API that
    launched the process: the tee is on and the file is
    `logs/engine/<that name>-<pid>.log`, whatever the arguments say. The API
    reads that exact path; a script choosing its own name would be tailed at
    a file that never appears.
    """
    _ignore_sigpipe()
    pinned = pinned_log_name()
    if pinned:
        log_path, name, tee = None, pinned, True
    path = log_path or default_log_path(name)
    shared = next((s.tee for s in (sys.stdout, sys.stderr) if isinstance(s, SafeStream) and s.tee), None)
    if tee and shared is None:
        shared = LineLog(path)
    for attr, marker in (("stdout", "|"), ("stderr", "!")):
        current = getattr(sys, attr, None)
        if isinstance(current, SafeStream):
            current.log_path = path
            continue
        setattr(sys, attr, SafeStream(current, path, label=f"sys.{attr}", tee=shared, marker=marker))
    return path


def is_installed() -> bool:
    return isinstance(sys.stdout, SafeStream) and isinstance(sys.stderr, SafeStream)


# --- the EXIT line -------------------------------------------------------------

_exit_lock = threading.Lock()
_exit_installed = False
_exit_note: Optional[Tuple[int, str]] = None


def note_exit(code: int, reason: str) -> None:
    """
    Why the process is about to end, for its EXIT line. The last explanation
    wins: a SIGTERM whose shutdown then crashes in a `finally` exits as the
    crash, and should say so.
    """
    global _exit_note
    _exit_note = (int(code), str(reason))


def _exit_status(status: object) -> Tuple[int, str]:
    """The code and reason `sys.exit(status)` ends the process with."""
    if status is None:
        return 0, "sys.exit()"
    if isinstance(status, int):
        return int(status), f"sys.exit({status})"
    # A message: Python prints it to stderr and exits with 1.
    return 1, str(status)


def exit_line() -> str:
    """The line `install_exit_line` writes, from what has been noted so far."""
    code, reason = _exit_note if _exit_note is not None else (0, "finished")
    return f"EXIT code={code} reason={' '.join(reason.split()) or '-'}"


def install_exit_line() -> None:
    """
    End the process's output with one line saying how it ended:

        EXIT code=1 reason=uncaught KeyError: 'ltp'
        EXIT code=0 reason=signal SIGTERM
        EXIT code=2 reason=sys.exit(2)

    The API reads a runner's output from its log file, and for a runner it
    adopted after a restart it has no exit code of its own: the reason read
    "exit code unknown". Written from atexit, so it covers the script ending,
    sys.exit() and an uncaught exception; a signal handler says which signal
    with `note_exit`. SIGKILL and os._exit write nothing, and a missing line
    is itself that answer. Idempotent.

    CPython consumes SystemExit before atexit runs, leaving no way to read the
    code there, so `sys.exit` is wrapped to remember its argument first. A
    bare `raise SystemExit(n)` is not seen; the runner raises one only from
    its signal handler, which notes the signal itself.
    """
    global _exit_installed
    with _exit_lock:
        if _exit_installed:
            return
        _exit_installed = True

    previous_hook = sys.excepthook

    def remember_crash(exc_type, exc, tb):
        note_exit(1, f"uncaught {exc_type.__name__}: {exc}")
        previous_hook(exc_type, exc, tb)

    sys.excepthook = remember_crash

    original_exit = sys.exit

    def remember_exit(status: object = None):
        note_exit(*_exit_status(status))
        original_exit(status)

    sys.exit = remember_exit
    atexit.register(_write_exit_line)


def _write_exit_line() -> None:
    # Whatever is half-written goes first, so the EXIT line is the last one.
    for stream in (sys.stdout, sys.stderr):
        if isinstance(stream, SafeStream) and stream.tee is not None:
            stream.tee.flush_pending()
    try:
        print(exit_line(), file=sys.stdout, flush=True)
    except Exception:
        pass
