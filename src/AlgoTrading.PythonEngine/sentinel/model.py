"""What an agent reports, and what the store keeps."""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class Severity(str, Enum):
    """How loudly to shout. Ordered: a finding can only escalate."""

    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    CRITICAL = "critical"

    @property
    def rank(self) -> int:
        return _RANK[self]

    @property
    def icon(self) -> str:
        return _ICON[self]


_RANK = {Severity.LOW: 0, Severity.MEDIUM: 1, Severity.HIGH: 2, Severity.CRITICAL: 3}
_ICON = {Severity.LOW: "·", Severity.MEDIUM: "!", Severity.HIGH: "‼", Severity.CRITICAL: "🔴"}

# Evidence lines that start with this are the incident's context pack (what
# changed around it: the last deploy, the live commit, the runs, the logs; see
# sentinel/pack.py), not the agent's own observations. They share the evidence
# column because the incidents table is a contract with the API and has no
# other place for them: the store keeps them across sightings, and replaces
# them only with a newer pack.
CONTEXT_PREFIX = "context: "


@dataclass(frozen=True)
class Finding:
    """
    One thing an agent saw, in one check.

    ``fingerprint`` is the identity of the problem, not of the observation: the
    same feed being silent at 11:28 and at 11:29 is one incident with two
    sightings, so both checks must produce the same fingerprint. Put in it what
    distinguishes one problem from another (the rule, the symbol, the run, the
    account) and nothing that changes from one check to the next (a count, an
    age, a timestamp).
    """

    agent: str
    rule: str
    severity: Severity
    title: str
    summary: str
    fingerprint: str
    where: str = ""
    evidence: list[str] = field(default_factory=list)
    suggestion: str = ""
    extra: dict[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.fingerprint:
            raise ValueError("a finding needs a fingerprint")
        if len(self.fingerprint) > 200:
            raise ValueError("fingerprint longer than 200 characters")


class Status(str, Enum):
    OPEN = "open"
    ACKNOWLEDGED = "acknowledged"
    RESOLVED = "resolved"


@dataclass
class Incident:
    """A problem as the store holds it: a finding with a history."""

    id: int
    fingerprint: str
    agent: str
    rule: str
    severity: Severity
    status: Status
    title: str
    summary: str
    where: str
    evidence: list[str]
    suggestion: str
    occurrences: int
    first_seen_utc: str
    last_seen_utc: str
    resolved_utc: str | None = None
