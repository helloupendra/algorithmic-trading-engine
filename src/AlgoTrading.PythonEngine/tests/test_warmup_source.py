"""
Warm-up must not need a FYERS token when another connector has already filled
the platform's candle store.

On 2026-09-16 the Dhan feed carried the market from 09:15 — ticks, bars and
quotes all arriving — and Ghost still waited in broker-auth retries until the
owner signed in to FYERS at 09:36. These pin the order: local store first, the
broker only to fill a gap, and never a blocking wait when local bars exist.
"""

import unittest
from datetime import datetime, timedelta, timezone

from core.warmup_source import canonical_resolution, load_warmup_bars, local_bars


def rows(count, symbol="NSE:NIFTY50-INDEX", start="2026-09-15T03:45:00Z"):
    first = datetime.fromisoformat(start.replace("Z", "+00:00"))
    return [{
        "symbol": symbol, "timestampUtc": (first + timedelta(minutes=5 * i)).isoformat().replace("+00:00", "Z"),
        "open": 100.0 + i, "high": 101.0 + i, "low": 99.0 + i, "close": 100.5 + i, "volume": 1000 + i,
    } for i in range(count)]


class FakeApi:
    def __init__(self, stored=None, error=None):
        self.stored, self.error, self.calls = stored or [], error, []

    def get_local_history(self, symbol, resolution, from_date, to_date):
        self.calls.append((symbol, resolution, from_date, to_date))
        if self.error:
            raise self.error
        return self.stored


class FakeEngine:
    """A broker history client that answers, refuses, or returns nothing."""
    made = 0

    def __init__(self, bars=None, error=None):
        self.bars, self.error = bars, error

    def __call__(self):
        FakeEngine.made += 1
        return self

    def get_historical_bars(self, symbol, resolution, start_date, end_date):
        if self.error:
            raise self.error
        return self.bars or []


def broker_bars(count):
    from core.data_models import BarData
    first = datetime(2026, 9, 15, 3, 45, tzinfo=timezone.utc)
    return [BarData(symbol="NSE:NIFTY50-INDEX", resolution="5m", timestamp_start=first + timedelta(minutes=5 * i),
                    open=1.0, high=2.0, low=0.5, close=1.5, volume=10) for i in range(count)]


AUTH = Exception("API is not authenticated with Broker. Please login.")


class ResolutionTests(unittest.TestCase):
    def test_the_store_keys_candles_without_the_m(self):
        self.assertEqual("5", canonical_resolution("5m"))
        self.assertEqual("15", canonical_resolution("15"))
        self.assertEqual("D", canonical_resolution("d"))


class LocalBarTests(unittest.TestCase):
    def test_rows_become_bars_oldest_first(self):
        bars = local_bars(FakeApi(list(reversed(rows(3)))), "NSE:NIFTY50-INDEX", "5m", "2026-09-01", "2026-09-15")
        self.assertEqual(3, len(bars))
        self.assertEqual(sorted(b.timestamp_start for b in bars), [b.timestamp_start for b in bars])
        self.assertEqual("5m", bars[0].resolution)

    def test_a_broken_row_is_dropped_not_fatal(self):
        bad = rows(2) + [{"open": 1}]
        self.assertEqual(2, len(local_bars(FakeApi(bad), "X", "5m", "2026-09-01", "2026-09-15")))

    def test_an_unreachable_api_answers_with_nothing(self):
        self.assertEqual([], local_bars(FakeApi(error=RuntimeError("500")), "X", "5m", "a", "b"))


class LoadTests(unittest.TestCase):
    def setUp(self):
        FakeEngine.made = 0

    def load(self, api, engine, **kw):
        return load_warmup_bars(api=api, make_engine=engine, symbol="NSE:NIFTY50-INDEX", resolution="5m",
                                start_date="2026-09-01", end_date="2026-09-16", label="NIFTY",
                                log=lambda _: None, sleep=lambda _: None, **kw)

    def test_a_full_local_store_is_used_and_the_broker_is_never_called(self):
        engine = FakeEngine(error=AUTH)
        out = self.load(FakeApi(rows(80)), engine)
        self.assertEqual(("local store", 80), (out.source, len(out.bars)))
        self.assertEqual(0, FakeEngine.made)

    def test_an_expired_broker_token_no_longer_blocks_when_local_bars_exist(self):
        # The 16 Sep case: 21 minutes of retries replaced by one try.
        out = self.load(FakeApi(rows(20)), FakeEngine(error=AUTH))
        self.assertEqual("local store", out.source)
        self.assertEqual(20, len(out.bars))
        self.assertIn("rejected the token", out.detail)
        self.assertEqual(1, FakeEngine.made)

    def test_the_broker_fills_a_short_local_store(self):
        out = self.load(FakeApi(rows(10)), FakeEngine(bars=broker_bars(300)))
        self.assertEqual(("broker history", 300), (out.source, len(out.bars)))

    def test_with_nothing_local_the_broker_is_retried_while_someone_signs_in(self):
        out = self.load(FakeApi([]), FakeEngine(error=AUTH))
        self.assertEqual(("none", 0), (out.source, len(out.bars)))
        self.assertGreater(FakeEngine.made, 5)   # it waited for a sign-in
        self.assertIn("no bars", out.detail)

    def test_a_non_auth_failure_still_falls_back_to_local_bars(self):
        out = self.load(FakeApi(rows(12)), FakeEngine(error=RuntimeError("history API 500")))
        self.assertEqual(("local store", 12), (out.source, len(out.bars)))
        self.assertIn("broker failed", out.detail)
