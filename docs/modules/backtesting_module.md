# Backtesting Module — Architecture & Documentation

## Overview
The Backtesting Module replays any strategy from the Strategies catalog over stored history, bar by bar, through the same `BaseStrategy.on_bar` contract the live runner uses. It is deliberately **coverage-first**: the operator is shown which underlyings, resolutions and date ranges actually have candles before choosing anything, can top the store up from FYERS, and then runs the strategy with the same knobs as a live run — lots, rupee stop-loss, rupee target — plus an end-of-day square-off.

Results are **position-based**, exactly like the Live Runner: one row per contract with lots, lot size, quantity, entry, exit, P&L and the reason the leg was closed. A trade the engine could not price (no premium history for that contract) is never dropped silently; it is listed under data notes.

A backtest is persisted as a `SimulationRun` with mode `OfflineReplay`, and its signals, orders, positions and equity snapshots land in the same Simulator tables as paper trading — with **historical** timestamps — so a run survives restarts and can be re-opened at any time.

## Architecture Stack
1. **Python engine (`src/AlgoTrading.PythonEngine`)**
   - `backtest/engine.py` — the replay loop: loads driver bars, builds `StrategyInput` per bar (spot, ATM strike, ATM CE/PE contracts as of that date, cumulative bars per required resolution), calls `on_bar`, prices legs, updates the ledger, enforces EOD / SL / target, streams results to the API.
   - `backtest/feed.py` — `HistoricalFeed`: index candles from `GET /api/MarketData/history/local`; option candles per contract with an on-demand FYERS sync (`POST /api/MarketData/history/sync`) when the store is empty and the broker is linked; a contract with no history is marked once and never retried.
   - `backtest/contracts.py` — expiry as of a bar date, strike step from the option chain, ATM rounding, exact contract lookup, logical-symbol resolution (`BANKNIFTY_PE_50300`).
   - `backtest/ledger.py` — `PaperLedger`: positions keyed by group + contract, lots × lot size P&L, averaging, reduce-only closes, marks, charges, square-off.
   - `backtest/run_spec.py`, `backtest/timeutil.py` — run-row parsing and IST/UTC helpers. `core/resolutions.py` — the single resolution-code mapper (`"5m"` ↔ `"5"`).
   - `tools/backtest_runner.py` — the process the API spawns (`--run-id`). `tools/run_backtest.py` — a terminal wrapper that creates a run through the API and prints a text ledger (replaces the old hard-coded Ghost script).
   - `tests/` — `unittest` suite for the ledger, EOD/SL/target rules, resolution mapping, contract resolution and an engine smoke test with a fake API.
2. **.NET API (`src/AlgoTrading.Api`)**
   - `Controllers/BacktestController.cs` — coverage, backfill, start, stop, delete, run list, run view, logs.
   - `Services/BacktestProcessRegistry.cs`, `BacktestRunControl.cs` — process registry keyed by run id (drained logs, live progress, exit monitor) and the stop/exit pipeline.
   - `Services/BacktestDataService.cs` — coverage per resolution (bars, IST sessions, source) and chunked FYERS backfill.
   - `Services/BacktestRunViewBuilder.cs`, `PositionViewBuilder.cs` — the results view; `PositionViewBuilder` is shared with the Live Runner so both modules render contracts identically.
   - `Controllers/SimulatorController.cs` — runner-facing endpoints: bulk equity snapshots, marks, progress, complete.
3. **Infrastructure (`src/AlgoTrading.Infrastructure`)**
   - `Services/ResolutionCodes.cs` — one normaliser for candle resolution codes (`"5"`, `"1"`, `"15"`, `"D"`); fixes the old `"1m"` → `"1M"` mismatch.
   - `Services/PaperTradingService.cs` — for `OfflineReplay` runs: order/position timestamps come from the signal, the wall-clock risk gate is skipped, and no mark-to-market from live quotes ever happens.
4. **React web (`web/src/pages/backtesting`)**
   - `BacktestOverviewPage.tsx` — data on hand (index × resolution), backfill dialog, recent backtests.
   - `NewBacktestPage.tsx` + `BacktestDialog.tsx` — strategy cards → dialog (underlying, resolution with coverage, bounded date range, lots, SL, target, EOD time, advanced).
   - `BacktestRunsPage.tsx` — history with filters, stop, delete.
   - `BacktestRunPage.tsx` + `charts.tsx` — progress while running, metric tiles, equity curve, daily P&L, positions, activity, data notes, runner output.

