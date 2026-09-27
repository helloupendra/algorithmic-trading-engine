"""
The ``desk_checkups`` table: one row per checkup.

The table belongs to the API (entity DeskCheckup, its migration) and is shared
with it like ``incidents``: the console asks for a checkup by inserting a
"requested" row, Sentinel claims it, runs it and fills it in; a scheduled
checkup is inserted by Sentinel already running. DeskCheckupsTableContractTests
checks that every column named here exists — this file names no other table.

Every text written is redacted first (sentinel.notify.redact): an item's
detail can quote a vendor's message.
"""
from __future__ import annotations

import json
import logging
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime
from typing import Optional

from sentinel.checkup.model import Report
from sentinel.notify import redact
from sentinel.store import _text

log = logging.getLogger("sentinel.checkup.store")

#: Status values, as the API and the console read them.
REQUESTED, RUNNING, DONE, FAILED = "requested", "running", "done", "failed"


def items_json(report: Report) -> str:
    """The report's items as the console reads them, every text redacted."""
    rows = []
    for item in report.items:
        row = item.to_json()
        for key in ("title", "detail", "action"):
            row[key] = redact(row[key])
        rows.append(row)
    return json.dumps(rows, ensure_ascii=False)


class CheckupStore(ABC):
    """Where checkups are kept. Each call is on its own; a failure raises."""

    @abstractmethod
    def claim_request(self, now_utc: datetime, host: str) -> Optional[int]:
        """The oldest checkup a person asked for, now marked running; None when none waits."""

    @abstractmethod
    def start(self, slot: str, now_utc: datetime, host: str) -> Optional[int]:
        """A scheduled checkup, inserted running. None when there is no table to keep it in."""

    @abstractmethod
    def finish(self, checkup_id: int, report: Report, headline: str) -> None: ...

    @abstractmethod
    def fail(self, checkup_id: int, error: str, now_utc: datetime) -> None: ...

    @abstractmethod
    def mark_notified(self, checkup_id: int, now_utc: datetime) -> None: ...


@dataclass
class MemoryCheckupStore(CheckupStore):
    """A dry run's store, and the tests'. ``rows`` is what the table would hold."""

    rows: list[dict] = field(default_factory=list)

    def request(self, now_utc: datetime, by: str = "someone") -> int:
        """What the console does: ask for a checkup."""
        self.rows.append({"id": len(self.rows) + 1, "slot": "on-request", "status": REQUESTED,
                          "requested_utc": now_utc, "requested_by": by})
        return len(self.rows)

    def claim_request(self, now_utc: datetime, host: str) -> Optional[int]:
        for row in self.rows:
            if row["status"] == REQUESTED:
                row.update(status=RUNNING, started_utc=now_utc, host=host)
                return row["id"]
        return None

    def start(self, slot: str, now_utc: datetime, host: str) -> Optional[int]:
        self.rows.append({"id": len(self.rows) + 1, "slot": slot, "status": RUNNING,
                          "started_utc": now_utc, "host": host})
        return len(self.rows)

    def _row(self, checkup_id: int) -> dict:
        return next(r for r in self.rows if r["id"] == checkup_id)

    def finish(self, checkup_id: int, report: Report, headline: str) -> None:
        self._row(checkup_id).update(status=DONE, completed_utc=report.completed_utc, verdict=report.verdict.value,
                                     headline=redact(headline), items=json.loads(items_json(report)))

    def fail(self, checkup_id: int, error: str, now_utc: datetime) -> None:
        self._row(checkup_id).update(status=FAILED, completed_utc=now_utc, error=redact(error))

    def mark_notified(self, checkup_id: int, now_utc: datetime) -> None:
        self._row(checkup_id)["notified_utc"] = now_utc


class PostgresCheckupStore(CheckupStore):
    """
    Through psycopg2, one connection reopened after any failure, each call its
    own transaction — as PostgresIncidentStore. Until the API that brings the
    table has been deployed, the table is missing: scheduled checkups are then
    reported without being kept (``start`` returns None), said once in the log.
    """

    def __init__(self, dsn: str) -> None:
        self._dsn = dsn
        self._conn = None
        self._lock = threading.Lock()
        self._missing_said = False

    def _run(self, work):
        import psycopg2  # imported here so tests without a database never need it

        with self._lock:
            conn = None
            try:
                if self._conn is None or self._conn.closed:
                    self._conn = psycopg2.connect(self._dsn, connect_timeout=5)
                conn = self._conn
                with conn.cursor() as cur:
                    result = work(cur)
                conn.commit()
                return result
            except Exception as exc:
                self._conn = None
                if conn is not None:
                    for end in (conn.rollback, conn.close):
                        try:
                            end()
                        except Exception:
                            pass
                if getattr(exc, "pgcode", None) == "42P01":   # undefined_table: the API is older than this
                    if not self._missing_said:
                        log.warning("desk_checkups does not exist yet (the API with it is not deployed): "
                                    "checkups are sent but not kept")
                        self._missing_said = True
                    return None
                raise

    def claim_request(self, now_utc: datetime, host: str) -> Optional[int]:
        # SKIP LOCKED: two claims can never take the same request, whatever
        # else is reading the table.
        def work(cur) -> Optional[int]:
            cur.execute(
                'UPDATE desk_checkups SET "Status" = %s, "StartedUtc" = %s, "Host" = %s '
                'WHERE "Id" = (SELECT "Id" FROM desk_checkups WHERE "Status" = %s '
                'ORDER BY "Id" LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING "Id"',
                (RUNNING, now_utc, _text(host), REQUESTED))
            row = cur.fetchone()
            return int(row[0]) if row else None

        return self._run(work)

    def start(self, slot: str, now_utc: datetime, host: str) -> Optional[int]:
        def work(cur) -> int:
            cur.execute(
                'INSERT INTO desk_checkups ("Slot", "Status", "StartedUtc", "Host", "RequestedBy", "Verdict", '
                '"Headline", "ItemsJson", "Error") VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s) RETURNING "Id"',
                (slot, RUNNING, now_utc, _text(host), "", "", "", "[]", ""))
            return int(cur.fetchone()[0])

        return self._run(work)

    def finish(self, checkup_id: int, report: Report, headline: str) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE desk_checkups SET "Status" = %s, "CompletedUtc" = %s, "Verdict" = %s, "Headline" = %s, '
            '"ItemsJson" = %s WHERE "Id" = %s',
            (DONE, report.completed_utc, report.verdict.value, _text(redact(headline)),
             _text(items_json(report)), checkup_id)))

    def fail(self, checkup_id: int, error: str, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE desk_checkups SET "Status" = %s, "CompletedUtc" = %s, "Error" = %s WHERE "Id" = %s',
            (FAILED, now_utc, _text(redact(error))[:2000], checkup_id)))

    def mark_notified(self, checkup_id: int, now_utc: datetime) -> None:
        self._run(lambda cur: cur.execute(
            'UPDATE desk_checkups SET "NotifiedUtc" = %s WHERE "Id" = %s', (now_utc, checkup_id)))
