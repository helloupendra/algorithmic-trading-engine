# FulcrumMulti70

Source: `strategies/fulcrum/fulcrum_multi.py` (`FulcrumMultiStraddleStrategy`),
registered as `FulcrumMulti70` by `strategies/variants.py` with
`adjustment_steps=0.7`, `minor_steps=0.1` and no `direction`, so it sells.
The class's own registration (`FulcrumMultiStraddle`, defaults 0.5 / 0.1) and
the other thresholds (`FulcrumMulti50`, `FulcrumMulti90`) and the long twins
(`FulcrumMultiBuy50/70/90`) have their own pages. Strike arithmetic is
`strategies/strike_math.py`, the direction/wings switch
`strategies/fulcrum/_direction.py`, the (unused here) buyer's time stop
`strategies/fulcrum/_exit_rules.py`, lots `strategies/base_strategy.py`;
contract plumbing is `strategies/execution_runner.py` (live) and
`backtest/engine.py` + `backtest/contracts.py` (replay). Every rule below is
read from the code; where the code and a docstring disagree, the code is
documented and the difference listed under Limitations. Times are IST
(UTC + 5:30); the database stores UTC.

## Idea

Sell the straddles on the two strikes that bracket the spot (23,500 and
23,550 for a NIFTY spot of 23,526) and buy a far out-of-the-money call and put
about 3.5 % away as wings, so a runaway move is capped. While the spot stays
near the centre strike nothing is touched and the short premium decays. When
the spot drifts past the adjustment threshold — 0.7 of a strike for this
variant, 35 NIFTY points or 70 BANKNIFTY points — the whole group is
closed and rebuilt around the new level, keeping an old straddle as a third
one when it is still within 1.5 strikes of the spot; so the position carries
one to three straddles at a time. It is paid by decay in a slowly drifting
market and loses when the spot moves faster than decay pays, or when it
re-centres repeatedly across the same strikes. Nothing in this repository
tests that beyond the run cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none — `get_data_requirements` is not overridden (`[]`), so the runner does no warm-up and the first tick is evaluated | live only: ingestor → Redis stream `market:ticks`; each spot tick is one call of `on_bar` (`execution_runner.py`, the `listen_for_ticks` loop) |
| index candles | the same spot symbol | the run's resolution (5m in run 113) | none (`BacktestSession.warms_up` is false) | replay only: `candles` drives the loop, one call of `on_bar` per session bar with `spot_price` = the bar's close; the strategy never reads `inp.bars` |
| option quotes / candles | every leg the strategy names itself: CE and PE at each active straddle strike plus the two wings, all of the first expiry on or after today | live: latest quote; replay: the leg's candle at the run's resolution | none | live: `enrich_signal_leg_prices` puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for quotes; replay: `candles` for the contract (`HistoricalFeed.option_close_at`), synced from FYERS history while the contract exists |
| strike step | the option chain of the chosen expiry | once per run | — | live: smallest gap between strikes (`resolve_strike_step` → `strike_step_from_chain`; 50 for NIFTY, 100 for BANKNIFTY), else `FALLBACK_STRIKE_STEPS`; replay: `ContractResolver.step_for`; a run parameter `strike_step` overrides both (`strike_math.resolve_step`) |

It never reads option-chain OI, any bar series, or `inp.contracts`: the
runner resolves the default `atm_ce` / `atm_pe` requirements
(`BaseStrategy.get_contract_requirements`) and logs them, but the strategy
builds its legs from strike numbers as logical symbols (`NIFTY_CE_23500`)
that the runner and the replay resolve against the instrument master.

## Timeframe

