"""
Version 2's inputs (analysis/context.py). Every point-in-time rule has a test
built to fail if the rule were wrong: the fixture holds a row dated on (or
after) the session with a value no honest input could produce, and the test
checks the input did not see it.
"""

import _bootstrap  # noqa: F401

import json
import math
import os
import unittest
from dataclasses import replace
from datetime import date, datetime, time, timedelta, timezone
from urllib.parse import urlparse

import numpy as np

import _analysis_fakes as fakes
from analysis import context as ctx
from analysis.context import BreadthDay, Context, Event, GlobalRow, ParticipantDay
from analysis.data import DataError, SessionBar, SessionSeries
from analysis.models import MODELS_V2, build_table, forecast

NO_EXPIRY = (lambda _d: False)

# A plain week: Mon 2026-09-14 .. Fri 2026-09-18, then Mon 2026-09-21.
MON, TUE, WED, THU, FRI = (date(2026, 9, 14) + timedelta(days=k) for k in range(5))
NEXT_MON = date(2026, 9, 21)


def only_global(symbol, rows):
    return Context(global_rows={symbol: [GlobalRow(d, c) for d, c in rows]})


def value(c, name, days, day):
    return float(ctx.columns(days, c).values[name][days.index(day)])


class OvernightGlobalTests(unittest.TestCase):
    def test_the_us_move_is_the_last_session_before_the_morning_never_the_sessions_own_date(self):
        # SPX dated WED is Wednesday's US session: it closes at 01:30 IST Thursday, after India's Wednesday
        # close, and before Thursday's forecast. The row dated THU is Thursday's US session, which has not
        # happened at 08:50 IST Thursday: a 1000% move there must not reach Thursday's input.
        c = only_global("SPX", [(MON, 100.0), (TUE, 101.0), (WED, 102.01), (THU, 1020.1)])
        days = [TUE, WED, THU]
        self.assertAlmostEqual(value(c, "spxRet", days, THU), 1.0, places=6)     # WED over TUE
        self.assertAlmostEqual(value(c, "spxRet", days, WED), 1.0, places=6)     # TUE over MON

    def test_after_an_indian_holiday_the_move_adds_up_every_us_session_india_missed(self):
        # India shut on Wednesday: Thursday's previous session is Tuesday. The US traded Tue and Wed since
        # India's Tuesday close, so the input is Wed's close over Mon's (the last row before Tuesday).
        c = only_global("SPX", [(MON, 100.0), (TUE, 110.0), (WED, 121.0), (THU, 999.0)])
        self.assertAlmostEqual(value(c, "spxRet", [TUE, THU], THU), 21.0, places=6)

    def test_no_us_session_since_the_indian_close_is_no_move(self):
        # A US holiday on Thursday: Friday's forecast has seen no US session since India's Thursday close.
        c = only_global("SPX", [(MON, 100.0), (TUE, 101.0), (WED, 105.0), (FRI, 999.0)])
        self.assertEqual(value(c, "spxRet", [THU, FRI], FRI), 0.0)

    def test_a_series_that_stopped_arriving_is_missing_not_flat(self):
        c = only_global("SPX", [(MON - timedelta(days=7), 100.0), (MON, 101.0)])
        self.assertTrue(math.isnan(value(c, "spxRet", [MON + timedelta(days=5), NEXT_MON + timedelta(days=1)],
                                         NEXT_MON + timedelta(days=1))))

    def test_vix_level_and_change_and_yield_change_are_in_points(self):
        c = Context(global_rows={"VIX": [GlobalRow(MON, 15.0), GlobalRow(TUE, 18.5), GlobalRow(WED, 99.0)],
                                 "US10Y": [GlobalRow(MON, 4.10), GlobalRow(TUE, 4.25), GlobalRow(WED, 9.0)]})
        days = [TUE, WED]
        self.assertEqual(value(c, "usVix", days, WED), 18.5)
        self.assertAlmostEqual(value(c, "usVixChange", days, WED), 3.5)
        self.assertAlmostEqual(value(c, "us10yChange", days, WED), 0.15)

    def test_asia_is_its_last_closed_session_not_this_mornings(self):
        # At 08:50 IST Tokyo has been open for three hours: the row dated THU is a session in progress.
        c = only_global("N225", [(MON, 100.0), (TUE, 102.0), (WED, 99.96), (THU, 500.0)])
        self.assertAlmostEqual(value(c, "n225Ret", [WED, THU], THU), -2.0, places=6)
        self.assertAlmostEqual(value(c, "asiaRet", [WED, THU], THU), -2.0, places=6)


