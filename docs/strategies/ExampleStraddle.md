# ExampleStraddle

Source: `strategies/example/example_straddle.py` (`ExampleStraddleStrategy`).
This is the template strategy: its purpose, in the class's own words, is "to
show the minimum code needed to plug a strategy into the runner". It is
listed in the catalog (`category = "Example"`, `listed` left at the default
`True`) and can be launched like any other, so it gets a spec like any
other. Every rule below is read from the code; the plumbing it depends on
is in `strategies/base_strategy.py`, `strategies/contract_selector.py`,
`strategies/execution_runner.py` (live) and `backtest/engine.py` (replay).
Times are IST (UTC + 5:30); the database stores UTC.

## Idea

It is an example. The class sells the at-the-money call and put of the
nearest expiry on the first evaluation that carries both contracts and then
holds them for the rest of the run — the same trade as `ShortStraddle`,
written as a 50-line illustration of the `BaseStrategy` contract:
`initialize_state`, `on_bar`, `StrategySignal` with legs, `lots_from`.
A short straddle pays when the underlying stays near the strike and the
premiums decay; it loses on a move. Nothing in the class decides when the
bet is over, and nothing in this repository tests that it pays: no run of
this strategy has ever produced a signal of its own (see Worked example).

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTYBANK-INDEX` in every run so far, `NSE:NIFTY50-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution | none — `get_data_requirements` is not overridden, so it returns `[]` and a replay does no warm-up | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | ATM CE and ATM PE of the nearest expiry (`BaseStrategy.get_contract_requirements`, the default `atm_ce` / `atm_pe`; not overridden) | live: latest quote; replay: each contract's candle at the run's resolution | none | live: the runner puts both on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` per contract, synced from FYERS history on first use when the broker is linked (`HistoricalFeed._sync_then_read`) |

It never reads option-chain OI or any bar series.

## Timeframe

- **Bar:** none of its own. `on_bar` looks only at `state` and
  `inp.contracts`; the cadence is the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick with
  the contracts resolved for that tick's ATM strike, so the straddle is
  sold on the first tick after start-up for which both contracts exist in
  the instrument master — pre-open ticks included (the ingestor stamps
  pre-open prints; the runner's `market_is_open()` feeds only the
  feed-stall warning). No session window, no last-entry time.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`in_session`: 09:15 ≤
  start < 15:30). The entry is the first session bar of the range that has
  both contracts, filled at the close of each option's candle for that bar
  (`HistoricalFeed.option_close_at`). No replay of this strategy exists;
  `ShortStraddle` run 105 is a replay of the same logic on NIFTY 5m and is
  walked through in that spec.
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30
  on weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. A replay squares off at
  `eod_square_off_ist` (15:15 by default) and, if positions are still open
  on the last driver bar, there with reason "End of backtest". The strategy
  relies on these: it has no exit of its own.

## Entry

### Rule (`ExampleStraddleStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{atm\_ce},\ \text{atm\_pe}\} \subseteq \text{contracts}
$$

where `is_invested` is the flag in `state` (false from `initialize_state`)
and `contracts` is the map the runner or replay resolved for this tick or
bar. On entry the strategy emits one `OPEN_GROUP` with two legs — SELL the
ATM CE and SELL the ATM PE, `lots` each — a fresh `uuid4` as `group_id`
(the only metadata key it sets) and the reason
`Initial ATM Straddle entry`; then it sets `is_invested = True`.

In words: on the first evaluation that has both ATM contracts, sell one
straddle and never look again. The differences from `ShortStraddle` are
cosmetic: the reason text, no `strategy_type` in the metadata, and the
category.

### Strike, expiry, contract

- **ATM strike** $K = \mathrm{round}(S/\Delta)\cdot\Delta$ — the nearest
  strike on the grid (`contract_selector.round_to_step`). $S$ is the tick's
  LTP live, the driver bar's close in a replay. $\Delta$ is the strike step
  read from the option chain of the chosen expiry (`strike_step_from_chain`;
  100 for BANKNIFTY, 50 for NIFTY), else `FALLBACK_STRIKE_STEPS`.
- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). For BANKNIFTY the master holds
  monthly expiries only (2026-09-29 was the only unexpired one in September
  2026), so the contracts are the monthly ones.
