# IronButterfly

Source: `strategies/neutral/butterfly_strategy.py` (`IronButterflyStrategy`).
Every rule below is read from the code; the strike arithmetic it depends on
is in `strategies/contract_selector.py` (`strike_for_requirement`,
`requirement_distance`), the plumbing in `strategies/execution_runner.py`
(live) and `backtest/engine.py` (replay). Times are IST (UTC + 5:30); the
database stores UTC.

## Idea

Sell the at-the-money straddle and buy a call and a put `wing_offset_steps`
strikes further out as wings, once, and hold all four legs. The body
collects two premiums; the wings give part of it back in exchange for a
ceiling on the loss: past either wing the long option gains point for point
with the short one, so the worst case at expiry is the wing distance minus
the net credit. The position pays off when the underlying finishes near the
centre strike. The class encodes only the entry; when the bet is over is
decided by the run's risk rules and the platform's square-offs. Nothing in
this repository tests that the bet pays beyond the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution (5m in run 119) | none — `get_data_requirements` is not overridden, so it returns `[]` and the replay does no warm-up | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | four contracts of the nearest expiry: ATM CE and PE (`atm_ce`, `atm_pe`) and the wings `wing_offset_steps` strikes out (`otm_ce`, `otm_pe`) — `get_contract_requirements` | live: latest quote; replay: each contract's candle at the run's resolution | none | live: the runner puts all four on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` per contract, synced from FYERS history on first use when the broker is linked (run 119 synced `NSE:NIFTY2691523700CE` and `…23300PE`, `syncedSymbols`) |

It never reads option-chain OI or any bar series.

## Timeframe

- **Bar:** none of its own. `on_bar` looks only at `state` and
  `inp.contracts`; the cadence is the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick with
  the four contracts resolved for that tick's ATM strike; the butterfly is
  opened on the first tick after start-up for which all four exist in the
  instrument master — pre-open ticks included (the ingestor stamps pre-open
  prints; the runner's `market_is_open()` feeds only the feed-stall
  warning). No session window, no last-entry time.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`in_session`: 09:15 ≤
  start < 15:30). The entry is the first session bar of the range that has
  all four contracts — 09:15 in run 119 — filled at the close of each
  option's candle for that bar (`HistoricalFeed.option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30
  on weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. A replay squares off at
  `eod_square_off_ist` (15:15 by default) and, if positions are still open
  on the last driver bar, there with reason "End of backtest". The strategy
  relies on these: it has no exit of its own.

## Entry

### Rule (`IronButterflyStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{atm\_ce},\ \text{atm\_pe},\ \text{otm\_ce},\ \text{otm\_pe}\} \subseteq \text{contracts}
$$

On entry the strategy emits one `OPEN_GROUP` with four legs, `lots` each:
SELL ATM CE, SELL ATM PE, BUY OTM CE, BUY OTM PE — a fresh `uuid4` as
`group_id`, metadata `strategy_type: "Neutral"` and the reason
`Opening Iron Butterfly. Center: <K>, Wings: CE:<K_C> PE:<K_P>`; then it
sets `is_invested = True`. All four keys are required (`all(k in
inp.contracts for k in req_keys)`): with one wing missing from the master
the strategy does not trade a three-legged position, it does not trade at
all.

In words: on the first evaluation that has the body and both wings, sell
the straddle, buy the wings, and never look again.

### Strikes (`get_contract_requirements` → `strike_for_requirement`)

With $S$ the spot (tick LTP live, driver-bar close in a replay), $\Delta$
the strike step from the option chain (50 for NIFTY, 100 for BANKNIFTY) and
$w$ = `wing_offset_steps`:

$$
K = \mathrm{round}(S/\Delta)\cdot\Delta,\qquad
K_C = \mathrm{round}\!\left(\frac{K + w\Delta}{\Delta}\right)\Delta,\qquad
K_P = \mathrm{round}\!\left(\frac{K - w\Delta}{\Delta}\right)\Delta .
$$

The body is declared as `ContractRequirement(key="atm_ce", option_type="CE")`
and its PE twin (moneyness `atm`, so the distance is ignored); the wings as
`ContractRequirement(key="otm_ce", option_type="CE", moneyness="otm",
steps=4, param="wing_offset_steps")` and the PE mirror. `requirement_distance`
turns a wing into points: the run parameter named by `param` wins when it
is a number greater than 0 (a name not ending in `_points` counts strikes,
× $\Delta$); otherwise `steps` (4) × $\Delta$. `strike_for_requirement` adds
the distance for the OTM CE, subtracts it for the OTM PE and snaps back to
the grid with `round_to_step`. On NIFTY the default wing is ±200 points
(`describe_requirement`: `otm_ce: OTM CE +4 strikes (+200 pts) [param
wing_offset_steps]`); on BANKNIFTY the same 4 is ±400.

- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). On an expiry day the same-day
  contracts are traded.
- **Contracts:** the exact rows of the instrument master at the four
  (strike, type) pairs for that expiry. A missing row leaves its key out and
  the strategy does not enter on that tick or bar (state untouched, so it
  tries again on the next); the replay lists the gap once under
  `skippedEntries`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  default `default_lots` = 1, never below 1) per leg — the same count on
  every leg, so the structure is always balanced; `paper_orders.Quantity` is
  lots, the API multiplies by the master's lot size (NIFTY 65).
- **Fill:** live, up to 10 s of quote wait for all four legs together
  (`enrich_signal_leg_prices`, `SIGNAL_PRICE_WAIT_SECONDS`), then
  `/api/Simulator/signals`; the API prices every leg before it fills any and
  commits the four legs in one transaction, so the butterfly is booked whole
  or not at all, and an unpriced group is refused. Replay: the close of each
  option's candle at the signal bar; no premium history for any one of the
  four skips the entry.

## Position management

None. `state` is `{is_invested, group_id}` and nothing after the entry
changes it:

- no adjustment, no roll, no addition, no re-entry — after the one
  `OPEN_GROUP` the method returns `[]` on every later call;
- it does not know whether the group is still open: a leg closed by a risk
  rule, a group closed at 15:30 or a manual square-off leaves
  `is_invested = True`, so the run never enters again;
- a refused entry (no quote for one of the four within 10 s → the API
  rejects the unpriced group; or a run filter such as
  `filters.trade_window_ist` blocking it) leaves the flag set too, because
  it is written inside `on_bar` before the signal is posted — the runner's
  own comment: "The strategy still believes this group is open." Such a run
  does nothing for the rest of the day. Four fresh subscriptions make this
  more likely than for a two-leg strategy;
- live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`), so a restart does not re-enter;
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on positions 1728–1731 of run 119).

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s) in the order leg → group → overall, each level in the fixed order
stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; closes that leg only. On a butterfly this breaks
  the structure: a leg stop on a short body leg leaves a lopsided position,
  a leg target on a wing removes the protection the wing was bought for.
  The API books the four legs as four `paper_positions`, so the "defined
  risk" of the docstring holds only while all four are open.
- `group` — rupees on the group's realized + unrealized P&L across all four
  legs; closes them together. The butterfly is one group, so this is the
  level that matches the position.
- `overall` — rupees on the run's total P&L; flattens everything and ends
  the run. `scope` (`"day"` default, `"run"`) is honoured by the replay
  engine only.

The replay mirrors the same rules once per bar (`_check_risk`). Run 119 set
none (`"risk":{}`); its four positions rode from 09:15 to the end of the
range through a −113.75 low at 09:35 with nothing watching.

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, the stop button or a square-off, the butterfly stays
open until 15:30.

## Exit

Live, `StrategyRiskGuardService.SweepAsync` checks each open position in
this order on every sweep; the first rule that fires wins:

1. The position's own `StopLossPrice` / `TargetPrice` — never set by this
   strategy.
2. Leg rules: stop-loss points, stop-loss percent, trailing stop points,
   trailing stop percent, target points, target percent (`EvaluateLeg`) —
   evaluated on each of the four legs separately.
3. Group rules on the group's P&L (`EvaluateGroup`).
4. Overall rules on the run's total P&L (`EvaluateOverall`) — flattens and
   stops the run.

Outside the sweep, on their own clocks: a manual square-off or the run's
stop button; market close by `MarketHoursService` at 15:30 IST on weekdays.

A replay applies the same four levels per bar, then the day's square-off at
`eod_square_off_ist` (`_eod_check`), then "End of backtest" at the last
driver bar (`execute`). Every close is a reduce-only `CLOSE_GROUP` — one
signal per group, all its open legs — priced at each option's candle close
for that bar, falling back to the last mark, then the entry price
(`PaperLedger.close_positions`).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `wing_offset_steps` | 4 | strikes between the centre strike and each wing, on the underlying's own grid (× step = points: 200 on NIFTY, 400 on BANKNIFTY) | wings further out: cheaper wings, larger net credit, larger maximum loss (wing distance − credit), protection starts later | wings closer in: dearer wings, smaller credit, smaller maximum loss; 0, a negative or a non-numeric value is ignored by `requirement_distance` and the built-in 4 applies, so a wing cannot coincide with the body; a fraction is snapped to the grid by `round_to_step` |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | lots on every leg; P&L = premium points × lots × lot size | proportionally larger credit and loss, same single signal, still balanced | n/a below 1 — `lots_from` clamps to 1 |

The body is always the ATM straddle, the wings always symmetric and always
BUY; none of that is a parameter.

## Worked example

Run **119** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5`, range
2026-09-09 (`FromUtc` 2026-09-08 18:30:00 → `ToUtc` 2026-09-09 18:29:59 =
00:00–23:59:59 IST on Wednesday 2026-09-09), replayed on 2026-09-11 at
00:08:33 IST. `ParametersJson` verbatim:
`{"wing_offset_steps":4,"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Summary (signal 1709): 63 driver bars, 1 session, 4 trades,
`realizedPnl 344.5`, `charges 0`, `eodSquareOffs 0`, no skipped entries,
`syncedSymbols ["NSE:NIFTY2691523700CE", "NSE:NIFTY2691523300PE"]` — the
two wings had no stored candles and were fetched from FYERS history during
the replay; the body's candles were already in `candles`. Lot size 65 from
`instruments.LotSize` of each of the four symbols; all expire Tuesday
2026-09-15, the first expiry in the master on or after the replayed day.

### Signal 1707 — 09:15, OPEN_GROUP → orders 3434–3437 → positions 1728–1731

`MetadataJson`: `reason "Opening Iron Butterfly. Center: 23500.0, Wings:
CE:23700.0 PE:23300.0"`, `spot_price 23500.9`, `atm_strike 23500`,
`strategy_type "Neutral"`, `group_id 25e55030-3b20-4369-8c27-f3b083ede8b1`.
The 09:15 index candle closed at **23500.90**: $23500.9/50 = 470.018 \to
23500$; with $w = 4$ and $\Delta = 50$ the wings are $23500 \pm 200$.

