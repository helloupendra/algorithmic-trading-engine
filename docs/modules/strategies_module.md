# Strategies Module — Live Runner Architecture & Documentation

## Overview
The Strategies Module turns a Python strategy class into a supervised paper-trading run on live ticks. It answers three questions an operator has every time they press Start:

1. **What is this strategy and what can it trade?** Every strategy carries a description, a category, the underlyings it supports, a legs summary and its data requirements — read straight from the Python class, never typed twice.
2. **Where should it run, and with what limits?** The underlying is chosen from the F&O contracts actually loaded in the database (never a free-text box). Lots, stop-loss and target are set per run; stop-loss and target are optional.
3. **What is it doing right now?** A position-based live view: one row per contract with lots, lot size, quantity, entry premium, LTP and P&L. When a leg is exited the same row shows quantity 0 and its realized P&L — there is no separate "sell order" row.

The module runs strictly in **LivePaper** mode: real ticks, simulated fills through the Simulator's paper book. No broker order path exists here.

## Architecture Stack
1. **Python engine (`src/AlgoTrading.PythonEngine`)**
   - `strategies/registry.py` discovers every `BaseStrategy` subclass plus the parameterised factories in `strategies/variants.py`.
   - `tools/list_strategies.py` prints the catalog as one JSON array (name, description, category, supported underlyings, legs summary, default lots and parameters, data requirements, source file). The API shells out to it.
   - `strategies/execution_runner.py` is the per-run process: loads the run's parameters, resolves the nearest expiry and the strike step from the option chain, warms the strategy up on history, then consumes ticks from the Redis stream and posts signals to the Simulator.