class ParticipantAndBreadthTests(unittest.TestCase):
    def fii(self):
        rows = [ParticipantDay(date(2026, 9, 7) + timedelta(days=k), 100 + 10 * k, 100) for k in range(12)]
        return [r for r in rows if r.day.weekday() < 5]

    def test_fii_positioning_is_the_previous_sessions_file(self):
        rows = self.fii()
        days = [r.day for r in rows]
        # The file for the session itself is published that evening: replace it with an absurd one.
        i = 7
        poisoned = rows[:i] + [replace(rows[i], long=10 ** 9)] + rows[i + 1:]
        cols = ctx.columns(days, Context(fii=poisoned))
        prev = rows[i - 1]
        self.assertAlmostEqual(cols.values["fiiNetLong"][i], prev.net_long_pct)
        self.assertAlmostEqual(cols.values["fiiChange1"][i], prev.net_long_pct - rows[i - 2].net_long_pct)
        self.assertAlmostEqual(cols.values["fiiChange5"][i], prev.net_long_pct - rows[i - 6].net_long_pct)

    def test_a_missing_file_is_missing_not_the_one_before(self):
        rows = self.fii()
        days = [r.day for r in rows]
        cols = ctx.columns(days, Context(fii=[r for r in rows if r.day != days[5]]))
        self.assertTrue(math.isnan(cols.values["fiiNetLong"][6]))       # its previous session's file is gone
        self.assertFalse(math.isnan(cols.values["fiiNetLong"][7]))

    def test_net_long_is_a_share_of_open_positions(self):
        self.assertEqual(ParticipantDay(MON, 300, 100).net_long_pct, 50.0)
        self.assertEqual(ParticipantDay(MON, 100, 300).net_long_pct, -50.0)
        self.assertTrue(math.isnan(ParticipantDay(MON, 0, 0).net_long_pct))

    def test_a_missing_52_week_file_leaves_only_those_inputs_missing(self):
        cols = ctx.columns([TUE, WED], Context(breadth=[BreadthDay(TUE, 1500, 500, 100, 2100, None, None)]))
        self.assertTrue(math.isnan(cols.values["netHighsLows"][1]))
        self.assertAlmostEqual(cols.values["adRatio"][1], 3.0)

    def test_breadth_is_the_previous_session(self):
        rows = [BreadthDay(TUE, 1500, 500, 100, 2100, 60, 10), BreadthDay(WED, 1, 10 ** 6, 0, 10 ** 6, 0, 10 ** 5)]
        cols = ctx.columns([TUE, WED], Context(breadth=rows))
        self.assertAlmostEqual(cols.values["adRatio"][1], 3.0)
        self.assertAlmostEqual(cols.values["pctAdvancing"][1], 1500 / 2100 * 100)
        self.assertEqual(cols.values["netHighsLows"][1], 50.0)
        self.assertAlmostEqual(cols.values["netHighsLowsPct"][1], 50 / 2100 * 100)


class HeavyweightTests(unittest.TestCase):
    def series(self, closes_by_stock):
        return {s: SessionSeries(f"NSE:{s}-EQ", [SessionBar(d, c, c, c, c, 75, "candles 5m") for d, c in rows])
                for s, rows in closes_by_stock.items()}

    def test_the_previous_sessions_returns_and_their_spread(self):
        stocks = ctx.HEAVYWEIGHTS
        moves = [1.0, -1.0, 2.0, 0.0, 0.5, -0.5, 1.5, -2.0, 0.0, 1.0]
        data = {s: [(TUE, 100.0), (WED, 100.0 * (1 + m / 100)), (THU, 10_000.0)] for s, m in zip(stocks, moves)}
        cols = ctx.columns([WED, THU], Context(heavy=self.series(data)))
        self.assertAlmostEqual(cols.values["hwRet"][1], np.mean(moves), places=6)
        self.assertAlmostEqual(cols.values["hwDispersion"][1], np.std(moves), places=6)
        self.assertEqual(cols.values["hwCount"][1], 10)

    def test_a_bonus_issue_is_not_a_crash(self):
        stocks = ctx.HEAVYWEIGHTS
        data = {s: [(TUE, 100.0), (WED, 101.0)] for s in stocks}
        data["RELIANCE"] = [(TUE, 3000.0), (WED, 1500.0)]              # 1:1 bonus in unadjusted candles
        cols = ctx.columns([WED, THU], Context(heavy=self.series(data)))
        self.assertEqual(cols.values["hwCount"][1], 9)
        self.assertAlmostEqual(cols.values["hwRet"][1], 1.0, places=6)

    def test_too_few_stocks_is_missing(self):
        data = {s: [(TUE, 100.0), (WED, 101.0)] for s in ctx.HEAVYWEIGHTS[:6]}
        cols = ctx.columns([WED, THU], Context(heavy=self.series(data)))
        self.assertTrue(math.isnan(cols.values["hwRet"][1]))


