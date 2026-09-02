#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# AlgoTrading — one-command bootstrap for macOS, Linux, WSL and Git Bash.
#
#   ./scripts/setup.sh              full setup
#   ./scripts/setup.sh --refresh    also re-download the instrument masters
#   ./scripts/setup.sh --skip-build skip dotnet restore/build
#
# Safe to re-run: every step is idempotent.
#
# Written for bash 3.2 so it works on a stock macOS shell.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

REFRESH_INSTRUMENTS=0
SKIP_BUILD=0
for arg in "$@"; do
  case "$arg" in
    --refresh)    REFRESH_INSTRUMENTS=1 ;;
    --skip-build) SKIP_BUILD=1 ;;
    -h|--help)    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $arg (try --help)" >&2; exit 2 ;;
  esac
done

# --- pretty output ---------------------------------------------------------
if [ -t 1 ]; then
  BOLD=$'\033[1m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; RESET=$'\033[0m'
else
  BOLD=''; GREEN=''; YELLOW=''; RED=''; RESET=''
fi
step() { printf '\n%s==> %s%s\n' "$BOLD" "$1" "$RESET"; }
ok()   { printf '    %s✓%s %s\n' "$GREEN" "$RESET" "$1"; }
warn() { printf '    %s!%s %s\n' "$YELLOW" "$RESET" "$1"; }
die()  { printf '\n%sERROR:%s %s\n' "$RED" "$RESET" "$1" >&2; exit 1; }

# ---------------------------------------------------------------------------
step "1/7  Checking prerequisites"
# ---------------------------------------------------------------------------
MISSING=""

if command -v docker >/dev/null 2>&1; then
  docker info >/dev/null 2>&1 \
    && ok "docker $(docker --version | awk '{print $3}' | tr -d ,)" \
    || die "Docker is installed but the daemon is not running. Start Docker Desktop and re-run."
else
  MISSING="$MISSING\n  - Docker Desktop       https://www.docker.com/products/docker-desktop/"
fi

if docker compose version >/dev/null 2>&1; then
  COMPOSE="docker compose"
  ok "docker compose $(docker compose version --short 2>/dev/null || echo '')"
elif command -v docker-compose >/dev/null 2>&1; then
  COMPOSE="docker-compose"
  ok "docker-compose (legacy v1)"
else
  MISSING="$MISSING\n  - Docker Compose       bundled with Docker Desktop"
fi

if command -v dotnet >/dev/null 2>&1; then
  DOTNET_MAJOR="$(dotnet --version | cut -d. -f1)"
  if [ "${DOTNET_MAJOR:-0}" -lt 10 ] 2>/dev/null; then
    warn ".NET SDK $(dotnet --version) found, but this solution targets net10.0"
    MISSING="$MISSING\n  - .NET SDK 10          https://dotnet.microsoft.com/download/dotnet/10.0"
  else
    ok ".NET SDK $(dotnet --version)"
  fi
else
  MISSING="$MISSING\n  - .NET SDK 10          https://dotnet.microsoft.com/download/dotnet/10.0"
fi

PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1; then
    if "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)' 2>/dev/null; then
      PY="$candidate"; break
    fi
  fi
done
if [ -n "$PY" ]; then
  ok "Python $($PY -c 'import platform;print(platform.python_version())') ($PY)"
else
  MISSING="$MISSING\n  - Python 3.10+         https://www.python.org/downloads/"
fi

if [ -n "$MISSING" ]; then
  printf '\n%sMissing prerequisites:%s' "$RED" "$RESET" >&2
  printf "$MISSING\n\n" >&2
  die "Install the tools above, then re-run ./scripts/setup.sh"
fi

# ---------------------------------------------------------------------------
step "2/7  Preparing .env"
# ---------------------------------------------------------------------------
if [ -f .env ]; then
  ok ".env already exists (leaving it untouched)"
else
  cp .env.example .env
  ok "created .env from .env.example"

  # Generate real secrets on first run so nothing ships with a known default.
  gen_secret() {
    if command -v openssl >/dev/null 2>&1; then
      openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-48
    else
      LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 48
    fi
  }
  JWT_VALUE="$(gen_secret)"
  DB_VALUE="$(gen_secret | cut -c1-32)"

  # sed -i differs between BSD (macOS) and GNU, so rewrite via a temp file.
  awk -v jwt="$JWT_VALUE" -v db="$DB_VALUE" '
    /^JWT_SECRET_KEY=/     { print "JWT_SECRET_KEY=" jwt; next }
    /^POSTGRES_PASSWORD=/  { print "POSTGRES_PASSWORD=" db;  next }
    { print }
  ' .env > .env.tmp && mv .env.tmp .env
  chmod 600 .env 2>/dev/null || true
  ok "generated a random JWT signing key and Postgres password"
  warn "Add your FYERS_APP_ID and FYERS_SECRET_KEY to .env before trading."
fi

# ---------------------------------------------------------------------------
step "3/7  Starting infrastructure (PostgreSQL/TimescaleDB, Redis, Prometheus, Grafana)"
# ---------------------------------------------------------------------------
$COMPOSE up -d
ok "containers requested"

printf '    waiting for health checks'
DEADLINE=$(( $(date +%s) + 180 ))
while :; do
  DB_STATE="$(docker inspect -f '{{.State.Health.Status}}' algotrading_db 2>/dev/null || echo starting)"
  REDIS_STATE="$(docker inspect -f '{{.State.Health.Status}}' algotrading_redis 2>/dev/null || echo starting)"
  [ "$DB_STATE" = "healthy" ] && [ "$REDIS_STATE" = "healthy" ] && break
  [ "$(date +%s)" -ge "$DEADLINE" ] && { printf '\n'; die "Timed out. Check: $COMPOSE logs timescaledb redis"; }
  printf '.'; sleep 2
done
printf '\n'
ok "PostgreSQL and Redis are healthy"

# ---------------------------------------------------------------------------
step "4/7  Generating appsettings.Local.json from .env"
# ---------------------------------------------------------------------------
"$PY" scripts/_gen_local_settings.py

# ---------------------------------------------------------------------------
step "5/7  Downloading FYERS instrument masters"
# ---------------------------------------------------------------------------
mkdir -p data/instruments
fetch() {
  url="$1"; dest="$2"
  if [ -s "$dest" ] && [ "$REFRESH_INSTRUMENTS" -eq 0 ]; then
    ok "$(basename "$dest") already present ($(du -h "$dest" | cut -f1)) — use --refresh to update"
    return
  fi
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 -o "$dest.part" "$url" || die "Download failed: $url"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$dest.part" "$url" || die "Download failed: $url"
  else
    die "Neither curl nor wget is available to download $url"
  fi
  mv "$dest.part" "$dest"
  ok "$(basename "$dest") ($(du -h "$dest" | cut -f1))"
}
fetch "https://public.fyers.in/sym_details/NSE_CM.csv" "data/instruments/NSE_CM.csv"
fetch "https://public.fyers.in/sym_details/NSE_FO.csv" "data/instruments/NSE_FO.csv"
fetch "https://public.fyers.in/sym_details/BSE_FO.csv" "data/instruments/BSE_FO.csv"
fetch "https://public.fyers.in/sym_details/MCX_COM.csv" "data/instruments/MCX_COM.csv"

# ---------------------------------------------------------------------------
step "6/7  Building the .NET solution"
# ---------------------------------------------------------------------------
if [ "$SKIP_BUILD" -eq 1 ]; then
  warn "skipped (--skip-build)"
else
  dotnet restore AlgoTrading.slnx --nologo -v quiet
  dotnet build   AlgoTrading.slnx --nologo -v quiet --no-restore
  ok "solution built"
fi

# ---------------------------------------------------------------------------
step "7/7  Setting up the Python engine"
# ---------------------------------------------------------------------------
if [ ! -d .venv ]; then
  "$PY" -m venv .venv
  ok "created virtualenv at .venv"
else
  ok ".venv already exists"
fi
./.venv/bin/python -m pip install --quiet --upgrade pip
./.venv/bin/python -m pip install --quiet -r src/AlgoTrading.PythonEngine/requirements.txt
ok "Python dependencies installed"

# ---------------------------------------------------------------------------
cat <<EOF

${GREEN}${BOLD}Setup complete.${RESET}

${BOLD}Next — run these in order:${RESET}

  1. Start the API (leave it running; it applies DB migrations on boot):

       dotnet run --project src/AlgoTrading.Api

  2. In a second terminal, load reference data (expiry rules + instruments):

       ./scripts/load-data.sh

  3. Activate the Python engine in that second terminal and launch it:

       source .venv/bin/activate
       export PYTHONPATH="\$PWD/src/AlgoTrading.PythonEngine"
       python src/AlgoTrading.PythonEngine/algo.py

${BOLD}Services:${RESET}
  API + Swagger   http://localhost:5025/swagger
  Grafana         http://localhost:3000
  Prometheus      http://localhost:9090

EOF
