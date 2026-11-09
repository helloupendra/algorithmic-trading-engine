"""A bounded, run-scoped view of the API book for stateful strategies."""

from time import monotonic
from typing import Any, Callable, Optional


class PositionSnapshot:
    def __init__(self, fetch: Callable[[], Any], *, clock: Callable[[], float] = monotonic):
        self._fetch, self._clock = fetch, clock
        self._until = 0.0
        self._groups: Optional[list[str]] = None

    def invalidate(self) -> None:
        # A booked order changes the book before the ordinary poll is due.
        self._until = 0.0

    def groups(self) -> Optional[list[str]]:
        now = self._clock()
        if now < self._until:
            return None if self._groups is None else list(self._groups)
        try:
            rows = self._fetch()
            if not isinstance(rows, list):
                raise ValueError("Expected a position list")
            groups = set()
            for row in rows:
                if not isinstance(row, dict) or row.get("status") not in ("Open", "Closed", "Carried"):
                    raise ValueError("Unknown position status")
                if row["status"] == "Open":
                    group = row.get("groupId")
                    if not isinstance(group, str) or not group.strip():
                        raise ValueError("Open position has no group")
                    groups.add(group)
            self._groups = sorted(groups)
        except Exception:
            # Unknown is never an empty book. The strategy still manages known
            # exits, but cannot open another position using this answer.
            self._groups = None
        self._until = now + 5.0
        return None if self._groups is None else list(self._groups)