- **Contracts:** the exact CE and PE rows of the instrument master at
  $(K, \text{expiry})$. A missing row leaves the key out and the strategy does
  not enter on that tick or bar (state untouched, so it tries again on the
  next).
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`:
  `lots`, else the legacy `quantity`, else `default_lots` = 1; never below 1)
  per leg. `paper_orders.Quantity` stores lots; the API multiplies by the
  master's lot size (BANKNIFTY 30, NIFTY 65) for P&L.
- **Fill:** live, the runner waits up to 10 s for both quotes
  (`enrich_signal_leg_prices`, `SIGNAL_PRICE_WAIT_SECONDS`), stamps
  `reason`, `spot_price`, `atm_strike` into the metadata
  (`stamp_signal_metadata`) and posts `/api/Simulator/signals`; the API
  prices both legs before it fills either and commits them in one
  transaction, so the straddle is booked whole or not at all, and an
  unpriced group is refused. Replay: the close of each option's candle at
  the signal bar.

## Position management

None — this is the point of the example. `state` is `{is_invested, group_id}`
and nothing after the entry changes it:

- no adjustment, no roll, no addition, no re-entry — after the one
  `OPEN_GROUP` the method returns `[]` on every later call;
- it does not know whether the group is still open: a leg closed by a risk
  rule, a group closed at 15:30 or a manual square-off leaves
  `is_invested = True`, so the run never enters again;
- a refused entry (no quote within 10 s → the API rejects the unpriced
  group; or a run filter such as `filters.trade_window_ist` blocking it)
  leaves the flag set too, because it is written inside `on_bar` before the
  signal is posted — the runner's own comment: "The strategy still believes
  this group is open." Such a run does nothing for the rest of the day;
- live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`), so a restart does not re-enter;
- it never sets a per-position stop or target.

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s) in the order leg → group → overall, each level in the fixed order
stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; closes that leg only, leaving the other half of
  the straddle naked.
- `group` — rupees on the group's realized + unrealized P&L; closes both
  legs together — the level that matches a straddle.
- `overall` — rupees on the run's total P&L; flattens everything and ends
  the run. The legacy keys `stop_loss` / `target` in `parametersJson` are
  read as the overall level when no `risk` object is present
  (`parse_risk_rules`); run 25 below is exactly that case. `scope` (`"day"`
  default, `"run"`) is honoured by the replay engine only.

A replay mirrors the same rules once per bar (`_check_risk`).

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, the stop button or a square-off, a sold straddle stays
open until 15:30.

## Exit

Live, `StrategyRiskGuardService.SweepAsync` checks each open position in
this order on every sweep; the first rule that fires wins:

1. The position's own `StopLossPrice` / `TargetPrice` — never set by this
   strategy.
2. Leg rules: stop-loss points, stop-loss percent, trailing stop points,
   trailing stop percent, target points, target percent (`EvaluateLeg`).
3. Group rules on the group's P&L (`EvaluateGroup`).
4. Overall rules on the run's total P&L (`EvaluateOverall`) — flattens and
   stops the run (run 25, signal 199: "Stop loss hit: P&L -627 ≤ −100").

Outside the sweep, on their own clocks: a manual square-off or the run's
stop button ("Stopped by admin", "Stopped by testtrader" — every other
started ExampleStraddle run ended this way); market close by `MarketHoursService` at 15:30 IST on weekdays.

A replay applies the same four levels per bar, then the day's square-off at
`eod_square_off_ist` (`_eod_check`), then "End of backtest" at the last
driver bar (`execute`). Every close is a reduce-only `CLOSE_GROUP` priced at
the option's candle close for that bar, falling back to the last mark, then
the entry price (`PaperLedger.close_positions`).

## Parameters

`default_params` is empty. The only knob is the run's lot count.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `lots` (run parameter; legacy spelling `quantity`) | `default_lots` = 1 | lots per leg; P&L = premium points × lots × lot size | proportionally larger premium, margin and loss; the same single signal | n/a below 1 — `lots_from` clamps to 1 |

Run 5 (`Pending`, never started) carries `{"strike_step":100,"qty_lots":2}`
in its `ParametersJson`; neither key is read by this class or by
`lots_from`, so that run would have traded 1 lot on the chain's own step.

## Worked example

