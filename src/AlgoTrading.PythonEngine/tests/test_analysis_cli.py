import _bootstrap  # noqa: F401

import io
import json
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from datetime import date, datetime, timedelta
from pathlib import Path
from unittest import mock

import _analysis_fakes as fakes
from analysis import __main__ as cli
from analysis import backtest
from analysis.api import Answer, ApiError, ForecastApi
from analysis.data import SessionSeries
from analysis.issue import issue_session
from analysis.models import MODEL_VERSION, MODELS, build_table, forecast, forecast_payload
from analysis.score import score_forecasts, unscored
from backtest.timeutil import IST

ALL = list(cli.ALL_MODELS)


def at(day: date, hh: int, mm: int = 0) -> datetime:
    return datetime(day.year, day.month, day.day, hh, mm, tzinfo=IST)


class ArgumentTests(unittest.TestCase):
    def parse(self, *argv):
        return cli.parser().parse_args(list(argv))

    def fails(self, *argv):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            self.parse(*argv)
        self.assertEqual(caught.exception.code, 2)

    def test_defaults(self):
        b = self.parse("backtest")
        self.assertEqual((b.models, b.underlyings, b.dry_run), (ALL, ["NIFTY", "BANKNIFTY", "SENSEX"], False))
        i = self.parse("issue", "--session", "2026-09-28")
        self.assertEqual(i.session, date(2026, 9, 28))
        s = self.parse("score")
        self.assertIsNone(s.from_day)

    def test_bad_arguments_exit_2(self):
        self.fails("issue")                                   # --session is required
        self.fails("issue", "--session", "28-09-2026")
        self.fails("backtest", "--models", "range.garch")
        self.fails("backtest", "--underlyings", "FINNIFTY")
        self.fails("launch")

    def test_lists_keep_the_canonical_order(self):
        self.assertEqual(self.parse("backtest", "--models", "trend.logit,range.har").models,
                         ["range.har", "trend.logit"])


class IssueTests(unittest.TestCase):
    def setUp(self):
        self.market = fakes.market(names=("NIFTY", "SENSEX"), n=400)
        self.last = self.market.series["NIFTY"].last
        self.session = self.last + timedelta(days=1 if self.last.weekday() < 4 else 3)
        self.sent = []

    def post(self, status=201, message=""):
        def send(payload):
            self.sent.append(payload)
            return Answer(status, {"id": len(self.sent), "issuedUtc": "2026-09-28T03:20:04Z"}, message)
        return send

    def test_every_model_and_underlying_is_posted(self):
        result = issue_session(self.session, self.market, ALL, ["NIFTY", "SENSEX"], self.post())
        self.assertEqual((result.issued, result.errors, result.exit_code), (8, [], 0))
        self.assertEqual({(p["modelKey"], p["underlying"]) for p in self.sent},
                         {(k, u) for k in ALL for u in ("NIFTY", "SENSEX")})
        self.assertTrue(all(p["sessionDate"] == self.session.isoformat() and p["modelVersion"] == MODEL_VERSION
                            for p in self.sent))

    def test_409_is_logged_not_failed(self):
        result = issue_session(self.session, self.market, ALL, ["NIFTY"], self.post(409, "session has opened"))
        self.assertEqual((result.issued, result.refused, result.exit_code), (0, 4, 0))

    def test_an_unregistered_model_is_an_error(self):
        result = issue_session(self.session, self.market, ["range.har"], ["NIFTY"], self.post(400, "not registered"))
        self.assertEqual(result.exit_code, 1)
        self.assertIn("not registered", result.errors[0])

    def test_a_holiday_is_skipped(self):
        self.market.holidays = {"NSE": frozenset({self.session})}
        result = issue_session(self.session, self.market, ALL, ["NIFTY", "SENSEX"], self.post())
        self.assertEqual(len(result.skipped), 1)                      # NIFTY's exchange is shut, SENSEX's is not
        self.assertEqual({p["underlying"] for p in self.sent}, {"SENSEX"})

    def test_a_weekend_is_skipped(self):
        saturday = self.last + timedelta(days=(5 - self.last.weekday()) % 7 or 7)
        result = issue_session(saturday, self.market, ALL, ["NIFTY"], self.post())
        self.assertEqual((result.issued, len(result.skipped), self.sent), (0, 1, []))

    def test_stale_history_issues_nothing_and_says_so(self):
        series = self.market.series["NIFTY"]
        self.market.series["NIFTY"] = SessionSeries(series.symbol, series.sessions[:-1],
                                                    [(self.last, "12 of 75 bars (candles 5m)")])
        result = issue_session(self.session, self.market, ALL, ["NIFTY"], self.post())
        self.assertEqual((self.sent, result.exit_code), ([], 1))
        self.assertIn("12 of 75 bars", result.errors[0])

    def test_missing_vix_stops_only_the_models_that_read_it(self):
        self.market = fakes.market(n=400, drop_vix=[self.last])
        result = issue_session(self.session, self.market, ALL, ["NIFTY"], self.post())
        self.assertEqual([p["modelKey"] for p in self.sent], ["range.har"])
        self.assertEqual((len(result.errors), result.exit_code), (3, 1))
        self.assertIn("India VIX", result.errors[0])

    def test_a_dry_run_sends_nothing(self):
        result = issue_session(self.session, self.market, ALL, ["NIFTY"], None)
        self.assertEqual((result.issued, len(result.payloads), self.sent), (4, 4, []))


