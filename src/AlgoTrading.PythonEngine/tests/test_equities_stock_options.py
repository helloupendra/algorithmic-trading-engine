"""
Stock option history (D3): the request names the stock option series exactly,
rows outside the month or the session are dropped, a finished series is not
fetched again, a changed security id fetches it again, and an expired token
stops the run instead of recording errors.
"""

import os
import tempfile
import unittest
from datetime import datetime, timezone

from research.equities import stock_options as so
from research.equities.intraday import AuthError, Pacer


def stamp(ist: str) -> int:
    dt = datetime.fromisoformat(ist + "+05:30")
    return int(dt.astimezone(timezone.utc).timestamp())


def payload(side="ce", stamps=("2025-03-03 09:15", "2025-03-03 09:20")):
    ts = [stamp(s) for s in stamps]
    block = {"timestamp": ts, "open": [10.0] * len(ts), "high": [11.0] * len(ts), "low": [9.5] * len(ts),
             "close": [10.5] * len(ts), "iv": [22.0] * len(ts), "volume": [500] * len(ts), "oi": [1000] * len(ts),
             "strike": [1200.0] * len(ts), "spot": [1199.6] * len(ts)}
    return {"data": {"ce": block if side == "ce" else None, "pe": block if side == "pe" else None}}


class RequestTests(unittest.TestCase):
    def test_body_names_the_monthly_stock_option_series(self):
        body = so.request_body(so.Series("RELIANCE", "2885", "2025-02", "PE", -3))
        self.assertEqual(("NSE_FNO", "OPTSTK", "MONTH", 1, 2885), (body["exchangeSegment"], body["instrument"],
                                                                   body["expiryFlag"], body["expiryCode"],
                                                                   body["securityId"]))
        self.assertEqual(("ATM-3", "PUT", "2025-02-01", "2025-02-28"),
                         (body["strike"], body["drvOptionType"], body["fromDate"], body["toDate"]))
        self.assertEqual("ATM", so.strike_argument(0))
        self.assertEqual("ATM+2", so.strike_argument(2))
        self.assertEqual(("2024-12-01", "2024-12-31"), tuple(d.isoformat() for d in so.month_bounds("2024-12")))

    def test_parse_keeps_the_month_and_the_session_only(self):
        s = so.Series("RELIANCE", "2885", "2025-03", "CE", 0)
        rows = so.parse(payload(stamps=("2025-02-28 15:25", "2025-03-03 09:10", "2025-03-03 09:15", "2025-03-31 15:25",
                                        "2025-03-31 15:30")), s)
        self.assertEqual(["2025-03-03 09:15", "2025-03-31 15:25"], [r["bar_start_ist"] for r in rows])
        self.assertEqual((1200.0, 1199.6, 22.0), (rows[0]["strike"], rows[0]["spot"], rows[0]["iv"]))
        # A CALL request answers "ce" with "pe" null: asking the PE block of it finds nothing.
        self.assertEqual([], so.parse(payload(side="ce"), so.Series("RELIANCE", "2885", "2025-03", "PE", 0)))

    def test_plan_is_month_by_month_and_has_no_duplicates(self):
        series = so.plan([("B", "2", "2025-02"), ("A", "1", "2025-03"), ("B", "2", "2025-02")], offsets=(0, 1))
        self.assertEqual(8, len(series))
        self.assertEqual(["2025-02"] * 4 + ["2025-03"] * 4, [s.month for s in series])


class PosterTests(unittest.TestCase):
    def test_the_poster_asks_the_rolling_option_endpoint_not_the_stock_bars_one(self):
        # Found on the first trial: posting these bodies to charts/intraday answers 200 with empty
        # arrays, which read as "no bars" and marked every series finished.
        from unittest import mock
        import requests
        seen = []

        class Response:
            status_code = 200

            def json(self):
                return {}

        def fake_post(self, url, json=None, timeout=None):
            seen.append(url)
            return Response()

        with mock.patch.dict(os.environ, {"DHAN_CLIENT_ID": "1", "DHAN_ACCESS_TOKEN": "t"}), \
                mock.patch.object(requests.Session, "post", fake_post):
            so.dhan_poster()({"x": 1})
        self.assertEqual([so.URL], seen)
        self.assertTrue(so.URL.endswith("/charts/rollingoption"))


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.calls = []

    def post(self, body):
        self.calls.append(body)
        side = "ce" if body["drvOptionType"] == "CALL" else "pe"
        return 200, payload(side=side)

    def test_finished_series_are_not_fetched_again_but_a_new_id_is(self):
        pacer = Pacer(spacing=0, sleep=lambda _: None)
        series = so.plan([("RELIANCE", "2885", "2025-03")], offsets=(0,))
        counts = so.download(self.root, series, self.post, pacer, log=lambda _: None)
        self.assertEqual(2, counts["ok"])
        self.assertTrue(os.path.exists(so.series_path(self.root, series[0])))
        self.calls.clear()
        so.download(self.root, series, self.post, pacer, log=lambda _: None)
        self.assertEqual([], self.calls)
        so.download(self.root, so.plan([("RELIANCE", "9999", "2025-03")], offsets=(0,)), self.post, pacer,
                    log=lambda _: None)
        self.assertEqual(2, len(self.calls))

    def test_an_expired_token_stops_the_run(self):
        series = so.plan([("RELIANCE", "2885", "2025-03")], offsets=(0,))
        with self.assertRaises(AuthError):
            so.download(self.root, series, lambda body: (401, {"errorCode": "DH-901"}),
                        Pacer(spacing=0, sleep=lambda _: None), log=lambda _: None)

    def test_the_time_limit_stops_before_the_next_request(self):
        series = so.plan([("RELIANCE", "2885", "2025-03")], offsets=(0, 1))
        counts = so.download(self.root, series, self.post, Pacer(spacing=0, sleep=lambda _: None),
                             stop_at=lambda: len(self.calls) >= 1, log=lambda _: None)
        self.assertEqual(1, len(self.calls))
        self.assertEqual(1, counts.get("stopped"))


if __name__ == "__main__":
    unittest.main()
