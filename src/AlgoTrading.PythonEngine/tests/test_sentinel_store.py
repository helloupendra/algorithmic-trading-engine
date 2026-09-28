import _bootstrap  # noqa: F401

import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

from sentinel.model import FLAP_WINDOW, Finding, Severity
from sentinel.store import (FALLBACK_MAX_BYTES, WATCH_LOCK_KEY, EarlierEpisodes, MemoryIncidentStore,
                            PostgresIncidentStore, WatchLock, dsn_from_env)

try:
    import psycopg2
    from psycopg2.extensions import adapt, parse_dsn
except ImportError:  # the tests that need its quoting are skipped where it is not installed
    psycopg2 = None

NOW = datetime(2026, 9, 28, 5, 0, tzinfo=timezone.utc)


def finding(**kw):
    base = dict(agent="logs", rule="new-error", severity=Severity.MEDIUM, title="New error line from the API",
                summary="An error line never seen before.", fingerprint="logs:new-error:abc",
                where="logs/api.log", evidence=["fail: something broke"], suggestion="Read the line.")
    base.update(kw)
    return Finding(**base)


class UniqueViolation(Exception):
    pgcode = "23505"


class Cursor:
    """
    Enough of a psycopg2 cursor to see what the store sends. ``answers`` is a
    list of (sql prefix, what to do): rows to fetch, or an exception to raise.
    With ``quote`` every parameter is adapted as psycopg2 adapts it before a
    query leaves the process — where a NUL or a lone surrogate fails.
    """

    def __init__(self, rows=(), rowcount=1, raise_on=None, quote=False):
        self.rows = list(rows)
        self.rowcount = rowcount
        self.raise_on = dict(raise_on or {})
        self.quote = quote
        self.executed = []

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def execute(self, sql, params=None):
        for prefix, exc in list(self.raise_on.items()):
            if sql.startswith(prefix):
                del self.raise_on[prefix]
                raise exc
        if self.quote:
            for value in params or ():
                if isinstance(value, str):
                    a = adapt(value)
                    a.encoding = "UTF8"
                    a.getquoted()
        self.executed.append((sql, params))

    def fetchone(self):
        return self.rows.pop(0) if self.rows else None

    def fetchall(self):
        rows, self.rows = self.rows, []
        return rows


class Connection:
    def __init__(self, cursor):
        self._cursor = cursor
        self.closed = False
        self.rolled_back = 0

    def cursor(self):
        return self._cursor

    def commit(self):
        pass

    def rollback(self):
        self.rolled_back += 1

    def close(self):
        self.closed = True


def store_with(cursor, fallback=None):
    fallback = fallback or Path(tempfile.mkdtemp()) / "unstored.jsonl"
    store = PostgresIncidentStore("dbname=x", fallback)
    connections = []

    def connect():
        conn = Connection(cursor)
        connections.append(conn)
        return conn

    store._connection = connect
    return store, connections


@unittest.skipIf(psycopg2 is None, "psycopg2 is not installed")
class TextParameterTests(unittest.TestCase):
    """Log text reaches the incidents table; what psycopg2 cannot send must never make a finding unstorable."""

    def test_a_nul_in_any_text_is_stored_not_refused_for_good(self):
        cur = Cursor(quote=True)
        cur.rows = [None, None, (7,)]   # no live row, no episode before, then the INSERT's id
        store, _ = store_with(cur)
        result = store.upsert(finding(title="bad \x00 frame", summary="x\x00y", where="api\x00.log",
                                      suggestion="\x00", evidence=["line \x00 with nul"],
                                      fingerprint="logs:new-error:\x00"), NOW)
        self.assertEqual(7, result.incident_id)
        insert = cur.executed[-1][1]
        self.assertFalse(any("\x00" in p for p in insert if isinstance(p, str)), insert)
        self.assertEqual("bad \ufffd frame", insert[5])

    def test_a_lone_surrogate_is_replaced_not_an_encoding_error(self):
        cur = Cursor(quote=True)
        cur.rows = [("12", "medium", 1, "[]", NOW.replace(tzinfo=None))]
        store, _ = store_with(cur)
        store.upsert(finding(title="caf\udce9", summary="\ud800", evidence=["\udcff"]), NOW)
        update = cur.executed[-1][1]
        self.assertEqual("caf?", update[1])
        self.assertEqual(["?"], json.loads(update[3]))

    def test_attached_context_and_the_agent_name_are_cleaned_too(self):
        cur = Cursor(quote=True)
        cur.rows = [(json.dumps(["x"]),)]
        store, _ = store_with(cur)
        store.attach_context(3, ["context: api.log: \x00\udcff"])
        store.live_for_agent("lo\x00gs")
        self.assertNotIn("\x00", cur.executed[1][1][0])


