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
3. After each bar: marks open positions and applies the run's risk rules exactly as the live guard does — per-leg (premium points / % of entry, closes that leg), per-group (₹, closes that group), then overall (₹, ends the backtest) — appends an equity point, and posts progress every two seconds.
4. Squares off at the EOD time and at the end of the range, then posts the equity curve and a summary (`BACKTEST_SUMMARY` signal: bars, sessions, trades, skipped entries, EOD square-offs, stop reason, data notes).

### 3. Reading results
`GET /api/Backtest/runs/{id}` returns everything the results page needs: header, progress, P&L (realized, unrealized, charges, return %), metrics (win rate, profit factor, average and largest win/loss, expectancy, max drawdown in ₹ and %, profitable days), daily P&L by IST day, positions with exit price and exit reason, activity, data notes and the equity curve. `GET /api/Backtest/runs` lists all backtests with net P&L, trades and win rate.

### 3b. The run's own rules
Besides the strategy's logic, a run carries rules of its own, set in the console and stored in
`parametersJson`. They apply to every strategy, which is what makes two runs comparable, and they
are enforced in the **backtest only** (the live runner applies `filters`, not the rest, yet).

| Block | What it does | Where |
|---|---|---|
| `filters` | When it may trade (windows, weekdays, expiry days), what the market must look like (gap, INDIA VIX, ATR, volume) and which trends it may not fight (VWAP, EMA, EMA order, Supertrend, ADX, move from the open, opening range, recent bars, a vote of them), plus RSI levels/crosses and candle shape | `strategies/signal_filters.py`, shared with the live runner |
| `limits` | Trades a day, per window and open at once; a cooldown after a loss; no repeat of a direction that lost; the day ends after N losses or −₹X realised | `backtest/rules.py` |
| `exits` | Exit N minutes after entry; the stop moves to entry after a set gain and then steps up with the profit; exit when the index closes against EMA/VWAP/Supertrend | `backtest/rules.py` |
| `contract` | For a bare BUY/SELL signal: how many strikes in or out of the money, and whether to flip the side | `backtest/rules.py` |
| `costs` | Slippage a side plus brokerage, STT, exchange, SEBI, stamp duty and GST on every fill (replaces the flat charge per lot) | `core/charges.py`, shared with the research harness |

Every blocked entry and every rule-driven exit is counted in the run summary (`limitBlocks`,
`runExits`, `limitDayCloses`) and explained in its data notes, so "it traded less" is never mistaken
for "it traded better". The console edits all of this on `/admin/backtesting/new`
(`web/src/lib/backtestRules.ts` holds the catalogue).

### 4. Data honesty
- **Option premiums, recent contracts:** from FYERS history per contract. FYERS serves history only for contracts that still exist.
- **Option premiums, expired index contracts (NIFTY from Aug 2020, BANKNIFTY from Aug 2021, SENSEX from May 2023):** from `option_history_bars`, Dhan's expired-options history, which stores the nearest expiry at each strike offset from ATM, 1-minute bars.
  - The expiry each bar belongs to comes from `SeedData/index_option_expiries.json`. The file is built from the NSE and BSE derivative bhavcopies by `tools/option_expiry_calendar.py`, so holiday moves and weekday changes are what the exchanges recorded.
  - The backtest asks for these with `includeHistory=true` on `GET /api/Instruments/derivatives/expiries` and `/contract`. An expired contract is built in the master's symbol grammar (`NSE:NIFTY2131015100CE`, monthly `NSE:NIFTY21MAR15100CE`), and `GET /api/MarketData/history/local` serves its bars rolled up to the run's resolution. Stored broker candles win where both exist.
  - Only strikes near ATM exist: ATM−5…ATM+5 as imported, and Dhan offers at most ±10. A strategy whose legs sit further out (far OTM hedge wings, e.g. Fulcrum2Straddle20 and the FulcrumMulti variants) cannot open on these years, and every such entry is skipped and listed. A held contract the market moves away from keeps its last price of that day.
  - Anything else with no price is skipped and listed — the results say how many.
- **Option chain, past sessions:** `GET /api/OptionChain/view?asOfUtc=…` answers from the platform's own
  minute captures when it has them, and otherwise rebuilds the chain from `option_history_bars`:
  each stored strike with its premium, open interest, IV, the build-up since that session's open and a
  Black-Scholes delta from the stored IV (`OptionMath`). That is what lets a chain-reading strategy
  (ChainFlowBuy) replay years the poller never saw; it asks for the chain **as of the candle it is
  judging**, and a chain from another minute is refused by its own age check.
- Index candles for those years come from Dhan's index history (`tools/index_history_import.py`, SourceKey `dhan`); FYERS candles win where both exist.
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
