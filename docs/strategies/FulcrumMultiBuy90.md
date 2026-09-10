# FulcrumMultiBuy90

Source: `strategies/fulcrum/fulcrum_multi.py` (`FulcrumMultiStraddleStrategy`),
registered as `FulcrumMultiBuy90` by `strategies/variants.py` with
`adjustment_steps=0.9`, `minor_steps=0.1`, `direction="BUY"`. It is the
long twin of `FulcrumMulti90`: the same class, the same strike logic, every
straddle leg bought instead of sold, no wings, and a time stop. The class's
own registration (`FulcrumMultiStraddle`) and the other thresholds have their
own pages. Strike arithmetic is `strategies/strike_math.py`, the
direction/wings switch `strategies/fulcrum/_direction.py`, the time stop
`strategies/fulcrum/_exit_rules.py`, lots `strategies/base_strategy.py`;
contract plumbing is `strategies/execution_runner.py` (live) and
`backtest/engine.py` + `backtest/contracts.py` (replay). Every rule below is
read from the code; where the code and a docstring disagree, the code is
documented and the difference listed under Limitations. Times are IST
(UTC + 5:30); the database stores UTC.

## Idea

Buy the straddles on the two strikes that bracket the spot (23,500 and
23,550 for a NIFTY spot of 23,518) and hold them for a move. Premium is paid
up front — ₹33,286.50 a lot for the two NIFTY straddles run 121 first
bought — and time decay works against the position, so it needs the underlying to travel;
a range-bound session is its losing case. It re-centres the way its short
twin does: once the spot is 0.9 of a strike (45 NIFTY points, 90
BANKNIFTY points) from the centre strike the group is closed and rebuilt
around the new level, which for a buyer realises whatever the move gave and
buys fresh at-the-money time value; and a group that has gone nowhere for
`max_hold_minutes` (45) is closed by a time stop and re-bought on the next
evaluation. There are no wings: a long option's loss is already capped at
the premium paid (`_direction.py`). Nothing in this repository tests that
this pays; the one session cited below lost money.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none — `get_data_requirements` is not overridden (`[]`), so the runner does no warm-up and the first tick is evaluated | live only: ingestor → Redis stream `market:ticks`; each spot tick is one call of `on_bar` (`execution_runner.py`, the `listen_for_ticks` loop) |
| index candles | the same spot symbol | the run's resolution (5m in run 121) | none (`BacktestSession.warms_up` is false) | replay only: `candles` drives the loop, one call of `on_bar` per session bar with `spot_price` = the bar's close; the strategy never reads `inp.bars` |
| option quotes / candles | every leg the strategy names itself: CE and PE at each active straddle strike (4 or 6 legs), all of the first expiry on or after today | live: latest quote; replay: the leg's candle at the run's resolution | none | live: `enrich_signal_leg_prices` puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for quotes; replay: `candles` for the contract (`HistoricalFeed.option_close_at`), synced from FYERS history while the contract exists |
| strike step | the option chain of the chosen expiry | once per run | — | live: smallest gap between strikes (`resolve_strike_step` → `strike_step_from_chain`; 50 for NIFTY, 100 for BANKNIFTY), else `FALLBACK_STRIKE_STEPS`; replay: `ContractResolver.step_for`; a run parameter `strike_step` overrides both (`strike_math.resolve_step`) |
| the clock | `inp.timestamp_utc` | every evaluation | — | the time stop compares it with the group's `group_entry_utc` (`_exit_rules._parse_utc`); live that is the tick's timestamp, replay the bar's start |

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
  thresholds are tested against the last traded price and the time stop
  against the tick's timestamp; a group can be rebuilt any number of times
  within a minute. There is no session window and no last-entry time: a run
  started mid-session buys on its first tick. `FulcrumMultiBuy90` itself has only been
  run as a replay.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`HistoricalFeed.driver_bars`,
  09:15 ≤ start < 15:30). The spot is the bar's close, so a threshold crossed
  inside a bar is seen only if the close is still past it; the time stop
  fires on the first bar whose start is ≥ 45 minutes after the group's bar —
  70 minutes in run 121 across the missing 13:20–13:40 bars. Fills are the
  close of each leg's candle for that bar (`option_close_at`), and a signal is
  stamped with the bar's start time (10:20 for the 10:20–10:25 bar).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30 on
  weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. In a replay the engine
  squares off at `eod_square_off_ist` (15:15 by default) and, if positions
  are still open on the last driver bar, at that bar with reason "End of
  backtest" — which is what closed run 121, whose candles end at 14:50.
  The strategy's own closes (the rebuild, the time stop) never leave it flat
  for more than one evaluation, so the day's exposure ends only there.

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
N = 1.5\,\Delta,\qquad
H = \texttt{max\_hold\_minutes} = 45 .
$$

