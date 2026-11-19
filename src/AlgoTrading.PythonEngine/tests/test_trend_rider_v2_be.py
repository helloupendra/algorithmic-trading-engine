"""TrendRiderV2BE: the breakeven step, the market's own session (MCX), the share itself (equity), and V2 left as it was."""

import random
import unittest
from datetime import datetime, timedelta, timezone

import _bootstrap  # noqa: F401
from strategies.base_strategy import BarFrame, OptionContract, StrategyInput
from strategies.directional.trend_rider_v2 import TrendRiderV2Strategy
from strategies.directional.trend_rider_v2_be import TrendRiderV2BEStrategy
from tests.test_trend_rider import FIRST_DAY, IST, flat_day, session

CONTRACTS = {"buy_ce": OptionContract("NSE:NIFTY_CE", "NIFTY", "2026-03-31", 19950, "CE"),
             "buy_pe": OptionContract("NSE:NIFTY_PE", "NIFTY", "2026-03-31", 20050, "PE")}
CRUDE = "MCX:CRUDEOIL26MARFUT"
SHARE = "NSE:HDFCBANK-EQ"


def weekdays(n, first=FIRST_DAY):
    out, d = [], first
    while len(out) < n:
        if d.weekday() < 5:
            out.append(d)
        d += timedelta(days=1)
    return out


def candles(symbol, day, start, prices):
    """5-minute candles of `symbol` from `start` (HH:MM, IST), one (open, high, low, close) each."""
    h, m = map(int, start.split(":"))
    out = []
    for i, (o, hi, lo, c) in enumerate(prices):
        t = (day.replace(hour=h, minute=m) + timedelta(minutes=5 * i)).astimezone(timezone.utc)
        out.append(BarFrame(symbol, "5m", t.isoformat(), o, hi, lo, c))
    return out


def mcx_day(day, base, rng, close="23:30"):
    """A whole MCX session, 09:00 to `close`, its range set by the first candle (the rest swing a point)."""
    h, m = map(int, close.split(":"))
    n = ((h * 60 + m) - 9 * 60) // 5
    half = rng / 2
    return candles(CRUDE, day, "09:00", [(base, base + half, base - half, base)] + [(base, base + 1, base - 1, base)] * (n - 1))


def stamp_ist(frame):
    return datetime.fromisoformat(frame.timestamp_utc).astimezone(IST).strftime("%H:%M")


