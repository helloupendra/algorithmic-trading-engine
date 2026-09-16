"""
Mover screen: the volume baseline follows R2's rule and never reads the day it
is built for, the usual volume by time of day is interpolated, Dhan quotes
parse, the 09:30 list is frozen once (and not at all when the screen starts
late), and the page escapes what it prints.
"""

import os
import tempfile
import unittest
from datetime import date, datetime, timedelta, timezone

import numpy as np

from research.equities import screen as sc
from research.equities.setups import SessionBars

IST = timezone(timedelta(hours=5, minutes=30))
TIMES = [f"{(555 + 5 * k) // 60:02d}:{(555 + 5 * k) % 60:02d}" for k in range(75)]   # 09:15 ... 15:25


def session(day, volume_per_bar=100.0, times=TIMES):
    n = len(times)
    one = np.ones(n)
    return SessionBars("AAA", day, list(times), one, one, one, one, np.full(n, volume_per_bar))


def at(hhmm_ss):
    h, m, s = (int(x) for x in hhmm_ss.split(":"))
    return datetime(2026, 9, 17, h, m, s, tzinfo=IST)


def baseline(symbol, sid, or15=300.0, cum=None, prev_close=100.0, prev_high=102.0, prev_low=98.0, atr=2.0,
             asm=""):
    return sc.Baseline(symbol, sid, "INE000000000", "2026-09-16", prev_close, prev_high, prev_low, atr, 2e8, or15,
                       cum if cum is not None else [100.0 * (k + 1) for k in range(75)], 20, asm)


def quote(sid, ltp, volume, high=None, low=None, open_=None, upper=None, lower=None):
    return sc.Quote(sid, ltp, open_ or ltp, high or ltp, low or ltp, volume, upper, lower, "")


class SlotTests(unittest.TestCase):
    def test_slots(self):
        self.assertEqual((0, 1, 74), (sc.slot_of("09:15"), sc.slot_of("09:20"), sc.slot_of("15:25")))
        self.assertIsNone(sc.slot_of("09:10"))
        self.assertIsNone(sc.slot_of("15:30"))
        self.assertEqual(TIMES[-1], "15:25")


class BaselineTests(unittest.TestCase):
    def test_follows_r2_and_ignores_the_day_it_is_built_for(self):
        days = [f"2026-08-{d:02d}" for d in range(1, 26)]
        sessions = {d: session(d, volume_per_bar=100.0) for d in days}
        # A session missing its 09:25 bar has no full opening range and does not count.
        broken = [t for t in TIMES if t != "09:25"]
        sessions["2026-08-24"] = session("2026-08-24", 100.0, broken)
        # The day being screened must not leak into its own baseline.
        sessions["2026-08-26"] = session("2026-08-26", volume_per_bar=1e9)
        out = sc.baseline_from_sessions(sessions, before="2026-08-26")
        self.assertEqual(19, out["sessions"])              # last 20 before the 26th, minus the broken one
        self.assertAlmostEqual(300.0, out["or15_volume"])
        self.assertEqual(75, len(out["cum_volume"]))
        self.assertAlmostEqual(100.0, out["cum_volume"][0])
        self.assertAlmostEqual(7500.0, out["cum_volume"][-1])

    def test_too_little_history_gives_no_baseline(self):
        sessions = {f"2026-08-{d:02d}": session(f"2026-08-{d:02d}") for d in range(1, 10)}
        out = sc.baseline_from_sessions(sessions, before="2026-09-01")
        self.assertIsNone(out["or15_volume"])
        self.assertEqual([], out["cum_volume"])

    def test_a_missing_bar_adds_nothing_to_the_running_volume(self):
        gap = [t for t in TIMES if t != "10:00"]
        sessions = {f"2026-08-{d:02d}": session(f"2026-08-{d:02d}", 10.0, gap) for d in range(1, 13)}
        cum = sc.baseline_from_sessions(sessions, before="2026-09-01")["cum_volume"]
        k = sc.slot_of("10:00")
        self.assertAlmostEqual(cum[k - 1], cum[k])


