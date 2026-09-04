# Strategies Module — Live Runner Architecture & Documentation

## Overview
The Strategies Module turns a Python strategy class into a supervised paper-trading run on live ticks. It answers three questions an operator has every time they press Start:

1. **What is this strategy and what can it trade?** Every strategy carries a description, a category, the underlyings it supports, a legs summary and its data requirements — read straight from the Python class, never typed twice.
2. **Where should it run, and with what limits?** The underlying is chosen from the F&O contracts actually loaded in the database (never a free-text box). Lots, stop-loss and target are set per run; stop-loss and target are optional.
3. **What is it doing right now?** A position-based live view: one row per contract with lots, lot size, quantity, entry premium, LTP and P&L. When a leg is exited the same row shows quantity 0 and its realized P&L — there is no separate "sell order" row.

The module runs strictly in **LivePaper** mode: real ticks, simulated fills through the Simulator's paper book. No broker order path exists here.

## Architecture Stack
1. **Python engine (`src/AlgoTrading.PythonEngine`)**
   - `strategies/registry.py` discovers every `BaseStrategy` subclass plus the parameterised factories in `strategies/private_strategies.py`.
   - `tools/list_strategies.py` prints the catalog as one JSON array (name, description, category, supported underlyings, legs summary, default lots and parameters, data requirements, source file). The API shells out to it.
   - `strategies/execution_runner.py` is the per-run process: loads the run's parameters, resolves the nearest expiry and the strike step from the option chain, warms the strategy up on history, then consumes ticks from the Redis stream and posts signals to the Simulator.
2. **.NET API (`src/AlgoTrading.Api`)**
   - `Services/StrategyCatalogService.cs` — cached catalog (5 s TTL + source-file mtime check, regex-scan fallback when Python is unavailable). Strategy ids are a deterministic FNV-1a hash of the name.
   - `Services/StrategyProcessRegistry.cs` — the in-memory registry of running runner processes, with drained stdout/stderr ring buffers and a last-exit record per strategy.
   - `Services/StrategyRunControl.cs` — the single stop pipeline (mark run `Stopping` → SIGTERM, then kill → square off open positions → persist `RUN_STOPPED` signal → registry bookkeeping). Used by the UI stop, the risk guard, market close and runner self-exit.
   - `Services/StrategyRiskGuardService.cs` — background service; every 3 s it marks each running run to market and trips the stop pipeline when total P&L ≤ −stop-loss or ≥ target.
   - `Controllers/StrategyController.cs` — catalog, start, stop, live view, logs, signal mirror.
   - `Controllers/InstrumentsController.cs` — `GET /api/Instruments/derivatives/underlyings`: the F&O inventory the launch dialog is built from.
3. **Infrastructure (`src/AlgoTrading.Infrastructure`)**
   - `Services/LotSizeResolver.cs` — lot size from the instrument master, else the configured `LotSizes` table, else 1.
   - `Services/UnderlyingCatalog.cs` — underlying ↔ spot symbol mapping (BANKNIFTY ↔ `NSE:NIFTYBANK-INDEX`, stocks ↔ `NSE:{NAME}-EQ`).
   - `Services/PaperTradingService.cs` — signal → order → position engine; `FlattenRunAsync` squares off a run at the last mark; signals for stopped runs are rejected.
   - `Services/LocalCsvInstrumentImportService.cs` — now stores lot size (column 3) and the master's own underlying (column 13), so stock options resolve correctly.
4. **React web (`web/src/pages/strategies`)**
   - `LiveRunnerPage.tsx` — readiness strip, stat row, one `RunCard` per running (or just-stopped) strategy, and the "Start a strategy" grid.
   - `StrategyLibraryPage.tsx` — the catalog with full metadata and the same Start dialog.
   - `shared.tsx` — `StrategyCard`, `LaunchDialog`, `ReadinessStrip`, `CategoryBadge`, `PnlValue`.

---

## Key Workflows

