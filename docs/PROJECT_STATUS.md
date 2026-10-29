# AlgoTrading Platform — Progress Summary

> **Current status — 29 October 2026**
>
> The sections after this note are the historical progress log from mid-2026; where one of them reads as a statement
> about today, it says what came after. This note is what runs today. Every figure the desk shows is a paper result
> after charges, and nothing in these pages claims a profit.
>
> - **Live paper desk.** Strategy runs trade index options and futures on NSE, BSE and MCX. Fills are at the bid or
>   ask, with the desk's charges. Risk rules apply per run, per group and per leg, and can be changed while a run is
>   live. Manual orders and carried positions sit beside the runs. Every position is squared off at its own exchange's
>   close, and a holiday calendar covers NSE, BSE and MCX, including MCX half days. See
>   [Strategies & live runner](modules/strategies_module.md), [Manual orders](modules/manual_orders.md) and
>   [Risk management](03_ARCHITECTURE_AND_RISK_MANAGEMENT.md).
> - **Data.** Dhan is the backbone feed and records the option chain every minute. FYERS is the fallback and the
>   history source. Backfills are coverage-first. A nightly archive writes candles after the last exchange closes,
>   and a nightly India VIX check fills any gap. See [Data](modules/data_module.md), [Dhan](modules/dhan_connector.md)
>   and [Option chain](modules/option_chain.md).
> - **Market replay.** Any recorded day can be played again through the live strategy runners at 1×, 2×, 5× or 10×,
>   with its prices kept apart from the live ones. A queue plays many days in a row, only when no market the desk
>   trades is open. See [Market replay](modules/market_replay.md).
> - **AI workspace.** Fifteen agents are built, each on a hosted model:
>   - the Desk Assistant, which the owner asks: read-only tools and memory, and five actions that code carries out
>     only after the owner's PIN;
>   - the Trade Reviewer, which checks each run against its written specification, the News Analyst, which turns
>     headlines and filings into structured events, and the Incident Explainer;
>   - four desk analysts: Technical, Options/OI, Macro and Fundamental;
>   - the Data Curator, the Compliance Triage and the Strategy Researcher, which put into words what code measured;
>   - the AI Trader, and the three checks around its paper orders: the Validator, the Risk Memo and the Execution
>     Reporter.
>
>   A daily check and a weekly exam with held-out questions grade the Assistant, and one Telegram digest a day
>   summarises the agents' work. A lesson is kept only when a test shows it helps. See
>   [AI workspace & agents](modules/ai.md).
> - **AI Trader, on paper.** While it is switched on, every ten minutes from 09:20 to 15:00 IST on trading days it
>   reads a market brief built by code and proposes one action, and code checks the plan against fixed limits. Since
>   10 Oct 2026 (`Ai:AiTraderExecute`, on by default) what the limits allow is placed as paper orders in its own
>   ₹5 lakh paper account, never another's: an exit or a strategy stop at once; a buy or a strategy start only after
>   the Validator, asked three times, has accepted it (two rejections, or no majority, block it), the Risk Memo has
>   read it (it may only tighten the plan), and the limits have judged it again on the fresh book. It never sends an
>   order to a broker. With the switch off, and always in a market replay, it places nothing and a shadow book keeps
>   its allowed buys as if placed. Every day it decided on, live or replayed, is scored after charges against a fixed
>   rule and against doing nothing. See [AI Trader](modules/ai_trader.md).
> - **Forecasts and market intelligence.** Daily range and trend forecasts are judged on live results only, never on
>   their backtests. News, exchange filings, global markets, breadth and FII/DII flows are recorded. See
>   [Forecasts & analysis](modules/analysis.md) and [Market intelligence](modules/market_intelligence.md).
> - **Operations.**
>   - Sentinel watches the desk and opens one incident per problem.
>   - Checkups run before the open, after the close and at the end of the day.
>   - A pager sends texts for what cannot wait.
>   - The Today page gives the owner everything on one page.
>   - The auto-deploy never builds or restarts while live runs are trading.
>
>   See [Today](modules/today.md), the [daily timetable](modules/desk_timetable.md), [Sentinel](modules/sentinel.md)
>   and [Checkup](modules/checkup.md).
> - **Backtesting.** Backtests check coverage first, can price expired contracts from the exchange expiry calendar,
>   replay the historical option chain, and take per-run rules. See [Backtesting](modules/backtesting_module.md).

