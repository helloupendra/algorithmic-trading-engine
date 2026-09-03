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
To provide a lag-free experience in the React dashboard:
- The `.NET API` utilizes `Microsoft.AspNetCore.SignalR`.
- Whenever the `LiveDataController` detects a database UPSERT (or through background polling services), it pushes the latest data directly to the web client over WebSockets.
- The React frontend uses `@microsoft/signalr` to connect to `/hubs/livefeed` and directly updates the `react-query` cache, preventing the need for aggressive REST API polling.

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

## Module Components

### Python Scripts
- `src/AlgoTrading.PythonEngine/market_data/live/fyers_streamer.py`: The main entry point for data ingestion.

### .NET C# Services & Controllers
- `src/AlgoTrading.Api/Controllers/LiveDataController.cs`: Handles REST endpoints for live data.
- `src/AlgoTrading.Api/Hubs/LiveFeedHub.cs`: The SignalR Hub for WebSockets.
- `src/AlgoTrading.Infrastructure/Services/LiveDataService.cs`: Contains business logic for fetching quotes, calculating staleness, and auto-expiring contracts.

### React UI Pages
- `web/src/pages/data/DataOverviewPage.tsx`: High-level dashboard showing ingestor health, stored history counts, and stale quotes warnings.
- `web/src/pages/data/LiveFeedsPage.tsx`: The granular control page. Allows adding/removing symbols from the Database Recording List, viewing real-time LTPs, and inspecting raw DB entries.
- `web/src/lib/queries.ts`: Contains the `useLiveFeedSignalR()` hook that manages the WebSocket connection lifecycle.

---

## Summary
The Data Module is fully decoupled, scalable, and self-healing. By leveraging Redis for IPC (Inter-Process Communication), SignalR for UI reactivity, and PostgreSQL for robust time-series aggregation, it is fully capable of driving advanced algorithmic strategies.
