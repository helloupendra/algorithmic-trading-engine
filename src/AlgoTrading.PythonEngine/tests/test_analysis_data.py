import _bootstrap  # noqa: F401

import os
import unittest
from datetime import date, datetime, timedelta, timezone

from analysis import data
from analysis.data import (CANDLES_5M, LIVE_1M, ExpiryCalendar, SessionBar, Underlying, aggregate_bars,
                           incomplete_reason, is_trading_day, merge_live, previous_trading_day, to_sessions)

UTC = timezone.utc


def ist(day: date, hh: int, mm: int) -> datetime:
    """A bar starting at hh:mm IST on `day`, as UTC."""
    return datetime(day.year, day.month, day.day, hh, mm, tzinfo=UTC) - timedelta(hours=5, minutes=30)


def full_day(day: date, minutes: int = 5, start_price: float = 100.0):
    """Every bar of a 09:15-15:30 session, prices rising by 1 a bar."""
    rows, t, p = [], ist(day, 9, 15), start_price
    while t < ist(day, 15, 30):
        rows.append((t, p, p + 2, p - 1, p + 1))
        t += timedelta(minutes=minutes)
        p += 1
    return rows


class SessionAggregationTests(unittest.TestCase):
    def test_a_full_session_folds_to_first_open_extremes_last_close(self):
        day = date(2026, 9, 21)                                    # a Monday
        aggs = aggregate_bars(full_day(day))
        self.assertEqual(len(aggs), 1)
        a = aggs[0]
        self.assertEqual((a.day, a.bars), (day, 75))
        self.assertEqual(a.open, 100.0)                            # the 09:15 bar's open
        self.assertEqual(a.close, 175.0)                           # the 15:25 bar's close
        self.assertEqual((a.high, a.low), (176.0, 99.0))

    def test_ist_boundaries_keep_0915_and_drop_the_auction_and_the_evening(self):
        day = date(2026, 9, 21)
        rows = full_day(day) + [
            (ist(day, 9, 10), 1.0, 1000.0, 0.5, 1.0),              # pre-open auction print: outside
            (ist(day, 15, 30), 1.0, 1000.0, 0.5, 1.0),             # 15:30 is the close, not a bar of the session
            (ist(day, 20, 0), 1.0, 1000.0, 0.5, 1.0),              # a vendor still quoting at 20:00
            (ist(day, 0, 0), 1.0, 1000.0, 0.5, 1.0),               # a BSE midnight date stamp
        ]
        a = aggregate_bars(rows)[0]
        self.assertEqual(a.bars, 75)
        self.assertEqual((a.high, a.low, a.open, a.close), (176.0, 99.0, 100.0, 175.0))

    def test_the_session_date_is_the_ist_date_not_the_utc_one(self):
        day = date(2026, 9, 22)
        # 09:15 IST is 03:45 UTC the same day; 15:25 IST is 09:55 UTC. Neither crosses midnight, but a
        # UTC-dated grouping of an IST 00:10 stamp would: that stamp must never land in the session.
        a = aggregate_bars(full_day(day))[0]
        self.assertEqual(a.first_utc, datetime(2026, 9, 22, 3, 45, tzinfo=UTC))
        self.assertEqual(a.last_utc, datetime(2026, 9, 22, 9, 55, tzinfo=UTC))
        self.assertEqual(a.day, day)

    def test_short_late_early_and_weekend_sessions_are_dropped_with_their_reason(self):
        monday = date(2026, 9, 21)
        complete = aggregate_bars(full_day(monday))[0]
        short = aggregate_bars(full_day(monday + timedelta(days=1))[:59])[0]                 # 59 of 75
        late = aggregate_bars(full_day(monday + timedelta(days=2))[3:])[0]                   # opens at 09:30
        early = aggregate_bars(full_day(monday + timedelta(days=3))[:-4])[0]                 # ends at 15:05
        saturday = aggregate_bars(full_day(monday + timedelta(days=5)))[0]                   # a Budget Saturday
        kept, dropped = to_sessions([complete, short, late, early, saturday], CANDLES_5M)
        self.assertEqual([s.day for s in kept], [monday])
        reasons = dict(dropped)
        self.assertIn("59 of 75 bars", reasons[monday + timedelta(days=1)])
        self.assertIn("first bar at 09:30", reasons[monday + timedelta(days=2)])
        self.assertIn("last bar at 15:05", reasons[monday + timedelta(days=3)])
        self.assertEqual(reasons[monday + timedelta(days=5)], "weekend")

    def test_sixty_bars_is_enough_and_a_muhurat_hour_is_not(self):
        monday = date(2026, 9, 21)
        rows = full_day(monday)
        sixty = [r for k, r in enumerate(rows) if k < 2 or k >= 17]                         # a 75-minute hole
        self.assertEqual(len(sixty), 60)
        self.assertIsNone(incomplete_reason(aggregate_bars(sixty)[0], CANDLES_5M))
        muhurat = aggregate_bars(rows[:12])[0]
        self.assertIsNotNone(incomplete_reason(muhurat, CANDLES_5M))

    def test_a_forward_filled_session_is_not_a_quiet_one(self):
        monday = date(2026, 9, 21)
        rows = full_day(monday)
        filled = rows[:12] + [(t, 111.0, 111.0, 111.0, 111.0) for t, *_ in rows[12:]]   # a Muhurat hour, then flat
        agg = aggregate_bars(filled)[0]
        self.assertEqual((agg.bars, agg.flat), (75, 63))
        self.assertIn("filled in: 63 of 75 bars flat", incomplete_reason(agg, CANDLES_5M))
        # FYERS's two flat closing bars are not a filled-in day
        two = rows[:-2] + [(t, 175.0, 175.0, 175.0, 175.0) for t, *_ in rows[-2:]]
        self.assertIsNone(incomplete_reason(aggregate_bars(two)[0], CANDLES_5M))

    def test_one_minute_live_bars_need_the_last_minutes(self):
        monday = date(2026, 9, 21)
        rows = full_day(monday, minutes=1)
        self.assertEqual(len(rows), 375)
        self.assertIsNone(incomplete_reason(aggregate_bars(rows)[0], LIVE_1M))
        # scored at 15:26 from a feed that stopped: not the day's close
        self.assertIn("last bar", incomplete_reason(aggregate_bars(rows[:-5])[0], LIVE_1M))


