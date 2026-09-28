# The pager

[Sentinel](sentinel.md) watches the desk all day and writes to Telegram. A
Telegram message does not wake a sleeping owner. The pager does one thing:
when the live desk is in the kind of trouble that costs money while he sleeps,
it rings his phone (a [CallMeBot](https://www.callmebot.com/) Telegram call)
and says why in the Desk System channel.

Code: `scripts/pager/pager.py`. Tests:
`src/AlgoTrading.PythonEngine/tests/test_pager.py`. Unit:
`scripts/pager/openfno-pager.service`.

It is its own process, `openfno-pager`, running a copy of `pager.py` from
`~/openfno-pager` on the server — outside the deploy path on purpose. A deploy
is gated while runs are live, and the pager has to work on exactly the day a
deploy would break it. From the desk's checkout it reads only `.env` (the
admin sign-in, Telegram, Redis) and `sentinel/clock.py`. Everything it does to
the desk is a GET, and one read-only `XREVRANGE` on the tick stream.

## What rings

Once a minute it reads the desk and judges each rule: *found*, *healthy*,
*could not tell*, or *outside its window*. All times IST.

| Rule | When | Rings when | Grace |
| --- | --- | --- | --- |
| `api-down` | 08:40 to the day's last close | `GET /health` does not answer, or the API has gone down 3 times in 30 minutes (a crash loop the desk keeps restarting, each outage too short to page alone) | 3 min |
| `api-degraded` | same | `/health` answers but every signed-in read fails, or one read has failed 5 checks in a row. `/health` is not a health check: it is the console's `index.html` (the SPA fallback in `Program.cs`) and never touches the database | 3 min |
| `no-broker` | 09:00–15:30, and the MCX evening | neither Dhan nor FYERS is signed in (`isConnected` and `expiresUtc` only: `needsReconnect` means "saved on an earlier date", wrong for Dhan's 24-hour token) | 2 min |
| `no-ticks` | NSE open, 09:17–15:29 | the newest NSE/BSE tick in Redis `market:ticks` — what the strategies read — is over 3 minutes old; the API's index rows stand in only when the stream cannot be read | 3 min |
| `redis-down` | same, and the MCX evening | Redis refuses the connection: every strategy is blind | 3 min |
| `runs-not-up` | 09:30–15:00 | the morning plan has runs and none is live, or from 09:40 fewer than half of them have ever been live today | 2 min |
| `runs-dropped` | 09:20–15:10 | at least 4 of today's plan runs died and they are half the day's peak | 2 min |
| `disk-low` | 08:40 to the last close | under 3 GB free on `/` | 2 min |
| `mcx-no-ticks` | 15:30 to 5 min before the MCX close, when the plan has MCX runs | no MCX tick for 5 minutes | 3 min |
| `mcx-runs-dropped` | same | no MCX run is live and at least one of them died | 2 min |

"Died" is read the way Sentinel's `ended_on_purpose` reads it (a test keeps
the two in step): the runner exited on its own (`Runner exited …`, stopped by
`runner`), failed to start (status `Failed`), was lost in an API restart
(`API restarted; runner not found`, stopped by `api`), or nothing was recorded.
The risk guard, the market-hours stop and a person's stop are meant, and never
count. The manual order book and alerter runs are not strategy runs; with the
plan readable, runs outside it do not count either.

"The day's last close" is the MCX close when MCX trades today, unless the plan
is known to have no MCX runs; then 15:30.

## What never rings

A false call on a normal trading day is the worst thing it can do after
missing a real outage, so:

- **One slow minute never rings.** A problem pages only once it has held for
  its grace *and* been seen on as many checks as that grace holds. It is over
  only after 3 clean checks in a row, so one that flaps (down two checks of
  three) still pages.
- **"Could not tell" is never a page.** A check where the API is down is "could
  not tell" for every rule that reads through the API; an unreadable answer
  leaves its rule where it was — neither nearer to paging nor resolved.
- **Only on days the desk trades.** The calendar is the API's; while the API
  is down, what it said earlier the same day (`sentinel/clock.remember_day`),
  then the repo's seeded holidays, then the weekday rule. Never at a weekend —
  Muhurat, a Budget Sunday — unless a planned run is live: the morning job does
  not run at weekends.
- **A holiday the calendar does not know.** When the calendar warns it cannot
  vouch for today (a year's holidays not loaded) and no index has priced since
  the 09:15 open, `no-ticks` and `runs-not-up` do not call; one text says it is
  probably a holiday — the morning job's own conclusion. On a day the calendar
  vouches for, the same silence is a feed dead from the open (10 Sep), and it
  rings.
- **Planned restarts.** API outages count toward a crash loop only from 08:40,
  when the deploy gate shuts; the 08:45 restart alone is one outage.
- **The owner's own pre-open deploys**, for the same reason.
- **Two run rules, one event.** Nothing live at all is `runs-not-up`; deaths
  explain a short count better than "fewer than half came up", so then it is
  `runs-dropped`.

## How a page escalates

- **PAGE** — a text, then a call. The text says "Calling the owner." only when
  a call will be tried; otherwise why not (no CallMeBot user set, or the day's
  limit reached).
- The call again every **10 minutes**, at most **3 per episode** and **9 per
  problem per day** (the flood guard). A second outage of the same kind later
  the same day rings again. Attempts count, not only calls CallMeBot accepted.
- **CALL FAILED** — a text when CallMeBot refuses or times out, at most one per
  half hour, so a broken call path is visible in the channel.
- **STILL** — a text every 30 minutes while it lasts, whether or not the calls
  got through.
- **RESOLVED** — one text after 3 clean checks in a row.
- A rule whose window closes while it is open says so — "NOT verified fixed"
  — and never that it was resolved.

Texts that are never calls: the probable holiday; no morning plan file
(`GET /api/Desk/plan` answers 404); Dhan's token ending before today's close
with FYERS signed out; the pager itself blind (the admin sign-in refused, or
no read has worked for 15 minutes); its own state file unwritable (a full disk
— it keeps paging from memory); 15 passes in a row that failed.

## Watching the pager

Nothing on the box can report the box dying. Set `PAGER_HEARTBEAT_URL` to a
dead-man's switch and the pager pings it after each completed check on
weekdays from 08:00 IST. With [healthchecks.io](https://healthchecks.io/):

- schedule `* 8-23 * * 1-5`, time zone `Asia/Kolkata`, grace 5 minutes (a check
  that places a call can take a minute or more);
- its "down" integration a webhook GET to
  `https://api.callmebot.com/start.php?user=@owner&text=OpenFNO+pager+is+silent&rpt=3&cc=yes`.

A dead box, a lost network, a crashed pager or one stuck in a restart loop all
stop the pings, and the phone rings from outside. A ping that fails is logged
once and never stops a check.

## Install and update

On the server, as `ubuntu`:

```bash
mkdir -p ~/openfno-pager
cp ~/algorithmic-trading-engine/scripts/pager/pager.py ~/openfno-pager/
# First install only — the settings, readable by ubuntu alone:
printf 'PAGER_CALLMEBOT_USER=@username\nPAGER_HEARTBEAT_URL=https://hc-ping.com/<uuid>\n' > ~/openfno-pager/pager.env
chmod 600 ~/openfno-pager/pager.env
sudo cp ~/algorithmic-trading-engine/scripts/pager/openfno-pager.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now openfno-pager

cd ~/openfno-pager
~/algorithmic-trading-engine/.venv/bin/python pager.py --once   # what it sees now; pages nothing, writes nothing
~/algorithmic-trading-engine/.venv/bin/python pager.py --test   # one test text and one test call
tail -f ~/openfno-pager/pager.log                               # or: journalctl -u openfno-pager -f
```

To update: copy `pager.py` again and `sudo systemctl restart openfno-pager`.
Its `state.json` is kept, and one written by an older version still loads. A
restart shorter than five minutes keeps each problem's grace; a longer one
starts the grace again for anything not yet paged.

| Setting | Where | What |
| --- | --- | --- |
| `PAGER_CALLMEBOT_USER` | `pager.env` | the `@username` the call goes to; unset, it texts but cannot call, and each PAGE says so |
| `PAGER_HEARTBEAT_URL` | `pager.env` | optional dead-man's switch pinged each check |
| `API_BASE_URL`, `ADMIN_USERNAME`, `ADMIN_PASSWORD` | repo `.env` | the admin's read-only GETs |
| `TELEGRAM_BOT_TOKEN`, `TELEGRAM_SYSTEM_CHAT_ID` (else `TELEGRAM_CHAT_ID`) | repo `.env` | where the texts go |
| `REDIS_HOST`, `REDIS_PORT`, `REDIS_PASSWORD`, `REDIS_DB`, `REDIS_STREAM_NAME` | repo `.env` | the tick stream |
| `OPENFNO_REPO` | environment | the checkout, when not `~/algorithmic-trading-engine` |

`pager.env`, `state.json` and `pager.log` live beside `pager.py`, never in the
repo; if it is ever run from the checkout, `.gitignore` keeps them out.
