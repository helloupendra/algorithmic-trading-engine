"""
Version 2 end to end on synthetic data: the walk-forward comparison with v1,
the registration rule, the CLI's backtest-v2, and issuing and scoring both
versions side by side.
"""

import _bootstrap  # noqa: F401

import io
import json
import math
import tempfile
import unittest
from contextlib import redirect_stdout
from datetime import date, datetime, timedelta
from pathlib import Path
from unittest import mock

import numpy as np

import _analysis_fakes as fakes
from analysis import __main__ as cli
from analysis import backtest
from analysis.api import Answer, ApiError
from analysis.context import COLUMNS, Context
from analysis.data import BY_NAME, ExpiryCalendar, MarketData, SessionBar, SessionSeries, VIX_SYMBOL
from analysis.issue import issue_session
from analysis.models import MODEL_VERSION, MODEL_VERSION_V2, MODELS_V2
from analysis.score import score_forecasts
from backtest.timeutil import IST

V1 = list(cli.ALL_MODELS)
V2 = list(cli.V2_MODELS)


def at(day: date, hh: int, mm: int = 0) -> datetime:
    return datetime(day.year, day.month, day.day, hh, mm, tzinfo=IST)


def planted(names=("NIFTY",), n=700, start=date(2023, 6, 1), strength=0.45, seed=9):
    """
    A market whose session range grows with the size of the previous US
    session's move (a real, point-in-time effect v1 cannot see), and the
    context that carries that move. strength=0 plants nothing.
    """
    days = fakes.weekdays(start, n)
    rng = np.random.default_rng(seed)
    us_days = fakes.weekdays(start - timedelta(days=90), n + 80)
    spx = {d: float(rng.normal(0, 1.0)) for d in us_days}
    prev_weekday = {d: max(u for u in us_days if u < d) for d in days}
    series = {}
    for k, name in enumerate(names):
        r = np.random.default_rng(seed + 10 * k)
        out, close, log_range = [], 20000.0, 0.0
        for day in days:
            log_range = 0.6 * log_range + r.normal(0, 0.2)
            span = close * math.exp(log_range + strength * abs(spx[prev_weekday[day]]) - 0.3) / 100.0
            open_ = close * (1 + r.normal(0, 0.002))
            new_close = open_ + r.uniform(-0.45, 0.45) * span
            out.append(SessionBar(day, open_, max(open_, new_close) + r.uniform(0, 0.3) * span,
                                  min(open_, new_close) - r.uniform(0, 0.3) * span, new_close, 75, "candles 5m"))
            close = new_close
        series[name] = SessionSeries(BY_NAME[name].symbol, out)
    far = date(2099, 1, 1)
    calendar = ExpiryCalendar({name: fakes.tuesdays(days) for name in names}, {"NSE": far, "BSE": far}, {}, {})
    market = MarketData(series, SessionSeries(VIX_SYMBOL, fakes.vix(days)), calendar, {})
    return market, fakes.context_for(days, spx_returns=spx)


class ComparisonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.market, cls.context = planted()
        cls.records = backtest.run(cls.market, V1 + V2, ["NIFTY"], cls.context)
        cls.comparisons = {k: backtest.compare(cls.records[MODELS_V2[k].compares_with], cls.records[k]) for k in V2}

    def test_both_versions_are_scored_on_the_same_sessions(self):
        days = {k: [(r.underlying, r.day) for r in rs] for k, rs in self.records.items()}
        self.assertTrue(days["range.har"])
        self.assertTrue(all(d == days["range.har"] for d in days.values()))

    def test_a_real_effect_v1_cannot_see_is_found_and_passes_the_rule(self):
        c = self.comparisons["range.har-vix-cues"]
        v = c["validation"]
        self.assertGreater(v["diffCiLow"], 0)
        self.assertGreater(v["skillVsV1"], 0.05)
        self.assertTrue(backtest.beats_v1(c))
        self.assertAlmostEqual(v["skillVsV1"], 1 - v["v2Loss"] / v["v1Loss"], places=3)

    def test_noise_does_not_pass(self):
        # Nothing was planted for trend or direction: fifteen noise inputs must not clear the bar.
        for key in ("trend.logit-cues", "direction.logit-cues"):
            self.assertFalse(backtest.beats_v1(self.comparisons[key]), key)

    def test_the_same_market_without_the_effect_registers_no_range_model(self):
        market, context = planted(strength=0.0)
        records = backtest.run(market, ["range.har-vix", "range.har-vix-cues"], ["NIFTY"], context)
        self.assertFalse(backtest.beats_v1(backtest.compare(records["range.har-vix"], records["range.har-vix-cues"])))

    def test_the_rule_reads_validation_only(self):
        good = {"design": None, "validation": {"diffCiLow": 0.001}, "holdout": {"diffCiLow": -1.0}}
        self.assertTrue(backtest.beats_v1(good))
        self.assertFalse(backtest.beats_v1({"validation": {"diffCiLow": 0.0}, "holdout": {"diffCiLow": 9.0}}))
        self.assertFalse(backtest.beats_v1({"validation": None, "holdout": {"diffCiLow": 9.0}}))

    def test_the_registration_body_carries_the_comparison_and_the_second_look(self):
        spec = MODELS_V2["range.har-vix-cues"]
        summary = backtest.summarize(spec, self.records[spec.key])
        body = backtest.registration_v2(spec, summary, self.comparisons[spec.key])
        self.assertEqual((body["key"], body["version"], body["target"]), (spec.key, MODEL_VERSION_V2, "range"))
        self.assertEqual(body["backtest"]["configurationsTried"], 3)
        vs = body["backtest"]["versusV1"]
        self.assertEqual((vs["model"], vs["version"], vs["chosenOn"]), ("range.har-vix", MODEL_VERSION, "validation"))
        self.assertIn("second look", vs["holdout"])
        self.assertIn("second look", body["backtest"]["notes"])
        self.assertLess(len(json.dumps(body)), 64 * 1024)               # the API's cap on one JSON field

    def test_v1_summaries_keep_v1_counts(self):
        s = backtest.summarize(backtest.SPECS["trend.logit"], self.records["trend.logit"])
        self.assertEqual(s["configurationsTried"], 1)
        self.assertEqual(backtest.summarize(backtest.SPECS["trend.logit-cues"],
                                            self.records["trend.logit-cues"])["configurationsTried"], 2)


class BacktestV2CommandTests(unittest.TestCase):
    def run_main(self, market, context, api, now=at(date(2026, 3, 2), 18)):
        out = io.StringIO()
        conn = mock.Mock()
        with mock.patch.object(cli, "_connect", return_value=conn), \
                mock.patch("analysis.data.load_market", return_value=market), \
                mock.patch("analysis.context.load", return_value=context), \
                mock.patch.object(cli, "_api", return_value=api), \
                mock.patch.object(cli, "now_ist", return_value=now), \
                tempfile.TemporaryDirectory() as tmp, redirect_stdout(out):
            code = cli.main(["backtest-v2", "--report-dir", tmp])
            reports = {p.name: p.read_text() for p in Path(tmp).glob("*.md")}
        conn.close.assert_called_once()
        return code, reports

    def test_registers_only_what_beats_v1_and_reports_everything(self):
        market, context = planted(names=cli.ALL_UNDERLYINGS)
        api = mock.Mock()
        api.register_model.return_value = Answer(200, {}, "")
        code, reports = self.run_main(market, context, api)
        self.assertEqual(code, 0)
        registered = [c.args[0]["key"] for c in api.register_model.call_args_list]
        self.assertEqual(registered, ["range.har-vix-cues"])
        report = reports["backtest-v2-2026-03-02.md"]
        self.assertIn("holdout (second look)", report)
        self.assertIn("**range.har-vix-cues: registered.**", report)
        self.assertIn("version 2 added nothing here", report)
        self.assertIn("| spxRet |", report)                             # the context's coverage is shown

    def test_a_context_that_does_not_reach_validation_registers_nothing_and_fails(self):
        market, _ = planted(names=cli.ALL_UNDERLYINGS)
        api = mock.Mock()
        code, reports = self.run_main(market, Context(), api)
        self.assertEqual(code, 1)
        api.register_model.assert_not_called()
        self.assertIn("forecast no validation session", reports["backtest-v2-2026-03-02.md"])

    def test_a_dry_run_registers_nothing(self):
        market, context = planted(names=cli.ALL_UNDERLYINGS)
        api = mock.Mock()
        with mock.patch.object(cli, "_connect", return_value=mock.Mock()), \
                mock.patch("analysis.data.load_market", return_value=market), \
                mock.patch("analysis.context.load", return_value=context), \
                mock.patch.object(cli, "_api", return_value=api), \
                mock.patch.object(cli, "now_ist", return_value=at(date(2026, 3, 2), 18)), \
                tempfile.TemporaryDirectory() as tmp, redirect_stdout(io.StringIO()):
            self.assertEqual(cli.main(["backtest-v2", "--dry-run", "--report-dir", tmp]), 0)
        api.register_model.assert_not_called()