class EventTests(unittest.TestCase):
    def test_the_session_an_event_first_reaches(self):
        holidays = {"NSE": frozenset({FRI})}
        cases = [
            (Event(TUE, time(10, 0), "RBI policy", "x"), TUE),              # during the session
            (Event(TUE, None, "Budget", "x"), TUE),                          # no stated time: that day
            (Event(TUE, time(23, 30), "Fed policy", "x"), WED),              # after the close
            (Event(WED, time(0, 30), "Fed policy", "x"), WED),               # 00:30 IST: before that day's open
            (Event(TUE, time(17, 30), "India CPI", "x"), WED),
            (Event(THU, time(18, 0), "US jobs", "x"), NEXT_MON),             # Friday is a holiday
            (Event(date(2026, 9, 19), time(11, 0), "Budget", "x"), NEXT_MON),  # a Saturday Budget
        ]
        for event, expected in cases:
            self.assertEqual(ctx.reaction_session(event, holidays), expected, event)

    def test_flags_day_before_and_after_by_group(self):
        events = [Event(WED, time(10, 0), "RBI policy", "RBI monetary policy decision"),
                  Event(MON, time(18, 0), "US CPI", "US CPI inflation (Aug 2026)")]
        flags = ctx.event_flags([MON, TUE, WED, THU, FRI], events, {})
        self.assertEqual([f["majorEvent"] for f in flags], [0, 0, 1, 0, 0])
        self.assertEqual([f["majorEve"] for f in flags], [0, 1, 0, 0, 0])
        self.assertEqual([f["majorAfter"] for f in flags], [0, 0, 0, 1, 0])
        self.assertEqual([f["dataRelease"] for f in flags], [0, 1, 0, 0, 0])   # 18:00 IST Monday reaches Tuesday
        self.assertEqual(flags[2]["label"], "rbi day, usCpi after")
        self.assertEqual(flags[1]["label"], "rbi before, usCpi day")

    def test_an_unannounced_decision_is_known_only_afterwards(self):
        # RBI's off-cycle hike of 4 May 2022 came at 14:00: at 08:50 that morning nobody knew, and the day
        # before nobody knew either. Only the session after it may carry the flag.
        events = [Event(WED, time(14, 0), "RBI policy", "RBI off-cycle monetary policy decision")]
        flags = ctx.event_flags([TUE, WED, THU], events, {})
        self.assertEqual([(f["majorEve"], f["majorEvent"], f["majorAfter"]) for f in flags],
                         [(0, 0, 0), (0, 0, 0), (0, 0, 1)])

    def test_an_unannounced_decision_out_before_the_morning_flags_its_own_session(self):
        # The Fed's Sunday cut of 15 March 2020 came at 17:00 US Eastern, 02:30 IST Monday: by 08:50 it was known.
        monday, friday = date(2020, 3, 16), date(2020, 3, 13)
        events = [Event(monday, time(2, 30), "Fed policy", "US Fed unscheduled rate decision")]
        flags = ctx.event_flags([friday, monday, date(2020, 3, 17)], events, {})
        self.assertEqual([(f["majorEve"], f["majorEvent"], f["majorAfter"]) for f in flags],
                         [(0, 0, 0), (0, 1, 0), (0, 0, 1)])
        # With no stated time there is no telling whether it was out by 08:50: only the day after.
        events = [Event(monday, None, "Fed policy", "US Fed unscheduled rate decision")]
        flags = ctx.event_flags([monday, date(2020, 3, 17)], events, {})
        self.assertEqual([(f["majorEvent"], f["majorAfter"]) for f in flags], [(0, 0), (0, 1)])

    def test_other_categories_are_not_model_inputs(self):
        flags = ctx.event_flags([WED], [Event(WED, None, "Election", "Results")], {})
        self.assertEqual((flags[0]["majorEvent"], flags[0]["label"]), (0, ""))


