# ShortStrangle

Source: `strategies/neutral/strangle_strategy.py` (`StrangleStrategy`).
Every rule below is read from the code; the strike arithmetic it depends on
is in `strategies/contract_selector.py` (`strike_for_requirement`,
`requirement_distance`), the plumbing in `strategies/execution_runner.py`
(live) and `backtest/engine.py` (replay). Times are IST (UTC + 5:30); the
database stores UTC.

## Idea

Sell one out-of-the-money call and one out-of-the-money put of the nearest
expiry, each `otm_offset_steps` strikes away from the ATM strike, once, and
hold both. Compared with a straddle it collects less premium but pays off
over a wider band: the position keeps both premiums as long as the
underlying finishes inside the two strikes plus what was collected, and
loses once it breaks out past either strike by more than that. The class
encodes only the entry — when the bet is over is decided by the run's risk
rules and the platform's square-offs, not by the strategy. Nothing in this
repository tests that the bet pays beyond the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none | live only: ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar` |
| index candles | the same spot symbol | the run's resolution (5m in run 120) | none — `get_data_requirements` is not overridden, so it returns `[]` and the replay does no warm-up | replay only: `candles` (backfill) drives the loop, one call of `on_bar` per session bar; the strategy never reads `inp.bars` |
| option candles | OTM CE and OTM PE of the nearest expiry, `otm_offset_steps` strikes out on each side (`get_contract_requirements`: keys `otm_ce`, `otm_pe`) | live: latest quote; replay: the contract's candle at the run's resolution | none | live: the runner puts both on the ingestor watchlist (`ensure_contracts_tracked`) and the fill is the latest quote; replay: `candles` for the contract, synced from FYERS history on first use when the broker is linked (`HistoricalFeed._sync_then_read`; run 107 and 106 had already synced the two contracts run 120 used) |

It never reads option-chain OI or any bar series.

## Timeframe

- **Bar:** none of its own. `on_bar` looks only at `state` and
  `inp.contracts`; the cadence is the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick with
  the contracts resolved for that tick's ATM strike, so the strangle is sold
  on the first tick after start-up for which both OTM contracts exist in the
  instrument master — pre-open ticks included (the ingestor stamps pre-open
  prints; `market_is_open()` in the runner feeds only the feed-stall
  warning). No session window, no last-entry time.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`in_session`: 09:15 ≤
  start < 15:30). The entry is the first session bar of the range that has
  both contracts — 09:15 in run 120 — filled at the close of each option's
  candle for that bar (`HistoricalFeed.option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30
  on weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. A replay squares off at
  `eod_square_off_ist` (15:15 by default) and, if positions are still open
  on the last driver bar, there with reason "End of backtest". The strategy
  relies on these: it has no exit of its own.

## Entry

### Rule (`StrangleStrategy.on_bar`)

$$
\text{enter} \iff \neg\,\text{is\_invested} \;\land\; \{\text{otm\_ce},\ \text{otm\_pe}\} \subseteq \text{contracts}
$$

On entry the strategy emits one `OPEN_GROUP` with two legs — SELL the OTM
CE and SELL the OTM PE, `lots` each — a fresh `uuid4` as `group_id`,
metadata `strategy_type: "Neutral"` and the reason
`Opening Short Strangle at strikes CE:<K_C>, PE:<K_P>`; then it sets
`is_invested = True`.

In words: on the first evaluation that has both OTM contracts, sell one
strangle and never look again.

### Strikes (`get_contract_requirements` → `strike_for_requirement`)

With $S$ the spot (tick LTP live, driver-bar close in a replay), $\Delta$
the strike step from the option chain (50 for NIFTY, 100 for BANKNIFTY) and
$n$ = `otm_offset_steps`:

$$
K = \mathrm{round}(S/\Delta)\cdot\Delta,\qquad
K_C = \mathrm{round}\!\left(\frac{K + n\Delta}{\Delta}\right)\Delta,\qquad
K_P = \mathrm{round}\!\left(\frac{K - n\Delta}{\Delta}\right)\Delta .
$$