class MergeTests(unittest.TestCase):
    def bar(self, day, source, close=1.0):
        return SessionBar(day, 1.0, 2.0, 0.5, close, 75, source)

    def test_candles_win_and_live_bars_fill_only_what_candles_lack(self):
        d1, d2, d3 = date(2026, 9, 22), date(2026, 9, 23), date(2026, 9, 24)
        candles = ([self.bar(d1, "candles 5m", 10.0)], [(d2, "40 of 75 bars (candles 5m)")])
        live = ([self.bar(d1, "live_bars 1m", 99.0), self.bar(d2, "live_bars 1m", 11.0)],
                [(d3, "last bar at 12:00 (live_bars 1m)")])
        sessions, dropped = merge_live(candles, live)
        self.assertEqual([(s.day, s.source, s.close) for s in sessions],
                         [(d1, "candles 5m", 10.0), (d2, "live_bars 1m", 11.0)])
        self.assertEqual(dropped, [(d3, "last bar at 12:00 (live_bars 1m)")])

    def test_load_series_runs_one_query_per_source(self):
        day = date(2026, 9, 25)

        class Cursor:
            def __init__(self, log):
                self.log = log

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

            def execute(self, sql, params):
                self.log.append((sql, params))

            def fetchall(self):
                sql, params = self.log[-1]
                if "live_bars" in sql:
                    agg = aggregate_bars(full_day(day, minutes=1))[0]
                    return [(agg.day, agg.open, agg.high, agg.low, agg.close, agg.bars, agg.first_utc, agg.last_utc,
                             agg.flat)]
                return []

        class Conn:
            def __init__(self):
                self.log = []

            def cursor(self):
                return Cursor(self.log)

        conn = Conn()
        series = data.load_series(conn, "NSE:NIFTY50-INDEX", None, day)
        self.assertEqual(len(conn.log), 2)
        self.assertTrue(all(p["symbol"] == "NSE:NIFTY50-INDEX" for _, p in conn.log))
        self.assertEqual([s.source for s in series.sessions], ["live_bars 1m"])
        self.assertEqual(conn.log[0][1]["resolution"], "5")
        self.assertEqual(conn.log[1][1]["resolution"], "1m")


