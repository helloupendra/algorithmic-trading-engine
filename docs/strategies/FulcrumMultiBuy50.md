# FulcrumMultiBuy50

Source: `strategies/fulcrum/fulcrum_multi.py` (`FulcrumMultiStraddleStrategy`),
registered as `FulcrumMultiBuy50` by `strategies/variants.py` with
`adjustment_steps=0.5`, `minor_steps=0.1`, `direction="BUY"`. It is the
long twin of `FulcrumMulti50`: the same class, the same strike logic, every
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
23,550 for a NIFTY spot of 23,538) and hold them for a move. Premium is paid
up front — ₹34,199.75 a lot for the two NIFTY straddles run 115 first
bought — and time decay works against the position, so it needs the underlying to travel;
a range-bound session is its losing case. It re-centres the way its short
twin does: once the spot is 0.5 of a strike (25 NIFTY points, 50
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
| index candles | the same spot symbol | the run's resolution (5m in run 115) | none (`BacktestSession.warms_up` is false) | replay only: `candles` drives the loop, one call of `on_bar` per session bar with `spot_price` = the bar's close; the strategy never reads `inp.bars` |
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
  started mid-session buys on its first tick. `FulcrumMultiBuy50` itself has only been
  run as a replay.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`HistoricalFeed.driver_bars`,
  09:15 ≤ start < 15:30). The spot is the bar's close, so a threshold crossed
  inside a bar is seen only if the close is still past it; the time stop
  fires on the first bar whose start is ≥ 45 minutes after the group's bar —
  70 minutes in run 115 across the missing 13:20–13:40 bars. Fills are the
  close of each leg's candle for that bar (`option_close_at`), and a signal is
  stamped with the bar's start time (10:20 for the 10:20–10:25 bar).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30 on
  weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote. In a replay the engine
  squares off at `eod_square_off_ist` (15:15 by default) and, if positions
  are still open on the last driver bar, at that bar with reason "End of
  backtest" — which is what closed run 115, whose candles end at 14:50.
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
A = \texttt{adjustment\_steps}\cdot\Delta = 0.5\,\Delta,\qquad
M = \texttt{minor\_steps}\cdot\Delta = 0.1\,\Delta,\qquad
N = 1.5\,\Delta,\qquad
H = \texttt{max\_hold\_minutes} = 45 .
$$

