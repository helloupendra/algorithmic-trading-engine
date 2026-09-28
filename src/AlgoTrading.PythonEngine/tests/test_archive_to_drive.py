"""
The rules that decide what is copied to Drive and what may be freed.

A day is copied only once it is over, and freed only when every archived table
is verified on Drive; the gzipped CSV is read back with quoted newlines intact.
The streaming, Drive and database sides were exercised on a real day on
2026-09-15 (168,327 ticks archived, MD5 matched, restored into a scratch
database with an identical content hash).
"""

import gzip
import io
import os
import sys
import unittest
from datetime import date, datetime, timezone
from unittest import mock

import _bootstrap  # noqa: F401

SCRIPTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts"))
if SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, SCRIPTS_DIR)

import archive_to_drive as ad  # noqa: E402


class ArchiveRuleTests(unittest.TestCase):
    def test_only_days_over_in_utc_are_closed(self):
        # 04:00 IST on the 16th is still the 15th in UTC: the 15th is not closed.
        early = datetime(2026, 9, 15, 22, 30, tzinfo=timezone.utc)
        self.assertEqual([date(2026, 9, 13), date(2026, 9, 14)], ad.closed_days(date(2026, 9, 13), early))
        after = datetime(2026, 9, 16, 0, 30, tzinfo=timezone.utc)  # 06:00 IST
        self.assertEqual(date(2026, 9, 15), ad.closed_days(date(2026, 9, 13), after)[-1])

    def test_a_day_is_freed_only_when_every_table_is_verified(self):
        entries = [
            {"table": "live_ticks", "day": "2026-09-10", "verified": True},
            {"table": "option_chain_snapshots", "day": "2026-09-10", "verified": True},
            {"table": "live_bars", "day": "2026-09-10", "verified": True},
            {"table": "run_pnl_minutes", "day": "2026-09-10", "verified": True},
            {"table": "live_ticks", "day": "2026-09-11", "verified": True},
            {"table": "option_chain_snapshots", "day": "2026-09-11", "verified": False},
            {"table": "live_bars", "day": "2026-09-11", "verified": True},
            {"table": "run_pnl_minutes", "day": "2026-09-11", "verified": True},
        ]
        verified = ad.verified_days(entries)
        self.assertEqual([date(2026, 9, 10)],
                         ad.droppable_days([date(2026, 9, 10), date(2026, 9, 11), date(2026, 9, 12)], verified, ad.TABLES))

    def test_the_live_runs_pnl_minutes_are_archived_by_their_minute(self):
        # Every datum is kept (owner's rule): the run P&L minutes go to Drive
        # with the market data, placed in a day by the minute they record.
        self.assertEqual("AtUtc", ad.TABLES["run_pnl_minutes"])
        # A plain table: a verified day is freed with a DELETE of that day.
        self.assertNotIn("run_pnl_minutes", ad.HYPERTABLES)

    def test_a_day_whose_pnl_minutes_are_not_on_drive_is_kept(self):
        entries = [{"table": t, "day": "2026-09-29", "verified": True}
                   for t in ad.TABLES if t != "run_pnl_minutes"]
        verified = ad.verified_days(entries)
        self.assertEqual([], ad.droppable_days([date(2026, 9, 29)], verified, ad.TABLES))

    def test_freeing_a_day_of_pnl_minutes_deletes_that_utc_day_only(self):
        sql = []
        with mock.patch.object(ad, "psql", side_effect=lambda q: sql.append(q) or "0"), \
                mock.patch.object(ad, "say"):
            ad.drop_local([date(2026, 9, 29)], dry_run=False)
        statement = [q for q in sql if "run_pnl_minutes" in q]
        self.assertEqual(
            ['delete from run_pnl_minutes where "AtUtc" >= \'2026-09-29T00:00:00+00:00\' '
             'and "AtUtc" < \'2026-09-30T00:00:00+00:00\''],
            statement)

    def test_the_pnl_minutes_days_are_read_from_their_minute_column(self):
        sql = []
        now = datetime(2026, 9, 30, 0, 30, tzinfo=timezone.utc)  # 06:00 IST on the 30th
        with mock.patch.object(ad, "psql", side_effect=lambda q: sql.append(q) or "2026-09-28\n2026-09-29\n2026-09-30"):
            days = ad.days_with_rows("run_pnl_minutes", "AtUtc", set(), None, now)
        self.assertEqual([date(2026, 9, 28), date(2026, 9, 29)], days)  # the 30th is not over yet
        self.assertEqual(['select distinct ("AtUtc" at time zone \'UTC\')::date from run_pnl_minutes  order by 1'], sql)

    def test_remote_path_is_a_folder_per_year_month_and_day(self):
        self.assertEqual("gd:openfno-archive/2026/09/15/live_ticks.csv.gz",
                         ad.remote_path("gd:openfno-archive/", "live_ticks", date(2026, 9, 15)))

    def test_rows_are_counted_with_quoted_newlines(self):
        raw = b'Id,RawPayload\n1,"{""a"":\n1}"\n2,plain\n'
        self.assertEqual(2, ad.count_csv_rows(io.BytesIO(gzip.compress(raw))))

    def test_market_ticks_is_freed_but_never_archived(self):
        # It is a second copy of live_ticks; one copy on Drive is enough.
        self.assertNotIn("market_ticks", ad.TABLES)
        self.assertIn("market_ticks", ad.HYPERTABLES)


if __name__ == "__main__":
    unittest.main()
