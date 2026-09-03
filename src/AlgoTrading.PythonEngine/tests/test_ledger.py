"""Paper ledger math (backtest/ledger.py)."""

import unittest

import _bootstrap  # noqa: F401

from backtest.ledger import PaperLedger

T0 = "2026-08-19T03:45:00Z"
T1 = "2026-08-19T03:50:00Z"
T2 = "2026-08-19T03:55:00Z"
CE = "NSE:BANKNIFTY26SEP57600CE"
PE = "NSE:BANKNIFTY26SEP57600PE"


def leg(symbol, side, lots, price):
    return {"symbol": symbol, "side": side, "quantity": lots, "price": price}


class LedgerTests(unittest.TestCase):
    def test_long_pnl_is_lots_times_lot_size(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 2, 100.0)], T0)
        self.assertEqual(ledger.open_symbols(), [CE])
        self.assertAlmostEqual(ledger.used_capital(), 100.0 * 2 * 30)

        ledger.mark({CE: 110.0}, T1)
        self.assertAlmostEqual(ledger.unrealized_pnl(), 10.0 * 2 * 30)
        self.assertAlmostEqual(ledger.total_pnl(), 600.0)

        result = ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 2, 115.0)], T2, "take profit")
        self.assertTrue(result.applied)
        self.assertEqual(len(result.closed), 1)
        self.assertAlmostEqual(ledger.realized_pnl(), 15.0 * 2 * 30)
        self.assertAlmostEqual(ledger.unrealized_pnl(), 0.0)
        self.assertFalse(ledger.has_open())
        self.assertEqual(ledger.trades, 1)
        closed = ledger.closed[0]
        self.assertEqual(closed.status, "Closed")
        self.assertEqual(closed.exit_price, 115.0)
        self.assertEqual(closed.exit_reason, "take profit")
        self.assertEqual(closed.closed_utc, T2)

    def test_short_pnl_sign(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(PE, "SELL", 1, 200.0)], T0)
        ledger.mark({PE: 180.0}, T1)
        self.assertAlmostEqual(ledger.unrealized_pnl(), 20.0 * 30)   # premium fell: short profits
        ledger.mark({PE: 230.0}, T1)
        self.assertAlmostEqual(ledger.unrealized_pnl(), -30.0 * 30)
        ledger.apply("CLOSE_GROUP", "g1", [leg(PE, "BUY", 1, 230.0)], T2)
        self.assertAlmostEqual(ledger.realized_pnl(), -900.0)

    def test_averaging_same_direction(self):
        ledger = PaperLedger(lot_size=10)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 1, 100.0)], T0)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 3, 120.0)], T1)
        pos = ledger.position("g1", CE)
        self.assertEqual(pos.lots, 4)
        self.assertAlmostEqual(pos.avg_price, 115.0)
        ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 4, 125.0)], T2)
        self.assertAlmostEqual(ledger.realized_pnl(), 10.0 * 4 * 10)

    def test_close_is_reduce_only(self):
        ledger = PaperLedger(lot_size=30)
        # Nothing open: the leg is ignored and counted, nothing is filled.
        result = ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 1, 100.0)], T0)
        self.assertFalse(result.applied)
        self.assertEqual(result.ignored[0][1], "no open position")
        self.assertEqual(ledger.ignored_legs, 1)

        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 2, 100.0)], T0)
        # Over-sized close is capped at the open lots; no reverse position appears.
        result = ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 5, 110.0)], T1)
        self.assertEqual(result.legs[0]["quantity"], 2)
        self.assertFalse(ledger.has_open())
        self.assertAlmostEqual(ledger.realized_pnl(), 10.0 * 2 * 30)
        # Same-direction close leg would increase the book: ignored.
        ledger.apply("OPEN_GROUP", "g2", [leg(PE, "SELL", 1, 50.0)], T1)
        result = ledger.apply("CLOSE_GROUP", "g2", [leg(PE, "SELL", 1, 55.0)], T2)
        self.assertFalse(result.applied)
        self.assertEqual(result.ignored[0][1], "would increase the position")

    def test_partial_close_keeps_remainder(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 3, 100.0)], T0)
        ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 1, 110.0)], T1)
        pos = ledger.position("g1", CE)
        self.assertEqual(pos.lots, 2)
        self.assertAlmostEqual(pos.realized, 300.0)
        self.assertEqual(ledger.trades, 0)

    def test_open_group_opposite_side_nets_then_reverses(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 1, 100.0)], T0)
        result = ledger.apply("OPEN_GROUP", "g1", [leg(CE, "SELL", 3, 120.0)], T1)
        self.assertAlmostEqual(result.realized_delta, 20.0 * 30)
        pos = ledger.position("g1", CE)
        self.assertEqual(pos.side, "SHORT")
        self.assertEqual(pos.lots, 2)
        self.assertEqual(ledger.trades, 1)

    def test_charges_per_lot_per_fill(self):
        ledger = PaperLedger(lot_size=30, charges_per_lot=10.0)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 2, 100.0)], T0)
        self.assertAlmostEqual(ledger.charges, 20.0)
        ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 2, 100.0)], T1)
        self.assertAlmostEqual(ledger.charges, 40.0)
        self.assertAlmostEqual(ledger.total_pnl(), -40.0)

    def test_leg_without_price_is_ignored_not_filled_at_zero(self):
        ledger = PaperLedger(lot_size=30)
        result = ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 1, None)], T0)
        self.assertFalse(result.applied)
        self.assertEqual(result.ignored[0][1], "no price")
        self.assertFalse(ledger.has_open())

    def test_flatten_all_prices_every_leg(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "SELL", 1, 100.0), leg(PE, "SELL", 1, 90.0)], T0)
        ledger.apply("OPEN_GROUP", "g2", [leg(CE, "BUY", 2, 105.0)], T0)
        ledger.mark({PE: 95.0}, T1)

        signals = ledger.flatten_all(lambda s: 111.0 if s == CE else None, T2, "End of backtest", "Test")
        self.assertEqual({s.metadata["group_id"] for s in signals}, {"g1", "g2"})
        by_group = {s.metadata["group_id"]: s for s in signals}
        g1 = {l["symbol"]: l for l in by_group["g1"].legs}
        self.assertEqual(g1[CE]["side"], "BUY")
        self.assertEqual(g1[CE]["price"], 111.0)
        self.assertEqual(g1[PE]["price"], 95.0)            # last mark fallback
        self.assertEqual(g1[PE]["price_source"], "last mark")
        self.assertEqual(by_group["g2"].legs[0]["side"], "SELL")
        self.assertEqual(by_group["g2"].legs[0]["quantity"], 2)
        for sig in signals:
            self.assertEqual(sig.signal_type, "CLOSE_GROUP")
            self.assertEqual(sig.reason, "End of backtest")
            ledger.apply(sig.signal_type, sig.metadata["group_id"], sig.legs, T2, sig.reason)
        self.assertFalse(ledger.has_open())
        self.assertEqual(ledger.trades, 3)
        # g1: short CE 100 -> 111 = -330, short PE 90 -> 95 = -150; g2: long CE 105 -> 111 = +360
        self.assertAlmostEqual(ledger.realized_pnl(), -330.0 - 150.0 + 360.0)

    def test_snapshot_shape(self):
        ledger = PaperLedger(lot_size=30)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 1, 100.0)], T0)
        snap = ledger.snapshot(T1)
        self.assertEqual(set(snap), {"snapshotUtc", "realizedPnl", "unrealizedPnl", "charges", "usedCapital", "openPositions", "closedPositions"})
        self.assertEqual(snap["openPositions"], 1)
        self.assertEqual(snap["snapshotUtc"], T1)
        self.assertEqual(snap["charges"], 0.0)

    def test_snapshot_carries_cumulative_charges(self):
        ledger = PaperLedger(lot_size=30, charges_per_lot=25.0)
        ledger.apply("OPEN_GROUP", "g1", [leg(CE, "BUY", 2, 100.0)], T0)
        self.assertEqual(ledger.snapshot(T1)["charges"], 50.0)
        ledger.apply("CLOSE_GROUP", "g1", [leg(CE, "SELL", 2, 100.0)], T2)
        snap = ledger.snapshot(T2)
        self.assertEqual(snap["charges"], 100.0)
        self.assertAlmostEqual(ledger.total_pnl(), -100.0)


if __name__ == "__main__":
    unittest.main()