For this variant that is $A = 45$, $M = 5$, $N = 75$ points on NIFTY's
50-point grid; $A = 90$, $M = 10$, $N = 150$ on BANKNIFTY's 100; $A = 22.5$,
$M = 2.5$, $N = 37.5$ on MIDCPNIFTY's 25. The "90" in the name is the
BANKNIFTY value; `variants.py` keeps the buy thresholds equal to the sell
twin's "so the two can be compared on identical data". The state is three
straddle strikes $(s_0, s_1, s_2)$ — lower, centre, upper — with 0 meaning
"none", plus the open group's id, legs and entry time. If $K^* = S$ exactly
the call returns without doing anything (`if atm == price: return []`).

### First group (`on_bar`, $s_1 = 0$)

On the first evaluation (and on the first one after a time stop, which
resets the state) the gate below cannot hold, $s_1$ is left at 0 so the
`st1 < price` branch runs:

$$
s_1 \leftarrow K^-,\qquad s_2 \leftarrow K^+,\qquad s_0 \leftarrow 0 .
$$

The group is the straddles at $K^-$ and $K^+$ — the two strikes bracketing
the spot — each as a BUY CE and a BUY PE of `lots` lots: four legs, no
wings. `resolve_direction` reads `direction` (`BUY` / `LONG` / `B`) and,
because `use_hedges` is not passed, sets `use_hedges = (direction == SELL)`
= false, so the wing legs the short twin adds are simply not appended. A
run parameter `use_hedges: true` would add them.

In words: buy the straddle on the strike just below the spot and the one
just above. What that costs is the sum of four premiums — run 121's first
real group paid 512.10 points a lot, ₹33,286.50 at lot size 65 —
and that is also the most the group can lose.

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
  the leg's candle for the signal bar; no slippage, `charges_per_lot` 0 by
  default.
- **Signal names:** the strategy stamps its rebuild signals
  `strategy_name = f"{name}{variant_label}"` = `FulcrumMultiStraddle90` —
  the same string the short twin uses — and its time-stop closes plain
  `FulcrumMultiStraddle` (`long_time_stop_close` is given `self.name`); group
  ids `FULCRUM-MULTI-<timestamp>-<counter>`; reasons
  `Fulcrum Multi Straddle Adjusted. Active: [strikes]`, `Adjusting straddles`
  and `Time stop: held N minutes without the move this position was opened
  for; decay is the only thing working.` (with `exit: time_stop` in the
  metadata). The engine adds `spot_price` and `atm_strike`; its own closes
  carry the registry name `FulcrumMultiBuy90`.

## Position management

Every evaluation runs, in this order: the time stop, then the gate, then the
rebuild. Let $\hat s_0 = s_0$ if $s_0 \ne 0$ else $s_1$, and $\hat s_2 = s_2$
if $s_2 \ne 0$ else $s_1$ (the code writes these placeholders into `st0` /
`st2` before testing), and let $t_0$ be `group_entry_utc`, the timestamp of
the last `OPEN_GROUP`.

### The time stop — checked first (`long_time_stop_close`)

$$
\text{close} \iff \texttt{direction} = \text{BUY} \ \land\ \text{a group is open} \ \land\ t - t_0 \ge H .
$$