class BreakevenTest(unittest.TestCase):
    """The rule that passed (BE25): once +0.25 x ADR in favour, back at the entry sells; the trail is untouched."""

    def setUp(self):
        self.days = weekdays(12)
        # As in the V2 tests: eleven sessions of 100 points, the last 50 wide (19975-20025).
        self.bars = []
        for i, day in enumerate(self.days[:11]):
            self.bars += flat_day(day, 20000, 50 if i == 10 else 100)
        self.adr = (9 * 100 + 50) / 10

    def inp(self, state, spot, stamp):
        groups = [state["position"]["group_id"]] if state["position"] else []
        return StrategyInput(mode="OfflineReplay", timestamp_utc=stamp, underlying="NIFTY", spot_price=spot,
                             contracts=CONTRACTS, bars={"5m": {"index": list(self.bars)}}, metadata={"open_groups": groups})

    def until_entry(self, strategy, state):
        level = 20000
        for i in range(75):
            frame = session(self.days[11], [(level, level + 12, level - 2, level + 10)])[0]
            frame.timestamp_utc = (self.days[11].replace(hour=9, minute=15) + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat()
            self.bars.append(frame)
            level += 10
            sig = strategy.on_bar(state, self.inp(state, frame.close, frame.timestamp_utc))
            if sig:
                return sig, frame
        self.fail("no entry")

    def test_on_by_default_and_sells_back_at_the_entry(self):
        for params, sells in (({}, True), ({"breakeven_adr": 0}, False)):
            with self.subTest(params=params):
                self.setUp()
                strategy = TrendRiderV2BEStrategy(params)
                state = strategy.initialize_state()
                sig, frame = self.until_entry(strategy, state)
                self.assertEqual(sig[0].signal_type, "OPEN_GROUP")
                self.assertEqual(sig[0].metadata["trend_rider"]["index_exits"].get("breakeven_adr", 0), params.get("breakeven_adr", 0.25))
                entry, ts = frame.close, frame.timestamp_utc
                self.assertEqual(strategy.on_bar(state, self.inp(state, entry + 0.25 * self.adr, ts)), [])
                out = strategy.on_bar(state, self.inp(state, entry, ts))
                self.assertEqual(bool(out), sells)
                if sells:
                    self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
                    self.assertIn("breakeven stop", out[0].reason)
                    self.assertTrue(out[0].reason.startswith("TrendRiderV2BE "))
                    self.assertEqual(out[0].legs, [{"symbol": "NSE:NIFTY_CE", "side": "SELL", "quantity": 1}])
                    self.assertIsNone(state["position"])

    def test_not_before_the_move_has_reached_a_quarter_of_the_range(self):
        strategy = TrendRiderV2BEStrategy()
        state = strategy.initialize_state()
        _, frame = self.until_entry(strategy, state)
        entry, ts = frame.close, frame.timestamp_utc
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry + 0.2 * self.adr, ts)), [])
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry, ts)), [])
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry - 1, ts)), [])
        self.assertIsNotNone(state["position"])

    def test_v2s_stop_and_trail_are_unchanged(self):
        # The stop still sells at 0.5 x ADR against, with its own reason, before any move in favour.
        strategy = TrendRiderV2BEStrategy()
        state = strategy.initialize_state()
        _, frame = self.until_entry(strategy, state)
        out = strategy.on_bar(state, self.inp(state, frame.close - 0.5 * self.adr, frame.timestamp_utc))
        self.assertIn("index stop", out[0].reason)
        # The trail still sells on a 0.35 x ADR give-back once 0.5 x ADR in favour, far above the entry.
        self.setUp()
        strategy = TrendRiderV2BEStrategy()
        state = strategy.initialize_state()
        _, frame = self.until_entry(strategy, state)
        entry, ts = frame.close, frame.timestamp_utc
        self.assertEqual(strategy.on_bar(state, self.inp(state, entry + 100, ts)), [])
        out = strategy.on_bar(state, self.inp(state, entry + 100 - 0.35 * self.adr, ts))
        self.assertIn("index trail", out[0].reason)

    def test_defaults_are_v2s_but_the_breakeven_market_and_instrument(self):
        v2, be = TrendRiderV2Strategy.default_params, TrendRiderV2BEStrategy.default_params
        added = {"breakeven_adr": 0.25, "market": "auto", "instrument_kind": "options", "first_entry": "auto", "last_entry": "auto"}
        self.assertEqual({k: v for k, v in be.items() if k not in added}, {k: v for k, v in v2.items() if k not in added})
        self.assertEqual({k: be[k] for k in added}, added)
        s, base = TrendRiderV2BEStrategy({"underlying": "NIFTY"}), TrendRiderV2Strategy()
        for attr in ("first_entry", "last_entry", "session_last", "full_session_bars", "warmup_bars", "itm_steps", "lots"):
            self.assertEqual(getattr(s, attr), getattr(base, attr), attr)
        self.assertEqual(s.index_exits.describe(), {**base.index_exits.describe(), "breakeven_adr": 0.25})

    def test_bad_parameters_are_refused(self):
        for bad in ({"breakeven_adr": -0.1}, {"breakeven_adr": "x"}, {"market": "bse"}, {"instrument_kind": "futures"},
                    {"first_entry": "9am"}, {"instrument": "equity"}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                TrendRiderV2BEStrategy(bad)


class McxSessionTest(unittest.TestCase):
    """An MCX run reads 09:00 to the MCX close: yesterday, the average range, the open and the entries."""

    def setUp(self):
        self.days = weekdays(12)
        # Ten sessions of 60 points, then yesterday: its high in the 09:00 candle, its low in the 23:25 one.
        self.bars = []
        for day in self.days[:10]:
            self.bars += mcx_day(day, 6000, 60)
        yday = mcx_day(self.days[10], 6000, 2)
        yday[0] = BarFrame(CRUDE, "5m", yday[0].timestamp_utc, 6000, 6040, 5999, 6000)
        yday[-1] = BarFrame(CRUDE, "5m", yday[-1].timestamp_utc, 6000, 6001, 5960, 5990)
        self.bars += yday

    def inp(self, strategy, state, frames, underlying="CRUDEOIL"):
        groups = [state["position"]["group_id"]] if state["position"] else []
        contracts = {"buy_ce": OptionContract("MCX:CRUDEOIL26MAR5950CE", "CRUDEOIL", "2026-03-17", 5950, "CE"),
                     "buy_pe": OptionContract("MCX:CRUDEOIL26MAR6050PE", "CRUDEOIL", "2026-03-17", 6050, "PE")}
        return StrategyInput(mode="OfflineReplay", timestamp_utc=frames[-1].timestamp_utc, underlying=underlying,
                             spot_price=frames[-1].close, contracts=contracts, bars={"5m": {"index": list(frames)}},
                             metadata={"open_groups": groups})

    def feed(self, strategy, state, today):
        """Today's candles one by one; (HH:MM, signals) of each candle that signalled."""
        out, frames = [], list(self.bars)
        for frame in today:
            frames.append(frame)
            sig = strategy.on_bar(state, self.inp(strategy, state, frames))
            if sig:
                out.append((stamp_ist(frame), sig))
        return out

    def test_the_session_is_mcx_from_the_underlying_or_its_future(self):
        for params, underlying in (({"underlying": "CRUDEOIL"}, None), ({}, "CRUDEOIL"), ({}, "SOMETHING")):
            with self.subTest(params=params, underlying=underlying):
                strategy = TrendRiderV2BEStrategy(params)
                state = strategy.initialize_state()
                strategy.on_bar(state, self.inp(strategy, state, self.bars, underlying=underlying or "CRUDEOIL"))
                self.assertEqual(strategy.market, "mcx")     # "SOMETHING" is read from the MCX: price symbol
                self.assertEqual((strategy.first_entry, strategy.last_entry), ("09:00", "22:30"))
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "HDFCBANK"}).market, "nse")
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "CRUDEOIL", "market": "nse"}).market, "nse")

    def test_candles_at_0900_and_2200_count_and_yesterday_spans_0900_to_2330(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "CRUDEOIL"})
        today = mcx_day(self.days[11], 6000, 2)[:160]               # 09:00 .. 22:15
        closed = [stamp_ist(f) for _, f in strategy._closed(self.inp(strategy, strategy.initialize_state(), self.bars + today))]
        self.assertIn("09:00", closed)
        self.assertIn("22:00", closed)
        self.assertIn("23:25", closed)
        state = strategy.initialize_state()
        strategy.on_bar(state, self.inp(strategy, state, self.bars + today[:1]))
        yesterday = state["days"][self.days[10].date().isoformat()]
        self.assertEqual(yesterday[1:3], [6040, 5960])                # the 09:00 high and the 23:25 low
        self.assertEqual(yesterday[4], 174)                           # every candle of 09:00-23:30
        adr, prev = strategy._context(state, self.days[11].date().isoformat())
        self.assertEqual(adr, (9 * 60 + 80) / 10)                    # ten complete MCX sessions, yesterday's 80 too
        self.assertEqual(prev[1:3], [6040, 5960])
        # V2 reads NSE's 09:15-15:30 only, so on the same candles it sees neither end of yesterday, and no 22:00.
        v2 = TrendRiderV2Strategy()
        v2_closed = [stamp_ist(f) for _, f in v2._closed(self.inp(v2, v2.initialize_state(), self.bars + today))]
        self.assertNotIn("09:00", v2_closed)
        self.assertNotIn("22:00", v2_closed)

    def test_a_winter_session_to_2355_counts_to_its_last_candle(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "CRUDEOIL"})
        late = mcx_day(self.days[11], 6000, 2, close="23:55")
        self.assertEqual(stamp_ist(late[-1]), "23:50")
        closed = strategy._closed(self.inp(strategy, strategy.initialize_state(), self.bars + late))
        self.assertEqual(stamp_ist(closed[-1][1]), "23:50")

    def test_the_0900_candle_can_enter_above_yesterdays_high(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "CRUDEOIL"})
        state = strategy.initialize_state()
        today = candles(CRUDE, self.days[11], "09:00", [(6030, 6050, 6029, 6045)])
        fired = self.feed(strategy, state, today)
        self.assertEqual(fired[0][0], "09:00")
        sig = fired[0][1][0]
        self.assertEqual((sig.signal_type, sig.legs[0]["symbol"]), ("OPEN_GROUP", "MCX:CRUDEOIL26MAR5950CE"))
        ctx = sig.metadata["trend_rider"]
        self.assertEqual((ctx["prev_high"], ctx["day_open"], ctx["market"]), (6040, 6030, "mcx"))
        self.assertTrue(sig.reason.startswith("Trend Rider V2 BE: CRUDEOIL 6045 closed above yesterday's high"))

    def test_entries_run_to_2230(self):
        flat = [(6000, 6001, 5999, 6000)]
        for break_at, enters in (("22:00", True), ("22:30", True), ("22:35", False)):
            with self.subTest(break_at=break_at):
                strategy = TrendRiderV2BEStrategy({"underlying": "CRUDEOIL"})
                state = strategy.initialize_state()
                h, m = map(int, break_at.split(":"))
                n = ((h * 60 + m) - 9 * 60) // 5
                today = candles(CRUDE, self.days[11], "09:00", flat * n + [(6000, 6050, 5999, 6045)] * 3)
                fired = self.feed(strategy, state, today)
                self.assertEqual([at for at, _ in fired], [break_at] if enters else [])

    def test_the_day_open_exit_names_the_commodity(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "CRUDEOIL", "index_stop_adr": 0, "breakeven_adr": 0})
        state = strategy.initialize_state()
        today = candles(CRUDE, self.days[11], "09:00", [(6030, 6050, 6029, 6045), (6045, 6046, 6020, 6025)])
        fired = self.feed(strategy, state, today)
        self.assertEqual([s[0].signal_type for _, s in fired], ["OPEN_GROUP", "CLOSE_GROUP"])
        self.assertTrue(fired[1][1][0].reason.startswith("CRUDEOIL closed 6025 back across the day's open 6030"))