For this variant that is $A = 25$, $M = 5$, $N = 75$ points on NIFTY's
50-point grid; $A = 50$, $M = 10$, $N = 150$ on BANKNIFTY's 100; $A = 12.5$,
$M = 2.5$, $N = 37.5$ on MIDCPNIFTY's 25. The "50" in the name is the
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
just above. What that costs is the sum of four premiums — run 115's first
real group paid 526.15 points a lot, ₹34,199.75 at lot size 65 —
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
  `strategy_name = f"{name}{variant_label}"` = `FulcrumMultiStraddle50` —
  the same string the short twin uses — and its time-stop closes plain
  `FulcrumMultiStraddle` (`long_time_stop_close` is given `self.name`); group
  ids `FULCRUM-MULTI-<timestamp>-<counter>`; reasons
  `Fulcrum Multi Straddle Adjusted. Active: [strikes]`, `Adjusting straddles`
  and `Time stop: held N minutes without the move this position was opened
  for; decay is the only thing working.` (with `exit: time_stop` in the
  metadata). The engine adds `spot_price` and `atm_strike`; its own closes
  carry the registry name `FulcrumMultiBuy50`.

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
ignore a move under $A$ (25 points) **if there is already a straddle on
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
still re-buys the rest. In run 115 the 12:30 roll sold `[23450, 23500,
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
(`BacktestSession._check_risk`). Run 115 set none (`"risk": {}`;
`StopLossPrice` / `TargetPrice` null on all its positions). A premium-based
target or stop is therefore a run parameter, not something this class
provides — `_exit_rules.py` says so explicitly.

### What the strategy never does

- It never stays flat: a rebuild re-buys in the same call, and the time stop
  is followed by a fresh purchase on the next evaluation (run 115: closed
  11:05, re-bought 11:10). Without a group / overall rule,
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
  and its clock starts — run 115 carried such phantoms from 09:15 (see the
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
   on the last driver bar (run 115, signal 1664 at 14:50).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.5 (this variant; the class default is 0.5) | $A/\Delta$: how far (in strikes) the spot must be from the centre strike before the group is rolled, when a straddle already sits on that side. 0.5 = 50 pts BANKNIFTY, 25 pts NIFTY | Fewer rolls, so a move is allowed to run further before it is realised and the time stop decides more often; at ≥ 1.0 the spot can reach the neighbouring strike first | More rolls, each re-buying at-the-money time value and paying the spread live; below `minor_steps` the minor rule dominates |
| `minor_steps` | 0.1 | $M/\Delta$: moves under this are ignored outright; a move past it on a side with **no** straddle rebuilds and adds one | The third straddle is added later | On NIFTY (5 pts) the empty-side add fires on noise |
| `adjustment_threshold`, `minor_threshold` | — | legacy spellings in points on the 100-point grid; read only when the `_steps` key is absent and divided by `LEGACY_GRID` (100) (`steps_from_params`) | as above | as above |
| `direction` | `BUY` (this variant passes it) | every straddle leg is a BUY (`resolve_direction`); leaving it out gives `FulcrumMulti50` | n/a | n/a |
| `use_hedges` | `false` when buying | `true` adds the short twin's two wings (BUY legs 3.5 % out) to a long group — extra premium, no protection to speak of | n/a | n/a |
| `max_hold_minutes` | 45 | $H$: a group held this long is closed by the time stop and re-bought next evaluation (`resolve_long_exit`; values ≤ 0 or unreadable fall back to 45) | Fewer time stops, more decay carried; at ≥ 375 the stop never fires in a session | More churn: close, re-buy at the same quotes, spread paid live |
| `target_steps` | 2.0 | resolved by `resolve_long_exit` but **never read** by this class (only `fulcrum_standard.py` uses `long_exit_reason`) | no effect | no effect |
| `strike_step` | from the chain | overrides the grid (`resolve_step`); wrong values name contracts that do not exist | — | — |
| `lots` (`quantity`) | `default_lots` = 1 | lots per leg on all 4–6 legs; P&L = points × lots × lot size | larger position, same signals | — |

Not parameters: the 1.5-strike keep rule (the literal `1.5` in `on_bar`)
and the time-stop constants' fallbacks (`DEFAULT_MAX_HOLD_MINUTES` 45,
`DEFAULT_TARGET_STEPS` 2.0).

## Worked example

Run **115** — OfflineReplay, `NSE:NIFTY50-INDEX`, 5m, 2026-09-09 (one
session), `ParametersJson` verbatim: `{"adjustment_steps":0.5,"minor_steps":0.1,
"direction":"BUY","lots":1,"underlying":"NIFTY","resolution":"5m",
"eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,
"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}` —
`max_hold_minutes` absent, so 45. Lot size 65 (`instruments.LotSize` for
`NSE:NIFTY2691523500CE`), expiry 2026-09-15, grid 50 → $A = 25$, $M = 5$,
$N = 75$. 63 driver bars 09:15–14:50; 21 signals (10 `OPEN_GROUP`, 6
"Adjusting straddles" closes, 3 time stops, the engine's "End of backtest",
one `BACKTEST_SUMMARY`), 96 orders, 48 positions, `realizedPnl` −809.25.
Every close below is the 5m index close the strategy saw (`candles`), every
fill the leg's candle close for the same bar.

### 09:15 to 10:15 — two phantoms

09:15 (C 23500.90): $K^- = 23500$, $K^+ = 23550$ → `Active: [23500, 23550]`,
four BUY legs, skipped — `skippedEntries[0]`: "no premium history for
NSE:NIFTY2691523550CE, NSE:NIFTY2691523550PE" (the 23550 candles begin at
09:35) — with `state` holding group `…-001` as open from 09:15. 09:35
(C 23526.25, 26.25 above) passed the gate and re-centred to $s_0 = 23500$,
$s_1 = 23550$: the same straddles, and with no wings nothing was emitted
(the short twin rebuilt here for a moved wing). 09:45 (C 23498.55, 51.45
below): `else`, $s_0 = 23450$, $s_1 = 23500$, $\hat s_2 = 23550$ kept →
`Active: [23450, 23500, 23550]`, skipped again (`skippedEntries[1]`, the
23450 candles begin at 10:00). The two phantoms' 4 + 6 legs are the "10
close legs ignored".

