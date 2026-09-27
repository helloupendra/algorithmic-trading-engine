# Desk checkup

A checklist the desk runs on itself at fixed times, and whenever someone asks.
Each item says what was found, in numbers, and when a person has to do
something, what: the console page, the command, the log to read.

[Sentinel](sentinel.md)'s agents are alarms: something is wrong now, and an
incident opens. The checkup is the round an operator makes before the open and
after the close:
- Is the Dhan token good until the MCX close?
- Did every planned run start?
- Were today's forecasts issued and scored?
- Is anything left open that should not be?
- Is a restart owed?
- Is the archive current?

Each answer that needs a person comes with its next step.

Code: `src/AlgoTrading.PythonEngine/sentinel/checkup/`. Console: **System →
Health → Checkups** (`/system/checkups`). API (admin only): `GET /api/Checkups`,
`/latest`, `/{id}`; `POST /api/Checkups/run`.

## When it runs

All times are IST. Each checkup has a window: one that could not run on time
(Sentinel was restarting, the box was down) still runs late inside it. A
morning report at 10:30 still says whether the plan started.

| Checkup | At | Window | Days | Telegram |
| --- | --- | --- | --- | --- |
| Before the open | 08:55 | to 12:00 | NSE trading days | always |
| After the close | 16:00 | to 20:00 | NSE trading days | always |
| End of day | 00:15 | to 03:00 | every day | only when something needs a person |
| Weekly review | 18:00 | to 23:00 | Sundays | always |
| On request | when asked | — | any | no (the person is looking at the page) |

The morning time follows the desk's own morning, so the checkup at 08:55 sees
all three steps:
- the Dhan sign-in, between 08:00 and 08:40;
- the morning job at 08:45;
- the forecasts at 08:50.

The NSE close (15:30) and the forecast scoring (15:50) come before 16:00, and
`market-close.sh` (23:58) comes before 00:15.

Messages go to the desk's system channel (`TELEGRAM_SYSTEM_CHAT_ID`). A message
Telegram does not take is tried again on the following minutes for two hours;
the console has every report either way.

## What each checks

