"""What an agent reports, and what the store keeps."""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, timedelta
from enum import Enum
from typing import Any, Optional


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

# Evidence lines that start with this say the problem has happened before:
# how many times, when last, and what was done then (the Resolution a person
# wrote on the last episode in the console). Added once, when the incident
# opens, and kept across sightings like the context pack.
HISTORY_PREFIX = "history: "

# Evidence lines that start with this say the incident is flapping: it
# cleared and came back within FLAP_WINDOW, how many times, since when. The
# engine rewrites the line on every return; kept across sightings like the rest.
FLAP_PREFIX = "flapping: "

#: The prefixes of the lines Sentinel adds to an incident's evidence itself,
#: which a new sighting keeps instead of replacing with the agent's.
KEPT_PREFIXES = (HISTORY_PREFIX, CONTEXT_PREFIX, FLAP_PREFIX)

#: A problem that comes back within this long of Sentinel resolving it is the
#: same episode, not a new one: the resolved incident is reopened, and while it
#: keeps flapping it is messaged at most once per this window. 28 Sep 13:06-13:28:
#: the feed stalled for ~2 min every few minutes, and each stall opened a new
#: CRITICAL "ticks have stopped" incident per exchange group, with its NEW and
#: its RESOLVED message, where the problem was one flapping feed.
FLAP_WINDOW = timedelta(minutes=30)


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
    #: When the newest observation behind this finding was made, for a finding
    #: an agent repeats from memory rather than from what it saw this check (a
    #: log finding held open after its last line, the last answer carried while
    #: a source cannot be read). None: observed now. An incident resolved at or
    #: after this moment is not reopened by it — 28 Sep 13:08 the operator
    #: resolved four API errors of a deliberate Postgres restart (13:05-13:06),
    #: and at 13:11 their held findings opened them again as new incidents,
    #: counting the same lines.
    observed_utc: Optional[datetime] = None

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