The requirements are declared as `ContractRequirement(key="otm_ce",
option_type="CE", moneyness="otm", steps=2, param="otm_offset_steps")` and
its PE mirror. `requirement_distance` turns them into points: the run
parameter named by `param` wins when it is a number greater than 0 (a name
that does not end in `_points` is a count of strikes, multiplied by
$\Delta$); otherwise `steps` (2) × $\Delta$. `strike_for_requirement` adds
the distance for an OTM CE and subtracts it for an OTM PE, then snaps back
to the grid with `round_to_step`. On NIFTY the default is ±100 points
(`describe_requirement` logs it as `otm_ce: OTM CE +2 strikes (+100 pts)
[param otm_offset_steps]`); the same 2 on BANKNIFTY is ±200.

- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the first expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). On an expiry day the same-day
  contract is traded.
- **Contracts:** the exact rows of the instrument master at
  $(K_C, \text{CE})$ and $(K_P, \text{PE})$ for that expiry. A missing row
  leaves its key out, the condition is false and the strategy does not enter
  on that tick or bar (state untouched, so it tries again on the next); the
  replay lists the gap once under `skippedEntries`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  default `default_lots` = 1, never below 1) per leg; `paper_orders.Quantity`
  is lots, the API multiplies by the master's lot size (NIFTY 65).
- **Fill:** live, up to 10 s of quote wait (`enrich_signal_leg_prices`,
  `SIGNAL_PRICE_WAIT_SECONDS`), then `/api/Simulator/signals`; the API prices
  every leg before it fills any and commits both legs in one transaction, so
  a strangle is booked whole or not at all, and an unpriced group is refused.
  Replay: the close of each option's candle at the signal bar; no premium
  history for either contract skips the entry.

## Position management

None. `state` is `{is_invested, group_id}` and nothing after the entry
changes it:

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
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on positions 1732 and 1733 of run 120).

**What the run's risk rules add.** `parametersJson.risk` carries three
levels that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s) in the order leg → group → overall, each level in the fixed order
stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; closes that leg only, leaving the other side of the
  strangle naked.
- `group` — rupees on the group's realized + unrealized P&L; closes both
  legs together. The strangle is one group, so this is the level that
  matches the position.
- `overall` — rupees on the run's total P&L; flattens everything and ends
  the run. `scope` (`"day"` default, `"run"`) is honoured by the replay
  engine only.

The replay mirrors the same rules once per bar (`_check_risk`). Run 120 set
none (`"risk":{}`); its positions rode from 09:15 to the end of the range
through a −136.50 low at 12:55 with nothing watching.

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, the stop button or a square-off, a sold strangle stays
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

Outside the sweep, on their own clocks: a manual square-off or the run's
stop button; market close by `MarketHoursService` at 15:30 IST on weekdays.

A replay applies the same four levels per bar, then the day's square-off at
`eod_square_off_ist` (`_eod_check`), then "End of backtest" at the last
driver bar (`execute`). Every close is a reduce-only `CLOSE_GROUP` priced at
the option's candle close for that bar, falling back to the last mark, then
the entry price (`PaperLedger.close_positions`).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `otm_offset_steps` | 2 | strikes between the ATM strike and each short strike, on the underlying's own grid (× step = points: 100 on NIFTY, 200 on BANKNIFTY) | strikes further out: less premium, wider band before either side is breached, smaller delta per leg | strikes closer in: more premium, narrower band; 0, a negative or a non-numeric value is ignored by `requirement_distance` and the built-in 2 applies — the offset cannot be set to 0 (that would be a straddle). A fraction is snapped to the grid by `round_to_step` |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | lots per leg; P&L = premium points × lots × lot size | proportionally larger premium and loss, same single signal | n/a below 1 — `lots_from` clamps to 1 |

Both sides are always SELL and the offset is symmetric; neither is a
parameter.

## Worked example