| Item | Before the open | After the close | End of day | Weekly | What it reads |
| --- | :-: | :-: | :-: | :-: | --- |
| Dhan token | ✓ | ✓ | | ✓ | `/api/Providers`, `/api/Dhan/auto-sign-in`: signed in, and good until today's last close (the MCX close from the calendar). On Sunday: whether Monday will sign itself in. |
| FYERS backup | ✓ | | | | Signed in or not. Only a note while the failover is in log mode; worth a look once it is live, since a switch needs it. |
| Live feeds | ✓ | | ✓ | | `/api/Feeds`: one running before the open, none after the last close. |
| Morning plan | ✓ | | | | Every run in `config/morning-plan.txt` live, by account. |
| Market data recorders | ✓ | | | ✓ | `/api/MarketIntelligence/status`: every [recorder](market_intelligence.md) on schedule, failing sources, and how far the 2020 backfills have to go. A stalled news recorder is to do: a headline not recorded today cannot be fetched later. |
| Runs after the close | | ✓ | | | No NSE/BSE strategy run still live after 15:30; crude runs noted until the MCX close. |
| Runs overnight | | | ✓ | | No strategy run live after the day's last close. |
| Strategy legs after the close | | ✓ | | | No NSE/BSE leg open in a strategy run. |
| Manual books | ✓ | ✓ | | ✓ | What is held, and what was ticked to carry. After the close, an NSE leg left open without the Carry tick (the square-off should have closed it). A carried leg with no stop-loss or target. |
| Carried at the close | | ✓ | | | How many strategy legs moved to the manual books. |
| Runs stuck in Pending | ✓ | | | ✓ | Live runs that never left Pending (see [below](#runs-stuck-in-pending)). |
| Legs in ended runs | ✓ | | | ✓ | Open legs whose run has stopped. |
| Forecasts issued / scored | ✓ | ✓ | | | Today's forecasts, and how many have a score. |
| Today's result | | ✓ | | | Per account: runs, trades, net P&L after charges. A fact, not a judgement. |
| Feed failover | | ✓ | | ✓ | Its mode (log or live) and what it did or would have done, from `alert_events`. In log mode the weekly review asks for the decision. |
| Open incidents | ✓ | ✓ | ✓ | ✓ | A high or critical one nobody acknowledged is to do. So is one acknowledged over a day ago and still open. |
| Incident notes | | | | ✓ | Incidents a person closed this week without a cause or a fix written down. |
| Deploys | ✓ | ✓ | | ✓ | The last deploy's outcome, and commits waiting for the deploy gate. |
| Desk supervisor | | ✓ | | ✓ | `desk.sh` reads its code once, at start. If `scripts/desk.sh` or `scripts/lib/desk-common.sh` changed after it started, a restart is owed. The API, the console and the notifier are restarted by the deploy itself. |
| Disk space | ✓ | | | ✓ | Worth a look under 20% free; to do under 10%. |
| Archive to Drive | ✓ | | | ✓ | Today's `logs/archive-<date>.log`; the week's seven. |
| Today's session / holidays this week | ✓ | | | ✓ | The session's hours; holidays in the next seven days, with MCX's half closures. From November, next year's holidays if they are missing. |

## States and the verdict

- **To do** (`fail`): something is wrong and a person has to act.
- **Worth a look** (`warn`): the day can go on.
- **Note** (`info`): a fact to have in mind, such as what is carried overnight.
- **Fine** (`ok`).
- **Not checked** (`skip`): the check does not apply here or now. Examples: no
  FYERS set up on this desk, or forecasts asked for before 08:50.

A report's verdict is the worst of its items: *needs action*, *worth a look*,
or *all good*.

A check that *should* have been possible but was not is never shown as fine:
- **An endpoint answers with an error:** that item is worth a look.
- **The API or the database does not answer at all:** the report adds one
  *to do* item for it, and the checks that needed it are listed as not
  checked. One outage is one line, not ten.
- **A check crashes on a bug:** it becomes a *worth a look* item naming the
  file, and the rest of the report is unaffected.

## Runs stuck in Pending

Before the Live Runner rebuild (3 Sep 2026), a live run was created Pending
and was meant to move on as it started and stopped. Twenty-four never did.
Seven still held 18 "open" legs on contracts that had long expired, and every
page that lists runs or open positions kept showing them. Today a live run is
created Running. So a Pending live run older than a day means something has
gone wrong again, and the checkup reports it.

The fix is `scripts/close-stale-runs.sh`:

```bash
./scripts/close-stale-runs.sh            # list them; changes nothing
./scripts/close-stale-runs.sh --apply    # back up, then close them in one transaction
```

With `--apply`, each open leg becomes Closed with **no P&L booked**. Closing a
leg at a months-old mark would book a profit or loss no one made. Closing with
none keeps every account's booked total exactly as it was. Each run becomes
Stopped at its last activity, with a RUN_STOPPED reason, so the run history
explains itself. Nothing is deleted. The rows are first copied to
`~/backups/stale-runs-<time>/`.

## What it will not do

Like the rest of Sentinel, it changes nothing in production:
- Its database reads run in `READ ONLY` transactions.
- Its API calls are GETs.
- It writes only its own table, `desk_checkups`. The console inserts a
  "requested" row there, and Sentinel claims it within a minute.

Every text it keeps or sends is redacted with the same spec as incidents.

## Running it by hand

```bash
cd src/AlgoTrading.PythonEngine
../../.venv/bin/python -m sentinel.checkup                 # the checks that mean something at any hour
../../.venv/bin/python -m sentinel.checkup --slot morning  # one scheduled checkup (morning, close, night, weekly)
```

It prints the report and stores and sends nothing. The console's **Run a
checkup now** asks the service instead, and the result is kept.

**Status (27 Sep 2026):** built; runs inside the `algotrading-sentinel`
service as its `checkup` agent.
