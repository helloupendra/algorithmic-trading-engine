# LogicEngine

Source: `strategies/logic_engine.py` (`LogicEngine`). **This is not a trading
strategy.** It is the alerts engine: `AlertsSupervisor`
(`src/AlgoTrading.Api/Services/AlertsSupervisor.cs`) launches one
`execution_runner.py --strategy LogicEngine` per chain underlying with a run
row whose `ParametersJson` carries `role: "alerts"`, and the class evaluates
rules on every tick and publishes alerts to the Redis channel `alerts:new`
with `source: "logic_engine"`. The API's `AlertSubscriberService` stores each
one in `alert_events` and forwards it to Telegram. It never opens a position.
It has a spec because `strategies.registry.discover_strategies` registers
every `BaseStrategy` subclass and the spec test reads the registry; it is
hidden from the Strategies catalog by `listed = False` (line 44 —
`tools/list_strategies.py` drops it unless `--include-hidden`, which
`StrategyCatalogService` never passes). Every rule below is read from the
code; where the code and its docstring disagree, the code is documented and
the difference listed under Limitations.

## Idea

The engine watches an index for three things a discretionary options trader
would want a nudge about, and says so on Telegram instead of trading:

1. **Bear trap** — the index prints above the high of its newest 15-minute
   bar while a heavyweight constituent (HDFC Bank or Reliance) trades below
   the low of *its* newest 15-minute bar: a breakout the leaders are not in.
   The alert points at the ATM put.
2. **Open-interest shift** — the strike carrying the most call open interest
   near the money moves *down* (call writers expect a lower ceiling → watch
   the ATM put), or the peak put-OI strike moves *up* (put writers defend a
   higher floor → watch the ATM call).
3. **Selling pressure** — the top of the ATM call's order book shows asks
   several times the bids while the index sits near "resistance": watch the
   ATM put.

On an equity target (not launched by default) the first rule becomes a plain
"price above the session VWAP" bullish alert. Nothing in this repository
tests any of these readings, and — see the worked example — none has ever
fired on this installation.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the target's spot symbol — by default `NSE:NIFTYBANK-INDEX`, `NSE:NIFTY50-INDEX`, `BSE:SENSEX-INDEX` (`AlertsSupervisor.GetTargetsAsync`; overridable by the `alerts.targets` process setting as `UNDERLYING\|SPOT,…`, unset on this installation) | every tick | none | ingestor → Redis stream `market:ticks`; one call of `on_bar` per tick of the spot symbol |
| index candles | the same index, asked for by name inside Rule 1: `NSE:NIFTYBANK-INDEX` for BANKNIFTY, `NSE:NIFTY50-INDEX` for NIFTY / NIFTY50 — and for SENSEX the bare underlying string `"SENSEX"`, which is not a symbol (see Limitations) | 15m, the newest bucket (`get_recent_bars(..., "15m", take=2)`, `bars[0]`), forming or not | none — a single bucket is enough | `GET /api/LiveData/bars?resolution=15m&take=2`: `live_bars` 1m rows aggregated on read |
| equity candles | `heavyweights` (default `NSE:HDFCBANK-EQ`, `NSE:RELIANCE-EQ`): newest 15m bucket's low plus the latest quote; an equity target: its own `NSE:<UNDERLYING>-EQ` 1m bars for the session VWAP | 15m newest bucket / 1m, up to 500 rows | none / at least one 1m row of the newest IST date with positive `volumeDelta` | the same bars endpoint and `GET /api/LiveData/latest`. The engine does not watch-list the heavyweights; the ingestor must already track them (it does: 1,168 1m rows for HDFCBANK, 1 for RELIANCE in `live_bars`) |
| option chain OI | every CE and PE of the nearest option expiry within `strike_window` strikes of the ATM (Rule 2); the ATM CE's newest tick (Rule 3); the ATM CE / PE quote for the "Premium" in the message | latest quote / newest tick | none, but Rule 2 needs one earlier evaluation to compare against | `GET /api/LiveData/latest/all` (`live_quotes_latest.OpenInterest` — 0 or null on this feed), `GET /api/LiveData/ticks?take=1` (`bidSize` / `askSize`, which the broker sends for options), `GET /api/LiveData/latest`. Not the chain poller: `option_chain_snapshots` holds 2.25 million rows with OI > 0 and the engine never reads them |
| instrument master | the nearest option expiry's chain of the underlying, once per process (`_chain_facts`) | — | — | `GET /api/Instruments/derivatives/expiries` and the chain endpoint → `strike_step_from_chain` (100 for BANKNIFTY and SENSEX, 50 for NIFTY); the runner resolves the ATM CE and PE the same way (`BaseStrategy.get_contract_requirements` default) and watch-lists them |