When it fires the method emits one `CLOSE_GROUP` with every open leg
reversed (`closing_legs`), sets $s_0 = s_1 = s_2 = 0$, forgets the group and
**returns without evaluating the gate**. The next evaluation therefore
starts from scratch and buys the two bracketing straddles again — one bar
later in a replay, the next tick live. $t_0$ is reset by every rebuild, so
the 45 minutes count from the last re-centring, not from the first entry.
It is the only rule this class takes from `_exit_rules.py`: `target_steps`
is resolved (`resolve_long_exit`) and never read.

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
$M$ past $s_1$ falls through to a rebuild — that is how a third straddle is
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

So the two strikes bracketing the spot are always held, and at most one
more: the strike one step beyond $K^*$ on the side away from the spot, and
only when a straddle was already there — the previous outer one, or the
previous centre when the spot has just crossed into the next interval (the
placeholder makes $s_1$ the candidate). With $N = 1.5\Delta$ strict no other
strike can pass the test. Everything else is dropped. `active_straddles` is
$\{s_i > 0\}$ in ascending order (the reason text lists it).

### The group is rebuilt whenever the straddle set differs

The new leg list is BUY CE + BUY PE at each active strike. `_legs_differ`
compares the sets of (symbol, side, lots) with the open group's legs:

- different → a `CLOSE_GROUP` with every open leg reversed (reason
  "Adjusting straddles"), then an `OPEN_GROUP` with the new legs, both
  stamped with the same timestamp, spot and a new group id, and $t_0$ reset.
  The strategy never sells one straddle: it flattens everything it holds and
  re-buys what it wants.
- identical → nothing is emitted; $(s_0, s_1, s_2)$ are still updated. With
  no wings this is common: a pass that leaves the straddle set alone is
  silent, where the short twin would rebuild for a moved wing.

**What a roll costs a buyer.** The old group is sold at the current quotes,
so the move's gain on one side and its loss on the other are realised, and
the new group is bought at the same quotes: the straddles kept are re-bought
at the price they were just sold for (in a replay a wash, live the bid–ask
spread on every leg, twice), and the straddle added is bought at or next to
the money, where time value is highest. A rebuild that only drops a strike
still re-buys the rest. In run 121 the 12:30 roll sold `[23450, 23500,
23550]` and bought `[23500, 23550]` back at exactly the sale prices — its
only effect was to let the 23450 straddle go.

### What the run's risk rules add

`parametersJson.risk` carries three levels that `StrategyRiskGuardService`
sweeps every `RiskGuardIntervalSeconds` (3 s, `appsettings.json`) in the
order leg → group → overall, each level stop-loss → trailing stop → target:

- `leg` — per open position, premium points or percent of `AveragePrice`
  against the last mark; a trip closes that leg only, leaving the other half
  of that straddle alone and the strategy unaware.
- `group` — rupees on a signal group's realized + unrealized P&L; closes
  every leg of that group — the whole position, since it holds one group at
  a time.
- `overall` — rupees on the run's total P&L; flattens everything and stops
  the run. Its `scope` (`day` default, or `run`) is honoured by the backtest
  engine; the live guard measures since the run started.

The backtest engine mirrors the same rules after the marks of every bar
(`BacktestSession._check_risk`). Run 121 set none (`"risk": {}`;
`StopLossPrice` / `TargetPrice` null on all its positions). A premium-based
target or stop is therefore a run parameter, not something this class
provides — `_exit_rules.py` says so explicitly.

### What the strategy never does

- It never stays flat: a rebuild re-buys in the same call, and the time stop
  is followed by a fresh purchase on the next evaluation (run 121: closed
  11:30, re-bought 11:35). Without a group / overall rule,
  the stop button, the 15:30 close or the replay's square-off, premium is
  being paid all day.