class ResolveTests(unittest.TestCase):
    def test_resolve_says_whether_it_changed_anything(self):
        store, _ = store_with(Cursor(rowcount=1))
        self.assertTrue(store.resolve(4, NOW))
        store, _ = store_with(Cursor(rowcount=0))   # resolved from the console a moment before
        self.assertFalse(store.resolve(4, NOW))

    def test_the_memory_store_agrees(self):
        store = MemoryIncidentStore()
        incident_id = store.upsert(finding(), NOW).incident_id
        self.assertTrue(store.resolve(incident_id, NOW))
        self.assertFalse(store.resolve(incident_id, NOW))
        self.assertFalse(store.resolve(999, NOW))


class RaceTests(unittest.TestCase):
    def test_a_second_writer_inserting_first_is_retried_as_an_update(self):
        # Both writers saw no live row; the other one's INSERT landed first and
        # the partial unique index refused this one. Once more: the SELECT now
        # finds the row, and this sighting counts on it.
        cur = Cursor(raise_on={"INSERT INTO incidents": UniqueViolation("duplicate key")})
        cur.rows = [None, None, (41, "medium", 1, "[]", NOW.replace(tzinfo=None))]
        store, connections = store_with(cur)
        result = store.upsert(finding(), NOW)
        self.assertEqual((41, False), (result.incident_id, result.is_new))
        self.assertTrue(cur.executed[-1][0].startswith("UPDATE incidents SET"))
        self.assertTrue(connections[0].closed, "the failed connection is closed, not leaked")

    def test_any_other_failure_is_kept_on_disk_and_raised(self):
        cur = Cursor(raise_on={"SELECT": ConnectionError("database is down")})
        store, _ = store_with(cur)
        with self.assertRaises(ConnectionError):
            store.upsert(finding(), NOW)
        self.assertEqual(1, len(store._fallback.read_text().splitlines()))


class FallbackFileTests(unittest.TestCase):
    def test_the_file_is_capped_with_one_older_generation(self):
        # A database down all day, a sighting every 30 s: the file must stop growing somewhere.
        self.assertLessEqual(FALLBACK_MAX_BYTES, 10 * 1024 * 1024)
        fallback = Path(tempfile.mkdtemp()) / "unstored.jsonl"
        fallback.write_text("x" * 100, encoding="utf-8")
        store, _ = store_with(Cursor(raise_on={"SELECT": ConnectionError("down")}), fallback)
        with mock.patch("sentinel.store.FALLBACK_MAX_BYTES", 100), self.assertRaises(ConnectionError):
            store.upsert(finding(), NOW)
        self.assertEqual(100, (fallback.parent / "unstored.jsonl.1").stat().st_size)
        self.assertEqual("logs:new-error:abc", json.loads(fallback.read_text().splitlines()[0])["fingerprint"])

    def test_text_the_file_cannot_encode_does_not_hide_the_database_error(self):
        store, _ = store_with(Cursor(raise_on={"SELECT": ConnectionError("down")}))
        with self.assertRaises(ConnectionError):
            store.upsert(finding(summary="\ud800"), NOW)


