# BullCallSpread

Source: `strategies/bullish/bull_call_spread_strategy.py`
(`BullCallSpreadStrategy`). Every rule below is read from the code; the
strike arithmetic it depends on is in `strategies/contract_selector.py`
(`strike_for_requirement`, `requirement_distance`), the plumbing in
`strategies/execution_runner.py` (live) and `backtest/engine.py` (replay).
Where the docstring and the code disagree, the code is documented and the
difference listed under Limitations. Times are IST (UTC + 5:30); the
database stores UTC.

## Idea

Buy the at-the-money call and sell a call `otm_offset_steps` strikes above
it, once, and hold the pair. The short call pays for part of the long one,
so the position costs less than a naked call and gains as the underlying
rises towards the short strike; above it the two calls move together and
the gain is capped at the strike distance minus the debit paid. It loses
the debit — at most — if the underlying stays put or falls. The class
encodes only the entry: it has no view on *when* the rise should happen
and no exit, so the run's risk rules and the platform's square-offs decide
when the bet is over. Nothing in this repository tests that the bet pays
beyond the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution (5m in run 107) | none — `get_data_requirements` is not overridden, so it returns `[]` and the replay does no warm-up | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | ATM CE (`atm_ce`) and the CE `otm_offset_steps` strikes above it (`otm_ce`) of the nearest expiry — `get_contract_requirements` | live: latest quote; replay: each contract's candle at the run's resolution | none | live: the runner puts both on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` per contract, synced from FYERS history on first use when the broker is linked (run 107 synced `NSE:NIFTY2691523600CE`, `syncedSymbols`) |

It never reads option-chain OI or any bar series. Nothing bullish is
measured: the direction is in the name, not in a rule.

## Timeframe

- **Bar:** none of its own. `on_bar` looks only at `state` and
  `inp.contracts`; the cadence is the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick with
  the two contracts resolved for that tick's ATM strike; the spread is
  opened on the first tick after start-up for which both exist in the
  instrument master — pre-open ticks included (the ingestor stamps pre-open
  prints; the runner's `market_is_open()` feeds only the feed-stall
  warning). No session window, no last-entry time.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`in_session`: 09:15 ≤
  start < 15:30). The entry is the first session bar of the range that has
  both contracts — 09:15 in run 107 — filled at the close of each option's
  candle for that bar (`HistoricalFeed.option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30
  on weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. A replay squares off at
  `eod_square_off_ist` (15:15 by default) and, if positions are still open
  on the last driver bar, there with reason "End of backtest". The strategy
  relies on these: it has no exit of its own.

## Entry

### Rule (`BullCallSpreadStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{atm\_ce},\ \text{otm\_ce}\} \subseteq \text{contracts}
$$

On entry the strategy emits one `OPEN_GROUP` with two legs, `lots` each:
BUY the ATM CE (`long_ce`) and SELL the OTM CE (`short_ce`) — a fresh
`uuid4` as `group_id`, metadata `strategy_type: "Bullish"` and the reason
`Opening Bull Call Spread. Buy CE:<K>, Sell CE:<K_C>`; then it sets
`is_invested = True`.

In words: on the first evaluation that has both calls, buy the spread and
never look again.

### Strikes (`get_contract_requirements` → `strike_for_requirement`)

With $S$ the spot (tick LTP live, driver-bar close in a replay), $\Delta$
the strike step from the option chain (50 for NIFTY, 100 for BANKNIFTY) and
$n$ = `otm_offset_steps`:

$$
K = \mathrm{round}(S/\Delta)\cdot\Delta,\qquad
K_C = \mathrm{round}\!\left(\frac{K + n\Delta}{\Delta}\right)\Delta .
$$

The long leg is declared as `ContractRequirement(key="atm_ce",
option_type="CE")` (moneyness `atm`, distance ignored); the short leg as
`ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm",
steps=2, param="otm_offset_steps")`. `requirement_distance` turns it into
points: the run parameter named by `param` wins when it is a number greater
than 0 (a name not ending in `_points` counts strikes, × $\Delta$);
otherwise `steps` (2) × $\Delta$. For an OTM CE `strike_for_requirement`
adds the distance and snaps back to the grid with `round_to_step`. On NIFTY
the default short strike is $K + 100$ (`describe_requirement`: `otm_ce: OTM
CE +2 strikes (+100 pts) [param otm_offset_steps]`); on BANKNIFTY $K + 200$.

- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). On an expiry day the same-day
  contracts are traded.
- **Contracts:** the exact CE rows of the instrument master at $K$ and
  $K_C$ for that expiry. A missing row leaves its key out and the strategy
  does not enter on that tick or bar (state untouched, so it tries again on
  the next); the replay lists the gap once under `skippedEntries`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  default `default_lots` = 1, never below 1) on both legs, so the spread is
  always 1:1; `paper_orders.Quantity` is lots, the API multiplies by the
  master's lot size (NIFTY 65).
- **Fill:** live, up to 10 s of quote wait for both legs
  (`enrich_signal_leg_prices`, `SIGNAL_PRICE_WAIT_SECONDS`), then
  `/api/Simulator/signals`; the API prices both legs before it fills either
  and commits them in one transaction, so the spread is booked whole or not
  at all, and an unpriced group is refused. Replay: the close of each
  option's candle at the signal bar; no premium history for either leg skips
  the entry.

## Position management

None. `state` is `{is_invested, group_id}` and nothing after the entry
changes it:

- no adjustment, no roll, no addition, no re-entry — after the one
  `OPEN_GROUP` the method returns `[]` on every later call;
- it does not know whether the group is still open: a leg closed by a risk
  rule, a group closed at 15:30 or a manual square-off leaves
  `is_invested = True`, so the run never enters again;
- a refused entry (no quote for either call within 10 s → the API rejects
  the unpriced group; or a run filter such as `filters.trade_window_ist`
  blocking it) leaves the flag set too, because it is written inside
  `on_bar` before the signal is posted — the runner's own comment: "The
  strategy still believes this group is open." Such a run does nothing for
  the rest of the day;
- live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`), so a restart does not re-enter;
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on positions 1124 and 1125 of run 107).

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s) in the order leg → group → overall, each level in the fixed order
stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; closes that leg only. On a spread the two legs
  move together, so a leg rule is a rule on one half of the position: a
  rising market that profits the long call is a loss on the short call, and
  a leg stop-loss would close the short call there and leave a naked long
  call — the opposite of what the spread was for. The API books the two
  legs as two `paper_positions`.
- `group` — rupees on the group's realized + unrealized P&L across both
  legs; closes them together. The spread is one group, so this is the level
  that matches the position.
- `overall` — rupees on the run's total P&L; flattens everything and ends
  the run. `scope` (`"day"` default, `"run"`) is honoured by the replay
  engine only.

The replay mirrors the same rules once per bar (`_check_risk`). Run 107 set
none (`"risk":{}`); its positions rode from 09:15 to the end of the range
through a +611.00 high at 12:55 and a −188.50 low at 12:05 with nothing
watching — a `group.target` of 500 would have closed the spread at 12:35
(the first bar at or over +500, +552.50), well above where the range ended.

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, the stop button or a square-off, the spread stays
open until 15:30.

## Exit

Live, `StrategyRiskGuardService.SweepAsync` checks each open position in
this order on every sweep; the first rule that fires wins:

1. The position's own `StopLossPrice` / `TargetPrice` — never set by this
   strategy.
2. Leg rules: stop-loss points, stop-loss percent, trailing stop points,
   trailing stop percent, target points, target percent (`EvaluateLeg`) —
   on each leg separately.
3. Group rules on the group's P&L (`EvaluateGroup`).
4. Overall rules on the run's total P&L (`EvaluateOverall`) — flattens and
   stops the run.

Outside the sweep, on their own clocks: a manual square-off or the run's
stop button; market close by `MarketHoursService` at 15:30 IST on weekdays.

A replay applies the same four levels per bar, then the day's square-off at
`eod_square_off_ist` (`_eod_check`), then "End of backtest" at the last
driver bar (`execute`). Every close is a reduce-only `CLOSE_GROUP` priced at
each option's candle close for that bar, falling back to the last mark,
then the entry price (`PaperLedger.close_positions`).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `otm_offset_steps` | 2 | strikes between the long ATM call and the short call, on the underlying's own grid (× step = points: 100 on NIFTY, 200 on BANKNIFTY) | short call further out: less premium sold, larger debit, larger maximum gain and maximum loss, the spread behaves more like a naked call | short call closer in: more premium sold, smaller debit, smaller maximum gain; 0, a negative or a non-numeric value is ignored by `requirement_distance` and the built-in 2 applies, so the two legs cannot share a strike; a fraction is snapped to the grid by `round_to_step` |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | lots on both legs; P&L = premium points × lots × lot size | proportionally larger debit and gain, same single signal | n/a below 1 — `lots_from` clamps to 1 |

The long leg is always the ATM call and the short leg always above it;
neither is a parameter.

## Worked example

Run **107** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5`, range
2026-09-09 (`FromUtc` 2026-09-08 18:30:00 → `ToUtc` 2026-09-09 18:29:59 =
00:00–23:59:59 IST on Wednesday 2026-09-09), replayed on 2026-09-11 at
00:07:28 IST. `ParametersJson` verbatim:
`{"otm_offset_steps":2,"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Summary (signal 1491): 63 driver bars, 1 session, 2 trades,
`realizedPnl 87.75`, `charges 0`, `eodSquareOffs 0`, no skipped entries,
`syncedSymbols ["NSE:NIFTY2691523600CE"]` — the short call's candles were
fetched from FYERS history during the replay; the ATM call's were already
stored. Lot size 65 from `instruments.LotSize` of both symbols; both expire
Tuesday 2026-09-15, the first expiry in the master on or after the replayed
day.

### Signal 1489 — 09:15, OPEN_GROUP → orders 2226, 2227 → positions 1124, 1125

`MetadataJson`: `reason "Opening Bull Call Spread. Buy CE:23500.0, Sell
CE:23600.0"`, `spot_price 23500.9`, `atm_strike 23500`,
`strategy_type "Bullish"`, `group_id 83314b40-39e9-4320-9097-f994a80077d4`.
The 09:15 index candle closed at **23500.90**: $23500.9/50 = 470.018 \to
23500$; with $n = 2$ and $\Delta = 50$, $K_C = 23600$.