Run **120** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5`, range
2026-09-09 (`FromUtc` 2026-09-08 18:30:00 → `ToUtc` 2026-09-09 18:29:59 =
00:00–23:59:59 IST on Wednesday 2026-09-09), replayed on 2026-09-11 at
00:08:38 IST. `ParametersJson` verbatim:
`{"otm_offset_steps":2,"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Summary (signal 1712): 63 driver bars, 1 session, 2 trades,
`realizedPnl 1348.75`, `charges 0`, `eodSquareOffs 0`, no skipped entries,
`syncedSymbols []`. Lot size 65 from `instruments.LotSize` of
`NSE:NIFTY2691523600CE` and `…23400PE`; both expire Tuesday 2026-09-15, the
first expiry in the master on or after the replayed day.

### Signal 1710 — 09:15, OPEN_GROUP → orders 3442, 3443 → positions 1732, 1733

`MetadataJson`: `reason "Opening Short Strangle at strikes CE:23600.0,
PE:23400.0"`, `spot_price 23500.9`, `atm_strike 23500`,
`strategy_type "Neutral"`, `group_id 4ae0b981-a882-48a4-b399-e12899ac56bc`.
The 09:15 index candle closed at **23500.90**: $23500.9/50 = 470.018 \to
23500$; with $n = 2$ and $\Delta = 50$, $K_C = 23600$ and $K_P = 23400$.

Fills — the close of each contract's 09:15 candle (FYERS history, synced
by the earlier runs 107 and 106):

- order 3442: SELL 1 lot `NSE:NIFTY2691523600CE` @ **108.80** (candle
  O 154.85, H 165.00, L 101.15, C 108.80) → position 1732, SHORT,
  `AveragePrice 108.80`;
- order 3443: SELL 1 lot `NSE:NIFTY2691523400PE` @ **72.55** (candle
  O 50.00, H 75.45, L 46.30, C 72.55) → position 1733, SHORT,
  `AveragePrice 72.55`.

Premium collected: $108.80 + 72.55 = 181.35$ points = ₹11,787.75 for one
lot — 87.75 points less than the straddle sold at the same bar in run 105
(269.10), for a band of 23,400–23,600 instead of a point.

### The day in between

