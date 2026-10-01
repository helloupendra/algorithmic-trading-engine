# Market Replay

Play a trading day the desk recorded again, through the same strategy runners the live desk uses, whenever the
markets are shut (Data → Replay, `/data/replay`, admin). The owner asked on 1 Oct 2026: replay a day "jesa system
abhi chal raha hai, same", whenever he wants.

## How a replay runs

1. The page lists the recorded days, newest first. For each it shows how many of the 375 session minutes were
   recorded for NIFTY, BANKNIFTY, SENSEX and India VIX, and the size of the day's ticks. Ticks are kept for 90 days,
   from 9 Sep 2026.
2. Pick a day, then the strategy runs (strategy, underlying, lots), a speed (1×, 2×, 5×, 10×) and a start time
   (09:15–15:00).
3. Start. The page starts each run as a **recap run of that day** (`session: recap`, `recap_date`). It then starts
   the replay with the run ids.
4. The **player** (`market_data/replay/run_replay.py`) waits until every run is listening, at most five minutes.
   It then plays the day's ticks in the order they arrived, at the chosen speed:
   - to the API's **replay book**, which prices the runs' fills and marks;
   - then to the Redis stream `market:ticks`, marked `isReplay`, which the runs trade on.
5. When the day is played out, past 15:30 so the runs see the close, the API stops the runs. They are squared off at
   the replay's closing prices, and the book is cleared. Stop does the same at any point. Pause holds the replay's
   clock still.

Recap runs are tests: they are left out of every live total (the run history, Today, track records, the AI tools).
Trade → History lists them apart: its **Recap runs** toggle shows the recap runs alone, each marked "Recap test" with
the day it replayed, and the page's totals are then the tests' own.

## What keeps the live desk safe

| Rule | Why |
|---|---|
| The replay's prices live in the API's memory (`IMarketReplayBook`), never in `live_quotes_latest`. Only a recap run of the replayed day reads them: its fills, marks, risk guard, P&L, clock (`RecapClock`), position views and greeks, and its runner's quote calls (`replay=true&recapDate=` that day). | `live_quotes_latest` prices every live fill, mark, risk check, option chain and pulse. A past day's prices there would mark carried positions at that day's prices and could fire their stops. |
| No desk replay starts while a vendor's recap feed runs (below). | The two would price each other's runs. |
| Nothing a replay plays is stored: no `live_ticks` rows, no `live_bars` merges. | The day is already recorded. Played again through the feed path, its bars' volumes would double. |
| The player is not a feed. It is outside `/api/Feeds`, the 15:30 feed stop, failover, Sentinel's feed rules and the close job's feed loop. | A feed is judged as live market data. A replay is not. |
| Live runners pass over `isReplay` ticks; recap runners take only those. | The two share the Redis stream. |
| Only NSE and BSE symbols are played. | The evening crude run is live on MCX while a replay plays. |
| A replay cannot start on a trading day from 08:45 until NSE's close. One still playing at 08:45 is stopped, and its runs with it. | The morning job and the session belong to the live desk. |
| A replay owns its runs. When it finishes, is stopped, its player dies, or the morning comes, the runs are stopped first and the book cleared after. | A recap run left running would trade the next live session's prices. |
| A replay's runs are left out of the 15:30 market-close sweep. The close job's stray-runner check counts them as open. | A weekday holiday still has a 15:30 close, and a replay played that afternoon must not be cut there. |

## What the runs see

- **Bars:** `GET /api/LiveData/bars?untilUtc=` returns the recorded 1m bars that had ended by the replay's clock.
  The minute in progress is built from the replayed ticks, never the recorded one, whose close the replay has not
  reached. 5m and 15m bars are aggregated from those. Warm-up stops the day before (`RecapSession`).
- **Expiry and contracts:** taken as of the replayed day: the first expiry on or after it, expired contracts
  included. The strike step comes from the chain, then the F&O inventory, then the known steps.
- **Option chain** (ChainFlowBuy): read as of the tick's time, from the recorded snapshots.
- **Prices of a contract not yet traded that day:** Dhan stamps a quote with its last trade, so it can carry an
  earlier day's time. The book still uses it to price a fill. It does not move the replay's clock or a minute's bar.
- **Greeks on the run page:** computed from the replay's option price and spot (Black-Scholes with the IV solved from
  that price, as a live leg with no fresh feed greeks is), with the time to expiry counted from the replay's clock
  (`PositionGreeksBuilder`). They are fresh or stale by that clock. The player sends no greeks, and today's live
  quotes and chain are never used. A leg the replay has no quote for yet is counted as not priced.

## Speeds

1× is faithful. Above 1×, a fill can be priced a little after the tick the strategy decided on: the book has moved
on while the runner worked. A whole session at 1× takes 6 h 15 min, and at 10× about 38 min.

## API

