"""
ChainFlowBuy: option buying confirmed by the option chain.

Every gate is pinned with plain numbers: the trend candle, the OI flow over the
same strikes, the option's volume surge, the IV ceiling, the delta pick, the
build-up, the data check that blocks trading on a stale or foreign chain, and
the one-entry-per-setup rule.
"""

import io
import unittest
from contextlib import redirect_stdout
from datetime import datetime, timedelta, timezone

from strategies.base_strategy import BarFrame, StrategyInput
from strategies.directional.chain_flow_buy import (
    ChainFlowBuyStrategy,
    data_check,
    oi_flow,
    pick_leg,
    volume_surge,
)

IST = timezone(timedelta(hours=5, minutes=30))
SESSION = datetime.now(IST).replace(hour=9, minute=15, second=0, microsecond=0)


def index_bars(count, start=24000.0, step=6.0, direction=1):
    """`count` 5-minute candles from 09:15 IST, each closing `step` points further in `direction`."""
    bars = []
    price = start
    for i in range(count):
        open_ = price
        close = price + direction * step
        bars.append(BarFrame(
            symbol="NSE:NIFTY50-INDEX", resolution="5m",
            timestamp_utc=(SESSION + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat(),
            open=open_, high=max(open_, close) + 2, low=min(open_, close) - 2, close=close, volume=0,
        ))
        price = close
    return bars


def option_bars(count, surge=True):
    """Option candles with a flat 1,000 volume, and 3,000 on the signal bar (the second last) when `surge`."""
    bars = []
    for i in range(count):
        volume = 3000.0 if (surge and i == count - 2) else 1000.0
        bars.append(BarFrame(
            symbol="OPT", resolution="5m",
            timestamp_utc=(SESSION + timedelta(minutes=5 * i)).astimezone(timezone.utc).isoformat(),
            open=100, high=110, low=95, close=105, volume=volume,
        ))
    return bars


def leg(symbol, oi, delta, ltp=120.0, build_up="LongBuildUp"):
    return {"symbol": symbol, "openInterest": oi, "delta": delta, "lastTradedPrice": ltp, "buildUp": build_up}


def chain(spot, call_oi, put_oi, iv=14.0, captured=None, build_up="LongBuildUp", expiry="2099-01-01"):
    """A chain around 24,300 with 50-point strikes; `call_oi`/`put_oi` apply to every strike."""
    strikes = []
    for k in range(24000, 24650, 50):
        call_delta = max(0.05, min(0.95, 0.5 + (spot - k) / 400.0))
        strikes.append({
            "strikePrice": k,
            "call": leg(f"NSE:NIFTY{k}CE", call_oi, round(call_delta, 3), build_up=build_up),
            "put": leg(f"NSE:NIFTY{k}PE", put_oi, round(call_delta - 1, 3), build_up=build_up),
        })
    return {
        "underlying": "NIFTY",
        "expiryDate": expiry,
        "spotPrice": spot,
        "atTheMoneyStrike": round(spot / 50) * 50,
        "strikes": strikes,
        "header": {
            "snapshotCapturedUtc": (captured or datetime.now(timezone.utc)).isoformat(),
            "spot": {"lastPrice": spot},
            "atTheMoneyStrike": round(spot / 50) * 50,
            "atTheMoneyIv": iv,
            "liveLegs": 20,
        },
    }


class Harness:
    """A strategy whose chain fetch returns what the test says, and a trend already under way."""

    def __init__(self, direction=1, params=None):
        self.strategy = ChainFlowBuyStrategy({"lots": 2, **(params or {})})
        self.state = self.strategy.initialize_state()
        self.direction = direction
        self.bars = index_bars(40, direction=direction)  # signal bar = 38th, decided at 12:25 IST
        self.close = self.bars[-2].close
        self.chain = None
        self.strategy._fetch_chain = lambda underlying: self.chain
        self.decided_at = datetime.fromisoformat(self.bars[-2].timestamp_utc) + timedelta(minutes=5)

    def seed(self, call_oi, put_oi, iv_low=None):
        """An OI sample 20 minutes before the decision, and the session's IV low so far."""
        then = {str(float(k)): [float(call_oi), float(put_oi)] for k in range(24000, 24650, 50)}
        self.state["session_date"] = SESSION.strftime("%Y-%m-%d")
        self.state["oi_samples"] = [[(self.decided_at - timedelta(minutes=20)).timestamp(), then]]
        if iv_low is not None:
            self.state["iv_low"] = iv_low

    def run(self, mode="LivePaper", surge=True, metadata=None):
        inp = StrategyInput(
            mode=mode,
            timestamp_utc=datetime.now(timezone.utc).isoformat(),
            underlying="NIFTY",
            spot_price=self.close,
            atm_strike=round(self.close / 50) * 50,
            strike_step=50,
            lot_size=65,
            bars={"5m": {
                "index": self.bars,
                "atm_ce": option_bars(40, surge=surge and self.direction == 1),
                "atm_pe": option_bars(40, surge=surge and self.direction == -1),
            }},
            metadata=metadata or {"source": "live-api"},
        )
        out = io.StringIO()
        with redirect_stdout(out):
            signals = self.strategy.on_bar(self.state, inp)
        self.log = out.getvalue()
        return signals


class EntryTests(unittest.TestCase):
    def test_bullish_trend_confirmed_by_the_chain_buys_the_call_nearest_half_delta(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        # Puts added 6,000 a strike, calls 1,000: writers backing the move up.
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000, iv=14.0)

        signals = h.run()

        self.assertEqual(len(signals), 1, h.log)
        signal = signals[0]
        self.assertEqual("OPEN_GROUP", signal.signal_type)
        self.assertEqual("BUY", signal.legs[0]["side"])
        self.assertEqual(2, signal.legs[0]["quantity"])
        self.assertTrue(signal.legs[0]["symbol"].endswith("CE"))
        # The strike whose delta is closest to 0.5 is the one nearest the index.
        self.assertEqual(round(h.close / 50) * 50, signal.metadata["strike"])
        self.assertEqual("CE", signal.metadata["option_side"])
        self.assertGreater(signal.metadata["oi_flow_pct"], 1.0)
        self.assertIn("DATA NIFTY", h.log)
        self.assertIn("chain OK", h.log)

    def test_bearish_trend_buys_the_put(self):
        h = Harness(direction=-1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=106_000, put_oi=101_000, iv=14.0)

        signals = h.run()

        self.assertEqual(len(signals), 1, h.log)
        self.assertTrue(signals[0].legs[0]["symbol"].endswith("PE"))
        self.assertLess(signals[0].metadata["oi_flow_pct"], -1.0)

    def test_oi_flow_against_the_trend_blocks_the_entry(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=106_000, put_oi=101_000)
        self.assertEqual([], h.run())

    def test_an_iv_spike_blocks_a_buy(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=10.0)
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000, iv=13.0)  # 30% above the low
        self.assertEqual([], h.run())
        self.assertIn("above the session low", h.log)

    def test_no_volume_surge_on_the_option_blocks_the_entry(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000)
        self.assertEqual([], h.run(surge=False))

    def test_premium_not_being_bid_up_blocks_the_entry(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000, build_up="ShortBuildUp")
        self.assertEqual([], h.run())
        self.assertIn("build-up is ShortBuildUp", h.log)

    def test_a_stale_chain_blocks_trading_and_says_so(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000,
                        captured=datetime.now(timezone.utc) - timedelta(minutes=10))
        self.assertEqual([], h.run())
        self.assertIn("BLOCKED: chain is", h.log)

    def test_one_entry_per_setup_and_nothing_outside_live_paper(self):
        h = Harness(direction=1)
        h.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        h.chain = chain(h.close, call_oi=101_000, put_oi=106_000)
        self.assertEqual(1, len(h.run()))

        # The next candle, same trend, same confirmation: no second position.
        h.bars = index_bars(41, direction=1)
        h.close = h.bars[-2].close
        h.chain = chain(h.close, call_oi=102_000, put_oi=112_000)
        self.assertEqual([], h.run())

        replay = Harness(direction=1)
        replay.seed(call_oi=100_000, put_oi=100_000, iv_low=13.5)
        replay.chain = chain(replay.close, call_oi=101_000, put_oi=106_000)
        self.assertEqual([], replay.run(mode="OfflineReplay"))
        self.assertEqual([], replay.run(metadata={"source": "warmup"}))


