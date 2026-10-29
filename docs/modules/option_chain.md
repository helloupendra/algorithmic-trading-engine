# Option chain

One page, **Markets → Option chain**, with a tab per reading, and one table behind them.

* **Chain** (`/markets/chain`) — every strike of one expiry, priced, with the open
  interest written behind it.
* **OI history** (`/markets/chain/oi`) — how one strike moved through the session.
* **Levels** (`/markets/chain/levels`) — the OI walls, max pain, PCR, IV and the move
  the straddle prices, read off the same chain.
* **Straddle & IV** (`/markets/chain/straddle`) — the ATM straddle through a session
  with up to three other days laid over it by the time of day, the ATM IV through the
  day, and the ATM IV by day with the IV rank and percentile of the value now.

## Where open interest comes from

From a recorder that asks a vendor's option chain and writes every strike to
`option_chain_snapshots`, one row per strike per capture, with the vendor's key.

* **Dhan** (primary since 2026-09-15): the API's chain recorder, once a minute,
  nearest expiry, for NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX, BANKEX,
  CRUDEOIL and NATURALGAS. Each row carries OI, previous OI, volume, IV and all
  four greeks from Dhan. See [Dhan connector](../dhan/).
  Since 2026-10-30 it also records some **later expiries**, every 5 minutes
  (see below).
* **FYERS** (fallback): `market_data/live/option_chain_poller.py`. A FYERS tick
  carries no open interest, so the poller merges it in from a second endpoint.

An MCX chain is priced on the future its options are written on. Dhan's MCX chain
reports an underlying price that is not that future's (CRUDEOIL 9,577 against a
future at 9,971 on 2026-09-14), so the recorder stores the future's own quote.

**This has a consequence that cannot be worked around:** a session where the
poller was not running has no open interest at all, permanently. Not in the
chain, not in the OI curves, and not in any backtest of that day. A strike's
open interest at 10:15 is knowable only because something wrote it down at
10:15. Start the poller with the ingestor, every session.

## Which expiries are recorded