class EquityTest(unittest.TestCase):
    """instrument_kind equity: the share itself, long only, and no option asked for."""

    def setUp(self):
        self.days = weekdays(12)
        self.bars = []
        for i, day in enumerate(self.days[:11]):
            self.bars += candles(SHARE, day, "09:15", [(1700, 1700 + (5 if i == 10 else 10), 1700 - (5 if i == 10 else 10), 1700)]
                                 + [(1700, 1701, 1699, 1700)] * 74)

    def inp(self, state, frames, underlying="HDFCBANK"):
        groups = [state["position"]["group_id"]] if state["position"] else []
        return StrategyInput(mode="OfflineReplay", timestamp_utc=frames[-1].timestamp_utc, underlying=underlying,
                             spot_price=frames[-1].close, contracts={}, bars={"5m": {"index": list(frames)}},
                             metadata={"open_groups": groups})

    def run_day(self, strategy, state, step):
        frames, out, level = list(self.bars), [], 1700
        for i in range(60):
            frames += candles(SHARE, self.days[11], "09:15", [(level, max(level, level + step) + 0.5,
                                                                min(level, level + step) - 0.5, level + step)])
            frames[-1].timestamp_utc = (self.days[11].replace(hour=9, minute=15) + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat()
            level += step
            sig = strategy.on_bar(state, self.inp(state, frames))
            if sig:
                out.append(sig)
        return out

    def test_buys_the_share_itself_on_an_up_break(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "HDFCBANK", "instrument_kind": "equity", "lots": 25})
        state = strategy.initialize_state()
        fired = self.run_day(strategy, state, 1)
        sig = fired[0][0]
        self.assertEqual(sig.signal_type, "OPEN_GROUP")
        self.assertEqual(sig.legs, [{"symbol": SHARE, "side": "BUY", "quantity": 25}])
        self.assertEqual(sig.metadata["direction"], "BUY")
        self.assertEqual(sig.metadata["trend_rider"]["instrument_kind"], "equity")
        self.assertIn("buying the share NSE:HDFCBANK-EQ", sig.reason)
        self.assertEqual(state["position"]["symbol"], SHARE)
        # Its stop is on the share's own price: 0.5 x the share's average range below the entry.
        adr = sig.metadata["trend_rider"]["adr"]
        out = strategy.on_bar(state, StrategyInput(
            mode="OfflineReplay", timestamp_utc=sig.timestamp_utc, underlying="HDFCBANK",
            spot_price=state["position"]["entry_spot"] - 0.5 * adr, bars={}, metadata={"open_groups": [sig.metadata["group_id"]]}))
        self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
        self.assertEqual(out[0].legs, [{"symbol": SHARE, "side": "SELL", "quantity": 25}])

    def test_a_down_break_is_skipped_not_sold_short(self):
        strategy = TrendRiderV2BEStrategy({"underlying": "HDFCBANK", "instrument_kind": "equity"})
        state = strategy.initialize_state()
        self.assertEqual(self.run_day(strategy, state, -1), [])
        self.assertGreater(state["skipped"].get("equity_long_only", 0), 0)
        self.assertIsNone(state["position"])
        # The same day in options mode buys the put: only the equity run skips it.
        options = TrendRiderV2BEStrategy({"underlying": "HDFCBANK"})
        state = options.initialize_state()
        contracts = {"buy_ce": OptionContract("NSE:HDFCBANK_CE", "HDFCBANK", "2026-03-31", 1690, "CE"),
                     "buy_pe": OptionContract("NSE:HDFCBANK_PE", "HDFCBANK", "2026-03-31", 1710, "PE")}
        frames, level, legs = list(self.bars), 1700, []
        for i in range(10):
            frames += candles(SHARE, self.days[11], "09:15", [(level, level + 0.5, level - 1.5, level - 1)])
            frames[-1].timestamp_utc = (self.days[11].replace(hour=9, minute=15) + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat()
            level -= 1
            groups = [state["position"]["group_id"]] if state["position"] else []
            for s in options.on_bar(state, StrategyInput(mode="OfflineReplay", timestamp_utc=frames[-1].timestamp_utc,
                                                          underlying="HDFCBANK", spot_price=frames[-1].close, contracts=contracts,
                                                          bars={"5m": {"index": list(frames)}}, metadata={"open_groups": groups})):
                legs += s.legs
        self.assertEqual(legs[0]["symbol"], "NSE:HDFCBANK_PE")

    def test_asks_for_no_option_contract(self):
        self.assertEqual(TrendRiderV2BEStrategy.get_contract_requirements({"instrument_kind": "equity"}), [])
        self.assertEqual([r.key for r in TrendRiderV2BEStrategy.get_contract_requirements({})], ["buy_ce", "buy_pe"])
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "ICICIBANK", "instrument_kind": "equity"}).instrument_kind, "equity")

    def test_equity_on_mcx_or_an_index_is_refused(self):
        for params in ({"underlying": "CRUDEOIL", "instrument_kind": "equity"},
                       {"underlying": "NATURALGAS", "instrument_kind": "equity"},
                       {"market": "mcx", "instrument_kind": "equity"},
                       {"underlying": "NIFTY", "instrument_kind": "equity"}):
            with self.subTest(params=params), self.assertRaises(ValueError) as caught:
                TrendRiderV2BEStrategy(params)
            self.assertIn("no share to buy", str(caught.exception))
        # A run whose underlying arrives only with the first call is refused there.
        strategy = TrendRiderV2BEStrategy({"instrument_kind": "equity"})
        frames = mcx_day(self.days[0], 6000, 60)
        with self.assertRaises(ValueError):
            strategy.on_bar(strategy.initialize_state(), StrategyInput(
                mode="OfflineReplay", timestamp_utc=frames[-1].timestamp_utc, underlying="CRUDEOIL", spot_price=6000,
                bars={"5m": {"index": frames}}))