`get_data_requirements` is not overridden, so the runner hands the class no
bars and runs no warm-up; everything above is fetched by the class itself
over HTTP on each tick it evaluates.

## Timeframe

- **Every tick, no bar close.** `on_bar` runs on every tick of the spot
  symbol (the runner skips ticks of other symbols) and returns at once when
  `inp.atm_strike` is falsy. Rule 1 reads the *newest* 15-minute bucket, the
  one still forming; nothing waits for a close.
- **Cooldown.** After any alert, `state["last_alert_time"]` (wall clock,
  `time.time()`) silences all three rules for `cooldown_seconds` (300 s).
- **Session window:** from the moment an admin starts the alerter
  (`POST /api/alerts/start`, `AlertsSupervisor.StartAsync`; the market-open
  script does not start it) until `POST /api/alerts/stop`, the process
  exiting, or the 15:30 IST sweep: `MarketHoursService` calls
  `_alerts.StopAsync("Market closed (15:30 IST)")` after stopping the trading
  runs ("The alerter too: it watches the same closed market, and left running
  it sat 'waiting for ticks' until the next reboot"). Each ending closes the
  run row with a `RUN_STOPPED` (`CloseRunAsync` → `RecordRunStoppedAsync`,
  `by: "api"` for a stop, `"alerter"` for an exit).
- **15:30:** there is nothing to square off — an alerter run has
  `InitialCapital = 0` and never places an order.

## Entry

"Entry" here means *an alert fires*; there are no orders. Notation: $S$ is
the tick's LTP of the spot (`inp.spot_price`), $K$ the ATM strike the runner
computed from it (`round_to_step`, nearest strike), $\Delta$ the strike step
from the chain, $U$ the underlying name (`inp.underlying`). Rules are tried
in the order 1, 2, 3 on each tick; the first that fires ends the evaluation
(lines 483, 531), and Rule 2 records its strikes whether or not it fires.

### Gate

$$
\text{evaluate} \iff K \ne 0 \ \land\ t_{\text{now}} - t_{\text{last alert}} \ge \texttt{cooldown\_seconds}
$$

### Rule 1a — index bear trap (`is_index`, lines 431–461)

For $U \in \{\text{BANKNIFTY}, \text{NIFTY}, \text{NIFTY50}, \text{SENSEX}\}$,
with $H^{15}_{\text{idx}}$ the high of the newest 15m bucket the API returns
for the index symbol (`_fetch_15m_high_low`, `bars[0]`; 0 when there are
none), and for each heavyweight $h$ in `heavyweights` (in order) $L^{15}_h$
the low of its newest 15m bucket and $P_h$ its latest quote:

$$
\text{fire} \iff S > H^{15}_{\text{idx}} > 0 \ \land\ \exists h:\ P_h < L^{15}_h,\ L^{15}_h > 0 .
$$

Alert: title `"<U> BEAR TRAP"`, severity `warning`, symbol the ATM PE
(`inp.contracts["atm_pe"]`, `""` if unresolved), message
`"Logic: <h> broke Day Low. Action: Watch <K> PE. Premium: ₹<PE LTP>"`
(the first heavyweight that satisfies the test names the reason). The class
also returns a `StrategySignal` of type `"ALERT"` with reason "Rule 1: Bear
Trap divergence detected." — see *What the runner does with it* below.

### Rule 1b — equity VWAP breakout (`else` branch, lines 462–481)

For any other $U$ (only reachable with a custom `alerts.targets` entry),
with the symbol `NSE:<U>-EQ` and, over the 1m bars of the newest IST date in
the last 500 rows (`_session_vwap`),

$$
\mathrm{VWAP} = \frac{\sum_{i:\,v_i>0} \tfrac{H_i+L_i+C_i}{3}\, v_i}{\sum_{i:\,v_i>0} v_i},
\qquad
\text{fire} \iff \text{source} = \text{"vwap"} \ \land\ S > \mathrm{VWAP} > 0 ,
$$

where $v_i$ is the bar's `volumeDelta`. When no bar has volume the helper
returns the LTP with source `"ltp"` and logs once "no traded volume in the
stored 1m bars for <symbol>; comparing against its LTP instead." — and the
rule is skipped ("spot above its own LTP is never a breakout"). Alert
`"<U> BULLISH BREAKOUT"`, `info`, ATM CE, message
`"Logic: Price <S> broke above the session VWAP <VWAP>. Action: Watch <K> CE. Premium: ₹<CE LTP>"`.
There is no crossing memory: while $S > \mathrm{VWAP}$ this fires again every
`cooldown_seconds`.

### Rule 2 — open-interest shift (`_highest_oi_strikes`, lines 487–533)

Among the chain rows of the nearest expiry with $|K_i - K| \le \Delta \cdot
\texttt{strike\_window}$, take the CE strike $K^{CE}_{\max}$ and the PE strike
$K^{PE}_{\max}$ whose `openInterest` in `GET /api/LiveData/latest/all` is the
largest (strictly positive values only; a side with none is `None`). With the
previous evaluation's $K^{CE}_{\text{prev}}, K^{PE}_{\text{prev}}$ from
`state` and $\delta = \Delta \cdot \max(1, \texttt{oi\_shift\_steps})$:

$$
\text{bearish} \iff K^{CE}_{\text{prev}} - K^{CE}_{\max} \ge \delta,
\qquad
\text{bullish} \iff K^{PE}_{\max} - K^{PE}_{\text{prev}} \ge \delta \quad (\text{checked only if not bearish}).
$$

Alerts: `"<U> BEARISH OI SHIFT"` (`warning`, ATM PE, "Peak call OI moved down
from <prev> to <now>. Watch <K> PE. Premium: ₹…") and `"<U> BULLISH OI
SHIFT"` (`info`, ATM CE, "Peak put OI moved up from <prev> to <now>. Watch
<K> CE. Premium: ₹…"). Both strikes are then stored for the next tick. When
no quote in the window carries positive OI the helper logs once —

> `[LOGIC-ENGINE] the feed reports no open interest for <U> contracts; the OI rule is skipped until the ingestor stores OI.`

(lines 385–391) — and returns `None, None`, so the rule stays silent. That is
the state of this installation: `live_quotes_latest.OpenInterest` is 0 for
39 BANKNIFTY option rows and null for 5 (the FYERS stream maps
`min_oi`/`oi`/`open_interest` with a default of 0, `fyers_streamer.py`
line 553).

### Rule 3 — order-book imbalance (`_top_of_book`, lines 535–566)

With $(b, a)$ the `bidSize`, `askSize` of the newest stored tick of the ATM
CE (`None` when the tick carries no depth, logged once: "the feed carries no
bid/ask depth for <symbol>; the order-book rule is skipped for it."), and
$R = K + 100$ (a literal 100 points, not a strike step):

$$
\text{fire} \iff |S - R| < 20 \ \land\ b > 0 \ \land\ a > \texttt{ask\_bid\_ratio} \cdot b .
$$

Alert `"<U> HEAVY SELLING PRESSURE"`, `error`, ATM PE, "Top-of-book on <CE
symbol>: ask <a> > <ratio>x bid <b> near resistance <R>. Watch <K> PE.
Premium: ₹…". Because $K$ is the *nearest* strike, $|S - K| \le \Delta/2$, so
$|S - R| \ge 100 - \Delta/2$: 50 points for BANKNIFTY and SENSEX
($\Delta = 100$), 75 for NIFTY ($\Delta = 50$) — never under 20. On the three
default targets this rule cannot fire (Limitations).

### What an alert is (`_publish_alert`, lines 403–413)

One JSON message on the Redis channel `alerts:new`:

```json
{"title": "...", "message": "...", "source": "logic_engine", "underlying": "<U>",
 "severity": "info|warning|error", "symbol": "<option symbol or empty>", "simulationRunId": null}
```

`simulationRunId` is `getattr(inp, "simulation_run_id", None)` and
`StrategyInput` has no such attribute, so it is always `null` — a stored
alert would not link to the alerter's run. `AlertSubscriberService`
deserialises it, posts `🚨 ALERT: <title>!\n<message>` to Telegram when
`Telegram:BotToken` and `Telegram:ChatId` are configured, and inserts an
`alert_events` row (`Source` = `logic_engine`, `DeliveredToTelegram` = the
Telegram result).

### What the runner does with the `ALERT` signal

`execution_runner.py` prints every returned signal (`print_signals`) and
posts only `OPEN_GROUP` / `CLOSE_GROUP` to `/api/Simulator/signals`. An
`ALERT` signal is therefore never persisted: no `simulation_signals` row, no
order, no position. The Redis publish inside `on_bar` is the only output.

### The E2E test command (`_listen_for_commands`, `_execute_e2e_test`)

Each alerter process runs a daemon thread subscribed to the Redis pub/sub
channel `cmd:python_engine`. `POST /api/Alerts/test-e2e {instrument}` (admin)
publishes `{"command":"TEST_E2E_ALERT","instrument":"<X>"}` there and writes
its own `alert_events` row (`Source` = `test-e2e`, "E2E Test Triggered",
`DeliveredToTelegram` = false) whether or not any engine answers. Every running
alerter — three by default — then publishes one alert
`"E2E TEST: <X> BREAKOUT"` (`info`, `source` `logic_engine`,
`simulationRunId` `null`) with "Spot at <S>, ATM calculated at <K>. Watch
<CE symbol>. Premium: ₹<LTP>", built from the latest quote, a hard-coded
strike interval (100; 50 only for the literal `NIFTY50`; 10 for anything
else — `NIFTY` maps to `NSE:NIFTY-EQ`, which has no quote), the nearest
option expiry and the exact CE from the master. Any failure falls back to
mock values: spot 10000, `MOCK:<X>-10000CE`, premium 150.

## Position management

None. The class never returns an `OPEN_GROUP` or `CLOSE_GROUP`, `legs` are
never set, and `legs_summary` reads "No legs (Telegram alerts only)". The
run's risk rules (`parametersJson.risk` — leg, group, overall, swept every
3 s by `StrategyRiskGuardService`) have nothing to act on: the alerter's run
row is created by the supervisor with `ParametersJson =
{"underlying":"<U>","role":"alerts"}` and `InitialCapital = 0`, and the
history page shows the role instead of lots (`LiveRunParameters.ReadRole`).
The only state kept between ticks is `last_alert_time`,
`last_highest_call_oi_strike` and `last_highest_put_oi_strike`.

## Exit

None — there is never a position to close. What ends an alerter run:

1. `POST /api/alerts/stop` (admin) — `AlertsSupervisor.StopAsync` terminates
   each managed or adopted process (`ProcessTerminator`) and closes the run
   (`RUN_STOPPED`, `by: "api"`).
2. The 15:30 IST market-close sweep, which calls the same `StopAsync` with
   "Market closed (15:30 IST)" after stopping the trading runs.
3. The process exiting on its own (`MonitorExitAsync` → "Alerter process
   exited", `by: "alerter"`), for instance when Redis goes away — the three
   2026-09-08 alerter processes ended with `redis.exceptions.ConnectionError`
   and "Connection refused" (their run rows, from the old start path the
   supervisor did not own, were closed by hand).

The `cooldown_seconds` silence after an alert is the only thing resembling a
position lifetime.

## Parameters

The supervisor passes no parameters (only `underlying` and `role`), so the
defaults below are what runs; changing them means editing `default_params`.
`lots` is read by `lots_from` and never used.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `cooldown_seconds` | 300 | Seconds after any alert during which no rule is evaluated (wall clock) | Fewer, sparser alerts; a second setup inside the window is lost | More repeats — Rule 1b in particular re-fires every cooldown while price stays above VWAP |
| `ask_bid_ratio` | 3.0 | Rule 3: the ask size must exceed this multiple of the bid size at the ATM call's top of book | Rarer (the rule is unreachable on the default targets anyway) | More frequent, if it were reachable |
| `strike_window` | 5 | Rule 2: strikes either side of the ATM scanned for the peak-OI strike ($\pm 5\Delta$: ±500 points on BANKNIFTY / SENSEX, ±250 on NIFTY) | Wider scan; far strikes with large OI can dominate | Narrower; the peak may sit outside and the rule sees only the near strikes |
| `oi_shift_steps` | 1 | Rule 2: how many strikes the peak-OI strike must move (×$\Delta$) between two evaluations to count; clamped to ≥ 1 | Needs a bigger jump between consecutive ticks | 1 is the floor |
| `heavyweights` | `["NSE:HDFCBANK-EQ", "NSE:RELIANCE-EQ"]` | Rule 1a: the stocks whose newest-15m-bar low must be broken to confirm the trap; the first hit names the alert | More names → more chances of a confirmation | Fewer / none → Rule 1a can never fire |

## Worked example

There is no alert of this engine to walk through, and this section says so
with the rows that show it. Times are IST (the database stores UTC;
IST = UTC + 5:30).

### No `logic_engine` alert has ever been stored

```
select "Source", count(*) from alert_events group by 1;
  process     |   5
  strategyrun | 115
  system      |   2
  test-e2e    |   1
```

Every row of `alert_events` came from the API itself
(`RedisSystemNotifier`: run started / stopped / leg opened notices, process
notices, the two console pipeline checks) or from the test endpoint. Nothing
with `Source = 'logic_engine'`.

### The alerter runs that exist

`select "Id","StrategyName","Status","StartedUtc","ParametersJson" from
simulation_runs where "StrategyName"='LogicEngine' order by "Id" desc limit 5`:

| Id | Status | StartedUtc | ParametersJson |
|----|--------|------------|----------------|
| 78 | Stopped | 2026-09-07 19:22:59 (2026-09-08 00:52:59 IST) — `Symbol` `BSE:SENSEX-INDEX` | `{"role": "alerts"}` |
| 77 | Stopped | 2026-09-07 19:22:59 — `NSE:NIFTY50-INDEX` | `{"role": "alerts"}` |
| 76 | Stopped | 2026-09-07 19:22:59 — `NSE:NIFTYBANK-INDEX` | `{"role": "alerts"}` |
| 13 | Pending | null | `{}` |
| 12 | Pending | null | `{}` |

Runs 7–13 (2026-09-01) are rows the runner created for itself under the old
start path — the supervisor's remarks describe them: "which stayed Pending
forever: the runner's pid registration was refused (a Pending run is not
'open'), nothing ever closed the row". Runs 76–78 are the same path's rows
for the three targets, closed by hand at 2026-09-08 20:22:00 IST with
`LastError` "Alerter run from the old start path: never registered, process
ended at the 09 Sep reboot" and no `RUN_STOPPED` signal. The
supervisor-owned row (`CreateRunRowAsync`, commit b85e31a of 2026-09-09) has
not produced a run on this installation yet: no LogicEngine row after 78 and
no `alerts.pid.*` key in `system_settings`.

What those three processes saw is in their runner logs
(`logs/engine/runner-76-63058.log`, `runner-77-63069.log`,
`runner-78-63070.log`, local, gitignored; each file starts where a broken
stdout pipe made the runner switch to file-only output — its first `[STATUS]`
lines still say "waiting for ticks" — and its 4,083 `[STATUS]` lines at one
every 10 s reach back about 11.3 h from the 20:22 IST end, so it covers the
whole 2026-09-08 session):

- each printed the OI line exactly once — `[LOGIC-ENGINE] the feed reports no
  open interest for BANKNIFTY contracts; the OI rule is skipped until the
  ingestor stores OI.` (and the NIFTY, SENSEX twins) — right after its first
  ticks (`ticks=2`, spot 57088.30, ATM 57100 for BANKNIFTY);
- 74,977 (run 76), 80,888 (77) and 19,948 (78) ticks processed by the end;
- no `SIGNALS` block anywhere — `on_bar` never returned an `ALERT` — and no
  "no bid/ask depth" or "no traded volume" line either;
- all three end with `redis.exceptions.ConnectionError: Connection closed by
  server` and `Connection refused` on port 6379 at 20:22.

So Rule 2 was skipped by the OI line, Rule 3 could not reach its distance
test, and Rule 1a's double condition did not occur in 175,813 ticks. The
`alerts:new` subscriber was storing and delivering rows that afternoon
(three `strategyrun` rows, 15:17–15:30 IST on 2026-09-08), so an alert
published then would be in the table.

### The E2E test row, and what it does and does not prove

`alert_events` id 1: 2026-09-05 23:45:15 IST, `Source` `test-e2e`,
`Underlying` BANKNIFTY, `Title` "E2E Test Triggered", `Message` "API
initiated an E2E test for BANKNIFTY.", `MetadataJson`
`{"command":"TEST_E2E_ALERT","instrument":"BANKNIFTY"}`,
`DeliveredToTelegram` false. This row is written by
`AlertsController.TriggerE2ETest` itself, directly to the database, when it
publishes the command on `cmd:python_engine` — it proves the API side of the
test: the endpoint, the Redis publish and the audit row. The engine's answer
would be a second row, `Source` `logic_engine`, `Title` "E2E TEST: BANKNIFTY
BREAKOUT", stored by the subscriber; there is none, and no LogicEngine run
was open at that moment (76–78 started three days later, 7–13 never
started). The `alerts:new` → Telegram → `alert_events` leg is proven
separately: ids 2 and 3 (`Source` `system`, "Notification pipeline check",
2026-09-05 23:47:21 and 23:47:22 IST, `DeliveredToTelegram` true) travelled
the same channel and subscriber through `RedisSystemNotifier`, as did 106 of
the 115 `strategyrun` rows (9 on 2026-09-07 were not delivered). What has
never been exercised end to end is the engine's own publish.

## Limitations

- **Nothing has fired.** Every rule has a data or arithmetic reason not to on
  this installation; the engine's output path has been proven only for
  messages the API published itself.
- **Rule 2 reads OI from the wrong place.** It scans
  `live_quotes_latest.openInterest`, which the FYERS stream leaves at 0/null;
  the chain poller has stored 2.25 million rows with OI > 0 in
  `option_chain_snapshots` (2026-09-06 → 2026-09-09) that the engine never
  reads. Until the ingestor stores OI on the quote row, the rule is skipped
  with the log line quoted above. OI also cannot be backfilled — it exists
  only for the periods the poller ran.
- **Rule 3 is unreachable on every default target.** "Resistance" is the
  literal ATM + 100 with a 20-point tolerance, but the ATM is the nearest
  strike, so the spot is always 50–150 points (BANKNIFTY, SENSEX) or 75–125
  (NIFTY) away from it. The top-of-book read (`bidSize` / `askSize` on the
  option's newest tick, present for 89 of 121 option quote rows) never gets
  used.
- **Rule 1a depends on a race.** `spot > index_high` compares the tick's LTP
  with the high of the newest 15-minute bucket `LiveDataService` has written
  from the same tick stream — normally that high already includes this tick,
  so the test is true only when the runner reads a tick before the bar writer
  has folded it in (or while the writer lags). The heavyweight test is the
  same comparison the other way, between a quote row and a bar row both
  maintained from the stock's ticks. Neither happened in 175,813 ticks.
- **SENSEX can never trip Rule 1a.** For `SENSEX` the code asks for bars of
  the string `"SENSEX"` (`spot_sym = ... else index_symbol`), not
  `BSE:SENSEX-INDEX`; `live_bars` has no such symbol, `_fetch_15m_high_low`
  returns `(0, 0)`, and `index_high > 0` fails.
- **Docstring versus code.** The class says it "watches the index against its
  15-minute range"; the code uses the high of the newest 15-minute bucket
  only, forming included, never an opening range. The message says a
  heavyweight "broke Day Low"; the test is against the low of *its* newest
  15-minute bucket, not the day's. The payload's `simulationRunId` is always
  `null` (`StrategyInput` has no `simulation_run_id`), so a stored alert
  would carry no link to the run that produced it.
- **Rule 1b has no edge detection.** On an equity target it alerts every
  `cooldown_seconds` for as long as the price is above the session VWAP, and
  the VWAP itself is anchored to the ingestor's first bar of the day, not the
  09:15 open.
- **Heavyweights are not subscribed by the engine.** `NSE:RELIANCE-EQ` has
  one 1m row in `live_bars`; a 15m low from one minute of one day is what
  Rule 1a would compare against.
- **Three answers to one test.** Every running alerter subscribes to
  `cmd:python_engine`, so one `test-e2e` call yields one alert per target;
  the test resolves `NIFTY` to `NSE:NIFTY-EQ` (no quote) and a 10-point
  strike grid, so for that name it always publishes the mock payload.
- **Per-tick HTTP.** Each evaluated tick costs at least three API calls
  (15m bars, all latest quotes, the ATM CE's ticks), more when a rule gets
  as far as a premium lookup; the cooldown only applies after an alert.
- **Alerts are not signals.** No `simulation_signals`, `paper_orders` or
  `paper_positions` row is ever written for this strategy; the run row only
  records that a process ran. There is no backtest of an alerter.

## Facts (machine-readable)

```yaml
name: LogicEngine
category: Alerts
evaluates_on: tick
resolution: 15m
data: ticks, index candles, option chain OI
instruments: BANKNIFTY, NIFTY, SENSEX
default_lots: 1
built_in_exit: false
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"

/* no logic_engine source */
SELECT "Source", count(*) FROM alert_events GROUP BY 1;
SELECT "Source", "DeliveredToTelegram", count(*), min("OccurredUtc"), max("OccurredUtc")
FROM alert_events GROUP BY 1, 2 ORDER BY 1, 2;

/* the E2E row and the two console pipeline checks */
SELECT "Id", "OccurredUtc" AT TIME ZONE 'Asia/Kolkata', "Source", "Underlying", "Title", "Message",
       "MetadataJson", "DeliveredToTelegram", "SimulationRunId"
FROM alert_events WHERE "Id" IN (1, 2, 3) ORDER BY "Id";

/* alerter runs: 76-78 (old start path, closed by hand), 7-13 (Pending forever) */
SELECT "Id", "StrategyName", "Status", "Symbol", "StartedUtc", "CompletedUtc", "ParametersJson",
       "InitialCapital", "UserId", "LastError"
FROM simulation_runs WHERE "StrategyName" = 'LogicEngine' ORDER BY "Id" DESC;

/* nothing was ever signalled or traded by them */
SELECT count(*) FROM simulation_signals s JOIN simulation_runs r ON r."Id" = s."SimulationRunId"
WHERE r."StrategyName" = 'LogicEngine';

/* why Rule 2 is skipped: OI on the quote rows the engine reads ... */
SELECT "OpenInterest", count(*) FROM live_quotes_latest WHERE "Symbol" LIKE 'NSE:BANKNIFTY%' GROUP BY 1;
/* ... while the poller's snapshots do carry it */
SELECT count(*), sum(CASE WHEN "OpenInterest" > 0 THEN 1 ELSE 0 END), min("CapturedUtc"), max("CapturedUtc")
FROM option_chain_snapshots;

/* depth the engine could read for Rule 3 (option quote rows with bid/ask sizes) */
SELECT count(*), sum(CASE WHEN "BidSize" > 0 AND "AskSize" > 0 THEN 1 ELSE 0 END)
FROM live_quotes_latest WHERE "Symbol" LIKE '%CE' OR "Symbol" LIKE '%PE';

/* SENSEX has no bars under the bare name the code asks for */
SELECT "Symbol", count(*) FROM live_bars
WHERE "Symbol" IN ('SENSEX', 'BSE:SENSEX-INDEX', 'NSE:HDFCBANK-EQ', 'NSE:RELIANCE-EQ') GROUP BY 1;

/* the alerter targets setting is unset (defaults apply) and no alerter pid is stored */
SELECT "Key", "Value" FROM system_settings WHERE "Key" LIKE 'alerts%';

Runner logs cited (local, gitignored): logs/engine/runner-76-63058.log, runner-77-63069.log,
runner-78-63070.log — grep for "LOGIC-ENGINE" (one line each), "SIGNALS" (none), the last
"ticks=" value, and the Redis ConnectionError at the end.
-->
