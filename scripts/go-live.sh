#!/usr/bin/env bash
# Takes the app live from this machine, for free:
#   1. Starts the dockerized infra (TimescaleDB, Redis).
#   2. Builds the React web client with relative API URLs and copies it into
#      the API's wwwroot, so the API serves both frontend and backend.
#   3. Starts the API on http://localhost:5025.
#   4. Opens a free Cloudflare quick tunnel and prints the public URL.
#
# Usage: ./scripts/go-live.sh
# Stop with Ctrl+C — it shuts down the API and tunnel (infra keeps running).

set -euo pipefail
cd "$(dirname "$0")/.."

echo "==> Starting infra (TimescaleDB, Redis)..."
docker compose up -d --wait timescaledb redis

echo "==> Building web client..."
(cd web && VITE_API_BASE_URL='' npm run build)

echo "==> Copying web build into API wwwroot..."
rm -rf src/AlgoTrading.Api/wwwroot
mkdir -p src/AlgoTrading.Api/wwwroot
cp -R web/dist/. src/AlgoTrading.Api/wwwroot/

echo "==> Starting API on http://localhost:5025 ..."
dotnet run --project src/AlgoTrading.Api --launch-profile http &
API_PID=$!

cleanup() {
  echo "==> Shutting down..."
  kill "$API_PID" 2>/dev/null || true
}
trap cleanup EXIT

# Wait for the API to answer before opening the tunnel.
echo "==> Waiting for the API to come up..."
for _ in $(seq 1 60); do
  if curl -sf -o /dev/null http://localhost:5025/; then break; fi
  sleep 2
done

echo "==> Opening Cloudflare tunnel (public URL appears below)..."
cloudflared tunnel --url http://localhost:5025