### 1. Starting a run
1. The launch dialog lists underlyings from `GET /api/Instruments/derivatives/underlyings` — each with next expiry, lot size (and whether it came from the master or configuration), strike step and contract count. Underlyings the strategy does not support are shown disabled.
2. `POST /api/Strategy/{id}/start` with `{ underlying, lots, stopLoss?, target?, parameters?, initialCapital? }`. The API validates (underlying mandatory; lots ≥ 1; stop-loss/target > 0; the underlying must have unexpired option contracts loaded), creates a `SimulationRun` (mode `LivePaper`, symbol = spot symbol, `parametersJson` = strategy defaults ⊕ overrides ⊕ `{lots, stop_loss, target, underlying}`), adds the spot symbol to the live watchlist and launches `execution_runner.py --run-id …`.
3. The runner logs a `[CONFIG]` line with the effective parameters, resolves the expiry and strike step, and waits for ticks. Every 10 s it prints a `[STATUS]` line even when no ticks arrive (market closed, feed stopped), so a silent runner is never mistaken for a healthy one.

### 2. Lots, lot size and P&L
- Every leg quantity in a signal is a number of **lots**. Units = lots × lot size.
- P&L = (exit − entry) × lots × lot size for longs, and the reverse for shorts. Open rows show unrealized P&L against the latest live quote; closed rows show realized P&L.
- Lot sizes come from the FYERS master (`Instruments.LotSize`, populated by the importer) and fall back to `appsettings.json → LotSizes` (NIFTY 65, BANKNIFTY 30, FINNIFTY 60, MIDCPNIFTY 120, NIFTYNXT50 25, SENSEX 20, BANKEX 30 as of the September 2026 master). The live view reports `lotSizeSource` so the operator can tell which one was used.