class WholeContextLeakTests(unittest.TestCase):
    """Corrupt every context row dated on or after a session; nothing up to that session may move."""

    def setUp(self):
        self.market = fakes.market(n=600)
        self.days = [s.day for s in self.market.series["NIFTY"].sessions]
        self.ctx = fakes.context_for(self.days)
        self.i = 520
        self.cut = self.days[self.i]

    def corrupted(self):
        cut, c = self.cut, self.ctx
        glob = {s: [r if r.day < cut else replace(r, close=r.close * 50) for r in rows]
                for s, rows in c.global_rows.items()}
        fii = [r if r.day < cut else replace(r, long=r.long * 1000) for r in c.fii]
        breadth = [r if r.day < cut else replace(r, advances=1, declines=10 ** 6, highs=0, lows=10 ** 5)
                   for r in c.breadth]
        heavy = {s: SessionSeries(x.symbol, [b if b.day < cut else replace(b, close=b.close * 9) for b in x.sessions])
                 for s, x in c.heavy.items()}
        # Unannounced events on or after the cut (known only afterwards) must not move anything before it either.
        events = c.events + [Event(d, time(14, 0), "RBI policy", "RBI off-cycle decision") for d in self.days[self.i:]]
        return Context(glob, fii, breadth, heavy, events, c.holidays)

    def test_no_context_column_moves(self):
        good = ctx.columns(self.days, self.ctx)
        bad = ctx.columns(self.days, self.corrupted())
        for name in ctx.COLUMNS:
            np.testing.assert_array_equal(good.values[name][:self.i + 1], bad.values[name][:self.i + 1], name)
        self.assertEqual(good.events[:self.i + 1], bad.events[:self.i + 1])
        self.assertTrue(np.isfinite(good.values["spxRet"][self.i]))

    def test_no_v2_forecast_moves_when_its_session_and_everything_after_it_change(self):
        sessions, vix = self.market.series["NIFTY"].sessions, self.market.vix.closes()
        bad_sessions = [s if s.day < self.cut else replace(s, high=s.high * 5, close=s.close * 2) for s in sessions]
        bad_vix = {d: (v if d < self.cut else v * 10) for d, v in vix.items()}
        for key, spec in MODELS_V2.items():
            good = forecast(spec, build_table("NIFTY", sessions, vix, NO_EXPIRY, context=self.ctx), self.i, self.cut)
            bad = forecast(spec, build_table("NIFTY", bad_sessions, bad_vix, NO_EXPIRY, context=self.corrupted()),
                           self.i, self.cut)
            self.assertEqual(good, bad, key)

    def test_the_morning_path_and_the_history_path_give_the_same_v2_forecast(self):
        sessions, vix = self.market.series["NIFTY"].sessions, self.market.vix.closes()
        full = build_table("NIFTY", sessions, vix, NO_EXPIRY, context=self.ctx)
        morning = build_table("NIFTY", [s for s in sessions if s.day < self.cut], vix, NO_EXPIRY, pending=self.cut,
                              context=self.ctx)
        for key, spec in MODELS_V2.items():
            self.assertEqual(forecast(spec, full, self.i, self.cut),
                             forecast(spec, morning, len(morning.days) - 1, self.cut), key)

    def test_a_v2_forecast_records_every_context_input(self):
        t = build_table("NIFTY", self.market.series["NIFTY"].sessions, self.market.vix.closes(), NO_EXPIRY,
                        context=self.ctx)
        inputs = forecast(MODELS_V2["trend.logit-cues"], t, self.i, self.cut).inputs
        for name in ctx.COLUMNS:
            self.assertIn(name, inputs)
        self.assertIsInstance(inputs["events"], str)
        self.assertIs(type(inputs["majorEvent"]), bool)
        json.dumps(inputs)

    def test_a_missing_input_names_its_table(self):
        c = self.ctx
        no_fii = Context(c.global_rows, [], c.breadth, c.heavy, c.events, c.holidays)
        t = build_table("NIFTY", self.market.series["NIFTY"].sessions, self.market.vix.closes(), NO_EXPIRY,
                        context=no_fii)
        from analysis.models import ForecastUnavailable, fit, predict
        model = fit(MODELS_V2["range.har-vix-cues"], build_table(
            "NIFTY", self.market.series["NIFTY"].sessions, self.market.vix.closes(), NO_EXPIRY, context=c), self.cut)
        with self.assertRaises(ForecastUnavailable) as caught:
            predict(model, t, self.i)
        self.assertIn("market_participant_oi FII", str(caught.exception))