| Endpoint | What |
|---|---|
| `GET /api/Replay/days` | The recorded days with each index's minutes and the day's size |
| `GET /api/Replay/status` | `canStart`, `whyNot`, and the session: state, replay clock, progress, ticks sent, runs with their net. An ended replay keeps where its clock stood and the ticks it sent |
| `POST /api/Replay/start` | `{ date, speed, from, runIds }`; the runs must be running recap runs of that day. Refused while a queue plays or a vendor's recap feed runs |
| `POST /api/Replay/stop`, `pause`, `resume` | The session |
| `POST /api/Replay/queue`, `DELETE /api/Replay/queue` | Several days one after another with the AI Trader alone (see [The queue](#the-queue)) |
| `GET /api/Replay/logs?lines=` | The player's log |
| `POST /api/Replay/ticks` | The player's ticks for the book (Service or Admin, at most 2,000 a batch). 409 when no replay is on, which stops the player |

The session is kept in `system_settings` (`replay.session`), the queue in `replay.queue`; each fits the setting's
2,000 characters (names and reasons are kept short and plain, an error to 400 characters). An API restart in the
middle of a replay carries on: the player keeps running, and its next ticks reopen the book and refill it (the
ticks endpoint reopens it itself while the stored replay plays, so the player's first post is never refused for
want of a book). `ReplayMonitor` looks every 5 s. Every change of the replay's state (the monitor's look, start,
stop, queue, cancel, the book reopened) goes through one gate, so two of them never both start a day.

## The queue

`POST /api/Replay/queue {dates, speed}` plays up to 20 recorded days one after another, oldest first, each from the
open, with the AI Trader alone; how it is scored is in [AI Trader](ai_trader.md#scored-against-a-baseline). The
monitor starts each next day when all of these hold:

- no replay is playing, and the last one ended at least 90 s ago;
- not a trading day's 08:45 to NSE's close, and the day will be played out before the next trading morning's 08:45
  (holidays from the calendar);
- no vendor's recap feed is running;
- the last day's player has exited.

Otherwise the day waits; it is never skipped for any of these. A day with nothing recorded, or whose player does
not start, is skipped with its reason. A day the queue started but had not written down when the API stopped is
counted as played, not played again. While a queue plays, a manual start is refused, between its days too. `DELETE`
ends the queue: no further day starts, and the day playing plays on unless it is stopped.

## The desk replay and a vendor's recap (TrueData)

Before the desk recorded its own days, a recap came from a vendor: TrueData replays the day's session in the evening
on its own host (`replay.truedata.in`), and the TrueData feed run against it is a **recap feed**
(`python-truedata-recap`; the top bar shows "NSE recap" while it runs). Its ticks are marked `isReplay` like the
player's, but they go where a feed's go: into `live_quotes_latest`, and onto `market:ticks`. A recap run started for
it (`session: recap`, `recap_date` that day) is priced from the live table.

How the two are kept apart:

- **No desk replay starts while a recap feed runs.** The API reads it from the feeds' heartbeats: a vendor whose
  newest heartbeat is `python-<key>-recap` is running one when that heartbeat is under a minute old, or, when it has
  gone quiet (the API was down and refused it), while the feed still holds its Redis lock `feed:<key>:lock`
  (`HeartbeatRecapFeeds`). A manual start is refused with why, `status` says so, and a queued day waits.
- **A recap run's quote calls name the day they replay** (`/api/LiveData/latest?replay=true&recapDate=yyyy-mm-dd`,
  `latest/all` the same). Only a run of the day the desk is replaying is answered from the replay book; a recap of
  any other day (the vendor's, replaying today) reads the live table, with or without a desk replay on. A runner
  started before runners sent the day asks with `replay=true` alone and is answered as before: from the book while
  one is on.
- With a desk replay on, only the recap runs of the replayed day are filled, marked and timed from the replay book. A
  recap run of any other day is filled, marked and timed from the live table, as before.
- Every recap runner drops a tick stamped outside its own replayed session, so the runs of one replay never trade the
  other's ticks when the two replay different days.

What is still not prevented: nothing stops a vendor's recap feed from being started **during** a desk replay (the
feed is switched to its recap host by hand). With the two replaying different days, the runs of each are still
priced from their own day's prices, as above. With the same day (possible only after midnight: a vendor recap still
playing past 00:00 while a desk replay of that day is on), both replays' ticks reach every recap run of that day at
two different clocks, and the vendor recap's runs are priced from the desk replay's book: both sets of results are
spoilt. Neither reaches a live run: live runners pass over every `isReplay` tick, and a live run is never priced from
the replay book.

Before switching TrueData to its recap host, make sure Data → Replay shows no replay playing, and stop it if one is.

## Not in v1

- MCX replays.
- Several days in a row with strategy runs: the queue plays the AI Trader alone.
- Greeks on the replayed ticks themselves: the runners get none (the run page computes its own, see above).
- The option chain page during a replay.
- A guard on the feed's side: a vendor's recap feed can still be started during a desk replay (see above).