No rule was configured. The equity snapshots (`simulation_equity_snapshots`,
run 120) show total P&L at a low of **−136.50 at 12:55** (index close
23,566.00, within 34 points of the short call's strike) and a high of
+1,387.75 at 14:45 before the last bar. The index never left the
23,400–23,600 band that day: the session candles' extremes were a low of
23,467.10 (10:10) and a high of 23,571.35 (12:40).

### Signal 1711 — 14:50, CLOSE_GROUP "End of backtest" → orders 3444, 3445

`MetadataJson`: `square_off true`, `reason "End of backtest"`,
`spot_price 23489.55`, `atm_strike null`. The 15:15 EOD square-off never
fired because the stored index candles for the day end at 14:50 (see
Limitations); `execute` flattened the open group on that last bar. Fills
are the 14:50 candle closes:

- order 3444: BUY 1 `…23600CE` @ **98.35** (candle C 98.35);
- order 3445: BUY 1 `…23400PE` @ **62.25** (candle C 62.25).

### P&L

Short leg: (entry − exit) × lots × lot size.

$$
\text{CE: } (108.80 - 98.35) \times 1 \times 65 = 10.45 \times 65 = 679.25
$$

$$
\text{PE: } (72.55 - 62.25) \times 1 \times 65 = 10.30 \times 65 = 669.50
$$

= `RealizedPnl` of positions 1732 (679.25) and 1733 (669.50); the sum
₹1,348.75 is the summary's `realizedPnl`. No charges, no slippage.

## Limitations

- **No exit and no position awareness.** One signal per run. A leg closed
  by a `leg` rule leaves the other short leg naked and the strategy does not
  know; after any close the run is idle, since `is_invested` stays true.
- **A refused entry idles the run.** The flag is set before the signal is
  posted; an API rejection (no quote for either OTM contract within 10 s —
  OTM strikes are subscribed only when first resolved) or a run filter block
  means the run never trades. Do not pair with `filters.trade_window_ist`
  unless the runner starts inside the window.
- **Entry time is start-up time.** Live, the first tick — pre-open prints
  included. A replay enters on the 09:15 bar of the first day that has
  contracts, at that bar's close.
- **The 09:15 fills are the closes of very wide candles.** The 23600 CE's
  first five minutes ranged 101.15–165.00 and opened at 154.85, only 3.70
  below the ATM call's 158.55 open in the same minute — not a plausible
  price for a strike 100 points further out (by the close of the bar the
  gap was 51.75), so the opening prints of that candle are pre-open or
  illiquid. The replay took the close (108.80); a live run filled
  in those minutes could have got anything in the range.
- **The one-day replay ends at the range end, not by any rule.** "End of
  backtest" (signal 1711) is the engine flattening on the last driver bar;
  it is not an exit of the strategy and would not exist on a longer range,
  where the day ends at `eod_square_off_ist`.
- **The 2026-09-09 index candles are the ingestor's.** `candles` rows for
  `NSE:NIFTY50-INDEX` on that day are `SourceKey = live`, 08:40–14:50 with no
  13:20–13:40 bars: 63 session bars, not 75, and no bar at or after 15:15,
  so the EOD square-off could not fire. The OTM contracts' candles, by
  contrast, are FYERS history (77 bars, 09:15–15:35); the replay stopped
  where the driver stopped.
- **Replay fills are candle closes; no slippage; `charges_per_lot` 0.**
- **Expired contracts have no broker history** (summary `dataNotes`): an
  older day whose weekly has expired cannot be replayed unless the candles
  were stored while the contract was alive.
- **Expiry-day contracts.** The nearest expiry on or after today is used,
  so on a Tuesday the strangle sold expires that afternoon.
- **The offset is in strikes, so the band in points depends on the grid**:
  ±100 on NIFTY, ±200 on BANKNIFTY, ±50 on MIDCPNIFTY for the same
  `otm_offset_steps = 2`. Equal strikes are not equal moneyness.
- **`scope` of the overall rule is a replay-only concept**; the live guard
  measures the run cumulatively.
- **OI is not consulted** and is unavailable to a replay anyway.
- Docstring and code agree ("Sells an OTM Call and an OTM Put"; "wider range
  … but collects lower premium" is the thesis, not a rule the code checks).

## Facts (machine-readable)

```yaml
name: ShortStrangle
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
FROM simulation_runs WHERE "Id" = 120;

/* signals 1710 (OPEN_GROUP), 1711 (CLOSE_GROUP "End of backtest"), 1712 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 120 ORDER BY "Id";

/* fills 3442, 3443 (SELL @ 108.80 / 72.55), 3444, 3445 (BUY @ 98.35 / 62.25); Quantity is lots */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 120 ORDER BY "Id";

/* positions 1732 (679.25), 1733 (669.50) */
SELECT "Id", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 120 ORDER BY "Id";

SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" IN ('NSE:NIFTY2691523600CE', 'NSE:NIFTY2691523400PE');

/* driver bars (SourceKey live, 63 session bars) and the option candles behind the fills */
SELECT "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-08 18:30' AND '2026-09-09 18:29:59' GROUP BY 1;
SELECT "Symbol", "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close", "SourceKey"
FROM candles WHERE "Symbol" IN ('NSE:NIFTY2691523600CE', 'NSE:NIFTY2691523400PE') AND "Resolution" = '5'
  AND "TimeStampUtc" IN ('2026-09-09 03:45:00+00', '2026-09-09 09:20:00+00') ORDER BY 1, 2;

/* intraday P&L path */
SELECT "SnapshotUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "TotalPnl", "OpenPositions"
FROM simulation_equity_snapshots WHERE "SimulationRunId" = 120 ORDER BY "SnapshotUtc", "Id";
-->