class ScoreTests(unittest.TestCase):
    def setUp(self):
        self.market = fakes.market(n=400)
        self.sessions = self.market.series["NIFTY"].sessions
        self.day = self.sessions[-1].day
        # the forecasts as the API hands them back: issued that morning, not scored yet
        morning = build_table("NIFTY", self.sessions[:-1], self.market.vix.closes(), self.market.is_expiry("NIFTY"),
                              pending=self.day)
        self.forecasts = []
        for n, key in enumerate(ALL, start=1):
            fc = forecast(MODELS[key], morning, len(morning.days) - 1, self.day)
            body = forecast_payload(MODELS[key], "NIFTY", self.day, fc)
            self.forecasts.append({"id": n, **body, "issuedUtc": "2026-09-28T03:20:04Z", "outcome": None,
                                   "scores": None, "scoredUtc": None})
        self.sent = []

    def post(self, status=200):
        def send(forecast_id, payload):
            self.sent.append((forecast_id, payload))
            return Answer(status, {}, "already scored" if status == 409 else "")
        return send

    def test_only_closed_unscored_sessions_are_picked(self):
        scored = dict(self.forecasts[0], outcome={"range": 1.0}, scores={"loss": 0.1})
        today = dict(self.forecasts[1], sessionDate=(self.day + timedelta(days=1)).isoformat())
        pending = unscored([scored, today, self.forecasts[2]], at(self.day + timedelta(days=1), 15, 29))
        self.assertEqual([f["id"] for f in pending], [3])
        after_close = unscored([today], at(self.day + timedelta(days=1), 15, 30))
        self.assertEqual(len(after_close), 1)

    def test_scores_are_posted_in_the_contract_shape(self):
        result = score_forecasts(self.forecasts, self.market, at(self.day, 15, 50), self.post())
        self.assertEqual((result.scored, result.errors), (4, []))
        for forecast_id, payload in self.sent:
            self.assertEqual(set(payload), {"outcome", "scores"})
            self.assertEqual(set(payload["outcome"]), {"open", "high", "low", "close", "range", "bucket",
                                                       "trendDay", "efficiency", "up"})
            self.assertEqual(set(payload["scores"]), {"loss", "baselineLoss", "metrics", "calibration"})
            json.dumps(payload)
        by_id = dict(self.sent)
        bar = self.sessions[-1]
        self.assertEqual(by_id[1]["outcome"]["close"], round(bar.close, 2))
        self.assertEqual(len(by_id[1]["scores"]["calibration"]), 3)       # range: three buckets
        self.assertIn(by_id[1]["outcome"]["bucket"], ("quiet", "normal", "wild"))
        self.assertEqual(len(by_id[3]["scores"]["calibration"]), 1)       # trend: one probability
        self.assertIsNone(by_id[3]["outcome"]["bucket"])

    def test_a_session_without_bars_waits_then_fails(self):
        series = self.market.series["NIFTY"]
        self.market.series["NIFTY"] = SessionSeries(series.symbol, series.sessions[:-1],
                                                    [(self.day, "last bar at 12:01 (live_bars 1m)")])
        result = score_forecasts(self.forecasts, self.market, at(self.day, 15, 50), self.post())
        self.assertEqual((result.scored, len(result.waiting), result.exit_code), (0, 4, 0))
        self.assertIn("12:01", result.waiting[0])
        later = score_forecasts(self.forecasts, self.market, at(self.day + timedelta(days=6), 15, 50), self.post())
        self.assertEqual((len(later.errors), later.exit_code), (4, 1))

    def test_409_is_logged_not_failed(self):
        result = score_forecasts(self.forecasts, self.market, at(self.day, 15, 50), self.post(409))
        self.assertEqual((result.scored, result.refused, result.exit_code), (0, 4, 0))