class FakeCursor:
    def __init__(self, conn):
        self.conn = conn

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def execute(self, sql, params=None):
        self.conn.log.append((sql, params))
        for needle, answer in self.conn.answers.items():
            if needle in sql:
                if isinstance(answer, Exception):
                    raise answer
                self.rows = list(answer)
                return
        self.rows = []

    def fetchall(self):
        return self.rows

    def fetchone(self):
        return self.rows[0] if self.rows else None


class FakeConn:
    def __init__(self, answers):
        self.answers = answers
        self.log = []
        self.rolled_back = 0

    def cursor(self):
        return FakeCursor(self)

    def rollback(self):
        self.rolled_back += 1


class LoadTests(unittest.TestCase):
    def test_load_reads_each_table_once_and_the_heavyweights(self):
        conn = FakeConn({
            "market_global_daily": [("SPX", MON, 100.0), ("SPX", TUE, 101.0), ("N225", MON, None)],
            "market_participant_oi": [(MON, 200, 100)],
            "market_breadth_daily": [(MON, 1, 2, 3, 6, None, None)],
            "market_events": [(TUE, time(10, 0), "RBI policy", "admin-added")],
        })
        with self.assertLogs("analysis.context", "WARNING") as logged:
            c = ctx.load(conn, MON, TUE, {})
        self.assertIn("no rows for BRENT", logged.output[0])            # a series never stored is said out loud
        self.assertEqual(c.global_rows["SPX"], [GlobalRow(MON, 100.0), GlobalRow(TUE, 101.0)])
        self.assertNotIn("N225", c.global_rows)                        # a null close is not a price
        self.assertEqual(c.fii, [ParticipantDay(MON, 200, 100)])
        self.assertEqual(c.breadth[0].traded, 6)
        self.assertEqual(set(c.heavy), set(ctx.HEAVYWEIGHTS))
        self.assertIn(Event(TUE, time(10, 0), "RBI policy", "admin-added"), c.events)
        fii_sql, params = next((q, p) for q, p in conn.log if "market_participant_oi" in q)
        self.assertIn("'FII'", fii_sql)
        self.assertEqual(params["end"], TUE)                            # nothing after the last closed day

    def test_a_missing_table_is_a_data_error_that_names_it(self):
        conn = FakeConn({"market_global_daily": RuntimeError("relation does not exist")})
        with self.assertRaises(DataError) as caught:
            ctx.load(conn, MON, TUE, {})
        self.assertIn("market_global_daily", str(caught.exception))


class SeedCalendarTests(unittest.TestCase):
    """The shipped event calendar is the backtest's event history: every row must be checkable."""

    #: The official publishers: the RBI, the Fed, the BLS, India's Budget site, MoSPI and the Government of
    #: India's press bureau.
    OFFICIAL = ("rbi.org.in", "federalreserve.gov", "bls.gov", "indiabudget.gov.in", "mospi.gov.in", "pib.gov.in")

    def setUp(self):
        from core.option_symbol import SEED_DIR
        with open(os.path.join(SEED_DIR, "market_events.json"), encoding="utf-8") as handle:
            self.seed = json.load(handle)

    def test_every_event_is_readable_sourced_and_unique(self):
        seen = set()
        for e in self.seed["events"]:
            date.fromisoformat(e["date"])
            if e.get("timeIst"):
                time.fromisoformat(e["timeIst"])
            url = urlparse(e["source"])
            self.assertEqual(url.scheme, "https", e)
            self.assertTrue(any(url.hostname == d or url.hostname.endswith("." + d) for d in self.OFFICIAL), e)
            self.assertLessEqual(len(e["source"]), 300)
            self.assertLessEqual(len(e.get("notes") or ""), 1000)
            self.assertIn(e["importance"], (1, 2, 3))
            key = (e["date"], e["category"], e["title"])
            self.assertNotIn(key, seen)                                  # the database's unique index
            seen.add(key)

    def test_the_categories_the_models_read_have_history_from_2020(self):
        events = ctx.load_events(None)
        for category in ("RBI policy", "Fed policy", "US CPI", "US jobs", "Budget"):
            first = min(e.day for e in events if e.category == category)
            self.assertLessEqual(first, date(2020, 8, 31), category)   # NIFTY's candles start in Aug 2020


