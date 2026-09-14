# Dhan connector

Dhan is the platform's third market-data vendor, after FYERS and TrueData. It
is its own module: `Infrastructure/Providers/Dhan/` in the API, and a separate
live-feed adapter in the Python engine. Nothing in FYERS or TrueData code knows
it exists.

It was chosen for the data it adds:
- open interest in quotes and in history;
- an option chain with OI, previous OI, volume, IV and greeks in one call;
- 20- and 200-level market depth;
- history for expired options.

Dhan is also a broker. Only its data side is built; it registers as a data
connector until an order path exists.

Every fact below was checked against the live API on 2026-09-14 (an exchange
holiday, with an active data plan) unless it says otherwise.

## Status

| Piece | State |
| --- | --- |
| History (1, 5, 15, 25, 60-minute and daily bars, with OI) | **Working** |
| Option chain with OI, OI change, volume, IV and greeks | **Working**: indices and MCX (CRUDEOIL, NATURALGAS) |
| Option chain recording (every minute, into the chain history) | **Working**: verified on MCX, 2026-09-14 |
| Live feed (binary websocket): price, 5-level depth, volume, OI | **Working**: verified on MCX, 2026-09-14; first NSE session 2026-09-15 |
| Instrument import (canonical symbol → Dhan security id) | **Working**: 104,114 of 104,355 live instruments matched |
| Token and data-plan status | **Working** |
| Daily sign-in with the API key (Connect, like FYERS) | **Working** |
| Unattended daily sign-in (PIN + TOTP) | **Next** |
| Orders | Not built |

## What the platform takes from Dhan

Dhan is the primary market-data source whenever it is signed in; FYERS is the
backup. The Dhan connector page shows each of these as on or off, live.

| Data | How | Where it lands |
| --- | --- | --- |
| Ticks: last price, best bid and ask with sizes, 5-level depth, volume, OI, day OHLC | Live feed, Full mode for F&O and MCX, Quote mode for indices and equities | `live_ticks`, `live_quotes_latest`, 1-minute bars, the Redis tick stream |
| Option chain: every strike's OI, previous OI, volume, IV, delta, gamma, theta, vega, bid and ask | Chain recorder, once a minute, nearest expiry | `option_chain_snapshots` with source `dhan`: the chain page, OI history and replay read it |
| History with OI | History router (fallback behind FYERS and TrueData) | `candles` |

## Setting it up

Dhan needs an active **Data APIs** subscription, which Dhan charges for
separately from trading, and these values from Dhan's web console (*DhanHQ
Trading APIs*):

| Value | Where it goes | Notes |
| --- | --- | --- |
| Client ID | `Dhan:ClientId` or `DHAN_CLIENT_ID`; editable on the Connectors page | Shown on the Dhan profile. |
| API key | `Dhan:ApiKey` or `DHAN_API_KEY` | Valid 12 months. Generated with the redirect URL below. |
| API secret | `Dhan:ApiSecret` or `DHAN_API_SECRET`; editable on the Connectors page | Stored encrypted when saved from the console. |
| Access token (optional) | `Dhan:AccessToken` or `DHAN_ACCESS_TOKEN` | A 24-hour token pasted from the console. Used only while nobody has signed in today. |

The `Dhan:*` keys go in `appsettings.Local.json`, which is never committed. The
`DHAN_*` names are environment variables.

**Redirect URL** registered with the API key: `https://openfno.com/api/dhan/callback`.
It must match character for character.

### The daily sign-in (Connect)

Dhan tokens last 24 hours, so, as with FYERS, someone signs in once a day. It
takes one click and one login:

1. On **Connectors → Dhan**, press **Connect**. The platform asks Dhan for a
   consent with the API key and secret, and opens Dhan's login page. Dhan allows
   25 consents a day.
2. Sign in on Dhan's site with your Dhan login and 2FA.
3. Dhan returns to `/api/dhan/callback` with a one-time `tokenId`. The platform
   exchanges it for the day's access token and saves it encrypted as the
   connector's session. You land back on the Dhan connector page, marked
   connected.

**Safeguards:**
- **Pinned account.** The callback accepts a sign-in only for the configured
  client ID. A sign-in to any other Dhan account is refused and nothing is saved.
- **Token order.** Every Dhan call uses the signed-in session while it is valid,
  and falls back to a pasted token only when there is none.
