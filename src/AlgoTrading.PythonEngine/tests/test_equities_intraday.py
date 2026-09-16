"""
Dhan intraday download: windows cover the range without overlap, bars are
trimmed to the session and the window, a re-run skips finished windows, and an
expired token stops the run instead of recording thousands of failures.
"""

import os
import tempfile
import unittest
from datetime import date, datetime, timedelta, timezone

from research.equities import intraday as idl

IST = timezone(timedelta(hours=5, minutes=30))


def epoch(text):
    return int(datetime.strptime(text, "%Y-%m-%d %H:%M").replace(tzinfo=IST).timestamp())


def payload(stamps):
    n = len(stamps)
    return {"open": [100.0] * n, "high": [101.0] * n, "low": [99.0] * n, "close": [100.5] * n,
            "volume": [1000] * n, "timestamp": [epoch(s) for s in stamps]}


class NoWait(idl.Pacer):
    def __init__(self):
        super().__init__(spacing=0)

    def wait(self):
        pass


class WindowTests(unittest.TestCase):
    def test_windows_are_contiguous_and_at_most_90_days(self):
        ws = idl.windows(date(2024, 9, 1), date(2026, 9, 15))
        self.assertEqual(date(2024, 9, 1), ws[0][0])
        self.assertEqual(date(2026, 9, 15), ws[-1][1])
        for (lo, hi), (nxt, _) in zip(ws, ws[1:]):
            self.assertLessEqual((hi - lo).days + 1, 90)
            self.assertEqual(hi + timedelta(days=1), nxt)

    def test_request_asks_from_0900_so_the_0915_bar_is_included(self):
        body = idl.request_body("2885", date(2025, 1, 1), date(2025, 3, 31))
        self.assertEqual("2025-01-01 09:00:00", body["fromDate"])
        self.assertEqual(("NSE_EQ", "EQUITY", "5"), (body["exchangeSegment"], body["instrument"], body["interval"]))


class ParseTests(unittest.TestCase):
    def test_bars_outside_the_session_or_the_window_are_dropped(self):
        rows = idl.parse_bars(payload(["2025-01-01 09:10", "2025-01-01 09:15", "2025-01-01 15:25",
                                       "2025-01-01 15:30", "2025-01-02 09:15"]), date(2025, 1, 1), date(2025, 1, 1))
        self.assertEqual(["2025-01-01 09:15", "2025-01-01 15:25"], [r["bar_start_ist"] for r in rows])


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.bodies = []

    def poster(self, answers):
        def post(body):
            self.bodies.append(body)
            return answers(body)
        return post

    def test_a_rerun_fetches_only_unfinished_windows(self):
        def answers(body):
            day = body["fromDate"][:10]
            return 200, payload([f"{day} 09:15", f"{day} 09:20"])
        stocks = [("RELIANCE", "2885")]
        counts = idl.download(self.root, stocks, date(2025, 1, 1), date(2025, 6, 30), post=self.poster(answers),
                              log=lambda _: None, pacer=NoWait())
        self.assertEqual({"ok": 3, "empty": 0, "error": 0, "skipped": 0}, counts)
        self.assertTrue(os.path.exists(idl.window_path(self.root, "RELIANCE", date(2025, 1, 1), date(2025, 3, 31))))
        self.bodies.clear()
        counts = idl.download(self.root, stocks, date(2025, 1, 1), date(2025, 6, 30), post=self.poster(answers),
                              log=lambda _: None, pacer=NoWait())
        self.assertEqual([], self.bodies)
        self.assertEqual(3, counts["skipped"])

    def test_a_corrected_dhan_id_fetches_the_windows_again(self):
        def answers(body):
            return 200, payload([body["fromDate"][:10] + " 09:15"])
        idl.download(self.root, [("CHOLAFIN", "19257")], date(2025, 1, 1), date(2025, 1, 31),
                     post=self.poster(answers), log=lambda _: None, pacer=NoWait())
        self.bodies.clear()
        idl.download(self.root, [("CHOLAFIN", "685")], date(2025, 1, 1), date(2025, 1, 31),
                     post=self.poster(answers), log=lambda _: None, pacer=NoWait())
        self.assertEqual(["685"], [b["securityId"] for b in self.bodies])

    def test_a_window_before_listing_is_empty_not_an_error(self):
        counts = idl.download(self.root, [("NEWCO", "1")], date(2025, 1, 1), date(2025, 1, 31),
                              post=self.poster(lambda body: (200, payload([]))), log=lambda _: None, pacer=NoWait())
        self.assertEqual(1, counts["empty"])

    def test_an_expired_token_stops_the_run(self):
        with self.assertRaises(idl.AuthError):
            idl.download(self.root, [("A", "1"), ("B", "2")], date(2025, 1, 1), date(2025, 1, 31),
                         post=self.poster(lambda body: (401, {"errorCode": "DH-901"})), log=lambda _: None,
                         pacer=NoWait())
        self.assertEqual(1, len(self.bodies))

    def test_rate_limit_answers_are_retried(self):
        answers = iter([(429, {"errorCode": "DH-904"}), (200, payload(["2025-01-01 09:15"]))])
        manifest = idl.Manifest(self.root)
        attempt = idl.fetch_window(self.root, manifest, "A", "1", date(2025, 1, 1), date(2025, 1, 31),
                                   self.poster(lambda body: next(answers)), NoWait(), sleep=lambda _: None)
        self.assertEqual(("ok", 2), (attempt.status, len(self.bodies)))


if __name__ == "__main__":
    unittest.main()