class UnsentTests(unittest.TestCase):
    def test_live_incidents_above_low_with_no_message_sent(self):
        store = MemoryIncidentStore()
        sent = store.upsert(finding(fingerprint="a"), NOW).incident_id
        store.mark_notified(sent, NOW)
        store.upsert(finding(fingerprint="low", severity=Severity.LOW), NOW)
        unsent = store.upsert(finding(fingerprint="b", evidence=["e", "context: checked out: 3f2a1b9"]),
                              NOW).incident_id
        gone = store.upsert(finding(fingerprint="c"), NOW).incident_id
        store.resolve(gone, NOW)
        rows = store.unnotified_live(20)
        self.assertEqual([unsent], [r.incident_id for r in rows])
        self.assertEqual(["e", "context: checked out: 3f2a1b9"], rows[0].evidence)

    def test_the_query_names_the_table_s_own_columns(self):
        cur = Cursor()
        cur.rows = [(5, "health:api-down", "health", "api-down", "high", "API down", None, None, "not json",
                     None, NOW.replace(tzinfo=None))]
        store, _ = store_with(cur)
        [row] = store.unnotified_live(20)
        sql, params = cur.executed[0]
        self.assertIn('"NotifiedUtc" IS NULL', sql)
        self.assertIn('"Severity" <> %s', sql)
        self.assertEqual(("low", 20), params[1:])
        self.assertEqual((5, Severity.HIGH, "", [], NOW), (row.incident_id, row.severity, row.summary,
                                                          row.evidence, row.first_seen_utc))


class EarlierEpisodesTests(unittest.TestCase):
    """What the store says about a fingerprint's resolved episodes, for the "seen before" lines."""

    def test_one_query_counts_every_earlier_episode_and_returns_the_latest(self):
        cur = Cursor(rows=[(3, NOW.replace(tzinfo=None), "Restarted the feed")])   # the column comes back naive: UTC
        store, _ = store_with(cur)
        earlier = store.earlier_episodes("health:feed-silent:NSE", 12)
        sql, params = cur.executed[0]
        # The window count is taken before the LIMIT: all of them, and the latest one's row.
        self.assertIn('SELECT COUNT(*) OVER (), "LastSeenUtc", "Resolution" FROM incidents', sql)
        self.assertIn('"Fingerprint" = %s AND "Status" = %s AND "Id" <> %s', sql)
        self.assertIn('ORDER BY "FirstSeenUtc" DESC, "Id" DESC LIMIT 1', sql)
        self.assertEqual(("health:feed-silent:NSE", "resolved", 12), params)
        self.assertEqual(EarlierEpisodes(3, NOW, "Restarted the feed"), earlier)

    def test_none_before_is_none_and_no_resolution_is_empty(self):
        store, _ = store_with(Cursor(rows=[]))
        self.assertIsNone(store.earlier_episodes("x", 1))
        store, _ = store_with(Cursor(rows=[(1, NOW, None)]))
        self.assertEqual(EarlierEpisodes(1, NOW, ""), store.earlier_episodes("x", 2))

    def test_a_failed_read_is_raised_for_the_engine_and_kept_out_of_the_fallback_file(self):
        # It is not a finding: nothing to keep on disk, and the engine decides what it costs.
        store, _ = store_with(Cursor(raise_on={"SELECT": ConnectionError("down")}))
        with self.assertRaises(ConnectionError):
            store.earlier_episodes("x", 1)
        self.assertFalse(store._fallback.exists())

    def test_the_memory_store_agrees(self):
        store = MemoryIncidentStore()
        self.assertIsNone(store.earlier_episodes("logs:new-error:abc", 1))
        first = store.upsert(finding(), NOW).incident_id
        store.resolve(first, NOW)
        store.write_resolution(first, "Fixed the log format")
        second = store.upsert(finding(), NOW + FLAP_WINDOW + timedelta(seconds=1)).incident_id
        self.assertEqual(EarlierEpisodes(1, NOW, "Fixed the log format"), store.earlier_episodes("logs:new-error:abc", second))
        self.assertIsNone(store.earlier_episodes("logs:new-error:other", second))