2. **.NET API (`src/AlgoTrading.Api`)**
   - `Services/StrategyCatalogService.cs` — cached catalog (5 s TTL + source-file mtime check, regex-scan fallback when Python is unavailable). Strategy ids are a deterministic FNV-1a hash of the name.
   - `Services/StrategyProcessRegistry.cs` — the in-memory registry of running runner processes, with drained stdout/stderr ring buffers and a last-exit record per strategy.
   - `Services/StrategyRunControl.cs` — the single stop pipeline (mark run `Stopping` → SIGTERM, then kill → [at the market close only: move the legs ticked "carry forward" to the owner's manual book] → square off open positions → persist `RUN_STOPPED` signal → registry bookkeeping). Used by the UI stop, the risk guard, market close and runner self-exit.
   - `Services/PositionCarryForward.cs` — the per-position carry-forward tick and the close's move of ticked legs (section 8).
   - `Services/StrategyRiskGuardService.cs` — background service; every 3 s it marks each running run to market, judges its leg, group and overall rules (leg and group only on fresh marks, section 9) and closes legs or trips the stop pipeline.
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
- **Overall** (₹ on the run's net P&L: realized + unrealized, less the statutory charges of its fills so far, the same net the run page and the history show; gross until 28 Sep): a trip squares off every position and ends the run. Group rules stay on the group's gross, since charges belong to the run.
- **Per group** (₹ on one `OPEN_GROUP`, e.g. a straddle pair: realized of the group + unrealized of its open legs): a trip closes that group only; the run continues.
- **Per leg** (premium points and/or % of the entry premium; BUY legs lose when the premium falls, SELL legs when it rises; when both are set the first to trip wins): a trip closes that leg only.

The guard runs in the API every 3 seconds (`StrategyRiskGuardService`), not in the runner, so a wedged runner cannot skip its own stop. Each sweep marks the run to market, evaluates leg → group → overall, and closes through reduce-only `CLOSE_GROUP` signals at the latest quote, across the spread (section 11), with the reason ("Leg stop-loss hit: BANKNIFTY 57500 CE −21.4 pts (−2.6%) ≤ −20 pts", "Group stop-loss hit: G1 P&L −1,240 ≤ −1,000"). A strategy's own later `CLOSE_GROUP` for an already-closed leg is reduce-only and ignored, so the guard can never leave a reverse position behind.

An overall trip runs the stop pipeline: the run is marked `Stopping` (further signals are rejected), the runner receives SIGTERM (falling back to a kill after 5 s; an adopted runner, not being the API's child, gets 15 s and is watched by pid), every open position is squared off at its latest quote (its last mark when it has none), and a `RUN_STOPPED` signal with the reason is persisted. The same pipeline serves the UI Stop button ("Stopped by <user>"), the market-close service (NSE and BSE runs at 15:30 IST, MCX runs at the MCX close — `MarketCloseRules`) and a runner that exits on its own ("Runner exited (code N)"). Only the market close's stop honours a leg's carry-forward tick (section 8); every other one squares off every leg. The backtest engine applies the same three levels bar by bar, so a rule behaves the same in replay and live.

### 4. Several runs of one strategy
Runs are keyed by run id, so the same strategy can run on several underlyings at once (Fulcrum on BANKNIFTY and on NIFTY). Starting a strategy on an underlying it is already running on in the same account answers 409 — also when the run's row is still Running or Stopping with no runner the API knows of. Every refusal is decided before the run row is inserted, one start at a time, so a refused start leaves no Failed row: 409 for a duplicate, 429 for the account's run limit, the desk's (`StrategyRunner:MaxConcurrentProcesses`, 28) or a server with less than `StrategyRunner:MinAvailableMemoryMb` (1024) of memory available, 403 for the trader's package. An admin may start a run in another account (`ownerUserId`); the run row records who started it (`StartedByUserId`, `StartedByName`) as well as whose it is. Each run has its own card, stop, live view, logs and signal ring under `/api/Strategy/runs/{runId}/…`; the older strategy-scoped routes resolve to the single active run, or to the caller's own newest exit.

### 5. Surviving an API restart
The ingestor and every runner report their process id (heartbeat `processId`, `POST /api/Strategy/runs/{runId}/runner`), stored in `system_settings`. On startup the API adopts runners that are still alive (their cards come back) and closes the runs whose runner is gone; the ingestor's Stop button works for an adopted process too. A runner whose pid is alive but cannot be verified as that run's (its command line — `/proc/<pid>/cmdline` on Linux — cannot be read) is probed again after 2, 4 and 8 s; if it still cannot, its run is neither adopted nor closed, and an alert says so. Python processes write through a safe stdio wrapper: when the API's pipe closes their output moves to `logs/engine/*.log` instead of crashing the heartbeat or the runner. A strategy runner keeps `logs/engine/runner-<run>-<pid>.log` from its first line — every line stamped with its UTC time and stream — and ends it with `EXIT code=… reason=…`.

### 6. Live view
`GET /api/Strategy/runs/{runId}/live` returns the run as:
- header: underlying, spot LTP, lots, lot size, risk rules, started by/at, stop reason;
- `pnl`: realized, unrealized, total, capital used, premium outlay (open BUY legs) and premium received (open SELL legs);
- `positions[]`: contract label ("BANKNIFTY 57600 CE · 29 Sep"), side, lots, lot size, quantity, entry, value (entry × qty, and the current value while open), LTP, P&L with premium points and %, status (`Open`, `Closed`, or `Carried` for a leg moved to the manual book at the close), the carry-forward tick, opened/closed time — open rows first;
- `canCarryForward` (the run is live and not a recap) and `isManualBook`;
- `positions[].greeks` and `greeks`: each open option leg's IV, delta, gamma, theta and vega with their rupee effect (theta ₹/day, vega ₹ per 1% IV, delta per underlying), the source and its age, and the run's totals — see [Manual orders & carried positions](manual_orders.md#3-greeks-on-open-positions);
- `groups[]`: P&L and open/closed leg counts per group;
- `activity[]`: every signal with the strategy's own reason text, newest first;
- `runner`: process id and last log time. `GET /api/Strategy/runs/{runId}/logs` returns the runner's output, read from its log file every second (the last 300 lines on adoption), so a run reads the same before and after an API restart; an adopted runner's exit reason comes from its `EXIT` line.

The web client polls the live view every 2 s while a run is active and stops polling once it has ended.

---

### 7. Run history (per user)
Every live run is a `SimulationRun` owned by the user who started it and is never deleted by the UI. `GET /api/Strategy/runs` lists runs (filters: user — admin only, strategy, underlying, status, IST date range, paging) with lots, lot size, risk rules, status, stop reason and who stopped it, duration, trades and net P&L; `GET /api/Strategy/runs/summary` gives the per-user rollup (runs, active, net P&L, last run). A trader only ever sees their own runs (the API answers 403 for another user's run id on every run-scoped route); admins see everyone. The console pages are Strategies › Run history (`/admin/strategies/history`), the run detail (`/admin/strategies/runs/{runId}`: positions, activity, orders ledger, runner output) and the trader's "My runs". Dismissing a stopped card on the Live runner only hides it from that list.

### 8. Carrying a leg forward
The owner, 27 Sep: "Put a carry-forward system in strategies and in manual orders: if I want to carry forward, there should be a tick there and ticking it is enough. In strategies, even a single leg — I should be able to do it."

**The tick.** Every open leg on a run card has a **Carry** tick (`PUT /api/Strategy/runs/{runId}/positions/{positionId}/carry-forward { "carryForward": true }`). The owner of the run or an admin may change it (403 for anyone else, the same rule as squaring a leg off); only on an open leg of a running run, and never on a recap (409). Each change is a `CARRY_FORWARD` activity row with who and when (`Carry forward ticked by trader: NIFTY 24500 CE · 29 Sep — moves to the manual book at the close instead of being squared off`). Nothing is ticked unless someone ticks it, so a run nobody touches ends exactly as before.

**At the close.** When `MarketHoursService` stops a run because its market has closed (`StrategyRunControl.StopAtMarketCloseAsync`, reasons `Market closed (15:30 IST)` / `MCX closed (23:30 IST)`), the pipeline, after the runner is stopped and before the flatten, moves each open ticked leg into the run owner's **manual book** — opening the book if the owner has none, exactly as the manual ticket does — and then squares off the rest as always. The moved leg keeps its contract, side, lots, entry price and opening time. Both sides say what happened:

> run: Carried forward to the manual book at the close (15:30 IST): NIFTY 24500 CE · 29 Sep — SELL 2 lots at 100.00
> book: Carried forward from run #412 (Ghost) at the close (15:30 IST): NIFTY 24500 CE · 29 Sep — SELL 2 lots at 100.00

In the book it is held overnight (ticked there too), marked against tomorrow's quote, shown with its greeks, and settled at expiry like any hand-placed position — see [Manual orders › Legs a strategy carried in](manual_orders.md#1a-legs-a-strategy-carried-in). The run's own stop-loss, target and leg rules do not travel with it.

**Which stops ignore the tick, and why.** Only the market close carries. The **Stop** button, a **risk-rule trip** (overall stop-loss/target) and a **runner that dies**, as well as an API restart that finds the runner gone, square off every leg, ticked or not: those are decisions — or failures — that call for getting out, and a protective stop that left legs behind would not be one. The run card says so under the table, and the Stop confirmation says so when a ticked leg is open. The strategy's own exits and the leg/group rules also keep working on a ticked leg during the day: the tick changes only what happens at the close. A recap run never carries (its fills are a replayed session's prices), and a leg whose move fails is squared off with the rest rather than left on a stopped run.

**P&L and charges.** The move is not a trade: no order is written on either side (a close and a re-open would each be charged, and the book's entry would be the close's price instead of the real one).
- The **run** keeps the leg's **entry fill and its charges**; its P&L stops counting the leg (the row is `Carried`: it keeps the lots that left and whatever it had realized before, no unrealized, no exit). It is not counted as a trade in the history, and it adds nothing to the run's realized P&L.
- The **book** owns the leg **from its entry**: the whole P&L of the trade, entry to exit, lands there, and so do the exit fill's charges.
- Across the two, the trade is counted once and charged once — the run's entry charges plus the book's exit charges equal what the round trip costs in one run (to the paisa; `CarryForwardTests.After_a_move_the_run_keeps_its_entry_charges_and_the_book_the_trade`). The run history, the run page and `RunCharges` read the same figures.

### 9. P&L over the day, minute by minute
Until 28 Sep a live run kept no P&L over time: its card and its history row said where it stood, never how it got there, so the Desk's Day P&L was one number. `RunPnlRecorderService` now writes `run_pnl_minutes` fifty seconds into every minute.

- **Which runs.** Every LivePaper run whose row says Running or Stopping, manual books included. A strategy run gets a row every minute it is live, whether anything moved or not, so a gap in its series means the recorder was not running, never "flat". A manual book is open for good, so it gets a row only when its figures moved; otherwise it would store the same number all night.
- **The last row.** A run that ended in the last ten minutes is written at the minute of its `CompletedUtc`, with its figures after the stop (the flatten realized its open legs). If the live pass wrote that minute seconds before the stop, the row is updated. No state is kept between passes, so an API restart loses nothing but the minutes it was down.
- **The figures.** Realized over every position; unrealized over the open legs at the latest live quote (the stored mark when no quote is known), valued with `PaperPnl.Unrealized` at the lot size the fills were booked at; the statutory charges of every fill so far (`RunCharges`); net = realized + unrealized − charges, in rupees to the paisa. They come from `RunPnl`, the code the run history marks with, so a minute's net is what the run card said at that minute (`RunPnlSeriesTests` checks it against `GET /api/Strategy/runs/{runId}/live`). Since the same change the history marks an open manual book at the latest quote too; it used to read the book at its stored mark while the book's own page marked it live.
- **Cost.** Per pass, whatever the number of runs: one query for the runs, one for all their positions, one for the open legs' quotes, one for their fills, one for the minutes already written, one save.
- **One row per run per minute.** `(SimulationRunId, AtUtc)` is unique; a minute written twice is updated, never doubled.

`GET /api/Strategy/runs/pnl-series?date=yyyy-MM-dd` (an IST day, default today; `userId` filters for an admin) returns that day:
- `runs[]`: run id, account (`userId`, `userName`), strategy, underlying, `isManualBook`, status, `startedUtc`, `inAccountTotals`, and parallel arrays `minutes` (counted from 00:00 IST, so 555 is 09:15; `dayStartUtc` is that midnight), `realized`, `unrealized`, `charges` and `net`;
- `accounts[]`: per account, the same arrays summed over its runs in the totals at every minute any of them has a point. A run counts from its first point, holds its last value between points and keeps its final value after it ends: a run that stopped at 11:02 still owns what it made.
- The totals are the day's trading runs: the ones `GET /api/Strategy/runs?fromDate=…&toDate=…` lists for that day, which the Desk's day figures are summed from, so the curve ends where the figure stands. A manual book opened on an earlier day is returned, but left out of the totals (its net is its whole life, not the day), and so is an alert-only run.

Scoped like the run list: a trader gets their own runs whatever `userId` they pass; an admin gets every account's, or one account's.

In the console the series is drawn twice (`web/src/lib/pnlSeries.ts` turns it into points, gaps and a day axis). The Desk's **Day P&L** draws each account's net through the day on the IST axis, 09:15 to 15:30 and on to the MCX close once an MCX run has points in the evening, with now marked. **Trade → Runs → Tracks** (`/trade/runs?view=tracks`) draws every run of the day as a track on the same axis, under NIFTY's one-minute closes and the accounts' curves: a track is a grid cell (an account's runs of one strategy on one underlying, a restart adding to it), its line the net on one shared square-root scale, its ticks the fills from the runs' order ledgers, its end how the last run stopped (`web/src/lib/tracks.ts`). Where no strategy run has a point while one was live, both break the line and say when: a gap is the recorder's, never the market's.

### 10. Every open leg, on every underlying
`GET /api/Positions/open` answers with every open leg of the live runs (Running or Stopping) and manual books in scope, whatever they are written on. The Desk used to ask `/api/OptionChain/positions` once per underlying it knew of, so a leg on anything else (a crude future carried into the book, a share) never reached it.

Each leg goes through `PositionViewBuilder`, the run card's builder: run id, strategy, `isManualBook`, account, symbol, underlying, expiry, strike, option type and contract label, direction (`LONG`/`SHORT`), lots, lot size and quantity, entry, the mark (latest live quote, else the stored mark) with `markUtc` and `markAgeSeconds` to the answer's `asOfUtc`, the unrealized P&L at that mark (null while no mark exists), the carry tick and `carriedFromRunId`/`carriedFromStrategy`, the leg's own stop-loss and target, when it was opened, and its `greeks` (see section 6; null when no source can price it). The greeks are worked out in one batch for all the legs; an MCX option costs one more lookup, for the future it is written on.

Behind the strategies grant. A trader sees their own legs whatever `userId` they pass; an admin sees every account's, or one account's with `userId`.

In the console, **Trade → Positions** (`/trade/positions`, one page for every role) lists the answer by account and then by run, the manual book first: each leg with its mark and the mark's age (stale past 30 s, and said so), its open P&L before exit charges, its stop and target, delta with IV, theta and vega in rupees, the **Carry** tick (it can be changed there) and **Square off**. An admin can narrow it to one account. The Desk's open-legs panel and the grid's carried marks read the same answer.

**Trade → Orders** (`/trade/orders`) is the day's orders across runs. There is no orders-across-runs endpoint, so the page reads each run's ledger (`GET /api/Strategy/runs/{runId}/orders`) for the day's runs, the manual books holding an open leg and the viewer's own book, and merges them (`web/src/lib/orders.ts`). Another account's manual book with no open leg is not asked, and the page says so. On a day without a session it shows the last day with runs, like the Desk. Both pages replaced the v1 Simulator pages (a run picked from a list); their old URLs redirect, and `/trader/runs/{id}` goes to the run's own page.

### 11. Paper fills: the spread, and how old a quote may be
A live run's fill is priced by the API from the contract's latest quote (`live_quotes_latest`), not taken as the runner sent it (`PaperTradingService`, `PaperFillPricing`):
- a SELL fills at the **bid** and a BUY at the **ask** when the quote has that side of the book;
- otherwise at the last trade **less** (SELL) or **plus** (BUY) a half-spread: `LTP × (1 − HalfSpreadFraction)` / `LTP × (1 + HalfSpreadFraction)`. Some SENSEX contracts quote no book at all; a crossed book (bid above ask) is not used either;
- a contract with no quote row at all fills at the price the signal carried, across the same half-spread.

Net P&L already took the statutory charges off (`RunCharges`); the spread is the other half of what a round trip costs, and leaving it out flattered exactly the strategies that trade most. Fills booked before 28 Sep are left as they were. Replays (bar closes) and the manual ticket (its limit, or the bid or ask it showed the person) fill at the price they give.

Each order row says how it was priced, in `paper_orders.MetadataJson`: `rule` (`bid`, `ask`, `ltp-less-half-spread`, `ltp-plus-half-spread`, `signal-…`, `mark-…`, `entry-…`, or `signal` for a price filled as given), `note` — the sentence a run view can show as it is ("filled at the bid", "LTP 101.20 less half-spread (0.15%)") — `quoteAgeSeconds` and, when it applies, `staleQuote`. `RequestedPrice` is what the runner asked for.

**Stale quotes.** On 24 Sep the feed stalled from 11:27:36 to 11:34:06 and every quote froze while still looking current. While the contract's market is open, a quote older than `MaxQuoteAgeSeconds` now prices no strategy fill: the leg is left unpriced and the signal refused with the quote's age ("its latest quote is 384 s old; while the market is open a fill needs one under 60 s"), and the runner puts the strategy back as it was (section 12). The square-offs a person or the close asks for — the Stop button, closing a leg by hand, the 15:30 / MCX close, the risk guard's closes, the kill switch — always fill, on an old quote if that is all there is, and the order says "priced on a stale quote (N s old)": a leg left open overnight is worse than one closed at a stale price. Out of market hours the last quote is the only price there is; it is used, with the same note.

**The risk guard** judges a leg only on a fresh mark. A position's mark carries the time of the quote it came from (`UpdatedUtc`), so a frozen quote re-applied every sweep still shows its age. A position marked from a quote older than `StrategyRunner:RiskGuardMaxMarkAgeSeconds` is skipped by its own stop/target and the leg rules, and its group by the group rules, until the quote moves again. The run log and the API log say so once per stall, and the owner gets one message ("Risk rules paused — Ghost on NIFTY"); the overall rules and the market-close square-off still apply.

| Setting | Default | What it does |
|---|---|---|
| `PaperFills:UseBidAsk` | `true` | Fill a SELL at the bid and a BUY at the ask when the quote has them. |
| `PaperFills:HalfSpreadFraction` | `0.0015` | Half-spread (0.15%) a fill pays when there is no usable bid or ask. |
| `PaperFills:MaxQuoteAgeSeconds` | `60` | While the market is open, the oldest quote a strategy's fill may be priced from. |
| `StrategyRunner:RiskGuardMaxMarkAgeSeconds` | `30` | The oldest mark the guard judges a leg or a group on. |

### 12. Booking signals, and the runner's saved state
A strategy changes its own state inside `on_bar`: by the time the runner posts an `OPEN_GROUP`, the strategy already believes the group is open. So every signal is booked exactly once, or the strategy is told it was not (`strategies/signal_booking.py`):
- Each signal is posted with a `clientSignalId` (a uuid, the same on every retry). The API books an id once per run — it looks the id up under the run's lock, and a unique index on `(SimulationRunId, ClientSignalId)` backs it — and answers a repeat with the row it already has. The signal row is written in the same transaction as its fills, so a refused signal leaves nothing behind.
- A lost answer (connection error, timeout, 502/503/504) is retried: an `OPEN_GROUP` for about 5 s, sent again **without prices** so the API prices it from the quote as it is then; a `CLOSE_GROUP` for about a minute. An answer lost at the deadline gets one more post with the same id, since only the API can say whether it was booked.
- Before `on_bar` the runner keeps a deep copy of the strategy's state. When a signal of the tick is refused (a 409 rate limit, a stale quote, a stopped run) or still unanswered when its retries run out, the runner hands the strategy that copy, logs `SIGNAL NOT BOOKED`, and sends none of the tick's later signals: the strategy emits them again on a later tick if it still wants them. One case cannot be undone that way — an `OPEN_GROUP` already booked earlier in the same tick, which the restored strategy would open a second time — so then the state is kept and the log says the strategy and the book disagree. The tick's later `CLOSE_GROUP`s are still sent: getting flat never waits, and a close emitted again closes nothing twice.
- A refusal rarely clears by the next tick (a rate limit lasts up to a minute, a stale quote as long as the feed is stalled), and a strategy put back asks again every tick. So after a refused `OPEN_GROUP` the runner holds every OPEN of the run for `OPEN_REFUSAL_COOLDOWN_SECONDS` (environment, default 30) without posting it: the strategy is still put back each tick, the log says `OPEN held for 30 s after refusal: <the API's reason>` once, and the first OPEN after the hold is posted. A `CLOSE_GROUP` is never held.

The runner saves the strategy's state to Redis (`strategy:state:{runId}`) once right after warm-up and after every tick whose signals were booked. A strategy that keeps objects in its state writes and reads them through `state_to_json` / `state_from_json` (SmcStructureBreak and Ghost save their structure readers as the candles they read). A restarted runner recovers a saved state — and skips warm-up — only when it reads back as it was saved; one that lost a value to text, or that its strategy cannot read, is dropped and the runner warms up. When the runner exits, which stops its run, it deletes the run's state, heartbeat and lock; a saved state also expires after three days.

## Module Components

### Python
- `src/AlgoTrading.PythonEngine/strategies/base_strategy.py` — `BaseStrategy` with the catalog attributes (`description`, `category`, `supported_underlyings`, `legs_summary`, `default_lots`, `default_params`) and `lots_from()`.
- `src/AlgoTrading.PythonEngine/strategies/registry.py`, `tools/list_strategies.py` — discovery and catalog output.
- `src/AlgoTrading.PythonEngine/strategies/execution_runner.py` — the live runner.
- `src/AlgoTrading.PythonEngine/strategies/signal_booking.py` — posting a tick's signals with retries and a client id, and putting the strategy back when one is not booked.
- `src/AlgoTrading.PythonEngine/state_management/runner_state.py`, `state_store.py` — what a runner starts from, and the run's keys in Redis.

### .NET
- `src/AlgoTrading.Api/Controllers/StrategyController.cs`, `InstrumentsController.cs`, `PositionsController.cs`
- `src/AlgoTrading.Api/Services/StrategyCatalogService.cs`, `StrategyProcessRegistry.cs`, `StrategyRunControl.cs`, `StrategyRiskGuardService.cs`, `PythonEngineLocator.cs`, `MarketHoursService.cs`, `PositionCarryForward.cs`, `ManualBook.cs`
- `src/AlgoTrading.Api/Services/RunPnl.cs` (a run's realized, unrealized, charges and net), `RunPnlRecorder.cs` (the minute recorder and its hosted service), `RunPnlSeriesBuilder.cs` (the day's series), `OpenPositionsBuilder.cs` (every open leg)
- `src/AlgoTrading.Domain/Entities/RunPnlMinute.cs` — one run's P&L at one minute (`run_pnl_minutes`).
- `src/AlgoTrading.Contracts/Strategies/*.cs` — request/response DTOs.
- `src/AlgoTrading.Infrastructure/Services/LotSizeResolver.cs`, `UnderlyingCatalog.cs`, `PaperTradingService.cs`, `PaperFillPricing.cs`, `PaperFillOptions.cs`, `LocalCsvInstrumentImportService.cs`

### React
- `web/src/pages/strategies/LiveRunnerPage.tsx`, `StrategyLibraryPage.tsx`, `StrategiesOverviewPage.tsx`, `shared.tsx`
- `web/src/pages/strategies/RunCard.tsx` — the run card, with the positions table's Carry column.
- `web/src/pages/strategies/RunTracks.tsx` — the Tracks view of Trade → Runs; `web/src/lib/tracks.ts` (tracks, scale, ends) and `web/src/lib/pnlSeries.ts` (points, gaps, the day axis), shared with the Desk's `pages/desk/PnlChart.tsx`.
- `web/src/pages/trade/PositionsPage.tsx`, `OrdersPage.tsx` — every open leg and the day's orders, across runs and books; `web/src/lib/openPositions.ts` (grouping, sums, mark age) and `web/src/lib/orders.ts` (which ledgers, merged).
- `web/src/lib/queries.ts` (`useStrategies`, `useStartStrategy`, `useStopStrategy`, `useStrategyLive`, `useStrategyLogs`, `useFnoUnderlyings`, `useSetCarryForward`), `web/src/lib/symbols.ts` (`parseOptionSymbol`, `formatContract`), `web/src/lib/carry.ts` (what the tick does where, its tooltip and hints).

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
- A leg carried forward into the manual book leaves its run's leg, group and overall rules behind, and the book has no way yet to give an existing position a stop-loss or target (only the ticket sets one, at order time). Overnight, a carried leg is guarded by nothing but its owner.