- **Separate from FYERS.** The platform's broker session (used by the FYERS feed,
  history sync and backtests) is scoped to FYERS. A Dhan sign-in can never be
  handed to them, and a FYERS disconnect no longer signs Dhan out.

The unattended route that removes even this click is PIN + TOTP (see *Next
steps*).

## Checking it

| Endpoint | What it answers |
| --- | --- |
| `GET /api/Dhan/status` | Token accepted? Where did it come from (sign-in or configuration)? Valid until when? Data plan active? Read from Dhan's own profile, so "configured" is never mistaken for "working". |
| `GET /api/Providers/dhan/auth-url` | Dhan's login page for today's sign-in. The Connect button calls this. |
| `GET /api/dhan/callback?tokenId=` | Where Dhan returns after the sign-in. Open to the browser, pinned to the configured account. |
| `GET /api/Dhan/session` | The client id and token the live feed connects with (admin or Service account only). |
| `POST /api/Providers/dhan/test` | History probe: BANKNIFTY index, 15-minute bars, last 7 days. It returned 100 bars in 0.6 s. |
| `GET /api/Dhan/expiries?underlying=NIFTY` | Expiries Dhan lists, nearest first. |
| `GET /api/Dhan/chain?underlying=BANKNIFTY&expiry=2026-09-29` | The whole chain, plus peak call and put OI strikes and put/call OI ratio. |
| `POST /api/Dhan/instruments/import` | Downloads Dhan's instrument master and maps every live instrument. About a minute. |
| `POST /api/Dhan/instruments/resolve` | Dhan ids for canonical symbols; the live feed subscribes with this. |
| `GET /api/Dhan/universe` | What the live feed streams beyond the recording list, with the prices the strikes were centred on and any warnings. |
| `GET /api/Dhan/chain-poller` | Is the chain recorder on, and what each underlying's last round did (recorded, idle because its market is closed, or failed and why). |
| `POST /api/Dhan/chain-poller/start` and `/stop` | Turns the recorder on or off until the API restarts. |
| `POST /api/Dhan/chain-poller/capture?underlyings=CRUDEOIL` | One round now. Closed markets are skipped unless `evenIfClosed=true`. |

All of them are admin-only, except `session`, `resolve` and `universe`, which the
Service account behind the live feed may call too.

## Every trading day

`scripts/market-open.sh` runs at 08:45 IST on the server:

1. Holiday check (System > Market calendar). On a holiday nothing starts.
2. **Dhan status.** If Dhan is not signed in, a notification asks for Connect,
   and the script waits until 09:12. If the token ends before the MCX close, it
   says so.
3. **Instrument import**, so the day's new strikes and expiries have Dhan ids.
4. **Dhan feed** started from Live feeds, and the **chain recorder** switched on.
5. FYERS sign-in wait, as before, for the strategies.
6. The FYERS feed and its chain poller are **not** started while Dhan is the
   feed.
7. After the open, the script counts fresh prices. If the Dhan feed delivered
   none, it stops it and starts the FYERS feed instead; Dhan's chain keeps
   recording.

Only one live feed runs at a time. Bars and latest quotes are kept per symbol,
not per vendor, so two feeds on one contract would build a bar from two vendors'
volume counters.

**Operator's one job:** press **Connect** on Connectors → Dhan before 09:00. The
feed picks up a new token by itself; nothing needs restarting.

## Live feed

The adapter is `market_data/live/vendors/dhan.py`, started from Live feeds like
every other vendor.

- **Credentials** come from `GET /api/Dhan/session`, so the daily Connect is
  all it needs. `.env` (`DHAN_CLIENT_ID`, `DHAN_ACCESS_TOKEN`) is the fallback.
  A refused token is remembered, and the feed waits, checking every 30 s, for a
  different one.
- **What it streams:** the recording list, plus the universe from
  `GET /api/Dhan/universe`, asked again every 5 minutes so strikes follow the
  market:
  - the six indices;
  - the two nearest futures of each index, CRUDEOIL(M), NATURALGAS, GOLD(M) and
    SILVER(M);
  - five strikes either side of at-the-money for the nearest expiry of each
    index, CRUDEOIL and NATURALGAS; on an expiry day, the next expiry too.
  - Prices for at-the-money come from Dhan's last price. When the exchange is
    closed they fall back to the last recorded quote, candle or chain spot.