class IssueAndScoreBothVersionsTests(unittest.TestCase):
    def setUp(self):
        self.market = fakes.market(names=("NIFTY",), n=400)
        days = [s.day for s in self.market.series["NIFTY"].sessions]
        self.last = days[-1]
        self.session = self.last + timedelta(days=1 if self.last.weekday() < 4 else 3)
        self.context = fakes.context_for(days + [self.session], tail=0)
        self.sent = []

    def post(self, payload):
        self.sent.append(payload)
        return Answer(201, {"id": len(self.sent)}, "")

    def test_both_versions_are_issued_with_their_own_version_and_inputs(self):
        live = {"usedByModels": False, "giftNiftyGapPct": 0.4}
        result = issue_session(self.session, self.market, V1 + V2, ["NIFTY"], self.post, self.context, live)
        self.assertEqual((result.issued, result.errors), (7, []))
        versions = {p["modelKey"]: p["modelVersion"] for p in self.sent}
        self.assertEqual({k: versions[k] for k in V1}, {k: MODEL_VERSION for k in V1})
        self.assertEqual({k: versions[k] for k in V2}, {k: MODEL_VERSION_V2 for k in V2})
        for p in self.sent:
            self.assertEqual(p["inputs"]["liveOnly"], live)             # recorded on every forecast, v1's too
            json.dumps(p)
        v2 = next(p for p in self.sent if p["modelKey"] == "trend.logit-cues")
        self.assertTrue(set(COLUMNS) <= set(v2["inputs"]))
        v1 = next(p for p in self.sent if p["modelKey"] == "trend.logit")
        self.assertFalse(set(COLUMNS) & set(v1["inputs"]))

    def test_a_stale_context_stops_v2_and_only_v2(self):
        c = self.context
        stale = {s: [r for r in rows if r.day < self.last - timedelta(days=10)] for s, rows in c.global_rows.items()}
        context = Context(stale, c.fii, c.breadth, c.heavy, c.events, c.holidays)
        result = issue_session(self.session, self.market, V1 + V2, ["NIFTY"], self.post, context, None)
        self.assertEqual(sorted(p["modelKey"] for p in self.sent), sorted(V1))
        self.assertEqual((len(result.errors), result.exit_code), (3, 1))
        self.assertIn("market_global_daily", result.errors[0])

    def test_without_the_context_v2_is_an_error_not_a_crash(self):
        result = issue_session(self.session, self.market, V1 + V2, ["NIFTY"], self.post, None, None)
        self.assertEqual(len(self.sent), 4)
        self.assertIn("version 2 inputs were not loaded", result.errors[0])

    def test_scoring_does_not_care_which_version_issued(self):
        sessions = self.market.series["NIFTY"].sessions
        day = sessions[-1].day
        market = MarketData({"NIFTY": SessionSeries("NSE:NIFTY50-INDEX", sessions[:-1])}, self.market.vix,
                            self.market.calendar, {})
        morning = issue_session(day, market, V1 + V2, ["NIFTY"], None, self.context, None)
        self.assertEqual(morning.errors, [])
        forecasts = [{"id": k, **p, "outcome": None, "scores": None} for k, p in enumerate(morning.payloads, start=1)]
        result = score_forecasts(forecasts, self.market, at(day, 15, 50), None)
        self.assertEqual((result.scored, result.errors), (7, []))


