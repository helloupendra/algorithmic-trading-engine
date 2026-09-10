# FulcrumMulti90

Source: `strategies/fulcrum/fulcrum_multi.py` (`FulcrumMultiStraddleStrategy`),
registered as `FulcrumMulti90` by `strategies/variants.py` with
`adjustment_steps=0.9`, `minor_steps=0.1` and no `direction`, so it sells.
The class's own registration (`FulcrumMultiStraddle`, defaults 0.5 / 0.1) and
the other thresholds (`FulcrumMulti50`, `FulcrumMulti70`) and the long twins
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
the spot drifts past the adjustment threshold — 0.9 of a strike for this
variant, 45 NIFTY points or 90 BANKNIFTY points — the whole group is
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
| index candles | the same spot symbol | the run's resolution (5m in run 114) | none (`BacktestSession.warms_up` is false) | replay only: `candles` drives the loop, one call of `on_bar` per session bar with `spot_price` = the bar's close; the strategy never reads `inp.bars` |
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
  such a run). `FulcrumMulti90` itself has only been run as a replay.
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
  backtest" — which is what closed run 114, whose candles end at 14:50.
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
A = \texttt{adjustment\_steps}\cdot\Delta = 0.9\,\Delta,\qquad
M = \texttt{minor\_steps}\cdot\Delta = 0.1\,\Delta,\qquad
N = 1.5\,\Delta .
$$

For this variant that is $A = 45$, $M = 5$, $N = 75$ points on NIFTY's
50-point grid; $A = 90$, $M = 10$, $N = 150$ on BANKNIFTY's 100; $A = 22.5$,
$M = 2.5$, $N = 37.5$ on MIDCPNIFTY's 25. The "90" in the name is the
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
  `strategy_name = f"{name}{variant_label}"` = `FulcrumMultiStraddle90`
  (`variant_label` is `adjustment_steps × 100`), group ids
  `FULCRUM-MULTI-<timestamp>-<counter>`, reason
  `Fulcrum Multi Straddle Adjusted. Active: [strikes]`; the engine adds
  `spot_price` and `atm_strike` to the metadata. The engine's own closes
  carry the registry name `FulcrumMulti90`.

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
ignore a move under $A$ (45 points) **if there is already a straddle on
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
passes do (run 114: none of its 3 groups, but run 112 with the 0.5 threshold on the same bars: 7 of 16). On BANKNIFTY ($g = 500$) a wing
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
(`BacktestSession._check_risk`). Run 114 set none (`"risk": {}`;
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
  `state` before the signal leaves `on_bar` — run 114 carried such a phantom
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
   on the last driver bar (run 114, signal 1643 at 14:50).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.9 (this variant; the class default is 0.5) | $A/\Delta$: how far (in strikes) the spot must be from the centre strike before the group is rebuilt, when a straddle already sits on that side. 0.9 = 90 pts BANKNIFTY, 45 pts NIFTY | Fewer rebuilds; the spot can sit further off-centre; at ≥ 1.0 it can reach the neighbouring strike before re-centring | More rebuilds, each paying the spread on 6–8 legs live; below `minor_steps` the minor rule dominates |
| `minor_steps` | 0.1 | $M/\Delta$: moves under this are ignored outright; a move past it on a side with **no** straddle rebuilds and adds one | The third straddle is added later; fewer wing-only rebuilds | Almost every tick past the centre evaluates; on NIFTY (5 pts) the empty-side add fires on noise |
| `adjustment_threshold`, `minor_threshold` | — | legacy spellings in points on the 100-point grid; read only when the `_steps` key is absent and divided by `LEGACY_GRID` (100), so a stored `50` / `10` becomes 0.5 / 0.1 (`steps_from_params`) | as above | as above |
| `direction` | `SELL` (this variant passes none) | `BUY` / `LONG` / `B` makes every straddle leg a buy (`resolve_direction`); that is `FulcrumMultiBuy90` | n/a | n/a |
| `use_hedges` | `true` when selling | whether the two wings are held; `false` gives a naked short | n/a | n/a |
| `max_hold_minutes` | 45 | BUY only (`long_time_stop_close` returns `[]` for a seller); no effect here | — | — |
| `target_steps` | 2.0 | resolved by `resolve_long_exit` but **never read** by this class (only `fulcrum_standard.py` uses `long_exit_reason`) | no effect | no effect |
| `strike_step` | from the chain | overrides the grid (`resolve_step`); wrong values name contracts that do not exist | — | — |
| `lots` (`quantity`) | `default_lots` = 1 | lots per leg on all 6–8 legs; P&L = points × lots × lot size | larger position, same signals | — |

Not parameters: the 3.5 % wing distance, the 5-step wing grid and its 1 %
cap, the 1.5-strike keep rule (`strike_math` constants and the literal
`1.5` in `on_bar`).

## Worked example

Run **114** — OfflineReplay, `NSE:NIFTY50-INDEX`, 5m, 2026-09-09 (one
session), `ParametersJson` verbatim: `{"adjustment_steps":0.9,"minor_steps":0.1,
"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15",
"charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},
"stop_loss":null,"target":null}`. Lot size 65 (`instruments.LotSize` for
`NSE:NIFTY2691523500CE`), expiry 2026-09-15, grid 50 → $A = 45$, $M = 5$,
$N = 75$. 63 driver bars 09:15–14:50; 7 signals (3 `OPEN_GROUP`, 2
"Adjusting straddles" closes, the engine's "End of backtest", one
`BACKTEST_SUMMARY`), 40 orders, 20 positions, `realizedPnl` 1,946.75. Every
close below is the 5m index close the strategy saw (`candles`), every fill
the leg's candle close for the same bar.

### 09:15 to 12:25 — two phantom groups and three hours of holding

09:15 (C 23500.90): $K^- = 23500$, $K^+ = 23550$ → `Active: [23500, 23550]`,
skipped — `skippedEntries[0]`: "no premium history for NSE:NIFTY2691523550CE,
NSE:NIFTY2691523550PE" (the 23550 candles begin at 09:35) — with `state`
holding group `…-001` as open. 09:55 (C 23488.85): 11.15 below $s_1 = 23500$
with no straddle below, past $M = 5$, so the group was rebuilt to
`Active: [23450, 23500, 23550]` (`else` branch, $\hat s_2 = 23550$ kept at
61.15) — `skippedEntries[1]`, the 23450 candles begin at 10:00; second
phantom, and the two phantoms' 6 + 8 legs are the "14 close legs ignored".
From 10:00 to 12:25 no close was 45 from the centre: 10:20's 37.8 and
10:25's 39.65 above 23,500 held because 23550 was in place (the 0.5 and 0.7
twins rebuilt there), 11:55's 11.5 below held because 23450 was in place,
12:05's 25.05 likewise. With nothing actually open, the run had no exposure
for the first 3 h 15 min of the session.