- It never sets a per-position stop or target.
- It never learns that the platform closed something. A group closed by a
  risk rule, the 15:30 sweep or the stop button stays in `state` as open; the
  next rebuild's or time stop's `CLOSE_GROUP` names legs that are flat (the
  API's reduce-only path ignores them, `ApplyPositionAsync` returns false;
  the replay ledger counts them as "close legs ignored") and its next
  `OPEN_GROUP` is a fresh entry. The same happens after a refused or skipped
  open: the group is recorded in `state` before the signal leaves `on_bar`,
  and its clock starts — run 121 carried such phantoms from 09:15 (see the
  worked example).
- Live, `state` is saved to Redis after each signal and recovered on a
  runner restart (`StrategyStateStore`, warm-up skipped), so a restart
  carries $(s_0, s_1, s_2)$, the last group id and $t_0$ forward.

## Exit

In order of precedence for one position:

1. **The time stop** — `CLOSE_GROUP` "Time stop: held N minutes …" on the
   first evaluation ≥ 45 minutes after the group's `OPEN_GROUP`; checked
   before anything else in `on_bar`, closes at the latest quote (live, 0 s
   wait) or the bar's candle close (replay). The next evaluation re-enters.
2. **The strategy's own rebuild** — `CLOSE_GROUP` "Adjusting straddles" on
   the tick/bar the gate falls through and the straddle set differs; the new
   group opens in the same call.
3. **Leg rules** (`EvaluateLeg`): stop-loss points / percent, trailing stop
   points / percent, target points / percent — close that leg only.
4. **Group rules** (`EvaluateGroup`) on the group's P&L.
5. **Overall rules** (`EvaluateOverall`) — flatten and stop the run.
6. Outside the sweep, on their own clocks, whichever takes the run lock first:
   a manual square-off or the run's stop button; market close by
   `MarketHoursService` at 15:30 IST.
7. Replay only: `eod_square_off_ist` (15:15 by default) and "End of backtest"
   on the last driver bar (run 121, signal 1728 at 14:50).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.9 (this variant; the class default is 0.5) | $A/\Delta$: how far (in strikes) the spot must be from the centre strike before the group is rolled, when a straddle already sits on that side. 0.9 = 90 pts BANKNIFTY, 45 pts NIFTY | Fewer rolls, so a move is allowed to run further before it is realised and the time stop decides more often; at ≥ 1.0 the spot can reach the neighbouring strike first | More rolls, each re-buying at-the-money time value and paying the spread live; below `minor_steps` the minor rule dominates |
| `minor_steps` | 0.1 | $M/\Delta$: moves under this are ignored outright; a move past it on a side with **no** straddle rebuilds and adds one | The third straddle is added later | On NIFTY (5 pts) the empty-side add fires on noise |
| `adjustment_threshold`, `minor_threshold` | — | legacy spellings in points on the 100-point grid; read only when the `_steps` key is absent and divided by `LEGACY_GRID` (100) (`steps_from_params`) | as above | as above |
| `direction` | `BUY` (this variant passes it) | every straddle leg is a BUY (`resolve_direction`); leaving it out gives `FulcrumMulti90` | n/a | n/a |
| `use_hedges` | `false` when buying | `true` adds the short twin's two wings (BUY legs 3.5 % out) to a long group — extra premium, no protection to speak of | n/a | n/a |
| `max_hold_minutes` | 45 | $H$: a group held this long is closed by the time stop and re-bought next evaluation (`resolve_long_exit`; values ≤ 0 or unreadable fall back to 45) | Fewer time stops, more decay carried; at ≥ 375 the stop never fires in a session | More churn: close, re-buy at the same quotes, spread paid live |
| `target_steps` | 2.0 | resolved by `resolve_long_exit` but **never read** by this class (only `fulcrum_standard.py` uses `long_exit_reason`) | no effect | no effect |
| `strike_step` | from the chain | overrides the grid (`resolve_step`); wrong values name contracts that do not exist | — | — |
| `lots` (`quantity`) | `default_lots` = 1 | lots per leg on all 4–6 legs; P&L = points × lots × lot size | larger position, same signals | — |

