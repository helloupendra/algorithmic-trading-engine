"""
Where incidents live.

The table belongs to the platform — it is created by the API's EF migration
(``incidents``, PascalCase quoted columns like every other table), so the
console can list what Sentinel found. Sentinel writes to it directly rather
than through the API, because the moment it most needs to record something is
the moment the API is not answering.

When the database is unreachable too, incidents go to a JSON-lines file under
``logs/sentinel/`` so nothing is lost; Telegram still gets the message.
"""
from __future__ import annotations

import json
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

from sentinel.model import CONTEXT_PREFIX, Finding, Severity, Status

# Statuses that mean "this problem is still being tracked": a new sighting of
# the same fingerprint updates it rather than opening another.
_LIVE = (Status.OPEN.value, Status.ACKNOWLEDGED.value)


def _evidence_list(raw: Any) -> list[str]:
    """The evidence column as a list. It is text, not jsonb: a value that does not parse is no evidence, not an error."""
    if isinstance(raw, str):
        try:
            raw = json.loads(raw)
        except ValueError:
            return []
    return [str(e) for e in raw] if isinstance(raw, list) else []


def _keep_context(evidence: list[str], before: list[str]) -> list[str]:
    """A new sighting's evidence, followed by the context pack the incident already carries."""
    return list(evidence) + [e for e in before if e.startswith(CONTEXT_PREFIX)]


def _with_context(before: list[str], context: list[str]) -> list[str]:
    """The incident's evidence with its context pack replaced by a newer one."""
    return [e for e in before if not e.startswith(CONTEXT_PREFIX)] + list(context)


@dataclass(frozen=True)
class Upserted:
    """What happened to a finding in the store."""

    incident_id: int
    is_new: bool
    escalated: bool
    severity: Severity
    occurrences: int
    first_seen_utc: Optional[datetime] = None


@dataclass(frozen=True)
class LiveIncident:
    incident_id: int
    fingerprint: str
    agent: str
    severity: Severity
    title: str


class IncidentStore(ABC):
    @abstractmethod
    def upsert(self, finding: Finding, now_utc: datetime) -> Upserted: ...

    @abstractmethod
    def live_for_agent(self, agent: str) -> list[LiveIncident]: ...

    @abstractmethod
    def resolve(self, incident_id: int, now_utc: datetime) -> None: ...

    @abstractmethod
    def mark_notified(self, incident_id: int, now_utc: datetime) -> None: ...

    def heartbeat(self, now_utc: datetime) -> None:
        """
        Record that a round of checks finished. Without it an empty incident
        list cannot be told apart from a Sentinel that has stopped looking.
        """

    def attach_context(self, incident_id: int, context: list[str]) -> None:
        """
        Put a context pack (sentinel/pack.py) in an incident's evidence, in
        place of the one it carried. Later sightings replace the agent's own
        evidence and keep the pack. A store that cannot keep one does nothing:
        the pack is still in the message.
        """