Fills — the close of each contract's 09:15 candle:

| Order | Leg | Symbol | Side | Fill | 09:15 candle (O/H/L/C) | Position |
|-------|-----|--------|------|------|------------------------|----------|
| 3434 | body | `NSE:NIFTY2691523500CE` | SELL 1 | **160.55** | 158.55 / 163.40 / 157.85 / 160.55 | 1728, SHORT |
| 3435 | body | `NSE:NIFTY2691523500PE` | SELL 1 | **108.55** | 112.20 / 112.50 / 105.45 / 108.55 | 1729, SHORT |
| 3436 | wing | `NSE:NIFTY2691523700CE` | BUY 1 | **69.90** | 90.00 / 92.40 / 67.10 / 69.90 | 1730, LONG |
| 3437 | wing | `NSE:NIFTY2691523300PE` | BUY 1 | **47.10** | 30.15 / 49.35 / 30.15 / 47.10 | 1731, LONG |

Net credit: $(160.55 + 108.55) - (69.90 + 47.10) = 269.10 - 117.00 = 152.10$
points = ₹9,886.50 for one lot. Held to expiry that would cap the loss at
$(200 - 152.10) \times 65 = 47.90 \times 65 = 3113.50$ = ₹3,113.50 per lot beyond
either wing; the strategy never gets there — see the close below.

### The day in between

No rule was configured. The equity snapshots (`simulation_equity_snapshots`,
run 119) put the group's total P&L at a low of **−113.75 at 09:35** and a
high of +347.75 at 14:35; the index stayed inside 23,467.10–23,571.35
(session candle extremes) all day, far from either wing, so the wings only
ever decayed.

### Signal 1708 — 14:50, CLOSE_GROUP "End of backtest" → orders 3438–3441

`MetadataJson`: `square_off true`, `reason "End of backtest"`,
`spot_price 23489.55`, `atm_strike null`. The 15:15 EOD square-off never
fired because the stored index candles for the day end at 14:50 (see
Limitations); `execute` flattened the group on that last bar. Fills are the
14:50 candle closes: order 3438 BUY 1 `…23500CE` @ **151.45**, 3439 BUY 1
`…23500PE` @ **94.75**, 3440 SELL 1 `…23700CE` @ **59.70**, 3441 SELL 1
`…23300PE` @ **39.70**.

### P&L

Short legs: (entry − exit) × lots × lot size; long legs: (exit − entry) ×
lots × lot size.

$$
\text{23500 CE (short): } (160.55 - 151.45) \times 65 = 9.10 \times 65 = 591.50
$$

$$
\text{23500 PE (short): } (108.55 - 94.75) \times 65 = 13.80 \times 65 = 897.00
$$

$$
\text{23700 CE (long): } (59.70 - 69.90) \times 65 = -10.20 \times 65 = -663.00
$$

$$
\text{23300 PE (long): } (39.70 - 47.10) \times 65 = -7.40 \times 65 = -481.00
$$

= `RealizedPnl` of positions 1728 (591.50), 1729 (897.00), 1730 (−663.00)
and 1731 (−481.00). Sum: $591.50 + 897.00 - 663.00 - 481.00 = 344.50$, the
summary's `realizedPnl`. The body made ₹1,488.50 — exactly the ShortStraddle
of run 105 at the same bars — and the wings cost ₹1,144.00 of it: on a day
the index never approached a wing, the insurance was paid for and unused.
No charges, no slippage.