class V2UnchangedTest(unittest.TestCase):
    """With the breakeven step off, on NSE, the new class decides exactly as TrendRiderV2 on the same input."""

    _cache = {}

    @staticmethod
    def sample(seed):
        """Twenty sessions: trend days, reversal days (the trail), and now and then a deep wick (the stop)."""
        rng = random.Random(seed)
        bars, level = [], 20000.0
        for day in weekdays(20):
            drift = rng.choice((-2.5, -1.5, 0, 1.5, 2.5))
            flip = rng.random() < 0.5
            prices = []
            for k in range(75):
                nxt = level + (-drift if flip and k >= 40 else drift) + rng.gauss(0, 4)
                low_wick = abs(rng.gauss(0, 3)) + (rng.choice((40, 70)) if rng.random() < 0.03 else 0)
                high_wick = abs(rng.gauss(0, 3)) + (rng.choice((40, 70)) if rng.random() < 0.03 else 0)
                prices.append((level, max(level, nxt) + high_wick, min(level, nxt) - low_wick, nxt))
                level = nxt
            bars += session(day, prices)
        return bars

    def decisions(self, cls, params, seed):
        """Every signal, candle by candle, with a tick at each extreme of a candle while a position is open."""
        key = (cls.__name__, tuple(sorted(params.items())), seed)
        if key in self._cache:
            return self._cache[key]
        strategy, bars = cls(params), self.sample(seed)
        state, out, open_groups = strategy.initialize_state(), [], set()
        for i, frame in enumerate(bars):
            t0 = datetime.fromisoformat(frame.timestamp_utc)
            window = bars[max(0, i - 899):i + 1]        # twelve sessions, as a live run is handed
            calls = [(frame.close, frame.timestamp_utc)]
            if open_groups:
                calls += [(frame.high, (t0 + timedelta(minutes=6)).isoformat()),
                          (frame.low, (t0 + timedelta(minutes=7)).isoformat())]
            for spot, stamp in calls:
                inp = StrategyInput(mode="OfflineReplay", timestamp_utc=stamp, underlying="NIFTY", spot_price=spot,
                                    contracts=CONTRACTS, bars={"5m": {"index": window}},
                                    metadata={"open_groups": sorted(open_groups)})
                for s in strategy.on_bar(state, inp):
                    gid = s.metadata["group_id"]
                    (open_groups.add if s.signal_type == "OPEN_GROUP" else open_groups.discard)(gid)
                    reason = s.reason.replace("TrendRiderV2BE ", "TrendRiderV2 ").replace("Trend Rider V2 BE: ", "Trend Rider V2: ")
                    out.append((stamp, s.signal_type, gid, s.legs, reason))
        self._cache[key] = (out, dict(state["skipped"]))
        return self._cache[key]

    @staticmethod
    def kind(signal_type, reason):
        if signal_type == "OPEN_GROUP":
            return signal_type
        return next((k for k in ("index stop", "index trail", "breakeven stop", "back across the day's open", "time exit")
                     if k in reason), reason)

    def test_breakeven_off_decides_as_v2(self):
        kinds = set()
        for seed in (1, 2):
            v2, v2_skips = self.decisions(TrendRiderV2Strategy, {}, seed)
            be, be_skips = self.decisions(TrendRiderV2BEStrategy, {"breakeven_adr": 0}, seed)
            self.assertEqual(be, v2)
            self.assertEqual(be_skips, v2_skips)
            kinds |= {self.kind(t, r) for _, t, _, _, r in v2}
        # The sample exercises the entries and every V2 exit, the 15:15 square-off (square_off_ist) included: this
        # replay has no engine square-off, so a position still open at 15:15 is sold by the strategy itself.
        self.assertEqual(kinds, {"OPEN_GROUP", "index stop", "index trail", "back across the day's open", "time exit"})

    def test_the_default_differs_from_v2_first_at_a_breakeven_exit(self):
        v2, _ = self.decisions(TrendRiderV2Strategy, {}, 2)
        be, _ = self.decisions(TrendRiderV2BEStrategy, {}, 2)
        first = next(i for i, (a, b) in enumerate(zip(v2, be)) if a != b)
        self.assertEqual(be[:first], v2[:first])
        self.assertEqual(self.kind(be[first][1], be[first][4]), "breakeven stop")


