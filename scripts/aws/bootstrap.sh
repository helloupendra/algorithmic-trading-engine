#!/usr/bin/env bash
# Turns a fresh Ubuntu 24.04 EC2 instance into the AlgoTrading server: the same
# layout as the Mac (docker compose for TimescaleDB + Redis, the API and the
# Python engine native, scripts/desk.sh keeping it all alive and deploying from
# GitHub), reached through the same Cloudflare tunnel — so the domain, the
# FYERS callback URL and every console link stay exactly what they are.
#
# Run ONCE, as the default `ubuntu` user, after copying .env to the home dir:
#
#   scp -i key.pem .env ubuntu@<ip>:~/.env
#   ssh -i key.pem ubuntu@<ip>
#   curl -fsSL https://raw.githubusercontent.com/helloupendra/algorithmic-trading-engine/main/scripts/aws/bootstrap.sh -o bootstrap.sh
#   bash bootstrap.sh --tunnel-token <cloudflare tunnel token> --env-file ~/.env
#
# Then move the data over with scripts/aws/migrate-db.sh (see scripts/aws/README.md).
#
# Idempotent: every step checks before it installs, so re-running after a
# failure continues where it stopped.

set -euo pipefail

REPO_URL="https://github.com/helloupendra/algorithmic-trading-engine.git"
BRANCH="main"
TUNNEL_TOKEN=""
ENV_FILE="$HOME/.env"
PUBLIC_HOST="console.snehatra.com"
REPO_DIR="$HOME/algorithmic-trading-engine"

while [ $# -gt 0 ]; do
  case "$1" in
    --tunnel-token) TUNNEL_TOKEN="$2"; shift 2;;
    --env-file) ENV_FILE="$2"; shift 2;;
    --repo) REPO_URL="$2"; shift 2;;
    --branch) BRANCH="$2"; shift 2;;
    --host) PUBLIC_HOST="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

