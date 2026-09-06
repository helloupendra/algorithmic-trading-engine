# Option chain

Two screens and one table behind them.

* **Data → Option chain** — every strike of one expiry, priced, with the open
  interest written behind it.
* **Data → Open interest** — how one strike moved through the session.

## Where open interest comes from

Not from the tick feed. A FYERS `SymbolUpdate` message carries ltp, bid, ask,
volume and the day's OHLC — and no open interest of any kind. Neither `candles`
nor `live_bars` has a column for it either. Before this module the platform had
never recorded a single OI figure.

So it is fetched separately, by `market_data/live/option_chain_poller.py`, from
the broker's REST option chain, and written to `option_chain_snapshots` — one
row per strike per poll.

**This has a consequence that cannot be worked around:** a session where the
poller was not running has no open interest at all, permanently. Not in the
chain, not in the OI curves, and not in any backtest of that day. A strike's
open interest at 10:15 is knowable only because something wrote it down at
10:15. Start the poller with the ingestor, every session.

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

## Operating it

Start and stop from **Data → Live feeds**, beside the ingestor's control. The
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
| GET | `/api/OptionChain` | the strike ladder; `asOfUtc` for replay |
| GET | `/api/OptionChain/series` | one strike through the session |
| GET | `/api/OptionChain/expiries` | which expiries have been captured |
| POST | `/api/OptionChain/snapshots` | the poller's write path |
| POST | `/api/OptionChain/poller/start` · `/stop` | admin only |
| GET | `/api/OptionChain/poller/status` · `/logs` | process state and output |
