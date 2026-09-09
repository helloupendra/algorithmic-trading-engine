# Running the platform on AWS

The Mac stays the development machine; the server runs the live desk. Same
layout, same scripts, same domain: `console.snehatra.com` keeps pointing at the
Cloudflare tunnel, which now terminates on the server instead of the Mac. The
FYERS callback URL and every console link stay unchanged.

## What it costs

AWS is not free for this workload. A new account's **Free Plan** carries a credit
(check the exact amount in Billing → Free Tier; it was $100–200 for six months
when this was written) and cannot be charged beyond it — when the credit or the
six months run out, the account has to be upgraded to a paid plan or it stops.
A c7i-flex.large runs about **$62 a month** in Mumbai 24/7 (t4g.medium about $32:
instance ~$25, 40 GB gp3 ~$3, public IPv4 ~$3.6). To make a $200 credit last the six
months, stop the instance outside trading hours (EventBridge Scheduler is free):
22:00–07:00 and weekends off brings c7i-flex.large to ~$25 a month; the console is
offline while it is stopped, trading is not affected. Set a zero-spend budget alert
anyway (Billing → Budgets → Zero spend budget) so nothing surprises you.

## 1. Launch the instance (AWS console)

| Setting | Value |
|---|---|
| Region | **Asia Pacific (Mumbai) ap-south-1** — the exchange and FYERS are here |
| AMI | Ubuntu Server 24.04 LTS (plain, not "Ubuntu Pro"), the architecture of the instance type below |
| Instance type | **c7i-flex.large** (2 vCPU, 4 GB, x86; marked "Free tier eligible" under the Free Plan, ~$62/month against the credit). t4g.medium (Arm, ~$25) is cheaper when the account may launch it. Not Spot: a Spot instance can be taken away mid-session |
| Key pair | create one, download `key.pem`, `chmod 400 key.pem` |
| Network | default VPC, public subnet, **auto-assign public IP: enable** |
| Security group | inbound **SSH (22)** only — from My IP, or from Anywhere when your IP keeps changing (logins are key-only; bootstrap installs fail2ban). **Untick HTTP and HTTPS**: the console is reached through the tunnel, nothing listens on those ports |
| Storage | **40 GB gp3** |

Postgres alone uses 1.3 GB of RAM; the API, the ingestor, the chain poller and
three strategy runners take the rest. 2 GB (t4g.small) is too small.

## 2. Bootstrap the server

```bash
# on the Mac
scp -i key.pem .env ubuntu@<public-ip>:~/.env
ssh -i key.pem ubuntu@<public-ip>

# on the server
curl -fsSL https://raw.githubusercontent.com/helloupendra/algorithmic-trading-engine/main/scripts/aws/bootstrap.sh -o bootstrap.sh
bash bootstrap.sh --tunnel-token '<cloudflare tunnel token>' --env-file ~/.env
```

The tunnel token is the one the Mac uses (Cloudflare → Zero Trust → Networks →
Tunnels → the tunnel → Configure; it is also inside
`~/Library/LaunchAgents/com.algotrading.tunnel.plist` on the Mac). Bootstrap
installs Docker, .NET 10, Node 22, cloudflared, clones the repo, runs
`scripts/setup.sh`, builds, and installs `algotrading-desk.service` (not started
yet). Ten to fifteen minutes; re-run it if a step fails, it continues.

## 3. Move the data

```bash
# on the Mac — everything except raw ticks, plus the encryption keys (~70 MB)
./scripts/aws/migrate-db.sh dump
scp -i key.pem algotrading-*.tar ubuntu@<public-ip>:~/

# on the server
cd ~/algorithmic-trading-engine
./scripts/aws/migrate-db.sh restore ~/algotrading-*.tar
sudo systemctl start algotrading-desk
./scripts/status.sh
```

`restore` starts the API once so the migrations create the schema, loads the
dump, and installs the ASP.NET data-protection keys — the stored FYERS app
credentials, trading PIN and tokens are encrypted with them, so they keep
working without being re-entered.

## 4. Cut over

Once `./scripts/status.sh` on the server is green and
`https://console.snehatra.com` answers from it, stop the Mac's copies so two
desks never run the same morning:

```bash
# on the Mac
./scripts/install-desk.sh --remove
./scripts/desk.sh --stop
launchctl bootout gui/$(id -u)/com.algotrading.tunnel
```

Cloudflare accepts two connectors on one tunnel, so there is no gap: the server
starts serving before the Mac stops.

## Day to day

- `sudo systemctl status algotrading-desk`, `tail -f logs/desk.log`, `./scripts/status.sh` — the same log and status as on the Mac.
- Deploys: push to GitHub; the desk pulls, rebuilds and restarts the API within two minutes, exactly as before.
- Heavy migrations (a rebuild of a big table): apply them by hand first with `dotnet ef database update` while the desk is stopped, then start the desk. The desk restarts an API that takes longer than two minutes to come up, which would interrupt the migration.
- The FYERS sign-in is still a daily manual step at the console before 09:15; the desk waits for it.
- Logs rotate as on the Mac (`logs/api-until-*.log`); raw ticks age out after seven days by TimescaleDB's retention policy.
