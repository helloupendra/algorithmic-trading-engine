"""
What the checkup reads straight from the database: the few questions the API
has no endpoint for — which runs were left Pending, which legs are open in a
run that has ended, what is held in the manual books, which incidents were
closed without notes.

Every query runs in a READ ONLY transaction, so a mistake here cannot write,
whatever the SQL says.
"""
from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Any, Optional

#: The manual order book's strategy name (ManualOrdersController.BookStrategyName).
MANUAL_BOOK = "Manual"

#: "Stale" as scripts/close-stale-runs.sh defines it; the action the checkup
#: suggests is that script, so both must mean the same runs.
STALE_PENDING = ('r."Mode" = \'LivePaper\' AND r."Status" = \'Pending\' AND r."StrategyName" <> \'Manual\' '
                 'AND r."CreatedUtc" < now() - make_interval(days => %s)')


def _utc(value: Any) -> Optional[datetime]:
    if not isinstance(value, datetime):
        return None
    return value if value.tzinfo else value.replace(tzinfo=timezone.utc)


def _num(value: Any) -> Optional[float]:
    return float(value) if value is not None else None


class DeskReads:
    """Read-only questions to the desk's database. Each returns plain dicts."""

    def __init__(self, dsn: str) -> None:
        self._dsn = dsn
        self._conn = None
        self._lock = threading.Lock()

    def _query(self, sql: str, params: tuple = ()) -> list[tuple]:
        import psycopg2

        with self._lock:
            try:
                if self._conn is None or self._conn.closed:
                    self._conn = psycopg2.connect(self._dsn, connect_timeout=5)
                    self._conn.set_session(readonly=True, autocommit=False)
                with self._conn.cursor() as cur:
                    cur.execute("SET LOCAL statement_timeout = 10000")
                    cur.execute(sql, params)
                    rows = cur.fetchall()
                self._conn.rollback()   # nothing to keep: end the read-only transaction
                return rows
            except Exception:
                if self._conn is not None:
                    try:
                        self._conn.close()
                    except Exception:
                        pass
                self._conn = None
                raise

    def stale_pending_runs(self, min_age_days: int = 1) -> list[dict]:
        rows = self._query(
            'SELECT r."Id", r."StrategyName", coalesce(u."UserName", r."UserId"::text), r."CreatedUtc", '
            'count(p."Id") FILTER (WHERE p."Status" = \'Open\') '
            'FROM simulation_runs r LEFT JOIN paper_positions p ON p."SimulationRunId" = r."Id" '
            'LEFT JOIN app_user u ON u."Id" = r."UserId" '
            f'WHERE {STALE_PENDING} GROUP BY r."Id", u."UserName" ORDER BY r."Id"',
            (min_age_days,))
        return [{"run": r[0], "strategy": r[1], "account": r[2], "created_utc": _utc(r[3]), "open_legs": int(r[4])}
                for r in rows]

    def orphaned_legs(self) -> list[dict]:
        """Open legs whose run has ended (Pending runs are the stale ones, above)."""
        rows = self._query(
            'SELECT r."Id", r."StrategyName", r."Status", coalesce(u."UserName", r."UserId"::text), '
            'p."Symbol", p."Direction", p."Quantity" '
            'FROM paper_positions p JOIN simulation_runs r ON r."Id" = p."SimulationRunId" '
            'LEFT JOIN app_user u ON u."Id" = r."UserId" '
            'WHERE p."Status" = \'Open\' AND r."Mode" = \'LivePaper\' '
            'AND r."Status" NOT IN (\'Running\', \'Stopping\', \'Pending\') '
            'ORDER BY r."Id", p."Id" LIMIT 100')
        return [{"run": r[0], "strategy": r[1], "run_status": r[2], "account": r[3], "symbol": r[4],
                 "direction": r[5], "quantity": int(r[6])} for r in rows]

    def manual_book_legs(self) -> list[dict]:
        """Every open leg in a manual book: what a person holds, carried or bought today."""
        rows = self._query(
            'SELECT r."Id", coalesce(u."UserName", r."UserId"::text), p."Symbol", p."Direction", p."Quantity", '
            'p."AveragePrice", p."LastMarkPrice", p."UnrealizedPnl", p."CarryForward", p."StopLossPrice", '
            'p."TargetPrice", p."OpenedUtc", p."CarriedFromPositionId" '
            'FROM paper_positions p JOIN simulation_runs r ON r."Id" = p."SimulationRunId" '
            'LEFT JOIN app_user u ON u."Id" = r."UserId" '
            'WHERE p."Status" = \'Open\' AND p."Quantity" > 0 AND r."StrategyName" = %s '
            'AND r."Status" IN (\'Running\', \'Stopping\') ORDER BY u."UserName", p."Id" LIMIT 200',
            (MANUAL_BOOK,))
        return [{"run": r[0], "account": r[1], "symbol": r[2], "direction": r[3], "quantity": int(r[4]),
                 "average": _num(r[5]), "mark": _num(r[6]), "unrealized": _num(r[7]) or 0.0, "carry": bool(r[8]),
                 "stop": _num(r[9]), "target": _num(r[10]), "opened_utc": _utc(r[11]),
                 "carried_from": r[12]} for r in rows]

    def strategy_legs_open(self, prefixes: tuple[str, ...]) -> list[dict]:
        """Open legs on these exchanges in strategy runs (not the manual books), running or not."""
        likes = " OR ".join('p."Symbol" LIKE %s' for _ in prefixes)
        rows = self._query(
            'SELECT r."Id", r."StrategyName", r."Status", coalesce(u."UserName", r."UserId"::text), '
            'p."Symbol", p."Direction", p."Quantity" '
            'FROM paper_positions p JOIN simulation_runs r ON r."Id" = p."SimulationRunId" '
            'LEFT JOIN app_user u ON u."Id" = r."UserId" '
            f'WHERE p."Status" = \'Open\' AND p."Quantity" > 0 AND r."Mode" = \'LivePaper\' '
            f'AND r."StrategyName" <> %s AND ({likes}) ORDER BY r."Id", p."Id" LIMIT 100',
            (MANUAL_BOOK, *[f"{p}%" for p in prefixes]))
        return [{"run": r[0], "strategy": r[1], "run_status": r[2], "account": r[3], "symbol": r[4],
                 "direction": r[5], "quantity": int(r[6])} for r in rows]

    def carried_since(self, since_utc: datetime) -> int:
        """Legs moved to a manual book by the close since this moment (the carry-forward tick)."""
        rows = self._query('SELECT count(*) FROM paper_positions WHERE "CarriedFromPositionId" IS NOT NULL '
                           'AND "OpenedUtc" >= %s', (since_utc,))
        return int(rows[0][0])

    def failover_events(self, since_utc: datetime) -> dict[str, int]:
        """
        What the feed failover did since then, from the messages it sent (the
        API records every one in alert_events, with the time it happened).
        """
        rows = self._query(
            'SELECT count(*) FILTER (WHERE "Title" LIKE \'Feed failover (dry run)%%\'), '
            'count(*) FILTER (WHERE "Title" LIKE \'Feed switched from Dhan to FYERS%%\') '
            'FROM alert_events WHERE "OccurredUtc" >= %s', (since_utc,))
        return {"would_switch": int(rows[0][0]), "switched": int(rows[0][1])}

    def live_incidents(self) -> list[dict]:
        rows = self._query(
            'SELECT "Id", "Severity", "Status", "Title", "FirstSeenUtc", "AcknowledgedUtc" FROM incidents '
            'WHERE "Status" IN (\'open\', \'acknowledged\') ORDER BY "FirstSeenUtc" LIMIT 100')
        return [{"id": r[0], "severity": r[1], "status": r[2], "title": r[3], "first_seen_utc": _utc(r[4]),
                 "acknowledged_utc": _utc(r[5])} for r in rows]

    def resolved_without_notes(self, since_utc: datetime) -> list[dict]:
        """
        Closed by a person since then with neither a cause nor what was done
        written down. Sentinel's own resolves leave ResolvedBy empty: the
        condition cleared, and there is nothing a person could have noted.
        """
        rows = self._query(
            'SELECT "Id", "Title", "ResolvedBy", "ResolvedUtc" FROM incidents '
            'WHERE "Status" = \'resolved\' AND "ResolvedUtc" >= %s AND coalesce("ResolvedBy", \'\') <> \'\' '
            'AND coalesce(btrim("RootCause"), \'\') = \'\' AND coalesce(btrim("Resolution"), \'\') = \'\' '
            'ORDER BY "ResolvedUtc" DESC LIMIT 50', (since_utc,))
        return [{"id": r[0], "title": r[1], "by": r[2], "resolved_utc": _utc(r[3])} for r in rows]