- **Bar: none of its own.** `on_bar` reads `inp.spot_price`,
  `inp.strike_step`, `inp.timestamp_utc` and `inp.underlying`; the cadence is
  the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick, so the
  thresholds below are tested against the last traded price, not a bar
  close, and a group can be rebuilt any number of times within a minute.
  There is no session window and no last-entry time: a run started
  mid-session sells on its first tick (the `FulcrumMultiStraddle` page walks
  such a run). `FulcrumMulti70`'s one live-paper run (4) never left Pending; the run cited below is a replay.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`HistoricalFeed.driver_bars`,
  09:15 ≤ start < 15:30). The spot is the bar's close, so a threshold crossed
  inside a bar is seen only if the close is still past it. Fills are the
  close of each leg's candle for that bar (`option_close_at`), and a signal is
  stamped with the bar's start time (09:35 for the 09:35–09:40 bar).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30 on
  weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. In a replay the engine
  squares off at `eod_square_off_ist` (15:15 by default) and, if positions
  are still open on the last driver bar, at that bar with reason "End of
  backtest" — which is what closed run 113, whose candles end at 14:50.
  The strategy relies on these: it closes groups only to rebuild them and is
  never flat by its own decision.

## Entry

### Notation

$S$ is the spot, $\Delta$ the strike step (`step`). Grid points:

$$
K^- = \left\lfloor \tfrac{S}{\Delta} \right\rfloor \Delta,\qquad
K^+ = \left\lceil \tfrac{S}{\Delta} \right\rceil \Delta,\qquad
K^* = \mathrm{round}\!\left(\tfrac{S}{\Delta}\right) \Delta
$$

(`round_down_to_step`, `round_up_to_step`, `round_to_step`). The thresholds
are strike multiples turned into points of this underlying
(`steps_to_points`):

$$
A = \texttt{adjustment\_steps}\cdot\Delta = 0.7\,\Delta,\qquad
M = \texttt{minor\_steps}\cdot\Delta = 0.1\,\Delta,\qquad
N = 1.5\,\Delta .
$$

For this variant that is $A = 35$, $M = 5$, $N = 75$ points on NIFTY's
50-point grid; $A = 70$, $M = 10$, $N = 150$ on BANKNIFTY's 100; $A = 17.5$,
$M = 2.5$, $N = 37.5$ on MIDCPNIFTY's 25. The "70" in the name is the
BANKNIFTY value (`variants.py`: "0.5 IS the 50 these variants are named
after"). The state is three straddle strikes $(s_0, s_1, s_2)$ — lower,
centre, upper — with 0 meaning "none", plus the open group's id and legs. If
$K^* = S$ exactly the call returns without doing anything
(`if atm == price: return []`).

### First group (`on_bar`, $s_1 = 0$)

On the first evaluation the gate below cannot hold (every distance from
$s_1 = 0$ exceeds $A$), $s_1$ is left at 0 so the `st1 < price` branch runs:

$$
s_1 \leftarrow K^-,\qquad s_2 \leftarrow K^+,\qquad s_0 \leftarrow 0 .
$$

The group is the straddles at $K^-$ and $K^+$ — the two strikes bracketing
the spot — each as a SELL CE and a SELL PE of `lots` lots, plus the wings:

$$
W_{PE} = \left\lfloor \tfrac{S\,(1 - 0.035)}{g} \right\rfloor g,\qquad
W_{CE} = \left\lceil \tfrac{S\,(1 + 0.035)}{g} \right\rceil g,\qquad
g = \begin{cases} 5\Delta & 5\Delta / S \le 0.01 \\ \Delta & \text{otherwise}\end{cases}
$$

(`hedge_strike`: `DEFAULT_HEDGE_PCT` 0.035, `DEFAULT_HEDGE_GRID_STEPS` 5,
`MAX_HEDGE_GRID_FRACTION` 0.01; always rounded further out). The wings are
measured from the spot, not from the straddle strikes. On NIFTY at 23,500
the 250-point grid is 1.06 % of the spot, so the wings snap to the ordinary
50-point grid — 823 points out, 22,700 / 24,350 for a spot of 23,526; on
BANKNIFTY at 57,500 the grid is 500 (55,500 / 60,000). The wings are BUY legs
of the same `lots`.

In words: sell the straddle on the strike just below the spot and the one
just above, and buy a put about 3.5 % below and a call about 3.5 % above.

### Contracts, expiry, size, fill

- **Leg symbols** are logical — `f"{underlying}_CE_{strike}"` — and become
  broker symbols at signal time: live, `resolve_leg_symbol` →
  `api.get_exact_contract(underlying, expiry, strike, type)`; replay,
  `ContractResolver.resolve_logical`. A strike the master lacks leaves the
  logical symbol in place live (the API then refuses the group) and skips the
  `OPEN_GROUP` in a replay (`[SKIP] … no premium history` / missing contract).
- **Expiry:** the first listed expiry on or after today's UTC date
  (`valid_expiries[0]` live; `ContractResolver.expiry_for(day)` per replay
  day). NIFTY replays of 2026-09-09 trade the 2026-09-15 weekly
  (`NSE:NIFTY26915…`); BANKNIFTY has only monthly expiries in the September
  2026 master.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  legacy key `quantity`, default `default_lots` = 1) on every leg;
  `paper_orders.Quantity` stores lots and the API multiplies by the master's
  lot size (NIFTY 65, BANKNIFTY 30) for P&L.