- **One update a second per contract** (`DHAN_MIN_TICK_INTERVAL_MS`, default
  1000). Dhan sends about ten a second per contract in Full mode, and the server
  records every update it receives. Each update sent carries the latest merged
  state, and a contract's last change is always sent, even if it then goes quiet.
- **Depth** (five levels) is stored with each tick in the database, but left
  out of the Redis stream that strategies read.

Measured on MCX on 2026-09-14: 66 contracts, about 55 updates a second in total,
with price, bid and ask with sizes, volume, OI and exchange time on every quote.

Settings, all optional, in the `Dhan:Universe` section: `StrikesEachSide` (5),
`FuturesPerUnderlying` (2), `IndexUnderlyings`, `McxFutureUnderlyings`,
`McxOptionUnderlyings` (comma-separated).

## Option chain recording

The chain recorder runs inside the API. Each round covers every configured
underlying whose exchange is open: the nearest expiry, one call per underlying.
Dhan allows one chain call every three seconds, so a round of eight takes about
25 seconds. The rows are written through the option chain module with source
`dhan`, under the platform's own contract symbols, matched by strike and CE/PE
(never built from a string).

| Setting (`Dhan:ChainPoller`) | Default | |
| --- | --- | --- |
| `Enabled` | false | On only on the host that records. Dhan's chain limit is per account, so two hosts polling would starve each other. |
| `IntervalSeconds` | 60 | Between the starts of two rounds. |
| `Underlyings` | NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX, BANKEX, CRUDEOIL, NATURALGAS | An MCX commodity is chained on its nearest future. |

First MCX capture, 2026-09-14 18:24 IST:
- CRUDEOIL: 402 rows. 24 strikes Dhan lists had no platform contract and were skipped.
- NATURALGAS: 184 rows.

## How symbols are translated

Dhan identifies an instrument by an **exchange segment** and a numeric
**security id**; the platform by its canonical symbol. A mapping row stores
Dhan's side as `SEGMENT:SECURITYID:INSTRUMENT`, for example
`NSE_FNO:47317:OPTIDX` for `NSE:NIFTY2691523950CE`.

- **Indices** are a fixed table: NIFTY 13, BANKNIFTY 25, FINNIFTY 27,
  MIDCPNIFTY 442 (NSE), SENSEX 51, BANKEX 69 (BSE), all in segment `IDX_I`.
- **Everything else** comes from the instrument import, which matches Dhan's
  master to the platform's instruments **by contract, never by building a name**.
  - Options: exchange, underlying, expiry date, strike and CE/PE.
  - Futures: exchange, underlying and expiry.
  - NSE equities: the symbol, series EQ.

Traps in Dhan's master file, all handled:

| Trap | Why it matters |
| --- | --- |
| The derivative rows give NIFTY's underlying id as **26000**, while the index itself, the chain and the feed use **13**. | Matching on that id would pair options with the wrong instrument, so the underlying is matched by symbol. |
| A future's strike is `-0.01`, not zero. | A zero test would not recognise the row as a future. |
| `INSTRUMENT_TYPE` says `OP` on NSE but `OPTIDX` on BSE. | The `INSTRUMENT` column is the consistent one. |
| Expired contracts are still in the file. | They are skipped. |
| The header ends with a trailing comma. | Columns are read by name. |

Of 104,355 live instruments, 104,114 matched. The remaining 235 are NSE shares
outside the EQ series, which Dhan lists under other series.

## History rules

- Timestamps mark the **start** of each bar. A 1-minute session runs 09:15 to
  15:29: 375 bars.
- Dhan's `fromDate` **excludes a bar that starts exactly on it**. Asked from 09:15,
  the 5-minute answer began at 09:20. Requests therefore start one minute early,
  and the result is trimmed to the range asked for.
- Daily bars are stamped 00:00 IST of their date, and the daily `toDate` is
  exclusive.
- Intraday requests cover at most **90 days**; longer ranges are split
  automatically. History goes back five years intraday, and to inception daily.
- Derivatives and MCX return **open interest** per bar. An OI of zero is reported
  as unknown, not as "every position closed".
- Index bars carry a volume figure. What Dhan aggregates into it is not yet
  documented, so treat it as indicative.

## Option chain

One call per underlying and expiry. For each strike, calls and puts carry:
- last price, previous close and average price;
- best bid and ask with sizes;
- OI and previous OI, with the change computed;
- volume and previous volume;
- implied volatility;
- delta, theta, gamma and vega;
- the contract's security id.

