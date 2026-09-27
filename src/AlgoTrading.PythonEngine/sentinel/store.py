"""
Where incidents live.

The table belongs to the platform — it is created by the API's EF migration
(``incidents``, PascalCase quoted columns like every other table), so the
console can list what Sentinel found. Sentinel writes to it directly rather
than through the API, because the moment it most needs to record something is
the moment the API is not answering.

When the database is unreachable too, incidents go to a JSON-lines file under
``logs/sentinel/`` so nothing is lost; Telegram still gets the message. The
file is capped (FALLBACK_MAX_BYTES, then one older generation): a database
down for a day must not fill the disk with the same sighting every 30 s.

One watcher per database: the service takes a Postgres advisory lock
(:class:`WatchLock`), so a second ``python3 -m sentinel`` started by hand
while the service runs exits instead of sending every message twice.
"""
from __future__ import annotations

import json
import logging
import os
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Optional

from sentinel.model import KEPT_PREFIXES, Finding, Severity, Status

log = logging.getLogger("sentinel.store")

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


def _text(value: Any) -> str:
    """
    A value psycopg2 can send. Log text can carry a NUL, which Postgres text
    cannot hold and psycopg2 refuses before the query is even sent — the same
    finding would then fail on every sighting, for good — and a lone
    surrogate, which is not UTF-8 at all.
    """
    text = "" if value is None else str(value)
    return text.replace("\x00", "\ufffd").encode("utf-8", "replace").decode("utf-8")


def _json(value: Any) -> str:
    return _text(json.dumps(value, ensure_ascii=False))


def _unique_violation(exc: BaseException) -> bool:
    """Postgres's unique_violation (23505), without importing psycopg2 where tests have none."""
    return getattr(exc, "pgcode", None) == "23505"


def _keep_context(evidence: list[str], before: list[str]) -> list[str]:
    """A new sighting's evidence, followed by what Sentinel added itself: the seen-before lines, the context pack."""
    return list(evidence) + [e for e in before if e.startswith(KEPT_PREFIXES)]


def _with_context(before: list[str], added: list[str]) -> list[str]:
    """
    The incident's evidence with Sentinel's own lines added: each kind given
    (a context pack, seen-before lines) replaces the lines of that kind it
    carried; a kind not given is kept. A newer pack must not drop the history.
    """
    kinds = tuple(p for p in KEPT_PREFIXES if any(line.startswith(p) for line in added))
    return [e for e in before if not (kinds and e.startswith(kinds))] + list(added)


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
class EarlierEpisodes:
    """
    The resolved episodes of a fingerprint before the incident just opened:
    how many, when the latest was last seen, and what a person wrote there
    under "what was done" (the Resolution column the console fills), if anything.
    """

    count: int
    last_seen_utc: Optional[datetime]
    last_resolution: str = ""


@dataclass(frozen=True)
class LiveIncident:
    incident_id: int
    fingerprint: str
    agent: str
    severity: Severity
    title: str


@dataclass(frozen=True)
class UnsentIncident:
    """A live incident whose message never went out (its NotifiedUtc is empty): enough to send it now."""

    incident_id: int
    fingerprint: str
    agent: str
    rule: str
    severity: Severity
    title: str
    summary: str
    where: str
    evidence: list[str]
    suggestion: str
    first_seen_utc: Optional[datetime] = None


