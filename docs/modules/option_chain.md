# Option chain

Two screens and one table behind them.

* **Data → Option chain** — every strike of one expiry, priced, with the open
  interest written behind it.
* **Data → Open interest** — how one strike moved through the session.

## Where open interest comes from

From a recorder that asks a vendor's option chain and writes every strike to
`option_chain_snapshots`, one row per strike per capture, with the vendor's key.

* **Dhan** (primary since 2026-09-15): the API's chain recorder, once a minute,
  nearest expiry, for NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX, BANKEX,
  CRUDEOIL and NATURALGAS. Each row carries OI, previous OI, volume, IV and all
  four greeks from Dhan. See [Dhan connector](../dhan/).
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

## The advanced chain screen

**Data → Option chain** (`/admin/data/chain`) is laid out like a trader's chain,
and every number on it says where it came from.

* **Header:** spot with its day change, the nearest future and its premium, India
  VIX, PCR and PCR of OI change, max pain, the ATM strike and ATM IV, support
  (heaviest put OI) and resistance (heaviest call OI), total call and put OI with
  their change, days to expiry and lot size.
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
| GET | `/api/OptionChain/view` | the ladder with live quotes laid over it and the header block; `asOfUtc` for replay |
| GET | `/api/OptionChain/trend` | spot, call and put OI and PCR at each capture of the session |
| GET | `/api/OptionChain/series` | one strike through the session |
| GET | `/api/OptionChain/expiries` | which expiries have been captured |
| POST | `/api/OptionChain/snapshots` | the poller's write path |
| POST | `/api/OptionChain/poller/start` · `/stop` | admin only |
| GET | `/api/OptionChain/poller/status` · `/logs` | process state and output |