### 3. Risk rules: overall, per group, per leg
Every run carries a `risk` object (all fields optional, set at start or changed while running with
`PATCH /api/Strategy/runs/{runId}/risk`; each change is recorded as a `RISK_UPDATED` activity row):
- **Overall** (₹ on the run's total P&L, realized + unrealized): a trip squares off every position and ends the run.
- **Per group** (₹ on one `OPEN_GROUP`, e.g. a straddle pair: realized of the group + unrealized of its open legs): a trip closes that group only; the run continues.
- **Per leg** (premium points and/or % of the entry premium; BUY legs lose when the premium falls, SELL legs when it rises; when both are set the first to trip wins): a trip closes that leg only.

The guard runs in the API every 3 seconds (`StrategyRiskGuardService`), not in the runner, so a wedged runner cannot skip its own stop. Each sweep marks the run to market, evaluates leg → group → overall, and closes through reduce-only `CLOSE_GROUP` signals at the last mark with the reason ("Leg stop-loss hit: BANKNIFTY 57500 CE −21.4 pts (−2.6%) ≤ −20 pts", "Group stop-loss hit: G1 P&L −1,240 ≤ −1,000"). A strategy's own later `CLOSE_GROUP` for an already-closed leg is reduce-only and ignored, so the guard can never leave a reverse position behind.

An overall trip runs the stop pipeline: the run is marked `Stopping` (further signals are rejected), the runner receives SIGTERM (falling back to a kill after 5 s), every open position is squared off at its last mark, and a `RUN_STOPPED` signal with the reason is persisted. The same pipeline serves the UI Stop button ("Stopped by <user>"), the 15:30 IST market-close service and a runner that exits on its own ("Runner exited (code N)"). The backtest engine applies the same three levels bar by bar, so a rule behaves the same in replay and live.

### 4. Several runs of one strategy
Runs are keyed by run id, so the same strategy can run on several underlyings at once (Titli on BANKNIFTY and on NIFTY). Starting a strategy on an underlying it is already running on answers 409. Each run has its own card, stop, live view, logs and signal ring under `/api/Strategy/runs/{runId}/…`; the older strategy-scoped routes resolve to the single active run.

### 5. Surviving an API restart
The ingestor and every runner report their process id (heartbeat `processId`, `POST /api/Strategy/runs/{runId}/runner`), stored in `system_settings`. On startup the API adopts runners that are still alive (their cards come back, output is no longer captured) and closes the runs whose runner is gone; the ingestor's Stop button works for an adopted process too. Python processes write through a safe stdio wrapper: when the API's pipe closes their output moves to `logs/engine/*.log` instead of crashing the heartbeat or the runner.

### 6. Live view
`GET /api/Strategy/runs/{runId}/live` returns the run as:
- header: underlying, spot LTP, lots, lot size, risk rules, started by/at, stop reason;
- `pnl`: realized, unrealized, total, capital used, premium outlay (open BUY legs) and premium received (open SELL legs);
- `positions[]`: contract label ("BANKNIFTY 57600 CE · 29 Sep"), side, lots, lot size, quantity, entry, value (entry × qty, and the current value while open), LTP, P&L with premium points and %, status, opened/closed time — open rows first;
- `groups[]`: P&L and open/closed leg counts per group;
- `activity[]`: every signal with the strategy's own reason text, newest first;
- `runner`: process id and last log time. `GET /api/Strategy/{id}/logs` returns the drained process output.

The web client polls the live view every 2 s while a run is active and stops polling once it has ended.

---

### 7. Run history (per user)
Every live run is a `SimulationRun` owned by the user who started it and is never deleted by the UI. `GET /api/Strategy/runs` lists runs (filters: user — admin only, strategy, underlying, status, IST date range, paging) with lots, lot size, risk rules, status, stop reason and who stopped it, duration, trades and net P&L; `GET /api/Strategy/runs/summary` gives the per-user rollup (runs, active, net P&L, last run). A trader only ever sees their own runs (the API answers 403 for another user's run id on every run-scoped route); admins see everyone. The console pages are Strategies › Run history (`/admin/strategies/history`), the run detail (`/admin/strategies/runs/{runId}`: positions, activity, orders ledger, runner output) and the trader's "My runs". Dismissing a stopped card on the Live runner only hides it from that list.

## Module Components

### Python
- `src/AlgoTrading.PythonEngine/strategies/base_strategy.py` — `BaseStrategy` with the catalog attributes (`description`, `category`, `supported_underlyings`, `legs_summary`, `default_lots`, `default_params`) and `lots_from()`.
- `src/AlgoTrading.PythonEngine/strategies/registry.py`, `tools/list_strategies.py` — discovery and catalog output.
- `src/AlgoTrading.PythonEngine/strategies/execution_runner.py` — the live runner.

### .NET
- `src/AlgoTrading.Api/Controllers/StrategyController.cs`, `InstrumentsController.cs`
- `src/AlgoTrading.Api/Services/StrategyCatalogService.cs`, `StrategyProcessRegistry.cs`, `StrategyRunControl.cs`, `StrategyRiskGuardService.cs`, `PythonEngineLocator.cs`, `MarketHoursService.cs`
- `src/AlgoTrading.Contracts/Strategies/*.cs` — request/response DTOs.
- `src/AlgoTrading.Infrastructure/Services/LotSizeResolver.cs`, `UnderlyingCatalog.cs`, `PaperTradingService.cs`, `LocalCsvInstrumentImportService.cs`

### React
- `web/src/pages/strategies/LiveRunnerPage.tsx`, `StrategyLibraryPage.tsx`, `StrategiesOverviewPage.tsx`, `shared.tsx`
- `web/src/lib/queries.ts` (`useStrategies`, `useStartStrategy`, `useStopStrategy`, `useStrategyLive`, `useStrategyLogs`, `useFnoUnderlyings`), `web/src/lib/symbols.ts` (`parseOptionSymbol`, `formatContract`).

---

## Adding a strategy
1. Create a `BaseStrategy` subclass under `strategies/` and set `name`, `description`, `category`, `supported_underlyings`, `legs_summary`, `default_lots` and `default_params`.
2. Use `self.lots = self.lots_from(self.params, self.default_lots)` for every leg quantity.
3. Emit `OPEN_GROUP` / `CLOSE_GROUP` signals with a human-readable `reason`; the reason is what the operator sees in the activity feed.
4. No API or web change is needed — the catalog picks the class up on the next request.

## Known limits
- The runner supplies only ATM contracts today, so strategies that need OTM legs (strangle, butterfly, spreads) wait for entry until OTM contract selection ships; their descriptions say so.
- Runner state lives in the API process. An API restart cannot stop an orphaned runner from the console; the live view still rebuilds from the database.
- Live mode against a broker is not wired — every run is paper.
