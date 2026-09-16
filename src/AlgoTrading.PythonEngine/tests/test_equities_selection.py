"""
R1 selection features: nothing at date d may depend on a later row, a split
must not look like a crash, the universe rules hold, and outcomes belong to
the next session of the same stock.
"""

import unittest

import numpy as np
import pandas as pd

from research.equities import selection as sel


def stock(symbol, closes, start="2025-01-01", volume=1_000_000.0, turnover=2e8, deliv=40.0, series="EQ"):
    dates = pd.bdate_range(start, periods=len(closes))
    rows = []
    prev = closes[0]
    for day, close in zip(dates, closes):
        rows.append(dict(trade_date=day, symbol=symbol, series=series, open=prev, high=max(prev, close) * 1.01,
                         low=min(prev, close) * 0.99, close=close, prev_close=prev, volume=volume,
                         turnover=turnover, delivery_pct=deliv))
        prev = close
    return pd.DataFrame(rows)


def features(frame):
    return sel.add_next_day(sel.add_market_relative(sel.add_features(frame)))


class LookAheadTests(unittest.TestCase):
    def test_changing_future_rows_moves_no_feature_of_an_earlier_date(self):
        rng = np.random.default_rng(7)
        closes = list(100 * np.cumprod(1 + rng.normal(0, 0.02, 300)))
        base = features(pd.concat([stock("AAA", closes), stock("BBB", closes[::-1])]))
        changed = closes[:250] + [c * 3 for c in closes[250:]]
        moved = features(pd.concat([stock("AAA", changed), stock("BBB", closes[::-1])]))
        cols = ["px", "median_turnover", "atr_pct", "ema20", "ema50", "trend_up", "dist_high", "ret20", "ret60",
                "vol_exp", "deliv_ratio", "nr7", "in_universe"]
        cutoff = pd.bdate_range("2025-01-01", periods=300)[248]  # the last date whose NEXT row is unchanged
        a = base[(base["symbol"] == "AAA") & (base["trade_date"] <= cutoff)].reset_index(drop=True)
        b = moved[(moved["symbol"] == "AAA") & (moved["trade_date"] <= cutoff)].reset_index(drop=True)
        pd.testing.assert_frame_equal(a[cols], b[cols])
        # rs20 is relative to the universe median ON THE SAME DATE, so BBB's
        # unchanged history keeps it unchanged too.
        pd.testing.assert_series_equal(a["rs20"], b["rs20"])


class SplitTests(unittest.TestCase):
    def test_a_two_for_one_split_is_not_a_minus_fifty_percent_day(self):
        frame = stock("SPLT", [100.0] * 30 + [50.0] * 30)
        # On the ex-date the exchange's previous close is adjusted: 100 -> 50.
        frame.loc[30, "prev_close"] = 50.0
        frame.loc[30, "open"] = 50.0
        out = sel.add_features(frame)
        self.assertAlmostEqual(0.0, out.loc[30, "ret"])
        self.assertAlmostEqual(1.0, out["px"].iloc[-1])


class UniverseTests(unittest.TestCase):
    def test_rules(self):
        closes = [100.0] * 80
        ok = sel.add_features(stock("OK", closes))
        self.assertFalse(ok.loc[58, "in_universe"])   # 59 sessions
        self.assertTrue(ok.loc[59, "in_universe"])    # 60 sessions, turnover Rs 20 crore
        thin = sel.add_features(stock("THIN", closes, turnover=5e7))
        self.assertFalse(thin["in_universe"].any())
        cheap = sel.add_features(stock("CHEAP", [30.0] * 80))
        self.assertFalse(cheap["in_universe"].any())
        be = sel.add_features(stock("BESER", closes, series="BE"))
        self.assertFalse(be["in_universe"].any())


class OutcomeTests(unittest.TestCase):
    def test_outcomes_are_the_next_session_of_the_same_stock(self):
        frame = pd.concat([stock("AAA", [100.0, 110.0, 99.0]), stock("BBB", [50.0, 50.0, 50.0])])
        out = sel.add_next_day(sel.add_features(frame))
        first = out[(out["symbol"] == "AAA")].iloc[0]
        # Next session: open 100 (previous close), high 110*1.01, low 100*0.99, close 110.
        self.assertAlmostEqual((111.1 - 99.0) / 100.0 * 100, first["n_range"], places=6)
        self.assertAlmostEqual(10.0, first["n_move"], places=6)
        self.assertTrue(np.isnan(out[out["symbol"] == "AAA"].iloc[-1]["n_range"]))  # no next row

    def test_pick_top_takes_universe_stocks_only(self):
        frame = pd.DataFrame({"trade_date": pd.to_datetime(["2025-01-01"] * 4), "symbol": list("ABCD"),
                              "in_universe": [True, True, False, True], "score": [3.0, 1.0, 9.0, np.nan]})
        self.assertEqual(["A", "B"], list(sel.pick_top(frame, "score", 5)["symbol"]))


if __name__ == "__main__":
    unittest.main()
