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

Statutory rates change with budgets and exchange circulars. The defaults are
approximations for NSE index options in 2026 - in particular STT on option
premium is taken as 0.15% (raised from 0.1% by the 2026 Budget, effective
1 April 2026); check a current contract note before trusting the last rupee.
Every rate is a field, so a run can override it.

The model itself lives in `core/charges.py`, because the backtest engine charges
a run with the same one: a backtest whose costs differ from a study's would be a
different experiment. This module stays as the name research code imports.
"""

from __future__ import annotations

from core.charges import CostModel

__all__ = ["CostModel"]
