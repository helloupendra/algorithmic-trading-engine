import _bootstrap  # noqa: F401

import json
import math
import unittest
from dataclasses import replace
from datetime import date

import numpy as np

import _analysis_fakes as fakes
from analysis import backtest
from analysis.data import SessionBar
from analysis.models import (MIN_TRAIN_SESSIONS, MODEL_VERSION, MODELS, ForecastUnavailable, build_table, fit,
                             forecast, forecast_payload, logistic_irls, ols)
from analysis.scoring import BUCKETS

NO_EXPIRY = (lambda _d: False)


def flat_sessions(days, ranges, level=10000.0):
    """Sessions that close where they opened, at `level`, with the given ranges (% of the previous close)."""
    half = [r * level / 200.0 for r in ranges]
    return [SessionBar(d, level, level + h, level - h, level, 75, "candles 5m") for d, h in zip(days, half)]


class TableTests(unittest.TestCase):
    def test_inputs_come_from_earlier_sessions_only(self):
        days = fakes.weekdays(date(2025, 1, 6), 30)
        ranges = [0.5 + 0.1 * k for k in range(30)]
        t = build_table("NIFTY", flat_sessions(days, ranges), {}, NO_EXPIRY)
        self.assertTrue(math.isnan(t.range_pct[0]))                  # no previous close
        self.assertAlmostEqual(t.range_pct[5], ranges[5])
        i = 25
        self.assertAlmostEqual(t.r1[i], ranges[i - 1])
        self.assertAlmostEqual(t.r5[i], np.mean(ranges[i - 5:i]))
        self.assertAlmostEqual(t.r22[i], np.mean(ranges[i - 22:i]))
        self.assertAlmostEqual(t.mean20[i], np.mean(ranges[i - 20:i]))
        self.assertTrue(math.isnan(t.r22[22]))                       # would need session 0's range
        self.assertFalse(math.isnan(t.r22[23]))

    def test_a_pending_row_has_inputs_and_no_outcome(self):
        days = fakes.weekdays(date(2025, 1, 6), 31)
        sessions = flat_sessions(days[:30], [1.0] * 30)
        t = build_table("NIFTY", sessions, {days[29]: 14.5}, NO_EXPIRY, pending=days[30])
        self.assertEqual(t.days[-1], days[30])
        self.assertTrue(math.isnan(t.close[-1]) and math.isnan(t.range_pct[-1]) and math.isnan(t.trend[-1]))
        self.assertAlmostEqual(t.r1[-1], 1.0)
        self.assertEqual(t.prev_close[-1], 10000.0)
        self.assertEqual(t.vix_prev[-1], 14.5)

    def test_a_pending_session_drops_anything_stored_for_it_or_after(self):
        days = fakes.weekdays(date(2025, 1, 6), 31)
        sessions = flat_sessions(days, [1.0] * 31)
        t = build_table("NIFTY", sessions, {}, NO_EXPIRY, pending=days[20])
        self.assertEqual(t.days, days[:21])


class HarTests(unittest.TestCase):
    TRUE = np.array([0.05, 0.35, 0.30, 0.25])
    SIGMA = 0.3

    def har_sessions(self, n=3000, seed=7):
        rng = np.random.default_rng(seed)
        ranges = [1.0] * 22
        for _ in range(n):
            x = np.array([1.0, math.log(ranges[-1]), math.log(np.mean(ranges[-5:])), math.log(np.mean(ranges[-22:]))])
            ranges.append(math.exp(float(x @ self.TRUE) + rng.normal(0, self.SIGMA)))
        days = fakes.weekdays(date(2010, 1, 4), len(ranges))
        return days, flat_sessions(days, ranges)

    def test_ols_recovers_known_coefficients(self):
        rng = np.random.default_rng(3)
        X = rng.normal(size=(5000, 3))
        y = 0.4 + X @ np.array([1.5, -0.7, 0.2]) + rng.normal(0, 0.1, 5000)
        coef, sigma = ols(X, y)
        np.testing.assert_allclose(coef, [0.4, 1.5, -0.7, 0.2], atol=0.01)
        self.assertAlmostEqual(sigma, 0.1, delta=0.005)

    def test_the_har_model_recovers_the_process_that_made_the_ranges(self):
        days, sessions = self.har_sessions()
        t = build_table("NIFTY", sessions, {}, NO_EXPIRY)
        model = fit(MODELS["range.har"], t, date(2099, 1, 1))
        np.testing.assert_allclose(model.coef, self.TRUE, atol=0.06)
        self.assertAlmostEqual(model.sigma, self.SIGMA, delta=0.02)
        # the bucket edges are the training terciles, so each bucket held a third of the sessions
        for share in model.bucket_freq:
            self.assertAlmostEqual(share, 1 / 3, delta=0.01)

    def test_the_range_distribution_is_consistent(self):
        days, sessions = self.har_sessions(n=600)
        t = build_table("NIFTY", sessions, {}, NO_EXPIRY)
        fc = forecast(MODELS["range.har"], t, len(days) - 1, days[-1])
        p = fc.prediction
        self.assertLess(p["low80"], p["median"])
        self.assertLess(p["median"], p["high80"])
        self.assertAlmostEqual(sum(p["buckets"].values()), 1.0, places=3)
        self.assertAlmostEqual(p["points"]["median"], p["median"] * p["prevClose"] / 100, places=1)
        self.assertEqual(fc.baseline["bucketEdges"], p["bucketEdges"])
        self.assertAlmostEqual(fc.baseline["median"], float(np.mean([s.high - s.low for s in sessions[-21:-1]]))
                               / 10000 * 100, places=3)

    def test_too_little_history_is_refused_not_guessed(self):
        days, sessions = self.har_sessions(n=100)
        t = build_table("NIFTY", sessions, {}, NO_EXPIRY)
        with self.assertRaises(ForecastUnavailable):
            fit(MODELS["range.har"], t, days[-1])