class IssueCommandTests(unittest.TestCase):
    def setUp(self):
        self.market = fakes.market(names=("NIFTY",), n=400)
        days = [s.day for s in self.market.series["NIFTY"].sessions]
        last = days[-1]
        self.session = last + timedelta(days=1 if last.weekday() < 4 else 3)
        self.context = fakes.context_for(days + [self.session], tail=0)

    def run_issue(self, api, *extra):
        out = io.StringIO()
        with mock.patch.object(cli, "_connect", return_value=mock.Mock()), \
                mock.patch("analysis.data.load_market", return_value=self.market), \
                mock.patch("analysis.context.load", return_value=self.context), \
                mock.patch.object(cli, "_live_only", return_value={"usedByModels": False}), \
                mock.patch.object(cli, "_api", return_value=api), \
                mock.patch.object(cli, "now_ist", return_value=at(self.session, 8, 50)), \
                redirect_stdout(out):
            code = cli.main(["issue", "--session", self.session.isoformat(), "--underlyings", "NIFTY", *extra])
        return code, out.getvalue()

    def api(self, models):
        api = mock.Mock()
        api.models.return_value = models
        api.issue.return_value = Answer(201, {"id": 1}, "")
        return api

    def test_the_morning_issues_v1_and_the_v2_models_registered_at_this_version(self):
        api = self.api([{"key": "range.har-vix-cues", "version": MODEL_VERSION_V2},
                        {"key": "trend.logit-cues", "version": "2020-01-01.1"},
                        {"key": "range.har", "version": MODEL_VERSION}])
        code, _ = self.run_issue(api)
        self.assertEqual(code, 0)
        keys = [c.args[0]["modelKey"] for c in api.issue.call_args_list]
        self.assertEqual(sorted(keys), sorted(V1 + ["range.har-vix-cues"]))

    def test_an_unreadable_registry_still_issues_v1_and_fails_the_run(self):
        api = self.api([])
        api.models.side_effect = ApiError("GET /api/Forecasts/models: HTTP 502")
        code, _ = self.run_issue(api)
        self.assertEqual(code, 1)
        self.assertEqual(sorted(c.args[0]["modelKey"] for c in api.issue.call_args_list), sorted(V1))

    def test_a_dry_run_is_v1_unless_v2_is_asked_for(self):
        code, out = self.run_issue(None, "--dry-run")
        self.assertEqual((code, len([ln for ln in out.splitlines() if ln.startswith("{")])), (0, 4))
        code, out = self.run_issue(None, "--dry-run", "--models", "trend.logit-cues")
        payloads = [json.loads(ln) for ln in out.splitlines() if ln.startswith("{")]
        self.assertEqual([p["modelKey"] for p in payloads], ["trend.logit-cues"])

    def test_registered_v2_filters_by_key_and_version(self):
        self.assertEqual(cli.registered_v2([{"key": "direction.logit-cues", "version": MODEL_VERSION_V2},
                                            {"key": "range.har-vix-cues", "version": MODEL_VERSION}, "junk"]),
                         ["direction.logit-cues"])


if __name__ == "__main__":
    unittest.main()