Not parameters: the 1.5-strike keep rule (the literal `1.5` in `on_bar`)
and the time-stop constants' fallbacks (`DEFAULT_MAX_HOLD_MINUTES` 45,
`DEFAULT_TARGET_STEPS` 2.0).

## Worked example

Run **121** — OfflineReplay, `NSE:NIFTY50-INDEX`, 5m, 2026-09-09 (one
session), `ParametersJson` verbatim: `{"adjustment_steps":0.9,"minor_steps":0.1,
"direction":"BUY","lots":1,"underlying":"NIFTY","resolution":"5m",
"eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,
"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}` —
`max_hold_minutes` absent, so 45. Lot size 65 (`instruments.LotSize` for
`NSE:NIFTY2691523500CE`), expiry 2026-09-15, grid 50 → $A = 45$, $M = 5$,
$N = 75$. 63 driver bars 09:15–14:50; 17 signals (8 `OPEN_GROUP`, 5
"Adjusting straddles" closes, 2 time stops, the engine's "End of backtest",
one `BACKTEST_SUMMARY`), 76 orders, 38 positions, `realizedPnl` −61.75.
(Run 117, the same parameters four minutes earlier, failed before its first
bar — the engine could not load the run, API login answered 429 — and was
re-run as 121.) Every close below is the 5m index close the strategy saw (`candles`), every
fill the leg's candle close for the same bar.

### 09:15 to 10:40 — two phantoms, and a time stop on nothing

