"""
The version 2 and news-scorer SQL against a real Postgres, when one is
configured (skipped in CI). The MarketIntelligence tables are created as
TEMP tables shaped like the migration's contract — quoted PascalCase columns,
the stated types — so the queries run exactly as they will on the server,
and a TEMP table shadows a real one of the same name for this session only.
"""

import _bootstrap  # noqa: F401

import os
import unittest
from datetime import date, datetime, time, timezone

from analysis import context as ctx
from analysis import news
from analysis.symbols import SymbolTagger, load_equities

DDL = [
    '''CREATE TEMP TABLE market_global_daily ("Id" bigserial, "Symbol" varchar(20), "Date" date, "Open" numeric,
       "High" numeric, "Low" numeric, "Close" numeric, "Volume" bigint, "Source" varchar(40),
       "FetchedUtc" timestamptz, UNIQUE ("Symbol", "Date"))''',
    '''CREATE TEMP TABLE market_participant_oi ("Id" bigserial, "Date" date, "ClientType" varchar(20),
       "FutureIndexLong" bigint, "FutureIndexShort" bigint, "Source" varchar(300), "FetchedUtc" timestamptz)''',
    '''CREATE TEMP TABLE market_breadth_daily ("Id" bigserial, "Exchange" varchar(10), "Date" date, "Advances" int,
       "Declines" int, "Unchanged" int, "Traded" int, "TurnoverCr" numeric, "Highs52w" int, "Lows52w" int,
       "Source" varchar(300))''',
    '''CREATE TEMP TABLE market_events ("Id" bigserial, "Date" date, "TimeIst" time, "Region" varchar(10),
       "Category" varchar(40), "Title" varchar(200), "Importance" int)''',
    '''CREATE TEMP TABLE candles ("Id" bigserial, "Symbol" varchar(100), "Resolution" varchar(10),
       "TimeStampUtc" timestamptz, "Open" numeric, "High" numeric, "Low" numeric, "Close" numeric, "Volume" bigint)''',
    '''CREATE TEMP TABLE live_bars ("Symbol" varchar(100), "Resolution" varchar(10), "BarStartUtc" timestamptz,
       "Open" numeric, "High" numeric, "Low" numeric, "Close" numeric)''',
    '''CREATE TEMP TABLE market_quote_snapshots ("Id" bigserial, "Key" varchar(20), "Price" numeric,
       "PreviousClose" numeric, "ChangePct" numeric, "AsOfUtc" timestamptz, "FetchedUtc" timestamptz,
       "Source" varchar(40))''',
    '''CREATE TEMP TABLE news_items ("Id" bigserial PRIMARY KEY, "Source" varchar(80), "Category" varchar(40),
       "Title" text, "Summary" text, "Link" text, "LinkHash" varchar(64), "PublishedUtc" timestamptz,
       "FirstSeenUtc" timestamptz, "Sentiment" numeric(4,3), "Importance" smallint, "Symbols" text DEFAULT '',
       "Topics" text DEFAULT '', "ScoredUtc" timestamptz, "ScoreModel" varchar(80) DEFAULT '')''',
    '''CREATE TEMP TABLE corporate_announcements ("Id" bigserial PRIMARY KEY, "Exchange" varchar(10),
       "Symbol" varchar(40), "Company" text, "Subject" text, "Details" text, "AttachmentUrl" text,
       "AnnouncedUtc" timestamptz, "FirstSeenUtc" timestamptz, "UniqueKey" varchar(200), "Sentiment" numeric(4,3),
       "Importance" smallint, "Symbols" text DEFAULT '', "Topics" text DEFAULT '', "ScoredUtc" timestamptz,
       "ScoreModel" varchar(80) DEFAULT '')''',
    '''CREATE TEMP TABLE corporate_calendar ("Id" bigserial, "Exchange" varchar(10), "Symbol" varchar(40),
       "Company" text, "Purpose" text, "EventDate" date, "FirstSeenUtc" timestamptz, "UniqueKey" varchar(200))''',
    '''CREATE TEMP TABLE instruments ("Symbol" varchar(100), "Exchange" varchar(50), "Segment" varchar(50),
       "Description" varchar(300))''',
]