Fills — the close of each contract's 09:15 candle:

- order 2226: BUY 1 lot `NSE:NIFTY2691523500CE` @ **160.55** (candle
  O 158.55, H 163.40, L 157.85, C 160.55) → position 1124, LONG,
  `AveragePrice 160.55`;
- order 2227: SELL 1 lot `NSE:NIFTY2691523600CE` @ **108.80** (candle
  O 154.85, H 165.00, L 101.15, C 108.80) → position 1125, SHORT,
  `AveragePrice 108.80`.

Net debit: $160.55 - 108.80 = 51.75$ points = ₹3,363.75 for one lot, the
most the spread can lose at expiry; the most it can make is
$(100 - 51.75) \times 65 = 48.25 \times 65 = 3136.25$ = ₹3,136.25, above 23,600. The
strategy never gets to expiry — see the close below.

### The day in between

No rule was configured. The equity snapshots (`simulation_equity_snapshots`,
run 107) put the spread at a low of **−188.50 at 12:05** (index close
23,474.95, 25 points under the long strike) and a high of **+611.00 at
12:55** (index close 23,566.00); by the last bar
the index had come back to 23,489.55 and the mark to +87.75.

### Signal 1490 — 14:50, CLOSE_GROUP "End of backtest" → orders 2228, 2229

`MetadataJson`: `square_off true`, `reason "End of backtest"`,
`spot_price 23489.55`, `atm_strike null`. The 15:15 EOD square-off never
fired because the stored index candles for the day end at 14:50 (see
Limitations); `execute` flattened the group on that last bar. Fills are the
14:50 candle closes:

- order 2228: SELL 1 `…23500CE` @ **151.45** (candle C 151.45);
- order 2229: BUY 1 `…23600CE` @ **98.35** (candle C 98.35).

### P&L

Long leg: (exit − entry) × lots × lot size; short leg: (entry − exit) ×
lots × lot size.

$$
\text{23500 CE (long): } (151.45 - 160.55) \times 1 \times 65 = -9.10 \times 65 = -591.50
$$

$$
\text{23600 CE (short): } (108.80 - 98.35) \times 1 \times 65 = 10.45 \times 65 = 679.25
$$

= `RealizedPnl` of positions 1124 (−591.50) and 1125 (679.25); the sum
₹87.75 is the summary's `realizedPnl`. The index closed the range 11.35
points *below* where the spread was bought, and the spread still made a
little: the short call decayed 10.45 points against the long call's 9.10.
No charges, no slippage.

## Limitations

- **Nothing bullish is checked.** The code has no trend, momentum or level
  test; it buys the spread on the first tick regardless of what the market
  is doing. "Used when expecting a moderate rise" in the docstring is
  advice to the operator, not a rule.