## Overview, mid-2026
This project has evolved from a basic broker integration into a working market data and simulator platform foundation. 
At this stage, the system supports:
- Broker authentication and token/session handling
- Historical data fetching and persistence
- Instrument universe import from FYERS symbol master CSV
- Live market data ingestion
- Live tick and bar processing
- Watchlist and latest quote APIs
- Heartbeat, status, and stale data monitoring
- Simulator session creation
- Offline replay execution foundation

---

## 1. Broker Authentication & Session Management
### Completed
- Built FYERS authentication flow
- Generated and stored access tokens
- Added broker session persistence in PostgreSQL
- Replaced in-memory broker session usage with DB-backed session storage

### Current Capabilities
- Authenticate through FYERS
- Generate access token
- Save session in DB
- Read active broker session from API / DB
- Reuse the token across API and worker processes

**Key Outcome:** The platform no longer depends on process-local memory for authentication. This allows API access, worker access, and live ingestor access to all use the same active broker session.

---

## 2. Historical Market Data Layer
### Completed
- Built historical market data integration with FYERS
- Fixed request formatting issues for FYERS history API
- Added proper mapping of candle responses
- Saved historical OHLCV candles into PostgreSQL
- Added duplicate detection logic
- Improved persistence logic to handle inserting new candles, updating changed candles, and skipping unchanged candles

### Current Capabilities
- Request historical candles from FYERS
- Persist OHLCV candles into DB
- Avoid duplicate inserts
- Update existing candles when values change
- Query stored candle history through API

**Key Outcome:** Historical data is now stored locally and can be reused for analysis, backtesting, replay, and future indicator computation.

---

## 3. PostgreSQL Persistence Foundation
### Completed
- Added EF Core + PostgreSQL integration
- Created `TradingDbContext`
- Added migrations and schema evolution flow
- Configured entity mappings

### Current Persisted Data Includes:
- Broker sessions
- Candles
- Instruments
- Live watchlist
- Latest live quotes
- Live ticks
- Live bars
- Live ingestor status
- Simulation runs

**Key Outcome:** The platform now has a proper database-backed foundation instead of temporary/in-memory data handling.

---

## 4. Instrument Universe Layer
### Completed
- Created `Instrument` entity and `instruments` table
- Imported FYERS `NSE_CM.csv`
- Parsed symbol master file and stored instruments in DB
- Added APIs to list instruments, search instruments, and import instruments from local CSV

### Current Capabilities
- Validate whether a symbol exists
- Search symbols from DB
- Support random user-selected symbols
- Prepare for future support of futures, options, and other exchanges/segments

**Key Outcome:** The system now has a real symbol universe, which is required before backtesting, replay, live watchlists, and strategy execution can occur.

---

## 5. Historical Coverage / Backfill Foundation
### Completed
- Built symbol validation using local instrument universe
- Added on-demand historical coverage service foundation
- Added `SymbolSyncState` style coverage approach
- Built the initial backfill orchestration layer

### Current Capabilities
- Validate instrument before backfill
- Check local availability
- Prepare for fetch of missing history slices
- Support data-readiness for future backtest requests

**Key Outcome:** The system is prepared to answer: *“Do I already have data for this symbol and date range?”* which is a key requirement for backtesting and replay.

---

## 6. Market Data Worker
### Completed
- Built background worker for historical market data sync
- Connected worker to DB-backed broker session
- Made worker fetch symbols and sync candles on schedule
- Added logging and improved duplicate/update behavior

### Current Capabilities
- Periodic sync from FYERS
- DB-backed authentication reuse
- Candle persistence through background worker
- Insert/update/skip summary behavior

