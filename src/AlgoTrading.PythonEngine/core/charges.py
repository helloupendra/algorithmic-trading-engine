"""
core/charges.py

What a fill in an index option costs beyond the premium: the slippage that moves
the price against the trader, and the statutory charges on the turnover.

Two separate things, kept separate so a report can show each:

  - **Slippage** is `slippage_pct` of the premium with a floor of `min_slippage`
    rupees (a tick or two), standing in for half the spread plus impact. A buy
    fills above the bar price, a sell below it. ATM index weekly spreads are
    usually a tick or two; far-OTM, expiry-day and fast-market spreads are much
    wider, so 0.5% is a middle estimate, not a best case.
  - **Charges** are computed on filled turnover (premium × quantity):
      brokerage   flat per executed order (discount brokers: Rs 20)
      STT         on the SELL side's turnover
      exchange    transaction charge on turnover, both sides
      SEBI fee    on turnover, both sides
      stamp duty  on the BUY side's turnover
      GST         on brokerage + exchange charge + SEBI fee

Statutory rates change with budgets and exchange circulars. The defaults are
approximations for NSE index options in 2026 — in particular STT on option
premium is taken as 0.15% (raised from 0.1% by the 2026 Budget, effective
1 April 2026) — so check a contract note before trusting the last rupee. Every
rate is a field, so a run can override it.

The research harness and the backtest engine share this model deliberately: a
backtest whose costs differ from the study's would be a different experiment.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any, Dict

BUY = "BUY"
SELL = "SELL"


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

    # --- slippage -----------------------------------------------------------

    def slip(self, price: float) -> float:
        return max(float(price) * self.slippage_pct / 100.0, self.min_slippage)

    def buy_fill(self, price: float) -> float:
        """What a market buy at a bar price of `price` actually pays."""
        return float(price) + self.slip(price)

    def sell_fill(self, price: float) -> float:
        """What a market sell at `price` actually receives (never below zero)."""
        return max(float(price) - self.slip(price), 0.0)

    def fill_price(self, side: str, price: float) -> float:
        """The fill price of one leg, by order side."""
        return self.buy_fill(price) if str(side).upper() == BUY else self.sell_fill(price)

    # --- charges ------------------------------------------------------------

    def charges(self, buy_turnover: float, sell_turnover: float, orders: int = 2) -> Dict[str, float]:
        """Charges for the given filled turnovers (premium × quantity) and order count."""
        turnover = float(buy_turnover) + float(sell_turnover)
        brokerage = self.brokerage_per_order * max(0, int(orders))
        stt = float(sell_turnover) * self.stt_sell_pct / 100.0
        exchange = turnover * self.exchange_txn_pct / 100.0
        sebi = turnover * self.sebi_fee_pct / 100.0
        stamp = float(buy_turnover) * self.stamp_buy_pct / 100.0
        gst = (brokerage + exchange + sebi) * self.gst_pct / 100.0
        total = brokerage + stt + exchange + sebi + stamp + gst
        return {"brokerage": brokerage, "stt": stt, "exchange": exchange, "sebi": sebi, "stamp": stamp,
                "gst": gst, "total": total}

    def fill_charges(self, side: str, turnover: float) -> float:
        """Charges of a single fill: one order, on the side that was traded."""
        is_buy = str(side).upper() == BUY
        return self.charges(turnover if is_buy else 0.0, 0.0 if is_buy else turnover, orders=1)["total"]

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, raw: Any) -> "CostModel":
        """A cost model from a run's `costs` block; unknown keys and bad numbers are ignored."""
        fields = {f: getattr(cls, "__dataclass_fields__")[f].default for f in cls.__dataclass_fields__}
        if not isinstance(raw, dict):
            return cls()
        values: Dict[str, float] = {}
        for name, default in fields.items():
            if name not in raw:
                continue
            try:
                values[name] = float(raw[name])
            except (TypeError, ValueError):
                values[name] = float(default)
        return cls(**values)
