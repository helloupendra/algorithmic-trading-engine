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

## Running it

```bash
cd src/AlgoTrading.PythonEngine
python3 -m sentinel --once --dry-run     # every agent once; log instead of storing or sending
python3 -m sentinel --only health,trading
python3 -m sentinel                      # watch until stopped
```

It reads the repository's `.env`: `API_BASE_URL`, `ADMIN_USERNAME` /
`ADMIN_PASSWORD` (read-only GETs), `POSTGRES_*` (its tables), `REDIS_*`, and
`TELEGRAM_BOT_TOKEN` / `TELEGRAM_CHAT_ID`. Thresholds can be tuned with
`SENTINEL_MAX_TRADES`, `SENTINEL_MAX_TRADES_<STRATEGY>`, `SENTINEL_MAX_DAY_LOSS`,
`SENTINEL_PUBLIC_URL`, `SENTINEL_PUBLIC_PORTS`, `SENTINEL_CONTAINERS`,
`SENTINEL_SSH_ALLOWED` and `SENTINEL_SECRET_ALLOW`.

**Status (27 Sep 2026):** built and tested, not yet running on the server. It
starts after the fixes from the 27 Sep audit have had a supervised session, so
that it is not the only new thing in a morning.