**Key Outcome:** Historical data can now be synced automatically, not only manually via API.

---

## 7. Live Watchlist Layer
### Completed
- Added `live_watchlist` table and APIs
- Added ability to create, list, and remove watchlist items

### Current Capabilities
- Manage active watchlist symbols through API
- Support small dynamic live symbol set
- Prepare symbols for Python live ingestor subscription

**Key Outcome:** The live data system is now watchlist-driven instead of hardcoded.

---

## 8. Latest Live Quote Layer
### Completed
- Added `live_quotes_latest` table
- Added APIs to upsert latest quote, fetch latest quote by symbol, and fetch all latest quotes

### Current Capabilities
- Maintain latest snapshot for each symbol
- Provide fast latest-quote reads
- Support dashboard-style or UI-style access

**Key Outcome:** The system has a fast live quote read model.

---

## 9. Python Live Data Ingestor
### Completed
- Built Python sidecar for live data ingestion
- Connected to FYERS live WebSocket
- Authenticated using stored broker session token
- Dynamically loaded active watchlist from API
- Subscribed/unsubscribed symbols dynamically
- Pushed live updates into .NET API

### Current Capabilities
- Connect to FYERS WebSocket
- Subscribe to active watchlist symbols
- Receive real-time market messages
- Call backend API to persist live data
- Refresh watchlist automatically
- Continue heartbeat/status updates

**Key Outcome:** The platform now has a working real-time data ingestion pipeline.

---

## 10. Live Data Health / Observability
### Completed
- Added live ingestor heartbeat API
- Added ingestor status storage
- Added APIs for heartbeat, status by source, all statuses, and stale quote detection

### Current Capabilities
- Know whether ingestor is alive
- Know which symbols are currently subscribed
- Know when watchlist was last refreshed
- Detect stale live quote data
- Monitor the health of live ingestion

**Key Outcome:** The live data pipeline is now observable and easier to operate/debug.

---

## 11. Live Tick Storage
### Completed
- Added `live_ticks` table
- Added tick ingestion endpoint
- Stored every incoming live market event
- Preserved raw payload for debugging/replay

### Current Capabilities
- Append live ticks
- Query recent ticks per symbol
- Preserve raw event stream

**Key Outcome:** You now have a raw live event history, not just the latest quote snapshot. This is important for debugging, replay, and future tick-based strategies.

---

## 12. Live Bar Aggregation
### Completed
- Added `live_bars` table
- Implemented 1-minute bar aggregation logic
- Built API to query recent live bars
- Hooked bar updates into tick ingestion flow

### Current Capabilities
- Each tick updates the live tick store, latest quote, and 1-minute live bar
- Query recent bars by symbol/resolution

**Key Outcome:** The platform now has strategy-ready live bar data. This is the bridge between raw ticks, indicator computation, and simulation logic.

---

## 13. Market Session / Trading Hours Service
### Completed
- Built `IMarketSessionService`
- Implemented `MarketSessionService`
- Added API to test session state

### Current Capabilities
- Determine whether market is open
- Return session open/close timestamps
- Determine next market open
- Support initial NSE + CM session rules (Monday to Friday, 09:15 IST to 15:30 IST)

**Key Outcome:** The platform now has a market-hours awareness layer, which is essential for live vs replay mode switching, future stale quote rules, session-aware bar finalization, and simulator mode logic.

---

## 14. Simulator Foundation
### Completed
- Added `SimulationRun` entity and APIs
- Added support for `LivePaper` and `OfflineReplay`
- Added market-session validation for `LivePaper`
- Added DB tracking for simulation runs

### Current Capabilities
- Create simulation runs
- Retrieve one run or list all runs
- Validate mode based on session state (e.g., `LivePaper` is allowed only during market open; `OfflineReplay` works after market close or on demand)

**Key Outcome:** The simulator now has a proper execution context model.

---