class ApiTests(unittest.TestCase):
    def test_endpoints_and_bodies(self):
        http = fakes.FakeHttp()
        api = ForecastApi(base_url="http://api.test/", http=http, verify_ssl=False)
        self.assertTrue(api.register_model({"key": "range.har"}).ok)
        api.issue({"modelKey": "range.har"})
        api.post_outcome(7, {"outcome": {}, "scores": {}})
        self.assertEqual([(c[0], c[1]) for c in http.calls], [
            ("POST", "http://api.test/api/Forecasts/models"),
            ("POST", "http://api.test/api/Forecasts"),
            ("POST", "http://api.test/api/Forecasts/7/outcome"),
        ])
        self.assertEqual(http.calls[1][2], {"modelKey": "range.har"})

    def test_a_refusal_is_an_answer_with_the_apis_message(self):
        http = fakes.FakeHttp([fakes.FakeResponse(409, {"message": "session has opened"})])
        answer = ForecastApi(base_url="http://api.test", http=http).issue({})
        self.assertTrue(answer.conflict)
        self.assertEqual(answer.message, "session has opened")

    def test_list_passes_the_window_and_fails_loudly(self):
        http = fakes.FakeHttp([fakes.FakeResponse(200, [{"id": 1}]), fakes.FakeResponse(403, text="")])
        api = ForecastApi(base_url="http://api.test", http=http)
        self.assertEqual(api.forecasts(date(2026, 9, 1), date(2026, 9, 28)), [{"id": 1}])
        self.assertEqual(http.calls[0][2], {"from": "2026-09-01", "to": "2026-09-28"})
        with self.assertRaises(ApiError):
            api.forecasts(date(2026, 9, 1), date(2026, 9, 28))

    def test_an_unreachable_api_is_an_api_error(self):
        class Down:
            def post(self, *a, **k):
                raise ConnectionError("refused")
        with self.assertRaises(ApiError):
            ForecastApi(base_url="http://api.test", http=Down()).issue({})


