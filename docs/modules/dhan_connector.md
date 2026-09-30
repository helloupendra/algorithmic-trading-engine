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
| Expired options history import (into `option_history_bars`) | **Built**: unit-tested, migration applied locally; a full local import not yet run |
| Daily sign-in with the API key (Connect, like FYERS) | **Working** |
| Unattended daily sign-in (PIN + TOTP) | **Built** 2026-09-27: runs once `DHAN_PIN` and `DHAN_TOTP_SECRET` are set on the server |
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
| PIN (for the automatic sign-in) | `Dhan:Pin` or `DHAN_PIN` | The account's Dhan PIN. **Server only.** |
| TOTP secret (for the automatic sign-in) | `Dhan:TotpSecret` or `DHAN_TOTP_SECRET` | The base32 secret Dhan shows once when TOTP is set up. **Server only.** |
| Access token (optional) | `Dhan:AccessToken` or `DHAN_ACCESS_TOKEN` | A 24-hour token pasted from the console. Used only while nobody has signed in today, and never once the token's own expiry (read from the JWT) has passed. |

The `Dhan:*` keys go in `appsettings.Local.json`, which is never committed. The
`DHAN_*` names are environment variables. On the server, put them in `.env`:
every deploy regenerates `appsettings.Local.json` from it
(`scripts/_gen_local_settings.py` writes the Dhan section, never the access
token), and every API start, by the desk or by the morning job, loads `.env`
into its environment.

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

### The automatic sign-in (PIN + TOTP)

With the PIN and TOTP secret set, the desk takes the day's token itself, through
Dhan's `POST https://auth.dhan.co/app/generateAccessToken?dhanClientId=…&pin=…&totp=…`.
The token is saved exactly where Connect saves it, so the feed and every call
pick it up the same way. Connect keeps working alongside it.

**One-time setup, by the account holder:**
1. On web.dhan.co, *DhanHQ Trading APIs* → **Setup TOTP**. Dhan shows a QR code
   and the secret behind it. Add it to an authenticator app too, and confirm a
   code, so the account can still be reached by hand.