BOLD=$'\033[1m'; RESET=$'\033[0m'
step() { printf '\n%s==> %s%s\n' "$BOLD" "$1" "$RESET"; }
ok()   { printf '    ok  %s\n' "$1"; }
die()  { printf '    FAILED: %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" -ne 0 ] || die "run as the ubuntu user, not root (sudo is used where needed)"
[ -f "$ENV_FILE" ] || die ".env not found at $ENV_FILE — copy it from the Mac first (scp .env ubuntu@<ip>:~/.env)"
ARCH="$(dpkg --print-architecture)"   # arm64 on t4g, amd64 on t3
export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
step "1/9  System: packages, timezone, swap"
# ---------------------------------------------------------------------------
sudo apt-get update -qq
sudo apt-get install -y -qq git curl jq unzip ca-certificates gnupg python3 python3-venv python3-pip build-essential fail2ban >/dev/null
# SSH is open to the world (the owner's IP changes). Logins are key-only, so
# a guess cannot succeed; fail2ban just stops the guessing from filling the
# log and the CPU. Its default sshd jail is enough.
sudo systemctl enable --now fail2ban >/dev/null 2>&1 || true
# The desk reads the wall clock for 08:45 and 15:30 IST.
sudo timedatectl set-timezone Asia/Kolkata
ok "timezone $(date '+%Z %H:%M')"
# 4 GB of RAM is enough for the platform but not for a build at the same time
# as a live session; swap turns an out-of-memory kill into a slow minute.
if ! swapon --show | grep -q swapfile; then
  sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile >/dev/null && sudo swapon /swapfile
  echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab >/dev/null
  echo 'vm.swappiness=10' | sudo tee /etc/sysctl.d/90-algotrading.conf >/dev/null && sudo sysctl -q -p /etc/sysctl.d/90-algotrading.conf
fi
ok "swap $(swapon --show --noheadings | awk '{print $3}' | head -1)"

# ---------------------------------------------------------------------------
step "2/9  Docker"
# ---------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sudo sh >/dev/null
fi
sudo usermod -aG docker "$USER"
sudo systemctl enable --now docker >/dev/null
ok "docker $(docker --version | awk '{print $3}' | tr -d ,)"

# ---------------------------------------------------------------------------
step "3/9  .NET 10 SDK"
# ---------------------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  sudo bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet >/dev/null
  sudo ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
fi
export DOTNET_ROOT=/usr/share/dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1
grep -q DOTNET_ROOT ~/.profile || printf '\nexport DOTNET_ROOT=/usr/share/dotnet\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1\nexport PATH="$PATH:/usr/share/dotnet:$HOME/.dotnet/tools"\n' >> ~/.profile
ok "dotnet $(dotnet --version)"

# ---------------------------------------------------------------------------
step "4/9  Node 22 (builds the console)"
# ---------------------------------------------------------------------------
if ! command -v node >/dev/null 2>&1 || [ "$(node -v | cut -c2-3)" -lt 22 ]; then
  curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash - >/dev/null
  sudo apt-get install -y -qq nodejs >/dev/null
fi
ok "node $(node -v)"

# ---------------------------------------------------------------------------
step "5/9  cloudflared (the tunnel that carries $PUBLIC_HOST)"
# ---------------------------------------------------------------------------
if ! command -v cloudflared >/dev/null 2>&1; then
  curl -fsSL "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${ARCH}.deb" -o /tmp/cloudflared.deb
  sudo dpkg -i /tmp/cloudflared.deb >/dev/null
fi
if [ -n "$TUNNEL_TOKEN" ]; then
  # One connector per tunnel is enough; installing again replaces the token.
  sudo cloudflared service uninstall >/dev/null 2>&1 || true
  sudo cloudflared service install "$TUNNEL_TOKEN" >/dev/null
  sudo systemctl enable --now cloudflared >/dev/null
  ok "cloudflared service installed (token given)"
else
  ok "cloudflared installed; no --tunnel-token given, service not configured"
fi

# ---------------------------------------------------------------------------
step "6/9  Repository"
# ---------------------------------------------------------------------------
if [ ! -d "$REPO_DIR/.git" ]; then
  git clone --branch "$BRANCH" "$REPO_URL" "$REPO_DIR" >/dev/null
fi
cd "$REPO_DIR"
git pull --ff-only --quiet origin "$BRANCH" || true
cp "$ENV_FILE" .env
# The console is served on the public host; the API must allow it.
if ! grep -q "https://$PUBLIC_HOST" .env; then
  if grep -q '^CORS_ALLOWED_ORIGINS=' .env; then sed -i "s|^CORS_ALLOWED_ORIGINS=\(.*\)|CORS_ALLOWED_ORIGINS=\1,https://$PUBLIC_HOST|" .env
  else echo "CORS_ALLOWED_ORIGINS=https://$PUBLIC_HOST" >> .env; fi
fi
ok "repo at $REPO_DIR ($(git rev-parse --short HEAD))"

# ---------------------------------------------------------------------------
step "7/9  Platform setup (infra, settings, masters, build, venv) — scripts/setup.sh"
# ---------------------------------------------------------------------------
# The docker group membership is not active in this shell yet; run setup under
# a shell that has it.
# Invoked through bash: a fresh clone does not always carry the executable bit.
sg docker -c "bash ./scripts/setup.sh --skip-build" || die "setup.sh failed — read the output above, fix, re-run bootstrap"
dotnet build src/AlgoTrading.Api -v q --nologo >/dev/null || die "dotnet build failed"
( cd web && npm ci --silent && VITE_API_BASE_URL='' npx vite build >/dev/null ) || die "web build failed"
rm -rf src/AlgoTrading.Api/wwwroot && mkdir -p src/AlgoTrading.Api/wwwroot && cp -R web/dist/. src/AlgoTrading.Api/wwwroot/
ok "built"

# ---------------------------------------------------------------------------
step "8/9  The desk as a systemd service"
# ---------------------------------------------------------------------------
mkdir -p "$REPO_DIR/logs"
sudo tee /etc/systemd/system/algotrading-desk.service >/dev/null <<UNIT
[Unit]
Description=AlgoTrading desk: keeps the API up, deploys from GitHub, runs the 08:45 market open
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$REPO_DIR
Environment=HOME=$HOME
Environment=DOTNET_ROOT=/usr/share/dotnet
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=PATH=/usr/local/bin:/usr/bin:/bin:/usr/share/dotnet:$HOME/.dotnet/tools
ExecStart=/bin/bash $REPO_DIR/scripts/desk.sh --daemon
StandardOutput=append:$REPO_DIR/logs/desk.log
StandardError=append:$REPO_DIR/logs/desk.log
Restart=always
RestartSec=15
KillMode=process

[Install]
WantedBy=multi-user.target
UNIT
# The desk starts docker compose and reads .env; it needs sudo only to start
# the docker service if it is down.
echo "$USER ALL=(ALL) NOPASSWD: /usr/bin/systemctl start docker" | sudo tee /etc/sudoers.d/algotrading-desk >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable algotrading-desk >/dev/null
ok "algotrading-desk.service installed (not started yet — restore the database first, see README)"

# ---------------------------------------------------------------------------
step "9/9  Done"
# ---------------------------------------------------------------------------
cat <<MSG

Next (from scripts/aws/README.md):
  1. On the Mac:   ./scripts/aws/migrate-db.sh dump            -> algotrading-YYYYMMDD.tar
  2. Copy it here: scp -i key.pem algotrading-*.tar ubuntu@<ip>:~/
  3. Here:         ./scripts/aws/migrate-db.sh restore ~/algotrading-*.tar
                   (starts the API once to create the schema, loads the data, copies the encryption keys)
  4. Here:         sudo systemctl start algotrading-desk && ./scripts/status.sh
  5. On the Mac:   ./scripts/install-desk.sh --remove; launchctl bootout gui/\$(id -u)/com.algotrading.tunnel
Log out and back in once so your shell picks up the docker group.
MSG
