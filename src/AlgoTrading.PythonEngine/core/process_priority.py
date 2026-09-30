"""
Run a process at a lower CPU priority, from RUNNER_NICE.

The strategy runners are many (23 on 28 Sep) and each one's work can wait a
few milliseconds; the Dhan feed that all of them read cannot, because the
bytes it does not read pile up in the kernel until Dhan stops sending. So the
runners give way: `nice` 10 by default. On the 2-vCPU server that is the
difference between the feed's socket thread waiting behind every runner and
running first.

This module is imported before anything else in the runner, so it must stay
small: no core.config (which would load settings and make a logs folder), no
threads. On Linux a nice value belongs to a thread, and only threads started
afterwards inherit it — numpy's and OpenBLAS's worker pools among them — so it
has to be set before any of those exist.
"""

import os
from pathlib import Path

#: core/ -> AlgoTrading.PythonEngine/ -> src/ -> <repo root>, as core.config has it.
REPO_ROOT = Path(__file__).resolve().parents[3]


def _configured(var: str):
    """The variable from the environment, else from the repo's .env, else None."""
    value = os.environ.get(var)
    if value is not None:
        return value
    try:
        from dotenv import dotenv_values
        return dotenv_values(REPO_ROOT / ".env").get(var)
    except Exception:
        return None


def _say(log, text: str) -> None:
    """
    One line, which must never take the process down: this runs before the
    runner's safe stdio is installed, and when the API that spawned it has
    died a plain print() to its closed pipe raises.
    """
    try:
        log(text)
    except Exception:  # noqa: BLE001
        pass


def lower_priority_from_env(var: str = "RUNNER_NICE", default: int = 10, log=print) -> int:
    """
    Add `var` (default 10) to this process's nice value. Returns the increment
    applied: 0 when the value is 0 or less, invalid, or the platform has no
    nice (Windows). An invalid value is said once and changes nothing.
    """
    raw = _configured(var)
    if raw is None or str(raw).strip() == "":
        increment = default
    else:
        try:
            increment = int(str(raw).strip())
        except ValueError:
            _say(log, f"[priority] {var}={raw!r} is not a whole number — the priority is left as it is.")
            return 0
    if increment <= 0 or not hasattr(os, "nice"):
        return 0
    try:
        os.nice(increment)
    except OSError as ex:
        _say(log, f"[priority] could not lower the priority by {increment}: {ex}")
        return 0
    return increment
