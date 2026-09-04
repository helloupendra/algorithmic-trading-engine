"""
core/heartbeat.py

A periodic loop that never dies.

Background threads such as the ingestor's heartbeat must keep running no
matter what one iteration does: the API may be unreachable (retry next
tick), the status computation may throw, and — the case that actually bit
us — stdout may be a closed pipe, so even the `print` inside the exception
handler raises. `run_forever` guards every step, including the logging of
failures, and sleeps in a guarded call too. Only SystemExit / KeyboardInterrupt
(a deliberate shutdown) leave the loop; `max_iterations` bounds it for tests.
"""

from __future__ import annotations

import time
from typing import Callable, Optional

Step = Callable[[], None]
Logger = Callable[[str], None]
Sleeper = Callable[[float], None]
ErrorHook = Callable[[BaseException], None]


def _quiet(log: Optional[Logger], text: str) -> None:
    """Log without ever raising (the logger itself may be a dead pipe)."""
    if log is None:
        return
    try:
        log(text)
    except BaseException:
        pass


def run_forever(step: Step, interval_seconds: float, *, log: Optional[Logger] = None,
                sleep: Sleeper = time.sleep, on_error: Optional[ErrorHook] = None,
                max_iterations: Optional[int] = None, label: str = "loop") -> int:
    """
    Call `step()` every `interval_seconds` until the process exits.

    A failing `step` is reported through `log` and `on_error` (both guarded)
    and the loop goes on; a failing `sleep` (it can hardly fail, but the loop
    must not depend on it) is tolerated too. `max_iterations` stops after
    that many calls (tests only) and the count of calls is returned.
    """
    iterations = 0
    while max_iterations is None or iterations < max_iterations:
        iterations += 1
        try:
            step()
        except (SystemExit, KeyboardInterrupt):
            raise
        except BaseException as ex:      # noqa: BLE001 — the whole point is to survive anything
            if on_error is not None:
                try:
                    on_error(ex)
                except BaseException:
                    pass
            _quiet(log, f"ERROR IN {label.upper()}: {type(ex).__name__}: {ex}")
        try:
            sleep(interval_seconds)
        except (SystemExit, KeyboardInterrupt):
            raise
        except BaseException:
            pass
    return iterations
