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

## What keeps the live desk safe

| Rule | Why |
|---|---|
| The replay's prices live in the API's memory (`IMarketReplayBook`), never in `live_quotes_latest`. Only a recap run of the replayed day reads them: its fills, marks, risk guard, P&L, clock (`RecapClock`), position views, and its runner's quote calls (`replay=true`). | `live_quotes_latest` prices every live fill, mark, risk check, option chain and pulse. A past day's prices there would mark carried positions at that day's prices and could fire their stops. |
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

## Speeds

1× is faithful. Above 1×, a fill can be priced a little after the tick the strategy decided on: the book has moved
on while the runner worked. A whole session at 1× takes 6 h 15 min, and at 10× about 38 min.

## API

| Endpoint | What |
|---|---|
| `GET /api/Replay/days` | The recorded days with each index's minutes and the day's size |
| `GET /api/Replay/status` | `canStart`, `whyNot`, and the session: state, replay clock, progress, ticks sent, runs with their net |
| `POST /api/Replay/start` | `{ date, speed, from, runIds }`; the runs must be running recap runs of that day |
| `POST /api/Replay/stop`, `pause`, `resume` | The session |
| `GET /api/Replay/logs?lines=` | The player's log |
| `POST /api/Replay/ticks` | The player's ticks for the book (Service or Admin, at most 2,000 a batch) |

The session is kept in `system_settings` (`replay.session`). An API restart in the middle of a replay carries on:
the player keeps running, and its next ticks refill the book. `ReplayMonitor` looks every 5 s.

## Not in v1

- MCX replays.
- Several days in a row.
- Greeks on replayed option ticks.
- The option chain page during a replay.
