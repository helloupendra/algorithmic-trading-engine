# BearPutSpread

Source: `strategies/bearish/bear_put_spread_strategy.py`
(`BearPutSpreadStrategy`). Every rule below is read from the code; the
strike arithmetic it depends on is in `strategies/contract_selector.py`
(`strike_for_requirement`, `requirement_distance`), the plumbing in
`strategies/execution_runner.py` (live) and `backtest/engine.py` (replay).
Times are IST (UTC + 5:30); the database stores UTC.

## Idea

Buy the at-the-money put and sell a put `otm_offset_steps` strikes below
it, once, and hold the pair. The short put pays for part of the long one,
so the position costs less than a naked put and gains as the underlying
falls towards the short strike; below it the two puts move together and
the gain is capped at the strike distance minus the debit paid. It loses
the debit — at most — if the underlying stays put or rises. The class
encodes only the entry: it has no view on *when* the fall should happen and
no exit, so the run's risk rules and the platform's square-offs decide when
the bet is over. Nothing in this repository tests that the bet pays beyond
the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution (5m in run 106) | none — `get_data_requirements` is not overridden, so it returns `[]` and the replay does no warm-up | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | ATM PE (`atm_pe`) and the PE `otm_offset_steps` strikes below it (`otm_pe`) of the nearest expiry — `get_contract_requirements` | live: latest quote; replay: each contract's candle at the run's resolution | none | live: the runner puts both on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` per contract, synced from FYERS history on first use when the broker is linked (run 106 synced `NSE:NIFTY2691523400PE`, `syncedSymbols`) |

It never reads option-chain OI or any bar series. Nothing bearish is
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
  both contracts — 09:15 in run 106 — filled at the close of each option's
  candle for that bar (`HistoricalFeed.option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30
  on weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. A replay squares off at
  `eod_square_off_ist` (15:15 by default) and, if positions are still open
  on the last driver bar, there with reason "End of backtest". The strategy
  relies on these: it has no exit of its own.

## Entry

### Rule (`BearPutSpreadStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{atm\_pe},\ \text{otm\_pe}\} \subseteq \text{contracts}
$$

On entry the strategy emits one `OPEN_GROUP` with two legs, `lots` each:
BUY the ATM PE (`long_pe`) and SELL the OTM PE (`short_pe`) — a fresh
`uuid4` as `group_id`, metadata `strategy_type: "Bearish"` and the reason
`Opening Bear Put Spread. Buy PE:<K>, Sell PE:<K_P>`; then it sets
`is_invested = True`.

In words: on the first evaluation that has both puts, buy the spread and
never look again.

### Strikes (`get_contract_requirements` → `strike_for_requirement`)

With $S$ the spot (tick LTP live, driver-bar close in a replay), $\Delta$
the strike step from the option chain (50 for NIFTY, 100 for BANKNIFTY) and
$n$ = `otm_offset_steps`:

$$
K = \mathrm{round}(S/\Delta)\cdot\Delta,\qquad
K_P = \mathrm{round}\!\left(\frac{K - n\Delta}{\Delta}\right)\Delta .
$$

The long leg is declared as `ContractRequirement(key="atm_pe",
option_type="PE")` (moneyness `atm`, distance ignored); the short leg as
`ContractRequirement(key="otm_pe", option_type="PE", moneyness="otm",
steps=2, param="otm_offset_steps")`. `requirement_distance` turns it into
points: the run parameter named by `param` wins when it is a number greater
than 0 (a name not ending in `_points` counts strikes, × $\Delta$);
otherwise `steps` (2) × $\Delta$. For an OTM PE `strike_for_requirement`
*subtracts* the distance (`away = -away` when `option_type == "PE"`) and
snaps back to the grid with `round_to_step`. On NIFTY the default short
strike is $K - 100$ (`describe_requirement`: `otm_pe: OTM PE -2 strikes
(-100 pts) [param otm_offset_steps]`); on BANKNIFTY $K - 200$.

- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). On an expiry day the same-day
  contracts are traded.
- **Contracts:** the exact PE rows of the instrument master at $K$ and
  $K_P$ for that expiry. A missing row leaves its key out and the strategy
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
- a refused entry (no quote for either put within 10 s → the API rejects
  the unpriced group; or a run filter such as `filters.trade_window_ist`
  blocking it) leaves the flag set too, because it is written inside
  `on_bar` before the signal is posted — the runner's own comment: "The
  strategy still believes this group is open." Such a run does nothing for
  the rest of the day;
