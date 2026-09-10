# ShortStraddle

Source: `strategies/neutral/straddle_strategy.py` (`StraddleStrategy`).
Every rule below is read from the code; the strike and contract plumbing it
depends on is in `strategies/base_strategy.py`, `strategies/contract_selector.py`,
`strategies/execution_runner.py` (live) and `backtest/engine.py` (replay).
Times are IST (UTC + 5:30); the database stores UTC.

## Idea

Sell the at-the-money call and the at-the-money put of the nearest expiry,
once, and hold both. The position collects two premiums and pays off when the
underlying finishes the session near the strike it was sold at: both options
decay towards their intrinsic value and the short side keeps the difference.
It loses when the underlying moves further than the premium collected in
either direction. The strategy encodes only the entry — nothing in the class
decides when the bet is over; that is left to the run's risk rules and the
platform's square-offs. Nothing in this repository tests that the bet pays
beyond the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution (5m in run 105) | none — `get_data_requirements` is not overridden, so it returns `[]` and the replay does no warm-up (`BacktestSession.warms_up` is false) | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | ATM CE and ATM PE of the nearest expiry (`BaseStrategy.get_contract_requirements`, the default `atm_ce` / `atm_pe`) | live: latest quote; replay: the contract's candle at the run's resolution | none | live: the runner puts both on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` for the contract, synced from FYERS history on first use when the broker is linked (`HistoricalFeed._sync_then_read`) |

It never reads option-chain OI or any bar series.

## Timeframe

- **Bar:** none of its own. `on_bar` looks only at `state` and `inp.contracts`,
  so the "bar" is whatever the caller's cadence is.
- **Live: every tick.** The runner calls `on_bar` on every spot tick
  (`execution_runner.py`, the `listen_for_ticks` loop) with the contracts
  resolved for that tick's ATM strike. The entry therefore fires on the
  first tick after start-up for which both contracts exist in the instrument
  master — whatever the clock says. There is no session window: the runner's
  `market_is_open()` feeds only the feed-stall warning, not the signal path,
  and the ingestor stamps pre-open prints (08:44 on 2026-09-09 in `live_bars`),
  so a runner started before 09:15 sells the straddle on a pre-open tick.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`). Driver bars are session bars only
  (`HistoricalFeed.driver_bars`, `in_session`: 09:15 ≤ start < 15:30), so the
  entry is the first session bar of the range that has both contracts —
  09:15 in run 105 — and the fill is the close of each option's candle for
  that bar (`HistoricalFeed.option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30 on
  weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. In a replay the engine
  squares off at `eod_square_off_ist` (15:15 by default,
  `run_spec.DEFAULT_EOD_SQUARE_OFF_IST`) and, if positions are still open on
  the last driver bar of the range, at that bar with reason "End of
  backtest". The strategy relies on all of these: it has no exit of its own.

## Entry

### Rule (`StraddleStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{atm\_ce},\ \text{atm\_pe}\} \subseteq \text{contracts}
$$

where `is_invested` is the flag in `state` (false from `initialize_state`)
and `contracts` is the map the runner or replay resolved for this tick or
bar. On entry the strategy emits one `OPEN_GROUP` signal with two legs —
SELL the ATM CE and SELL the ATM PE, `lots` each — a fresh `uuid4` as
`group_id`, metadata `strategy_type: "Neutral"`, and the reason
`Opening Short Straddle at strike <K>`; then it sets `is_invested = True`.

In words: on the first evaluation that has both ATM contracts, sell one
straddle and never look again.

### Strike, expiry, contract

- **ATM strike** $K = \mathrm{round}(S/\Delta)\cdot\Delta$ — the nearest
  strike on the grid (`contract_selector.round_to_step`; Python `round`, so
  an exact half goes to the even multiple). $S$ is the tick's LTP live, the
  driver bar's close in a replay (`spot_price=bar.close` in `_build_input`).
  $\Delta$ is the strike step read from the option chain of the chosen
  expiry (`strike_step_from_chain`: the smallest gap between consecutive
  strikes, 50 for NIFTY), else `FALLBACK_STRIKE_STEPS`.
- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). On an expiry day the same-day
  contract is traded.