## Limitations

- **No exit and no position awareness.** One signal per run. Any leg
  closed by a `leg` rule unbalances the structure and the strategy does not
  know; after any close the run is idle, since `is_invested` stays true.
- **"Defined risk" is a property of the group, not of the platform.** The
  four legs are four `paper_positions`. A leg rule can close one, the API
  fills each leg's close at its own quote, and nothing in the code keeps the
  four together except the group-level rules and the square-offs.
- **A refused entry idles the run.** The flag is set before the signal is
  posted; one of four contracts without a quote inside 10 s, or a run
  filter block, and the run never trades. Do not pair with
  `filters.trade_window_ist` unless the runner starts inside the window.
- **Entry time is start-up time.** Live, the first tick — pre-open prints
  included. A replay enters on the 09:15 bar of the first day that has all
  four contracts, at that bar's close.
- **The 09:15 fills are the closes of wide candles.** The 23700 CE's first
  five minutes opened at 90.00 against a 69.90 close; the 23300 PE at 30.15
  against 47.10. A live run filled in those minutes could have paid or
  received anything in the range.
- **The one-day replay ends at the range end, not by any rule.** "End of
  backtest" (signal 1708) is the engine flattening on the last driver bar;
  it is not an exit of the strategy and would not exist on a longer range,
  where the day ends at `eod_square_off_ist`.
- **The 2026-09-09 index candles are the ingestor's.** `candles` rows for
  `NSE:NIFTY50-INDEX` on that day are `SourceKey = live`, 08:40–14:50 with no
  13:20–13:40 bars: 63 session bars, not 75, and no bar at or after 15:15,
  so the EOD square-off could not fire. The body's candles have the same
  origin (63 bars, 09:15–14:50); the wings' are FYERS history (77 bars,
  09:15–15:35). The replay stopped where the driver stopped.
- **Replay fills are candle closes; no slippage; `charges_per_lot` 0.**
  With four legs, charges would matter more here than for the two-leg
  strategies.
- **Expired contracts have no broker history** (summary `dataNotes`): the
  wings of an older day whose weekly has expired cannot be priced, so the
  whole entry is skipped.
- **Expiry-day contracts.** The nearest expiry on or after today is used,
  so on a Tuesday the butterfly expires that afternoon.
- **The wing is in strikes, so the loss cap in points depends on the
  grid**: ±200 on NIFTY, ±400 on BANKNIFTY, ±100 on MIDCPNIFTY for the same
  `wing_offset_steps = 4`. (The Fulcrum family's `strike_math.hedge_strike`
  sizes wings as a share of spot; this strategy does not use it.)
- **`scope` of the overall rule is a replay-only concept**; the live guard
  measures the run cumulatively.
- **OI is not consulted** and is unavailable to a replay anyway.
- Docstring and code agree on the legs ("Sell ATM Call, Sell ATM Put …
  Buy OTM Call, Buy OTM Put"); "defined-risk" is true of the structure as
  filled, with the caveat above.

## Facts (machine-readable)

```yaml
name: IronButterfly
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

SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status", "FromUtc", "ToUtc",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" = 119;

/* signals 1707 (OPEN_GROUP), 1708 (CLOSE_GROUP "End of backtest"), 1709 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 119 ORDER BY "Id";

/* fills 3434-3437 (open), 3438-3441 (close); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 119 ORDER BY "Id";

/* positions 1728 (591.50), 1729 (897.00), 1730 (-663.00), 1731 (-481.00) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 119 ORDER BY "Id";

SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments
WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523500PE', 'NSE:NIFTY2691523700CE', 'NSE:NIFTY2691523300PE');

/* driver bars (SourceKey live, 63 session bars) and the option candles behind the fills */
SELECT "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1;
SELECT "Symbol", "SourceKey", count(*) FROM candles
WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523500PE', 'NSE:NIFTY2691523700CE', 'NSE:NIFTY2691523300PE')
  AND "Resolution" = '5' AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1, 2;
SELECT "Symbol", "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close"
FROM candles WHERE "Symbol" IN ('NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523500PE', 'NSE:NIFTY2691523700CE', 'NSE:NIFTY2691523300PE')
  AND "Resolution" = '5' AND "TimeStampUtc" IN ('2026-09-09 03:45:00+00', '2026-09-09 09:20:00+00') ORDER BY 1, 2;

/* intraday P&L path */
SELECT "SnapshotUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "TotalPnl", "OpenPositions"
FROM simulation_equity_snapshots WHERE "SimulationRunId" = 119 ORDER BY "SnapshotUtc", "Id";
-->