class KeptLinesTests(unittest.TestCase):
    def test_seen_before_lines_survive_later_sightings_and_a_newer_pack(self):
        store = MemoryIncidentStore()
        incident_id = store.upsert(finding(), NOW).incident_id
        store.attach_context(incident_id, ["history: Seen before: once", "context: deployed 3 min before"])
        store.attach_context(incident_id, ["context: then desk.log API up"])   # the lines after, two minutes on
        store.upsert(finding(evidence=["a newer sighting"]), NOW)
        self.assertEqual(["a newer sighting", "history: Seen before: once", "context: then desk.log API up"],
                         store.rows()[0]["evidence"])

    def test_the_flapping_line_is_kept_like_the_others(self):
        store = MemoryIncidentStore()
        incident_id = store.upsert(finding(), NOW).incident_id
        store.attach_context(incident_id, ["history: Seen before: once", "flapping: 2nd episode since 13:06 IST"])
        store.attach_context(incident_id, ["flapping: 3rd episode since 13:06 IST"])   # rewritten, the rest kept
        store.upsert(finding(evidence=["a newer sighting"]), NOW)
        self.assertEqual(["a newer sighting", "history: Seen before: once", "flapping: 3rd episode since 13:06 IST"],
                         store.rows()[0]["evidence"])


def minutes(n):
    return NOW + timedelta(minutes=n)


class FlapReopenTests(unittest.TestCase):
    """28 Sep 13:06-13:28: a feed stalling every few minutes is one incident, reopened, not one per stall."""

    def setUp(self):
        self.store = MemoryIncidentStore()
        self.first = self.store.upsert(finding(), NOW).incident_id

    def test_a_problem_back_within_the_window_of_sentinel_resolving_it_reopens_that_incident(self):
        self.store.resolve(self.first, minutes(2))
        back = self.store.upsert(finding(severity=Severity.HIGH, evidence=["stalled again"]), minutes(5))
        self.assertEqual((self.first, False, True, False), (back.incident_id, back.is_new, back.reopened, back.stale))
        self.assertTrue(back.escalated, "medium before, high now")
        self.assertEqual((2, NOW), (back.occurrences, back.first_seen_utc))
        [row] = self.store.rows()
        self.assertEqual(("open", None, 2, ["stalled again"]),
                         (row["status"], row["resolved"], row["occurrences"], row["evidence"]))

    def test_the_window_is_thirty_minutes_after_the_resolve(self):
        self.assertEqual(timedelta(minutes=30), FLAP_WINDOW)
        self.store.resolve(self.first, minutes(2))
        self.assertTrue(self.store.upsert(finding(), minutes(32)).reopened)
        self.store.resolve(self.first, minutes(33))
        later = self.store.upsert(finding(), minutes(64))
        self.assertTrue(later.is_new)
        self.assertEqual([("resolved", 1), ("open", 2)], [(r["status"], r["id"]) for r in self.store.rows()])

    def test_one_a_person_resolved_is_not_reopened_its_return_is_a_new_episode(self):
        self.store.resolve_by_person(self.first, minutes(2))
        back = self.store.upsert(finding(), minutes(5))
        self.assertTrue(back.is_new)
        self.assertNotEqual(self.first, back.incident_id)

    def test_a_notice_is_its_own_event_every_time(self):
        store = MemoryIncidentStore()
        first = store.upsert(finding(extra={"notice": True}), NOW).incident_id
        store.resolve(first, minutes(1))
        self.assertTrue(store.upsert(finding(extra={"notice": True}), minutes(2)).is_new)

    def test_one_acknowledged_before_it_cleared_comes_back_acknowledged(self):
        self.store.acknowledge(self.first, minutes(1))
        self.store.resolve(self.first, minutes(2))
        self.store.upsert(finding(), minutes(5))
        self.assertEqual("acknowledged", self.store.rows()[0]["status"])