- **Contracts:** the exact CE and PE rows of the instrument master at
  $(K, \text{expiry})$ (`contracts_for_requirements` → `ExactContractCache` live,
  `ContractResolver.contracts_for` in a replay). A missing row leaves the key
  out, the condition above is false and the strategy simply does not enter
  on that tick or bar; the state is untouched, so it tries again on the next.
  The replay lists each missing (key, strike) once under `skippedEntries`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`:
  `lots`, else the legacy `quantity`, else `default_lots` = 1; never below 1)
  is each leg's `quantity`. `paper_orders.Quantity` stores lots; the API
  multiplies by the master's lot size (NIFTY 65) for P&L.
- **Fill:** live, the runner waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s,
  `core.leg_pricing.DEFAULT_WAIT_SECONDS`) for both quotes
  (`enrich_signal_leg_prices`), stamps `reason`, `spot_price`, `atm_strike`
  into the metadata (`stamp_signal_metadata`) and posts
  `/api/Simulator/signals`; the API prices every leg before it fills any and
  commits all legs in one transaction (`PaperTradingService`), so a straddle
  is booked whole or not at all. Replay: the close of each option's candle
  at the signal bar; an entry with no premium history for either contract is
  skipped and listed.

## Position management

None. `state` is `{is_invested, group_id}` and nothing after the entry
changes it:

- no adjustment, no roll, no addition, no re-entry — after the one
  `OPEN_GROUP` the method returns `[]` on every later call;
- it does not know whether the group is still open. A leg closed by a risk
  rule, a group closed at 15:30, or a manual square-off leaves
  `is_invested = True`, so the run never enters again;
- an entry that is refused — no quote within 10 s, so the API rejects the
  unpriced group ("No price is available … rejected rather than filled at
  zero"), or a run-level signal filter (`parametersJson.filters`, e.g.
  `trade_window_ist`) blocking it — leaves the flag set too, because it is
  written inside `on_bar` before the signal leaves the strategy. The runner's
  own comment: "The strategy still believes this group is open." Such a run
  holds nothing and does nothing for the rest of the day;
- live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`), so a restart does not re-enter either;
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on positions 1120 and 1121 of run 105).

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s, `appsettings.json`) in the order leg → group → overall, each level in
the fixed order stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; a trip closes that leg only, leaving the other
  half of the straddle naked.
- `group` — rupees on the signal group's realized + unrealized P&L; closes
  both legs together. This is the level that matches the position: the
  straddle is one group.
- `overall` — rupees on the run's total P&L; a trip flattens everything and
  ends the run. `scope` (`"day"` by default, or `"run"`) is honoured by the
  replay engine (`_check_overall` measures from the day's opening P&L under
  `day`); the live guard never reads it.

The replay mirrors the same rules once per bar (`_check_risk`, run before
the strategy sees the bar and again after the marks). Run 105 set none of
them (`"risk":{}`), so its positions rode from 09:15 to the end of the range
through a −165.75 low at 12:55 with nothing watching.

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
   stops the run.

Two more closers run on their own clocks, outside the sweep:

- a manual square-off or the run's stop button ("Stopped by admin" /
  "Squared off by admin");
- market close: `MarketHoursService` at 15:30 IST, weekdays.

A replay applies the same four levels per bar (`_apply_leg_rules`,
`_apply_group_rules`, `_check_overall`), then the day's square-off at
`eod_square_off_ist` (`_eod_check`, first bar at or after it), then "End of
backtest" at the last driver bar (`execute`). Every close is a reduce-only
`CLOSE_GROUP` priced at the option's candle close for that bar, falling back
to the last mark, then the entry price (`PaperLedger.close_positions`).

## Parameters

`default_params` is empty. The only knob is the run's lot count.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `lots` (run parameter; legacy spelling `quantity`) | `default_lots` = 1 | lots per leg; P&L = premium points × lots × lot size | proportionally larger premium, margin and loss; the same single signal | n/a below 1 — `lots_from` clamps to 1 |

The strike (ATM), the expiry (nearest) and the sides (both SELL) are fixed
in code; there is no parameter for any of them.

## Worked example