## 15. Replay Execution Foundation
### Completed
- Built `IReplayFeedProvider`
- Implemented `ReplayFeedProvider`
- Added `ISimulationRunner` and `SimulationRunnerService`
- Added API to start a simulation run
- Implemented first replay execution skeleton

### Current Capabilities
- Load bars from DB for a simulation run
- Iterate over bars in chronological order
- Mark runs as Pending, Running, Completed, or Failed
- Return execution summary (frames processed, first frame time, last frame time)

**Key Outcome:** The simulator can now execute offline replay runs at the infrastructure level. *(Note: The first version executes the replay loop but does not yet apply strategy logic inside that loop.)*

---

## System Architecture, mid-2026

### Historical Side
`FYERS History API` → `API / Worker` → `PostgreSQL candles` → `Local read / backfill / replay support`

### Live Side
`FYERS WebSocket` → `Python ingestor` → `.NET LiveData API` → `live_ticks` + `live_quotes_latest` + `live_bars` → `Watchlist` + `Heartbeat` + `Stale monitoring`

### Simulator Side
`SimulationRun` → `Market session validation` → `Replay feed provider` → `Simulation runner` → `Offline replay execution skeleton`

**Since then:** vendors sit behind connectors, and Dhan is the primary live feed and records the option chain every
minute. FYERS is the backup feed and the history backfill source. Backtests replay strategies in the Python engine
(`backtest/engine.py`), not in the C# replay skeleton. See [Data](modules/data_module.md),
[Connectors](modules/connectors_module.md) and [Backtesting](modules/backtesting_module.md).

---

## What Was Working, mid-2026

### Data Ingestion
- [x] Historical data
- [x] Live data
- [x] Instrument import

### Data Persistence
- [x] Historical candles
- [x] Live ticks
- [x] Live bars
- [x] Latest quotes
- [x] Simulation runs

### Monitoring
- [x] Ingestor heartbeat
- [x] Ingestor status
- [x] Stale quote detection

### Execution Foundation
- [x] Replay run creation
- [x] Replay run start
- [x] Replay frame iteration

---

## What Was Not Built Yet, mid-2026, and What Came After

**1. Strategy Execution Hook**  
Then: the replay runner iterated bars but did not call a strategy engine.  
Since: a backtest replays any catalogue strategy bar by bar through the same `BaseStrategy.on_bar` contract the live
runner uses ([Backtesting](modules/backtesting_module.md)).

**2. Signal Generation + Signal Storage**  
Then: no signal table existed.  
Since: every signal a run or a backtest sends is stored in `simulation_signals`.

**3. Paper Trading Layer**  
Then: no paper orders, positions, P&L or trade lifecycle.  
Since: `paper_orders` and `paper_positions`, fills at the bid or ask with the desk's charges, and risk rules per run,
group and leg ([Strategies & live runner](modules/strategies_module.md)).

**4. Full Live Session-Aware Bar Finalization**  
Then: live bar processing was not session-aware in every case.  
Since: a minute its exchange was not open gets no live bar, and a holiday calendar covers NSE, BSE and MCX.

**5. Segment Expansion**  
Then: session logic covered NSE cash only.  
Since: index options and futures trade on NSE, BSE and MCX, each squared off at its own exchange's close.

---

## The Roadmap, mid-2026

Every phase below is built. Phase 3 is live on paper only: no run sends an order to a broker.

### Phase 1 — Strategy-Ready Replay
- Add strategy execution interface
- Generate signals from replay bars
- Persist signals

### Phase 2 — Paper Simulation
- Implement paper orders
- Track paper positions
- Implement PnL tracking

### Phase 3 — Live Strategy Mode
- Apply strategy hook to live bars
- Generate live paper-mode signals

### Phase 4 — Segment Expansion
- Support Futures and Options
- Implement more advanced market sessions
- Add more advanced symbol selection logic

---

## Summary, mid-2026
By mid-2026 the project had become a market data and replay simulator backend:
- A historical data platform
- A live data platform
- Session-aware mode validation
- A simulator run model
- An offline replay execution skeleton

What it became is the note at the top of this page.