class StaleFindingTests(unittest.TestCase):
    """28 Sep 13:11: findings held from 13:05-13:06 lines reopened what a person resolved at 13:08."""

    def setUp(self):
        self.store = MemoryIncidentStore()
        self.first = self.store.upsert(finding(), minutes(0)).incident_id
        self.store.resolve_by_person(self.first, minutes(3))

    def test_what_was_observed_before_the_resolve_writes_nothing(self):
        held = self.store.upsert(finding(observed_utc=minutes(1)), minutes(6))
        self.assertEqual((self.first, True, False, False), (held.incident_id, held.stale, held.is_new, held.reopened))
        self.assertEqual([("resolved", 1)], [(r["status"], r["occurrences"]) for r in self.store.rows()])

    def test_at_the_resolve_is_before_it_too(self):
        self.assertTrue(self.store.upsert(finding(observed_utc=minutes(3)), minutes(6)).stale)

    def test_what_was_observed_after_it_is_news(self):
        again = self.store.upsert(finding(observed_utc=minutes(4)), minutes(6))
        self.assertTrue(again.is_new)
        self.assertEqual(["resolved", "open"], [r["status"] for r in self.store.rows()])

    def test_a_finding_observed_now_is_news_as_before(self):
        self.assertTrue(self.store.upsert(finding(), minutes(6)).is_new)


class PostgresAfterResolveTests(unittest.TestCase):
    """The same three outcomes in SQL: the latest episode is read, and only a reopen or an insert writes."""

    def latest(self, resolved_by=None, resolved=minutes(2), acknowledged=None):
        # "Id", "Severity", "Occurrences", "EvidenceJson", "FirstSeenUtc", "ResolvedUtc", "ResolvedBy", "AcknowledgedUtc"
        return (19, "critical", 6, json.dumps(["old", "history: Seen before: once"]), NOW.replace(tzinfo=None),
                resolved.replace(tzinfo=None), resolved_by, acknowledged)

    def test_a_flap_reopens_the_row_it_came_from(self):
        cur = Cursor(rows=[None, self.latest()])
        store, _ = store_with(cur)
        result = store.upsert(finding(severity=Severity.CRITICAL, evidence=["new"]), minutes(5))
        self.assertEqual((19, True, False, 7, NOW), (result.incident_id, result.reopened, result.is_new,
                                                     result.occurrences, result.first_seen_utc))
        sql, params = cur.executed[1]
        self.assertIn('WHERE "Fingerprint" = %s ORDER BY "Id" DESC LIMIT 1 FOR UPDATE', sql)
        self.assertIn('"ResolvedUtc", "ResolvedBy", "AcknowledgedUtc"', sql)
        sql, params = cur.executed[2]
        self.assertTrue(sql.startswith('UPDATE incidents SET "Status" = %s, "ResolvedUtc" = NULL'), sql)
        self.assertEqual(("open", "critical"), params[:2])
        self.assertEqual(["new", "history: Seen before: once"], json.loads(params[4]))
        self.assertEqual(19, params[-1])
        self.assertEqual(3, len(cur.executed))

    def test_acknowledged_before_it_cleared_it_comes_back_acknowledged(self):
        cur = Cursor(rows=[None, self.latest(acknowledged=minutes(1).replace(tzinfo=None))])
        store, _ = store_with(cur)
        store.upsert(finding(), minutes(5))
        self.assertEqual("acknowledged", cur.executed[2][1][0])

    def test_old_news_writes_nothing(self):
        cur = Cursor(rows=[None, self.latest(resolved_by="upendra")])
        store, _ = store_with(cur)
        result = store.upsert(finding(observed_utc=minutes(1)), minutes(5))
        self.assertEqual((19, True), (result.incident_id, result.stale))
        self.assertEqual(2, len(cur.executed), "two reads, no write")

    def test_one_a_person_resolved_gets_a_new_row(self):
        cur = Cursor(rows=[None, self.latest(resolved_by="upendra"), (20,)])
        store, _ = store_with(cur)
        result = store.upsert(finding(), minutes(5))
        self.assertEqual((20, True), (result.incident_id, result.is_new))
        self.assertTrue(cur.executed[2][0].startswith("INSERT INTO incidents"))

    def test_a_reopen_that_races_another_writer_is_retried_as_an_update(self):
        cur = Cursor(raise_on={'UPDATE incidents SET "Status"': UniqueViolation("duplicate key")})
        cur.rows = [None, self.latest(), (21, "critical", 1, "[]", NOW.replace(tzinfo=None))]
        store, _ = store_with(cur)
        result = store.upsert(finding(), minutes(5))
        self.assertEqual((21, False, False), (result.incident_id, result.is_new, result.reopened))


