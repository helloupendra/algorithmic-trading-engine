"""Shared stand-ins for Sentinel's tests: a context with no network, a recording notifier."""
from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.context import SentinelContext
from sentinel.notify import Notifier


class RecordingNotifier(Notifier):
    def __init__(self) -> None:
        self.sent: list[str] = []

    def send(self, text: str) -> bool:
        self.sent.append(text)
        return True


def make_context(tmp: Path, api: Optional[dict[str, Any]] = None,
                 now: datetime = datetime(2026, 9, 24, 6, 0, tzinfo=timezone.utc),
                 redis_obj: Any = None, env: Optional[dict[str, str]] = None) -> SentinelContext:
    """
    A context rooted in ``tmp``. ``api`` maps a path (or a path prefix ending in
    '*') to a body, or to an Exception to raise.
    """
    routes = api or {}

    def api_get(path: str) -> Any:
        body = routes.get(path)
        if body is None:
            for key, value in routes.items():
                if key.endswith("*") and path.startswith(key[:-1]):
                    body = value
                    break
        if body is None:
            raise ConnectionError(f"no route for {path}")
        if isinstance(body, Exception):
            raise body
        if callable(body):
            return body(path)
        return body

    return SentinelContext(repo_root=tmp, env=env or {}, api_get=api_get,
                           redis_factory=lambda: redis_obj, clock=lambda: now)


def clock_ticks(start: float = 0.0, step: float = 1000.0) -> Callable[[], float]:
    """A monotonic clock that moves far enough on every read for every agent to be due."""
    state = {"t": start}

    def tick() -> float:
        state["t"] += step
        return state["t"]

    return tick
