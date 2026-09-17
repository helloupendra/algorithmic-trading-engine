"""
Index history import: IST file stamps become UTC bar starts, 1-minute bars roll up
to the platform's 15-minute and daily stamps, and option files keep only the bars
before the cut-off with their strike, offset and series.
"""

import unittest
from datetime import datetime, timezone

from tools import index_history_import as imp


def minute(ist, o, h, l, c):
    return {"bar_start_ist": ist, "open": str(o), "high": str(h), "low": str(l), "close": str(c), "volume": "0.0"}


class ImportTests(unittest.TestCase):
    def test_ist_stamps_become_utc_bar_starts(self):
        self.assertEqual(imp.ist_to_utc("2021-03-01 09:15"), datetime(2021, 3, 1, 3, 45, tzinfo=timezone.utc))

    def test_minutes_roll_up_to_quarter_hours_from_the_open_and_to_a_day_stamped_midnight_utc(self):
        bars = imp.candle_rows("NSE:NIFTY50-INDEX", "1", [
            minute("2021-03-01 09:15", 100, 101, 99, 100.5),
            minute("2021-03-01 09:29", 100.5, 104, 100, 103),
            minute("2021-03-01 09:30", 103, 103, 98, 99),
            minute("2021-03-01 15:29", 99, 99.5, 97, 98),
        ])
        quarter = imp.roll_up(bars, "15")
        self.assertEqual([b[2] for b in quarter], [datetime(2021, 3, 1, 3, 45, tzinfo=timezone.utc),
                                                    datetime(2021, 3, 1, 4, 0, tzinfo=timezone.utc),
                                                    datetime(2021, 3, 1, 9, 45, tzinfo=timezone.utc)])
        self.assertEqual(quarter[0][3:7], (100.0, 104.0, 99.0, 103.0))
        day = imp.roll_up(bars, "D")
        self.assertEqual(len(day), 1)
        self.assertEqual(day[0][1:7], ("D", datetime(2021, 3, 1, tzinfo=timezone.utc), 100.0, 104.0, 97.0, 98.0))
        self.assertEqual(day[0][8], "dhan")

    def test_option_files_keep_series_strike_and_only_bars_before_the_cutoff(self):
        self.assertEqual(imp.option_file_series("CE_+3.csv.gz"), ("CE", 3))
        self.assertEqual(imp.option_file_series("PE_-10.csv.gz"), ("PE", -10))
        self.assertIsNone(imp.option_file_series("manifest.jsonl"))
        rows = [
            {"bar_start_ist": "2024-08-30 15:29", "open": "10", "high": "11", "low": "9", "close": "10.5",
             "iv": "12.3", "volume": "1500.0", "oi": "", "strike": "25200.0", "spot": "25236.1"},
            {"bar_start_ist": "2024-09-02 09:15", "open": "20", "high": "21", "low": "19", "close": "20.5",
             "iv": "12.0", "volume": "10", "oi": "5", "strike": "25300.0", "spot": "25310"},
        ]
        cutoff = datetime(2024, 9, 2, 3, 45, tzinfo=timezone.utc)
        out = imp.option_rows("NIFTY", "CE", -1, rows, before=cutoff)
        self.assertEqual(len(out), 1)
        bar = out[0]
        self.assertEqual(bar[:9], (datetime(2024, 8, 30, 9, 59, tzinfo=timezone.utc), "NIFTY", "WEEK", 1, None, -1,
                                   25200.0, "CE", "1m"))
        self.assertEqual((bar[13], bar[14], bar[15]), (1500, None, 12.3))


class EquityImportTests(unittest.TestCase):
    """The stock importer's own pieces: the platform's symbol, and reading a name list."""

    def test_a_stock_name_becomes_the_platforms_equity_symbol(self):
        from tools import equities_candles_import as eq
        self.assertEqual(eq.equity_symbol("reliance"), "NSE:RELIANCE-EQ")
        self.assertEqual(eq.equity_symbol("NSE:TCS-EQ"), "NSE:TCS-EQ")

    def test_names_are_read_from_a_list_or_a_file_without_repeats(self):
        import tempfile
        from tools import equities_candles_import as eq
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as handle:
            handle.write("hdfcbank\nreliance,INFY\n")
            path = handle.name
        self.assertEqual(eq.read_symbol_list("reliance, tcs", path), ["RELIANCE", "TCS", "HDFCBANK", "INFY"])
        self.assertEqual(eq.read_symbol_list(None, None), [])


if __name__ == "__main__":
    unittest.main()