Run **105** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5`, range
2026-09-09 (`FromUtc` 2026-09-08 18:30:00 → `ToUtc` 2026-09-09 18:29:59, i.e.
00:00–23:59:59 IST on Wednesday 2026-09-09), replayed on 2026-09-11 at
00:06:29 IST in two seconds. `ParametersJson` verbatim:
`{"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
The summary row (signal 1485) says 63 driver bars, 1 session, 2 trades,
`realizedPnl 1488.5`, `charges 0`, `eodSquareOffs 0`, no skipped entries.
Lot size 65 is the instrument master's value for `NSE:NIFTY2691523500CE`
(`instruments.LotSize`). Symbols read NIFTY + `26` (year) + `9` (month) +
`15` (day) + strike + CE/PE, so every contract below expires on Tuesday
2026-09-15 — the first expiry in the master on or after 2026-09-09
(`expiry_for`; the 2026-09-08 weekly had expired the day before).

### Signal 1483 — 09:15, OPEN_GROUP → orders 2218, 2219 → positions 1120, 1121

`MetadataJson`: `reason "Opening Short Straddle at strike 23500.0"`,
`spot_price 23500.9`, `atm_strike 23500`, `strategy_type "Neutral"`,
`group_id 5a4fdf3c-0a91-42c0-8cda-0acfd27ea28f`. The first driver bar is the
09:15 index candle (O 23526.05, H 23531.80, L 23478.95, **C 23500.90**);
$23500.9 / 50 = 470.018 \to 470 \times 50 = 23500$. Both contracts exist for
2026-09-15, so the condition holds on the very first bar.

Fills — the close of each contract's 09:15 candle:

- order 2218: SELL 1 lot `NSE:NIFTY2691523500CE` @ **160.55** (candle
  O 158.55, H 163.40, L 157.85, C 160.55) → position 1120, SHORT,
  `AveragePrice 160.55`;
- order 2219: SELL 1 lot `NSE:NIFTY2691523500PE` @ **108.55** (candle
  O 112.20, H 112.50, L 105.45, C 108.55) → position 1121, SHORT,
  `AveragePrice 108.55`.

Premium collected: $160.55 + 108.55 = 269.10$ points = ₹17,491.50 for one
lot. Held to expiry that would make the textbook break-evens
$23500 \pm 269.10$; the strategy never gets there — see the close below.

### The day in between

No rule was configured, so the only record of the ride is the equity
snapshot per bar (`simulation_equity_snapshots`, run 105): total P&L −48.75
at 09:20, a low of **−165.75 at 12:55** (the index closed 23,474.95 at
12:05 and 23,566.00 at 12:55), +718.25 at 13:15 and +1,488.50 on the last bar.
A `group.stopLoss` of, say, 150 would have closed the straddle at 12:55.

### Signal 1484 — 14:50, CLOSE_GROUP "End of backtest" → orders 2220, 2221

`MetadataJson`: `square_off true`, `reason "End of backtest"`,
`spot_price 23489.55`, `atm_strike null` (the end-of-range square-off passes
no ATM, `execute` → `_square_off(last_t, "End of backtest", last_bar.close, None)`).
The engine's `eod_square_off_ist` of 15:15 never fired: the stored 5m index
candles for 2026-09-09 stop at 14:50 (see Limitations), so 14:50 was the
last driver bar and `execute` flattened the open group there. Fills are the
14:50 candle closes:

- order 2220: BUY 1 `…23500CE` @ **151.45** (candle C 151.45);
- order 2221: BUY 1 `…23500PE` @ **94.75** (candle C 94.75).

### P&L

Short leg: (entry − exit) × lots × lot size.

$$
\text{CE: } (160.55 - 151.45) \times 1 \times 65 = 9.10 \times 65 = 591.50
$$

$$
\text{PE: } (108.55 - 94.75) \times 1 \times 65 = 13.80 \times 65 = 897.00
$$

= `RealizedPnl` of positions 1120 (591.50) and 1121 (897.00); their sum,
₹1,488.50, is the summary's `realizedPnl`. `LastMarkPrice` on both rows is
the exit fill. No charges (`charges_per_lot 0`), no slippage.

## Limitations

- **No exit and no position awareness.** One signal per run, ever. A leg
  closed by a `leg` rule leaves the other side naked and the strategy does
  not know; a group closed at 12:55 by a `group` rule would have left the
  run idle for the rest of the day, since `is_invested` stays true.
- **A refused entry idles the run.** The flag is set before the signal is
  posted. If the API rejects the unpriced group (no quote for either
  contract within 10 s — typical seconds after a fresh subscription) or a
  run filter blocks it, the run never tries again. Do not pair this
  strategy with `filters.trade_window_ist` unless the runner is started
  inside the window.
- **Entry time is start-up time.** Live, the straddle is sold on the first
  tick, pre-open prints included; there is no "wait for 09:20" in the code.
  A replay always enters on the 09:15 bar of the first day that has
  contracts, at that bar's close — on 2026-09-09 the widest 5m candle of the
  session (52.85 index points, high to low).
- **The one-day replay ends at the range end, not by any rule.** "End of
  backtest" (signal 1484) is the engine flattening what is still open on the
  last driver bar; it is not an exit of the strategy and would not exist on
  a longer range (the day would end at `eod_square_off_ist` instead). Its
  P&L is a snapshot of where the premium stood at that bar.
- **The 2026-09-09 candles are the ingestor's, not FYERS's.** The `candles`
  rows for `NSE:NIFTY50-INDEX` on that day carry `SourceKey = live`, run from
  08:40 to 14:50 IST with no 13:20–13:40 bars, and give 63 session bars
  instead of 75 (the FYERS-backfilled days hold 75 bars, 09:15–15:25;
  2026-09-08 is `live` too, 08:45–15:30). The
  ATM contracts' candles have the same origin and the same holes; the
  strategy never saw a bar at or after 15:15, which is why the EOD square-off
  did not fire. A replay on a FYERS-backfilled day closes at 15:15.
- **Replay fills are candle closes.** The 09:15 candle of the CE ranged
  157.85–163.40 and the PE 105.45–112.50; a live run at 09:15:xx could have
  been filled anywhere in there and later than the bar's open by up to
  10 s of quote wait. No slippage model, `charges_per_lot` 0 in run 105.
- **Expired contracts have no broker history** (summary `dataNotes`): a
  replay of an older day whose weekly has expired skips the entry with
  "no premium history" unless the candles were stored while the contract
  was alive.
- **Expiry-day contracts.** The nearest expiry on or after today is used,
  so on a Tuesday the straddle sold is the one expiring that afternoon.
- **`scope` of the overall rule is a replay-only concept**; the live guard
  measures the run cumulatively, which coincides with `day` only because a
  live run is one session.
- **OI is not consulted** and is not available to a replay anyway (chain
  snapshots exist only while the poller ran).
- Docstring and code agree ("Sells an At-The-Money (ATM) Call and an ATM
  Put simultaneously", "only want to enter once"); the "Profits if the
  underlying stays near the strike price" clause is the thesis, not a rule
  the code checks.

## Facts (machine-readable)

```yaml
name: ShortStraddle
category: Neutral
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

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status", "FromUtc", "ToUtc",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" = 105;

/* signals 1483 (OPEN_GROUP), 1484 (CLOSE_GROUP "End of backtest"), 1485 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 105 ORDER BY "Id";

/* fills 2218, 2219 (SELL @ 160.55 / 108.55), 2220, 2221 (BUY @ 151.45 / 94.75); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 105 ORDER BY "Id";

/* positions 1120 (591.50), 1121 (897.00) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 105 ORDER BY "Id";

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523500PE');

/* the driver bars: 63 session bars, 09:15-13:15 and 13:45-14:50, SourceKey live */
SELECT "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1;

/* the option candles behind the fills (09:15 and 14:50 IST) */
SELECT "Symbol", "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close", "SourceKey"
FROM candles WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523500PE') AND "Resolution" = '5'
  AND "TimeStampUtc" IN ('2026-09-09 03:45:00+00', '2026-09-09 09:20:00+00') ORDER BY 1, 2;

/* the intraday P&L path */
SELECT "SnapshotUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "RealizedPnl", "UnrealizedPnl", "TotalPnl", "OpenPositions"
FROM simulation_equity_snapshots WHERE "SimulationRunId" = 105 ORDER BY "SnapshotUtc", "Id";
-->