class CalendarTests(unittest.TestCase):
    NIFTY = Underlying("NIFTY", "NSE:NIFTY50-INDEX", "NSE")
    BANKNIFTY = Underlying("BANKNIFTY", "NSE:NIFTYBANK-INDEX", "NSE")
    SENSEX = Underlying("SENSEX", "BSE:SENSEX-INDEX", "BSE")

    def test_recorded_days_are_what_the_exchange_recorded(self):
        cal = ExpiryCalendar({"NIFTY": frozenset({date(2024, 3, 27)})}, {"NSE": date(2024, 12, 31)}, {}, {})
        self.assertTrue(cal.is_expiry(self.NIFTY, date(2024, 3, 27)))   # a Wednesday: Holi moved it
        self.assertFalse(cal.is_expiry(self.NIFTY, date(2024, 3, 28)))  # the Thursday the rule would say

    def test_after_the_record_the_master_speaks_for_its_week_and_the_rule_elsewhere(self):
        holidays = {"NSE": frozenset({date(2026, 10, 20), date(2026, 11, 24)})}
        cal = ExpiryCalendar({}, {"NSE": date(2026, 9, 16), "BSE": date(2026, 9, 16)},
                             {"NIFTY": frozenset({date(2026, 9, 22)})}, holidays)
        self.assertTrue(cal.is_expiry(self.NIFTY, date(2026, 9, 22)))
        self.assertFalse(cal.is_expiry(self.NIFTY, date(2026, 9, 24)))
        # no listing near 13 Oct: the Tuesday rule
        self.assertTrue(cal.is_expiry(self.NIFTY, date(2026, 10, 13)))
        # a Tuesday holiday moves the weekly to Monday
        self.assertTrue(cal.is_expiry(self.NIFTY, date(2026, 10, 19)))
        self.assertFalse(cal.is_expiry(self.NIFTY, date(2026, 10, 20)))
        # SENSEX: Thursdays on BSE
        self.assertTrue(cal.is_expiry(self.SENSEX, date(2026, 10, 15)))
        self.assertFalse(cal.is_expiry(self.SENSEX, date(2026, 10, 13)))
        # BANKNIFTY: monthlies only, the last Tuesday — or the day before when that is a holiday
        self.assertTrue(cal.is_expiry(self.BANKNIFTY, date(2026, 10, 27)))
        self.assertFalse(cal.is_expiry(self.BANKNIFTY, date(2026, 10, 13)))
        self.assertTrue(cal.is_expiry(self.BANKNIFTY, date(2026, 11, 23)))
        self.assertFalse(cal.is_expiry(self.BANKNIFTY, date(2026, 11, 24)))

    def test_the_seed_calendar_loads(self):
        cal = data.load_expiry_calendar(None, {})
        self.assertIn("NSE", cal.recorded_through)
        self.assertTrue(cal.is_expiry(self.NIFTY, date(2026, 9, 15)))
        self.assertFalse(cal.is_expiry(self.NIFTY, date(2026, 9, 16)))

    def test_trading_days_skip_weekends_and_holidays(self):
        holidays = {"NSE": frozenset({date(2026, 9, 14)})}
        self.assertFalse(is_trading_day("NSE", date(2026, 9, 14), holidays))
        self.assertTrue(is_trading_day("BSE", date(2026, 9, 14), holidays))
        self.assertEqual(previous_trading_day("NSE", date(2026, 9, 15), holidays), date(2026, 9, 11))
        self.assertEqual(previous_trading_day("NSE", date(2026, 9, 21), holidays), date(2026, 9, 18))


class SessionSqlTests(unittest.TestCase):
    """The session SQL against a real Postgres, when one is configured (skipped in CI)."""

    def connect(self):
        try:
            import psycopg2

            import core.config  # noqa: F401
            from sentinel.store import dsn_from_env
        except ImportError:
            self.skipTest("psycopg2 not installed")
        dsn = dsn_from_env(dict(os.environ))
        if dsn is None:
            self.skipTest("no POSTGRES_PASSWORD configured")
        try:
            return psycopg2.connect(dsn, connect_timeout=2)
        except Exception:
            self.skipTest("no database reachable")

    def test_the_sql_folds_sessions_exactly_as_the_python_reference(self):
        conn = self.connect()
        try:
            day, other = date(2026, 9, 21), date(2026, 9, 22)
            rows = (full_day(day) + full_day(other, start_price=500.0)[:50]
                    + [(ist(other, 12, 0), 7.0, 7.0, 7.0, 7.0)]                        # a flat bar, counted
                    + [(ist(day, 9, 10), 1.0, 1000.0, 0.5, 1.0), (ist(day, 15, 30), 1.0, 1000.0, 0.5, 1.0),
                       (ist(day, 0, 0), 1.0, 1000.0, 0.5, 1.0)])
            with conn.cursor() as cur:
                cur.execute('CREATE TEMP TABLE analysis_sql_check ("Symbol" text, "Resolution" text, '
                            '"TimeStampUtc" timestamptz, "Open" numeric, "High" numeric, "Low" numeric, '
                            '"Close" numeric)')
                for ts, o, h, l, c in rows + [(ist(day, 10, 0), 1.0, 5000.0, 0.1, 1.0)]:
                    symbol = "X" if h != 5000.0 else "Y"          # another symbol's bar must not leak in
                    cur.execute("INSERT INTO analysis_sql_check VALUES (%s, '5', %s, %s, %s, %s, %s)",
                                (symbol, ts, o, h, l, c))
            got = data.fetch_aggregates(conn, CANDLES_5M, "X", day, other, table="analysis_sql_check")
            self.assertEqual(got, aggregate_bars(rows))
        finally:
            conn.rollback()
            conn.close()


if __name__ == "__main__":
    unittest.main()