- **Docstring versus code.** The docstring says "Buys an At-The-Money (ATM)
  or In-The-Money (ITM) Call"; `get_contract_requirements` declares only the
  ATM call (`moneyness` `atm`) and there is no parameter to make it ITM.
- **No exit and no position awareness.** One signal per run. A leg closed
  by a `leg` rule leaves a naked long or short call and the strategy does
  not know; after any close the run is idle, since `is_invested` stays true.
- **A refused entry idles the run.** The flag is set before the signal is
  posted; an API rejection (no quote for either call within 10 s) or a run
  filter block means the run never trades. Do not pair with
  `filters.trade_window_ist` unless the runner starts inside the window.
- **Entry time is start-up time.** Live, the first tick — pre-open prints
  included. A replay enters on the 09:15 bar of the first day that has both
  contracts, at that bar's close.
- **The 09:15 fill of the short call is the close of a wide candle.** The
  23600 CE ranged 101.15–165.00 in its first five minutes and opened at
  154.85, only 3.70 below the ATM call's open in the same minute — not a
  plausible price for a strike 100 points higher (the gap at the close was
  51.75), so the early prints are pre-open or illiquid. The replay took the
  close; a live run filled in those minutes could have got anything in the
  range.
- **The one-day replay ends at the range end, not by any rule.** "End of
  backtest" (signal 1490) is the engine flattening on the last driver bar;
  it is not an exit of the strategy and would not exist on a longer range,
  where the day ends at `eod_square_off_ist`.
- **The 2026-09-09 index candles are the ingestor's.** `candles` rows for
  `NSE:NIFTY50-INDEX` on that day are `SourceKey = live`, 08:40–14:50 with no
  13:20–13:40 bars: 63 session bars, not 75, and no bar at or after 15:15,
  so the EOD square-off could not fire. The ATM call's candles have the same
  origin; the short call's are FYERS history (77 bars, 09:15–15:35).
- **Replay fills are candle closes; no slippage; `charges_per_lot` 0.**
- **Expired contracts have no broker history** (summary `dataNotes`): an
  older day whose weekly has expired cannot be replayed unless the candles
  were stored while the contracts were alive.
- **Expiry-day contracts.** The nearest expiry on or after today is used,
  so on a Tuesday the spread expires that afternoon.
- **The offset is in strikes, so the width in points depends on the
  grid**: 100 on NIFTY, 200 on BANKNIFTY, 50 on MIDCPNIFTY for the same
  `otm_offset_steps = 2`.
- **`scope` of the overall rule is a replay-only concept**; the live guard
  measures the run cumulatively.
- **OI is not consulted** and is unavailable to a replay anyway.
- A LivePaper run of this strategy exists (run 36, `NSE:NIFTYBANK-INDEX`,
  2026-09-04 11:25–12:15 IST) and produced no signal, no order and no
  position before it was stopped by the admin; no runner log for it
  survives, so why it never entered — no contracts resolved, no ticks, or a
  refusal — cannot be established from the database.

## Facts (machine-readable)

```yaml
name: BullCallSpread
category: Bullish
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

SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status", "FromUtc", "ToUtc",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" IN (36, 107);

/* signals 1489 (OPEN_GROUP), 1490 (CLOSE_GROUP "End of backtest"), 1491 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 107 ORDER BY "Id";

/* fills 2226 (BUY @ 160.55), 2227 (SELL @ 108.80), 2228 (SELL @ 151.45), 2229 (BUY @ 98.35); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 107 ORDER BY "Id";

/* positions 1124 (-591.50), 1125 (679.25) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 107 ORDER BY "Id";

SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523600CE');

/* driver bars (SourceKey live, 63 session bars) and the option candles behind the fills */
SELECT "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1;
SELECT "Symbol", "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close", "SourceKey"
FROM candles WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523600CE') AND "Resolution" = '5'
  AND "TimeStampUtc" IN ('2026-09-09 03:45:00+00', '2026-09-09 09:20:00+00') ORDER BY 1, 2;

/* intraday P&L path */
SELECT "SnapshotUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "TotalPnl", "OpenPositions"
FROM simulation_equity_snapshots WHERE "SimulationRunId" = 107 ORDER BY "SnapshotUtc", "Id";

/* run 36: the live-paper run that never traded */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "MetadataJson" FROM simulation_signals WHERE "SimulationRunId" = 36;
-->