UTC = timezone.utc
MON, TUE, WED = date(2026, 9, 21), date(2026, 9, 22), date(2026, 9, 23)


class FakeModel:
    def predict(self, texts):
        return [(0.7, 0.1, 0.2) for _ in texts]


class SqlTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import psycopg2

            import core.config  # noqa: F401
            from sentinel.store import dsn_from_env
        except ImportError:
            raise unittest.SkipTest("psycopg2 not installed")
        dsn = dsn_from_env(dict(os.environ))
        if dsn is None:
            raise unittest.SkipTest("no POSTGRES_PASSWORD configured")
        try:
            cls.conn = psycopg2.connect(dsn, connect_timeout=2)
        except Exception:
            raise unittest.SkipTest("no database reachable")
        with cls.conn.cursor() as cur:
            for ddl in DDL:
                cur.execute(ddl)
            fetched = datetime(2026, 9, 24, 1, 0, tzinfo=UTC)
            for d, close in ((date(2026, 9, 18), 6600.0), (MON, 6660.0), (TUE, 6726.6), (WED, 9999.0)):
                cur.execute('INSERT INTO market_global_daily ("Symbol", "Date", "Close", "Source", "FetchedUtc") '
                            "VALUES ('SPX', %s, %s, 'test', %s)", (d, close, fetched))
            for d, lg in ((MON, 300), (TUE, 400), (WED, 10 ** 9)):
                cur.execute('INSERT INTO market_participant_oi ("Date", "ClientType", "FutureIndexLong", '
                            '"FutureIndexShort", "Source", "FetchedUtc") VALUES (%s, %s, %s, 100, %s, %s)',
                            (d, "FII", lg, "test", fetched))
                cur.execute('INSERT INTO market_participant_oi ("Date", "ClientType", "FutureIndexLong", '
                            '"FutureIndexShort", "Source", "FetchedUtc") VALUES (%s, %s, 1, 1, %s, %s)',
                            (d, "Client", "test", fetched))
            cur.execute('INSERT INTO market_breadth_daily ("Exchange", "Date", "Advances", "Declines", "Unchanged", '
                        '"Traded", "Highs52w", "Lows52w") VALUES (\'NSE\', %s, 1500, 500, 100, 2100, 60, 10), '
                        '(\'BSE\', %s, 1, 1, 1, 3, 0, 0)', (TUE, TUE))
            cur.execute('INSERT INTO market_events ("Date", "TimeIst", "Region", "Category", "Title", "Importance") '
                        "VALUES (%s, '10:00', 'IN', 'RBI policy', 'test RBI decision', 3)", (WED,))
            cur.execute('INSERT INTO market_quote_snapshots ("Key", "Price", "ChangePct", "AsOfUtc", "FetchedUtc") '
                        "VALUES ('GIFTNIFTY', 24800, 0.4, %s, %s), ('GIFTNIFTY', 99999, 9, %s, %s)",
                        (datetime(2026, 9, 24, 3, 15, tzinfo=UTC), datetime(2026, 9, 24, 3, 16, tzinfo=UTC),
                         datetime(2026, 9, 24, 4, 0, tzinfo=UTC), datetime(2026, 9, 24, 4, 1, tzinfo=UTC)))
            for k, (title, seen) in enumerate((("HDFC Bank Q2 profit beats estimates", datetime(2026, 9, 23, 12, 0)),
                                               ("Rupee falls", datetime(2026, 9, 24, 2, 0)),
                                               ("After the cutoff", datetime(2026, 9, 24, 3, 30)))):
                cur.execute('INSERT INTO news_items ("Source", "Category", "Title", "Summary", "FirstSeenUtc") '
                            "VALUES ('Wire', 'markets', %s, '', %s)", (title, seen.replace(tzinfo=UTC)))
            cur.execute('INSERT INTO corporate_announcements ("Exchange", "Symbol", "Company", "Subject", "Details", '
                        '"FirstSeenUtc") VALUES (\'NSE\', \'HDFCBANK\', \'HDFC Bank Limited\', \'Credit Rating\', '
                        "'', %s)", (datetime(2026, 9, 23, 13, 0, tzinfo=UTC),))
            cur.execute('INSERT INTO corporate_calendar ("Exchange", "Symbol", "Company", "Purpose", "EventDate", '
                        '"FirstSeenUtc") VALUES (\'NSE\', \'TCS\', \'TCS\', \'Financial Results\', %s, %s), '
                        "('NSE', 'SMALLCO', 'x', 'Financial Results', %s, %s)",
                        (date(2026, 9, 24), datetime(2026, 9, 1, tzinfo=UTC), date(2026, 9, 24),
                         datetime(2026, 9, 1, tzinfo=UTC)))
            cur.execute("INSERT INTO instruments VALUES ('NSE:HDFCBANK-EQ', 'NSE', 'CM', 'HDFC BANK LTD'), "
                        "('NSE:HDFCBANK-BE', 'NSE', 'CM', 'HDFC BANK LTD'), ('NSE:X24SEP100CE', 'NSE', 'FO', 'x')")

    @classmethod
    def tearDownClass(cls):
        cls.conn.rollback()
        cls.conn.close()

    def test_the_context_loads_from_the_contract_tables(self):
        with self.assertLogs("analysis.context", "WARNING"):           # only SPX is stored here, and it says so
            c = ctx.load(self.conn, TUE, WED, {})
        self.assertEqual([r.day for r in c.global_rows["SPX"]], [date(2026, 9, 18), MON, TUE, WED])
        self.assertEqual([(r.day, r.long) for r in c.fii], [(MON, 300), (TUE, 400), (WED, 10 ** 9)])  # FII only
        self.assertEqual([(r.day, r.advances) for r in c.breadth], [(TUE, 1500)])                    # NSE only
        self.assertTrue(any(e.title == "test RBI decision" and e.time_ist == time(10, 0) for e in c.events))
        cols = ctx.columns([TUE, WED], c)
        self.assertAlmostEqual(cols.values["spxRet"][1], 1.0, places=6)     # TUE over MON; WED's row unseen
        self.assertAlmostEqual(cols.values["fiiNetLong"][1], 60.0)          # (400 − 100) / 500
        self.assertEqual(cols.values["majorEvent"][1], 1.0)

    def test_the_live_only_queries_run_and_respect_the_cutoff(self):
        cutoff = datetime(2026, 9, 24, 3, 20, tzinfo=UTC)                  # 08:50 IST 24 Sep
        out = ctx.load_live_only(self.conn, date(2026, 9, 24), WED, 24650.0, cutoff)
        self.assertAlmostEqual(out["giftNiftyGapPct"], (24800 / 24650 - 1) * 100, places=3)
        self.assertEqual(out["news"]["markets"]["n"], 2)                    # the 09:00 IST headline is after it
        self.assertEqual(out["news"]["nifty50 announcements"]["n"], 1)
        self.assertEqual((out["earningsToday"], out["earningsSincePrev"]), (1, 0))

    def test_the_scorer_reads_and_writes_the_contract_columns(self):
        items = news.fetch_unscored(self.conn, 10)
        self.assertEqual([i.table for i in items], ["news_items", "corporate_announcements", "news_items",
                                                    "news_items"])
        tagger = SymbolTagger(load_equities(self.conn))
        self.assertEqual(tagger.tickers, {"HDFCBANK"})                     # -EQ cash equities only
        results = news.score_items(items, FakeModel(), tagger)
        now = datetime(2026, 9, 24, 3, 25, tzinfo=UTC)
        self.assertEqual(news.write(self.conn, results, now), 4)
        with self.conn.cursor() as cur:
            cur.execute('SELECT "Sentiment", "Importance", "Symbols", "Topics", "ScoreModel" FROM news_items '
                        'WHERE "Title" LIKE %s', ("HDFC Bank%",))
            sentiment, importance, symbols, topics, model = cur.fetchone()
        self.assertEqual((float(sentiment), importance, symbols, topics, model),
                         (0.6, 3, "HDFCBANK", "results", news.SCORE_MODEL))
        self.assertEqual(news.fetch_unscored(self.conn, 10), [])            # scored rows are not picked again
        self.assertEqual(news.write(self.conn, results, now), 0)            # nor written twice


if __name__ == "__main__":
    unittest.main()
