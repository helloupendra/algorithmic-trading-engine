"""
The rules of the option chain's conversion to a compressed hypertable.

The SQL itself was run end to end on a scratch database with the production
table's DDL on 31 Oct 2026: prepare, copy, swap and drop-old, then a new
capture with its identity id, a refused duplicate, and an update and an insert
on a compressed day.
"""

import os
import sys
import unittest
from datetime import date

import _bootstrap  # noqa: F401

SCRIPTS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "scripts"))
if SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, SCRIPTS_DIR)

import compress_option_chain as coc  # noqa: E402


class CompressOptionChainTests(unittest.TestCase):
    def test_only_days_not_held_in_full_are_copied(self):
        live = {date(2026, 10, 20): 90, date(2026, 10, 21): 100, date(2026, 10, 22): 100}
        copied = {date(2026, 10, 20): 90, date(2026, 10, 21): 40}
        self.assertEqual([date(2026, 10, 21), date(2026, 10, 22)], coc.days_to_copy(live, copied))

    def test_the_last_two_days_stay_uncompressed_for_the_recorder(self):
        today = date(2026, 10, 31)
        self.assertTrue(coc.compressible(date(2026, 10, 29), today))
        self.assertFalse(coc.compressible(date(2026, 10, 30), today))
        self.assertFalse(coc.compressible(today, today))

    def test_the_swap_hands_the_app_its_own_names_back(self):
        sql = coc.swap_statements()
        self.assertEqual("ALTER TABLE option_chain_snapshots RENAME TO option_chain_snapshots_old", sql[0])
        self.assertIn("ALTER TABLE option_chain_snapshots_ht RENAME TO option_chain_snapshots", sql)
        self.assertIn('ALTER TABLE option_chain_snapshots RENAME CONSTRAINT "PK_option_chain_snapshots_ht" '
                      'TO "PK_option_chain_snapshots"', sql)
        for name in coc.INDEXES:
            self.assertIn(f"ALTER INDEX {name}_ht RENAME TO {name}", sql)
        # The old table's names move out of the way before the new one takes them.
        self.assertLess(sql.index("ALTER INDEX ux_chain_symbol_time RENAME TO ux_chain_symbol_time_old"),
                        sql.index("ALTER INDEX ux_chain_symbol_time_ht RENAME TO ux_chain_symbol_time"))
        self.assertEqual('ALTER SEQUENCE "option_chain_snapshots_ht_Id_seq" RENAME TO "option_chain_snapshots_Id_seq"', sql[-1])


if __name__ == "__main__":
    unittest.main()