class DsnTests(unittest.TestCase):
    @unittest.skipIf(psycopg2 is None, "psycopg2 is not installed")
    def test_a_password_with_a_backslash_or_a_quote_reaches_libpq_unchanged(self):
        for password in ("ab\\cd", "abcd\\", "it's", "a\\'b", "p@ss w0rd"):
            dsn = dsn_from_env({"POSTGRES_PASSWORD": password, "POSTGRES_PORT": "5433"})
            parsed = parse_dsn(dsn)
            self.assertEqual(password, parsed["password"])
            self.assertEqual("5433", parsed["port"])

    def test_no_password_no_database(self):
        self.assertIsNone(dsn_from_env({}))


class LockConnection:
    def __init__(self, granted=True, alive=True):
        self.granted = granted
        self.alive = alive
        self.closed = False
        self.autocommit = False
        self.sql = []

    def cursor(self):
        conn = self

        class _Cur:
            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

            def execute(self, sql, params=None):
                if not conn.alive:
                    raise ConnectionError("server closed the connection")
                conn.sql.append((sql, params))

            def fetchone(self):
                return (conn.granted,)

        return _Cur()

    def close(self):
        self.closed = True


class WatchLockTests(unittest.TestCase):
    def lock(self, *connections):
        made = list(connections)
        opened = []

        def connect(dsn):
            if not made:
                raise ConnectionError("could not connect to server")
            conn = made.pop(0)
            if isinstance(conn, Exception):
                raise conn
            opened.append(conn)
            return conn

        return WatchLock("dbname=x", connect=connect), opened

    def test_the_first_watcher_takes_it_and_keeps_its_connection(self):
        lock, opened = self.lock(LockConnection(granted=True))
        self.assertFalse(lock.held_elsewhere())
        self.assertEqual(("SELECT pg_try_advisory_lock(%s)", (WATCH_LOCK_KEY,)), opened[0].sql[0])
        self.assertTrue(opened[0].autocommit, "not left idle in a transaction")
        self.assertFalse(lock.held_elsewhere())   # still ours: the same session answers
        self.assertEqual(1, len(opened))
        self.assertFalse(opened[0].closed)

    def test_a_second_watcher_is_told_so(self):
        lock, opened = self.lock(LockConnection(granted=False))
        self.assertTrue(lock.held_elsewhere())
        self.assertTrue(opened[0].closed)

    def test_a_database_that_does_not_answer_is_no_reason_to_stop_watching(self):
        lock, _ = self.lock(ConnectionError("could not connect to server"))
        self.assertFalse(lock.held_elsewhere())

    def test_a_lock_lost_with_its_connection_is_taken_again_unless_another_has_it(self):
        first = LockConnection(granted=True)
        lock, opened = self.lock(first, LockConnection(granted=False))
        self.assertFalse(lock.held_elsewhere())
        first.alive = False                      # the database restarted
        self.assertTrue(lock.held_elsewhere())   # and the hand-run one took the lock in between
        self.assertTrue(first.closed)


if __name__ == "__main__":
    unittest.main()