class ExpectedVolumeTests(unittest.TestCase):
    def test_interpolates_inside_the_slot(self):
        cum = [100.0 * (k + 1) for k in range(75)]
        self.assertIsNone(sc.expected_volume(cum, at("09:15:00")))
        self.assertAlmostEqual(50.0, sc.expected_volume(cum, at("09:17:30")))
        self.assertAlmostEqual(250.0, sc.expected_volume(cum, at("09:27:30")))
        self.assertAlmostEqual(7500.0, sc.expected_volume(cum, at("15:40:00")))
        self.assertIsNone(sc.expected_volume([], at("10:00:00")))


class QuoteTests(unittest.TestCase):
    def test_parses_dhan_quotes_and_skips_unpriced_rows(self):
        payload = {"status": "success", "data": {"NSE_EQ": {
            "2885": {"last_price": 1240, "ohlc": {"open": 1243, "close": 1240, "high": 1255, "low": 1240},
                     "volume": 10023997, "upper_circuit_limit": 1285, "lower_circuit_limit": 1210.2,
                     "last_trade_time": "16/09/2026 15:58:24", "depth": {"buy": [], "sell": []}},
            "9999": {"last_price": 0, "ohlc": {}, "volume": 0}}}}
        quotes = sc.parse_quotes(payload)
        self.assertEqual(["2885"], list(quotes))
        q = quotes["2885"]
        self.assertEqual((1240.0, 1243.0, 1255.0, 1240.0, 10023997.0, 1285.0), (q.ltp, q.open, q.high, q.low,
                                                                              q.volume, q.upper_circuit))
        self.assertEqual({}, sc.parse_quotes({"data": {}}))


class ScreenTests(unittest.TestCase):
    def setUp(self):
        self.state = sc.ScreenState([
            baseline("QUIET", "1", or15=1000.0),
            baseline("LOUD", "2", or15=100.0, asm="Y"),
            baseline("<b>X", "3", or15=500.0),
        ])

    def test_rows_rank_by_volume_against_the_usual_for_the_time_of_day(self):
        snap = self.state.update({"1": quote("1", 101.0, 250.0), "2": quote("2", 103.0, 1000.0, upper=103.0)},
                                 at("09:27:30"))
        self.assertEqual(["LOUD", "QUIET"], [r.symbol for r in snap.rows])
        loud = snap.rows[0]
        self.assertAlmostEqual(4.0, loud.rvol_now)          # 1000 against a usual 250 at 09:27:30
        self.assertAlmostEqual(1.5, loud.move_atr)          # +3% on a 2% ATR
        self.assertEqual(["upper circuit", "above prev high", "ASM/GSM"], loud.flags)
        self.assertIsNone(snap.frozen)
        self.assertEqual((2, 3), (snap.quoted, snap.universe))

    def test_the_0930_list_is_frozen_once_and_then_only_tracked(self):
        quotes = {"1": quote("1", 100.0, 2000.0, high=101.0, low=99.0),   # 2× its first 15 minutes
                  "2": quote("2", 100.0, 900.0, high=100.5, low=99.5),    # 9×
                  "3": quote("3", 100.0, 500.0)}                           # 1×
        snap = self.state.update(quotes, at("09:30:04"))
        self.assertEqual(["LOUD", "QUIET", "<b>X"], [f.symbol for f in snap.frozen])
        self.assertAlmostEqual(9.0, snap.frozen[0].rvol15)
        self.assertEqual((100.5, 99.5), (snap.frozen[0].or_high, snap.frozen[0].or_low))

        later = {"1": quote("1", 90.0, 90000.0), "2": quote("2", 105.0, 1000.0), "3": quote("3", 100.0, 99999.0)}
        snap = self.state.update(later, at("11:00:00"))
        self.assertEqual(["LOUD", "QUIET", "<b>X"], [f.symbol for f in snap.frozen])
        self.assertAlmostEqual(5.0, snap.frozen[0].since_0930_pct)
        self.assertAlmostEqual(9.0, snap.frozen[0].rvol15)

    def test_a_screen_started_after_0945_takes_no_0930_list(self):
        snap = self.state.update({"2": quote("2", 100.0, 5000.0)}, at("10:05:00"))
        self.assertEqual([], snap.frozen)
        self.assertIn("after 09:45", snap.frozen_note)

    def test_page_escapes_symbols_and_names_what_is_tested(self):
        snap = self.state.update({"3": quote("3", 100.0, 5000.0)}, at("09:31:00"))
        page = sc.render_html(snap)
        self.assertNotIn("<b>X", page)
        self.assertIn("&lt;b&gt;X", page)
        self.assertIn("09:30 list · tested", page)
        self.assertIn("Relative volume now · not tested", page)
        self.assertIn("not buy or sell calls", page)
        self.assertIn("symbol=NSE%3A%3Cb%3EX", page)          # the chart link is URL-encoded too
        self.assertIn("17 Sep 2026, 09:31:00 IST", page)

    def test_record_keeps_every_quote(self):
        quotes = {"1": quote("1", 101.0, 250.0), "2": quote("2", 103.0, 1000.0)}
        snap = self.state.update(quotes, at("09:20:00"))
        rec = sc.record(snap, quotes)
        self.assertEqual(2, len(rec["quotes"]))
        self.assertIsNone(rec["frozen"])