- **Fill:** live, the latest quote per leg after a single 10 s wait for all
  legs of the signal (`core/leg_pricing.py`); a leg still unpriced is sent
  unpriced and the API rejects the opening group ("No price is available …
  rejected rather than filled at zero", `PaperTradingService`). Closing legs
  wait 0 s and fall back to the position's last mark. Replay: the close of
  the leg's candle for the signal bar; a close leg with no candle uses the
  last mark; no slippage, `charges_per_lot` 0 by default.
- **Signal names:** the strategy stamps its signals
  `strategy_name = f"{name}{variant_label}"` = `FulcrumMultiStraddle70`
  (`variant_label` is `adjustment_steps × 100`), group ids
  `FULCRUM-MULTI-<timestamp>-<counter>`, reason
  `Fulcrum Multi Straddle Adjusted. Active: [strikes]`; the engine adds
  `spot_price` and `atm_strike` to the metadata. The engine's own closes
  carry the registry name `FulcrumMulti70`.

## Position management

Every later evaluation runs the same method. Let $\hat s_0 = s_0$ if
$s_0 \ne 0$ else $s_1$, and $\hat s_2 = s_2$ if $s_2 \ne 0$ else $s_1$ (the
code writes these placeholders into `st0` / `st2` before testing).

### The gate — when nothing happens

$$
\text{hold} \iff |S - s_1| < M
\ \lor\ \big(S < s_1 \ \land\ s_1 - S < A \ \land\ \hat s_0 = s_1 - \Delta\big)
\ \lor\ \big(S > s_1 \ \land\ S - s_1 < A \ \land\ \hat s_2 = s_1 + \Delta\big)
$$

In words: ignore any move under $M$ (5 NIFTY points) from the centre strike;
ignore a move under $A$ (35 points) **if there is already a straddle on
the neighbouring strike on that side**. When there is none ($s_0 = 0$ or
$s_2 = 0$), the placeholder is $s_1$, the equality fails, and a move of only
$M$ past $s_1$ falls through to a rebuild — that is how the third straddle is
added, and it does not depend on `adjustment_steps` at all. This is the rule
the class docstring does not mention.

### Re-centring — what the new group is

When the gate does not hold, $s_1 \leftarrow K^*$ and one of two branches
runs, chosen by which half of its strike interval the spot is in:

- $K^* < S$ (spot in the lower half, `if st1 < price`):
  $s_1 \leftarrow K^-$, $s_2 \leftarrow K^+$; the old $\hat s_0$ is kept as
  the lower straddle iff $\hat s_0 < K^-$ and $|\hat s_0 - S| < N$, else
  $s_0 \leftarrow 0$.
- $K^* > S$ (upper half, the `else`): $s_0 \leftarrow K^-$,
  $s_1 \leftarrow K^+$; the old $\hat s_2$ is kept as the upper straddle iff
  $\hat s_2 > K^+$ and $|\hat s_2 - S| < N$, else $s_2 \leftarrow 0$.

So the two strikes bracketing the spot are always sold, and at most one
more: the strike one step beyond $K^*$ on the side away from the spot
($K^* - \Delta$ when the spot is in the lower half, $K^* + \Delta$ in the
upper), and only when a straddle was already there — the previous outer one,
or the previous centre when the spot has just crossed into the next interval
(the placeholder makes $s_1$ the candidate). With $N = 1.5\Delta$ strict no
other strike can pass the test: from a spot in that half-interval the strike
two steps away is always at least $1.5\Delta$ off. Everything else is
dropped. `active_straddles` is $\{s_i > 0\}$ in ascending order (the reason
text lists it).

### The group is rebuilt whenever any leg differs

The new leg list is the two wings at $W_{PE}(S)$, $W_{CE}(S)$ — recomputed
from the spot on every pass through the gate — plus SELL CE + SELL PE at each
active strike. `_legs_differ` compares the sets of (symbol, side, lots) with
the open group's legs:

- different → a `CLOSE_GROUP` with every open leg reversed
  (`_exit_rules.closing_legs`, reason "Adjusting straddles"), then an
  `OPEN_GROUP` with the new legs, both stamped with the same timestamp, spot
  and a new group id (`signal_count` + 1). The strategy never closes one
  straddle: it flattens everything it holds and re-sells what it wants.
- identical → nothing is emitted; $(s_0, s_1, s_2)$ are still updated.

A pass that leaves the straddles where they are can still rebuild the whole
group because a wing re-snapped. On NIFTY, with $g = \Delta = 50$,
$W_{PE}$ moves every 51.8 spot points and $W_{CE}$ every 48.3, so many
passes do (run 113: 3 of its 9 groups re-sold the straddles it had just bought back, at the same candle close). On BANKNIFTY ($g = 500$) a wing
moves only every 480–520 points.

### What the run's risk rules add

`parametersJson.risk` carries three levels that `StrategyRiskGuardService`
sweeps every `RiskGuardIntervalSeconds` (3 s, `appsettings.json`) in the
order leg → group → overall, each level stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; a trip closes that leg only, leaving the other half
  of that straddle naked and the strategy unaware (it still lists the strike
  as active).
- `group` — rupees on a signal group's realized + unrealized P&L; closes
  every leg of that group. With this strategy that is the whole position,
  since it holds one group at a time.
- `overall` — rupees on the run's total P&L; flattens everything and stops
  the run. Its `scope` (`day` default, or `run`) is honoured by the backtest
  engine; the live guard measures since the run started.

The backtest engine mirrors the same rules after the marks of every bar
(`BacktestSession._check_risk`). Run 113 set none (`"risk": {}`;
`StopLossPrice` / `TargetPrice` null on all its positions).

### What the strategy never does

- It never goes flat on its own: every `CLOSE_GROUP` it emits is followed by
  an `OPEN_GROUP` in the same call. Without a group / overall rule, the stop
  button, the 15:30 close or the replay's square-off the exposure lasts all
  day.
- It never sets a per-position stop or target.
- It never learns that the platform closed something. A group closed by a
  risk rule, the 15:30 sweep or the stop button stays in `state` as open; the
  next rebuild's `CLOSE_GROUP` names legs that are flat (the API's reduce-only
  path ignores them, `ApplyPositionAsync` returns false; the replay ledger
  counts them as "close legs ignored") and its `OPEN_GROUP` is a fresh entry.
  The same happens after a refused or skipped open: the group is recorded in
  `state` before the signal leaves `on_bar` — run 113 carried such a phantom
  group from 09:15 (see the worked example).
