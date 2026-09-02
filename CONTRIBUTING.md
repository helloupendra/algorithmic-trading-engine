# Contributing

Thanks for your interest in this project. It is a polyglot monorepo — .NET 10, Python, and React — so please read the section relevant to what you are changing.

> **Licence note.** This project is released under the [PolyForm Noncommercial License 1.0.0](LICENSE). Contributions are accepted under those same terms. If you need a commercial licence, see [Commercial Use](#commercial-use).

## Ground Rules

- **Never commit secrets.** No API keys, tokens, passwords, or `.env` files. See [SECURITY.md](SECURITY.md).
- **Never commit generated output.** No `bin/`, `obj/`, `__pycache__/`, `node_modules/`, `dist/`, or log files.
- **Do not report security vulnerabilities as issues.** Use private reporting — see [SECURITY.md](SECURITY.md).
- Open an issue before starting substantial work, so effort is not duplicated.

## Development Setup

### Prerequisites

| Tool | Version |
| :--- | :--- |
| .NET SDK | 10.0+ |
| Python | 3.11+ |
| Node.js | 20+ |
| Docker + Compose | current |

### First run

The bootstrap script covers everything below — prerequisites, `.env`,
containers, configuration, instrument masters, build and virtualenv:

```bash
./scripts/setup.sh          # macOS / Linux / WSL
.\scripts\setup.ps1         # Windows
```

Then start the API and load reference data:

```bash
dotnet run --project src/AlgoTrading.Api     # terminal 1
./scripts/load-data.sh                       # terminal 2
```

See the [README quick start](README.md#quick-start) for the full walkthrough and
[the manual path](README.md#manual-setup-without-the-scripts) if you prefer to
drive each step yourself.

To work on the Python engine:

```bash
source .venv/bin/activate                              # Windows: .\.venv\Scripts\Activate.ps1
export PYTHONPATH="$(pwd)/src/AlgoTrading.PythonEngine"
```

### Configuring secrets

All configuration lives in a single git-ignored `.env` at the repo root. The
tracked `appsettings.json` files contain placeholders only; `scripts/setup.*`
generates the git-ignored `appsettings.Local.json` from `.env`, and the Python
engine reads the same file through `core/config.py`.

After changing any .NET-facing value, regenerate and restart:

```bash
python3 scripts/_gen_local_settings.py
```

Never put a credential in a tracked file. If you prefer .NET user-secrets for
local development, they also override `appsettings.json` and are stored outside
the repository:

```bash
cd src/AlgoTrading.Api
dotnet user-secrets init
dotnet user-secrets set "Fyers:ClientId"  "<your-app-id>"
dotnet user-secrets set "Fyers:SecretKey" "<your-secret>"
dotnet user-secrets set "Jwt:SecretKey"   "$(openssl rand -base64 48)"
```

## Coding Standards

### C# / .NET

- Follow the layering in [the architecture document](docs/RESEARCH_AND_ARCHITECTURE.md#41-the-net-solution--clean-architecture). Dependencies point **inward** — `Domain` depends on nothing, `Infrastructure` is referenced only from a composition root.
- New operations go in `Application/UseCases/<Area>/` as a single-responsibility use-case class. Controllers bind, delegate, and map — nothing more.
- Public types and members carry XML doc comments.
- Nullable reference types stay enabled. Do not suppress warnings to make a build pass.
- `dotnet format` before committing.

### Python

- Target 3.11+. Type-hint public functions.
- Follow the existing package layout: market data in `market_data/` (live/, options/, historical/), transport in `messaging/`, strategy logic in `strategies/`, persistence of run state in `state_management/`.
- New strategies subclass the contract in `strategies/base_strategy.py`.
- No credentials in source — read configuration through `core/config.py`, and give every `os.getenv` call a **non-sensitive** default.
- Format with `ruff format`; lint with `ruff check`.

### TypeScript / React

- Strict TypeScript. `npm run lint` (`tsc --noEmit`) must pass clean.
- Function components with hooks. Shared types belong in `src/types.ts`.
- All backend calls go through `src/lib/api.ts` — do not scatter `fetch` calls through components.

## Testing

```bash
dotnet test                                   # all .NET suites
dotnet test tests/AlgoTrading.UnitTests       # a single suite
```

Include tests with any change to risk logic, order handling, expiry resolution, or P&L calculation. These paths deal with money — they are not the place for untested changes.

## Commit Messages

[Conventional Commits](https://www.conventionalcommits.org/):

```
feat(worker): add dead-letter handling to tick consumer
fix(risk): apply daily-loss check before order admission
docs(architecture): document replay feed provider
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `perf`, `build`, `ci`.

## Pull Requests

1. Branch from `main` (`feat/…`, `fix/…`).
2. Keep the change focused — one concern per PR.
3. Confirm `dotnet build` and `dotnet test` pass.
4. Confirm no secrets or generated files are staged: `git diff --cached --name-only`.
5. Describe what changed, why, and how you verified it.
6. Update [docs/RESEARCH_AND_ARCHITECTURE.md](docs/RESEARCH_AND_ARCHITECTURE.md) if you changed the architecture.

## A Note on Trading Logic

Changes to strategy, risk, or execution code can cause real financial loss for anyone running this software. Contributions touching those areas should explain the reasoning, state the assumptions, and show backtest or paper-trading evidence where relevant. "It looks right" is not sufficient for code that moves capital.

## Commercial Use

The licence permits noncommercial use only. For commercial licensing, consulting, or a custom deployment, contact the maintainer through GitHub.
