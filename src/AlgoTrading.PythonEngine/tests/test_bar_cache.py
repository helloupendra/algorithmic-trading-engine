"""LiveBarCache (26 Nov 2026): a live run reads its bars once a minute in full and at most once a second in part,
not 500 bars on every tick, and the strategy still sees the candle that just closed whole and the forming one fresh."""
import os
import sys
import unittest

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))

from core.bar_cache import TAIL_TAKE, LiveBarCache, merge_tail  # noqa: E402


def bar(start, close, high=None):
    return {"symbol": "NSE:NIFTY50-INDEX", "resolution": "5m", "barStartUtc": start,
            "open": close, "high": high if high is not None else close, "low": close, "close": close}


class FakeApi:
    def __init__(self, rows):
        self.rows = rows          # newest first, as the API returns them
        self.calls = []

    def fetch(self, symbol, resolution, take):
        self.calls.append((symbol, resolution, take))
        return [dict(row) for row in self.rows[:take]]


class Clock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now


def make(rows, interval=1.0):
    api, clock = FakeApi(rows), Clock()
    cache = LiveBarCache(api.fetch, min_interval=interval, clock=clock, wall=lambda: 0.0)
    return api, clock, cache


HIST = [bar("2026-11-26T05:00:00+00:00", 25900.0), bar("2026-11-26T04:55:00+00:00", 25890.0),
        bar("2026-11-26T04:50:00+00:00", 25880.0), bar("2026-11-26T04:45:00+00:00", 25870.0)]


class LiveBarCacheTests(unittest.TestCase):
    def test_first_tick_reads_the_full_window(self):
        api, _, cache = make(HIST)
        rows = cache.get("NSE:NIFTY50-INDEX", "5m", "2026-11-26T05:02:10+00:00")
        self.assertEqual(api.calls, [("NSE:NIFTY50-INDEX", "5m", 500)])
        self.assertEqual([r["close"] for r in rows], [25900.0, 25890.0, 25880.0, 25870.0])

    def test_ticks_inside_a_second_read_nothing(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:02:10+00:00")
        for i in range(8):
            clock.now += 0.1
            cache.get("S", "5m", f"2026-11-26T05:02:1{i}+00:00")
        self.assertEqual(len(api.calls), 1)

    def test_after_a_second_only_the_newest_bars_are_read_and_laid_over(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:02:10+00:00")
        api.rows = [bar("2026-11-26T05:00:00+00:00", 25910.0, high=25915.0)] + HIST[1:]
        clock.now += 1.0
        rows = cache.get("S", "5m", "2026-11-26T05:02:11+00:00")
        self.assertEqual(api.calls[-1], ("S", "5m", TAIL_TAKE))
        self.assertEqual([r["close"] for r in rows], [25910.0, 25890.0, 25880.0, 25870.0])
        self.assertEqual(rows[0]["high"], 25915.0)

    def test_a_new_bucket_from_the_tail_goes_in_front(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:04:58+00:00")
        api.rows = [bar("2026-11-26T05:05:00+00:00", 25920.0)] + HIST
        clock.now += 1.5
        rows = cache.get("S", "5m", "2026-11-26T05:04:59+00:00")
        self.assertEqual([r["barStartUtc"][11:16] for r in rows], ["05:05", "05:00", "04:55", "04:50", "04:45"])

    def test_a_new_exchange_minute_reads_the_full_window_on_its_first_tick(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:04:59.900+00:00")
        clock.now += 0.2                                   # inside the second, but a new minute
        cache.get("S", "5m", "2026-11-26T05:05:00.100+00:00")
        self.assertEqual([c[2] for c in api.calls], [500, 500])

    def test_the_oldest_tail_bucket_is_never_used(self):
        # TAIL_TAKE x minutes rows can cut the oldest bucket short: it must not replace the whole cached bar.
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:02:10+00:00")
        api.rows = [HIST[0], HIST[1], bar("2026-11-26T04:50:00+00:00", 1.0)]   # a cut-short third bucket
        clock.now += 1.0
        rows = cache.get("S", "5m", "2026-11-26T05:02:11+00:00")
        self.assertEqual(rows[2]["close"], 25880.0)

    def test_each_symbol_and_resolution_has_its_own_window(self):
        api, _, cache = make(HIST)
        cache.get("A", "5m", "2026-11-26T05:02:10+00:00")
        cache.get("A", "60m", "2026-11-26T05:02:10+00:00")
        cache.get("B", "5m", "2026-11-26T05:02:10+00:00")
        self.assertEqual(len(api.calls), 3)

    def test_an_unreadable_stamp_falls_back_to_the_wall_clock_minute(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "not a time")
        clock.now += 0.1
        cache.get("S", "5m", None)
        self.assertEqual(len(api.calls), 1)

    def test_a_failed_read_raises_and_the_next_tick_tries_again(self):
        api, clock, cache = make(HIST)
        cache.get("S", "5m", "2026-11-26T05:02:10+00:00")
        clock.now += 1.0
        good = cache._fetch
        cache._fetch = lambda *a: (_ for _ in ()).throw(RuntimeError("API down"))
        with self.assertRaises(RuntimeError):
            cache.get("S", "5m", "2026-11-26T05:02:11+00:00")
        cache._fetch = good
        cache.get("S", "5m", "2026-11-26T05:02:11.5+00:00")
        self.assertEqual(api.calls[-1][2], TAIL_TAKE)

    def test_merge_tail_keeps_newest_first(self):
        merged = merge_tail(HIST, [bar("2026-11-26T05:05:00+00:00", 1.0), bar("2026-11-26T05:00:00+00:00", 2.0),
                                   bar("2026-11-26T04:55:00+00:00", 3.0)], keep=2)
        self.assertEqual([r["close"] for r in merged], [1.0, 2.0, 25890.0, 25880.0, 25870.0])


if __name__ == "__main__":
    unittest.main()
