# Data Module Architecture & Documentation

## Overview
The Data Module is the foundational component of the Algorithmic Trading Engine. It is responsible for subscribing to live market data streams (via broker WebSockets), persisting raw ticks and aggregated bars into the PostgreSQL database, and broadcasting real-time updates to the web frontend using SignalR.

Everything the trading strategies rely on—live pricing, historical data, and order execution signals—is fueled by this module.

## Architecture Stack
The Data Module spans across all layers of the trading engine:
1. **Python Ingestor (`fyers_streamer.py`)**: A standalone Python process that connects to the Fyers WebSocket API. It handles high-frequency data ingestion, manages subscriptions dynamically, and writes data directly to the database.
2. **.NET API (`AlgoTrading.Api`)**: Serves as the middle layer. It provides REST endpoints for managing the watchlist and a SignalR Hub (`LiveFeedHub`) for broadcasting real-time data to connected clients.
3. **React Frontend (`web`)**: Provides a user interface to visualize data health, manage the database recording list (watchlist), and view real-time incoming ticks and bars.
4. **PostgreSQL Database**: Persistent storage for `live_ticks`, `live_bars`, `ingestor_statuses`, and `watchlists`.
5. **Redis Pub/Sub**: Acts as the communication bridge between the .NET API and the Python Ingestor.

---

## Key Features & Workflows

### 1. Database Recording List (Watchlist)
The core logic of the data module revolves around the "Database Recording List" (formerly known as the Watchlist).
- Users add symbols (Equities, Indices, F&O) from the UI.
- The `.NET API` saves the symbol to the `watchlists` table in PostgreSQL.
- The `.NET API` immediately publishes a `watchlist_updates` message to a Redis channel.
- The running `Python Ingestor` listens to this Redis channel, detects the signal, and resyncs its Fyers WebSocket subscriptions without needing a full restart.

### 2. High-Frequency Data Ingestion
Once subscribed, the `Python Ingestor` receives a continuous stream of market ticks. For every tick:
- **Raw Ticks**: It saves the raw tick data into the `live_ticks` table.
- **1-Minute Bars**: It aggregates ticks into continuous 1-minute OHLCV (Open, High, Low, Close, Volume) bars in the `live_bars` table. It uses an `UPSERT` logic—creating a new bar if the minute has rolled over, or updating the existing bar's High, Low, Close, and Volume if it's within the same minute.

### 3. Real-Time UI Broadcasting (SignalR)
The console's websocket is the hub at `/hubs/livefeed` (`LiveFeedHub`), signed-in callers only; the browser sends its token as `?access_token=`. Each connection is sent the prices it asked for and nothing else. Until 28 Sep every tick batch went to every signed-in browser, so a page following three contracts was sent every strike of every chain on the feed.

**Asking for prices** (client → server):

| Method | Returns | What it does |
|---|---|---|
| `Subscribe(string[] symbols)` | `int` | Adds symbols to this connection's set and returns how many it follows. Symbols are trimmed, blanks dropped and case ignored. At most 400 per connection: a call that would go past that adds nothing and fails with a `HubException` whose message can be shown as it is. |
| `Unsubscribe(string[] symbols)` | `int` | Removes symbols; returns how many are left. |
| `SubscribeAll()` | `bool` | Every symbol on the feed, for the pages that show the whole feed. Only for an admin or an account with the market-data module, decided exactly as the market-data endpoints decide it (`ModuleAccess`); `false` otherwise, and nothing changes. |
| `UnsubscribeAll()` | `bool` | Back to the connection's own symbols. |

A connection's set lives in memory (`LiveFeedSubscriptions`) and ends with the connection: after a reconnect the page subscribes again.

