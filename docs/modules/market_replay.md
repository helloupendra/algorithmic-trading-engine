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
| The replay's prices live in the API's memory (`IMarketReplayBook`), never in `live_quotes_latest`. Only a recap run of the replayed day reads them: its fills, marks, risk guard, P&L, clock (`RecapClock`), position views and greeks, and its runner's quote calls (`replay=true`). | `live_quotes_latest` prices every live fill, mark, risk check, option chain and pulse. A past day's prices there would mark carried positions at that day's prices and could fire their stops. |
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
| `GET /api/Replay/status` | `canStart`, `whyNot`, and the session: state, replay clock, progress, ticks sent, runs with their net |
| `POST /api/Replay/start` | `{ date, speed, from, runIds }`; the runs must be running recap runs of that day |
| `POST /api/Replay/stop`, `pause`, `resume` | The session |
| `POST /api/Replay/queue`, `DELETE /api/Replay/queue` | Several days one after another with the AI Trader alone (see [AI Trader](ai_trader.md#scored-against-a-baseline)) |
| `GET /api/Replay/logs?lines=` | The player's log |
| `POST /api/Replay/ticks` | The player's ticks for the book (Service or Admin, at most 2,000 a batch) |

The session is kept in `system_settings` (`replay.session`). An API restart in the middle of a replay carries on:
the player keeps running, and its next ticks refill the book. `ReplayMonitor` looks every 5 s.

## The desk replay and a vendor's recap (TrueData)

Before the desk recorded its own days, a recap came from a vendor: TrueData replays the day's session in the evening
on its own host (`replay.truedata.in`), and the TrueData feed run against it is a **recap feed**
(`python-truedata-recap`; the top bar shows "NSE recap" while it runs). Its ticks are marked `isReplay` like the
player's, but they go where a feed's go: into `live_quotes_latest`, and onto `market:ticks`. A recap run started for
it (`session: recap`, `recap_date` that day) is priced from the live table.

The two work one at a time without any switch:

- With no desk replay on, a recap run's quote calls (`/api/LiveData/latest?replay=true`, `latest/all?replay=true`)
  fall back to the live table, so a vendor recap's runs read the vendor's prices as before.
- With a desk replay on, only the recap runs of the replayed day are priced from the replay book. A recap run of any
  other day is filled, marked and timed from the live table, as before.
- Every recap runner drops a tick stamped outside its own replayed session, so the runs of one replay never trade the
  other's ticks when the two replay different days.

**Do not run both at once.** Nothing stops it: the API does not refuse a desk replay while a vendor recap feed is
running, and nothing stops a recap feed from starting during a desk replay. What goes wrong then:

| Overlap | What happens |
|---|---|
| Different days (the usual case: the vendor replays today, the desk an earlier day) | The vendor recap's runners ask for quotes with `replay=true`, and while a desk replay is on that is answered from the desk replay's book, which holds another day's prices or none (404) for their contracts. Their fills and marks still come from the live table. Their decisions are then made on the wrong day's premiums: the test is spoilt. The desk replay's own runs are not affected. |
| The same day (possible only after midnight: a vendor recap still playing past 00:00 while a desk replay of that day starts) | Both replays' ticks reach every recap run of that day, at two different clocks, and the vendor recap's runs are filled and marked from the desk replay's book. Both sets of results are spoilt. |

Neither overlap reaches a live run: live runners pass over every `isReplay` tick, and a live run is never priced from
the replay book.

What an operator should do: before starting a desk replay, look at the top bar and Data → Live feeds; if a recap feed
is running ("NSE recap", "TrueData recap"), wait for it to end or stop that feed first. Before switching TrueData to
its recap host, make sure Data → Replay shows no replay playing, and stop it if one is. If both did run, treat the
recap runs of that evening as void and run them again.

## Not in v1

- MCX replays.
- Several days in a row with strategy runs: the queue plays the AI Trader alone.
- Greeks on the replayed ticks themselves: the runners get none (the run page computes its own, see above).
- The option chain page during a replay.
- A guard against a desk replay and a vendor recap feed running at once (see above).