09:15 (C 23500.90): `Active: [23500, 23550]`, four BUY legs, skipped —
`skippedEntries[0]`, no premium history for the 23550 pair before 09:35 —
with `state` holding group `…-001` as open. 09:35 (26.25 above) held (under
45). 09:55 (C 23488.85): 11.15 below $s_1 = 23500$ with **no** straddle
below — past $M$, neighbour test fails — `else`, $s_0 = 23450$, $s_1 =
23500$, $\hat s_2 = 23550$ kept (61.15 < 75) →
`Active: [23450, 23500, 23550]`, skipped (`skippedEntries[1]` at 04:25 UTC;
the 23450 candles begin at 10:00), and $t_0$ = 09:55 for a group that did
not exist. 10:20's 37.8 above (where the 0.5 and 0.7 twins bought) held here
with 23550 in place. At 10:40 (C 23509.75) the phantom was 45 minutes old:
the time stop emitted a `CLOSE_GROUP` whose six legs found no position (the
ledger ignored them — with the first phantom's four, the "10 close legs
ignored"), so it was not posted and does not appear in `simulation_signals`;
the state was reset all the same.

### Signal 1713 — 10:45 bar (C 23518.35) → orders 3446–3449 → positions 1734–1737

Fresh state, the first-group rule: $K^- = 23500$, $K^+ = 23550$ →
`Active: [23500, 23550]`. First real fills, at the 10:45 candle closes:
`23500CE` BUY 165.60, `23500PE` BUY 94.45, `23550CE` BUY 136.65, `23550PE`
BUY 115.40, 1 lot each — 512.10 points, ₹33,286.50 paid. $t_0$ = 10:45. Every
bar to 11:25 was under 45 from 23,500 (10:55's 35.6 above, which passed the
0.7 twins' gate, and 11:00's 36.95 held here with 23550 in place).

### Signal 1714 — 11:30 bar (C 23515.85), time stop → orders 3450–3453

$11{:}30 - 10{:}45 = 45$ minutes: "Time stop: held 45 minutes without the
move this position was opened for; decay is the only thing working."
(`StrategyName` `FulcrumMultiStraddle`, `exit: time_stop`). Sold at the
11:30 closes — position 1734, `23500CE` LONG:

$$
(167.75 - 165.60) \times 1 \times 65 = 2.15 \times 65 = 139.75
$$

1735 `23500PE` $(92.10 - 94.45) \times 65 = -152.75$; 1736 `23550CE`
$(138.00 - 136.65) \times 65 = 87.75$; 1737 `23550PE`
$(112.65 - 115.40) \times 65 = -178.75$: group 003 = **−104.00**. The spot
ended 2.5 points from where it started. State reset; 11:35 (signal 1715,
C 23517.20) re-bought `[23500, 23550]` at 166.10 / 91.80 / 136.75 / 112.10.

### Signals 1716 / 1717 — 11:55 bar (C 23488.50), a straddle added after 5 points → orders 3458–3467 → positions 1738–1747

11.5 below $s_1 = 23500$ with **no** straddle below: `else`, $s_0 = 23450$,
$s_1 = 23500$, $\hat s_2 = 23550$ kept (61.5 < 75) →
`Active: [23450, 23500, 23550]`. Group 004 sold at the 11:55 closes: 1738
`23500CE` $(146.30 - 166.10) \times 65 = -1287.00$, 1740 `23550CE`
$(119.85 - 136.75) \times 65 = -1098.50$, against 1739 `23500PE`
$(105.45 - 91.80) \times 65 = 887.25$, 1741 $+1033.50$: **−464.75** after a
28.7-point fall. Group 005 re-bought those four at the same closes and added
`23450CE` 177.20, `23450PE` 85.40 — six legs. 12:05 (25.05 below 23,500 with
23450 in place), 12:10 (12.2 below) and 12:25 (10.6 above with 23550 in
place) all held — under 45 — where `FulcrumMultiBuy50` (run 115) rolled at
12:25.

### Signals 1718 / 1719 — 12:30 bar (C 23547.85), the move arrives → orders 3468–3477 → positions 1748–1751

47.85 above $s_1 = 23500$ ≥ 45: `else`, $s_0 = 23500$, $s_1 = 23550$,
$s_2 = 0$ → `Active: [23500, 23550]`. Group 005 sold at the 12:30 closes:
1742 `23450CE` $(213.80 - 177.20) \times 65 = 2379.00$, 1744 `23500CE`
$(179.35 - 146.30) \times 65 = 2148.25$, 1746 `23550CE`
$(149.30 - 119.85) \times 65 = 1914.25$, against 1743 `23450PE`
$(65.40 - 85.40) \times 65 = -1300.00$, 1745 $-1566.50$, 1747 $-1823.25$:
**+1,751.75**, the run's best group — 35 minutes and 59 points from its
entry, realised by the roll. Group 006 bought `23500CE` 179.35, `23500PE`
81.35, `23550CE` 149.30, `23550PE` 99.95 — exactly the sale prices — so the
roll only dropped the 23450 straddle. 12:35 (7.45 above 23,550 with nothing
above) added 23600 (signals 1720 / 1721; group 006 **+871.00**).

### Signal 1722 — 13:45 bar (C 23553.50), a 70-minute time stop → orders 3488–3493 → positions 1752–1757

Group 007 (`[23500, 23550, 23600]`, $t_0$ = 12:35). 13:15 was 40 minutes in;
13:20–13:40 do not exist in `candles`; 13:45 is the first bar ≥ 45 minutes:
"Time stop: held 70 minutes …". The spot was 3.95 from the entry: 1752
`23500CE` $(180.90 - 189.70) \times 65 = -572.00$, 1754 $-588.25$, 1756
`23600CE` $(121.45 - 129.45) \times 65 = -520.00$, against 1753 $+48.75$,
1755 $+65.00$, 1757 $+81.25$: **−1,485.25**, the run's worst. 13:50
re-bought `[23500, 23550]` (signal 1723); 13:55 (C 23551.95, 51.95 above
23,500 ≥ 45) rolled to `[23500, 23550, 23600]` (1724 / 1725, group 008
**+659.75**, positions 1758–1761).

### Signals 1726 / 1727 — 14:35 bar (C 23492.85), the 0.9 threshold at work → orders 3508–3517 → positions 1762–1767

14:30 (C 23511.55) was 38.45 below $s_1 = 23550$ with the 23500 straddle in
place — under 45, hold, where the 0.5 and 0.7 twins rolled. 14:35 was 57.15
below: `else`, $s_0 = 23450$, $s_1 = 23500$, and $\hat s_2 = 23600$ is
107.15 from the spot, not under 75, dropped → `Active: [23450, 23500]`.
Group 009 (opened 13:55) sold at the 14:35 closes: 1762 `23500CE`
$(150.80 - 182.20) \times 65 = -2041.00$, 1764 `23550CE` $-1790.75$, 1766
`23600CE` $(98.75 - 122.50) \times 65 = -1543.75$, against 1763 `23500PE`
$(95.60 - 78.05) \times 65 = 1140.75$, 1765 $+1348.75$, 1767 `23600PE`
$(142.80 - 118.00) \times 65 = 1612.00$: **−1,274.00** — the 59-point fall
from 23,551.95 paid the puts less than the calls lost. Group 010 bought
`23450CE` 182.75, `23450PE` 77.15, `23500CE` 150.80, `23500PE` 95.60.

### Signal 1728 — 14:50, "End of backtest" → orders 3518–3521 → positions 1768–1771

The last driver bar; `square_off: true`, `atm_strike` null. Group 010 sold
at the 14:50 closes: 1768 `23450CE` $(183.70 - 182.75) \times 65 = 61.75$,
1769 $-65.00$, 1770 $+42.25$, 1771 $-55.25$: **−16.25**.

Day total, run 121, over groups 003–010: −104.00 − 464.75 + 1,751.75 +
871.00 − 1,485.25 + 659.75 − 1,274.00 − 16.25 = **₹−61.75** (sum of
`RealizedPnl` over positions 1734–1771, no charges). Eight groups against
the 0.5 and 0.7 twins' ten (runs 115 and 116, −809.25 each): the wider
threshold skipped the 10:20 entry and its 11:05 time stop, and held the
13:55 group through the 14:30 dip — for a smaller loss, not a profit.

## Limitations

- **The roll cuts winners; only the time stop is new.** `_exit_rules.py`'s
  module docstring says that closing a long straddle when the ATM changes
  "cuts the trade at the moment it starts working" and that a buyer should
  exit on `target_steps` or `max_hold_minutes` instead; this class keeps the
  seller's re-centring as its exit (`long_time_stop_reason`'s own docstring
  calls that "the winner being realised") and adds the time stop only.
  `target_steps` is accepted and ignored. Run 121's best group (signal
  1718, +1,751.75) was realised by a roll after 35 minutes; whether holding
  would have paid more is not something the code or the data can say.