**Receiving them** (server → client): `ReceiveTicks`, an array of `{ symbol, lastTradedPrice, bidPrice, askPrice, volume, openInterest, impliedVolatility, exchangeTimestampUtc }`, `symbol` spelled as the feed spells it. The ingest endpoints only queue each tick (`LiveTickDispatcher`, the last tick of a symbol wins); every `LiveFeed:PushIntervalMs` (default 250, never under 50) the dispatcher sends each connection at most one message, with its symbols that changed since the last one, or every changed symbol after `SubscribeAll`. A connection with nothing new is sent nothing. A browser whose previous message is still being written is not sent another on top: what it is owed waits, one price per symbol, until the first is through, so a slow tab never delays the others, and a failed send is logged and forgotten. The singular `ReceiveTick` message is gone.

**Desk events** (server → client): `DeskEvent`, an object `{ kind, runId, userId, symbol, atUtc, detail }` saying that something on the desk changed, so a page fetches again instead of polling fast. `kind` is `order`, `fill`, `run`, `risk`, `position` or `carry`; `runId`, `userId` and `symbol` may be null; `atUtc` is ISO UTC; `detail` is a short line a person can read, or null, and never a secret. On connecting, each connection joins `user:{id}` and an admin's also `role:admin`; an event is sent to the admins and to its run's owner, and an admin who owns the run is told once. Where each kind comes from is in the strategies module (section 13).