### Signal 1638 — 12:30 bar (C 23547.85) → orders 3082–3087 → positions 1552–1557

47.85 above $s_1 = 23500$ ≥ 45: $K^* = 23550 > S$, `else`, $s_0 = 23500$,
$s_1 = 23550$, $s_2 = 0$ (23450 dropped) → `Active: [23500, 23550]`, wings
$\lfloor 23547.85 \times 0.965 / 50 \rfloor \times 50 = 22700$ and
$\lceil 23547.85 \times 1.035 / 50 \rceil \times 50 = 24400$. First real
fills, at the 12:30 candle closes: `22700PE` BUY 5.30, `24400CE` BUY 3.40,
`23500CE` SELL 179.35, `23500PE` SELL 81.35, `23550CE` SELL 149.30, `23550PE`
SELL 99.95, 1 lot each — sold at the close of a 37-point up-bar.

### Signals 1639 / 1640 — 12:35 bar (C 23557.45) → orders 3088–3101 → positions 1558–1565

7.45 above $s_1 = 23550$ with **no** straddle above ($s_2 = 0$): past $M$,
neighbour test fails, rebuild: $K^* = 23550 < S$, `if st1 < price`:
$s_1 = 23550$, $s_2 = 23600$, and $\hat s_0 = 23500$ is 57.45 from the spot,
under 75, kept → `Active: [23500, 23550, 23600]`. Group 003 closed after one
bar — position 1554, `23500CE` SHORT:

$$
(179.35 - 189.70) \times 1 \times 65 = -10.35 \times 65 = -672.75
$$

1555 `23500PE` $(81.35 - 78.45) \times 65 = 188.50$; 1556 `23550CE`
$(149.30 - 158.55) \times 65 = -601.25$; 1557 `23550PE`
$(99.95 - 96.65) \times 65 = 214.50$; wings $-3.25$, $-9.75$: **−884.00**.
Group 004 re-sold the four legs at those closes and added `23600CE` SELL
129.45 and `23600PE` SELL 118.15 (orders 3094–3101; wings unchanged).

