# Architecture Overview

This project is an Enterprise-Grade Algorithmic Trading Platform built with a decoupled architecture. It uses **C# / .NET 10** for high-throughput market data ingestion, persistence, and API management, and **Python 3** for quantitative strategy execution.

The two systems communicate entirely via **Redis** (Pub/Sub and Streams), ensuring maximum fault tolerance, language independence, and scalability.

---

## 1. Core Components

### A. The C# Backend (`src/AlgoTrading.Api` & `AlgoTrading.Worker.MarketData`)
The C# Backend is the backbone of the infrastructure.
- **Entity Framework Core:** Manages Users, Strategies, Watchlists, and Portfolios.
- **ReferenceDataSeeder:** Automatically seeds Users and default configurations on startup.
- **Fyers Ingestor Worker:** Connects to the Fyers API websocket, pulling thousands of ticks per second, batching them, and writing them to TimescaleDB.
- **Redis Publisher:** Simultaneously pushes raw market ticks to Redis Streams (`market:ticks`) so the Python Engine can react in real-time.

### B. The TimescaleDB Database (PostgreSQL)
We use the TimescaleDB extension for PostgreSQL to handle hyper-scale time-series data.
- Stores historical `MarketTicks` representing every price change in the options/cash markets.
- Essential for high-fidelity backtesting.

### C. The Python Strategy Engine (`src/AlgoTrading.PythonEngine`)
This is where the actual quantitative strategies live (e.g. The `Titli` Multi-Straddle Strategy).
- **Execution Runner:** Listens to Redis for real-time (or replayed) market ticks.
- **State Management:** Uses Redis to store "Strategy State" (Entry Prices, Active Legs, Unrealized PnL). This means if the Python script crashes, it can instantly restart and pick up exactly where it left off without losing track of open trades!
- **Option Chain Tracker:** A specialized script that watches the Spot index movement and tells the C# Watchlist to dynamically subscribe to At-The-Money (ATM) Option contracts in real-time.

---

## 2. Advanced Features

### Time-Travel Backtesting
Traditional backtesting uses fake "candle" data or mathematical Black-Scholes pricing. We use True Tick Replay.
1. `market_data/historical/db_replayer.py` queries TimescaleDB for specific past days.
2. It pushes these historical ticks back into the Redis stream with an `IsReplay = True` flag.
3. The C# Engine routes them to the Paper Trading simulator but explicitly blocks them from polluting the live database.
4. The Strategy Engine trades against exact historical option prices and IV crush behavior.

### Paper Trading vs Live Trading
Strategies can be launched with different Modes (`LivePaper` vs `Live`).
- In `LivePaper`, the C# Backend intercepts the orders, simulates fill prices based on the most recent tick, and updates the `SimulationRuns` tables.
- Because of the Redis decoupling, the Python code has absolutely no idea whether it is trading real money or fake money. It just emits trade signals.
