"""
A strategy runner reads only its own symbol off market:ticks, in batches.

On 28 Sep, 23 runners each read every entry the Dhan feed wrote — ~300
contracts — and decoded every one's JSON to throw away all but their index.
The stream's `symbol` field says whose an entry is without decoding it. No
Redis is needed: the client is a mock whose xread answers from a script.
"""

import contextlib
import io
import json
import os
import unittest
from unittest import mock

import _bootstrap  # noqa: F401

import messaging.redis_subscriber as redis_subscriber
from messaging.redis_subscriber import RedisTickSubscriber, build_subscriber_from_env

SPOT = "NSE:NIFTY50-INDEX"


def entry(n, symbol, payload=None, with_symbol_field=True):
    fields = {"payload": payload if payload is not None else json.dumps({"symbol": symbol, "lastTradedPrice": n})}
    if with_symbol_field:
        fields["symbol"] = symbol
    return (f"{n}-0", fields)


def read(*entries):
    return [["market:ticks", list(entries)]]


class Clock:
    def __init__(self, t=100.0):
        self.t = t

    def __call__(self):
        return self.t


def subscriber(reads, clock=None, **kwargs):
    """A subscriber whose xread answers each call from `reads` in turn, then empties."""
    sleeps = []
    clock = clock or Clock()
    sub = RedisTickSubscriber(sleep=sleeps.append, clock=clock, **kwargs)
    sub.client = mock.MagicMock()
    answers = list(reads)
    calls = []

    def xread(streams, count, block):
        calls.append(count)
        return answers.pop(0) if answers else []

    sub.client.xread.side_effect = xread
    return sub, sleeps, calls


def take(sub, n, **kwargs):
    out = []
    with contextlib.redirect_stdout(io.StringIO()):
        stream = sub.listen_for_ticks(**kwargs)
        for _ in range(n):
            out.append(next(stream))
    return out


class FilterTests(unittest.TestCase):
    def test_only_the_spot_is_decoded_and_yielded(self):
        entries = [entry(i, f"NSE:OPT{i}") for i in range(99)]
        entries.insert(40, entry(500, SPOT))
        sub, _, calls = subscriber([read(*entries)], symbols={SPOT})
        with mock.patch.object(redis_subscriber.json, "loads", wraps=json.loads) as loads:
            ticks = take(sub, 2, yield_idle=True)       # the spot, then the next (empty) read
        self.assertEqual([SPOT, None], [t and t["symbol"] for t in ticks])
        self.assertEqual(1, loads.call_count, "the other 99 were never decoded")
        self.assertEqual(99, sub.skipped)
        self.assertEqual([1000, 1000], calls, "a filtering reader reads in bigger bites")

    def test_a_read_that_keeps_nothing_yields_none(self):
        sub, _, _ = subscriber([read(entry(1, "NSE:OPT1"), entry(2, "NSE:OPT2"))], symbols={SPOT})
        self.assertEqual([None], take(sub, 1, yield_idle=True))
        self.assertEqual("2-0", sub.last_id, "past the skipped entries")

    def test_an_entry_without_a_symbol_field_is_judged_by_its_payload(self):
        sub, _, _ = subscriber([read(entry(1, "NSE:OPT1", with_symbol_field=False),
                                     entry(2, SPOT, with_symbol_field=False))], symbols={SPOT})
        ticks = take(sub, 1, yield_idle=True)
        self.assertEqual([2], [t["lastTradedPrice"] for t in ticks])
        self.assertEqual(1, sub.skipped)

    def test_an_undecodable_spot_entry_is_counted_as_undecodable(self):
        sub, _, _ = subscriber([read(entry(1, SPOT, payload="{not json"), entry(2, "NSE:OPT2"))], symbols={SPOT})
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertIsNone(next(sub.listen_for_ticks(yield_idle=True)))
        self.assertEqual((1, 1), (sub.undecodable, sub.skipped))
        self.assertIn("skipped 1-0", out.getvalue())
        self.assertEqual("2-0", sub.last_id)

    def test_the_reading_position_moves_past_every_entry(self):
        sub, _, _ = subscriber([read(entry(1, SPOT), entry(2, "NSE:OPT2"), entry(3, "NSE:OPT3"))], symbols={SPOT})
        take(sub, 2, yield_idle=True)       # the spot, then the (empty) next read
        self.assertEqual("3-0", sub.last_id)


class BatchingTests(unittest.TestCase):
    def test_after_a_short_read_it_waits_out_the_interval_less_the_time_spent(self):
        clock = Clock()
        sub, sleeps, _ = subscriber([read(entry(1, SPOT)), read(entry(2, SPOT))], clock=clock,
                                    symbols={SPOT}, min_read_interval_ms=100)
        with contextlib.redirect_stdout(io.StringIO()):
            stream = sub.listen_for_ticks(yield_idle=True)
            next(stream)
            clock.t += 0.03                 # the runner spent 30 ms on the tick
            next(stream)
        self.assertEqual(1, len(sleeps))
        self.assertAlmostEqual(0.07, sleeps[0])

    def test_no_wait_after_a_full_read_or_an_empty_one(self):
        full = read(*[entry(i, SPOT) for i in range(1000)])
        sub, sleeps, _ = subscriber([full], symbols={SPOT}, min_read_interval_ms=100)
        take(sub, 1001, yield_idle=True)    # 1000 ticks, then the next read
        take(sub, 1, yield_idle=True)       # an empty read
        self.assertEqual([], sleeps)

    def test_no_wait_when_the_time_is_already_spent(self):
        clock = Clock()
        sub, sleeps, _ = subscriber([read(entry(1, SPOT))], clock=clock, symbols={SPOT}, min_read_interval_ms=100)
        with contextlib.redirect_stdout(io.StringIO()):
            stream = sub.listen_for_ticks(yield_idle=True)
            next(stream)
            clock.t += 0.2
            next(stream)
        self.assertEqual([], sleeps)


class SwitchTests(unittest.TestCase):
    def _env(self, **values):
        patcher = mock.patch.dict(os.environ, {}, clear=False)
        patcher.start()
        self.addCleanup(patcher.stop)
        for key in ("RUNNER_STREAM_FILTER", "RUNNER_STREAM_BATCH_MS"):
            os.environ.pop(key, None)
        os.environ.update(values)

    def test_by_default_a_runner_reads_only_its_spot_in_100ms_batches(self):
        self._env()
        sub = build_subscriber_from_env(symbols={SPOT})
        self.assertEqual((frozenset({SPOT}), 0.1, 1000), (sub.symbols, sub.min_read_interval, sub.read_count))

    def test_both_switches_at_zero_are_todays_reader(self):
        self._env(RUNNER_STREAM_FILTER="0", RUNNER_STREAM_BATCH_MS="0")
        sub = build_subscriber_from_env(symbols={SPOT})
        self.assertEqual((None, 0.0, 100), (sub.symbols, sub.min_read_interval, sub.read_count))
        sub, sleeps, calls = subscriber([read(entry(1, SPOT), entry(2, "NSE:OPT2"))])
        ticks = take(sub, 2)
        self.assertEqual([SPOT, "NSE:OPT2"], [t["symbol"] for t in ticks], "every entry, as before")
        self.assertEqual([100], calls)
        self.assertEqual([], sleeps)

    def test_without_symbols_nothing_changes(self):
        self._env()
        sub = build_subscriber_from_env()
        self.assertEqual((None, 0.0, 100), (sub.symbols, sub.min_read_interval, sub.read_count))


if __name__ == "__main__":
    unittest.main()
