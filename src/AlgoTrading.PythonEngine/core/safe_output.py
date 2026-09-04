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
"""

from __future__ import annotations

import os
import signal
import sys
import threading
from pathlib import Path
from typing import IO, Optional, TextIO

# core/ -> AlgoTrading.PythonEngine/ -> src/ -> <repo root>
REPO_ROOT = Path(__file__).resolve().parents[3]
ENGINE_LOG_DIR = REPO_ROOT / "logs" / "engine"

_BROKEN = (OSError, ValueError)   # BrokenPipeError is an OSError; ValueError = closed file


def default_log_path(name: Optional[str] = None) -> str:
    """`logs/engine/<name>-<pid>.log` (name defaults to the running script's stem)."""
    if not name:
        script = Path(sys.argv[0]).stem if sys.argv and sys.argv[0] else ""
        name = script or "engine"
    safe = "".join(ch if ch.isalnum() or ch in "-_." else "-" for ch in str(name)).strip("-") or "engine"
    return str(ENGINE_LOG_DIR / f"{safe}-{os.getpid()}.log")


class SafeStream:
    """
    A text stream wrapper that never raises. Writes go to the wrapped stream
    until it breaks; from then on they go to the fallback log file only.
    Thread-safe: the heartbeat thread, the tick executor and the main loop
    all print concurrently.
    """

    def __init__(self, target: Optional[TextIO], log_path: Optional[str], label: str = "") -> None:
        self._target = target
        self._log_path = log_path
        self._label = label
        self._dead = target is None
        self._log: Optional[IO[str]] = None
        self._log_failed = False
        self._lock = threading.RLock()

    # --- state ------------------------------------------------------------

    @property
    def target(self) -> Optional[TextIO]:
        return self._target

    @property
    def log_path(self) -> Optional[str]:
        return self._log_path

    @log_path.setter
    def log_path(self, value: Optional[str]) -> None:
        with self._lock:
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
        self._write_log(
            f"[safe_output] {self._label or 'stream'} lost ({type(why).__name__}: {why}); "
            f"output continues in this file only\n"
        )

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

    def close(self) -> None:
        """Never closes the wrapped stream; only releases the log file."""
        with self._lock:
            self._close_log()

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


def install_safe_stdio(log_path: Optional[str] = None, *, name: Optional[str] = None) -> str:
    """
    Wrap sys.stdout / sys.stderr (idempotent). `log_path` names the fallback
    log file; without it the file is `logs/engine/<name>-<pid>.log`. Calling
    it again only re-points the log file (a runner installs it before it
    knows its run id, then renames once it does). Returns the log path.
    """
    _ignore_sigpipe()
    path = log_path or default_log_path(name)
    for attr in ("stdout", "stderr"):
        current = getattr(sys, attr, None)
        if isinstance(current, SafeStream):
            current.log_path = path
            continue
        setattr(sys, attr, SafeStream(current, path, label=f"sys.{attr}"))
    return path


def is_installed() -> bool:
    return isinstance(sys.stdout, SafeStream) and isinstance(sys.stderr, SafeStream)