class RunnerContractsTest(unittest.TestCase):
    """The live runner asks an equity run for no option contract (it used to fall back to the ATM CE/PE)."""

    def test_equity_gets_none_and_options_get_theirs(self):
        import strategies.execution_runner as runner
        from strategies.base_strategy import BaseStrategy

        equity = runner.resolve_contract_requirements(
            TrendRiderV2BEStrategy({"underlying": "HDFCBANK", "instrument_kind": "equity"}), {"instrument_kind": "equity"})
        self.assertEqual(equity, [])
        options = runner.resolve_contract_requirements(TrendRiderV2BEStrategy({"underlying": "HDFCBANK"}), {})
        self.assertEqual([r.key for r in options], ["buy_ce", "buy_pe"])

        class Bare(BaseStrategy):
            @classmethod
            def get_contract_requirements(cls, params=None):
                return []

        # A strategy that declares nothing and is not an equity run keeps the ATM default, as before.
        self.assertEqual([r.key for r in runner.resolve_contract_requirements(Bare(), {"instrument_kind": "equity"})],
                         ["atm_ce", "atm_pe"])


class CatalogTest(unittest.TestCase):
    def test_listed_with_stocks_and_crude(self):
        from strategies.registry import describe_strategy, load_strategy_factories

        self.assertIn("TrendRiderV2BE", load_strategy_factories())
        entry = describe_strategy("TrendRiderV2BE", TrendRiderV2BEStrategy)
        for u in ("NIFTY", "HDFCBANK", "ICICIBANK", "CRUDEOIL"):
            self.assertIn(u, entry["supportedUnderlyings"])
        self.assertEqual(entry["defaultParameters"]["breakeven_adr"], 0.25)
        self.assertEqual(entry["defaultParameters"]["instrument_kind"], "options")
        self.assertEqual([c["key"] for c in entry["contractRequirements"]], ["buy_ce", "buy_pe"])
        self.assertNotRegex(entry["legsSummary"], r"(?i)\bsells?\b")    # the library files it under option buying