The React frontend keeps one connection to `/hubs/livefeed?v=2` (`web/src/lib/live.ts`) and asks it only for the symbols on screen, reference-counted across panels, matched as the hub matches them (trimmed, any case) and asked for again after a reconnect. Pushed prices are laid over the polled answers (positions, run cards and the Live P&L tiles above them, the market pulse, the watchlist, the option chain, the chart's forming candle, which moves only inside the regular session) when they reached the browser after the answer's request was sent (`web/src/lib/asOf.ts`), and desk events (orders, fills, runs, risk trips, carry changes) invalidate the queries they make stale. Polling stays as a slow safety net while the socket is up, and returns to its old pace while it is down; the top bar says "Live" or "Reconnecting — prices may be stale", and the connection retries at once when the API's status check is healthy again. Every (re)connect sends an access token refreshed first if it has expired or is about to, since the API closes a connection when its token expires. Each connection first calls `Unsubscribe([])`: an API from before 28 Sep has no such method, and against it the console is in a "legacy" state, polling at the old pace, folding the old API's broadcast to everyone into the quotes list, and saying "Prices only — fills polled" instead of "Live".

### 4. Automatic Expired Contract Cleanup
Since F&O (Futures & Options) contracts expire frequently, leaving them in the database can bloat storage and cause subscription failures.
- The Data Module includes a dynamic regex-based parsing mechanism in `LiveDataService.cs`.
- Whenever the watchlist is fetched or synced, the system identifies expired derivative contracts based on their symbol nomenclature and automatically deletes them from the `watchlists` table.

### 5. Ingestor Health & State Management
Because the Ingestor is a standalone Python process, its health must be tracked carefully:
- **Heartbeats**: The `fyers_streamer.py` script writes a "heartbeat" to the `ingestor_statuses` table every 5 seconds.
- **Dynamic Status Detection**: The `.NET API` checks the last heartbeat timestamp. If the heartbeat is less than **15 seconds** old, the ingestor is considered `Healthy` and `Running`.
- **External Runs vs API Runs**: 
  - If the ingestor is started via the Web UI, the API tracks its internal OS Process ID and captures its `stdout`/`stderr` logs.
  - If started externally via the terminal (`python3 start-engine.py`), the UI smartly detects it via heartbeats, labels it as "Running externally", and disables the "Stop" button in the UI to prevent zombie processes.

---

### 5a. The Market Pulse (the trader's first screen)

A trader's overview opens on the market, not on the platform's whole watchlist: three index
levels (NIFTY 50, BANK NIFTY, SENSEX), twelve large caps by index weight, and three commodities
(crude oil, gold, silver as the nearest unexpired MCX future). `MarketPulseService` owns that
universe, keeps every symbol on the live feed (`MarketPulseSubscriptionService`, on boot and
hourly), resolves each commodity from the instrument master and rolls it when the contract
expires — retiring the old contract from the feed unless a trader has it on their own list.
`GET /api/MarketPulse` returns the groups with the last saved quote, change and day range per row.

The per-user watchlist (`/api/Watchlist/me`, stored against the user id) is separate and starts
empty; it is for whatever a trader wants on top of the pulse, and it scrolls in place past eight
rows.

### 6. What Is Kept, and For How Long

Everything the platform sees during a session is written down; the only thing with a shelf life is the raw tick table.

| Data | Where | Retention |
| :--- | :--- | :--- |
| Every tick (price, bid/ask, sizes, OHLC, volume, raw payload) | `live_ticks` hypertable | 90 days, compressed after 2 (`LiveTicksRetention90Days` migration) |
| 1-minute bars folded from ticks | `live_bars` | Permanent |
| 1/5/15-minute candles | `candles` | Permanent. Broker backfills carry source `fyers`; the nightly archive writes source `live` and never overwrites a broker row |
| Option chain every ~5 s: price, bid/ask, volume, OI, previous-day OI, IV, delta/gamma/theta/vega | `option_chain_snapshots` | Permanent |
| Latest quote per symbol with IV and greeks | `live_quotes_latest` | Overwritten on every tick (the history is in the chain snapshots) |

**The nightly candle archive** (`NightlyArchiveService`, 23:50 IST, configurable as `Archive:RunAtIst`) turns the day's live 1-minute bars into 1, 5 and 15-minute candles for every symbol that ticked, and asks the broker for its own candles of the index symbols (`Archive:BrokerSymbols`, default NIFTY 50, BANKNIFTY, SENSEX, FINNIFTY). The last archived day is kept in `system_settings` (`archive.candles.lastDay`), so a night the API was down is caught up on its next tick, up to seven days back. Options are the reason this matters: the broker serves no history for an expired contract, so the bars captured live are the only record of what a strike did.

Run it by hand for any day with `POST /api/Backfill/archive?day=YYYY-MM-DD` (admin; add `&broker=false` to skip the broker calls). The response lists what was inserted, updated, or left to the broker.

**Not kept yet:** a live run's equity curve (the `equity-snapshots` endpoint is backtest-only; live P&L is reconstructed from positions and ticks), and OI for underlyings the chain poller is not configured for (FINNIFTY, MCX).

### 7. When a Tick Does Not Arrive

The rule: live data must never stop reaching a running strategy without something saying so. A tick takes two roads out of the feed (`core/live/feed_runner.py`), and each failure on either road is counted, logged, or kept.

1. **Redis stream `market:ticks`**, which the strategy runners read.
2. **`POST /api/LiveData/ticks/upsert-batch`**, which stores it: `live_ticks`, the 1-minute `live_bars`, and `live_quotes_latest`, the quote the runners price legs from.

| Where | What can go wrong | What says so |
| :--- | :--- | :--- |
| Feed | The API refuses a batch or does not answer | `ticksNotStored` in the heartbeat, and for ten minutes after the last failure the heartbeat's error (Data overview) reads "N tick(s) not stored…" when the connection has no error of its own |
| Feed | A tick the feed cannot handle (neither published nor stored) | `ticksRejected` in the heartbeat, and a printed line with the running count |
| Feed | The buffer in front of the API is full and sheds the oldest ticks | `queueDepth` and `ticksDropped` in the heartbeat |
| Feed | The Black-Scholes library does not load, so no option gets IV or greeks | `GREEKS UNAVAILABLE` once on stderr, `greeksUnavailable` in the heartbeat, and the heartbeat's error |
| Runner | A stream entry whose payload is not JSON | Counted; a line at the 1st, 100th, 1000th and every 10,000th |
| Runner | The strategy raises on a tick | A traceback per tick, `tick_errors=N` on the `[STATUS]` line, `algotrading_tick_errors_total` |

The heartbeat counts are the feed's own, since it started. The API used to drop `queueDepth` and `ticksDropped` on the floor — its heartbeat DTO did not have them — and now mirrors all of them per feed on `/metrics`: `algotrading_feed_queue_depth`, `algotrading_feed_ticks_dropped`, `algotrading_feed_ticks_not_stored`, `algotrading_feed_ticks_rejected` and `algotrading_feed_greeks_available` (label `source`, the feed key). They are not stored, so there is no table for them.

**One writer for the latest quote.** The API is the only writer of `live_quotes_latest`, and it refuses a tick whose exchange stamp is older than the stored one — unless the tick is a replay, which runs behind the live stamps on purpose. `market_ticks`, the second copy of every tick, has not been written by the API since 15 Sep; `live_ticks` is the record.

**What a runner does not replay.** A runner reads the stream from `$`: ticks published before it started are not fed to the strategy, because a strategy treats every tick as now and catching up on stale prices would pick stale strikes. This costs nothing in practice — every launch is a new run, a runner that dies has its run closed by the API, and a Redis reconnect inside one runner resumes from the last entry it read. Waiting for option prices before booking a signal takes at most 10 s for all legs together (`SIGNAL_PRICE_WAIT_SECONDS`), and none for a closing signal; the runner's status and feed-stall checks keep running while it waits.

**Fabricated ticks.** `ENABLE_MOCK_TICKS` (off by default) invents prices for symbols no feed can carry, never for a real dated contract or index. Each one carries `rawPayload` `{"mock": true}` and source key `mock`.

**The optional market-data worker** (`AlgoTrading.Worker.MarketData`, not run on the desk) drains the stream into `market_ticks` through a Redis consumer group. It delivers at least once and drops nothing silently: an entry left unacknowledged for `Redis:ClaimMinIdleMs` (60 s) is claimed back and retried, the last of `Redis:MaxDeliveries` (5) tries is made one entry at a time, and then the entry is copied with its reason to `Redis:DeadLetterStreamName` (`market:ticks:dead`; read it with `redis-cli XRANGE market:ticks:dead - +`). A payload that is not JSON goes there at once. A redelivered batch skips ticks already archived. It writes `live_quotes_latest` only with `Redis:ProjectLatestQuotes=true` — for a `db_replayer.py` replay, which publishes to Redis and never reaches the API — and then under the API's ordering rule.

## Module Components

### Python Scripts
- `src/AlgoTrading.PythonEngine/market_data/live/fyers_streamer.py`: The main entry point for data ingestion.

### .NET C# Services & Controllers
- `src/AlgoTrading.Api/Controllers/LiveDataController.cs`: Handles REST endpoints for live data.
- `src/AlgoTrading.Api/Hubs/LiveFeedHub.cs`: The SignalR Hub for WebSockets; `LiveFeedSubscriptions.cs` (who follows what), `LiveTickDispatcher.cs` (the coalesced push) and `SignalRDeskEventPublisher.cs` (desk events) beside it.
- `src/AlgoTrading.Infrastructure/Services/LiveDataService.cs`: Contains business logic for fetching quotes, calculating staleness, and auto-expiring contracts.

### React UI Pages
- `web/src/pages/data/DataOverviewPage.tsx`: High-level dashboard showing ingestor health, stored history counts, and stale quotes warnings.
- `web/src/pages/data/LiveFeedsPage.tsx`: The granular control page. Allows adding/removing symbols from the Database Recording List, viewing real-time LTPs, and inspecting raw DB entries.
- `web/src/lib/live.ts`: The one hub connection: symbol subscriptions, reconnects, desk events, and the `useLivePrices` / `useLiveAll` / `useLiveConnection` hooks.
- `web/src/lib/liveMarks.ts`: Lays pushed prices over polled answers with the server's own arithmetic.

---

## Summary
The Data Module is fully decoupled, scalable, and self-healing. By leveraging Redis for IPC (Inter-Process Communication), SignalR for UI reactivity, and PostgreSQL for robust time-series aggregation, it is fully capable of driving advanced algorithmic strategies.
