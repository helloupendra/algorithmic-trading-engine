"""
The Python market-structure reader against the schematic the C# reader is
tested on (tests/AlgoTrading.UnitTests/MarketStructureTests.cs). The chart and
the strategies must read the same structure, so the same candles have to give
the same swings, the same breaks and the same inducement. Where a number here
differs from the C# test, one of the two implementations has drifted.
"""

import unittest
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401

from strategies.market_structure import BEARISH, BOS, BULLISH, CHOCH, HIGH, LOW, Bar, MarketStructure

START = datetime(2026, 9, 18, 3, 45, tzinfo=timezone.utc)


def bars(*rows) -> list:
    return [Bar((START + timedelta(minutes=5 * i)).isoformat().replace("+00:00", "Z"), o, h, l, c)
            for i, (o, h, l, c) in enumerate(rows)]


def schematic() -> list:
    """The C# test's candles, bar for bar."""
    return bars(
        (100, 102, 99, 101),        # 0
        (101, 105, 100, 104),       # 1
        (104, 108, 103, 107),       # 2
        (107, 109, 106, 108),       # 3  swing high 109
        (108, 108, 104, 105),       # 4  takes bar 3's low: the pullback is valid
        (105, 106, 101, 102),       # 5
        (102, 103, 100, 101),       # 6  swing low 100
        (101, 104, 101, 103),       # 7  takes bar 6's high
        (103, 107, 102, 106),       # 8
        (106, 111, 105, 110),       # 9  closes through 109: the first break
        (110, 112, 109, 111),       # 10 swing high 112
        (111, 111, 107, 108),       # 11
        (108, 110, 106, 109),       # 12 swing low 106: the leg's inducement
        (109, 112, 108, 111),       # 13
        (111, 114, 110, 113),       # 14 closes above 112 — inducement still stands
        (113, 113, 108, 109),       # 15
        (109, 110, 104, 105),       # 16 takes the inducement at 106
        (105, 109, 104, 108),       # 17
        (108, 113, 107, 112),       # 18
        (112, 116, 111, 115),       # 19 closes through the leg's high 114: BOS
        (115, 117, 113, 116),       # 20 swing high 117
        (116, 116, 110, 111),       # 21
        (111, 112, 105, 106),       # 22
        (106, 107, 101, 102),       # 23 closes through the protected 104
    )


def read(candles=None, **kwargs) -> MarketStructure:
    reader = MarketStructure(**kwargs)
    for bar in candles or schematic():
        reader.push(bar)
    return reader


class SwingTests(unittest.TestCase):
    def test_a_high_stands_when_a_candle_takes_the_liquidity_of_the_candle_that_made_it(self):
        high = next(s for s in read().swings if s.kind == HIGH)

        self.assertEqual(high.price, 109)
        self.assertEqual(high.index, 3)
        self.assertEqual(high.confirmed_index, 4)

    def test_swings_alternate_and_carry_their_labels(self):
        self.assertEqual(
            [(s.kind, s.price, s.label) for s in read().swings],
            [(LOW, 99, None), (HIGH, 109, None), (LOW, 100, "HL"), (HIGH, 112, "HH"),
             (LOW, 106, "HL"), (HIGH, 114, "HH"), (LOW, 104, "LL"), (HIGH, 117, "HH")])


class BreakTests(unittest.TestCase):
    def test_the_marks_are_the_ones_the_chart_draws(self):
        self.assertEqual(
            [(e.kind, e.direction, e.level, e.break_index) for e in read().events],
            [(BOS, BULLISH, 109, 9), (BOS, BULLISH, 114, 19), (CHOCH, BEARISH, 104, 23)])

    def test_a_break_of_structure_waits_for_the_legs_inducement(self):
        reader = read()

        # Bar 14 closed at 113, above the 112 swing high, while the inducement at
        # 106 still stood: no break.
        self.assertFalse(any(e.break_index == 14 for e in reader.events))
        idm = next(i for i in reader.inducements if i.level == 106)
        self.assertEqual(idm.swept_index, 16)

    def test_the_trend_and_its_levels_are_readable_at_every_candle(self):
        reader = read()

        self.assertEqual(reader.trend, BEARISH)
        self.assertEqual(reader.protected_level, 117)      # breaking this turns it back
        self.assertFalse(reader.inducement_taken)

    def test_a_wick_through_a_level_is_not_a_break_unless_wicks_are_taken(self):
        candles = schematic()
        candles[9] = Bar(candles[9].time_utc, candles[9].open, 111, candles[9].low, 108)

        self.assertFalse(any(e.break_index == 9 for e in read(candles).events))
        self.assertTrue(any(e.break_index == 9 for e in read(candles, break_on="wick").events))

    def test_the_first_pullback_can_be_kept_instead_of_the_latest(self):
        # Two pullbacks inside one bullish leg: 103 first, then 109.
        candles = bars(
            (100, 102, 99, 101), (101, 105, 100, 104), (104, 109, 103, 108),
            (108, 108, 102, 103), (103, 104, 100, 101), (101, 106, 100, 105),
            (105, 111, 104, 110),
            (110, 110, 103, 105), (105, 112, 104, 111),
            (111, 116, 110, 115), (115, 115, 109, 110), (110, 117, 109, 116))

        # The one the leg is on now is live rather than filed away, because a
        # reader that is still running has no end to file it at.
        latest = read(candles)
        self.assertEqual([i.level for i in latest.inducements], [103])
        self.assertEqual(latest.inducement_level, 109)

        first = read(candles, inducement_mode="first")
        self.assertEqual([i.level for i in first.inducements], [])
        self.assertEqual(first.inducement_level, 103)


class LiveReadingTests(unittest.TestCase):
    def test_pushing_candle_by_candle_reports_each_break_as_it_happens(self):
        reader = MarketStructure()
        fired = {}
        for i, bar in enumerate(schematic()):
            for event in reader.push(bar):
                fired[i] = (event.kind, event.direction)

        self.assertEqual(fired, {9: (BOS, BULLISH), 19: (BOS, BULLISH), 23: (CHOCH, BEARISH)})

    def test_a_reader_with_nothing_pushed_says_it_knows_nothing(self):
        reader = MarketStructure()

        self.assertIsNone(reader.protected_level)
        self.assertIsNone(reader.break_level)
        self.assertEqual(reader.describe()["trend"], "none")


if __name__ == "__main__":
    unittest.main()
