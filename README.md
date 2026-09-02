# AlgoTrading Engine

A polyglot, event-driven algorithmic trading platform for Indian equity and
derivatives markets. A .NET 10 backend owns persistence, risk and broker
integration; a Python engine owns live ingestion and strategy execution; Redis
Streams carry ticks between them; TimescaleDB stores the time series; and a
React **web console** operates the whole thing — live feeds, historical data,
instruments and F&O chains from the browser.

> **Trading involves financial risk.** This software is provided for research
> and educational use. Run it in paper/simulation mode until you have validated
> it end to end. See [LICENSE](LICENSE) and [SECURITY.md](SECURITY.md).

---

## Contents

- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Quick start](#quick-start)
- [Connecting your broker account](#connecting-your-broker-account)
- [Running the system day to day](#running-the-system-day-to-day)
- [Project layout](#project-layout)
- [Configuration](#configuration)
- [Manual setup](#manual-setup-without-the-scripts)
- [Verifying the install](#verifying-the-install)
- [Troubleshooting](#troubleshooting)
- [Further documentation](#further-documentation)

---

## Architecture

```
                    ┌──────────────────────┐
   FYERS WebSocket  │  Python Engine       │
   ───────────────► │  fyers_streamer      │
                    └──────────┬───────────┘
                               │ XADD market:ticks
                               ▼
                    ┌──────────────────────┐
                    │  Redis Streams       │
                    └──────────┬───────────┘
                               │ consumer group
                               ▼
   ┌───────────────┐  ┌──────────────────────┐   ┌──────────────────┐
   │ AlgoTrading   │  │ Worker.MarketData    │──►│ TimescaleDB      │
   │ .Api  :5025   │◄─┤ batch tick writer    │   │ (PostgreSQL 15)  │
   │ REST + Swagger│  └──────────────────────┘   └──────────────────┘
   └───────┬───────┘                                      ▲
           │ REST                                         │
           ▼                                              │
   ┌──────────────────────┐                               │
   │ Python strategies    │───────────────────────────────┘
   │ execution_runner     │  signals, paper orders, positions
   └──────────────────────┘

   ┌──────────────────────┐
   │ Web console (React)  │  dev :5173, or served by the API from wwwroot
   │ admin + trader       │──► AlgoTrading.Api REST (polling)
   └──────────────────────┘

   Observability:  Prometheus :9090  ──►  Grafana :3000
```

| Component | Stack | Responsibility |
|---|---|---|
| `AlgoTrading.Api` | .NET 10 | REST API, auth, instruments, expiry resolution, simulation, risk; serves the built web console from `wwwroot` |
| `AlgoTrading.Worker.MarketData` | .NET 10 | Drains the Redis tick stream into TimescaleDB in batches |
| `AlgoTrading.Worker.Strategy` | .NET 10 | Strategy host (placeholder — live strategies run in the Python engine) |
| `AlgoTrading.Backtester` | .NET 10 | Placeholder — backtesting currently runs via the Python CLI tools |
| `AlgoTrading.PythonEngine` | Python 3.10+ | Live FYERS ingestion, option-chain tracking, strategy execution |
| `web/` | React 19 + Vite + TypeScript | Web console (v2 design system): admin modules — Data first — plus the trader screens |

The .NET solution follows a clean-architecture split: `Domain` → `Application`
→ `Infrastructure` → `Api`/`Worker.*`, with `Contracts` holding the DTOs shared
across boundaries.

---

## Prerequisites

The same four tools on every operating system:

| Tool | Version | Check | Install |
|---|---|---|---|
| Docker Desktop | any current | `docker --version` | <https://www.docker.com/products/docker-desktop/> |
| Docker Compose | v2+ | `docker compose version` | bundled with Docker Desktop |
| .NET SDK | **10.0+** | `dotnet --version` | <https://dotnet.microsoft.com/download/dotnet/10.0> |
| Python | **3.10+** | `python3 --version` | <https://www.python.org/downloads/> |

Plus a [FYERS](https://myapi.fyers.in/dashboard) account and API app if you want
live market data. The infrastructure and API start fine without one.

<details>
<summary><b>Platform install shortcuts</b></summary>

**macOS** (Homebrew)
```bash
brew install --cask docker
brew install dotnet python@3.12
```

**Windows** (winget, in an elevated PowerShell)
```powershell
winget install Docker.DockerDesktop
winget install Microsoft.DotNet.SDK.10
winget install Python.Python.3.12
```

**Ubuntu / Debian**
```bash
# Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker "$USER"   # log out and back in afterwards

# .NET 10 SDK
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"

# Python
sudo apt-get install -y python3 python3-venv python3-pip
```

**Arch**
```bash
sudo pacman -S docker docker-compose dotnet-sdk python python-virtualenv
```
</details>

---

## Quick start

Three steps, and the only difference between operating systems is the script
extension.

### 1. Clone and bootstrap

<table>
<tr><th align="left">macOS · Linux · WSL · Git Bash</th></tr>
<tr><td>

```bash
git clone https://github.com/helloupendra/algorithmic-trading-engine.git
cd algorithmic-trading-engine
./scripts/setup.sh
```

</td></tr>
<tr><th align="left">Windows (PowerShell)</th></tr>
<tr><td>

```powershell
git clone https://github.com/helloupendra/algorithmic-trading-engine.git
cd algorithmic-trading-engine
.\scripts\setup.ps1
```

If PowerShell blocks the script, allow local scripts for your user once:
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

</td></tr>
</table>

`setup` is idempotent — re-run it any time. It will:

1. verify all four prerequisites and print install links for anything missing;
2. create `.env` from `.env.example`, generating a random JWT signing key and
   Postgres password on first run;
3. start PostgreSQL/TimescaleDB, Redis, Prometheus and Grafana, then **wait for
   their health checks** rather than guessing with a sleep;
4. generate the git-ignored `appsettings.Local.json` files from `.env`;
5. download the current FYERS instrument masters into `data/instruments/`;
6. `dotnet restore` + `dotnet build`;
7. create `.venv` and install the Python dependencies.

Useful flags: `--refresh` / `-Refresh` re-downloads the instrument masters,
`--skip-build` / `-SkipBuild` skips the .NET build.

### 2. Start the API

Leave this running — it applies the EF Core migrations on boot, so the schema
is created here, not by a separate migration step.

```bash
dotnet run --project src/AlgoTrading.Api
```

Swagger comes up at **<http://localhost:5025/swagger>**.

### 3. Load reference data

In a second terminal:

```bash
./scripts/load-data.sh          # macOS / Linux / WSL
.\scripts\load-data.ps1         # Windows
```

This waits for the API, seeds the derivative expiry calendars, and imports both
instrument masters (~100,000 contracts). Also idempotent.

---

## Connecting your broker account

Live data needs a FYERS app and a daily access token.

1. Create an app at <https://myapi.fyers.in/dashboard>. Set its redirect URI to
   exactly `http://127.0.0.1:5025/api/auth/callback`.
2. Put the credentials in `.env`:
   ```ini
   FYERS_APP_ID=ABCD1234XY-100
   FYERS_SECRET_KEY=YOURSECRET
   ```
3. Regenerate the .NET config and restart the API:
   ```bash
   python3 scripts/_gen_local_settings.py
   ```
4. Open <http://localhost:5025/api/auth/start> in a browser and complete the
   FYERS login. The callback stores the access token in the `broker_sessions`
   table, where both the API and the Python engine read it from.

FYERS access tokens expire daily, so step 4 is a once-a-day action.

---

## Running the system day to day

Each of these wants its own terminal.

| # | What | Command |
|---|---|---|
| 1 | Infrastructure | `docker compose up -d` |
| 2 | API | `dotnet run --project src/AlgoTrading.Api` |
| 3 | Tick writer | `dotnet run --project src/AlgoTrading.Worker.MarketData` |
| 4 | Web console (dev) | `cd web && npm run dev` → <http://localhost:5173> |
| 5 | Python engine (optional CLI) | `python src/AlgoTrading.PythonEngine/algo.py` |

Day-to-day operation is designed to happen **from the web console**: sign in as
admin, connect FYERS under *Broker*, then use the **Data module** —
*Live feeds* starts/stops the ingestor and manages the watchlist,
*Historical* browses coverage and backfills candles from FYERS,
*Instruments & F&O* explores the contract universe. The `algo.py` menu remains
as a terminal fallback for the same operations.

To serve the console from the API itself (one origin, no dev server), run
`./scripts/go-live.sh` — it builds `web/` with relative URLs, copies it into
`src/AlgoTrading.Api/wwwroot`, starts the API and opens a Cloudflare quick
tunnel.

Before running anything Python, activate the virtualenv and set `PYTHONPATH` —
the engine uses absolute package imports, so it will not resolve without it:

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

`algo.py` is an interactive control centre covering the common operations:

```
[1] Start Live Data Ingestor       [6] Clear Entire Watchlist
[2] Open Live Prices Monitor       [7] Start Live Strategy Runner
[3] Add Single Stock to Watchlist  [8] Open Live Strategy Dashboard
[4] Add Equity Group to Watchlist  [9] Exit
[5] Add Option Chain to Watchlist
```

The underlying scripts can also be driven directly:

```bash
# Live tick ingestion from FYERS into Redis + the API
python src/AlgoTrading.PythonEngine/market_data/live/fyers_streamer.py

# Record the ATM option chain during market hours (09:15–15:30 IST)
python src/AlgoTrading.PythonEngine/market_data/options/chain_tracker.py

# Run a strategy
python src/AlgoTrading.PythonEngine/strategies/execution_runner.py \
    --strategy ExampleStraddle --user-id 1

# Live PnL / positions dashboard
python src/AlgoTrading.PythonEngine/tools/strategy_live_terminal_dashboard_v2.py \
    --user-id 1
```

Available strategies include `GhostTangentCrossings` (5m ellipse-tangent
breakout on BANKNIFTY/NIFTY/SENSEX), `LogicEngine` (15m rules + Telegram
alerts) and `ExampleStraddle`. Add your own as `BaseStrategy` subclasses in the
`strategies/` directory — they are auto-discovered.

The live ingestor is supervised for honesty: its heartbeat reports the real
websocket state (`Running` / `Stalled` / `Disconnected`), and a watchdog forces
a full reconnect with a fresh broker token if the socket stays down — so a
dead feed shows up as degraded in the console instead of silently freezing.
`GET /api/LiveData/bars` aggregates 5m/15m bars on read from the stored 1m
live bars.

### The web console

The console (in `web/`) is being rebuilt module by module on a single "v2"
design system — admin experience first. Current state:

| Module | Status | What it does |
|---|---|---|
| **Data** | **v2 — complete** | *Overview* (coverage matrix, pipeline health, needs-attention), *Live feeds* (start/stop the ingestor, index tickers, one merged live watchlist with quotes, diagnostics + process logs, tick/bar inspector), *Historical* (coverage-first candle browser with chart + FYERS backfill incl. ATM±N option chains), *Instruments & F&O* (master search, expiries, CE/PE chain ladder) |
| Strategies, Backtesting, Risk, Alerts, Users, Broker, System | v1 | Functional screens from the previous design, tagged `v1` in the sidebar; each will be rebuilt in turn |
| Trader screens | v1 | Rebuild queued after the admin modules; per-trader module access will then be granted from Users |

The module registry lives in `web/src/lib/modules.ts` — the sidebar, the admin
home grid and (later) per-trader module grants all read from it.

### Service endpoints

| Service | URL | Credentials |
|---|---|---|
| Web console (dev) | <http://localhost:5173> | admin / trader accounts (see [web/README.md](web/README.md)) |
| Web console (served by API) | <http://localhost:5025> | same — deployed by `scripts/go-live.sh` |
| API + Swagger | <http://localhost:5025/swagger> | — |
| Prometheus metrics | <http://localhost:5025/metrics> | — |
| Grafana | <http://localhost:3000> | from `.env` (`admin`/`admin` by default) |
| Prometheus | <http://localhost:9090> | — |
| PostgreSQL/TimescaleDB | `localhost:5432` | from `.env` |
| Redis | `localhost:6379` | from `.env` |
| RedisInsight (optional) | <http://localhost:8001> | `docker compose --profile tools up -d` |

---

## Project layout

```
algorithmic-trading-engine/
├── docker-compose.yml           Infrastructure: TimescaleDB, Redis, Prometheus, Grafana
├── .env.example                 Configuration template — copy to .env
├── AlgoTrading.slnx             .NET solution
│
├── scripts/
│   ├── setup.sh / setup.ps1             One-command bootstrap
│   ├── load-data.sh / load-data.ps1     Expiry rules + instrument import
│   └── _gen_local_settings.py           .env -> appsettings.Local.json
│
├── src/
│   ├── AlgoTrading.Domain/          Entities and domain rules
│   ├── AlgoTrading.Application/     Use cases and interfaces
│   ├── AlgoTrading.Infrastructure/  EF Core, FYERS clients, services, migrations
│   ├── AlgoTrading.Contracts/       Request/response DTOs
│   ├── AlgoTrading.Api/             REST API (:5025), serves web console from wwwroot
│   ├── AlgoTrading.Worker.MarketData/  Redis -> TimescaleDB tick writer
│   ├── AlgoTrading.Worker.Strategy/    Strategy host (placeholder)
│   ├── AlgoTrading.Backtester/         Placeholder (backtests run via Python tools)
│   └── AlgoTrading.PythonEngine/
│       ├── algo.py                  Interactive control centre
│       ├── core/                    Config, API client, metrics
│       ├── market_data/
│       │   ├── live/                fyers_streamer.py — the live ingestor
│       │   ├── options/             ATM option-chain tracker
│       │   └── historical/          FYERS downloader, DB replayer
│       ├── messaging/               Redis stream publisher/subscriber
│       ├── strategies/              Base strategy, runner, Ghost, LogicEngine, Titli
│       ├── state_management/        Strategy state persistence and recovery
│       └── tools/                   Monitors, dashboards, backfill CLIs
│
├── web/                         React web console (v2 design system)
│   └── src/
│       ├── lib/                 api client, query hooks, module registry, symbols
│       ├── components/          shell, icons, charts, shared UI primitives
│       └── pages/
│           ├── data/            Data module: overview, live feeds, historical, F&O
│           ├── admin/           admin home + v1 modules awaiting their rebuild
│           └── trader/          trader screens (v1, rebuild queued)
│
├── tests/                       Unit, integration and backtest projects
├── database/
│   ├── seed/                    Expiry-rule seed SQL
│   └── queries/                 Ad-hoc diagnostic queries
├── data/instruments/            Downloaded FYERS masters (git-ignored)
├── docker/                      Prometheus and Grafana provisioning
└── docs/                        Architecture, deployment and status docs
```

---

## Configuration

`.env` at the repo root is the single source of truth. Nothing else needs
editing.

```
.env  ──┬──►  docker-compose.yml                    (containers, read directly)
        ├──►  appsettings.Local.json                (generated by setup)
        │        └─► AlgoTrading.Api, Worker.MarketData
        └──►  core/config.py via python-dotenv      (Python engine)
```

After changing any .NET-facing value in `.env`, regenerate and restart:

```bash
python3 scripts/_gen_local_settings.py
```

### Key settings

| Variable | Purpose |
|---|---|
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | Database credentials, used by both the container and the apps |
| `REDIS_HOST` / `REDIS_PORT` / `REDIS_PASSWORD` | Redis connection |
| `REDIS_STREAM_NAME` / `REDIS_STREAM_MAXLEN` | Tick stream name and memory cap |
| `FYERS_APP_ID` / `FYERS_SECRET_KEY` / `FYERS_REDIRECT_URI` | Broker API app |
| `JWT_SECRET_KEY` | API token signing key — must be 32+ characters |
| `RISK_MAX_ORDERS_PER_MINUTE` / `RISK_MAX_DAILY_LOSS` | Risk guardrails |

### What is committed and what is not

Committed `appsettings.json` files contain **placeholders only**. Real
credentials live in `.env` and the generated `appsettings.Local.json`, both of
which are git-ignored. Never move a secret into a tracked file.

> **If you change `POSTGRES_PASSWORD` after the database already exists**, the
> running container keeps its original password — Postgres only reads that
> variable when it initialises an empty data directory. Either change it back,
> or wipe the volume with `docker compose down -v` (this deletes all stored
> market data).

---

## Manual setup (without the scripts)

Should you prefer to drive each step yourself:

```bash
# 1. Configuration
cp .env.example .env          # Windows: Copy-Item .env.example .env
#    edit .env and fill in the values

# 2. Infrastructure
docker compose up -d
docker compose ps             # wait until timescaledb and redis are healthy

# 3. .NET configuration + build
python3 scripts/_gen_local_settings.py
dotnet restore AlgoTrading.slnx
dotnet build AlgoTrading.slnx

# 4. Instrument masters
mkdir -p data/instruments
curl -fL -o data/instruments/NSE_CM.csv https://public.fyers.in/sym_details/NSE_CM.csv
curl -fL -o data/instruments/NSE_FO.csv https://public.fyers.in/sym_details/NSE_FO.csv

# 5. Python environment
python3 -m venv .venv
source .venv/bin/activate                     # Windows: .\.venv\Scripts\Activate.ps1
pip install -r src/AlgoTrading.PythonEngine/requirements.txt

# 6. Start the API — this creates the schema via EF Core migrations
dotnet run --project src/AlgoTrading.Api

# 7. In a second terminal: seed and import
docker exec -i algotrading_db psql -U postgres -d algotrading < database/seed/001_expiry_rules.sql
curl -X POST http://localhost:5025/api/Instruments/import-local \
     -H "Content-Type: application/json" \
     -d "{\"filePath\":\"$PWD/data/instruments/NSE_CM.csv\"}"
curl -X POST http://localhost:5025/api/Instruments/import-local \
     -H "Content-Type: application/json" \
     -d "{\"filePath\":\"$PWD/data/instruments/NSE_FO.csv\"}"
```

---

## Verifying the install

```bash
# Containers healthy?
docker compose ps

# API up?
curl -fsS http://localhost:5025/swagger/index.html >/dev/null && echo "API OK"

# Instruments imported? (expect ~100,000)
docker exec -i algotrading_db psql -U postgres -d algotrading \
  -c 'SELECT COUNT(*) FROM instruments;'

# Expiry rules seeded? (expect 2)
docker exec -i algotrading_db psql -U postgres -d algotrading \
  -c 'SELECT "Exchange","Underlying" FROM expiry_rules;'

# .NET tests
dotnet test AlgoTrading.slnx
```

---

## Troubleshooting

<details>
<summary><b>"Docker daemon is not running"</b></summary>

Start Docker Desktop (or `sudo systemctl start docker` on Linux) and wait for
it to report ready, then re-run the setup script.
</details>

<details>
<summary><b>Port already in use (5432, 6379, 3000, 5025)</b></summary>

Something else is bound to the port. Find it:

```bash
lsof -i :5432            # macOS / Linux
netstat -ano | findstr :5432   # Windows
```

Either stop that process, or change the port in `.env`
(`POSTGRES_PORT`, `REDIS_PORT`) and re-run setup. This repository previously
shipped containers named `algo_timescale` / `algo_redis`; if those are still
running from an older checkout, remove them with
`docker rm -f algo_timescale algo_redis`.
</details>

<details>
<summary><b>Password authentication failed for user "postgres"</b></summary>

The Postgres volume was created with a different password than the one now in
`.env`. Postgres only applies `POSTGRES_PASSWORD` when initialising an empty
data directory. Either restore the original password in `.env`, or reset the
volume — **this deletes all stored market data**:

```bash
docker compose down -v
docker compose up -d
```
</details>

<details>
<summary><b><code>ModuleNotFoundError: No module named 'core'</code></b></summary>

`PYTHONPATH` is not set. The engine uses absolute package imports:

```bash
export PYTHONPATH="$PWD/src/AlgoTrading.PythonEngine"        # macOS / Linux
$env:PYTHONPATH = "$PWD\src\AlgoTrading.PythonEngine"        # Windows
```
</details>

<details>
<summary><b><code>RuntimeError: FYERS_APP_ID is not set</code></b></summary>

Add `FYERS_APP_ID` (and `FYERS_SECRET_KEY`) to `.env`, then regenerate the .NET
config with `python3 scripts/_gen_local_settings.py` and restart the API.
</details>

<details>
<summary><b>"running scripts is disabled on this system" (Windows)</b></summary>

```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```
</details>

<details>
<summary><b><code>bad interpreter: /bin/bash^M</code> (WSL / Git Bash)</b></summary>

The shell scripts were checked out with CRLF endings. `.gitattributes` pins
them to LF, so refresh the working tree:

```bash
git rm --cached -r . && git reset --hard
```
</details>

<details>
<summary><b>Instrument import returns <code>skipped</code> for every row</b></summary>

Not an error — the contracts are already in the database and unchanged. The
import is idempotent.
</details>

<details>
<summary><b>No ticks arriving</b></summary>

Work down the chain:
1. Indian markets are open 09:15–15:30 IST on weekdays.
2. FYERS access tokens expire daily — re-run
   <http://localhost:5025/api/auth/start> (or the console's *Broker* page).
   The ingestor's heartbeat now reports `Disconnected`/`Stalled` honestly, so
   the console topbar shows the feed as degraded when this happens.
3. Check the watchlist is not empty — console → *Data → Live feeds*, or
   `algo.py` option 3/4/5.
4. Check the ingestor process output: console → *Data → Live feeds → Feed
   diagnostics*, or `GET /api/Ingestor/logs`.
5. Confirm ticks are reaching Redis:
   `docker exec -it algotrading_redis redis-cli XLEN market:ticks`
6. Confirm `AlgoTrading.Worker.MarketData` is running — nothing reaches the
   `market_ticks` archive without it.
</details>

<details>
<summary><b>Resetting everything</b></summary>

```bash
docker compose down -v          # removes containers AND all data volumes
rm -rf .venv data/instruments/*.csv
./scripts/setup.sh
```
</details>

---

## Further documentation

| Document | Contents |
|---|---|
| [docs/01_ARCHITECTURE_OVERVIEW.md](docs/01_ARCHITECTURE_OVERVIEW.md) | Component responsibilities and data flow |
| [docs/02_LOCAL_DEPLOYMENT_GUIDE.md](docs/02_LOCAL_DEPLOYMENT_GUIDE.md) | Deployment detail beyond the quick start |
| [docs/03_ARCHITECTURE_AND_RISK_MANAGEMENT.md](docs/03_ARCHITECTURE_AND_RISK_MANAGEMENT.md) | Risk controls and safety design |
| [docs/RESEARCH_AND_ARCHITECTURE.md](docs/RESEARCH_AND_ARCHITECTURE.md) | Long-form design rationale and research notes |
| [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md) | Current build status and roadmap |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Development workflow and conventions |
| [SECURITY.md](SECURITY.md) | Vulnerability reporting and security practices |

---

## License

See [LICENSE](LICENSE).
