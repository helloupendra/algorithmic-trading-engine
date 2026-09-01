# Local Deployment Guide

How to take this repository from a fresh clone to a running trading environment
on Windows, macOS or Linux.

For the three-command path, see the [Quick start](../README.md#quick-start) in
the README. This document explains what each step actually does and how to drive
the pieces individually.

## Prerequisites

| Tool | Version |
|---|---|
| Docker Desktop + Compose v2 | any current |
| .NET SDK | 10.0+ |
| Python | 3.10+ |

---

## Phase 1 — Infrastructure

### 1. Configuration

Everything reads from a single `.env` at the repo root.

```bash
cp .env.example .env          # Windows: Copy-Item .env.example .env
```

`scripts/setup.sh` / `scripts/setup.ps1` create this automatically on first run
and generate a random `JWT_SECRET_KEY` and `POSTGRES_PASSWORD`. Add your FYERS
credentials by hand.

### 2. Start the containers

```bash
docker compose up -d
```

`docker-compose.yml` lives at the repo root so Compose picks up the sibling
`.env` automatically. It provisions:

| Container | Image | Port |
|---|---|---|
| `algotrading_db` | `timescale/timescaledb:latest-pg15` | 5432 |
| `algotrading_redis` | `redis:7-alpine` | 6379 |
| `algotrading_prometheus` | `prom/prometheus` | 9090 |
| `algotrading_grafana` | `grafana/grafana` | 3000 |
| `algotrading_redisinsight` | `redis/redisinsight` | 8001 (profile `tools`) |

Postgres and Redis declare health checks, so `docker compose ps` tells you when
they are genuinely ready rather than merely started.

### 3. Generate the .NET configuration

```bash
python3 scripts/_gen_local_settings.py
```

This writes `appsettings.Local.json` for `AlgoTrading.Api` and
`AlgoTrading.Worker.MarketData` from `.env`. Both files are git-ignored; the
tracked `appsettings.json` files hold placeholders only, so no credential ever
reaches version control.

Re-run this command after changing any .NET-facing value in `.env`.

### 4. Start the API

```bash
dotnet run --project src/AlgoTrading.Api
```

The API applies all EF Core migrations on startup (`Database.MigrateAsync()` in
`Program.cs`), so there is no separate `dotnet ef database update` step and no
need for the `dotnet-ef` tool. It then seeds reference data from
`src/AlgoTrading.Api/SeedData/*.json` — users, strategies, the live watchlist
and equity groups.

Swagger: <http://localhost:5025/swagger>

### 5. Load reference data

With the API running, in a second terminal:

```bash
./scripts/load-data.sh        # macOS / Linux
.\scripts\load-data.ps1       # Windows
```

Two things happen:

1. **Expiry rules** — `database/seed/001_expiry_rules.sql` is piped into
   Postgres via `docker exec`. It carries an `ON CONFLICT` clause, so re-running
   refreshes rather than duplicates. Without these rows the engine cannot
   resolve which option contract an underlying is trading.

2. **Instrument masters** — `data/instruments/NSE_CM.csv` and `NSE_FO.csv` are
   POSTed to `/api/Instruments/import-local`, loading roughly 100,000 equity and
   derivative contracts. The path is sent in a JSON body rather than a query
   parameter so Windows backslashes need no escaping.

The CSVs are **not** committed — FYERS regenerates them daily, so a checked-in
copy would ship stale expiry dates. `scripts/setup.*` downloads current copies
from `https://public.fyers.in/sym_details/`; pass `--refresh` / `-Refresh` to
update them later.

---

## Phase 2 — The Python engine

The engine uses absolute package imports, so `PYTHONPATH` must point at
`src/AlgoTrading.PythonEngine`.

### 1. Environment

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

`scripts/setup.*` creates `.venv` and installs
`src/AlgoTrading.PythonEngine/requirements.txt` for you.

### 2. Broker session

FYERS access tokens are daily. Open <http://localhost:5025/api/auth/start>,
complete the login, and the callback persists the token to `broker_sessions`,
where both the API and the Python engine read it from.

### 3. Control centre

```bash
python src/AlgoTrading.PythonEngine/algo.py
```

An interactive menu covering ingestion, watchlist management, the strategy
runner and the live dashboard.

### 4. Individual entry points

```bash
# Live tick ingestion into Redis Streams
python src/AlgoTrading.PythonEngine/data_ingestion/fyers_live_stream.py

# Record ATM ±15 option strikes during market hours (09:15–15:30 IST)
python src/AlgoTrading.PythonEngine/data_ingestion/option_chain_tracker.py

# Strategy execution
python src/AlgoTrading.PythonEngine/strategies/execution_runner.py \
    --strategy Titli --user-id 1

# Live PnL / position dashboard
python src/AlgoTrading.PythonEngine/tools/strategy_live_terminal_dashboard_v2.py \
    --user-id 1

# Replay stored ticks
python src/AlgoTrading.PythonEngine/data_ingestion/historical_replayer.py \
    --start "2026-06-10T09:15:00" --end "2026-06-10T15:30:00" --speed 10
```

### 5. Tick persistence

Ticks published to Redis are only written to TimescaleDB while the market-data
worker is running:

```bash
dotnet run --project src/AlgoTrading.Worker.MarketData
```

It reads the `market:ticks` stream through a consumer group and writes in
batches.

---

## Phase 3 — Observability

Grafana is provisioned automatically from `docker/grafana/provisioning`, with
Prometheus wired up as a datasource and the AlgoTrading dashboard preloaded.

- Grafana: <http://localhost:3000> — credentials from `.env`
- Prometheus: <http://localhost:9090>
- Raw API metrics: <http://localhost:5025/metrics>

Prometheus scrapes `host.docker.internal:5025` (the .NET API) and
`host.docker.internal:8000` (the Python engine), so the natively-run processes
are visible from inside the container network.

---

## Troubleshooting

See the [Troubleshooting section](../README.md#troubleshooting) of the README,
which covers port conflicts, Postgres password mismatches, `PYTHONPATH` errors,
CRLF line endings on WSL, and how to reset the environment.