class SquareOffTest(BreakevenTest):
    """square_off_ist (20 Nov 2026): live sells at 15:15 on NSE/BSE as every backtest does; MCX keeps its own close."""

    def test_auto_is_1515_on_nse_and_nothing_on_mcx(self):
        self.assertEqual(TrendRiderV2Strategy().index_exits.exit_after_ist, "15:15")
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "NIFTY"}).index_exits.exit_after_ist, "15:15")
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "HDFCBANK"}).index_exits.exit_after_ist, "15:15")
        self.assertIsNone(TrendRiderV2BEStrategy({"underlying": "CRUDEOILM"}).index_exits.exit_after_ist)
        # A run whose market is known only at its first call: NSE's until then, then the market's own.
        late = TrendRiderV2BEStrategy()
        self.assertEqual(late.index_exits.exit_after_ist, "15:15")
        late._use_market("mcx", "CRUDEOIL")
        self.assertIsNone(late.index_exits.exit_after_ist)
        self.assertEqual(late.index_exits.breakeven_adr, 0.25)       # the rest of the rules are kept

    def test_a_set_time_or_none_and_the_runs_own_index_exit_wins(self):
        self.assertEqual(TrendRiderV2Strategy({"square_off_ist": "15:00"}).index_exits.exit_after_ist, "15:00")
        self.assertIsNone(TrendRiderV2Strategy({"square_off_ist": "none"}).index_exits.exit_after_ist)
        self.assertEqual(TrendRiderV2BEStrategy({"underlying": "CRUDEOILM", "square_off_ist": "23:00"})
                         .index_exits.exit_after_ist, "23:00")
        for underlying in ("NIFTY", "CRUDEOILM"):
            s = TrendRiderV2BEStrategy({"underlying": underlying, "index_exits": {"exit_after_ist": "14:45"}})
            self.assertEqual(s.index_exits.exit_after_ist, "14:45", underlying)
        for bad in ("3pm", "25:00", 1515):
            with self.assertRaises(ValueError, msg=bad):
                TrendRiderV2Strategy({"square_off_ist": bad})

    def test_sells_on_the_first_tick_at_1515(self):
        for cls in (TrendRiderV2Strategy, TrendRiderV2BEStrategy):
            with self.subTest(strategy=cls.__name__):
                self.setUp()
                strategy = cls()
                state = strategy.initialize_state()
                _, frame = self.until_entry(strategy, state)
                spot = frame.close + 1                     # a small move: no stop, trail or breakeven
                at = lambda hh, mm, ss: self.days[11].replace(hour=hh, minute=mm, second=ss).astimezone(timezone.utc).isoformat()
                self.assertEqual(strategy.on_bar(state, self.inp(state, spot, at(15, 14, 59))), [])
                self.assertIsNotNone(state["position"])
                out = strategy.on_bar(state, self.inp(state, spot, at(15, 15, 0)))
                self.assertEqual(out[0].signal_type, "CLOSE_GROUP")
                self.assertIn("time exit: 15:15 IST is at or after 15:15", out[0].reason)
                self.assertIsNone(state["position"])

    def test_off_with_none_holds_to_the_close(self):
        strategy = TrendRiderV2Strategy({"square_off_ist": "none"})
        state = strategy.initialize_state()
        _, frame = self.until_entry(strategy, state)
        stamp = self.days[11].replace(hour=15, minute=29, second=0).astimezone(timezone.utc).isoformat()
        self.assertEqual(strategy.on_bar(state, self.inp(state, frame.close + 1, stamp)), [])
        self.assertIsNotNone(state["position"])


if __name__ == "__main__":
    unittest.main()