- live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`), so a restart does not re-enter;
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on positions 1122 and 1123 of run 106).

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s) in the order leg → group → overall, each level in the fixed order
stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; closes that leg only. On a spread the two legs
  move together, so a leg rule is a rule on one half of the position: a
  falling market that profits the long put is a loss on the short put, and
  a leg stop-loss would close the short put there and leave a naked long
  put — the opposite of what the spread was for. The API books the two legs
  as two `paper_positions`.
- `group` — rupees on the group's realized + unrealized P&L across both
  legs; closes them together. The spread is one group, so this is the level
  that matches the position.
- `overall` — rupees on the run's total P&L; flattens everything and ends
  the run. `scope` (`"day"` default, `"run"`) is honoured by the replay
  engine only.

The replay mirrors the same rules once per bar (`_check_risk`). Run 106 set
none (`"risk":{}`); its positions rode from 09:15 to the end of the range
through a +143.00 high at 10:05 and a −581.75 low at 12:55 with nothing
watching — a `group.stopLoss` of 500 would have closed the spread at 12:35
(the first bar at or under −500, −546.00) instead of letting it recover to
−227.50 by chance.

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
| `otm_offset_steps` | 2 | strikes between the long ATM put and the short put, on the underlying's own grid (× step = points: 100 on NIFTY, 200 on BANKNIFTY) | short put further down: less premium sold, larger debit, larger maximum gain and maximum loss, the spread behaves more like a naked put | short put closer in: more premium sold, smaller debit, smaller maximum gain; 0, a negative or a non-numeric value is ignored by `requirement_distance` and the built-in 2 applies, so the two legs cannot share a strike; a fraction is snapped to the grid by `round_to_step` |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | lots on both legs; P&L = premium points × lots × lot size | proportionally larger debit and gain, same single signal | n/a below 1 — `lots_from` clamps to 1 |

The long leg is always the ATM put and the short leg always below it;
neither is a parameter.

## Worked example

Run **106** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5`, range
2026-09-09 (`FromUtc` 2026-09-08 18:30:00 → `ToUtc` 2026-09-09 18:29:59 =
00:00–23:59:59 IST on Wednesday 2026-09-09), replayed on 2026-09-11 at
00:07:23 IST. `ParametersJson` verbatim:
`{"otm_offset_steps":2,"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Summary (signal 1488): 63 driver bars, 1 session, 2 trades,
`realizedPnl -227.5`, `charges 0`, `eodSquareOffs 0`, no skipped entries,
`syncedSymbols ["NSE:NIFTY2691523400PE"]` — the short put's candles were
fetched from FYERS history during the replay; the ATM put's were already
stored. Lot size 65 from `instruments.LotSize` of both symbols; both expire
Tuesday 2026-09-15, the first expiry in the master on or after the replayed
day.

### Signal 1486 — 09:15, OPEN_GROUP → orders 2222, 2223 → positions 1122, 1123

`MetadataJson`: `reason "Opening Bear Put Spread. Buy PE:23500.0, Sell
PE:23400.0"`, `spot_price 23500.9`, `atm_strike 23500`,
`strategy_type "Bearish"`, `group_id 9ad71212-b799-45ac-8348-d8c26311c8a6`.
The 09:15 index candle closed at **23500.90**: $23500.9/50 = 470.018 \to
23500$; with $n = 2$ and $\Delta = 50$, $K_P = 23400$.

Fills — the close of each contract's 09:15 candle:

- order 2222: BUY 1 lot `NSE:NIFTY2691523500PE` @ **108.55** (candle
  O 112.20, H 112.50, L 105.45, C 108.55) → position 1122, LONG,
  `AveragePrice 108.55`;
- order 2223: SELL 1 lot `NSE:NIFTY2691523400PE` @ **72.55** (candle
  O 50.00, H 75.45, L 46.30, C 72.55) → position 1123, SHORT,
  `AveragePrice 72.55`.

Net debit: $108.55 - 72.55 = 36.00$ points = ₹2,340.00 for one lot, the
most the spread can lose at expiry; the most it can make is
$(100 - 36.00) \times 65 = 64.00 \times 65 = 4160.00$ = ₹4,160.00, below 23,400. The
strategy never gets to expiry — see the close below.

### The day in between

No rule was configured. The equity snapshots (`simulation_equity_snapshots`,
run 106) put the spread at a high of **+143.00 at 10:05** (index close
23,475.80, the morning dip) and a low of **−581.75 at 12:55** (index close
23,566.00, 66 points above the long strike); by the last bar the index had
come back to 23,489.55 and the mark to −227.50.

### Signal 1487 — 14:50, CLOSE_GROUP "End of backtest" → orders 2224, 2225

