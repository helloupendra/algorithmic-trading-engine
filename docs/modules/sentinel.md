# Sentinel

The desk's own watchman. It checks the running system the way an operator
would on a good day — every day, every half minute — and when something is
wrong it opens an **incident**: what happened, where, since when, with the
evidence already gathered and the first thing to try. It sends that to
Telegram, shows it under **System → Incidents**, and closes it again when the
condition clears.

Code: `src/AlgoTrading.PythonEngine/sentinel/`. Console: `/admin/incidents`.
API (admin only): `GET /api/Incidents`, `/summary`, `/{id}`, `/history`;
`POST /api/Incidents/{id}/acknowledge`, `/resolve`, `/notes`.

## What it will not do

- **It changes nothing in production.** It reads, and it writes only its own
  incidents and its heartbeat. A fix is proposed in the incident; a person
  applies it. The one feed fix that is automatic lives in the API, not here:
  `FeedFailoverService` switches a silent Dhan feed to FYERS, once a day, and
  ships as a dry run (see [Dhan connector](dhan_connector.md), "When Dhan goes
  silent during the session"). It measures silence exactly as `feed-silent`
  does — the newest live tick per exchange in `market:ticks`, from the later of
  the tick and the open — but acts later: `feed-silent` opens at 90 s, the
  feed rebuilds its own connection at 120 s, and the failover acts only on
  150 s on two checks in a row.
- **Nothing it reads is ever executed.** Logs carry text from outside — news
  headlines, vendor messages — and a watchman that acted on what it read could
  be told what to do by a headline. Agents can run only a fixed allowlist of
  read-only tools (`ss`, `df`, `docker ps`, `git log`, …), enforced in
  `sentinel/context.py` — including the options that would make an allowed tool
  run a program or write a file (`git grep -O`, `--output`, `--ext-diff`,
  `npm audit fix`).
- **It is rules, not a model.** Every rule is one of this desk's own past
  failures. Rules are cheap, run all day, and are wrong in ways that can be
  read and fixed. A model can be added later to write the prose of an incident;
  it would never be given the controls.

## The agents

| Agent | Every | Watches |
| --- | --- | --- |
| `health` | 30 s | API up and answering authenticated calls; the public site (the stale-tunnel 502); the newest tick per exchange during the session; a feed running at all; memory, disk, load; the database and Redis containers; the desk supervisor |
| `trading` | 60 s | every run the morning plan asks for is alive; no account runs the same strategy on the same underlying twice; runs that stopped early for a reason that is not a normal ending; unusual trade counts; an account's day loss; and that it can see at all: a runs list that has failed for 15 minutes during the session is itself an incident (`trading:blind`), its earlier findings still held rather than resolved |
| `logs` | 30 s | `api.log`, `desk.log`, the morning job's log, runner and feed logs, tailed incrementally: sign-in 429s, runner crashes, `FEED STALLED`, the Dhan reconnect loop, tracebacks, .NET `fail:` lines, failed jobs, vendor auth errors, and error lines never seen before |
| `security` | 5 min | failed sign-ins, privileged changes (users, roles, grants, password resets, kill switch), SSH logins from new addresses, public listeners (all unexpected ports in one incident), `.env` permissions, secrets in tracked files, vulnerable dependencies (weekly, outside 08:30-15:45, and again as soon as a lock file or project file changes, so a fixed dependency clears the same night) |
| `checkup` | 1 min | not an alarm: runs the [desk checkup](checkup.md) — the checklist before the open, after the close, at the end of the day and on Sundays, or when the console asks — and keeps and sends its report; opens no incidents of its own |

The plan it checks is `config/morning-plan.txt` — the same file
`scripts/market-open.sh` deploys from.

## How a finding becomes an incident

- Each finding has a **fingerprint** — the identity of the problem, not of the
  sighting. The same feed silent at 11:28 and at 11:29 is one incident with two
  sightings, and one Telegram message.
- A sighting at a higher severity **escalates** the incident and sends a
  second message; a milder one never lowers it.
- An open incident an agent stops reporting is counted clean; after enough clean
  checks in a row (per agent) it is **resolved**, with a message — unless a
  person resolved it from the console a moment before, in which case there is
  nothing to announce.
- **Not everything is a message.** A low finding is kept for the console only;
  if it escalates to medium or above, that is sent (and so is its end). A
  *notice* — a one-off event such as a privileged change or an SSH login — is
  sent when it opens (unless it is low) and closes on the next check without a
  "resolved" message, since nothing was ever open.
- A person can acknowledge ("someone is on it") or resolve an incident from the
  console. Resolving one whose condition is still there makes Sentinel open a
  fresh one on its next check — which is the honest answer.
- An agent whose own check crashes — or returns something that is not a list of
  findings — opens an incident about itself, and **its other incidents stay as
  they are** that round: a check that did not run is not evidence that a problem
  has gone. The console warns when an agent stops re-checking (after two of its
  checks and a margin: 5 minutes for health, trading and logs, 12 for security,
  which looks every 5), so a silent Sentinel never reads as a quiet desk.
  Sentinel records a heartbeat after every round for the same reason, and a
  round that fails on a bug in Sentinel itself is logged and the next one runs.
- Evidence is redacted before it leaves the machine, by one spec shared by
  Telegram (`sentinel/notify.py`), the API (`IncidentRedaction`), the console
  (`maskSecrets`) and the logs agent, which drops a line whole when the spec
  would mask any of it: `Authorization: Bearer|Basic …`; `Bearer` and a long
  token; the password in `scheme://user:password@`; a key such as `DHAN_PIN`,
  `JWT_SECRET_KEY`, `access_token` or `accessToken` followed by `:` or `=` on
  the same line; Telegram bot tokens (in `/bot…/` URLs too); JWTs. Prose that
  only talks about a token or a password ("Generate a new Dhan token before
  08:45") is left as it is. Each layer has a table-driven test with the same
  cases.
- An incident that opens, or escalates to high or critical, comes with a
  **context pack** (below).

## Getting the message out

- A message Telegram does not take — no network, a 429 — **waits and is sent
  later**: in order, a few per round, after a pause that grows from 15 s to
  5 minutes (or as long as Telegram's own retry-after asks, up to 15). The
  checks never wait for it. One delivered more than a minute and a half late
  says when it was due. A message Telegram refuses as such (a 400) is not tried
  again; the incident is still in the console.
- On start, live incidents whose message never went out (their `NotifiedUtc` is
  empty, and they are above low) are sent — a restart does not swallow them.
- When the **database does not take a finding**, Telegram still hears of the
  problem — once, marked "not stored", and again only if it gets worse; a
  problem the database already holds live is not messaged again. When the
  database is back, the stored incident is not announced a second time. Each
  failed sighting is appended to `logs/sentinel/unstored-incidents.jsonl`,
  capped at 5 MB with one older generation beside it.

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

## Has this happened before? The incident record

Every incident is kept, resolved ones included, one row per **episode** of a
problem: the fingerprint is the problem, and it opens a new row each time it
comes back after being resolved. So the table is the desk's own record of what
went wrong, how often, and what was done about it.

**Notes on each incident.** Three columns a person fills from the console —
**root cause**, **what was done**, and a **fix reference** (a commit sha, a pull
request or a doc link). The Resolve button opens a small form for them, all
optional, before closing the record; **Edit notes** writes or corrects them at
any time, on an open incident or one long resolved, because the cause is often
understood only after the incident has cleared itself. Each field is trimmed,
masked with the same redaction spec as the rest of an incident's text *before*
it is stored, and cut to 2,000 characters (300 for the reference). A field left
out is left alone; an emptied field is cleared. Sentinel never writes these
columns and its sightings never overwrite them. A notes save that races a
status change Sentinel made is refused with a 409, like acknowledge and resolve.

**"Seen before" on every incident.** The list and the detail say, for each
incident, how many earlier episodes of the same fingerprint there were, when the
problem was first seen at all, and how the episode just before this one ended:
when, who closed it (or that Sentinel's checks came back clean), and its notes.
The console shows it as *"Seen before: 3 times, first on 2 Sept 2026; last
time: closed the sixth Dhan socket (24 Sept 2026)"*, and the list marks a
recurring problem beside its title. It is computed from one query per page, not
one per row.

**In the Telegram message.** When Sentinel opens a new incident whose
fingerprint has earlier resolved episodes, the message says so under the
summary — *"Seen before: 3 times, last on 24 Sep 2026, 11:27 IST"* and, when
someone wrote what was done, *"Last time: closed the sixth Dhan socket"* — and
the same lines go into the incident's evidence, beginning `history: `, kept
across later sightings like the context pack. `store.py` reads them from the
`Resolution` and `LastSeenUtc` columns after the incident is stored; a read that
fails costs these lines and nothing else — the incident and its message go out
as before. An escalation is not a new episode and does not repeat them.

**The History tab.** System → Incidents → **History** (`GET
/api/Incidents/history?days=90`, 30 days to a year in the console, up to ten
years by the API) is one row per fingerprint over the window: the loudest
severity it reached, how many episodes and sightings, when it was last seen,
the **mean time to resolve** (first sighting to resolve, over the episodes that
ended — shown with how many that is, since a mean of one is an anecdote), and
whether it is open now. The problems that keep coming back come first. A row
opens to its latest notes and every episode (the latest 50), each with its
dates, how long it lasted, how it ended — resolved by a named person or cleared
because Sentinel's checks came back clean — and its notes. An episode counts in
the window when it was seen in it, or is live now however old.

The columns were added as nullable, so a Sentinel still running an older
`store.py` inserts exactly as before; `IncidentsTableContractTests` checks that,
and that every column `store.py` names exists.

## Weekends, holidays and after the close

Nothing is reported merely because the market is shut. The feed rules follow
the desk's own schedule: a feed is expected only on a weekday the exchange
calendar trades, and only until `market-close.sh` stops everything at 23:58,
after the MCX close — not on a Saturday or Sunday special session, nor on an
NSE holiday with an MCX evening, unless someone starts a feed by hand. The
plan is checked only on weekdays from 09:25, and each line until five minutes
before its own market's close: the NSE and BSE lines until 15:25, the crude
lines until five minutes before the MCX close the calendar gives (23:25 while
the US is on daylight saving, 23:50 while it is not). That follows the desk,
which since 27 Sep stops the NSE and BSE runs at 15:30 and the crude runs at
the MCX close; a crude run's "MCX closed (23:30 IST)" is an ending on purpose,
and one that dies in the afternoon stays reported through the evening rather
than being let go at 15:30 with the index runs. With every market shut, a
feed reconnecting without ticks is the market's silence: it becomes an
incident only after three reconnects in a row (on 16 Sep a feed left running
overnight reconnected in a loop until Dhan blocked the account), and then as
medium. With a market open
it takes two in a row, not one: a single reconnect that carried nothing is a
blip, and a real outage is caught by the 90-second silent-feed rule anyway.

Readings that hover at a line do not flap: memory, load and an account's day
loss clear only once they are back inside 80% of the line, and critical memory
needs two readings in a row.

The calendar comes from the API. When the API stops answering — every deploy
that changes it restarts it — Sentinel uses what the calendar said earlier the
same day, kept in `logs/sentinel/calendar.json`, so a holiday stays a holiday;
only with no answer at all that day does it fall back to plain weekdays.

## Running it

```bash
cd src/AlgoTrading.PythonEngine
python3 -m sentinel --once --dry-run     # every agent once; log instead of storing or sending
python3 -m sentinel --only health,trading
python3 -m sentinel                      # watch until stopped, or until its code or .env changes
```

A dry run keeps the agents' memory (how far each log has been read, which
activity-log entries were seen, today's calendar) in a temporary copy of
`logs/sentinel/`, so trying Sentinel next to the service takes nothing from it.

**One watcher at a time.** Watching takes a Postgres advisory lock on a
connection of its own. A second `python3 -m sentinel` started while the service
runs finds it taken, logs "another Sentinel is already watching this desk" and
exits — two would send every message twice. Only watching takes the lock:
`--dry-run` stores and sends nothing, and `--once` is one round asked for on
purpose (next to the service, add `--dry-run`). If the service is the one that finds it taken, systemd restarts it every
15 s until the other has gone, and it takes over. A database that cannot be
reached is not a reason to stop watching.

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
with; if so it logs "code changed: … — exiting so the service restarts it",
finishes the round, and exits cleanly for systemd to start it on the new code.
The repository's `.env` counts too: it is read once, at start, so a rotated
`POSTGRES_PASSWORD` or `TELEGRAM_BOT_TOKEN` would otherwise leave Sentinel
failing every write and every message; any edit to it ("settings changed")
restarts Sentinel the same way. Nothing else counts: it imports nothing else
from the repository, and a deploy of the API or the console is exactly when it
should keep watching, not blink for a minute. The morning plan needs no
restart: it is read afresh on every check.

It reads the repository's `.env`: `API_BASE_URL`, `ADMIN_USERNAME` /
`ADMIN_PASSWORD` (read-only GETs), `POSTGRES_*` (its tables), `REDIS_*`, and
`TELEGRAM_BOT_TOKEN` and `TELEGRAM_SYSTEM_CHAT_ID` (the desk's system
channel; `TELEGRAM_CHAT_ID`, the trades channel, when it is not set).
Thresholds can be tuned with
`SENTINEL_MAX_TRADES`, `SENTINEL_MAX_TRADES_<STRATEGY>`, `SENTINEL_MAX_DAY_LOSS`,
`SENTINEL_PUBLIC_URL`, `SENTINEL_PUBLIC_PORTS` (ports and ranges,
`22,8000-8019`), `SENTINEL_CONTAINERS`, `SENTINEL_SSH_ALLOWED` and
`SENTINEL_SECRET_ALLOW`.

Strategy runners serve their Prometheus metrics on `127.0.0.1` since 28 Sep
(`METRICS_BIND_ADDRESS` overrides it). They listened on every address, and the
first trading day would have opened an incident for each of the twenty ports.
The Prometheus container reaches the host through Docker's bridge, so it no
longer scrapes a runner unless `METRICS_BIND_ADDRESS` is set to that bridge
address (and the ports added to `SENTINEL_PUBLIC_PORTS`); it only ever scraped
the first runner's port.

**Status (27 Sep 2026):** running on the server as the `algotrading-sentinel`
service, started 27 Sep 2026.
