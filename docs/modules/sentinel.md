# Sentinel

The desk's own watchman. It checks the running system the way an operator
would on a good day — every day, every half minute — and when something is
wrong it opens an **incident**: what happened, where, since when, with the
evidence already gathered and the first thing to try. It sends that to
Telegram, shows it under **System → Incidents**, and closes it again when the
condition clears.

Code: `src/AlgoTrading.PythonEngine/sentinel/`. Console: `/admin/incidents`.
API: `GET/POST /api/Incidents` (admin only).

## What it will not do

- **It changes nothing in production.** It reads, and it writes only its own
  incidents and its heartbeat. A fix is proposed in the incident; a person
  applies it.
- **Nothing it reads is ever executed.** Logs carry text from outside — news
  headlines, vendor messages — and a watchman that acted on what it read could
  be told what to do by a headline. Agents can run only a fixed allowlist of
  read-only tools (`ss`, `df`, `docker ps`, `git log`, …), enforced in
  `sentinel/context.py`.
- **It is rules, not a model.** Every rule is one of this desk's own past
  failures. Rules are cheap, run all day, and are wrong in ways that can be
  read and fixed. A model can be added later to write the prose of an incident;
  it would never be given the controls.

## The agents

| Agent | Every | Watches |
| --- | --- | --- |
| `health` | 30 s | API up and answering authenticated calls; the public site (the stale-tunnel 502); the newest tick per exchange during the session; a feed running at all; memory, disk, load; the database and Redis containers; the desk supervisor |
| `trading` | 60 s | every run the morning plan asks for is alive; no account runs the same strategy on the same underlying twice; runs that stopped early for a reason that is not a normal ending; unusual trade counts; an account's day loss |
| `logs` | 30 s | `api.log`, `desk.log`, the morning job's log, runner and feed logs, tailed incrementally: sign-in 429s, runner crashes, `FEED STALLED`, the Dhan reconnect loop, tracebacks, .NET `fail:` lines, failed jobs, vendor auth errors, and error lines never seen before |
| `security` | 5 min | failed sign-ins, privileged changes (users, roles, grants, password resets, kill switch), SSH logins from new addresses, public listeners, `.env` permissions, secrets in tracked files, vulnerable dependencies (weekly, outside market hours) |

The plan it checks is `config/morning-plan.txt` — the same file
`scripts/market-open.sh` deploys from.

## How a finding becomes an incident

- Each finding has a **fingerprint** — the identity of the problem, not of the
  sighting. The same feed silent at 11:28 and at 11:29 is one incident with two
  sightings, and one Telegram message.
- A sighting at a higher severity **escalates** the incident and sends a
  second message; a milder one never lowers it.
- An open incident an agent stops reporting is counted clean; after enough clean
  checks in a row (per agent) it is **resolved**, with a message.
- A person can acknowledge ("someone is on it") or resolve an incident from the
  console. Resolving one whose condition is still there makes Sentinel open a
  fresh one on its next check — which is the honest answer.
- An agent whose own check crashes opens an incident about itself, and the
  console warns when an agent stops re-checking, so a silent Sentinel never reads
  as a quiet desk. Sentinel records a heartbeat after every round for the same
  reason.
- Evidence is redacted before it leaves the machine: tokens, passwords, JWTs and
  bot tokens are masked, and agents skip lines that carry them.
- An incident that opens, or escalates to high or critical, comes with a
  **context pack** (below).

## The context pack

The first thing anyone does with an incident is look around it — Sentinel's
own advice keeps saying "read logs/api.log around the time above" and "check
logs/desk.log for a deploy or restart at that time". So Sentinel does that
looking itself, with rules and nothing else, and attaches what it found:

- **the last deploy** from `data/deploy-history.json` (what the Deployments page
  shows): commit, time, outcome — and, when it went out within 30 minutes of the
  first sighting, plainly *"deployed 4 min before this was first seen"*;