### Signal 1645 — 10:20 bar (C 23537.80) → orders 3122–3125 → positions 1572–1575

37.8 above $s_1 = 23500$ ≥ 25: `else`, $s_0 = 23500$, $s_1 = 23550$,
$s_2 = 0$ → `Active: [23500, 23550]`. First real fills, at the 10:20 candle
closes: `23500CE` BUY 181.75, `23500PE` BUY 87.45, `23550CE` BUY 150.55,
`23550PE` BUY 106.40, 1 lot each — 526.15 points, ₹34,199.75 paid. $t_0$ =
10:20. The passes at 10:35 (37.95 below → `[23500, 23550]`, $s_0 = 0$) and
10:50 (25.25 above → $s_0 = 23500$, $s_1 = 23550$) changed no leg.

### Signal 1646 — 11:05 bar (C 23531.60), time stop → orders 3126–3129

$11{:}05 - 10{:}20 = 45$ minutes: "Time stop: held 45 minutes without the
move this position was opened for; decay is the only thing working."
(`StrategyName` `FulcrumMultiStraddle`, `exit: time_stop`). Sold at the
11:05 closes — position 1572, `23500CE` LONG:

$$
(175.30 - 181.75) \times 1 \times 65 = -6.45 \times 65 = -419.25
$$

1573 `23500PE` $(90.00 - 87.45) \times 65 = 165.75$; 1574 `23550CE`
$(145.35 - 150.55) \times 65 = -338.00$; 1575 `23550PE`
$(109.30 - 106.40) \times 65 = 188.50$: group 003 = **−403.00**. The spot
ended 6.2 points from where it started; both calls lost more than both puts
gained. State reset.

### Signal 1647 — 11:10 bar (C 23524.95) → orders 3130–3133 → positions 1576–1579

Fresh state, so the first-group rule: `Active: [23500, 23550]`, bought at
168.50 / 92.45 / 138.55 / 112.90 — 512.40 points, five minutes after selling
the same four for 519.95. $t_0$ = 11:10. Timed out again at 11:55 (signal
1648, C 23488.50, orders 3134–3137): the spot had fallen 36.45 points in
those 45 minutes, yet 1576 `23500CE` $(146.30 - 168.50) \times 65 =
-1443.00$, 1578 `23550CE` $-1215.50$ outweighed 1577 `23500PE`
$(105.45 - 92.45) \times 65 = 845.00$, 1579 $+981.50$: group 004 =
**−832.00**.

### Signals 1649 → 1650 / 1651 — 12:00 to 12:25 → positions 1580–1583, orders 3146–3151

12:00 (C 23481.15), fresh: $K^- = 23450$, $K^+ = 23500$ →
`Active: [23450, 23500]`, bought 173.50 / 89.15 / 143.80 / 109.40. 12:10
(C 23487.80, 37.8 above 23,450) re-centred to $s_0 = 23450$, $s_1 = 23500$,
$s_2 = 0$ without a signal. 12:25 (C 23510.60): 10.6 above $s_1 = 23500$
with **no** straddle above — past $M = 5$, neighbour test fails — so the
group was rebuilt: `if st1 < price`, $s_1 = 23500$, $s_2 = 23550$,
$\hat s_0 = 23450$ kept (60.6 < 75) → `Active: [23450, 23500, 23550]`. Group
005 closed **−55.25** (1580 `23450CE` $(188.35 - 173.50) \times 65 =
965.25$, 1581 $-854.75$, 1582 $+832.00$, 1583 $-997.75$); group 006 re-bought
those four at the same closes (188.35 / 76.00 / 156.60 / 94.05) and added
`23550CE` 127.75, `23550PE` 116.00 — six legs, 758.75 points, ₹49,318.75
paid.

