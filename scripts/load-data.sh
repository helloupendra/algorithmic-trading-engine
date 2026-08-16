#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# AlgoTrading — load reference data into a running system.
#
# Run this AFTER `dotnet run --project src/AlgoTrading.Api` is up, because the
# API creates the schema (EF Core migrations) on boot.
#
#   ./scripts/load-data.sh
#
# Steps: wait for the API -> seed expiry rules -> import instrument masters.
# Safe to re-run.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

if [ -t 1 ]; then
  BOLD=$'\033[1m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; RESET=$'\033[0m'
else
  BOLD=''; GREEN=''; YELLOW=''; RED=''; RESET=''
fi
step() { printf '\n%s==> %s%s\n' "$BOLD" "$1" "$RESET"; }
ok()   { printf '    %s✓%s %s\n' "$GREEN" "$RESET" "$1"; }
warn() { printf '    %s!%s %s\n' "$YELLOW" "$RESET" "$1"; }
die()  { printf '\n%sERROR:%s %s\n' "$RED" "$RESET" "$1" >&2; exit 1; }

# Read a key out of .env without sourcing it (values may contain spaces).
env_value() {
  [ -f .env ] || return 0
  awk -F= -v key="$1" '
    $0 !~ /^[[:space:]]*#/ && $1 == key {
      sub(/^[^=]*=/, ""); gsub(/^[ \t"'"'"']+|[ \t"'"'"']+$/, ""); print; exit
    }' .env
}

API_BASE_URL="$(env_value API_BASE_URL)"
API_BASE_URL="${API_BASE_URL:-http://localhost:5025}"
POSTGRES_USER="$(env_value POSTGRES_USER)"; POSTGRES_USER="${POSTGRES_USER:-postgres}"
POSTGRES_DB="$(env_value POSTGRES_DB)";     POSTGRES_DB="${POSTGRES_DB:-algotrading}"

# ---------------------------------------------------------------------------
step "1/3  Waiting for the API at $API_BASE_URL"
# ---------------------------------------------------------------------------
DEADLINE=$(( $(date +%s) + 120 ))
printf '    '
until curl -fsS "$API_BASE_URL/swagger/index.html" >/dev/null 2>&1; do
  if [ "$(date +%s)" -ge "$DEADLINE" ]; then
    printf '\n'
    die "API did not respond within 120s.
       Start it first:  dotnet run --project src/AlgoTrading.Api"
  fi
  printf '.'; sleep 2
done
printf '\n'
ok "API is responding"

# ---------------------------------------------------------------------------
step "2/3  Seeding derivative expiry rules"
# ---------------------------------------------------------------------------
if docker exec -i algotrading_db \
     psql -v ON_ERROR_STOP=1 -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
     < database/seed/001_expiry_rules.sql; then
  ok "expiry rules applied (NSE:BANKNIFTY, BSE:SENSEX)"
else
  die "Failed to apply database/seed/001_expiry_rules.sql — is the algotrading_db container running?"
fi

# ---------------------------------------------------------------------------
step "3/3  Importing instrument masters"
# ---------------------------------------------------------------------------
import_csv() {
  label="$1"; file="$2"
  abs="$REPO_ROOT/$file"
  [ -s "$abs" ] || die "$file is missing. Run ./scripts/setup.sh to download it."

  printf '    importing %s (%s) ... ' "$label" "$(du -h "$abs" | cut -f1)"
  # POST the path as JSON rather than a query parameter so paths containing
  # spaces or backslashes need no URL encoding.
  response="$(printf '{"filePath":"%s"}' "$abs" \
    | curl -fsS -X POST "$API_BASE_URL/api/Instruments/import-local" \
           -H "Content-Type: application/json" --data @- )" \
    || die "Import failed for $file"
  printf 'done\n'
  printf '      %s\n' "$response"
}
import_csv "Cash Market"      "data/instruments/NSE_CM.csv"
import_csv "Futures & Options" "data/instruments/NSE_FO.csv"

cat <<EOF

${GREEN}${BOLD}Reference data loaded.${RESET}

Start the Python engine next:

    source .venv/bin/activate
    export PYTHONPATH="\$PWD/src/AlgoTrading.PythonEngine"
    python src/AlgoTrading.PythonEngine/algo.py

EOF