Greeks or an IV of exactly zero mean Dhan could not price that option (seen on
deep in-the-money puts), and are reported as unknown.

Example, BANKNIFTY 29 Sep 2026, spot 56,606.55, 359 strikes, PCR 0.92:

| 56600 | LTP | OI | OI change | Volume | IV | Delta |
| --- | --- | --- | --- | --- | --- | --- |
| Call | 818.90 | 1,28,520 | +12,870 | 14,35,410 | 14.8 | 0.563 |
| Put | 493.90 | 1,32,990 | +30,840 | 9,21,870 | 13.1 | −0.431 |

## Limits Dhan enforces

| Kind | Limit | How the connector keeps to it |
| --- | --- | --- |
| Data APIs (history) | 5 requests/s, 100,000/day | paced to one call per 220 ms |
| Market quote | 1 request/s, 1,000 instruments per call | paced to one per 1.1 s |
| Option chain | one request per 3 s | paced to one per 3.1 s |
| Live feed | 5 connections, 5,000 instruments each, 100 per subscribe message | enforced in the Python adapter |
| Orders (not built) | 10/s, static IP required | — |

Pacing is process-wide, because Dhan's limits are per account.

## Errors

The connector reads both of Dhan's error shapes and says which kind of problem it
is.

| Kind | Codes | What the operator must do |
| --- | --- | --- |
| Token or client id | `DH-901`; 807 expired, 808 authentication failed, 809 invalid token, 810 invalid client id | Generate a new token. |
| No data plan | `DH-902`, 806 | Subscribe to the Data APIs; a new token will not help. |
| Refused request or symbol | anything else | For history, the caller skips that one symbol and carries on. |

## Next steps

1. **Verify the first NSE session** (2026-09-15):
   - index packets in Quote mode;
   - the tick rate and database growth at about 200 contracts;
   - subscribing new strikes in place as the universe rolls.
2. **Unattended sign-in.**
   `POST https://auth.dhan.co/app/generateAccessToken?dhanClientId=&pin=&totp=`
   returns a 24-hour token with no API key, once TOTP is enabled on the account.
   The PIN and TOTP secret will be stored encrypted, and the token renewed before
   the open.
3. **Stock option chains**, using the mapped underlying ids.
4. **More than one expiry per recorded chain.** The chain read assumes one
   expiry per capture today.
5. **Shadow comparison** against FYERS.

## Code map

| File | Role |
| --- | --- |
| `Providers/Dhan/DhanProvider.cs` | Descriptor: capabilities, limits, credential labels |
| `Providers/Dhan/DhanRegistration.cs` | Registers everything below |
| `Providers/Dhan/DhanApiClient.cs` | Headers, token choice (sign-in, then configuration), pacing (`DhanRateGate`), error reading |
| `Providers/Dhan/DhanLogin.cs` | The daily sign-in: consent, login URL, callback exchange, account pin |
| `Providers/Dhan/DhanHistory.cs` | History request and response rules |
| `Providers/Dhan/DhanMarketDataProvider.cs` | `IMarketDataProvider` for history |
| `Providers/Dhan/DhanOptionChain.cs`, `DhanOptionChainClient.cs` | Chain model and client |
| `Providers/Dhan/DhanChainPoller.cs` | Chain recorder: rows mapping, per-underlying rounds, the hosted loop and its state |
| `Providers/Dhan/DhanUniverse.cs` | What the live feed streams beyond the recording list |
| `Providers/Dhan/DhanInstruments.cs`, `DhanInstrumentMaster.cs`, `DhanInstrumentImporter.cs` | Symbol vocabulary, master parsing and matching, bulk import |
| `Api/Controllers/DhanController.cs` | The endpoints above |
| `tests/AlgoTrading.UnitTests/DhanConnectorTests.cs` | Rules pinned against real answers and master rows |
| `tests/AlgoTrading.UnitTests/DhanLoginTests.cs` | Sign-in, account pin, 24-hour expiry, and the FYERS session guard |
| `tests/AlgoTrading.UnitTests/DhanChainPollerTests.cs` | Chain rows, expiry choice, closed markets, rejected tokens, at-the-money selection |
| `market_data/live/vendors/dhan.py`, `tests/test_dhan_feed.py` (Python engine) | Live feed adapter: binary packets, subscribe batching, credentials from the API, one update a second per contract, the universe |
| `scripts/market-open.sh` | The morning: Dhan status, import, feed and recorder; FYERS as the fallback |
