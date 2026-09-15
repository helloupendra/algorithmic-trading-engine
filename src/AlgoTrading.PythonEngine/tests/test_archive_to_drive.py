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
            {"table": "live_ticks", "day": "2026-09-11", "verified": True},
            {"table": "option_chain_snapshots", "day": "2026-09-11", "verified": False},
            {"table": "live_bars", "day": "2026-09-11", "verified": True},
        ]
        verified = ad.verified_days(entries)
        self.assertEqual([date(2026, 9, 10)],
                         ad.droppable_days([date(2026, 9, 10), date(2026, 9, 11), date(2026, 9, 12)], verified, ad.TABLES))

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