| Expiry | Underlyings | How often |
| --- | --- | --- |
| The nearest (on an expiry day, that day's) | all eight above | every minute |
| The one after the nearest | NIFTY, BANKNIFTY, SENSEX | every 5 minutes |
| The nearest monthly, when it is neither of those | NIFTY, SENSEX | every 5 minutes |

A monthly is the last expiry Dhan lists in its month, so a monthly moved earlier
by a holiday still counts (NIFTY's November 2026 monthly is on the 23rd).
BANKNIFTY lists only monthlies: its nearest is this month's and its later one is
next month's. On a monthly's own expiry day the nearest is that monthly, and only
the next weekly is added.

The later expiries never cost the nearest anything. Each round records every
nearest expiry first; the later ones that are due are asked for only in the time
left before the next round, through the same one-chain-every-three-seconds slot,
and only for an underlying whose nearest was recorded in that round. A capture is
not started with less than 10 seconds left, and one still waiting on Dhan when the
next round is due is cut short and tried again after its interval. A round of the
eight nearest takes about 25 seconds, so there is room for the five later ones
(NIFTY 2, BANKNIFTY 1, SENSEX 2), which fall due together every fifth round.
Five extra chains every five minutes against eight every minute is roughly an
eighth more rows: an estimated 60 to 80 MB a trading day at today's row sizes.

Settings (`Dhan:ChainPoller`, defaults in `DhanChainPollerSettings`, so no server
configuration is needed): `LaterExpiryUnderlyings` (`NIFTY,BANKNIFTY,SENSEX`;
empty turns later expiries off) and `LaterExpiryIntervalSeconds` (300, never
less than a round). `GET /api/Dhan/chain-poller` lists them, with each later
expiry's last attempt under `laterOutcomes`, apart from the nearest's `outcomes`.

## Which expiry a read gets

Every read takes an optional `expiry`. Without one, it gets **the nearest
expiry recorded on the day of the capture it reads**: the smallest expiry, not
before that day, with a capture that day. That is the per-minute series, the
only one there was before 30 Oct 2026, and the one ChainFlowBuy, the AI desk,
the market factors page and the chain tabs before an expiry is picked all read.
A later expiry is never
mixed in, even when it was captured a few seconds after the nearest and is
therefore the newest row:

* `GET /api/OptionChain` and `/view`: the nearest expiry's newest capture at or
  before `asOfUtc`.
* `/trend`: that expiry's captures of that session, so totals and PCR are one
  expiry's, never two added together.
* `/series` without an expiry: each day's points are that day's nearest, as
  before. A strike listed only in a later expiry has no point that day rather
  than another contract's.

To read another expiry, name it: `GET /api/OptionChain/view?underlying=NIFTY&expiry=2026-11-10`.
`GET /api/OptionChain/expiries/recorded?underlying=NIFTY&day=2026-10-30` says
which expiries a day has, how many captures each, their usual spacing
(`typicalIntervalSeconds`: about 60 for the nearest, 300 for a later one) and
which is the default. Without `day` it answers for the newest capture's day.

## The advanced chain screen

**Markets → Option chain** (`/markets/chain`, the Chain tab; OI history and Levels are the page's
other tabs, at `/markets/chain/oi` and `/markets/chain/levels`) is laid out like a trader's chain,
and every number on it says where it came from.

* **Header:** spot with its day change, the nearest future and its premium, India
  VIX, PCR and PCR of OI change, max pain and its distance from the spot, the ATM
  strike and ATM IV, support (heaviest put OI) and resistance (heaviest call OI),
  total call and put OI with their change, days to expiry and lot size. The strip
  lays itself out by its own width: one row on a desk screen (from about 1,350px),
  two rows below that (the prices over the chain's readings), four short rows on
  a phone, every row filled. A line too long for its cell ends in an ellipsis and
  shows in full on hover; on a phone it wraps instead.
* **Freshness line:** "live · updated 2 s ago · Dhan · 31/402 legs live", or
  "snapshot 10:41 IST (1 min old)", or "market closed". A capture more than 3
  minutes old during the session warns that the recorder may be behind. A stale
  number is never shown as live.
* **Table:** calls left, strike in the middle, puts right.
  * Each side: build-up, OI with a bar, OI change and %, volume with a bar, IV,
    LTP and %.
  * Toggles: greeks, bid/ask, premium per lot.
  * The in-the-money half of each side is shaded, and the ATM row is highlighted.
  * A spot row sits between the two strikes the price is between.
  * The page opens scrolled to ATM; strike window ±10, ±20 or all.
* **OI analysis:** OI by strike, change in OI by strike, and PCR through the
  session.
* **Expiry:** the expiries recorded on the session shown, the nearest first and
  chosen by default. A later expiry is marked with how often it is recorded
  ("every 5 min", or "one capture so far" before its second). Picking the
  nearest clears the choice, so the page follows the nearest into the next week.
* **As of:** replays any capture of the day.

**Live and per-minute.** A strike whose live quote is at most 2 minutes old and
newer than the capture shows the quote's LTP, bid/ask, volume and OI, with its OI
change and build-up recomputed against the capture's baselines (marked with a teal
dot). Those are the ATM ±5 strikes the Dhan feed streams. IV, greeks, every other
strike and the trend come from the per-minute capture. The page polls every 3 s
while the market is open and every 60 s otherwise.

## The table is a time series, not a cache

`option_chain_snapshots` is written eagerly rather than derived on read, and
that is what makes three separate things possible from one query shape:

| Screen | Query |
| --- | --- |
| The chain | the newest row per strike |
| The OI curves | a day of rows for one strike |
| Either, inside a backtest | the same, with the clock moved back |

Live and replay are the same endpoint: `GET /api/OptionChain` returns the newest
capture, and `?asOfUtc=` returns the one that was true then. There is no separate
historical path to drift out of step with the live one.

## What is derived, and how

`OptionChainAnalytics` holds the parts a trader acts on, apart from the data
access so they can be proved against a table of inputs rather than a database.

**Build-up** — the four readings of price direction against open-interest
direction: price up with OI up is new buyers (long build-up); price down with OI
up is new sellers (short build-up); price up with OI down is shorts buying back
(short covering); price down with OI down is longs leaving (long unwinding).
Both changes are measured from the session open. Measuring OI since 09:15 against
a price change since the previous poll would label strikes almost at random.

**PCR** — put open interest over call open interest. Null, never zero, when
there are no calls to divide by: a PCR of 0 reads as an extreme signal, and "no
calls are written here" is not a signal at all.

**Max pain** — the settlement at which option writers pay out least, computed
across every strike. Null on an empty chain rather than the lowest strike.

`AtmStraddle` does the same for the Straddle & IV tab, which `StraddleService`
reads one expiry a day: the nearest on or after the day with captures on it, the
contract the recorder writes every minute. Later expiries recorded beside it never
enter a straddle.

**The ATM straddle** — in each capture, the strike nearest the spot (the lower on
an exact tie, as the chain marks it). Each leg is read at the mid of its bid and
ask when both are quoted and not crossed, otherwise at its last trade, and the
answer says which. A leg missing at that strike leaves the minute without a
straddle rather than borrowing a strike further out.

**ATM IV** — the mean of the call's and put's IV at that strike (either alone when
the other is unpriced), in percent whichever recorder stored it. The history reads
it at the first priced capture five minutes after the open and the last five
minutes before the close (09:20 and 15:25 on NSE; the session's own hours on MCX).

**IV rank and percentile** — of the current value (today's newest capture, or the
newest session's close before the open) against the closes of the sessions before
it in the last year: rank is where it sits between their lowest and highest, the
value included; percentile is the share that closed below it. Both come with the
number of sessions used, and the page says "over N days only" until there are
about 250.

Nothing here scans the table: expiries come from a loose index scan (three seeks
each), a day is one range on `(Underlying, ExpiryDate, CapturedUtc)` with only
strikes within 3% of the spot, an IV reading is one capture, closed sessions are
kept in memory once read, and today is read from its newest capture onward.

## Operating it

Start and stop from **Data → Feeds**, beside the ingestor's control. The
panel reports two things and they are not the same:

* **Process** — is it running.
* **Last chain stored** — is it working. A poller that started but cannot reach
  the broker is up, looks healthy, and is recording nothing. An expired daily
  token is the usual reason; it answers 401, and the log says so.

The poller backs off while the broker keeps refusing — the same line every five
seconds for an hour is a log nobody reads — and returns to the normal interval
as soon as anything comes back.

## API

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/OptionChain` | the strike ladder; `asOfUtc` for replay, `expiry` for another than the nearest |
| GET | `/api/OptionChain/view` | the ladder with live quotes laid over it and the header block; `asOfUtc` for replay, `expiry` as above |
| GET | `/api/OptionChain/trend` | spot, call and put OI and PCR at each capture of the session, one expiry (`expiry`, or the nearest) |
| GET | `/api/OptionChain/series` | one strike through the session, one expiry a day (`expiry`, or each day's nearest) |
| GET | `/api/OptionChain/expiries` | which expiries have ever been captured |
| GET | `/api/OptionChain/expiries/recorded` | the expiries recorded on one IST day (`day`, or the newest capture's), with captures, usual spacing and which is the default |
| GET | `/api/OptionChain/positions` | open legs of running runs on the underlying's contracts, the caller's own (an admin's: every account's); `mode` is `LivePaper` (default: strategy runs and manual books, marked at the newer of the live quote and the stored mark) or `OfflineReplay` (backtests, at their own last mark) — never both |
| POST | `/api/OptionChain/snapshots` | the poller's write path |
| POST | `/api/OptionChain/poller/start` · `/stop` | admin only |
| GET | `/api/OptionChain/poller/status` · `/logs` | process state and output |
| GET | `/api/Straddle` | one session's ATM straddle and IV, a point per capture minute; `date` (IST), default today; market-data grant |
| GET | `/api/Straddle/iv-history` | the last year's ATM IV near each session's open and close, and the IV rank and percentile of the value now with the sessions used; market-data grant |