### Signals 1652 / 1653 — 12:30 bar (C 23547.85), the move arrives → orders 3152–3161 → positions 1584–1589

One bar later the spot was 47.85 above $s_1 = 23500$: `else`, $s_0 =
23500$, $s_1 = 23550$, $s_2 = 0$ → `Active: [23500, 23550]`. Group 006 sold
at the 12:30 closes: 1584 `23450CE` $(213.80 - 188.35) \times 65 = 1654.25$,
1586 `23500CE` $(179.35 - 156.60) \times 65 = 1478.75$, 1588 `23550CE`
$(149.30 - 127.75) \times 65 = 1400.75$, against 1585 `23450PE`
$(65.40 - 76.00) \times 65 = -689.00$, 1587 $-825.50$, 1589
$(99.95 - 116.00) \times 65 = -1043.25$: **+1,976.00**, the run's best
group, realised by the roll after five minutes. Group 007 bought `23500CE`
179.35, `23500PE` 81.35, `23550CE` 149.30, `23550PE` 99.95 — exactly the
prices 3154–3157 had just sold them at — so the roll's only effect was to
drop the 23450 straddle. 12:35 (C 23557.45, 7.45 above 23,550 with nothing
above) added 23600 (signals 1654 / 1655; group 007 **+871.00**).

### Signal 1656 — 13:45 bar (C 23553.50), a 70-minute time stop → orders 3172–3177

Group 008 (`[23500, 23550, 23600]`, $t_0$ = 12:35). The 13:15 bar was 40
minutes in; 13:20–13:40 do not exist in `candles`; 13:45 is the first bar
≥ 45 minutes: "Time stop: held 70 minutes …". The spot was 3.95 from where
the group was bought: 1594 `23500CE` $(180.90 - 189.70) \times 65 =
-572.00$, 1596 $-588.25$, 1598 `23600CE` $(121.45 - 129.45) \times 65 =
-520.00$, against 1595 $+48.75$, 1597 $+65.00$, 1599 $+81.25$:
**−1,485.25**, the run's worst — seventy minutes of decay on six legs.
13:50 re-bought `[23500, 23550]` (signal 1657); 13:55 (51.95 above 23,500)
rolled to `[23500, 23550, 23600]` (1658 / 1659, group 009 **+659.75**);
14:30 (38.45 below 23,550) rolled to `[23500, 23550]` (1660 / 1661, group
010 **−1,339.00**); 14:35 (7.15 below with nothing below) to
`[23450, 23500, 23550]` (1662 / 1663, group 011 **−104.00**).

### Signal 1664 — 14:50, "End of backtest" → orders 3212–3217 → positions 1614–1619

The last driver bar; `square_off: true`, `atm_strike` null. Group 012 sold
at the 14:50 closes: 1614 `23450CE` $(183.70 - 182.75) \times 65 = 61.75$,
1615 $-65.00$, 1616 $+42.25$, 1617 $-55.25$, 1618 $-16.25$, 1619 $-65.00$:
**−97.50**.

Day total, run 115, over groups 003–012: −403.00 − 832.00 − 55.25 +
1,976.00 + 871.00 − 1,485.25 + 659.75 − 1,339.00 − 104.00 − 97.50 =
**₹−809.25** (sum of `RealizedPnl` over positions 1572–1619, no charges).
The short twin on the same bars (run 112) made ₹2,141.75; the three time
stops alone cost ₹2,720.25.

## Limitations

- **The roll cuts winners; only the time stop is new.** `_exit_rules.py`'s
  module docstring says that closing a long straddle when the ATM changes
  "cuts the trade at the moment it starts working" and that a buyer should
  exit on `target_steps` or `max_hold_minutes` instead; this class keeps the
  seller's re-centring as its exit (`long_time_stop_reason`'s own docstring
  calls that "the winner being realised") and adds the time stop only.
  `target_steps` is accepted and ignored. Run 115's best group (signal
  1652, +1,976.00) was realised by a roll after one bar; whether holding
  would have paid more is not something the code or the data can say.