class RuleTests(unittest.TestCase):
    def test_oi_flow_counts_only_strikes_seen_both_times(self):
        now = {"24000.0": (110.0, 150.0), "24050.0": (100.0, 100.0)}
        then = {"24000.0": (100.0, 100.0)}  # 24050 entered the window since
        flow = oi_flow(now, then)
        self.assertEqual(1, flow["strikes"])
        self.assertEqual(40.0, flow["net"])  # puts +50, calls +10
        self.assertAlmostEqual(100 * 40 / 260, flow["net_pct"])
        self.assertIsNone(oi_flow({"1": (None, 5.0)}, {"1": (1.0, 1.0)}))

    def test_leg_pick_by_delta_skips_unpriced_and_cheap_legs(self):
        c = chain(24310, 1, 1)
        picked = pick_leg(c, 24300, "call", 0.5, 3, 5.0)
        self.assertEqual(24300, picked["strikePrice"])
        c["strikes"][6]["call"]["lastTradedPrice"] = 2.0  # 24300 too cheap
        self.assertNotEqual(24300, pick_leg(c, 24300, "call", 0.5, 3, 5.0)["strikePrice"])

    def test_data_check_rejects_a_chain_from_another_market(self):
        ok, why, facts = data_check(chain(25000, 1, 1), 24300.0, datetime.now(timezone.utc), 180, 0.35)
        self.assertFalse(ok)
        self.assertIn("from the index close", why)
        ok, _, facts = data_check(chain(24310, 1, 1), 24300.0, datetime.now(timezone.utc), 180, 0.35)
        self.assertTrue(ok)
        self.assertEqual(14.0, facts["atm_iv"])
        self.assertFalse(data_check(None, 24300.0, datetime.now(timezone.utc), 180, 0.35)[0])

    def test_volume_surge(self):
        self.assertEqual((3000.0, 1000.0), volume_surge(option_bars(10), 1.5, 6))
        self.assertIsNone(volume_surge(option_bars(10, surge=False), 1.5, 6))
        self.assertIsNone(volume_surge(option_bars(5), 1.5, 6))


if __name__ == "__main__":
    unittest.main()
