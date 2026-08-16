# AlgoTrading Python Engine

Live data ingestion, option-chain tracking and real-time strategy execution.
This subsystem talks to FYERS over WebSocket, publishes ticks to Redis Streams,
and drives strategies against the .NET API.

For full setup instructions see the [repository README](../../README.md). This
document covers only the engine itself.

---

## Prerequisites

Everything below assumes `./scripts/setup.sh` (or `.\scripts\setup.ps1`) has
already run. Before starting the engine, make sure:

1. **Infrastructure is up** — `docker compose ps` shows `algotrading_db` and
   `algotrading_redis` healthy.
2. **The API is running** — <http://localhost:5025/swagger> responds.
3. **A broker session exists** — visit
   <http://localhost:5025/api/auth/start> and complete the FYERS login.
   Tokens expire daily, so this is a once-a-day step.

## Environment

The engine uses absolute package imports, so `PYTHONPATH` must point at this
directory. Run these from the **repository root**:

```bash
# macOS / Linux
source .venv/bin/activate
export PYTHONPATH="$PWD/src/AlgoTrading.PythonEngine"
```

```powershell
# Windows
.\.venv\Scripts\Activate.ps1
$env:PYTHONPATH = "$PWD\src\AlgoTrading.PythonEngine"
```

> If PowerShell refuses to run the activation script:
> `Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser`

Configuration is read from the **repo-root `.env`** via `core/config.py`. There
is no separate `.env` in this directory — the path is resolved from the module
location, so the engine behaves the same whichever directory you launch it from.

---

## The control centre

Most operations are available through a single interactive entry point:

```bash
python src/AlgoTrading.PythonEngine/algo.py
```

```text
========================================
   🔥 ALGOTRADING CONTROL CENTER 🔥
========================================
[1] Start Live Data Ingestor
[2] Open Live Prices Monitor
[3] Add Single Stock to Watchlist
[4] Add Equity Group to Watchlist
[5] Add Option Chain to Watchlist
[6] Clear Entire Watchlist
[7] Start Live Strategy Runner
[8] Open Live Strategy Dashboard
[9] Exit
========================================
```

Symbols are normalised automatically — type `IDEA` or `HDFCBANK` and the engine
converts it to the FYERS format (`NSE:IDEA-EQ`) before subscribing.

---

## Direct entry points

```bash
# Live tick ingestion: FYERS WebSocket -> Redis Streams
python data_ingestion/fyers_live_stream.py

# Track the ATM ±15 option chain during market hours (09:15–15:30 IST)
python data_ingestion/option_chain_tracker.py

# Replay stored ticks back onto the stream
python data_ingestion/historical_replayer.py \
    --start "2026-06-10T09:15:00" --end "2026-06-10T15:30:00" --speed 10

# Strategy execution
python strategies/execution_runner.py --strategy Titli --user-id 1

# Live PnL / positions dashboard
python tools/strategy_live_terminal_dashboard_v2.py --user-id 1
```

Available strategies: `Titli`, `Titli2Straddle20`, `Titli3Straddle175`,
`TitliMulti50`, `TitliMulti70`, `TitliMulti90`, `TitliQtyAdjustment`.

> Ticks published to Redis are only persisted to TimescaleDB while
> `AlgoTrading.Worker.MarketData` is running:
> `dotnet run --project src/AlgoTrading.Worker.MarketData`

---

## Directory structure

| Path | Contents |
|---|---|
| `algo.py` | Interactive control centre |
| `core/` | Configuration (`config.py`) and Prometheus metrics |
| `data_ingestion/` | FYERS live stream, option-chain tracker, historical replayer |
| `messaging/` | Redis Streams publisher and subscriber |
| `strategies/` | Base strategy contract, execution runner, contract/price resolution |
| `strategies/titli/` | Titli strategy variants |
| `state_management/` | Strategy state models, store and crash recovery |
| `tools/` | Monitors, dashboards, watchlist and order-analysis utilities |

---

## Conventions

- New strategies subclass `strategies/base_strategy.py`.
- No credentials in source. Read configuration through `core/config.py`, and
  give every `os.getenv` call a **non-sensitive** default — or none at all for
  values that must fail loudly, as `require_app_id()` does.
- Format with `ruff format`, lint with `ruff check`.
