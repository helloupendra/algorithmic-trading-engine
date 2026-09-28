# Local live stack

A throwaway copy of the platform on your own machine, for trying the console
against moving prices: its own Postgres and Redis in docker, the API built
from this checkout, the web dev server, synthetic ticks and a few paper
positions. Nothing in it can reach a broker, and it checks that it cannot
while it runs.

Production trades live on real Dhan and FYERS accounts. A second API that
signs in to Dhan with the PIN and TOTP ends the server's token, and a second
Dhan websocket disconnects the first; FYERS, Angel and TrueData behave the same
way. That is why this stack is isolated by construction rather than by care.

## The three commands

```bash
scripts/dev/local-live.sh up        # everything, then prints the URL and the sign-ins
scripts/dev/local-live.sh status    # what is running, and the isolation check
scripts/dev/local-live.sh down      # stops it all and removes the containers
```

`up` starts the containers, builds and starts the API on
`http://127.0.0.1:5125` (migrations run on start), creates a trader account,
loads a small synthetic instrument master, starts the console on
`http://127.0.0.1:5180`, starts the tick replay in the background and books the
paper positions. It prints two sign-ins, `devadmin` and `devtrader`, with
passwords generated for this stack (they exist in no other database). Run it
again at any time: whatever is already up is left alone. `--no-web`,
`--no-replay` and `--no-seed` skip those parts.

The two scripts it drives also work on their own:

```bash
python3 scripts/dev/replay-ticks.py [--rate 20] [--seconds 60] [--symbols A,B] [--vol-mult 3]
python3 scripts/dev/seed-positions.py            # the positions, once (--force for another set)
python3 scripts/dev/seed-positions.py --fill     # one more manual order
python3 scripts/dev/seed-positions.py --close    # square off the newest open leg (or --position ID)
```

`replay-ticks.py` posts to `POST /api/LiveData/ticks/upsert-batch` as the
engine's ingestor does (batches of up to 50 every 150 ms, as the service
account, a heartbeat every 10 s, the same ticks on the Redis stream
`market:ticks`). It prices NIFTY 50, NIFTY BANK and SENSEX as correlated random
walks, five NIFTY option strikes around the money for the next Tuesday expiry
off that NIFTY, and the near CRUDEOIL future. The API stores the ticks, marks
the open positions and pushes each batch to the console (`ReceiveTicks`), so
Data → Live feeds, Trade → Positions and the Desk move.

`seed-positions.py` books through the API: two manual books (the admin's:
long call, short put, long crude; the trader's: short call), all carried
forward so the close does not square them off, and a `LivePaper` strategy run
in the trader's account holding a short straddle.

## What keeps it away from the brokers

1. **No credential reaches the API.** It starts from an empty environment
   (`env -i`) holding [`scripts/dev/local-live.env`](../../scripts/dev/local-live.env)
   plus the ports, paths and generated passwords. Its content root is
   `.dev-live/content/api`, so `src/AlgoTrading.Api/appsettings.Local.json`,
   which holds the real keys on a configured machine, is never read (the copy
   the build puts next to the dll is deleted). The database is new, and the
   Data Protection key ring is its own (`HOME` is `.dev-live/home`).
2. **Nothing starts by itself.** The Dhan automatic sign-in and chain
   recorder, feed failover, the market-intelligence recorders, the NSE
   market-factor sync, the forecast jobs and the pattern alerts are switched
   off; the nightly candle archive is marked done until 2099.
3. **Every broker and vendor address is `http://127.0.0.1:9`**, a closed port,
   and so are `HTTP_PROXY` and `HTTPS_PROXY`, which catches the addresses
   compiled into the code (FYERS's token endpoints, Telegram, NSE, Yahoo).
4. **Python never runs.** `StrategyRunner:PythonExecutable` is
   `scripts/dev/refuse-python.sh`, which logs what was asked for to
   `.dev-live/logs/refused-python.log` and exits. No feed, runner, backtest,
   alerter or notifier can start, and the engine, which loads the repo-root
   `.env` on import, is never imported.
5. **Its own containers**, `openfno_dev_db` on port 5544 and
   `openfno_dev_redis` on 6390, labelled `com.openfno.dev-live`. `up` refuses
   the Mac's own ports (5025, 5432, 5433, 6379, 6380) and refuses to start if
   anything listens on port 9.
6. **It checks.** Every TCP and UDP socket of the API and its child processes
   is read with `lsof` while it starts, after it starts, on every `status` and
   `check`, and every 5 s by a background watchdog. Allowed: listening on
   `127.0.0.1:5125`, clients of that port, and connections to `127.0.0.1:5544`
   and `127.0.0.1:6390`. Anything else stops the API, including another
   loopback port: an SSH tunnel to the server listens on loopback too. Three
   unreadable checks in a row stop it as well.

`up` also refuses to start if `local-live.env` gives any credential a value,
points a URL anywhere but `127.0.0.1:9`, or is missing one of the lines above;
`scripts/tests/local-live.test.sh` checks the same in CI, together with the
socket rules and the stop path.

## Verifying it yourself

```bash
scripts/dev/local-live.sh check                       # prints every socket and the verdict
lsof -nP -iTCP -a -p "$(cat .dev-live/run/api.pid)"   # the same, by hand
cat .dev-live/logs/watchdog.log                       # "clean for the last 5 minutes"
cat .dev-live/logs/refused-python.log                 # what the API tried to run
```

## Things that look different from production

- The strategy run has no runner behind it, so its own page labels it
  *Stopped*; its legs are open, marked and closable, and it is on the Desk and
  in Positions like any live run. If the dev API restarts it is closed as
  orphaned, like any run whose runner is gone, and the next `up` books a new
  one.
- The Start buttons of strategies, feeds and backtests fail: Python is refused.
  The strategy list comes from the API's scan of the strategy files.
- Outside market hours the API keeps quotes and positions moving but builds no
  1-minute bars, as it does for real ticks after the close.
- The Desk reads `config/morning-plan.txt`; its accounts do not exist here, so
  every line reads *not live*.

Everything the stack writes is under `.dev-live/` (git-ignored). `down` keeps
`logs/` and `build/` and removes the rest.