---

## Key Workflows

### 1. Choosing what to backtest
`GET /api/Backtest/coverage?underlying=BANKNIFTY&strategyId=…&resolution=5` reports, per resolution, how many bars and IST sessions exist, the first/last date, the source (backfill or live) and whether FYERS can fill it. The dialog builds its resolution control and date bounds from this response; it refuses to start when the chosen range has no sessions. `POST /api/Backtest/backfill` pulls index candles from FYERS in 30-day chunks, skipping chunks already covered.

### 2. Running
`POST /api/Backtest/runs` validates the request, creates the run (IST dates → UTC bounds, `parametersJson` = strategy defaults ⊕ overrides ⊕ `{lots, stop_loss, target, underlying, resolution, eod_square_off_ist, charges_per_lot}`) and spawns `tools/backtest_runner.py`. The runner:
1. Logs a `[CONFIG]` line and warms the strategy up on index candles before the range (same as live).
2. For each driver bar inside 09:15–15:30 IST: resolves expiry/ATM/contracts as of that date, calls `on_bar`, converts bare BUY/SELL signals into one-leg ATM option groups, prices legs at the option candle close of that bar (last known close the same day as fallback), applies the ledger, and posts the signal with its historical timestamp.
3. After each bar: marks open positions, checks total P&L against stop-loss / target (the backtest ends when either trips, like the live risk guard), appends an equity point, and posts progress every two seconds.
4. Squares off at the EOD time and at the end of the range, then posts the equity curve and a summary (`BACKTEST_SUMMARY` signal: bars, sessions, trades, skipped entries, EOD square-offs, stop reason, data notes).

### 3. Reading results
`GET /api/Backtest/runs/{id}` returns everything the results page needs: header, progress, P&L (realized, unrealized, charges, return %), metrics (win rate, profit factor, average and largest win/loss, expectancy, max drawdown in ₹ and %, profitable days), daily P&L by IST day, positions with exit price and exit reason, activity, data notes and the equity curve. `GET /api/Backtest/runs` lists all backtests with net P&L, trades and win rate.

### 4. Data honesty
- Option premiums come from FYERS history per contract. FYERS serves history only for contracts that still exist, so entries on expired contracts are skipped and listed — the results say how many.
- Lot sizes are the current master values; historical lot-size changes are not modelled (noted in the run's data notes).
- Fills use the signal bar's close with no slippage; a flat per-lot charge can be set in the dialog.

---

## Endpoints
| Method | Route | Purpose |
|---|---|---|
| GET | `/api/Backtest/coverage` | bars/sessions per resolution, required resolutions, option candle inventory, broker state |
| POST | `/api/Backtest/backfill` | FYERS index backfill in chunks (admin) |
| POST | `/api/Backtest/runs` | validate, create the OfflineReplay run, spawn the runner (admin) |
| POST | `/api/Backtest/runs/{id}/stop` | square off at last mark, stop the process (admin) |
| DELETE | `/api/Backtest/runs/{id}` | remove a finished run and its rows (admin) |
| GET | `/api/Backtest/runs` | history with net P&L, trades, win rate, progress |
| GET | `/api/Backtest/runs/{id}` | full results view |
| GET | `/api/Backtest/runs/{id}/logs` | runner output |
| POST | `/api/Simulator/runs/{id}/equity-snapshots`, `/marks`, `/progress`, `/complete` | runner → API |

## Running from the terminal
```
cd src/AlgoTrading.PythonEngine
../../.venv/bin/python tools/run_backtest.py --strategy GhostTangentCrossings --underlying BANKNIFTY \
  --resolution 5m --from 2026-08-19 --to 2026-09-03 --lots 1 --sl 5000 --target 8000
```
The wrapper creates the run through the API, follows its progress and prints the ledger.

## Known limits
- Only ATM contracts are supplied to strategies (same as live); strangle/butterfly/spread strategies wait for entry until OTM selection ships.
- One backtest = one underlying and one driver resolution; multi-symbol portfolios are out of scope.
- The old C# `OfflineReplay` frame counter (`SimulationRunnerService`) is retained but `POST /api/Simulator/runs/{id}/start` now points callers at `/api/Backtest/runs`.
