"""
core/charges.py

What a fill in an index option costs beyond the premium: the slippage that moves
the price against the trader, and the statutory charges on the turnover.

Two separate things, kept separate so a report can show each:

  - **Slippage** is `slippage_pct` of the premium with a floor of `min_slippage`
    rupees (a tick or two), standing in for half the spread plus impact. A buy
    fills above the bar price, a sell below it. Measured from the desk's own
    bid and ask (Dhan, 34 sessions, 10 Sep to 30 Oct 2026, nearest expiry,
    minute snapshots), half the spread at the money was 0.12% of the premium
    for NIFTY (Rs 0.125 on Rs 116), 0.15% for BANKNIFTY and 0.12% for SENSEX,
    about the same at every time of day and on expiry day; options under
    Rs 10 were one tick wide. So the default 0.5% is about four times the
    quoted half-spread: the rest stands for latency and a fast market, which
    that study could not see. It is a cautious default, not a measurement.
    The best ask held about 11 lots of NIFTY at the money but only 2 of
    BANKNIFTY, so a bigger BANKNIFTY order walks the book.
  - **Charges** are computed on filled turnover (premium × quantity):
      brokerage   flat per executed order (discount brokers: Rs 20)
      STT         on the SELL side's turnover
      exchange    transaction charge on turnover, both sides
      SEBI fee    on turnover, both sides
      stamp duty  on the BUY side's turnover
      GST         on brokerage + exchange charge + SEBI fee

Each segment takes its own statutory rates (`CostModel.for_symbol`, the tables
below): MCX options and futures, NSE and BSE futures, NSE and BSE shares. Options
on NSE and BSE, and anything unreadable, are charged as an index option.

Statutory rates change with budgets and exchange circulars. The defaults are
approximations for NSE index options in 2026 — in particular STT on option
premium is taken as 0.15% (raised from 0.1% by the 2026 Budget, effective
1 April 2026) — so check a contract note before trusting the last rupee. Every
rate is a field, so a run can override it.

The research harness and the backtest engine share this model deliberately: a
backtest whose costs differ from the study's would be a different experiment.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, replace
from typing import Any, Dict

BUY = "BUY"
SELL = "SELL"

# MCX's statutory rates, from the brokers' published charge sheets (Zerodha,
# checked 27 Sep 2026). Until then crude was charged as an NSE index option:
# STT 0.15% where the commodity transaction tax on MCX options is 0.05%, and
# the exchange's 0.03503% where MCX takes 0.0418%. The API's ChargeSchedule
# (Application/Risk/OptionCharges.cs) carries the same three.
MCX_OPTION_RATES = {"stt_sell_pct": 0.05, "exchange_txn_pct": 0.0418, "stamp_buy_pct": 0.003}
# Non-agricultural futures. Brokerage is the lower of Rs 20 and 0.03% of the
# order; Rs 20 is kept, which overstates only mini contracts.
MCX_FUTURE_RATES = {"stt_sell_pct": 0.01, "exchange_txn_pct": 0.0021, "stamp_buy_pct": 0.002}

# NSE and BSE futures and shares, added 2 Nov 2026. Until then each of those fills
# was charged as an index option, about four to eight times too much: STT at 0.15%
# of notional. Every rate is from Zerodha's charge list (zerodha.com/charges) and
# its support articles on STT and on exchange transaction charges, read 2 Nov
# 2026. The dates behind them are from the onetradejournal.com charges changelog,
# which cites the NSE circulars: STT on futures 0.02% to 0.05% from 1 Apr 2026
# (Finance Act 2026, NSE/FATAX/73524), and transaction charges with the IPFT
# contribution included from 1 Mar 2026 (NSE/FA/73061). The NSE circulars
# themselves could not be opened when this was written.
#
# Brokerage on these is the lower of Rs 20 and 0.03% of the order; Rs 20 is kept,
# which overstates an order under Rs 66,667.
NSE_FUTURE_RATES = {"stt_sell_pct": 0.05, "exchange_txn_pct": 0.00183, "stamp_buy_pct": 0.002}
# BSE takes no transaction charge on index or stock futures.
BSE_FUTURE_RATES = {"stt_sell_pct": 0.05, "exchange_txn_pct": 0.0, "stamp_buy_pct": 0.002}
# Every share fill is charged as intraday, because a fill does not say how the
# shares ended up: the rates are chosen from the symbol alone. Intraday is the
# desk's usual case, and this undercharges the other: real delivery pays STT 0.1%
# on both sides and stamp 0.015% on the buy side, with no brokerage at a discount
# broker.
NSE_EQUITY_INTRADAY_RATES = {"stt_sell_pct": 0.025, "exchange_txn_pct": 0.00307, "stamp_buy_pct": 0.003}
# BSE charges some scrip groups differently; not modelled.
BSE_EQUITY_INTRADAY_RATES = {"stt_sell_pct": 0.025, "exchange_txn_pct": 0.00375, "stamp_buy_pct": 0.003}

#: The rates of every segment that is not an index option.
SEGMENT_RATES = {
    "mcx-option": MCX_OPTION_RATES,
    "mcx-future": MCX_FUTURE_RATES,
    "nse-future": NSE_FUTURE_RATES,
    "bse-future": BSE_FUTURE_RATES,
    "nse-equity": NSE_EQUITY_INTRADAY_RATES,
    "bse-equity": BSE_EQUITY_INTRADAY_RATES,
}


def _has_share_series(text: str) -> bool:
    """The part after the last hyphen is a series of one to three characters: BAJAJ-AUTO-EQ is a share, NIFTY50-INDEX is not."""
    _, hyphen, series = text.rpartition("-")
    return bool(hyphen) and 1 <= len(series) <= 3 and series.isalnum()


def segment_of(symbol: Any) -> str:
    """
    The charge segment of a symbol, read from its grammar. "MCX:…CE/PE" is "mcx-option" and any other MCX symbol
    "mcx-future". Elsewhere a symbol ending in CE or PE is "index-option" (a stock option too: its statutory rates
    are the same on NSE), in FUT "nse-future" or "bse-future", and in a series ("-EQ", "-BE", "-A") "nse-equity" or
    "bse-equity". Anything else, an index or a symbol it cannot read, stays "index-option". Keep in step with
    ChargeSchedule.ForSymbol (Application/Risk/OptionCharges.cs).
    """
    text = str(symbol or "").strip().upper()
    option = text.endswith(("CE", "PE"))
    if text.startswith("MCX:"):
        return "mcx-option" if option else "mcx-future"
    if option:
        return "index-option"
    exchange = "bse" if text.startswith("BSE:") else "nse"
    if text.endswith("FUT"):
        return f"{exchange}-future"
    return f"{exchange}-equity" if _has_share_series(text) else "index-option"


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

    def for_symbol(self, symbol: Any) -> "CostModel":
        """
        This model with the statutory rates of the segment `symbol` trades in.
        Slippage, brokerage and GST are the run's own; STT/CTT, the exchange
        charge and stamp duty are the exchange's, so an MCX fill or a share takes
        its own even in a run whose costs block set index-option rates.
        """
        rates = SEGMENT_RATES.get(segment_of(symbol))
        return replace(self, **rates) if rates is not None else self

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