- **Never flat, never aware.** After a time stop the next evaluation re-buys
  (run 115: 11:05 → 11:10, 11:55 → 12:00, 13:45 → 13:50); the strategy cannot see a leg closed by a
  risk rule, a group closed at 15:30, a refused open or a stop button, and
  after any of those its `state` describes a position that does not exist —
  and its clock keeps running on it. Run 115's "10 close legs
  ignored: no matching open position" (`dataNotes`) are the closes of its
  two skipped opens.
- **Decay is paid on schedule, and the time stop is not a stop-loss.** A
  group is closed after 45 minutes whatever its P&L; nothing closes a group
  early for losing, and nothing holds a winner past a roll. Premium-based
  stops and targets are the run's risk rules, per `_exit_rules.py`.
- **A third straddle is added after `minor_steps`, not `adjustment_steps`.**
  The docstring says small moves are ignored and the group is rebuilt past
  the adjustment threshold; the code rebuilds after a 5-point move toward a
  side that has no straddle (run 115, signals 1650 and 1654: 10.6 and 7.45 points on a 25-point threshold) and buys another straddle at
  the money. Raising `adjustment_steps` does not change this.
- **"Within 1.5 strikes" means one strike.** The docstring's "keeping a
  nearby straddle when it is still within 1.5 strikes" can only ever keep the
  strike adjacent to the nearest strike on the side away from the spot (see
  Re-centring); the group is two or three straddles on consecutive strikes.
- **The time stop counts bar starts in a replay.** It fires on the first bar
  whose start is ≥ 45 minutes after the group's bar, so a hole in the candles
  stretches it (70 minutes at 13:45 in run 115, across the missing
  13:20–13:40 bars); live it counts tick timestamps.
- **The state is not persisted anywhere a reviewer can read.**
  `simulation_signals.Symbol` is empty and `Price` null; `MetadataJson`
  carries `group_id`, `reason` (with the `Active:` list), `spot_price`,
  `atm_strike` and, on a time stop, `exit: time_stop` — not
  $(s_0, s_1, s_2)$ or $t_0$, which have to be rebuilt from the code and the
  bar closes as the worked example does.
- **Signal names do not carry the direction.** Rebuild signals are stamped
  `FulcrumMultiStraddle50`, exactly as the short twin's; only
  `simulation_runs.StrategyName` and `direction` in the run parameters tell
  `FulcrumMultiBuy50` from `FulcrumMulti50` in `simulation_signals` /
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
- **Half a strike on NIFTY is 25 points.** The gate falls through on most
  bars that move at all, but with no wings only a change of straddle set
  emits anything, so the roll count is set by the 5-point empty-side add and
  the ≥ 25-point moves that change $K^*$: run 115 rolled six times and timed
  out three times in 63 bars, and was identical to the 0.7 twin (run 116) —
  the 25-vs-35 difference never changed a leg on that day.
- **Dead code.** `ce_value` / `pe_value` are computed and never read;
  `state["straddle_list"]` is initialised and reset but never written;
  `target_steps` is resolved and never used by this class.

## Facts (machine-readable)

```yaml
name: FulcrumMultiBuy50
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
FROM simulation_runs WHERE "Id" = 115;

/* signals 1645-1665 (time stops carry StrategyName FulcrumMultiStraddle and exit: time_stop;
   the last is BACKTEST_SUMMARY with skippedEntries and dataNotes) */
SELECT "Id", "StrategyName", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 115 ORDER BY "Id";

/* orders 3122-3217 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_orders WHERE "SimulationRunId" = 115 ORDER BY "Id";

/* positions 1572-1619, per group and in total (-809.25) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata', "ClosedUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_positions WHERE "SimulationRunId" = 115 ORDER BY "Id";
SELECT "GroupId", count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 115 GROUP BY 1 ORDER BY 1;

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