class LogitTests(unittest.TestCase):
    def test_irls_recovers_a_known_effect(self):
        rng = np.random.default_rng(11)
        Z = rng.normal(size=(40000, 2))
        p = 1 / (1 + np.exp(-(0.5 + 1.0 * Z[:, 0] - 0.8 * Z[:, 1])))
        y = (rng.uniform(size=40000) < p).astype(float)
        np.testing.assert_allclose(logistic_irls(Z, y, ridge=1.0), [0.5, 1.0, -0.8], atol=0.05)

    def test_irls_stays_finite_on_separable_data(self):
        Z = np.array([[-2.0], [-1.0], [1.0], [2.0]] * 20)
        y = (Z[:, 0] > 0).astype(float)
        w = logistic_irls(Z, y, ridge=1.0)
        self.assertTrue(np.isfinite(w).all())

    def test_direction_logit_finds_a_planted_monday_effect(self):
        days = fakes.weekdays(date(2020, 1, 6), 1500)
        rng = np.random.default_rng(5)
        sessions, level = [], 10000.0
        for d in days[:-1]:
            up = rng.uniform() < (0.85 if d.weekday() == 0 else 0.30)
            close = level * (1.004 if up else 0.996)
            sessions.append(SessionBar(d, level, max(level, close) + 20, min(level, close) - 20, close, 75, "x"))
            level = close
        vix = {s.day: 15.0 + rng.normal() for s in fakes.vix(days)}
        monday = next(d for d in reversed(days) if d.weekday() == 0)
        tuesday = next(d for d in reversed(days) if d.weekday() == 1)
        ps = {}
        for label, day in (("monday", monday), ("tuesday", tuesday)):
            past = [s for s in sessions if s.day < day]
            t = build_table("NIFTY", past, vix, NO_EXPIRY, pending=day)
            ps[label] = forecast(MODELS["direction.logit"], t, len(t.days) - 1, day).prediction["p"]
        self.assertGreater(ps["monday"], 0.7)
        self.assertLess(ps["tuesday"], 0.4)


class LookAheadTests(unittest.TestCase):
    def setUp(self):
        self.market = fakes.market(n=600)
        self.sessions = self.market.series["NIFTY"].sessions
        self.vix = self.market.vix.closes()
        self.i = 520

    def corrupted(self):
        """Every session from i on, and every VIX close from session i on, replaced with nonsense."""
        cut = self.sessions[self.i].day
        bad = [s if s.day < cut else replace(s, open=s.open * 3, high=s.high * 5, low=s.low * 0.2, close=s.close * 2)
               for s in self.sessions]
        vix = {d: (v if d < cut else v * 10) for d, v in self.vix.items()}
        return bad, vix

    def test_no_forecast_moves_when_its_session_and_everything_after_it_change(self):
        day = self.sessions[self.i].day
        bad, bad_vix = self.corrupted()
        for key, spec in MODELS.items():
            before = forecast(spec, build_table("NIFTY", self.sessions, self.vix, NO_EXPIRY), self.i, day)
            after = forecast(spec, build_table("NIFTY", bad, bad_vix, NO_EXPIRY), self.i, day)
            self.assertEqual(before, after, key)

    def test_the_walk_forward_history_up_to_a_day_does_not_move_either(self):
        cut = self.sessions[self.i].day
        bad, bad_vix = self.corrupted()
        for key, spec in MODELS.items():
            good = backtest.walk_forward(spec, build_table("NIFTY", self.sessions, self.vix, NO_EXPIRY))
            worse = backtest.walk_forward(spec, build_table("NIFTY", bad, bad_vix, NO_EXPIRY))
            keep = lambda rs: [(r.day, r.prediction, r.baseline, r.inputs) for r in rs if r.day < cut]  # noqa: E731
            self.assertTrue(keep(good), key)
            self.assertEqual(keep(good), keep(worse), key)

    def test_the_morning_path_and_the_history_path_give_the_same_forecast(self):
        day = self.sessions[self.i].day
        full = build_table("NIFTY", self.sessions, self.vix, NO_EXPIRY)
        morning = build_table("NIFTY", [s for s in self.sessions if s.day < day], self.vix, NO_EXPIRY, pending=day)
        for key, spec in MODELS.items():
            self.assertEqual(forecast(spec, full, self.i, day),
                             forecast(spec, morning, len(morning.days) - 1, day), key)

    def test_a_forecast_may_not_be_fitted_on_its_own_session(self):
        t = build_table("NIFTY", self.sessions, self.vix, NO_EXPIRY)
        with self.assertRaises(ValueError):
            forecast(MODELS["range.har"], t, self.i, self.sessions[self.i + 1].day)

    def test_fits_are_deterministic(self):
        t = build_table("NIFTY", self.sessions, self.vix, NO_EXPIRY)
        for spec in MODELS.values():
            a, b = fit(spec, t, date(2099, 1, 1)), fit(spec, t, date(2099, 1, 1))
            np.testing.assert_array_equal(a.coef, b.coef)


