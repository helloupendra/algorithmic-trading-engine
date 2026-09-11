"""
rawPayload keeps what has no column, and drops what has one.

It was the vendor's whole message: 73% of every tick row, and the same numbers
already sitting in Symbol, LastTradedPrice, BidPrice and the rest. A full
session was 1.2 GB of duplication, in two tables. Trimming it is only safe if
two things hold, so both are pinned here: nothing a column already holds
survives, and everything else does — including a field the vendor adds later,
which is the whole reason this is a drop-list and not a keep-list.
"""

import json
import unittest

import _bootstrap  # noqa: F401

from market_data.live.vendors.fyers import trim_raw_payload

# One real MCX message, copied from live_ticks on 2026-09-11.
REAL_MESSAGE = {
    "ltp": 152258.0, "vol_traded_today": 23584, "last_traded_time": 1789121417,
    "exch_feed_time": 1789121419, "bid_size": 2, "ask_size": 2,
    "bid_price": 152259.0, "ask_price": 152289.0, "last_traded_qty": 2,
    "tot_buy_qty": 1443, "tot_sell_qty": 1241, "avg_trade_price": 152101.92,
    "low_price": 150744.0, "high_price": 153050.0, "lower_ckt": 0,
    "upper_ckt": 0, "open_price": 151500.0, "prev_close_price": 152519.0,
    "type": "sf", "symbol": "MCX:GOLDM26OCTFUT", "ch": -261.0, "chp": -0.1711,
}


class TrimRawPayloadTests(unittest.TestCase):
    def test_fields_with_a_column_are_dropped(self):
        kept = json.loads(trim_raw_payload(REAL_MESSAGE))
        for field in ("ltp", "bid_price", "ask_price", "bid_size", "ask_size",
                      "open_price", "high_price", "low_price", "prev_close_price",
                      "vol_traded_today", "symbol", "type", "last_traded_time",
                      "exch_feed_time"):
            self.assertNotIn(field, kept, f"{field} is already a column")

    def test_fields_without_a_column_survive(self):
        kept = json.loads(trim_raw_payload(REAL_MESSAGE))
        for field, value in (("last_traded_qty", 2), ("tot_buy_qty", 1443),
                             ("tot_sell_qty", 1241), ("avg_trade_price", 152101.92),
                             ("lower_ckt", 0), ("upper_ckt", 0)):
            self.assertIn(field, kept, f"{field} has no column and must be kept")
            self.assertEqual(kept[field], value)

    def test_an_unknown_future_field_is_kept(self):
        # The drop-list shape: a vendor adding open interest tomorrow is recorded
        # without anyone editing this file.
        kept = json.loads(trim_raw_payload({**REAL_MESSAGE, "oi": 4210}))
        self.assertEqual(kept["oi"], 4210)

    def test_it_is_much_smaller(self):
        before = len(json.dumps(REAL_MESSAGE))
        after = len(trim_raw_payload(REAL_MESSAGE))
        self.assertLess(after, before * 0.45, f"{before} -> {after} bytes is not a saving")

    def test_a_real_tick_is_never_confused_with_a_fabricated_one(self):
        # Mock ticks are written as {"mock": true}. A real message carrying
        # nothing new must still not come out as an empty object.
        bare = trim_raw_payload({"ltp": 1.0, "symbol": "X", "type": "sf"})
        self.assertNotEqual(bare, "{}")
        self.assertNotIn("mock", json.loads(bare))


if __name__ == "__main__":
    unittest.main()
