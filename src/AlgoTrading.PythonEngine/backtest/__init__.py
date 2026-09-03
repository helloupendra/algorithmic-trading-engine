"""
backtest/

Offline replay of one catalog strategy over stored candles, driven through the
same `BaseStrategy.on_bar` contract as the live runner:

  - `timeutil.py`   UTC/IST helpers shared by the package
  - `run_spec.py`   the run's configuration parsed from the SimulationRun row
  - `feed.py`       index/option candles from the platform API (HistoricalFeed)
  - `contracts.py`  expiries, ATM strikes and exact contracts (ContractResolver)
  - `ledger.py`     the paper ledger in lots x lot size (PaperLedger)
  - `engine.py`     the bar loop (run_backtest)

Everything except `engine.run_backtest`'s API calls is pure Python so the
package is unit-testable offline.
"""