There is no run in which this strategy's `on_bar` produced a signal. Eleven
LivePaper runs exist (ids 1, 2, 3, 5, 24, 25, 49–53, all
`NSE:NIFTYBANK-INDEX`); four never started (`Pending`), five (24, 49, 50,
51, 53) were stopped by hand within 40 s of starting, all outside market
hours, with no signal, and two — 25 and 52 — hold signals that were posted to
`/api/Simulator/signals` by hand as API tests. The evidence that they are
not the strategy's: their group ids (`TEST-G1`, `g-zero-test`,
`g-quote-test`, `g-atomic-test`) are not the `uuid4` `on_bar` generates,
their reasons (`Test: short straddle at 57600`, `Test: exit CE leg`) are
not `Initial ATM Straddle entry`, their metadata lacks the `spot_price` and
`atm_strike` keys `stamp_signal_metadata` adds to every signal the runner
posts, and none of the strings occurs anywhere in the repository. Run 25 is
the one that reconciles, so it is walked through here as what the platform
does *with* a short straddle once one exists; the entry itself is the API
test's, not the class's.

Run **25** — LivePaper, `NSE:NIFTYBANK-INDEX`, started Thursday 2026-09-03
at 21:49:49 IST (the market had closed six hours earlier), stopped 21:49:54.
`ParametersJson` verbatim:
`{"lots":2,"stop_loss":100,"target":null,"underlying":"BANKNIFTY"}` — the
legacy `stop_loss` key, read as `risk.overall.stopLoss = 100` rupees. Lot
size 30 from `instruments.LotSize` of `NSE:BANKNIFTY26SEP57600CE` and
`…PE` (monthly, expiry 2026-09-29 — the only unexpired BANKNIFTY expiry in
the master, so also what `valid_expiries[0]` would have chosen).

### Signal 197 — 21:49:52, OPEN_GROUP `TEST-G1` → orders 222, 223 → positions 120, 121