2. On the server, add to `.env` (never to the Mac's; see below):
   `DHAN_PIN=…` and `DHAN_TOTP_SECRET=…`.
3. Run `python3 scripts/_gen_local_settings.py`. The API reloads the file by
   itself; no restart.
4. On **Connectors → Dhan**, the *Automatic sign-in* panel shows **On**. Press
   **Sign in now** once to prove it end to end.

**When it signs in** (`DhanAutoSignInPolicy`, checked once a minute, weekdays
IST, **never before 08:00**):
- when there is no sign-in that is still valid, or it ends within 10 minutes;
- between 08:00 and 08:40, when the token would end before 23:59 tonight plus
  those 10 minutes. The 08:45 job starts the feeds, so nothing is streaming on
  Dhan yet.

Nothing happens before 08:00, even with no token at all: nothing streams on Dhan
before the 08:45 job, and starting every automatic sign-in at 08:00 means each
day's token is taken at the same hour and lasts the whole session. Before this
rule, the sign-in crept ten minutes earlier every day, and a token that died at
midnight was tried for at 00:00:30, which could use up the day's tries before
the morning.

It never replaces a working token in the middle of the session. Dhan's
documentation says `RenewToken` ends the token it renews, and says nothing about
whether a new sign-in ends the previous one; a sign-in at 11:00 could cut off a
feed that was streaming.

**When it stops:** Dhan refusing the PIN or code, or a value missing or
malformed, stops the automatic tries for the rest of the IST day, with one
Telegram message. A wrong PIN is wrong every time, and repeated wrong PINs can
lock the account. Dhan unreachable (no answer, a 5xx, 408 or 429, or a token
Dhan issued that could not be saved) is tried again after 15 minutes, three
times a day.

The stop **survives API restarts.** It is saved in `system_settings` under
`dhan.autosignin.stopped` as the IST day and a fixed reason
(`2026-09-29: Dhan refused the PIN or the code`, never the PIN). The worker
reads it back at startup, and every automatic try (the worker's or the morning
job's) reads it again first. When it was kept in memory only, the 08:45 restart
and every deploy wiped it, and the next try would have sent the same wrong PIN
again. A stop from an earlier day is ignored. If the setting cannot be read, no
automatic try is made: that costs a morning of pressing Connect, where trying
could lock the account.

**Sign in now** on the page always tries, whatever is saved, and a sign-in that
works clears the saved stop. To take the automatic sign-in off for longer, set
`Dhan:AutoSignIn:Enabled` to false; the morning job's call then gets 409 too.

**Safeguards:**
- **Server only.** Two hosts holding the PIN would each take the account's token
  on their own schedule.
- **The PIN never reaches a log.** Dhan takes it in the query string, so the
  HTTP client for this call is registered with no request logging at all, and a
  refusal repeats only Dhan's reason fields, never the response body.
- **Pinned account**, as for Connect: a token for any other client ID is refused.
- **One code, one sign-in.** Sign-ins run one at a time. A machine that asks
  within 2 minutes of a sign-in gets that one back without asking Dhan (the
  worker and the morning job can both decide "no token" at the same moment).
  No code is sent from the same 30-second TOTP step as the one before; it waits
  for the next step.
- **Not cancelled by the caller.** The morning job's `curl` gives up after 30
  seconds; the sign-in carries on, so a token Dhan has issued is still saved.
- Every attempt says what it did on Telegram (Connector category), and the panel
  shows the last try and when the token it took ends.

## Checking it

| Endpoint | What it answers |
| --- | --- |
| `GET /api/Dhan/status` | Token accepted? Where did it come from (sign-in or configuration)? Valid until when? Data plan active? Read from Dhan's own profile, so "configured" is never mistaken for "working". |
| `GET /api/Providers/dhan/auth-url` | Dhan's login page for today's sign-in. The Connect button calls this. |
| `GET /api/dhan/callback?tokenId=` | Where Dhan returns after the sign-in. Open to the browser, pinned to the configured account. |
| `GET /api/Dhan/session` | The client id and token the live feed connects with (admin or Service account only). |
| `GET /api/Dhan/auto-sign-in` | Is the automatic sign-in set up (names what is missing, never a value), its morning window, and what it last did. |
| `POST /api/Dhan/auto-sign-in?trigger=console` | Sign in now with the PIN and a TOTP code. 200 with the token's end; 400 when Dhan refused or a value is missing; 502 when Dhan was not reached. `trigger=morning job` gets 409 without asking Dhan when the automatic sign-in is switched off, stopped for today (including a stop saved before a restart), or pausing after a failure; within 2 minutes of a sign-in it gets 200 with that one. |
| `POST /api/Providers/dhan/test` | History probe: BANKNIFTY index, 15-minute bars, last 7 days. It returned 100 bars in 0.6 s. |
| `GET /api/Dhan/expiries?underlying=NIFTY` | Expiries Dhan lists, nearest first. |
| `GET /api/Dhan/chain?underlying=BANKNIFTY&expiry=2026-09-29` | The whole chain, plus peak call and put OI strikes and put/call OI ratio. |
| `POST /api/Dhan/instruments/import` | Downloads Dhan's instrument master and maps every live instrument. About a minute. |
| `POST /api/Dhan/instruments/resolve` | Dhan ids for canonical symbols; the live feed subscribes with this. |
| `GET /api/Dhan/universe` | What the live feed streams beyond the recording list, with the prices the strikes were centred on and any warnings. |
| `GET /api/Dhan/chain-poller` | Is the chain recorder on, and what each underlying's last round did (recorded, idle because its market is closed, or failed and why). |
| `POST /api/Dhan/chain-poller/start` and `/stop` | Turns the recorder on or off until the end of the IST day, surviving API restarts. The next day starts from `Dhan:ChainPoller:Enabled` again. |
| `POST /api/Dhan/chain-poller/capture?underlyings=CRUDEOIL` | One round now. Closed markets are skipped unless `evenIfClosed=true`. |

All of them are admin-only, except `session`, `resolve` and `universe`, which the
Service account behind the live feed may call too.

## Every trading day

`scripts/market-open.sh` runs at 08:45 IST on the server:

1. Holiday check (System → Calendar). On a holiday nothing starts.
2. **Dhan status.** If Dhan is not signed in, the script asks the API for the
   automatic sign-in first (when it is set up). If that fails too, a
   notification asks for Connect, and the script waits until 09:12. If the token
   ends before the MCX close, it takes a fresh one the same way (nothing streams
   on Dhan yet), and otherwise says so.
3. **Instrument import**, so the day's new strikes and expiries have Dhan ids.
4. **Dhan feed** started from Data → Feeds, and the **chain recorder** switched on.
5. FYERS sign-in wait, as before, for the strategies.
6. The FYERS feed and its chain poller are **not** started while Dhan is the
   feed.
7. After the open, the script counts fresh prices. If the Dhan feed delivered
   none, it stops it and starts the FYERS feed instead; Dhan's chain keeps
   recording.
8. It deploys the plan (`config/morning-plan.txt`) into every account, then,
   a minute later, **counts the live runs against the plan** and sends the
   answer to Telegram: `Morning plan 23/23 live`, or `Morning plan SHORT 21/23
   live — <account> <strategy> <underlying>: <why it ended>`. A short or unread
   count exits 2. A runner that dies, or a run that lasts under a minute, is
   also a warning on its own, not an ordinary "stopped" message.

Only one live feed runs at a time. Bars and latest quotes are kept per symbol,
not per vendor, so two feeds on one contract would build a bar from two vendors'
volume counters.

**Operator's one job:** none, once the automatic sign-in is set up; the
Telegram message at about 08:00 says it worked. Without it, press **Connect** on
Connectors → Dhan before 09:00. Either way the feed picks up a new token by
itself; nothing needs restarting.

### When Dhan goes silent during the session (automatic failover)

Step 7 covers the open. For the rest of the session the API's
`FeedFailoverService` does the same job: on 24 Sep the last Dhan tick was
11:27:36 and FYERS took over at 11:34:06 only because someone switched by hand;
unattended, all 26 runs would have stayed blind until the 15:30 square-off.

**It ships as a dry run** (owner's decision, 27 Sep): it watches, logs what it
would do and sends one message per incident, and changes nothing.

- **When it looks.** Every 15 s from 09:20 IST while NSE is open, and only while
  Dhan is the feed: the Dhan feed runs and the FYERS feed does not.
- **What it measures.** The newest live tick per exchange (NSE with NFO, BSE
  with BFO, MCX) in Redis `market:ticks`, the stream the strategies read. The
  measurement is Sentinel's feed-silent rule: the tick's `receivedUtc`, replays
  ignored, the age taken from the later of the tick and the 09:15 open.
  `live_quotes_latest` is not used: it keeps every symbol's last price forever
  (127 "prices" from the previous evening on 10 Sep).
- **What counts as silent.** NSE, or BSE once it has ticked today, older than
  **150 s on two checks in a row**, with the Dhan process up for more than
  180 s. MCX is reported but never acted on. The thresholds are staged so each
  layer gets its turn: at 90 s Sentinel opens an incident and the runners log
  `FEED STALLED`; at 120 s the feed rebuilds its own connection (`WATCHDOG`); at
  150 s on two checks the failover acts; a runner's stall reaches Telegram at
  180 s.
- **FYERS is checked with a real call**: `/api/v3/profile` with the token the
  FYERS feed would be handed. "Authenticated" in our own table is not proof (10 Sep).
- **Dry run.** Logs `FEED FAILOVER (dry run) would switch: <ages per exchange>;
  <Dhan pid, uptime>; FYERS: <answer>; at HH:mm:ss IST` and sends **one**
  System-channel message per incident, with Dhan's `/profile` answer and the
  feed's last heartbeat. Writes nothing.
- **Live** (`FeedFailover:DryRun` false). Records `feed.failover.<date>` in
  system settings first, then does what `market-open.sh` does at the open: stops
  the Dhan feed and starts the FYERS feed and the FYERS chain poller (Dhan's
  chain recorder keeps running). It then expects fresh FYERS ticks within 90 s
  and sends one critical alert with the cause and the result. **At most one
  switch a day**, surviving API restarts; it **never switches back**. Return to
  Dhan by hand on Data → Feeds.
- **FYERS not signed in.** `Dhan silent and FYERS not signed in — sign in to
  FYERS` every 10 minutes while the silence lasts; a live failover switches as
  soon as the sign-in lands.
- **An incident ends** when the ticks are fresh again: `FEED FAILOVER: recovered
  after Ns` in the log, once. The recovery is not sent.

**Turning the dry run off.** On the server, in
`src/AlgoTrading.Api/appsettings.Local.json`:

```json
"FeedFailover": { "DryRun": false }
```

It is read at every check, so no restart is needed. `"Enabled": false` switches
the service off entirely. To keep it from switching for the rest of one day,
write any value to the `feed.failover.<yyyy-MM-dd>` system setting.

**Judging a dry run.** All lines are in `logs/api.log`, prefixed
`FEED FAILOVER`:

| Line | Meaning |
| --- | --- |
| `FEED FAILOVER: dry run (logs and one message per incident; switches nothing) — checks every 15 s …` | The service started, and in which mode. |
| `FEED FAILOVER: Dhan ticks stale on one check at HH:mm:ss IST — …` | One stale check. Followed by nothing when the feed's own reconnect fixed it. |
| `FEED FAILOVER: Dhan feed silent at HH:mm:ss IST — …` | Two stale checks: an incident. |
| `FEED FAILOVER (dry run) would switch: …` | FYERS answered, so a live run would have switched here. Once per incident. |
| `FEED FAILOVER: Dhan says: …` | Dhan's `/profile` and the feed's last heartbeat at that moment. |
| `FEED FAILOVER: Dhan silent and FYERS not signed in at …` | Would have switched but could not; repeats every 10 minutes. |
| `FEED FAILOVER: recovered after Ns (silent since …, fresh at …)` | The incident's end. A short gap after a `would switch` line means the switch would not have been needed. |
| `FEED FAILOVER: incident closed without a switch — …` | Someone switched by hand, Dhan was restarted, or the session closed. |
| `FEED FAILOVER: not acting — today's one switch was already made (…)` | The day's switch is used. |

## Live feed

The adapter is `market_data/live/vendors/dhan.py`, started from Data → Feeds like
every other vendor.

- **Credentials** come from `GET /api/Dhan/session`, so the daily Connect is
  all it needs. `.env` (`DHAN_CLIENT_ID`, `DHAN_ACCESS_TOKEN`) is the fallback,
  then the environment the feed started with. A token whose own expiry (the
  JWT's `exp`) has passed, or is under a minute away, is skipped from every
  source: Dhan accepts a socket on a dead token and drops it without a reason,
  which on 16 Sep meant 238 reconnects and a blocked client id. A refused token
  is remembered and skipped too. When the feed runs on a fallback, its log says
  why each earlier source was passed over, for example
  `the API has no Dhan session (404: …); the token from .env expired 15 Sep 15:20 IST — using the Dhan credential from …`.
  With nothing usable left it does not connect: it waits, checking every 30 s,
  for a different token.
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
- **Why it reconnected.** Each time the feed's watchdog rebuilds the connection
  (the vendor refused the login, the credential was rejected, the socket stayed
  down, or it stayed up and carried nothing), the cause goes to the System
  channel as well as the feed's log, at most once per cause per 10 minutes
  (`core/live/watchdog_alerts.py`). On 25 Sep the desk got 572 feed alerts and
  none of them said why.

Measured on MCX on 2026-09-14: 66 contracts, about 55 updates a second in total,
with price, bid and ask with sizes, volume, OI and exchange time on every quote.

Settings, all optional, in the `Dhan:Universe` section: `StrikesEachSide` (5),
`FuturesPerUnderlying` (2), `IndexUnderlyings`, `McxFutureUnderlyings`,
`McxOptionUnderlyings` (comma-separated).

## Read path

### What went wrong on 28 Sep 2026

From 09:16 IST, when the 23 strategy runners started, the 2-vCPU server ran at
a load average of 10 to 23. The Dhan feed kept going "connected but silent for
120 s", its watchdog rebuilt the connection, and the runners logged FEED
STALLED for 90 to 130 s every 4 to 6 minutes. At 10:36 the socket's kernel
receive queue grew from 750 KB to 958 KB in 25 s. Dhan was sending; the feed
was not reading fast enough.

The socket thread did everything for every frame. It decoded the packet,
merged and conflated it, priced option greeks, and handed the ticks on, which
included one synchronous Redis write per tick. It also waited on the lock the
conflation flusher held across its own Redis writes. It topped out near 300
frames a second, and each of the 23 runners then decoded every one of the ~300
contracts' ticks only to keep its own index.

Whether Dhan stops sending once our receive window is full, and for how long,
is inferred. It is not proven.

### How it reads now

- **The socket thread only reads.** Per frame it reads the header, looks up the
  instrument, and keeps the frame as that instrument's latest packet of its
  kind (Full, Quote, OI and so on). It takes one lock, held only for that
  store. It never decodes a price, never takes the conflation lock, and never
  prints, publishes or calls the API in normal running. Disconnect,
  market-status and unknown-code packets are still handled on the spot, as
  before.
- **One `dhan-emit` thread hands on.** Every `DHAN_EMIT_PERIOD_MS` (100) it
  swaps the stored packets out. It merges each instrument's packets in the
  order their latest copies arrived and applies the same conflation rule as
  before: one tick per contract per second, the first after a quiet second at
  once. Then it hands the ticks on in one call. A pass that carries several
  packets for one contract sends its merged state once. A packet superseded
  before a pass is never decoded. The only field that can differ is one the
  superseded packet carried and the newest one sends as zero: the value from
  before the pass stays (pinned by a test).
- **Storage first, then one Redis write.** The runner hands every tick to the
  API's pump, then writes the whole batch to `market:ticks` in one pipelined
  round trip, never retried. The stream has its own Redis client that gives up
  after `FEED_REDIS_TIMEOUT_SECONDS` (2) with no retries. redis-py's default
  retries ten times on a 5 s timeout, so one write to a hung Redis could hold
  the feed for about a minute. After a failed write the stream is left alone
  for 5 s. The ticks still reach the API, and are counted as not published.
- **The process favours the socket.** The GIL switch interval is
  `FEED_GIL_SWITCH_MS` (1 ms; Python's is 5 ms). The objects startup made
  (numpy, scipy and vollib for the greeks, loaded at startup now) are frozen
  out of the cyclic GC (`FEED_GC_FREEZE`), so a full collection, which stops
  every thread, has far less to walk. The load test caught one such pass at
  391 ms.
- **The runners read less, and give way.** Each runner keeps only its spot:
  every other entry is passed over on the stream's `symbol` field without
  decoding its JSON (`RUNNER_STREAM_FILTER`). It reads in batches of
  `RUNNER_STREAM_BATCH_MS` (100), and runs at `RUNNER_NICE` (10), set before
  it starts a thread. `market:ticks` itself is unchanged: Sentinel, the pager,
  the feed failover and the Worker read every symbol on it.

Every switch is read when the process starts and restores the old behaviour
for its piece alone. See `.env.example`, where the value that restores 28 Sep
is shown in brackets.

| Switch | Default | 28 Sep behaviour |
| --- | --- | --- |
| `DHAN_INGEST` | `emitter` | `inline` |
| `DHAN_EMIT_PERIOD_MS` | 100 | none needed (the old flusher also woke every 100 ms) |
| `FEED_STREAM_BATCH` | 1 | 0 |
| `FEED_REDIS_TIMEOUT_SECONDS` | 2 | 0 |
| `FEED_GIL_SWITCH_MS` | 1 | 0 |
| `FEED_GC_FREEZE` | 1 | 0 |
| `FEED_FRAME_SILENCE_SECONDS` | 45 | 0 |
| `FEED_BACKLOG_ALERT_KB` | 128 | none needed (an alert threshold only) |
| `RUNNER_STREAM_FILTER` | 1 | 0 |
| `RUNNER_STREAM_BATCH_MS` | 100 | 0 |
| `RUNNER_NICE` | 10 | 0 |

### What the feed's log says about it

- **The stats line, every 60 s**, in this form:

  ```
  [dhan] read path: frames F (F/s), K KB/s, pings P, superseded S%, dropped D,
  socket backlog p99 B KB max M KB | emitted T ticks/s, pass p99 X ms max Y |
  stream batches N, p99 Z ms, errors E
  ```

  - **Frames and KB/s**: what Dhan sent in the minute.
  - **Superseded**: packets replaced by a newer one of their kind before a pass
    could hand them on. This is work saved.
  - **Dropped**: packets for an instrument the socket no longer carries.
  - **Socket backlog**: the kernel's unread bytes, sampled once a second. This
    is the Recv-Q that `ss -tn` shows.
  - **Pass**: how long the emitter's passes took.
  - **Stream**: the Redis writes, and the publish errors since the feed started.
- **`FALLING BEHIND: N KB unread on the socket for 10s (emit pass p99 X ms)`**:
  ten one-second samples in a row at or above `FEED_BACKLOG_ALERT_KB` in open
  session. It is said once per episode, and carried as the heartbeat's
  `lastError` until ten samples in a row fall below the line. It then says
  `socket drained again after Ns`. Nothing is restarted for it: a reconnect
  would lose the unread bytes and serve the same load again. If it shows up
  live, the in-process reader is not enough (see the next steps).
- **`connected but silent for Ns in open session (no frame at all, not even a
  ping)`**: no frame of any kind, including Dhan's own ping every 10 s, for
  `FEED_FRAME_SILENCE_SECONDS` (45). The line is dead, and the watchdog
  rebuilds it without waiting for the 120 s no-tick rule. That rule still
  covers a line that pings but carries no prices, and the time before the
  first frame. Like that rule, it follows NSE's hours, so it does not run in
  the MCX evening session. It starts with "connected but silent", so existing
  log readers still match it.

### Proving it before a session

`tools/feed_loadtest/` (Python engine) runs the real feed, the real Redis
publisher and the real runner reader on two pinned vCPUs of Docker Desktop's
Linux VM. They run against a local fake Dhan that sends TLS websocket frames
(one TLS record per frame, like Dhan) and pings, and they run beside CPU
burners and 23 runner emulators:

```
src/AlgoTrading.PythonEngine/tools/feed_loadtest/matrix.sh            # every case, 3 trials
src/AlgoTrading.PythonEngine/tools/feed_loadtest/matrix.sh L2 G       # some cases
```

- **Baseline and fix.** It runs the baseline (`BASE_REF`, default `ee27219`,
  unpacked with `git archive`) and this tree side by side.
- **Checks per trial.** It prints PASS or FAIL per criterion: the socket's
  unread bytes, the fake Dhan's pong round trip (the lag Dhan itself sees),
  probe lag to a runner, stored ticks, restarts and cutoffs.
- **Local only.** Nothing in it connects to Dhan, FYERS, Angel or TrueData.
- **Run it on a development Mac, never on the live server.** Its absolute
  numbers depend on how busy that Mac is, so compare the baseline and the fix
  from the same run.

### Rolling back

- **During the session.** Set the piece's switch in `.env` and restart only the
  affected process, and only with the owner's go-ahead: the feed for
  `DHAN_*`/`FEED_*`, the runners for `RUNNER_*`. A runner already running
  keeps its nice value until it restarts.
- **After the close.** Revert the commits.

### Next steps, if the live checks fail

- **P1: frame the websocket ourselves** (`DHAN_WS_READER=own`). This is for
  when live Recv-Q p99 stays above 64 KB. It saves about 3 GIL releases per
  frame, since Dhan sends one TLS record per frame.
- **P1: the TickPump flushers wait on a condition** instead of polling.
- **P1: per-symbol spot streams.**
- **P2: a child-process reader.** It must pass kill -9 tests that leave no
  orphan socket on the Dhan account.
- **Owner decisions.** Quote mode for the extras, or a smaller universe. These
  change what gets recorded, so they are the owner's call.

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

**On and off across restarts.** `Enabled` is false on the server; the morning
job switches the recorder on with `POST /api/Dhan/chain-poller/start`. A start
or stop is saved in `system_settings` (`dhan.chainpoller.enabled`, e.g.
`true on 2026-09-24`) and holds until the end of that IST day, so an API restart
in the middle of the session carries on recording. The next day starts from
`Enabled` again. If the saved value cannot be read at startup, the recorder
starts as configured; if it cannot be saved, the switch still happens and a
warning is logged.

Before this, every API restart switched the recorder off without a word. On
2026-09-24 a restart at 11:28 ended the day's chain at 11:27:49: ChainFlowBuy
blocked on a stale chain on every candle until the close, and the evening's
CRUDEOIL and NATURALGAS chains were lost. On 22 Sep nothing was recorded after
14:58:40.

As a backstop, while NSE is open and Dhan is signed in, a recorder that is off
without anyone having stopped it today sends one Telegram warning a day
(Process category): "Dhan chain recorder is off".

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

## Expired options history

Dhan sells minute bars for option series named by their distance from the
money: "NIFTY, nearest weekly expiry, one strike above ATM, call". Each bar has
premium OHLC, volume, OI, IV, the strike at that moment and the index spot. It
is the platform's only record of option premiums, OI and IV from before its own
chain recording began on 2026-09-14. It exists for researching option-buying
strategies on real history.

The importer fetches these series into **`option_history_bars`**, a TimescaleDB
hypertable (migration `OptionHistoryBars`). Its setup:
- 7-day chunks on `BarStartUtc`;
- compression after 7 days, segmented by `Underlying`;
- no retention policy.

| Column | Meaning |
| --- | --- |
| `Underlying` | `NIFTY`, `BANKNIFTY`, `FINNIFTY`, `MIDCPNIFTY`, `SENSEX`, `BANKEX` |
| `ExpiryFlag`, `ExpiryCode` | `WEEK`/`MONTH`; 1 nearest, 2 next, 3 far |
| `ExpiryDate` | Always null: Dhan does not send it (see below). Backtests take the expiry from the exchange calendar, `SeedData/index_option_expiries.json` |
| `StrikeOffset`, `Strike` | 0 is ATM, +1 one strike above; `Strike` is the strike at that offset during the bar |
| `OptionType` | `CE` / `PE` |
| `Resolution` | `1m`, `5m`, `15m`, `60m` |
| `BarStartUtc` | Bar start |
| `Open`…`Close`, `Volume`, `OpenInterest`, `ImpliedVolatility`, `SpotPrice` | As Dhan sends them. OI, IV or spot of 0 are stored as null. Volume and OI are in units, not lots. IV is in percent. |
| `SourceKey` | `dhan` |

The unique index is (Underlying, ExpiryFlag, ExpiryCode, StrikeOffset,
OptionType, Resolution, BarStartUtc). Inserts use `ON CONFLICT DO NOTHING`, so
a re-import writes nothing twice. This also holds for chunks that are already
compressed (checked on TimescaleDB 2.27).

### Endpoints (admin only)

| Call | What it does |
| --- | --- |
| `POST /api/Dhan/option-history/import` | Queues an import and returns `202` with a `jobId` straight away |
| `GET /api/Dhan/option-history/jobs/{id}` | Progress: windows total/done/remaining, what was skipped, empty or failed, rows, requests, retries, ETA, errors |
| `GET /api/Dhan/option-history/jobs` | Every job this process knows, newest first |
| `POST /api/Dhan/option-history/jobs/{id}/cancel` | Stops a job after the requests already in flight |
| `GET /api/Dhan/option-history/coverage?underlying=NIFTY` | For each series: days, bars, first and last day, and runs of consecutive weekdays (every missing weekday, holidays included, breaks a run) |

```json
POST /api/Dhan/option-history/import
{"underlying":"NIFTY","from":"2025-08-01","to":"2025-09-30",
 "strikeOffsets":"-3..3","optionTypes":["CE","PE"],
 "expiryFlag":"WEEK","expiryCode":1,"interval":"5"}
```

- `strikeOffsets` may be written as `"-3..3"`, as `[-3,-2,-1,0,1,2,3]`, or as a
  single number. It must lie within ±10.
- Defaults: `CE` and `PE`, `WEEK`, code 1, interval `5`.
- `to` must be before today, because a session still in progress would be
  stored half-finished.

### How an import runs

1. **Trading days.** It reads the underlying index's daily bars from Dhan: one
   request per year. This gives the exchange's own calendar, including years
   the holiday table does not cover. If that read fails, every weekday counts
   as a trading day.
2. **Windows.** The range is cut into windows of 30 days. Each window is asked
   for once per series, where a series is one offset and one option type.
3. **Skipping.** A window is skipped if the series already has bars on every
   trading day in it, or if the window has no trading days at all. So posting
   the same import again resumes it. Jobs live in memory, so an API restart
   loses the job but keeps the data.
4. **Fetching.** Four lanes run at once. All of them share the process-wide
   `DhanRateGate` (one data request per 220 ms).
5. **Retries.** Throttling (429, 805, `DH-904`), Dhan server errors and network
   failures are retried after 2, 5, 15, 30 and 60 s. Other failures are handled
   differently:
   - a refused token or a missing data plan fails the whole job at once;
   - any other refusal fails only that window, which is listed under `errors`,
     and the job carries on.
6. **Empty answers.** If Dhan returns no bars for a window that has trading
   days, the window is counted as done and reported under `windows.empty`.

Estimate: 2 years × 3 underlyings × ATM±3 × CE+PE is 25 windows × 14 series ×
3, or about 1,050 requests. The gate allows this in about 4 minutes. With Dhan answering in 0.2–4.5 s
per request, expect 5–10 minutes. That is about 1% of the 100,000-request
daily cap. At 1-minute bars it is about 7.8 million rows; at 5-minute bars,
about 1.6 million.

### Verified against the live API (2026-09-15)

Where Dhan's answers differ from its documentation, the answers win.

| Fact | Detail |
| --- | --- |
| Timestamps | Epoch seconds, **bar start**. The first bar is 09:15 IST. The 1-minute `spot` equals the NIFTY index's 1-minute **close** for the same stamp, and the 5-minute `spot` equals the index's 5-minute close. |
| `toDate` | **Inclusive** in practice, though documented as exclusive: from 2025-09-01 to 2025-09-01 returned that day's 75 bars. The importer asks for one day past the window and trims, which is correct either way. |
| Range per call | Documented as 30 days. A 44-day request answered, and 60 days was refused with `DH-905`. The importer keeps to 30. |
| History depth | Data starts in **August 2020**: 2020-08-03 answers, July 2020 is empty. Recent weeks are served too, including today's session after the close. |
| `expiryCode` | **1 is the nearest**, 2 the next, 3 the far. `0` is refused ("expiryCode is required"), although the annexure lists 0 as current. On expiry day, code 1 is still the contract expiring that day: it closed at 0.05 at 15:27 on 2025-09-02. |
| `WEEK` without weeklies | BANKNIFTY and BANKEX `WEEK` answer with the **monthly** series (identical bars to `MONTH`), with no error. |
| Strikes | ATM−10 to ATM+10 answer for index options, weekly and monthly. `ATM+11` answers 200 with empty arrays. A malformed strike such as `"+1"` is silently read as ATM. Lower case `atm+1` works. |
| CE / PE | A CALL request fills `ce` and returns `pe` as null. A PUT request does the reverse. |
| Intervals | 1, 5, 15 and 60 answer. **25 is refused** (`DH-905`) although it is documented. 60-minute bars start at 09:15. |
| BSE | SENSEX (51) and BANKEX (69) need `exchangeSegment` **`BSE_FNO`**. Under `NSE_FNO` or `IDX_I` they answer 200 with no bars. |
| Holidays | 200 with every array empty. The same shape is returned for out-of-range strikes, wrong segments and dates before 2020-08. |
| Extra bars | Some days carry bars after 15:25 (15:30 and 15:35 in September 2026). They are stored as sent. |
| Bar counts | 7,125 one-minute bars for a 30-day window (19 sessions). There is no per-response cap below that. |

**Traps for research:**
- **Bars coarser than 1 minute mix strikes.** When ATM moves inside a 5-minute
  bar, the bar is built from minutes of two different contracts. For example,
  NIFTY CE at 09:20 on 2025-09-01 shows `strike` 24550, but its open (102.45)
  and high (104.8) are the 24500 strike's prices. `strike` is the strike at
  the bar's end. Use 1-minute bars wherever a premium's path matters.
- **The series rolls.** After an expiry, the same offset and code point to the
  next contract. Dhan does not send the expiry date, and the platform has no
  historical expiry calendar (its holiday table starts in 2026), so
  `ExpiryDate` is left null rather than guessed.

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
2. **Stock option chains**, using the mapped underlying ids.
3. **More than one expiry per recorded chain.** The chain read assumes one
   expiry per capture today.
4. **Shadow comparison** against FYERS.

## Code map

| File | Role |
| --- | --- |
| `Providers/Dhan/DhanProvider.cs` | Descriptor: capabilities, limits, credential labels |
| `Providers/Dhan/DhanRegistration.cs` | Registers everything below |
| `Providers/Dhan/DhanApiClient.cs` | Headers, token choice (sign-in, then configuration), pacing (`DhanRateGate`), error reading |
| `Providers/Dhan/DhanLogin.cs` | The daily sign-in: consent, login URL, callback exchange, the PIN + TOTP call, account pin |
| `Providers/Dhan/DhanAutoSignIn.cs` | The automatic sign-in: when (`DhanAutoSignInPolicy`), how often (`DhanAutoSignInState`), the Telegram message, the hosted worker |
| `Providers/Dhan/DhanHistory.cs` | History request and response rules |
| `Providers/Dhan/DhanMarketDataProvider.cs` | `IMarketDataProvider` for history |
| `Providers/Dhan/DhanOptionChain.cs`, `DhanOptionChainClient.cs` | Chain model and client |
| `Providers/Dhan/DhanChainPoller.cs` | Chain recorder: rows mapping, per-underlying rounds, the hosted loop, its state and the saved on/off switch |
| `Providers/Dhan/DhanRollingOptions.cs` | Expired options request and response rules: strike strings, windows, mapping, offsets, coverage runs |
| `Providers/Dhan/DhanOptionHistoryImporter.cs` | Trading days, stored days, one window fetched with retries, bulk insert, coverage query |
| `Providers/Dhan/DhanOptionHistoryJobs.cs` | Import request validation, the resume plan, job state, the background worker |
| `Providers/Dhan/DhanUniverse.cs` | What the live feed streams beyond the recording list |
| `Providers/Dhan/DhanInstruments.cs`, `DhanInstrumentMaster.cs`, `DhanInstrumentImporter.cs` | Symbol vocabulary, master parsing and matching, bulk import |
| `Api/Controllers/DhanController.cs` | The endpoints above, and `GET`/`POST /api/Dhan/auto-sign-in` |
| `tests/AlgoTrading.UnitTests/DhanConnectorTests.cs` | Rules pinned against real answers and master rows |
| `tests/AlgoTrading.UnitTests/DhanLoginTests.cs` | Sign-in, account pin, 24-hour expiry, and the FYERS session guard |
| `tests/AlgoTrading.UnitTests/DhanAutoSignInTests.cs` | The PIN + TOTP call, refusals that never repeat the PIN, when to sign in, the day's tries, the stop across a restart, one code per TOTP step, the morning job's call, expired pasted tokens |
| `tests/AlgoTrading.UnitTests/DhanOptionHistoryTests.cs` | Expired options mapping against real answers, windows, the resume plan, request validation, retry rules |
| `tests/AlgoTrading.UnitTests/DhanChainPollerTests.cs` | Chain rows, expiry choice, closed markets, rejected tokens, the on/off switch across restarts and its warning, at-the-money selection |
| `market_data/live/vendors/dhan.py`, `tests/test_dhan_feed.py` (Python engine) | Live feed adapter: binary packets, subscribe batching, credentials from the API, one update a second per contract, the universe; the store-only socket thread and the `dhan-emit` thread |
| `core/live/feed_runner.py`, `market_data/live/run_feed.py` (Python engine) | Batched stream writes, the frame-silence and FALLING BEHIND watchdogs, the stats line; the GIL switch interval, the bounded stream client, the startup-heap freeze |
| `messaging/redis_subscriber.py`, `core/process_priority.py` (Python engine) | The runners' spot-only, batched read, and their lower CPU priority |
| `tools/feed_loadtest/` (Python engine) | The read path's load test against a local fake Dhan: baseline against fix, pass criteria per trial |
| `scripts/market-open.sh` | The morning: Dhan status, import, feed and recorder; FYERS as the fallback |
| `Api/Services/FeedFailoverService.cs` (+ `FeedFailoverPorts.cs`, `FeedFailoverAdapters.cs`) | The same fallback during the session: tick ages from `market:ticks`, the FYERS profile check, the once-a-day switch; dry run by default (`FeedFailover:DryRun`) |
| `tests/AlgoTrading.UnitTests/FeedFailoverServiceTests.cs` | The failover under a fake clock, stream and feeds: fresh, grace, one stale check, dry run, the switch, the FYERS sign-in reminder, one switch a day across a restart, closed session, before 09:20, per-exchange silence, recovery |