class ToolTests(unittest.TestCase):
    """The pure parts of tools/mover_screen.py."""

    def setUp(self):
        from tools import mover_screen as ms
        self.ms = ms

    def test_run_uses_the_newest_baseline_from_an_earlier_session(self):
        files = ["/x/baseline-2026-09-15.json", "/x/baseline-2026-09-16.json", "/x/baseline-junk.json",
                 "/x/baseline-2026-09-17.json"]
        self.assertEqual("/x/baseline-2026-09-16.json", self.ms.pick_baseline(files, date(2026, 9, 17)))
        self.assertIsNone(self.ms.pick_baseline(["/x/baseline-2026-09-17.json"], date(2026, 9, 17)))

    def test_maps_by_isin_to_the_nse_eq_row_of_todays_master(self):
        import csv
        import pandas as pd
        folder = tempfile.mkdtemp()
        master = os.path.join(folder, "master.csv")
        with open(master, "w", newline="") as fh:
            w = csv.writer(fh)
            w.writerow(["EXCH_ID", "SEGMENT", "SECURITY_ID", "ISIN", "INSTRUMENT", "UNDERLYING_SYMBOL", "SERIES",
                        "ASM_GSM_FLAG"])
            w.writerow(["NSE", "E", "2885", "INE002A01018", "EQUITY", "RELIANCE", "EQ", "N"])
            w.writerow(["BSE", "E", "500325", "INE002A01018", "EQUITY", "RELIANCE", "A", "N"])
            w.writerow(["NSE", "E", "13188", "INE330H01018", "EQUITY", "RCOM", "BE", "N"])     # trade-to-trade
            w.writerow(["NSE", "D", "35001", "", "OPTSTK", "RELIANCE", "NA", "N"])
        by_isin = self.ms.nse_equities_by_isin(master)
        self.assertEqual({"INE002A01018"}, set(by_isin))
        self.assertEqual("2885", by_isin["INE002A01018"]["security_id"])
        latest = pd.DataFrame({"symbol": ["RELIANCE", "RCOM"], "isin": ["INE002A01018", "INE330H01018"]})
        mapped, unmapped = self.ms.map_to_dhan(latest, by_isin)
        self.assertEqual(["2885"], [m["security_id"] for m in mapped])
        self.assertEqual(["RCOM"], unmapped)

    def test_env_file_is_read_without_quotes_or_comments(self):
        folder = tempfile.mkdtemp()
        path = os.path.join(folder, "dhan.env")
        with open(path, "w") as fh:
            fh.write("# comment\nDHAN_CLIENT_ID='123'\nDHAN_ACCESS_TOKEN=\"abc=def\"\n")
        self.assertEqual({"DHAN_CLIENT_ID": "123", "DHAN_ACCESS_TOKEN": "abc=def"}, self.ms.read_env_file(path))
        self.assertEqual({}, self.ms.read_env_file(os.path.join(folder, "missing.env")))


if __name__ == "__main__":
    unittest.main()