`MetadataJson`: `{"reason": "Test: short straddle at 57600"}` — nothing
else. Two legs, 2 lots each (the run's `lots`):

- order 222: SELL 2 `NSE:BANKNIFTY26SEP57600CE` @ **813.25** → position 120,
  SHORT, `AveragePrice 813.25`;
- order 223: SELL 2 `NSE:BANKNIFTY26SEP57600PE` @ **587.75** → position 121,
  SHORT, `AveragePrice 587.75`.

`RequestedPrice` equals `FillPrice` on both rows, so whether the poster
supplied the prices or the API resolved them from the day's last quotes
cannot be told from the database.

### Signal 198 — 21:49:53, CLOSE_GROUP `TEST-G1` "Test: exit CE leg" → order 224

One leg: BUY 2 `…57600CE` @ **840.00**. Reduce-only, so it closes position
120 and touches nothing else:

$$
(813.25 - 840.00) \times 2 \times 30 = -26.75 \times 60 = -1605.00
$$

= `RealizedPnl` of position 120 (`ClosedUtc` 21:49:53). Run P&L is now
−1,605 realized plus the PE's unrealized.

### Signal 199 — 21:49:54, CLOSE_GROUP by the risk guard → order 225

`MetadataJson`: `{"reason":"Stop loss hit: P&L -627 ≤ −100","system":true}`;
then signal 200, `RUN_STOPPED`, `{"reason":"Stop loss hit: P&L -627 ≤ −100","by":"risk-guard"}`.
The guard's next sweep saw total P&L = −1,605 + (587.75 − 571.45) × 60 =
−1,605 + 978 = −627 ≤ −100 and flattened: order 225 BUY 2 `…57600PE` @
**571.45** (the mark it had just used, so here the reason and the fill
agree exactly):

$$
(587.75 - 571.45) \times 2 \times 30 = 16.30 \times 60 = 978.00
$$

= `RealizedPnl` of position 121. Run total: $-1605.00 + 978.00 = -627.00$,
the number in the guard's reason.

Run 52 (Sunday 2026-09-06, 13:22 IST) is the other test run: three
hand-posted `OPEN_GROUP`s of which only `g-quote-test` (signal 561) filled —
order 814 SELL 1 `NSE:BANKNIFTY26AUG57600CE` @ 538.30, a contract that had
expired on 2026-08-25 — and was closed by "Stopped by admin" (signal 563,
order 815) at the same 538.30 for a `RealizedPnl` of 0. Signals 560
(`g-zero-test`) and 562 (`g-atomic-test`) have no orders: the API saves the
signal row before it prices the legs and refuses the whole group when one
leg has no usable price, which is what those two were testing.

## Limitations

- **It is an example.** The class exists to show the plumbing; `ShortStraddle`
  is the same trade under a catalog name. Nothing in the repository tests
  its profitability and no run has exercised its `on_bar`.
- **No exit and no position awareness.** One signal per run. A leg closed
  by a `leg` rule leaves the other side naked and the strategy does not
  know; after any close the run is idle, since `is_invested` stays true.
- **A refused entry idles the run.** The flag is set before the signal is
  posted; an API rejection (no quote for either contract within 10 s) or a
  run filter block means the run never trades. Do not pair with
  `filters.trade_window_ist` unless the runner starts inside the window.
- **Entry time is start-up time.** Live, the first tick — pre-open prints
  included; there is no "wait for 09:20" in the code. A replay enters on
  the 09:15 bar of the first day that has contracts, at that bar's close —
  see the `ShortStraddle` spec (run 105) for what that looks like on NIFTY,
  including the end-of-range square-off that a one-day replay produces
  instead of an exit rule.
- **The worked example is a hand-posted test, not a strategy signal.** It
  shows the API's fill, reduce-only close and overall stop-loss arithmetic
  on a straddle; it does not show the entry timing, strike choice or quote
  wait described under Entry. The prices are after-hours quotes of unknown
  origin and the closes came 1 s and 2 s after the open.
- **Run 52 traded an expired contract** (`NSE:BANKNIFTY26AUG57600CE`,
  expiry 2026-08-25, `LotSize` null in the master, traded on 2026-09-06).
  The API accepted it because a hand-posted signal names its own symbol;
  the runner would not have resolved it (`valid_expiries` keeps only
  expiries on or after today).
- **Replay fills are candle closes, no slippage, `charges_per_lot` 0**;
  live fills are the latest quote after up to 10 s of wait.
- **Expired contracts have no broker history**: a replay of an older day
  whose contracts have expired skips the entry.
- **`scope` of the overall rule is a replay-only concept**; the live guard
  measures the run cumulatively (run 25: −627 on the run's total).
- **OI is not consulted** and is unavailable to a replay anyway.
- Docstring and code agree ("sells an At-The-Money (ATM) Straddle once").

## Facts (machine-readable)

```yaml
name: ExampleStraddle
category: Example
evaluates_on: tick
resolution: any
data: ticks, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: false
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"

/* every ExampleStraddle run: 1, 2, 3, 5 Pending; 24, 49, 50, 51, 53 stopped by hand with no signal; 25 and 52 API tests */
SELECT "Id", "Mode", "Symbol", "Status", "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist,
       "CompletedUtc" AT TIME ZONE 'Asia/Kolkata' AS completed_ist, "ParametersJson"
FROM simulation_runs WHERE "StrategyName" = 'ExampleStraddle' ORDER BY "Id";

/* run 25: signals 197 (OPEN_GROUP TEST-G1), 198 (CLOSE_GROUP, CE leg), 199 (CLOSE_GROUP by risk guard), 200 (RUN_STOPPED) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 25 ORDER BY "Id";

/* fills 222, 223 (SELL 2 @ 813.25 / 587.75), 224 (BUY 2 @ 840.00), 225 (BUY 2 @ 571.45); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "RequestedPrice", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 25 ORDER BY "Id";

/* positions 120 (-1605.00), 121 (978.00) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 25 ORDER BY "Id";

/* lot sizes and expiries */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments
WHERE "Symbol" IN ('NSE:BANKNIFTY26SEP57600CE', 'NSE:BANKNIFTY26SEP57600PE', 'NSE:BANKNIFTY26AUG57600CE');
SELECT "ExpiryDate", count(*) FROM instruments WHERE "Underlying" = 'BANKNIFTY' AND "OptionType" IN ('CE', 'PE')
  AND "ExpiryDate" BETWEEN '2026-08-20' AND '2026-10-10' GROUP BY 1 ORDER BY 1;

/* run 52 */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 52 ORDER BY "Id";
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice" FROM paper_orders WHERE "SimulationRunId" = 52 ORDER BY "Id";
SELECT "Id", "Symbol", "Direction", "AveragePrice", "RealizedPnl" FROM paper_positions WHERE "SimulationRunId" = 52;
-->