class LiveOnlyTests(unittest.TestCase):
    NOW = datetime(2026, 9, 28, 3, 20, tzinfo=timezone.utc)            # 08:50 IST

    def test_the_cutoff_is_now_but_never_past_the_open(self):
        self.assertEqual(ctx.issue_cutoff(date(2026, 9, 28), self.NOW), self.NOW)
        late = datetime(2026, 9, 28, 9, 0, tzinfo=timezone.utc)
        self.assertEqual(ctx.issue_cutoff(date(2026, 9, 28), late), datetime(2026, 9, 28, 3, 45, tzinfo=timezone.utc))

    def test_gift_news_and_earnings_as_known_at_the_cutoff(self):
        conn = FakeConn({
            "market_quote_snapshots": [(24800.0, 0.4, None, datetime(2026, 9, 28, 3, 15, tzinfo=timezone.utc))],
            "FROM news_items": [("markets", 12, 10, 0.1234, 2), (None, 1, 0, None, None)],
            "FROM corporate_announcements": [(3, 3, -0.2, 3)],
            "corporate_calendar": [(date(2026, 9, 28), 2), (date(2026, 9, 26), 1)],
        })
        out = ctx.load_live_only(conn, date(2026, 9, 28), date(2026, 9, 25), 24650.0, self.NOW)
        self.assertEqual(out["usedByModels"], False)
        self.assertAlmostEqual(out["giftNiftyGapPct"], (24800 / 24650 - 1) * 100, places=3)
        self.assertEqual(out["news"]["markets"], {"n": 12, "scored": 10, "sentiment": 0.123, "maxImportance": 2})
        self.assertEqual(out["news"]["uncategorised"]["sentiment"], None)
        self.assertEqual(out["news"]["nifty50 announcements"]["n"], 3)
        self.assertEqual((out["earningsToday"], out["earningsSincePrev"]), (2, 1))
        # the window: from the previous session's close (15:30 IST 25 Sep = 10:00 UTC) to the cutoff
        news_sql, params = next((q, p) for q, p in conn.log if "FROM news_items" in q)
        self.assertEqual(params["since"], datetime(2026, 9, 25, 10, 0, tzinfo=timezone.utc))
        self.assertEqual(params["cutoff"], self.NOW)
        self.assertIn('"ScoredUtc" <= %(cutoff)s', news_sql)          # a score written after 08:50 does not count
        gift_sql, gift_params = next((q, p) for q, p in conn.log if "market_quote_snapshots" in q)
        self.assertIn('"FetchedUtc" <= %s', gift_sql)                  # what the desk had by then
        self.assertEqual(gift_params[1], self.NOW)
        self.assertEqual((out["giftNiftyAsOf"], out["giftNiftyFetchedUtc"]), (None, "2026-09-28T03:15:00Z"))
        json.dumps(out)

    def test_a_table_that_is_not_there_yet_never_stops_the_morning(self):
        conn = FakeConn({"market_quote_snapshots": RuntimeError("no table"), "news_items": RuntimeError("no table"),
                         "corporate_calendar": RuntimeError("no table")})
        out = ctx.load_live_only(conn, date(2026, 9, 28), date(2026, 9, 25), 24650.0, self.NOW)
        self.assertEqual(out["giftNifty"], "unavailable (RuntimeError)")
        self.assertEqual(out["news"], "unavailable (RuntimeError)")
        self.assertEqual(out["earnings"], "unavailable (RuntimeError)")


if __name__ == "__main__":
    unittest.main()