- Live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`, warm-up skipped), so a restart carries
  $(s_0, s_1, s_2)$ and the last group id forward.

## Exit

In order of precedence for one position:

1. **The strategy's own rebuild** — `CLOSE_GROUP` "Adjusting straddles" on
   the tick/bar the gate falls through and a leg differs; every open leg
   closes at the latest quote (live, 0 s wait) or the bar's candle close
   (replay), and the new group opens in the same call.
2. **Leg rules** (`EvaluateLeg`): stop-loss points / percent, trailing stop
   points / percent, target points / percent — close that leg only.
3. **Group rules** (`EvaluateGroup`) on the group's P&L.
4. **Overall rules** (`EvaluateOverall`) — flatten and stop the run.
5. Outside the sweep, on their own clocks, whichever takes the run lock first:
   a manual square-off or the run's stop button; market close by
   `MarketHoursService` at 15:30 IST.
6. Replay only: `eod_square_off_ist` (15:15 by default) and "End of backtest"
   on the last driver bar (run 113, signal 1636 at 14:50).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.7 (this variant; the class default is 0.5) | $A/\Delta$: how far (in strikes) the spot must be from the centre strike before the group is rebuilt, when a straddle already sits on that side. 0.7 = 70 pts BANKNIFTY, 35 pts NIFTY | Fewer rebuilds; the spot can sit further off-centre; at ≥ 1.0 it can reach the neighbouring strike before re-centring | More rebuilds, each paying the spread on 6–8 legs live; below `minor_steps` the minor rule dominates |
| `minor_steps` | 0.1 | $M/\Delta$: moves under this are ignored outright; a move past it on a side with **no** straddle rebuilds and adds one | The third straddle is added later; fewer wing-only rebuilds | Almost every tick past the centre evaluates; on NIFTY (5 pts) the empty-side add fires on noise |
| `adjustment_threshold`, `minor_threshold` | — | legacy spellings in points on the 100-point grid; read only when the `_steps` key is absent and divided by `LEGACY_GRID` (100), so a stored `50` / `10` becomes 0.5 / 0.1 (`steps_from_params`) | as above | as above |
| `direction` | `SELL` (this variant passes none) | `BUY` / `LONG` / `B` makes every straddle leg a buy (`resolve_direction`); that is `FulcrumMultiBuy70` | n/a | n/a |
| `use_hedges` | `true` when selling | whether the two wings are held; `false` gives a naked short | n/a | n/a |
| `max_hold_minutes` | 45 | BUY only (`long_time_stop_close` returns `[]` for a seller); no effect here | — | — |
| `target_steps` | 2.0 | resolved by `resolve_long_exit` but **never read** by this class (only `fulcrum_standard.py` uses `long_exit_reason`) | no effect | no effect |
| `strike_step` | from the chain | overrides the grid (`resolve_step`); wrong values name contracts that do not exist | — | — |
| `lots` (`quantity`) | `default_lots` = 1 | lots per leg on all 6–8 legs; P&L = points × lots × lot size | larger position, same signals | — |

Not parameters: the 3.5 % wing distance, the 5-step wing grid and its 1 %
cap, the 1.5-strike keep rule (`strike_math` constants and the literal
`1.5` in `on_bar`).

## Worked example

Run **113** — OfflineReplay, `NSE:NIFTY50-INDEX`, 5m, 2026-09-09 (one
session), `ParametersJson` verbatim: `{"adjustment_steps":0.7,"minor_steps":0.1,
"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15",
"charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},
"stop_loss":null,"target":null}`. Lot size 65 (`instruments.LotSize` for
`NSE:NIFTY2691523500CE`), expiry 2026-09-15, grid 50 → $A = 35$, $M = 5$,
$N = 75$. 63 driver bars 09:15–14:50; 19 signals (9 `OPEN_GROUP`, 8
"Adjusting straddles" closes, the engine's "End of backtest", one
`BACKTEST_SUMMARY`), 120 orders, 60 positions, `realizedPnl` 2,154.75. Every
close below is the 5m index close the strategy saw (`candles`), every fill
the leg's candle close for the same bar.

### 09:15 bar (C 23500.90) — first group, skipped

$K^- = 23500$, $K^+ = 23550$ → `Active: [23500, 23550]`, wings 22650 /
24350. Skipped — `skippedEntries[0]`: "no premium history for
NSE:NIFTY2691523550CE, NSE:NIFTY2691523550PE" (the 23550 candles begin at
09:35) — but `state` held group `…-001` as open. Through 09:50 no close was
35 from 23,500 (09:35's 26.25 above, which rebuilt the 0.5 twin, held here)
and 09:45–09:50 were under 5 below.

### 09:55 bar (C 23488.85) — a straddle added after 5 points, skipped

11.15 below $s_1 = 23500$ with **no** straddle below ($s_0 = 0$): past $M$,
and the neighbour test fails, so the group was rebuilt: `else`, $s_0 =
23450$, $s_1 = 23500$, $\hat s_2 = 23550$ kept (61.15 < 75) →
`Active: [23450, 23500, 23550]`. `skippedEntries[1]` at 04:25 UTC: the 23450
candles begin at 10:00. Two phantoms so far; their 6 + 8 legs are the "14
close legs ignored". Then 10:00–10:15 held: 20.2, 24.2, 15.15 below with
23450 in place, 16.8 above with 23550 in place.

### Signal 1619 — 10:20 bar (C 23537.80) → orders 2962–2967 → positions 1492–1497

37.8 above $s_1 = 23500$ ≥ 35: $K^* = 23550 > S$, `else`, $s_0 = 23500$,
$s_1 = 23550$, $s_2 = 0$ (the 23450 straddle is dropped) →
`Active: [23500, 23550]`, wings $\lfloor 23537.8 \times 0.965 / 50 \rfloor
\times 50 = 22700$ and $\lceil 23537.8 \times 1.035 / 50 \rceil \times 50 =
24400$. First real fills, at the 10:20 candle closes: `22700PE` BUY 5.65,
`24400CE` BUY 3.45, `23500CE` SELL 181.75, `23500PE` SELL 87.45, `23550CE`
SELL 150.55, `23550PE` SELL 106.40, 1 lot each.

### Signals 1620 / 1621 — 10:35 bar (C 23512.05) → orders 2968–2979 — a wing-only rebuild

37.95 below 23,550: $K^* = 23500 < S$, `if st1 < price`: $s_1 = 23500$,
$s_2 = 23550$, $\hat s_0 = 23500 \ge K^-$ → 0. Same straddles, but both
wings re-snapped (22650 / 24350), so group 003 was closed — position 1494,
`23500CE` SHORT:

$$
(181.75 - 164.20) \times 1 \times 65 = 17.55 \times 65 = 1140.75
$$

1495 `23500PE` $(87.45 - 97.90) \times 65 = -679.25$; 1496 `23550CE`
$(150.55 - 135.20) \times 65 = 997.75$; 1497 `23550PE`
$(106.40 - 118.80) \times 65 = -806.00$; wings $+26.00$, $-3.25$: group 003
= **+676.00** — and group 004 re-sold the same four legs at the same closes
(orders 2976–2979 at 164.20 / 97.90 / 135.20 / 118.80) with wings 5.65 /
3.85. Signals 1622 / 1623 at 10:55 (35.6 above — 0.6 past the threshold)
and 1624 / 1625 at 11:50 (47.7 below) were the same kind: 3 of the run's 9
groups were wing-only rebuilds.

### Group 005 — 10:55 to 11:50 → positions 1504–1509

`Active: [23500, 23550]` with $s_1 = 23550$. The ten closes from 11:00 to
11:45 were all below 23,550 by between 13.05 and 34.15 (11:30: 23515.85) with
the 23500 straddle in place — under $A = 35$, hold. The 0.5 twin (run 112)
rebuilt four times in the same window. Closed at 11:50 (C 23502.30, 47.7
below): 1506 `23500CE` $(174.50 - 157.30) \times 65 = 1118.00$, 1508
`23550CE` $(144.55 - 128.15) \times 65 = 1066.00$, 1507 $-500.50$, 1509
$-633.75$, wings $+35.75$, $+3.25$: **+1,088.75**.

### Signals 1626 / 1627 — 11:55 bar (C 23488.50) → orders 3004–3017 — a straddle added after 5 points

11.5 below $s_1 = 23500$ with nothing below: `else`, $s_0 = 23450$,
$s_1 = 23500$, $\hat s_2 = 23550$ kept (61.5 < 75) →
`Active: [23450, 23500, 23550]`, eight legs. Group 006 closed **+198.25**;
group 007 sold `23450CE` 177.20 and `23450PE` 85.40 on top of the re-sold
23500 / 23550 legs (146.30 / 105.45 / 119.85 / 128.00), wings 22650 / 24350.
It met the 12:25 → 12:30 bar, up 37 points, and was closed at 12:30 (C
23547.85, 47.85 above) for **−1,803.75**: position 1518 `23450CE`
$(177.20 - 213.80) \times 65 = -2379.00$, 1520 `23500CE` $-2148.25$, 1522
`23550CE` $-1914.25$, against 1519 `23450PE` $(85.40 - 65.40) \times 65 =
1300.00$, 1521 $+1566.50$, 1523 $+1823.25$, wings $-39.00$, $-13.00$.

### Group 009 — signals 1631 (12:35, C 23557.45) to 1632 (14:30, C 23511.55) → positions 1530–1537

Opened by the 5-point rule on the upside (7.45 above 23,550 with nothing
above): `Active: [23500, 23550, 23600]`, wings 22700 / 24400. Held 115
minutes — no close to 14:25 was 35 from 23,550, and 13:20–13:40 do not exist
— and closed when 14:30 printed 38.45 below: 1532 `23500CE`
$(189.70 - 158.50) \times 65 = 2028.00$, 1534 `23550CE` $1917.50$, 1536
`23600CE` $(129.45 - 103.65) \times 65 = 1677.00$, against 1533 $-747.50$,
1535 $-923.00$, 1537 `23600PE` $(118.15 - 135.30) \times 65 = -1114.75$,
wings $+16.25$, $+13.00$: **+2,866.50**.

### Signal 1636 — 14:50, "End of backtest" → orders 3074–3081 → positions 1544–1551

The last driver bar; `square_off: true`, `atm_strike` null. Group 011
(`[23450, 23500, 23550]`, opened at 14:35 by the 5-point rule after 14:30's
roll to `[23500, 23550]`) closed at the 14:50 closes: 1546 `23450CE`
$(182.75 - 183.70) \times 65 = -61.75$, 1547 $+65.00$, 1548 $-42.25$, 1549
$+55.25$, 1550 $+16.25$, 1551 $+65.00$, wings $+9.75$, $-9.75$: **+97.50**.

Day total, run 113, over groups 003–011: 676.00 − 204.75 + 1,088.75 +
198.25 − 1,803.75 − 884.00 + 2,866.50 + 120.25 + 97.50 = **₹2,154.75** (sum
of `RealizedPnl` over positions 1492–1551, no charges). Nine groups against
the 0.5 twin's sixteen on the same bars, for ₹13.00 more.

## Limitations

- **Never flat, never aware.** The strategy holds one group all day and
  replaces it in place; it cannot see a leg closed by a risk rule, a group
  closed at 15:30, a refused open or a stop button. After any of those its
  `state` describes a position that does not exist until the next rebuild
  re-enters (see Position management). Run 113's "14 close legs
  ignored: no matching open position" (`dataNotes`) are exactly the closes of
  its two skipped opens.
- **Wing re-snaps rebuild the whole group.** The wings are recomputed from
  the spot on every pass, and the leg comparison includes them. On NIFTY the
  wing grid falls back to the 50-point strike grid (the 250 grid is over 1 %
  of a 23,500 spot; it would apply only above 25,000), so a wing moves about
  every 50 index points and the straddles are bought back and re-sold at the
  same quote for no reason of their own — free in a replay (same-bar closes,
  `charges_per_lot` 0), a round trip on six to eight legs live. The class
  docstring's "designed for BANKNIFTY-scale prices" is, in the code, this
  1 % cap.
- **A third straddle is added after `minor_steps`, not `adjustment_steps`.**
  The docstring says small moves are ignored and the group is rebuilt past
  the adjustment threshold; the code rebuilds after a 5-point move toward a
  side that has no straddle (run 113 at 09:55 and signal 1627: 11.15 and 11.5 points on a 35-point threshold). Raising
  `adjustment_steps` does not change this.
- **"Within 1.5 strikes" means one strike.** The docstring's "keeping a
  nearby straddle when it is still within 1.5 strikes" can only ever keep the
  strike adjacent to the nearest strike on the side away from the spot (see
  Re-centring); the group is two or three straddles on consecutive strikes,
  never wider, and a straddle two strikes away is always closed.
- **The state is not persisted anywhere a reviewer can read.**
  `simulation_signals.Symbol` is empty and `Price` null; `MetadataJson`
  carries `group_id`, `reason` (with the `Active:` list), `spot_price`,
  `atm_strike` — not $(s_0, s_1, s_2)$ or the wings, which have to be rebuilt
  from the code and the bar closes as the worked example does.
- **Signal names do not carry the direction.** Every signal of this class is
  stamped `FulcrumMultiStraddle70` whether it sells or buys; only
  `simulation_runs.StrategyName` and the run parameters tell `FulcrumMulti70` from
  `FulcrumMultiBuy70` in `simulation_signals` / `paper_positions`.
- **The 2026-09-09 replay data.** The day's 5m index candles came from the
  live ingestor (`candles.SourceKey = 'live'`, 70 bars 08:40–14:50), of which
  63 are session bars — 13:20 to 13:40 are missing and nothing exists after
  14:50, so the 15:15 square-off never came and "End of backtest" closed the
  last group at 14:50. The 23500 option candles start 09:15, the 23550 ones
  09:35 and the 23450 ones 10:00 (also `live`), which is why the first group
  or two were skipped; the wings' candles are FYERS history (77 bars,
  09:15–15:35). Contracts that have expired have no broker history at all.
- **Replay differs from live.** Bar closes only (a threshold crossed and
  un-crossed inside a bar is invisible; live, every tick is tested), fills at
  the option candle close of the signal bar (the same close the decision was
  made on), no slippage, `charges_per_lot` 0. Live, a refused open (no quote
  in 10 s) leaves the phantom group described above. Ticks exactly on a
  strike are ignored (`atm == price`).
- **0.7 of a strike is 35 NIFTY points — an odd fraction of the grid.** The
  threshold sits between the 25 of the 0.5 twin and the 50 of a whole strike,
  so on 2026-09-09 it skipped five of run 112's morning rebuilds (10:50,
  11:15, 11:20, 11:40, 11:45) and made one of its own 5 minutes later (10:55,
  35.6 points — 0.6 past the line). The `minor_steps` add and the wing
  re-snaps are unchanged by it, so run 113 still rebuilt nine times in 63
  bars. Live, every one of those is a spread paid on six to eight legs; the
  replay's ₹2,154.75 carries none of that cost.
- **Dead code.** `ce_value` / `pe_value` are computed and never read;
  `state["straddle_list"]` is initialised and reset but never written;
  `target_steps` is resolved and never used by this class.

## Facts (machine-readable)

```yaml
name: FulcrumMulti70
category: Adjustment
evaluates_on: tick
resolution: any
data: ticks, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: false          # it closes groups only to rebuild them; never flat by its own decision
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status",
       "FromUtc" AT TIME ZONE 'Asia/Kolkata', "ToUtc" AT TIME ZONE 'Asia/Kolkata', "ParametersJson"
FROM simulation_runs WHERE "Id" = 113;

/* signals 1619-1637 (the last is BACKTEST_SUMMARY with skippedEntries and dataNotes) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 113 ORDER BY "Id";

/* orders 2962-3081 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_orders WHERE "SimulationRunId" = 113 ORDER BY "Id";

/* positions 1492-1551, per group and in total (2154.75) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata', "ClosedUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_positions WHERE "SimulationRunId" = 113 ORDER BY "Id";
SELECT "GroupId", count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 113 GROUP BY 1 ORDER BY 1;

/* the driver bars (5m index closes the strategy saw) */
SELECT "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata', "Open", "High", "Low", "Close", "SourceKey"
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" >= '2026-09-09 03:45' AND "TimeStampUtc" < '2026-09-09 10:00' ORDER BY 1;

/* option candle coverage that day (why 23550 / 23450 were skipped early) */
SELECT "Symbol", "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Symbol" LIKE 'NSE:NIFTY26915%' AND "Resolution" = '5'
  AND "TimeStampUtc" >= '2026-09-09' AND "TimeStampUtc" < '2026-09-10' GROUP BY 1, 2 ORDER BY 1;

/* lot size and expiry */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:NIFTY2691523500CE';
-->