class MemoryIncidentStore(IncidentStore):
    """For tests, and for ``--dry-run``: the same behaviour, nothing persisted."""

    def __init__(self) -> None:
        self._rows: dict[int, dict] = {}
        self._next = 1
        self._lock = threading.Lock()

    def upsert(self, finding: Finding, now_utc: datetime) -> Upserted:
        with self._lock:
            for row in self._rows.values():
                if row["fingerprint"] == finding.fingerprint and row["status"] in _LIVE:
                    before = row["severity"]
                    after = max(before, finding.severity, key=lambda s: s.rank)
                    row.update(severity=after, title=finding.title, summary=finding.summary,
                               evidence=_keep_context(list(finding.evidence), row["evidence"]), last_seen=now_utc,
                               occurrences=row["occurrences"] + 1)
                    return Upserted(row["id"], False, after.rank > before.rank, after, row["occurrences"],
                                    row["first_seen"])
            row = dict(id=self._next, fingerprint=finding.fingerprint, agent=finding.agent,
                       rule=finding.rule, severity=finding.severity, status=Status.OPEN.value,
                       title=finding.title, summary=finding.summary, where=finding.where,
                       evidence=list(finding.evidence), suggestion=finding.suggestion,
                       first_seen=now_utc, last_seen=now_utc, occurrences=1,
                       resolved=None, notified=None)
            self._rows[self._next] = row
            self._next += 1
            return Upserted(row["id"], True, False, finding.severity, 1, now_utc)

    def live_for_agent(self, agent: str) -> list[LiveIncident]:
        with self._lock:
            return [LiveIncident(r["id"], r["fingerprint"], r["agent"], r["severity"], r["title"])
                    for r in self._rows.values() if r["agent"] == agent and r["status"] in _LIVE]

    def resolve(self, incident_id: int, now_utc: datetime) -> None:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is not None:
                row["status"] = Status.RESOLVED.value
                row["resolved"] = now_utc

    def mark_notified(self, incident_id: int, now_utc: datetime) -> None:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is not None:
                row["notified"] = now_utc

    def heartbeat(self, now_utc: datetime) -> None:
        self.last_check = now_utc

    def attach_context(self, incident_id: int, context: list[str]) -> None:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is not None:
                row["evidence"] = _with_context(row["evidence"], context)

    # Test helpers.
    last_check: Optional[datetime] = None

    def rows(self) -> list[dict]:
        with self._lock:
            return [dict(r) for r in self._rows.values()]