class IncidentStore(ABC):
    @abstractmethod
    def upsert(self, finding: Finding, now_utc: datetime) -> Upserted: ...

    @abstractmethod
    def live_for_agent(self, agent: str) -> list[LiveIncident]: ...

    @abstractmethod
    def resolve(self, incident_id: int, now_utc: datetime) -> bool:
        """
        Resolve a live incident; True when this call changed it. False when it
        was no longer live — a person resolved it from the console in the
        meantime — so there is nothing to announce.
        """

    @abstractmethod
    def mark_notified(self, incident_id: int, now_utc: datetime) -> None: ...

    def unnotified_live(self, limit: int) -> list[UnsentIncident]:
        """
        Live incidents above low whose message never went out, oldest first:
        a send that failed before Sentinel restarted. A store that cannot tell
        returns none.
        """
        return []

    def heartbeat(self, now_utc: datetime) -> None:
        """
        Record that a round of checks finished. Without it an empty incident
        list cannot be told apart from a Sentinel that has stopped looking.
        """

    def attach_context(self, incident_id: int, context: list[str]) -> None:
        """
        Put a context pack (sentinel/pack.py), or seen-before lines, in an
        incident's evidence, in place of the lines of that kind it carried.
        Later sightings replace the agent's own evidence and keep these. A
        store that cannot keep them does nothing: they are still in the message.
        """

    def earlier_episodes(self, fingerprint: str, incident_id: int) -> Optional[EarlierEpisodes]:
        """
        The resolved episodes of ``fingerprint`` other than ``incident_id``,
        or None when there are none. A store that cannot tell returns None;
        the engine asks after the incident is stored, so a failed read costs
        the "seen before" line and nothing else.
        """
        return None


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

    def resolve(self, incident_id: int, now_utc: datetime) -> bool:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is None or row["status"] not in _LIVE:
                return False
            row["status"] = Status.RESOLVED.value
            row["resolved"] = now_utc
            return True

    def mark_notified(self, incident_id: int, now_utc: datetime) -> None:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is not None:
                row["notified"] = now_utc

    def unnotified_live(self, limit: int) -> list[UnsentIncident]:
        with self._lock:
            rows = [r for r in sorted(self._rows.values(), key=lambda r: r["id"])
                    if r["status"] in _LIVE and r["notified"] is None and r["severity"] is not Severity.LOW]
            return [UnsentIncident(r["id"], r["fingerprint"], r["agent"], r["rule"], r["severity"], r["title"],
                                   r["summary"], r["where"], list(r["evidence"]), r["suggestion"], r["first_seen"])
                    for r in rows[:limit]]

    def heartbeat(self, now_utc: datetime) -> None:
        self.last_check = now_utc

    def attach_context(self, incident_id: int, context: list[str]) -> None:
        with self._lock:
            row = self._rows.get(incident_id)
            if row is not None:
                row["evidence"] = _with_context(row["evidence"], context)

    def earlier_episodes(self, fingerprint: str, incident_id: int) -> Optional[EarlierEpisodes]:
        with self._lock:
            done = sorted((r for r in self._rows.values()
                           if r["fingerprint"] == fingerprint and r["id"] != incident_id
                           and r["status"] == Status.RESOLVED.value),
                          key=lambda r: (r["first_seen"], r["id"]))
            if not done:
                return None
            last = done[-1]
            return EarlierEpisodes(len(done), last["last_seen"], last.get("resolution") or "")

    # Test helpers.
    last_check: Optional[datetime] = None

    def rows(self) -> list[dict]:
        with self._lock:
            return [dict(r) for r in self._rows.values()]

    def write_resolution(self, incident_id: int, text: str) -> None:
        """What a person does from the console's Resolve form or "Edit notes"."""
        with self._lock:
            self._rows[incident_id]["resolution"] = text