class MainTests(unittest.TestCase):
    """The CLI end to end, with the database and the API replaced."""

    def run_main(self, argv, market, api, now):
        out = io.StringIO()
        conn = mock.Mock()
        with mock.patch.object(cli, "_connect", return_value=conn), \
                mock.patch("analysis.data.load_market", return_value=market), \
                mock.patch.object(cli, "_api", return_value=api), \
                mock.patch.object(cli, "now_ist", return_value=now), \
                redirect_stdout(out):
            code = cli.main(argv)
        conn.close.assert_called_once()
        return code, out.getvalue()

    def test_issue_dry_run_prints_payloads(self):
        market = fakes.market(n=400)
        last = market.series["NIFTY"].last
        session = last + timedelta(days=1 if last.weekday() < 4 else 3)
        api = mock.Mock()
        code, out = self.run_main(["issue", "--session", session.isoformat(), "--dry-run", "--underlyings", "NIFTY"],
                                  market, api, at(session, 8, 50))
        self.assertEqual(code, 0)
        payloads = [json.loads(line) for line in out.splitlines() if line.startswith("{")]
        self.assertEqual(len(payloads), 4)
        api.issue.assert_not_called()

    def test_backtest_writes_a_report_and_registers_every_model(self):
        # 2023-06 to 2026-02: a design period after the first 250 training sessions, 2025, and a holdout
        market = fakes.market(names=("NIFTY", "BANKNIFTY", "SENSEX"), n=700, start=date(2023, 6, 1))
        api = mock.Mock()
        api.register_model.return_value = Answer(200, {}, "")
        with tempfile.TemporaryDirectory() as tmp:
            code, _ = self.run_main(["backtest", "--report-dir", tmp], market, api, at(date(2026, 3, 2), 18))
            self.assertEqual(code, 0)
            report = next(Path(tmp).glob("backtest-*.md")).read_text()
        self.assertIn("range.har-vix", report)
        self.assertEqual(api.register_model.call_count, 4)
        body = api.register_model.call_args_list[1].args[0]
        self.assertEqual((body["key"], body["version"], body["target"]), ("range.har-vix", MODEL_VERSION, "range"))
        bt = body["backtest"]
        for split in ("design", "validation", "holdout"):
            self.assertTrue({"from", "to", "n", "loss", "baselineLoss", "skill"} <= set(bt[split]))
        self.assertEqual(set(bt["byUnderlying"]), {"NIFTY", "BANKNIFTY", "SENSEX"})
        self.assertEqual(bt["configurationsTried"], 2)
        self.assertIsInstance(bt["notes"], str)
        json.dumps(body)

    def test_a_narrowed_backtest_registers_nothing(self):
        market = fakes.market(names=("NIFTY",), n=420, start=date(2024, 6, 3))
        api = mock.Mock()
        with tempfile.TemporaryDirectory() as tmp:
            code, _ = self.run_main(["backtest", "--underlyings", "NIFTY", "--report-dir", tmp], market, api,
                                    at(date(2026, 1, 20), 18))
        self.assertEqual(code, 0)
        api.register_model.assert_not_called()

    def test_score_with_nothing_pending_does_not_touch_the_database(self):
        api = mock.Mock()
        api.forecasts.return_value = []
        with mock.patch.object(cli, "_connect") as connect, mock.patch.object(cli, "_api", return_value=api), \
                mock.patch.object(cli, "now_ist", return_value=at(date(2026, 9, 28), 15, 50)), \
                redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(["score"]), 0)
        connect.assert_not_called()
        self.assertEqual(api.forecasts.call_args.args, (date(2026, 8, 29), date(2026, 9, 28)))


class BacktestRunTests(unittest.TestCase):
    def test_models_are_compared_on_the_same_sessions(self):
        # VIX starts later than the index: range.har could forecast earlier, but is scored with the rest.
        market = fakes.market(n=520, start=date(2023, 1, 2))
        late = market.vix.sessions[60].day
        market.vix = SessionSeries(market.vix.symbol, [s for s in market.vix.sessions if s.day >= late])
        records = backtest.run(market, ALL, ["NIFTY"])
        days = {k: [r.day for r in rs] for k, rs in records.items()}
        self.assertTrue(days["range.har"])
        self.assertTrue(all(d == days["range.har"] for d in days.values()))

    def test_summary_splits_pool_and_score(self):
        market = fakes.market(names=("NIFTY", "SENSEX"), n=520, start=date(2024, 3, 4))
        records = backtest.run(market, ["range.har"], ["NIFTY", "SENSEX"])
        summary = backtest.summarize(MODELS["range.har"], records["range.har"])
        # 250 training sessions from March 2024 end in 2025: the design period has no forecasts, and says so
        self.assertIsNone(summary["design"])
        n = sum(summary[s]["n"] for s in backtest.SPLITS if summary[s])
        self.assertEqual(n, len(records["range.har"]))
        self.assertEqual(sum(v["n"] for v in summary["byUnderlying"].values()), n)
        v = summary["validation"]
        self.assertEqual(v["from"][:4], "2025")
        self.assertAlmostEqual(v["skill"], 1 - v["loss"] / v["baselineLoss"], places=3)
        self.assertTrue(0 <= v["coverage80"] <= 1)
        self.assertLessEqual(v["diffCiLow"], v["diffCiHigh"])


if __name__ == "__main__":
    unittest.main()
