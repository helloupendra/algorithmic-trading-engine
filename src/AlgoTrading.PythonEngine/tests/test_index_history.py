"""
Index history (D0): candle and option requests go to their own endpoints with the
right segment and instrument, option jobs run month by month, parsing keeps the
window and the session, finished jobs are skipped, and an expired token stops the
run.
"""

import os
import tempfile
import unittest
from datetime import date, datetime, timezone

from research import index_history as ih
from research.equities.intraday import AuthError, Pacer


def stamp(ist):
    return int(datetime.fromisoformat(ist + "+05:30").astimezone(timezone.utc).timestamp())


def block(stamps, extra=()):
    ts = [stamp(s) for s in stamps]
    b = {"timestamp": ts, "open": [1.0] * len(ts), "high": [2.0] * len(ts), "low": [0.5] * len(ts),
         "close": [1.5] * len(ts), "volume": [10] * len(ts)}
    for col in extra:
        b[col] = [7.0] * len(ts)
    return b


class RequestTests(unittest.TestCase):
    def test_candles_and_options_use_their_own_endpoints(self):
        c = ih.candle_jobs(["NIFTY"], ["5"], date(2020, 8, 1), date(2020, 12, 31))
        url, body = ih.request(c[0])
        self.assertEqual(ih.CANDLES_URL, url)
        self.assertEqual(("13", "IDX_I", "INDEX", "5"), (body["securityId"], body["exchangeSegment"], body["instrument"],
                                                          body["interval"]))
        self.assertEqual(2, len(c))                                  # 90-day windows
        url, body = ih.request(ih.Job("options", "SENSEX", "51", "1", date(2024, 3, 1), date(2024, 3, 31), "PE", -5))
        self.assertEqual(ih.OPTIONS_URL, url)
        self.assertEqual(("BSE_FNO", "OPTIDX", "WEEK", 1, "ATM-5", "PUT", 51),
                         (body["exchangeSegment"], body["instrument"], body["expiryFlag"], body["expiryCode"],
                          body["strike"], body["drvOptionType"], body["securityId"]))
        with self.assertRaises(ValueError):
            ih.request(ih.Job("options", "INDIAVIX", "21", "1", date(2024, 3, 1), date(2024, 3, 31), "CE", 0))

    def test_option_jobs_go_month_by_month(self):
        jobs = ih.option_jobs(["NIFTY", "BANKNIFTY"], date(2020, 8, 15), date(2020, 9, 10), offsets=(0,))
        self.assertEqual(8, len(jobs))
        self.assertEqual([date(2020, 8, 15)] * 4 + [date(2020, 9, 1)] * 4, [j.lo for j in jobs])
        self.assertEqual(date(2020, 9, 10), jobs[-1].hi)

    def test_parse_keeps_window_and_session(self):
        job = ih.Job("candles", "NIFTY", "13", "5", date(2020, 9, 1), date(2020, 9, 30))
        rows = ih.parse(job, block(["2020-08-31 15:25", "2020-09-01 09:10", "2020-09-01 09:15", "2020-09-30 15:25"]))
        self.assertEqual(["2020-09-01 09:15", "2020-09-30 15:25"], [r["bar_start_ist"] for r in rows])
        opt = ih.Job("options", "NIFTY", "13", "1", date(2020, 9, 1), date(2020, 9, 30), "CE", 0)
        rows = ih.parse(opt, {"data": {"ce": block(["2020-09-01 09:15"], extra=("iv", "oi", "strike", "spot")), "pe": None}})
        self.assertEqual((7.0, 7.0), (rows[0]["strike"], rows[0]["spot"]))


class RunTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.calls = []

    def post(self, url, body):
        self.calls.append(url)
        if url == ih.CANDLES_URL:
            return 200, block(["2020-09-01 09:15"])
        return 200, {"data": {"ce": block(["2020-09-01 09:15"], extra=("iv", "oi", "strike", "spot")), "pe": None}}

    def test_finished_jobs_are_skipped_and_empties_are_final(self):
        jobs = [ih.Job("candles", "NIFTY", "13", "5", date(2020, 9, 1), date(2020, 9, 30)),
                ih.Job("options", "NIFTY", "13", "1", date(2020, 9, 1), date(2020, 9, 30), "CE", 0),
                ih.Job("options", "NIFTY", "13", "1", date(2020, 9, 1), date(2020, 9, 30), "PE", 0)]
        counts = ih.run(self.root, jobs, self.post, Pacer(spacing=0, sleep=lambda _: None), log=lambda _: None)
        self.assertEqual({"ok": 2, "empty": 1, "error": 0, "skipped": 0}, counts)
        self.assertTrue(os.path.exists(ih.path_of(self.root, jobs[1])))
        self.calls.clear()
        counts = ih.run(self.root, jobs, self.post, Pacer(spacing=0, sleep=lambda _: None), log=lambda _: None)
        self.assertEqual([], self.calls)
        self.assertEqual(3, counts["skipped"])

    def test_expired_token_stops(self):
        jobs = ih.candle_jobs(["NIFTY"], ["5"], date(2020, 9, 1), date(2020, 9, 30))
        with self.assertRaises(AuthError):
            ih.run(self.root, jobs, lambda url, body: (401, {}), Pacer(spacing=0, sleep=lambda _: None), log=lambda _: None)


if __name__ == "__main__":
    unittest.main()
