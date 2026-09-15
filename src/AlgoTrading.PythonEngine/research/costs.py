"""
research/costs.py

What a round trip in a bought index option costs, beyond the premium.

Two separate things, kept separate so the report can show each:

  - **Slippage** moves the fill price against the trader: a buy fills above
    the bar price, a sell below it. It is `slippage_pct` of the premium with a
    floor of `min_slippage` rupees of premium (one or two ticks), which stands
    in for half the bid-ask spread plus impact. ATM NIFTY weekly spreads are
    usually a tick or two; far-OTM, expiry-day and fast-market spreads are much
    wider, so 0.5% is a middle estimate, not a best case.
  - **Charges** are computed on the filled turnover:
      brokerage   flat per executed order (discount brokers: Rs 20)
      STT         on the SELL side's premium turnover
      exchange    transaction charge on premium turnover, both sides
      SEBI fee    on turnover, both sides
      stamp duty  on the BUY side's turnover
      GST         on brokerage + exchange charge + SEBI fee

Statutory rates change with budgets and exchange circulars. The defaults below
are approximations for NSE index options in 2026 - in particular STT on option
premium is taken as 0.15% (raised from 0.1% by the 2026 Budget, effective
1 April 2026); check a current contract note before trusting the last rupee.
Every rate is a field, so a run can override it.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Dict


@dataclass(frozen=True)
class CostModel:
    brokerage_per_order: float = 20.0
    stt_sell_pct: float = 0.15
    exchange_txn_pct: float = 0.03503
    sebi_fee_pct: float = 0.0001
    stamp_buy_pct: float = 0.003
    gst_pct: float = 18.0
    slippage_pct: float = 0.5
    min_slippage: float = 0.05

    def slip(self, price: float) -> float:
        return max(price * self.slippage_pct / 100.0, self.min_slippage)

    def buy_fill(self, price: float) -> float:
        """What a market buy at a bar price of `price` actually pays."""
        return price + self.slip(price)

    def sell_fill(self, price: float) -> float:
        """What a market sell at `price` actually receives (never below zero)."""
        return max(price - self.slip(price), 0.0)

    def charges(self, buy_turnover: float, sell_turnover: float, orders: int = 2) -> Dict[str, float]:
        """Charges for a round trip with the given filled turnovers (premium x quantity)."""
        turnover = buy_turnover + sell_turnover
        brokerage = self.brokerage_per_order * orders
        stt = sell_turnover * self.stt_sell_pct / 100.0
        exchange = turnover * self.exchange_txn_pct / 100.0
        sebi = turnover * self.sebi_fee_pct / 100.0
        stamp = buy_turnover * self.stamp_buy_pct / 100.0
        gst = (brokerage + exchange + sebi) * self.gst_pct / 100.0
        total = brokerage + stt + exchange + sebi + stamp + gst
        return {"brokerage": brokerage, "stt": stt, "exchange": exchange, "sebi": sebi, "stamp": stamp,
                "gst": gst, "total": total}

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)