- **the commit checked out**, from `git log -1` through the read-only allowlist;
- **how many strategy runs were live**, per account (the trading agent's GET);
- up to **eight log lines** from `logs/api.log` and `logs/desk.log` in the two
  minutes before the sighting: errors, warnings, and what the desk did (an API
  restart, a deploy), a repeated line counted once. api.log's lines carry no
  time of their own, so Sentinel notes where each log ends after every round and
  reads only what was written since two minutes before — never the whole file;
- two minutes later, up to **four more** from the two minutes after it — the
  API's first words after a restart, the desk's next move — as lines beginning
  `then`. The message has gone by then, so these reach the console only.

The pack is kept in the incident's evidence as lines starting `context: ` (the
incidents table is shared with the API, so it takes no new column), survives
later sightings, and is replaced only by a newer one. The Telegram message shows
its first five lines under **Around then:**; the console shows all of it.

Every part is best-effort. A missing file, an API that does not answer (it is
then not asked again for five minutes), a machine without git — each makes the
pack shorter, never the incident late or lost. Log lines are redacted like any
evidence, and a line that may carry a credential is dropped whole.

## Weekends, holidays and after the close

Nothing is reported merely because the market is shut. The feed rules follow
the desk's own schedule: a feed is expected only on a weekday the exchange
calendar trades, and only until `market-close.sh` stops everything at 23:35 —
not on a Saturday or Sunday special session, nor on an NSE holiday with an MCX
evening, unless someone starts a feed by hand. The plan is checked only on
weekdays between 09:25 and 15:25. With every market shut, a feed reconnecting
without ticks is the market's silence: it becomes an incident only after three
reconnects in a row (on 16 Sep a feed left running overnight reconnected in a
loop until Dhan blocked the account), and then as medium.

The calendar comes from the API. When the API stops answering — every deploy
that changes it restarts it — Sentinel uses what the calendar said earlier the
same day, kept in `logs/sentinel/calendar.json`, so a holiday stays a holiday;
only with no answer at all that day does it fall back to plain weekdays.

## Running it

```bash
cd src/AlgoTrading.PythonEngine
python3 -m sentinel --once --dry-run     # every agent once; log instead of storing or sending
python3 -m sentinel --only health,trading
python3 -m sentinel                      # watch until stopped, or until its code changes
```

On the server it runs as its own systemd service, **`algotrading-sentinel`**:

```bash
./scripts/install-sentinel.sh            # install or update, enable, start (idempotent)
./scripts/install-sentinel.sh --remove   # stop and uninstall
systemctl status algotrading-sentinel
tail -f logs/sentinel.log                # its output
./scripts/status.sh                      # one line: active, and when it last looked
```

It is a service of its own, not something `desk.sh` starts, on purpose: the
watchman must not die with what it watches — "the desk supervisor is not
running" is one of the things it reports. It runs as the desk's user, on the
repository's `.venv` (the interpreter the strategy runners use), restarts 15 s
after any exit, and is held to `Nice=10`, a quarter of one CPU and 300 MB, so it
cannot crowd out trading on the 8 GB box; past those limits the kernel slows or
restarts Sentinel alone. It cannot gain privileges, and `/usr` and
`/etc` are read-only to it.

**It reloads itself.** The desk deploys from GitHub on its own, and a
long-running process keeps the code it started with. About once a minute the
watch loop checks whether any `sentinel/**/*.py` is newer than what it started
with; if so it logs "code changed — exiting so the service restarts it",
finishes the round, and exits cleanly for systemd to start it on the new code.
Only its own files count: it imports nothing else from the repository, and a
deploy of the API or the console is exactly when it should keep watching, not
blink for a minute. The morning plan needs no restart: it is read afresh on
every check.

It reads the repository's `.env`: `API_BASE_URL`, `ADMIN_USERNAME` /
`ADMIN_PASSWORD` (read-only GETs), `POSTGRES_*` (its tables), `REDIS_*`, and
`TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID`. Thresholds can be tuned with
`SENTINEL_MAX_TRADES`, `SENTINEL_MAX_TRADES_<STRATEGY>`, `SENTINEL_MAX_DAY_LOSS`,
`SENTINEL_PUBLIC_URL`, `SENTINEL_PUBLIC_PORTS`, `SENTINEL_CONTAINERS`,
`SENTINEL_SSH_ALLOWED` and `SENTINEL_SECRET_ALLOW`.

**Status (27 Sep 2026):** running on the server as the `algotrading-sentinel`
service, started 27 Sep 2026.