class PayloadShapeTests(unittest.TestCase):
    """The contract's JSON, key for key and type for type."""

    @classmethod
    def setUpClass(cls):
        market = fakes.market(n=400)
        sessions = market.series["NIFTY"].sessions
        day = sessions[-1].day
        cls.day = day
        cls.table = build_table("NIFTY", sessions[:-1], market.vix.closes(), market.is_expiry("NIFTY"), pending=day)

    def payload(self, key):
        spec = MODELS[key]
        fc = forecast(spec, self.table, len(self.table.days) - 1, self.day)
        body = forecast_payload(spec, "NIFTY", self.day, fc)
        json.dumps(body)   # plain JSON types only: no numpy scalars, no dates
        return body

    def test_every_forecast_carries_the_contract_keys(self):
        for key, spec in MODELS.items():
            body = self.payload(key)
            self.assertEqual(set(body), {"modelKey", "modelVersion", "target", "underlying", "sessionDate",
                                         "prediction", "baseline", "inputs"})
            self.assertEqual((body["modelKey"], body["modelVersion"], body["target"], body["underlying"],
                              body["sessionDate"]), (key, MODEL_VERSION, spec.target, "NIFTY", self.day.isoformat()))
            self.assertIsInstance(body["inputs"], dict)
            self.assertEqual(body["inputs"]["prevSession"], self.table.days[-2].isoformat())
            self.assertGreaterEqual(body["inputs"]["trainingSessions"], MIN_TRAIN_SESSIONS)

    def test_range_prediction_and_baseline_shape(self):
        for key in ("range.har", "range.har-vix"):
            body = self.payload(key)
            for part in (body["prediction"], body["baseline"]):
                self.assertEqual(set(part), {"median", "low80", "high80", "prevClose", "points", "buckets",
                                             "bucketEdges"})
                for k in ("median", "low80", "high80", "prevClose"):
                    self.assertIs(type(part[k]), float)
                self.assertEqual(set(part["points"]), {"median", "low80", "high80"})
                self.assertTrue(all(type(v) is float for v in part["points"].values()))
                self.assertEqual(list(part["buckets"]), list(BUCKETS))
                self.assertTrue(all(type(v) is float for v in part["buckets"].values()))
                self.assertEqual(len(part["bucketEdges"]), 2)
                self.assertLess(part["bucketEdges"][0], part["bucketEdges"][1])

    def test_probability_prediction_and_baseline_shape(self):
        for key in ("trend.logit", "direction.logit"):
            body = self.payload(key)
            for part in (body["prediction"], body["baseline"]):
                self.assertEqual(set(part), {"p"})
                self.assertIs(type(part["p"]), float)
                self.assertTrue(0 < part["p"] < 1)

    def test_inputs_are_what_each_model_saw(self):
        self.assertNotIn("vixPrevClose", self.payload("range.har")["inputs"])
        har_vix = self.payload("range.har-vix")["inputs"]
        self.assertIs(type(har_vix["expiryDay"]), bool)
        self.assertIs(type(har_vix["vixPrevClose"]), float)
        logit = self.payload("trend.logit")["inputs"]
        for k in ("rangeRatio", "prevReturn", "prevEfficiency", "vixPrevClose", "vixChange5", "expiryDay", "monday"):
            self.assertIn(k, logit)


if __name__ == "__main__":
    unittest.main()