### Group 004 — 12:35 to 14:35, the 0.9 threshold at work → positions 1558–1565

Held 120 minutes. Every close to 14:25 was within 45 of 23,550; 14:30 (C
23511.55) was 38.45 below with the 23500 straddle in place — under 45, hold,
where both the 0.5 and 0.7 twins rolled. 14:35 (C 23492.85) was 57.15
below: `else`, $s_0 = 23450$, $s_1 = 23500$, and $\hat s_2 = 23600$ is
107.15 away, not under 75, so it is dropped → `Active: [23450, 23500]`
(signals 1641 / 1642). Closed at the 14:35 closes: 1560 `23500CE`
$(189.70 - 150.80) \times 65 = 2528.50$, 1562 `23550CE`
$(158.55 - 123.20) \times 65 = 2297.75$, 1564 `23600CE`
$(129.45 - 98.75) \times 65 = 1995.50$, against 1561 `23500PE`
$(78.45 - 95.60) \times 65 = -1114.75$, 1563 $-1332.50$, 1565 `23600PE`
$(118.15 - 142.80) \times 65 = -1602.25$; wings 1558 $(5.50 - 5.25) \times
65 = 16.25$, 1559 $(3.65 - 3.25) \times 65 = 26.00$: **+2,814.50**. Group
005 sold `23450CE` 182.75, `23450PE` 77.15, `23500CE` 150.80, `23500PE`
95.60 with wings 22650 / 24350 at 5.10 / 4.05 (orders 3110–3115).

### Signal 1643 — 14:50, "End of backtest" → orders 3116–3121 → positions 1566–1571

The last driver bar; `square_off: true`, `atm_strike` null. Group 005 closed
at the 14:50 closes: 1568 `23450CE` $(182.75 - 183.70) \times 65 = -61.75$,
1569 $+65.00$, 1570 $-42.25$, 1571 $+55.25$, wings $+9.75$, $-9.75$:
**+16.25**.

Day total, run 114: −884.00 + 2,814.50 + 16.25 = **₹1,946.75** (sum of
`RealizedPnl` over positions 1552–1571, no charges). Three groups where the
0.5 twin had sixteen and the 0.7 twin nine, on the same bars: the wider
threshold held group 004 through the 14:30 dip the others rolled on, and
sat out the morning entirely.

## Limitations

- **Never flat, never aware.** The strategy holds one group all day and
  replaces it in place; it cannot see a leg closed by a risk rule, a group
  closed at 15:30, a refused open or a stop button. After any of those its
  `state` describes a position that does not exist until the next rebuild
  re-enters (see Position management). Run 114's "14 close legs
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
  side that has no straddle (run 114 at 09:55 and signal 1640: 11.15 and 7.45 points on a 45-point threshold). Raising
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
  stamped `FulcrumMultiStraddle90` whether it sells or buys; only
  `simulation_runs.StrategyName` and the run parameters tell `FulcrumMulti90` from
  `FulcrumMultiBuy90` in `simulation_signals` / `paper_positions`.
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
- **0.9 of a strike is 45 NIFTY points, and the spot can sit 44 off-centre
  for hours.** On 2026-09-09 that meant no real position until 12:30 — the
  09:15 and 09:55 groups were skipped for missing option candles and nothing
  after them crossed 45 points from the centre with a neighbour straddle in
  place — and then two groups that between them carried the whole
  ₹1,946.75. The `minor_steps` add is unchanged by the wider threshold: both
  of run 114's real rebuilds came from it (signal 1640, 7.45 points) or from
  the one 57-point move of the afternoon (signal 1642), not from a 45-point
  drift. A first group sold near a bar's high (signal 1638, after a 37-point
  bar) is held through the next 45 points either way.
- **Dead code.** `ce_value` / `pe_value` are computed and never read;
  `state["straddle_list"]` is initialised and reset but never written;
  `target_steps` is resolved and never used by this class.

## Facts (machine-readable)

```yaml
name: FulcrumMulti90
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
FROM simulation_runs WHERE "Id" = 114;

/* signals 1638-1644 (the last is BACKTEST_SUMMARY with skippedEntries and dataNotes) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 114 ORDER BY "Id";

/* orders 3082-3121 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_orders WHERE "SimulationRunId" = 114 ORDER BY "Id";

/* positions 1552-1571, per group and in total (1946.75) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata', "ClosedUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_positions WHERE "SimulationRunId" = 114 ORDER BY "Id";
SELECT "GroupId", count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 114 GROUP BY 1 ORDER BY 1;

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
