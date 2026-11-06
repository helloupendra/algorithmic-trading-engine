"""
ChainFlowBuyV2: which of the original's entries it takes, what a dropped entry leaves behind, how it holds
one position, when it exits, and that a replay through the engine books what it says.

The candles and chains are tests/test_chain_flow_buy.py's (a NIFTY trend under way from 09:15, a chain of
50-point strikes around it), so every entry here is one the original's own tests already pin. With every
change off, V2 signals exactly what the original signals.
"""

import io
import json
import unittest
from contextlib import redirect_stdout
from dataclasses import replace
from datetime import datetime, time, timedelta, timezone
from typing import Any, Dict, List, Optional

import _bootstrap  # noqa: F401

from backtest.engine import run_backtest
from backtest.timeutil import iso_utc
from strategies.base_strategy import BarFrame, OptionContract, StrategyInput
from strategies.directional.chain_flow_buy import ChainFlowBuyStrategy
from strategies.directional.chain_flow_buy_v2 import ChainFlowBuyV2Strategy
from test_chain_flow_buy import SESSION, chain, index_bars
from test_engine import DAY1, EXPIRY, SPOT, FakeApi, contract_symbol, quiet, run_row, session_bars

#: Every V2 change off: the original's strike search, no contract or flow-age check, no position rule.
EVERY_CHANGE_OFF = {"delta_search_strikes": 3, "require_same_contract": False, "flow_max_lookback_minutes": 0,
                    "one_position": False}
#: V2's own exits off, to look at the position rules alone.
NO_EXITS = {"exit_on_trend_break": "off", "exit_on_flow_flip": False}
CHAIN_EXPIRY = "2099-01-01"   # test_chain_flow_buy's chain
IST = timezone(timedelta(hours=5, minutes=30))


def strikes_around(spot: float) -> List[int]:
    """Fifty-point strikes from 400 below to 400 above the ATM strike."""
    atm = int(round(spot / 50) * 50)
    return list(range(atm - 400, atm + 450, 50))


def chain_at(spot: float, call_oi: float, put_oi: float, iv: float = 14.0, captured: Optional[datetime] = None,
             build_up: str = "LongBuildUp", expiry: str = CHAIN_EXPIRY) -> Dict[str, Any]:
    """test_chain_flow_buy's chain, centred on `spot` so the ATM strike is always listed."""
    built = chain(spot, call_oi, put_oi, iv=iv, captured=captured, build_up=build_up, expiry=expiry)
    rows = []
    for k in strikes_around(spot):
        call_delta = max(0.05, min(0.95, 0.5 + (spot - k) / 400.0))
        rows.append({"strikePrice": k,
                     "call": {"symbol": f"NSE:NIFTY{k}CE", "openInterest": call_oi, "delta": round(call_delta, 3),
                              "lastTradedPrice": 120.0, "buildUp": build_up},
                     "put": {"symbol": f"NSE:NIFTY{k}PE", "openInterest": put_oi, "delta": round(call_delta - 1, 3),
                             "lastTradedPrice": 120.0, "buildUp": build_up}})
    built["strikes"] = rows
    return built