class PostgresIncidentStore(IncidentStore):
    """
    The ``incidents`` table, through psycopg2.

    One connection, reopened after any failure; every call is its own short
    transaction. A write that fails is appended to the fallback file and the
    error is raised to the engine, which keeps running. Every text parameter
    goes through :func:`_text` first.
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
            conn = None
            try:
                conn = self._connection()
                with conn.cursor() as cur:
                    result = work(cur)
                conn.commit()
                return result
            except Exception:
                self._conn = None
                if conn is not None:
                    for end in (conn.rollback, conn.close):   # closed, not just dropped: no leaked sessions
                        try:
                            end()
                        except Exception:
                            pass
                raise

    def upsert(self, finding: Finding, now_utc: datetime) -> Upserted:
        fingerprint = _text(finding.fingerprint)

        def work(cur) -> Upserted:
            cur.execute(
                'SELECT "Id", "Severity", "Occurrences", "EvidenceJson", "FirstSeenUtc" FROM incidents '
                'WHERE "Fingerprint" = %s AND "Status" IN %s ORDER BY "Id" DESC LIMIT 1 FOR UPDATE',
                (fingerprint, _LIVE),
            )
            row = cur.fetchone()
            if row is not None:
                incident_id, before_raw, occurrences, evidence_before, first_seen = row
                before = Severity(before_raw)
                after = max(before, finding.severity, key=lambda s: s.rank)
                kept = _json(_keep_context(list(finding.evidence), _evidence_list(evidence_before)))
                cur.execute(
                    'UPDATE incidents SET "Severity" = %s, "Title" = %s, "Summary" = %s, "EvidenceJson" = %s, '
                    '"LastSeenUtc" = %s, "Occurrences" = "Occurrences" + 1 WHERE "Id" = %s',
                    (after.value, _text(finding.title)[:300], _text(finding.summary), kept, now_utc, incident_id),
                )
                if isinstance(first_seen, datetime) and first_seen.tzinfo is None:
                    first_seen = first_seen.replace(tzinfo=timezone.utc)
                return Upserted(incident_id, False, after.rank > before.rank, after, occurrences + 1,
                                first_seen if isinstance(first_seen, datetime) else None)

            cur.execute(
                'INSERT INTO incidents ("Fingerprint", "Agent", "Rule", "Severity", "Status", "Title", "Summary", '
                '"Location", "EvidenceJson", "Suggestion", "Occurrences", "FirstSeenUtc", "LastSeenUtc") '
                'VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, 1, %s, %s) RETURNING "Id"',
                (fingerprint, _text(finding.agent), _text(finding.rule), finding.severity.value, Status.OPEN.value,
                 _text(finding.title)[:300], _text(finding.summary), _text(finding.where)[:300],
                 _json(list(finding.evidence)), _text(finding.suggestion), now_utc, now_utc),
            )
            return Upserted(cur.fetchone()[0], True, False, finding.severity, 1, now_utc)

        try:
            try:
                return self._run(work)
            except Exception as exc:
                if not _unique_violation(exc):
                    raise
                # Another writer inserted this fingerprint's live row between
                # the SELECT and the INSERT (the partial unique index refused
                # the second). The row exists now: once more, and the SELECT
                # finds it and takes the update path.
                return self._run(work)
        except Exception:
            self._append_fallback(finding, now_utc)
            raise

    def live_for_agent(self, agent: str) -> list[LiveIncident]:
        def work(cur) -> list[LiveIncident]:
            cur.execute(
                'SELECT "Id", "Fingerprint", "Agent", "Severity", "Title" FROM incidents '
                'WHERE "Agent" = %s AND "Status" IN %s',
                (_text(agent), _LIVE),
            )
            return [LiveIncident(r[0], r[1], r[2], Severity(r[3]), r[4]) for r in cur.fetchall()]

        return self._run(work)

    def resolve(self, incident_id: int, now_utc: datetime) -> bool:
        def work(cur) -> bool:
            cur.execute(
                'UPDATE incidents SET "Status" = %s, "ResolvedUtc" = %s WHERE "Id" = %s AND "Status" IN %s',
                (Status.RESOLVED.value, now_utc, incident_id, _LIVE))
            return cur.rowcount == 1

        return self._run(work)

    def mark_notified(self, incident_id: int, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE incidents SET "NotifiedUtc" = %s WHERE "Id" = %s', (now_utc, incident_id)))

    def unnotified_live(self, limit: int) -> list[UnsentIncident]:
        def work(cur) -> list[UnsentIncident]:
            cur.execute(
                'SELECT "Id", "Fingerprint", "Agent", "Rule", "Severity", "Title", "Summary", "Location", '
                '"EvidenceJson", "Suggestion", "FirstSeenUtc" FROM incidents '
                'WHERE "Status" IN %s AND "NotifiedUtc" IS NULL AND "Severity" <> %s ORDER BY "Id" LIMIT %s',
                (_LIVE, Severity.LOW.value, limit),
            )
            found = []
            for r in cur.fetchall():
                first_seen = r[10]
                if isinstance(first_seen, datetime) and first_seen.tzinfo is None:
                    first_seen = first_seen.replace(tzinfo=timezone.utc)
                found.append(UnsentIncident(r[0], r[1], r[2], r[3], Severity(r[4]), r[5], r[6] or "", r[7] or "",
                                            _evidence_list(r[8]), r[9] or "",
                                            first_seen if isinstance(first_seen, datetime) else None))
            return found

        return self._run(work)

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
                        (_json(_with_context(_evidence_list(row[0]), context)), incident_id))

        self._run(work)

    def earlier_episodes(self, fingerprint: str, incident_id: int) -> Optional[EarlierEpisodes]:
        # One query: the window count is taken before the LIMIT, so it counts
        # every earlier episode while the row returned is the latest of them.
        def work(cur) -> Optional[EarlierEpisodes]:
            cur.execute(
                'SELECT COUNT(*) OVER (), "LastSeenUtc", "Resolution" FROM incidents '
                'WHERE "Fingerprint" = %s AND "Status" = %s AND "Id" <> %s '
                'ORDER BY "FirstSeenUtc" DESC, "Id" DESC LIMIT 1',
                (_text(fingerprint), Status.RESOLVED.value, incident_id),
            )
            row = cur.fetchone()
            if row is None:
                return None
            count, last_seen, resolution = row
            if isinstance(last_seen, datetime) and last_seen.tzinfo is None:
                last_seen = last_seen.replace(tzinfo=timezone.utc)
            return EarlierEpisodes(int(count), last_seen if isinstance(last_seen, datetime) else None,
                                   resolution or "")

        return self._run(work)

    def _append_fallback(self, finding: Finding, now_utc: datetime) -> None:
        """
        Keep the finding on disk. Every failed sighting is appended, so the file
        is capped: past FALLBACK_MAX_BYTES it becomes ``<name>.1`` (replacing
        the one before) and a new file starts — at most twice the cap on disk.
        """
        try:
            self._fallback.parent.mkdir(parents=True, exist_ok=True)
            try:
                if self._fallback.stat().st_size >= FALLBACK_MAX_BYTES:
                    os.replace(self._fallback, self._fallback.with_name(self._fallback.name + ".1"))
            except FileNotFoundError:
                pass
            line = _json({
                "at": now_utc.isoformat(), "fingerprint": finding.fingerprint, "agent": finding.agent,
                "rule": finding.rule, "severity": finding.severity.value, "title": finding.title,
                "summary": finding.summary, "where": finding.where, "evidence": list(finding.evidence),
                "suggestion": finding.suggestion,
            })
            with self._fallback.open("a", encoding="utf-8") as fh:
                fh.write(line + "\n")
        except Exception as exc:   # a fallback must never hide the error it is keeping a record of
            log.debug("could not append to %s: %s", self._fallback.name, exc)


#: The fallback file's cap; one older generation is kept beside it.
FALLBACK_MAX_BYTES = 5 * 1024 * 1024


def _conninfo_value(value: str) -> str:
    """A libpq connection-string value: quoted, with backslash and quote escaped (libpq reads both as escapes)."""
    return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"


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
    return " ".join(f"{k}={_conninfo_value(v)}" for k, v in parts.items())


#: The advisory lock one watching Sentinel holds: the word SENTINEL in ASCII.
WATCH_LOCK_KEY = 0x53454E54494E454C


def _psycopg2_connect(dsn: str):
    import psycopg2

    return psycopg2.connect(dsn, connect_timeout=5)


class WatchLock:
    """
    One watching Sentinel per database.

    Two watchers — the service and a ``python3 -m sentinel`` someone started
    by hand to try something — would each send every message, and race each
    other's inserts. The service takes a session-level advisory lock on a
    connection of its own and keeps that connection open; a second watcher
    finds the lock taken and exits. Only watching takes it: ``--dry-run``
    stores and sends nothing, and ``--once`` is one deliberate round.

    A database that cannot be reached is not a reason to stop watching — that
    is when watching matters most — so only "another session holds it" says
    stop. A lock lost with its connection (the database restarted) is taken
    again on the next look, unless someone else took it first.
    """

    def __init__(self, dsn: str, connect: Optional[Callable[[str], Any]] = None) -> None:
        self._dsn = dsn
        self._connect = connect or _psycopg2_connect
        self._conn = None

    def held_elsewhere(self) -> bool:
        """Take the lock, or confirm it is still this process's; True only when another session holds it."""
        if self._conn is not None:
            try:
                with self._conn.cursor() as cur:
                    cur.execute("SELECT 1")   # the session is alive, so the lock it holds is too
                return False
            except Exception:
                self.release()
        try:
            conn = self._connect(self._dsn)
            conn.autocommit = True
            with conn.cursor() as cur:
                cur.execute("SELECT pg_try_advisory_lock(%s)", (WATCH_LOCK_KEY,))
                taken = bool(cur.fetchone()[0])
        except Exception as exc:
            log.warning("could not ask the database whether another Sentinel is watching: %s", exc)
            return False
        if taken:
            self._conn = conn
            return False
        try:
            conn.close()
        except Exception:
            pass
        return True

    def release(self) -> None:
        conn, self._conn = self._conn, None
        if conn is not None:
            try:
                conn.close()   # the session ends, and its advisory lock with it
            except Exception:
                pass
