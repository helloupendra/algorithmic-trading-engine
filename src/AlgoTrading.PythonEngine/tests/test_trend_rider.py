"""TrendRider: the opening-range break with the day's trend, its day filters, exit and re-entry."""

import unittest
from datetime import datetime, timedelta, timezone

from strategies.base_strategy import BarFrame, OptionContract, StrategyInput
from strategies.contract_selector import strike_for_requirement
from strategies.directional.trend_rider import TrendRiderStrategy

IST = timezone(timedelta(hours=5, minutes=30))
FIRST_DAY = datetime(2026, 3, 2, tzinfo=IST)


def session(day, prices):
    """Candles from 09:15, one (open, high, low, close) per 5 minutes."""
    out = []
    for i, (o, h, l, c) in enumerate(prices):
        t = (day.replace(hour=9, minute=15) + timedelta(minutes=5 * i)).astimezone(timezone.utc)
        out.append(BarFrame("NSE:NIFTY50-INDEX", "5m", t.isoformat(), o, h, l, c))
    return out


def flat_day(day, base, rng):
    """75 candles swinging inside base +/- rng/2."""
    half = rng / 2
    return session(day, [(base, base + half, base - half, base)] + [(base, base + 1, base - 1, base)] * 74)


class TrendRiderTest(unittest.TestCase):
    def setUp(self):
        self.days = []
        d = FIRST_DAY
        while len(self.days) < 12:
            if d.weekday() < 5:
                self.days.append(d)
            d += timedelta(days=1)

    def history(self, last_range=50.0):
        """Eleven sessions with a 100-point range, the last of them `last_range` wide."""
        bars = []
        for i, day in enumerate(self.days[:11]):
            bars += flat_day(day, 20000, last_range if i == 10 else 100)
        return bars

    def run_day(self, strategy, state, history, today, groups=None):
        """Feed today's candles one at a time; returns (HH:MM, signals) for each candle that signalled."""
        out, bars = [], list(history)
        for frame in today:
            bars.append(frame)
            contracts = {"buy_ce": OptionContract("NSE:NIFTY_CE", "NIFTY", "2026-03-31", 19950, "CE"),
                         "buy_pe": OptionContract("NSE:NIFTY_PE", "NIFTY", "2026-03-31", 20050, "PE")}
            meta = {"open_groups": groups(state) if groups else
                    ([state["position"]["group_id"]] if state["position"] else [])}
            inp = StrategyInput(mode="OfflineReplay", timestamp_utc=frame.timestamp_utc, underlying="NIFTY",
                                spot_price=frame.close, contracts=contracts, bars={"5m": {"index": bars}}, metadata=meta)
            sig = strategy.on_bar(state, inp)
            if sig:
                at = datetime.fromisoformat(frame.timestamp_utc).astimezone(IST).strftime("%H:%M")
                out.append((at, sig))
        return out

    def trend_day(self, up=True):
        """Opening range 19990-20010 around a 20000 open, then a steady move away."""
        s = 1 if up else -1
        prices = [(20000, 20010, 19990, 20000)] * 6
        level = 20000
        for _ in range(69):
            nxt = level + 10 * s
            prices.append((level, max(level, nxt) + 2, min(level, nxt) - 2, nxt))
            level = nxt
        return session(self.days[11], prices)

    def test_buys_the_call_once_the_up_break_has_range(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        fired = self.run_day(strategy, state, self.history(), self.trend_day(up=True))
        first_time, first = fired[0]
        self.assertEqual(first[0].signal_type, "OPEN_GROUP")
        self.assertEqual(first[0].legs[0]["symbol"], "NSE:NIFTY_CE")
        # 09:45 closes 20010 (not above the range); 09:50 is above it but the day's range is 32 < 40% of 100
        self.assertEqual(first_time, "09:55")
        self.assertEqual(state["skipped"].get("range_too_small"), 1)
        self.assertEqual(len(fired), 1, "no target and no reversal: one position held all day")

    def test_buys_the_put_on_a_down_day(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        fired = self.run_day(strategy, state, self.history(), self.trend_day(up=False))
        self.assertEqual(fired[0][1][0].legs[0]["symbol"], "NSE:NIFTY_PE")

    def test_closes_when_nifty_closes_back_across_the_open(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        prices = [(20000, 20010, 19990, 20000)] * 6 + [(20000, 20062, 19998, 20060)] * 2 + [(20060, 20061, 19985, 19990)]
        fired = self.run_day(strategy, state, self.history(), session(self.days[11], prices))
        self.assertEqual([s[0].signal_type for _, s in fired], ["OPEN_GROUP", "CLOSE_GROUP"])
        self.assertIn("back across the day's open", fired[1][1][0].reason)
        self.assertIsNone(state["position"])

    def test_skips_the_day_after_a_wide_one(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        fired = self.run_day(strategy, state, self.history(last_range=160), self.trend_day(up=True))
        self.assertEqual(fired, [])
        self.assertGreater(state["skipped"].get("after_wide_day", 0), 0)

    def test_narrow_yesterday_rule_can_be_switched_off(self):
        normal = self.history(last_range=100)
        strict = TrendRiderStrategy()
        state = strict.initialize_state()
        self.assertEqual(self.run_day(strict, state, normal, self.trend_day(up=True)), [])
        self.assertGreater(state["skipped"].get("yesterday_not_narrow", 0), 0)
        loose = TrendRiderStrategy({"max_prev_range": 0})
        state = loose.initialize_state()
        self.assertTrue(self.run_day(loose, state, normal, self.trend_day(up=True)))

    def test_buys_one_strike_in_the_money_by_default(self):
        reqs = {r.key: r for r in TrendRiderStrategy.get_contract_requirements({})}
        self.assertEqual(set(reqs), {"buy_ce", "buy_pe"})
        self.assertEqual((reqs["buy_ce"].moneyness, reqs["buy_ce"].steps), ("itm", 1))
        # CE in the money sits below the ATM strike, PE above it.
        self.assertEqual(strike_for_requirement(reqs["buy_ce"], 20000, 50), 19950)
        self.assertEqual(strike_for_requirement(reqs["buy_pe"], 20000, 50), 20050)

    def test_itm_steps_moves_the_strike_and_zero_is_atm(self):
        deep = {r.key: r for r in TrendRiderStrategy.get_contract_requirements({"itm_steps": 2})}
        self.assertEqual(strike_for_requirement(deep["buy_ce"], 20000, 50), 19900)
        self.assertEqual(strike_for_requirement(deep["buy_pe"], 20000, 50), 20100)
        atm = {r.key: r for r in TrendRiderStrategy.get_contract_requirements({"itm_steps": 0})}
        self.assertEqual(atm["buy_ce"].moneyness, "atm")
        self.assertEqual(strike_for_requirement(atm["buy_pe"], 20000, 50), 20000)
        with self.assertRaises(ValueError):
            TrendRiderStrategy({"itm_steps": 9})

    def test_needs_ten_full_sessions_of_history(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        short = [b for b in self.history() if b.timestamp_utc >= self.days[5].astimezone(timezone.utc).isoformat()]
        self.assertEqual(self.run_day(strategy, state, short, self.trend_day(up=True)), [])
        self.assertGreater(state["skipped"].get("no_history", 0), 0)

    def test_a_stop_by_the_run_frees_it_and_re_entry_needs_a_new_high(self):
        strategy = TrendRiderStrategy()
        state = strategy.initialize_state()
        stopped = {"done": False}

        def groups(st):
            # The run's leg stop closes the first position at 10:00; every later group stays open.
            if st["position"] and st["position"]["group_id"].endswith("_001") and stopped["done"]:
                return []
            return [st["position"]["group_id"]] if st["position"] else []

        prices = [(20000, 20010, 19990, 20000)] * 6 + [(20000, 20062, 19998, 20060)] * 2
        prices += [(20060, 20061, 20030, 20040)] * 3        # a pullback that stays above the open
        prices += [(20040, 20055, 20035, 20050)]            # back above the range, no new high: no entry
        prices += [(20050, 20080, 20045, 20075)]            # a new day high: re-entry
        today = session(self.days[11], prices)
        fired, bars = [], self.history()
        for frame in today:
            bars.append(frame)
            at = datetime.fromisoformat(frame.timestamp_utc).astimezone(IST).strftime("%H:%M")
            stopped["done"] = at >= "10:00"
            fired += self.run_day(strategy, state, bars[:-1], [frame], groups)
        # 09:45 breaks the range with 64 points of range; the run stops it at 10:00; 10:05 and 10:10 are
        # above the range but below the 20062 high of the first entry; 10:15 makes a new high.
        self.assertEqual([t for t, _ in fired], ["09:45", "10:15"])
        self.assertEqual(state["count"], 2)


if __name__ == "__main__":
    unittest.main()