def option_candles(count: int, surge_at: Optional[int]) -> List[BarFrame]:
    """Option candles on the index candles' times: 1,000 volume, 3,000 on `surge_at`."""
    return [BarFrame(symbol="OPT", resolution="5m",
                     timestamp_utc=(SESSION + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat(),
                     open=100, high=110, low=95, close=105, volume=3000.0 if i == surge_at else 1000.0)
            for i in range(count)]


class Desk:
    """
    One strategy over a trend already under way. Live, the last candle is still forming and the one before
    it is the signal candle; a replay is handed the closed ones only.
    """

    def __init__(self, direction: int = 1, params: Optional[Dict[str, Any]] = None, original: bool = False):
        cls = ChainFlowBuyStrategy if original else ChainFlowBuyV2Strategy
        self.strategy = cls({"lots": 2, **(params or {})})
        self.state = self.strategy.initialize_state()
        self.direction = direction
        self.bars = index_bars(40, direction=direction)
        self.chain: Optional[Dict[str, Any]] = None
        self.strategy._fetch_chain = lambda underlying, as_of=None: self.chain
        self.strike: Optional[float] = None
        self.expiry = CHAIN_EXPIRY
        self.open_groups: Optional[List[str]] = None
        self.log = ""

    @property
    def close(self) -> float:
        return self.bars[-2].close

    @property
    def decided_at(self) -> datetime:
        return datetime.fromisoformat(self.bars[-2].timestamp_utc) + timedelta(minutes=5)

    def seed(self, call_oi: float, put_oi: float, iv_low: float = 13.5, minutes_before: int = 20) -> None:
        """An OI sample `minutes_before` the decision, and the session's IV low so far."""
        then = {str(float(k)): [float(call_oi), float(put_oi)] for k in strikes_around(self.close)}
        self.state["session_date"] = SESSION.strftime("%Y-%m-%d")
        self.state["oi_samples"] = [[(self.decided_at - timedelta(minutes=minutes_before)).timestamp(), then]]
        self.state["iv_low"] = iv_low

    def confirm(self, call_oi: float = 101_000, put_oi: float = 106_000, **kwargs) -> None:
        """A chain at the signal candle; the defaults back the trend of `direction`."""
        if self.direction < 0 and (call_oi, put_oi) == (101_000, 106_000):
            call_oi, put_oi = 106_000, 101_000
        self.chain = chain_at(self.close, call_oi=call_oi, put_oi=put_oi, **kwargs)

    def advance(self, close: Optional[float] = None) -> None:
        """The forming candle closes (at `close` when given) and a new one starts forming."""
        bar = self.bars[-1]
        if close is not None:
            bar = replace(bar, close=close, high=max(bar.open, close) + 2, low=min(bar.open, close) - 2)
            self.bars[-1] = bar
        start = datetime.fromisoformat(bar.timestamp_utc) + timedelta(minutes=5)
        self.bars.append(replace(bar, timestamp_utc=start.isoformat(), open=bar.close, high=bar.close + 2,
                                 low=bar.close - 2))

    def run(self, mode: str = "LivePaper", surge: bool = True, contracts: bool = True):
        bars = self.bars if mode == "LivePaper" else self.bars[:-1]
        signal_at = len(self.bars) - 2
        k = self.strike if self.strike is not None else round(self.close / 50) * 50
        metadata: Dict[str, Any] = {"source": "live-api"}
        if self.open_groups is not None:
            metadata["open_groups"] = list(self.open_groups)
        inp = StrategyInput(
            mode=mode, timestamp_utc=datetime.now(timezone.utc).isoformat(), underlying="NIFTY",
            spot_price=self.close, atm_strike=k, strike_step=50, lot_size=65,
            contracts={"atm_ce": OptionContract(f"NSE:NIFTY{k}CE", "NIFTY", self.expiry, k, "CE"),
                       "atm_pe": OptionContract(f"NSE:NIFTY{k}PE", "NIFTY", self.expiry, k, "PE")} if contracts else {},
            bars={"5m": {
                "index": bars,
                "atm_ce": option_candles(len(bars), signal_at if surge and self.direction > 0 else None),
                "atm_pe": option_candles(len(bars), signal_at if surge and self.direction < 0 else None),
            }},
            metadata=metadata,
        )
        out = io.StringIO()
        with redirect_stdout(out):
            signals = self.strategy.on_bar(self.state, inp)
        self.log = out.getvalue()
        return signals

    def hold(self, side: str, group: str = "held", since: Optional[str] = None) -> None:
        """A position V2 already holds, opened on an earlier candle."""
        k = round(self.close / 50) * 50
        self.state["position"] = {"group_id": group, "side": side, "symbol": f"NSE:NIFTY{k}{side}",
                                  "since": since or self.bars[-3].timestamp_utc, "session": SESSION.strftime("%Y-%m-%d")}


def kinds(signals) -> List[str]:
    return [s.signal_type for s in signals]


class EntryTests(unittest.TestCase):
    def test_it_buys_the_atm_contract_whose_volume_was_checked(self):
        desk = Desk()
        desk.seed(100_000, 100_000)
        desk.confirm()

        signals = desk.run()

        self.assertEqual(kinds(signals), ["OPEN_GROUP"], desk.log)
        atm = round(desk.close / 50) * 50
        self.assertEqual(signals[0].legs[0]["symbol"], f"NSE:NIFTY{atm}CE")
        self.assertEqual(signals[0].metadata["strike"], atm)
        self.assertEqual(signals[0].metadata["v2"], {"flow_lookback_s": 1200, "atm_contract": f"NSE:NIFTY{atm}CE"})
        self.assertEqual(desk.state["position"]["side"], "CE")
        self.assertEqual(desk.state["position"]["group_id"], signals[0].metadata["group_id"])

    def test_a_strike_other_than_the_atm_contract_is_refused_and_leaves_no_trace(self):
        desk = Desk()
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.strike = round(desk.close / 50) * 50 - 50      # the volume read was another strike's

        self.assertEqual(desk.run(), [])
        self.assertEqual(desk.state["dropped"]["contract_mismatch"], 1)
        self.assertIn("entry dropped: contract_mismatch", desk.log)
        # The original's own bookkeeping is as if it had not signalled.
        self.assertEqual(desk.state["trades_this_session"], 0)
        self.assertIsNone(desk.state["disarmed_side"])
        self.assertIsNone(desk.state["position"])

    def test_another_expiry_is_refused(self):
        desk = Desk()
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.expiry = "2099-01-08"

        self.assertEqual(desk.run(), [])
        self.assertEqual(desk.state["dropped"]["contract_mismatch"], 1)

    def test_a_chain_without_the_atm_strike_makes_the_original_buy_far_from_the_money(self):
        # near_money counts listed rows, not strike distance: with 23,750 at the money and a chain listing
        # 24,000 upwards, "three strikes either side" is 24,000 to 24,150, and the original buys there.
        original, fixed = Desk(direction=-1, original=True), Desk(direction=-1)
        for desk in (original, fixed):
            desk.seed(100_000, 100_000)
            desk.chain = chain(desk.close, call_oi=106_000, put_oi=101_000)   # strikes 24,000-24,600 only

        bought = original.run()
        self.assertEqual(kinds(bought), ["OPEN_GROUP"])
        self.assertEqual(bought[0].metadata["strike"], 24000)                 # five strikes from 23,750
        self.assertEqual(fixed.run(), [])
        self.assertEqual(fixed.state["dropped"]["contract_mismatch"], 1)

    def test_a_flow_measured_against_an_old_sample_is_refused(self):
        original, fixed = Desk(original=True), Desk()
        for desk in (original, fixed):
            desk.seed(100_000, 100_000, minutes_before=60)   # an hour-long chain outage
            desk.confirm()

        self.assertEqual(kinds(original.run()), ["OPEN_GROUP"])
        self.assertEqual(fixed.run(), [])
        self.assertEqual(fixed.state["dropped"]["flow_too_old"], 1)

    def test_no_limit_on_the_flow_window_takes_it(self):
        desk = Desk(params={"flow_max_lookback_minutes": 0})
        desk.seed(100_000, 100_000, minutes_before=60)
        desk.confirm()

        self.assertEqual(kinds(desk.run()), ["OPEN_GROUP"])

    def test_a_replay_refuses_a_chain_captured_after_its_decision(self):
        original, fixed = Desk(original=True), Desk()
        for desk in (original, fixed):
            desk.seed(100_000, 100_000)
            desk.confirm(captured=desk.decided_at + timedelta(seconds=60))   # inside the original's 180 s

        self.assertEqual(kinds(original.run(mode="OfflineReplay")), ["OPEN_GROUP"])
        self.assertEqual(fixed.run(mode="OfflineReplay"), [])
        self.assertEqual(fixed.state["dropped"]["chain_ahead"], 1)

    def test_a_replay_on_the_chain_of_that_very_minute_trades(self):
        desk = Desk()
        desk.seed(100_000, 100_000)
        desk.confirm(captured=desk.decided_at)

        self.assertEqual(kinds(desk.run(mode="OfflineReplay")), ["OPEN_GROUP"])


class PositionTests(unittest.TestCase):
    def test_one_position_at_a_time(self):
        desk = Desk()
        desk.seed(100_000, 100_000)
        desk.confirm()
        first = desk.run()
        # The original re-arms the side once its EMA stack breaks and comes back; here, at once.
        desk.state["disarmed_side"] = None
        desk.advance()
        desk.confirm()

        self.assertEqual(desk.run(), [], desk.log)
        self.assertEqual(desk.state["dropped"]["same_side_held"], 1)
        self.assertEqual(desk.state["position"]["group_id"], first[0].metadata["group_id"])
        self.assertEqual(desk.state["trades_this_session"], 1)

    def test_an_opposite_entry_reverses_the_position(self):
        desk = Desk(direction=-1, params=NO_EXITS)
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.hold("CE")

        signals = desk.run()

        self.assertEqual(kinds(signals), ["CLOSE_GROUP", "OPEN_GROUP"], desk.log)
        self.assertEqual(signals[0].metadata["group_id"], "held")
        self.assertEqual(signals[0].legs[0]["side"], "SELL")
        self.assertIn("opposite entry, PE", signals[0].reason)
        self.assertTrue(signals[1].legs[0]["symbol"].endswith("PE"))
        self.assertEqual(desk.state["position"]["side"], "PE")

    def test_on_opposite_close_only_closes(self):
        desk = Desk(direction=-1, params={**NO_EXITS, "on_opposite": "close"})
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.hold("CE")

        self.assertEqual(kinds(desk.run()), ["CLOSE_GROUP"])
        self.assertIsNone(desk.state["position"])
        self.assertEqual(desk.state["trades_this_session"], 0)

    def test_on_opposite_ignore_keeps_the_position(self):
        desk = Desk(direction=-1, params={**NO_EXITS, "on_opposite": "ignore"})
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.hold("CE")

        self.assertEqual(desk.run(), [])
        self.assertEqual(desk.state["position"]["group_id"], "held")
        self.assertEqual(desk.state["dropped"]["opposite_ignored"], 1)

    def test_a_position_the_replay_closed_is_forgotten(self):
        desk = Desk(params=NO_EXITS)
        desk.seed(100_000, 100_000)
        desk.confirm(captured=desk.decided_at)
        desk.hold("CE")
        desk.open_groups = []          # a run rule or the square-off closed it

        signals = desk.run(mode="OfflineReplay")

        self.assertEqual(kinds(signals), ["OPEN_GROUP"])     # free to enter again
        self.assertNotEqual(desk.state["position"]["group_id"], "held")

    def test_live_forgets_a_position_on_a_new_session(self):
        desk = Desk()
        desk.hold("CE")
        desk.state["position"]["session"] = "2000-01-01"

        live = StrategyInput(mode="LivePaper", timestamp_utc="", underlying="NIFTY", spot_price=0.0)
        desk.strategy._sync(desk.state, live, {"session": SESSION.strftime("%Y-%m-%d")})

        self.assertIsNone(desk.state["position"])

    def test_without_one_position_it_stacks_as_the_original(self):
        desk = Desk(params={"one_position": False})
        desk.seed(100_000, 100_000)
        desk.confirm()
        desk.run()
        desk.state["disarmed_side"] = None
        desk.advance()
        desk.confirm()

        self.assertEqual(kinds(desk.run()), ["OPEN_GROUP"])
        self.assertIsNone(desk.state["position"])


class ExitTests(unittest.TestCase):
    def entered(self, **params) -> Desk:
        desk = Desk(params=params or None)
        desk.seed(100_000, 100_000)
        desk.confirm()
        self.assertEqual(kinds(desk.run()), ["OPEN_GROUP"], desk.log)
        return desk

    def test_a_close_back_under_the_slow_ema_closes_the_call(self):
        desk = self.entered()
        desk.advance(close=desk.bars[-1].open - 150)
        desk.confirm()

        signals = desk.run()

        self.assertEqual(kinds(signals), ["CLOSE_GROUP"], desk.log)
        self.assertIn("trend broke", signals[0].reason)
        self.assertIn("slow EMA", signals[0].reason)
        self.assertEqual(signals[0].legs[0]["side"], "SELL")
        self.assertEqual(signals[0].legs[0]["quantity"], 2)
        self.assertTrue(signals[0].legs[0]["symbol"].endswith("CE"))
        self.assertIsNone(desk.state["position"])

    def test_the_flow_turning_against_the_call_closes_it(self):
        desk = self.entered()
        desk.advance()                                  # the trend goes on
        desk.confirm(call_oi=110_000, put_oi=101_000)   # but writers now add calls

        signals = desk.run()

        self.assertEqual(kinds(signals), ["CLOSE_GROUP"], desk.log)
        self.assertIn("OI flow turned against the CE", signals[0].reason)

    def test_with_its_exits_off_it_holds(self):
        desk = self.entered(**NO_EXITS)
        desk.advance(close=desk.bars[-1].open - 150)
        desk.confirm(call_oi=110_000, put_oi=101_000)

        self.assertEqual(desk.run(), [])
        self.assertIsNotNone(desk.state["position"])

    def test_nothing_happens_twice_on_one_candle(self):
        desk = self.entered()

        self.assertEqual(desk.run(), [])                # the same candle, the next tick
        self.assertIsNotNone(desk.state["position"])


class OriginalTests(unittest.TestCase):
    def test_with_every_change_off_it_signals_what_the_original_signals(self):
        def scenario(desk: Desk, step: int) -> None:
            if step == 0:
                desk.seed(100_000, 100_000)
                desk.confirm()
            elif step == 1:                       # the original's own re-entry on a re-armed side
                desk.state["disarmed_side"] = None
                desk.advance()
                desk.confirm()
            else:                                 # a drop under the EMAs: the original does nothing
                desk.advance(close=desk.bars[-1].open - 150)
                desk.confirm()

        def comparable(signals) -> List[tuple]:
            out = []
            for s in signals:
                meta = {k: v for k, v in s.metadata.items() if k not in ("group_id", "v2")}
                out.append((s.signal_type, json.dumps(s.legs, sort_keys=True), json.dumps(meta, sort_keys=True)))
            return out

        for direction in (1, -1):
            original, fixed = Desk(direction, original=True), Desk(direction, params=EVERY_CHANGE_OFF)
            for step in range(3):
                for desk in (original, fixed):
                    scenario(desk, step)
                with self.subTest(direction=direction, step=step):
                    self.assertEqual(comparable(original.run()), comparable(fixed.run()))


# --- a replay through the engine ---------------------------------------------------

def spot_at(n: int) -> float:
    """BANKNIFTY rising 1.5 a candle from 57,560 for 40 candles, then falling 4 a candle."""
    return 57560 + 1.5 * min(n, 40) - 4.0 * max(0, n - 40)


def engine_chain(at: datetime, spot: float) -> Dict[str, Any]:
    """The chain at `at`: put OI added through the session (writers backing the rise), calls flat."""
    minutes = (at - datetime.combine(DAY1, time(9, 15), tzinfo=IST)).total_seconds() / 60
    atm = round(spot / 100) * 100
    strikes = []
    for k in range(57200, 58100, 100):
        delta = max(0.05, min(0.95, 0.5 + (spot - k) / 800.0))
        strikes.append({"strikePrice": k,
                        "call": {"symbol": contract_symbol(k, "CE"), "openInterest": 100_000.0, "delta": round(delta, 3),
                                 "lastTradedPrice": 120.0, "buildUp": "LongBuildUp"},
                        "put": {"symbol": contract_symbol(k, "PE"), "openInterest": 100_000.0 + 500 * minutes,
                                "delta": round(delta - 1, 3), "lastTradedPrice": 120.0, "buildUp": "LongBuildUp"}})
    return {"underlying": "BANKNIFTY", "expiryDate": EXPIRY, "spotPrice": spot, "atTheMoneyStrike": atm,
            "strikes": strikes,
            "header": {"snapshotCapturedUtc": at.astimezone(timezone.utc).isoformat(), "spot": {"lastPrice": spot},
                       "atTheMoneyStrike": atm, "atTheMoneyIv": 14.0, "liveLegs": 20}}


class ReplayTests(unittest.TestCase):
    SURGE_AT = 30          # the 11:45 candle: the call's volume surges

    def test_a_replay_through_the_engine_enters_once_and_exits_when_the_trend_breaks(self):
        bars = session_bars(DAY1)
        closes = {bar_start: spot_at(i) for i, bar_start in enumerate(bars)}

        def rows(symbol: str, price, volume) -> List[Dict[str, Any]]:
            out = []
            for i, start in enumerate(bars):
                close, prev = price(i), price(i - 1) if i else price(i)
                out.append({"symbol": symbol, "resolution": "5", "timestampUtc": iso_utc(start), "open": prev,
                            "high": max(prev, close) + 1, "low": min(prev, close) - 1, "close": close,
                            "volume": volume(i)})
            return out

        candles = {(SPOT, "5"): rows(SPOT, spot_at, lambda i: 0)}
        for k in (57500, 57600):
            candles[(contract_symbol(k, "CE"), "5")] = rows(
                contract_symbol(k, "CE"), lambda i: 120.0 + (spot_at(i) - 57600) * 0.5,
                lambda i, k=k: 3000.0 if (k == 57600 and i == self.SURGE_AT) else 1000.0)
            candles[(contract_symbol(k, "PE"), "5")] = rows(
                contract_symbol(k, "PE"), lambda i: 120.0 - (spot_at(i) - 57600) * 0.5, lambda i: 1000.0)
        api = FakeApi(candles)

        def factory(params=None):
            strategy = ChainFlowBuyV2Strategy(params or {})
            index_at = {bar_start + timedelta(minutes=5): close for bar_start, close in closes.items()}
            strategy._fetch_chain = lambda underlying, as_of=None: engine_chain(as_of, index_at[as_of])
            return strategy

        row = run_row(DAY1, DAY1)
        row["strategyName"] = "ChainFlowBuyV2"
        with redirect_stdout(io.StringIO()):
            outcome = run_backtest(api, row, factory, log=quiet, progress_interval=0.0)

        self.assertEqual(outcome.status, "Completed")
        kinds_ = [(s["signalType"], s["groupId"]) for s in api.signals]
        self.assertEqual([k for k, _ in kinds_], ["OPEN_GROUP", "CLOSE_GROUP"], kinds_)
        self.assertEqual(kinds_[0][1], kinds_[1][1])
        opened, closed = api.signals
        self.assertEqual(opened["legs"][0]["symbol"], contract_symbol(57600, "CE"))
        self.assertEqual(opened["timestampUtc"][:16], iso_utc(bars[self.SURGE_AT])[:16])
        self.assertIn("trend broke", json.loads(closed["metadataJson"]).get("reason", ""))


if __name__ == "__main__":
    unittest.main()