class PostgresIncidentStore(IncidentStore):
    """
    The ``incidents`` table, through psycopg2.

    One connection, reopened after any failure; every call is its own short
    transaction. A write that fails is appended to the fallback file and the
    error is raised to the engine, which keeps running.
    """

    def __init__(self, dsn: str, fallback: Path) -> None:
        self._dsn = dsn
        self._fallback = fallback
        self._conn = None
        self._lock = threading.Lock()

    def _connection(self):
        import psycopg2  # imported here so tests without a database never need it

        if self._conn is None or self._conn.closed:
            self._conn = psycopg2.connect(self._dsn, connect_timeout=5)
            self._conn.autocommit = False
        return self._conn

    def _run(self, work):
        with self._lock:
            try:
                conn = self._connection()
                with conn.cursor() as cur:
                    result = work(cur)
                conn.commit()
                return result
            except Exception:
                try:
                    if self._conn is not None:
                        self._conn.rollback()
                except Exception:
                    pass
                self._conn = None
                raise

    def upsert(self, finding: Finding, now_utc: datetime) -> Upserted:
        evidence = json.dumps(finding.evidence, ensure_ascii=False)

        def work(cur) -> Upserted:
            cur.execute(
                'SELECT "Id", "Severity", "Occurrences", "EvidenceJson", "FirstSeenUtc" FROM incidents '
                'WHERE "Fingerprint" = %s AND "Status" IN %s ORDER BY "Id" DESC LIMIT 1 FOR UPDATE',
                (finding.fingerprint, _LIVE),
            )
            row = cur.fetchone()
            if row is not None:
                incident_id, before_raw, occurrences, evidence_before, first_seen = row
                before = Severity(before_raw)
                after = max(before, finding.severity, key=lambda s: s.rank)
                kept = json.dumps(_keep_context(list(finding.evidence), _evidence_list(evidence_before)),
                                  ensure_ascii=False)
                cur.execute(
                    'UPDATE incidents SET "Severity" = %s, "Title" = %s, "Summary" = %s, "EvidenceJson" = %s, '
                    '"LastSeenUtc" = %s, "Occurrences" = "Occurrences" + 1 WHERE "Id" = %s',
                    (after.value, finding.title[:300], finding.summary, kept, now_utc, incident_id),
                )
                if isinstance(first_seen, datetime) and first_seen.tzinfo is None:
                    first_seen = first_seen.replace(tzinfo=timezone.utc)
                return Upserted(incident_id, False, after.rank > before.rank, after, occurrences + 1,
                                first_seen if isinstance(first_seen, datetime) else None)

            cur.execute(
                'INSERT INTO incidents ("Fingerprint", "Agent", "Rule", "Severity", "Status", "Title", "Summary", '
                '"Location", "EvidenceJson", "Suggestion", "Occurrences", "FirstSeenUtc", "LastSeenUtc") '
                'VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, 1, %s, %s) RETURNING "Id"',
                (finding.fingerprint, finding.agent, finding.rule, finding.severity.value, Status.OPEN.value,
                 finding.title[:300], finding.summary, finding.where[:300], evidence, finding.suggestion,
                 now_utc, now_utc),
            )
            return Upserted(cur.fetchone()[0], True, False, finding.severity, 1, now_utc)

        try:
            return self._run(work)
        except Exception:
            self._append_fallback(finding, now_utc)
            raise

    def live_for_agent(self, agent: str) -> list[LiveIncident]:
        def work(cur) -> list[LiveIncident]:
            cur.execute(
                'SELECT "Id", "Fingerprint", "Agent", "Severity", "Title" FROM incidents '
                'WHERE "Agent" = %s AND "Status" IN %s',
                (agent, _LIVE),
            )
            return [LiveIncident(r[0], r[1], r[2], Severity(r[3]), r[4]) for r in cur.fetchall()]

        return self._run(work)

    def resolve(self, incident_id: int, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE incidents SET "Status" = %s, "ResolvedUtc" = %s WHERE "Id" = %s AND "Status" IN %s',
            (Status.RESOLVED.value, now_utc, incident_id, _LIVE)))

    def mark_notified(self, incident_id: int, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE incidents SET "NotifiedUtc" = %s WHERE "Id" = %s', (now_utc, incident_id)))

    def heartbeat(self, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'INSERT INTO sentinel_heartbeat ("Id", "LastCheckUtc") VALUES (1, %s) '
            'ON CONFLICT ("Id") DO UPDATE SET "LastCheckUtc" = EXCLUDED."LastCheckUtc"',
            (now_utc,)))

    def attach_context(self, incident_id: int, context: list[str]) -> None:
        def work(cur) -> None:
            cur.execute('SELECT "EvidenceJson" FROM incidents WHERE "Id" = %s FOR UPDATE', (incident_id,))
            row = cur.fetchone()
            if row is None:
                return
            cur.execute('UPDATE incidents SET "EvidenceJson" = %s WHERE "Id" = %s',
                        (json.dumps(_with_context(_evidence_list(row[0]), context), ensure_ascii=False),
                         incident_id))

        self._run(work)

    def _append_fallback(self, finding: Finding, now_utc: datetime) -> None:
        try:
            self._fallback.parent.mkdir(parents=True, exist_ok=True)
            with self._fallback.open("a", encoding="utf-8") as fh:
                fh.write(json.dumps({
                    "at": now_utc.isoformat(), "fingerprint": finding.fingerprint, "agent": finding.agent,
                    "rule": finding.rule, "severity": finding.severity.value, "title": finding.title,
                    "summary": finding.summary, "where": finding.where, "evidence": finding.evidence,
                    "suggestion": finding.suggestion,
                }, ensure_ascii=False) + "\n")
        except OSError:
            pass


def dsn_from_env(env: dict[str, str]) -> Optional[str]:
    """A psycopg2 DSN from the same .env keys the API's settings are built from."""
    password = env.get("POSTGRES_PASSWORD", "")
    if not password:
        return None
    parts = {
        "host": env.get("POSTGRES_HOST", "localhost"),
        "port": env.get("POSTGRES_PORT", "5432"),
        "dbname": env.get("POSTGRES_DB", "algotrading"),
        "user": env.get("POSTGRES_USER", "postgres"),
        "password": password,
    }
    return " ".join(f"{k}='{v.replace(chr(39), chr(92) + chr(39))}'" for k, v in parts.items())