- **Never flat, never aware.** After a time stop the next evaluation re-buys
  (run 121: 11:30 → 11:35, 13:45 → 13:50); the strategy cannot see a leg closed by a
  risk rule, a group closed at 15:30, a refused open or a stop button, and
  after any of those its `state` describes a position that does not exist —
  and its clock keeps running on it. Run 121's "10 close legs
  ignored: no matching open position" (`dataNotes`) are the closes of its
  two skipped opens, and one of those phantoms was even "closed" by the time stop at 10:40 — a signal with nothing to close, which the engine did not post.
- **Decay is paid on schedule, and the time stop is not a stop-loss.** A
  group is closed after 45 minutes whatever its P&L; nothing closes a group
  early for losing, and nothing holds a winner past a roll. Premium-based
  stops and targets are the run's risk rules, per `_exit_rules.py`.
- **A third straddle is added after `minor_steps`, not `adjustment_steps`.**
  The docstring says small moves are ignored and the group is rebuilt past
  the adjustment threshold; the code rebuilds after a 5-point move toward a
  side that has no straddle (run 121 at 09:55 and signals 1716, 1720: 11.15, 11.5 and 7.45 points on a 45-point threshold) and buys another straddle at
  the money. Raising `adjustment_steps` does not change this.
- **"Within 1.5 strikes" means one strike.** The docstring's "keeping a
  nearby straddle when it is still within 1.5 strikes" can only ever keep the
  strike adjacent to the nearest strike on the side away from the spot (see
  Re-centring); the group is two or three straddles on consecutive strikes.
