"""The shape every agent has."""
from __future__ import annotations

from abc import ABC, abstractmethod

from sentinel.context import SentinelContext
from sentinel.model import Finding


class Agent(ABC):
    """
    One watcher. ``check`` looks at the system once and returns what is wrong
    right now — an empty list means "nothing, as far as I can see". It is
    called every ``interval_seconds``.

    Returning the same fingerprint on consecutive checks keeps one incident
    open; not returning it for ``resolve_after`` consecutive checks closes it.
    A check that raises is itself reported (rule "agent-crashed"), so a broken
    watcher cannot fail silently — but it should not raise for an ordinary
    "could not reach X": that is a finding, or at most a debug line.
    """

    name: str = ""
    interval_seconds: int = 60
    resolve_after: int = 2

    @abstractmethod
    def check(self, ctx: SentinelContext) -> list[Finding]:
        ...

    def let_go(self, ctx: SentinelContext, fingerprints: set[str]) -> None:
        """
        Findings this agent repeated from memory (``observed_utc`` set) whose
        incident was resolved at or after they were last observed: the store
        did not reopen them. An agent that holds findings open stops holding
        these, so a later observation starts afresh; by default nothing is held.
        """