`MetadataJson`: `square_off true`, `reason "End of backtest"`,
`spot_price 23489.55`, `atm_strike null`. The 15:15 EOD square-off never
fired because the stored index candles for the day end at 14:50 (see
Limitations); `execute` flattened the group on that last bar. Fills are the
14:50 candle closes:

- order 2224: SELL 1 `…23500PE` @ **94.75** (candle C 94.75);
- order 2225: BUY 1 `…23400PE` @ **62.25** (candle C 62.25).

### P&L

Long leg: (exit − entry) × lots × lot size; short leg: (entry − exit) ×
lots × lot size.

$$
\text{23500 PE (long): } (94.75 - 108.55) \times 1 \times 65 = -13.80 \times 65 = -897.00
$$

$$
\text{23400 PE (short): } (72.55 - 62.25) \times 1 \times 65 = 10.30 \times 65 = 669.50
$$

= `RealizedPnl` of positions 1122 (−897.00) and 1123 (669.50); the sum
₹−227.50 is the summary's `realizedPnl`. The index closed the range 11.35
points *below* where the spread was bought — the direction the spread
wanted — and the spread still lost: both puts decayed, and the long put
lost 13.80 points of premium against the short put's 10.30 gain. A debit
spread held through a flat day pays for time; the direction alone is not
enough. No charges, no slippage.

## Limitations

- **Nothing bearish is checked.** The code has no trend, momentum or level
  test; it buys the spread on the first tick regardless of what the market
  is doing. "Used when expecting a moderate drop" in the docstring is
  advice to the operator, not a rule.
- **No exit and no position awareness.** One signal per run. A leg closed
  by a `leg` rule leaves a naked long or short put and the strategy does
  not know; after any close the run is idle, since `is_invested` stays true.
- **A refused entry idles the run.** The flag is set before the signal is
  posted; an API rejection (no quote for either put within 10 s) or a run
  filter block means the run never trades. Do not pair with
  `filters.trade_window_ist` unless the runner starts inside the window.
- **Entry time is start-up time.** Live, the first tick — pre-open prints
  included. A replay enters on the 09:15 bar of the first day that has both
  contracts, at that bar's close.
- **The 09:15 fill of the short put is the close of a wide candle.** The
  23400 PE ranged 46.30–75.45 in its first five minutes and opened at
  50.00 against a 72.55 close. The replay took the close; a live run filled
  in those minutes could have got anything in the range.
- **The one-day replay ends at the range end, not by any rule.** "End of
  backtest" (signal 1487) is the engine flattening on the last driver bar;
  it is not an exit of the strategy and would not exist on a longer range,
  where the day ends at `eod_square_off_ist`.
- **The 2026-09-09 index candles are the ingestor's.** `candles` rows for
  `NSE:NIFTY50-INDEX` on that day are `SourceKey = live`, 08:40–14:50 with no
  13:20–13:40 bars: 63 session bars, not 75, and no bar at or after 15:15,
  so the EOD square-off could not fire. The ATM put's candles have the same
  origin; the short put's are FYERS history (77 bars, 09:15–15:35).
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
- Docstring and code agree on the legs ("Buys an At-The-Money (ATM) Put,
  and Sells an Out-Of-The-Money (OTM) Put").

## Facts (machine-readable)

```yaml
name: BearPutSpread
category: Bearish
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
FROM simulation_runs WHERE "Id" = 106;

/* signals 1486 (OPEN_GROUP), 1487 (CLOSE_GROUP "End of backtest"), 1488 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 106 ORDER BY "Id";

/* fills 2222 (BUY @ 108.55), 2223 (SELL @ 72.55), 2224 (SELL @ 94.75), 2225 (BUY @ 62.25); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 106 ORDER BY "Id";

/* positions 1122 (-897.00), 1123 (669.50) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 106 ORDER BY "Id";

SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" IN ('NSE:NIFTY2691523500PE', 'NSE:NIFTY2691523400PE');

/* driver bars (SourceKey live, 63 session bars) and the option candles behind the fills */
SELECT "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1;
SELECT "Symbol", "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close", "SourceKey"
FROM candles WHERE "Symbol" IN ('NSE:NIFTY2691523500PE', 'NSE:NIFTY2691523400PE') AND "Resolution" = '5'
  AND "TimeStampUtc" IN ('2026-09-09 03:45:00+00', '2026-09-09 09:20:00+00') ORDER BY 1, 2;

/* intraday P&L path */
SELECT "SnapshotUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "TotalPnl", "OpenPositions"
FROM simulation_equity_snapshots WHERE "SimulationRunId" = 106 ORDER BY "SnapshotUtc", "Id";
-->