- **The time stop counts bar starts in a replay.** It fires on the first bar
  whose start is ≥ 45 minutes after the group's bar, so a hole in the candles
  stretches it (70 minutes at 13:45 in run 121, across the missing
  13:20–13:40 bars); live it counts tick timestamps.
- **The state is not persisted anywhere a reviewer can read.**
  `simulation_signals.Symbol` is empty and `Price` null; `MetadataJson`
  carries `group_id`, `reason` (with the `Active:` list), `spot_price`,
  `atm_strike` and, on a time stop, `exit: time_stop` — not
  $(s_0, s_1, s_2)$ or $t_0$, which have to be rebuilt from the code and the
  bar closes as the worked example does.
- **Signal names do not carry the direction.** Rebuild signals are stamped
  `FulcrumMultiStraddle90`, exactly as the short twin's; only
  `simulation_runs.StrategyName` and `direction` in the run parameters tell
  `FulcrumMultiBuy90` from `FulcrumMulti90` in `simulation_signals` /
  `paper_positions`. Time-stop closes are stamped `FulcrumMultiStraddle`.
- **The 2026-09-09 replay data.** The day's 5m index candles came from the
  live ingestor (`candles.SourceKey = 'live'`, 70 bars 08:40–14:50), of which
  63 are session bars — 13:20 to 13:40 are missing and nothing exists after
  14:50, so the 15:15 square-off never came and "End of backtest" closed the
  last group at 14:50. The 23500 option candles start 09:15, the 23550 ones
  09:35 and the 23450 ones 10:00 (also `live`), which is why the first two
  groups were skipped. Contracts that have expired have no broker history at
  all.
- **Replay differs from live.** Bar closes only (a threshold crossed and
  un-crossed inside a bar is invisible; live, every tick is tested), fills at
  the option candle close of the signal bar, no slippage, `charges_per_lot`
  0 — so the replay's rolls and re-buys after a time stop cost nothing,
  where live each is the spread on four to six legs. Live, a refused open
  (no quote in 10 s) leaves the phantom group described above. Ticks exactly
  on a strike are ignored (`atm == price`).
- **0.9 of a strike is 45 NIFTY points, so the time stop does most of the
  closing.** Between rolls the spot has to travel almost a full strike from
  the centre; on 2026-09-09 only three bars did (12:30, 13:55, 14:35), the
  other two rolls came from the 5-point add (11:55, 12:35), and two of the
  eight groups ended by time stop, one by the end of the data. The wider
  threshold
  also left the first phantom group's clock running to 10:40 and the first
  real entry at 10:45, 25 minutes after the 0.5 / 0.7 twins.
- **Dead code.** `ce_value` / `pe_value` are computed and never read;
  `state["straddle_list"]` is initialised and reset but never written;
  `target_steps` is resolved and never used by this class.

## Facts (machine-readable)

```yaml
name: FulcrumMultiBuy90
category: Adjustment
evaluates_on: tick
resolution: any
data: ticks, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: true           # 45-minute time stop (max_hold_minutes) and the roll; it re-enters on the next evaluation
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status",
       "FromUtc" AT TIME ZONE 'Asia/Kolkata', "ToUtc" AT TIME ZONE 'Asia/Kolkata', "ParametersJson"
FROM simulation_runs WHERE "Id" = 121;

/* signals 1713-1729 (time stops carry StrategyName FulcrumMultiStraddle and exit: time_stop;
   the last is BACKTEST_SUMMARY with skippedEntries and dataNotes) */
SELECT "Id", "StrategyName", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 121 ORDER BY "Id";

/* orders 3446-3521 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_orders WHERE "SimulationRunId" = 121 ORDER BY "Id";

/* positions 1734-1771, per group and in total (-61.75) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata', "ClosedUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_positions WHERE "SimulationRunId" = 121 ORDER BY "Id";
SELECT "GroupId", count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 121 GROUP BY 1 ORDER BY 1;

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
