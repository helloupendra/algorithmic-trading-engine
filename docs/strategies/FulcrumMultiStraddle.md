# FulcrumMultiStraddle

Source: `strategies/fulcrum/fulcrum_multi.py` (`FulcrumMultiStraddleStrategy`),
registered under the class's own `name` with the class defaults
`default_params = {"adjustment_steps": 0.5, "minor_steps": 0.1}` and no
`direction`, so it sells. `strategies/variants.py` registers the same class six
more times (`FulcrumMulti50/70/90`, `FulcrumMultiBuy50/70/90`); each has its
own page. Strike arithmetic is `strategies/strike_math.py`, the
direction/wings switch `strategies/fulcrum/_direction.py`, the buyer's time
stop `strategies/fulcrum/_exit_rules.py`, lots `strategies/base_strategy.py`;
contract plumbing is `strategies/execution_runner.py` (live) and
`backtest/engine.py` + `backtest/contracts.py` (replay). Every rule below is
read from the code; where the code and a docstring disagree, the code is
documented and the difference listed under Limitations. Times are IST
(UTC + 5:30); the database stores UTC.

## Idea

Sell the straddles on the two strikes that bracket the spot (57,500 and 57,600
for a BANKNIFTY spot of 57,530) and buy a far out-of-the-money call and put
about 3.5 % away as wings, so a runaway move is capped. While the spot stays
near the centre strike nothing is touched and the short premium decays. When
the spot drifts past the adjustment threshold — half a strike here — the whole
group is closed and rebuilt around the new level, keeping an old straddle as a
third one when it is still within 1.5 strikes of the spot; so the position
carries one to three straddles at a time. It is paid by decay in a slowly
drifting market and loses when the spot moves faster than decay pays, or when
it re-centres repeatedly across the same strikes. Nothing in this repository
tests that beyond the runs cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTYBANK-INDEX` in run 41; `NSE:NIFTY50-INDEX`, …) | every tick | none — `get_data_requirements` is not overridden (`[]`), so the runner does no warm-up and the first tick is evaluated | live only: ingestor → Redis stream `market:ticks`; each spot tick is one call of `on_bar` (`execution_runner.py`, the `listen_for_ticks` loop) |
| index candles | the same spot symbol | the run's resolution | none (`BacktestSession.warms_up` is false) | replay only: `candles` drives the loop, one call of `on_bar` per session bar with `spot_price` = the bar's close; the strategy never reads `inp.bars` |
| option quotes / candles | every leg the strategy names itself: CE and PE at each active straddle strike plus the two wings, all of the first expiry on or after today | live: latest quote; replay: the leg's candle at the run's resolution | none | live: `enrich_signal_leg_prices` puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for quotes; replay: `candles` for the contract (`HistoricalFeed.option_close_at`), synced from FYERS history while the contract exists |
| strike step | the option chain of the chosen expiry | once per run | — | live: smallest gap between strikes (`resolve_strike_step` → `strike_step_from_chain`; 100 for BANKNIFTY, 50 for NIFTY), else `FALLBACK_STRIKE_STEPS`; replay: `ContractResolver.step_for`; a run parameter `strike_step` overrides both (`strike_math.resolve_step`) |

It never reads option-chain OI, any bar series, or `inp.contracts`: the
runner resolves the default `atm_ce` / `atm_pe` requirements
(`BaseStrategy.get_contract_requirements`) and logs them, but the strategy
builds its legs from strike numbers as logical symbols
(`BANKNIFTY_CE_57500`) that the runner and the replay resolve against the
instrument master.

## Timeframe

- **Bar: none of its own.** `on_bar` reads `inp.spot_price`,
  `inp.strike_step`, `inp.timestamp_utc` and `inp.underlying`; the cadence is
  the caller's.
- **Live: every tick.** The runner calls `on_bar` on every spot tick, so the
  thresholds below are tested against the last traded price, not a bar close,
  and a group can be rebuilt any number of times within a minute. There is no
  session window and no last-entry time: run 41 was started at 12:47:51 and
  sold its first group on its first tick, 12:47:55.
- **Replay: once per closed driver bar** at the run's resolution
  (`BacktestSession.execute`), session bars only (`HistoricalFeed.driver_bars`,
  09:15 ≤ start < 15:30). The spot is the bar's close, so a threshold crossed
  inside a bar is seen only if the close is still past it. Fills are the
  close of each leg's candle for that bar (`option_close_at`).
- **15:30.** Live, `MarketHoursService` stops every run at or after 15:30 on
  weekdays and the API closes each open position with a `CLOSE_GROUP`
  "Market closed (15:30 IST)" at the latest quote (run 41: signal 551 at
  15:31:41). In a replay the engine squares off at `eod_square_off_ist`
  (15:15 by default) and, if positions are still open on the last driver bar,
  at that bar with reason "End of backtest". The strategy relies on these: it
  closes groups only to rebuild them and is never flat by its own decision.

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
A = \texttt{adjustment\_steps}\cdot\Delta,\qquad
M = \texttt{minor\_steps}\cdot\Delta,\qquad
N = 1.5\,\Delta .
$$

With the defaults $A = 0.5\Delta$ and $M = 0.1\Delta$: 50 and 10 points on
BANKNIFTY's 100-point grid, 25 and 5 on NIFTY's 50-point grid, 12.5 and 2.5
on MIDCPNIFTY's 25. $N$ (`keep_nearby`) is 150 / 75 / 37.5. The state is
three straddle strikes $(s_0, s_1, s_2)$ — lower, centre, upper — with 0
meaning "none", plus the open group's id and legs. If $K^* = S$ exactly the
call returns without doing anything (`if atm == price: return []`).

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
measured from the spot, not from the straddle strikes. On BANKNIFTY at
57,530 the grid is 500 (500 / 57,530 = 0.87 %) and the wings land on 55,500
and 60,000; on NIFTY at 23,500 the 250 grid is 1.06 % of the spot, so the
wings snap to the ordinary 50-point grid (22,700 / 24,350) — see Limitations.
The wings are BUY legs of the same `lots`.

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
  day). BANKNIFTY carries only monthly expiries in the September 2026 master,
  so run 41 traded `NSE:BANKNIFTY26SEP…` (2026-09-29); NIFTY replays of
  2026-09-09 trade the 2026-09-15 weekly.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  legacy key `quantity`, default `default_lots` = 1) on every leg;
  `paper_orders.Quantity` stores lots and the API multiplies by the master's
  lot size (BANKNIFTY 30, NIFTY 65) for P&L.
- **Fill:** live, the latest quote per leg after a single 10 s wait for all
  legs of the signal (`core/leg_pricing.py`); a leg still unpriced is sent
  unpriced and the API rejects the opening group ("No price is available …
  rejected rather than filled at zero", `PaperTradingService`). Closing legs
  wait 0 s and fall back to the position's last mark. Replay: the close of
  the leg's candle for the signal bar; no slippage, `charges_per_lot` 0 by
  default.
- **Signal names:** the strategy stamps its signals
  `strategy_name = f"{name}{variant_label}"` = `FulcrumMultiStraddle50`
  (`variant_label` is `adjustment_steps × 100`), group ids
  `FULCRUM-MULTI-<timestamp>-<counter>`, reason
  `Fulcrum Multi Straddle Adjusted. Active: [strikes]`; the runner adds
  `spot_price` and `atm_strike` to the metadata (`stamp_signal_metadata`).

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

In words: ignore any move under $M$ from the centre strike; ignore a move
under $A$ **if there is already a straddle on the neighbouring strike on that
side**. When there is none ($s_0 = 0$ or $s_2 = 0$), the placeholder is
$s_1$, the equality fails, and a move of only $M$ past $s_1$ falls through to
a rebuild — that is how the third straddle is added (run 41, 13:16:45: the
spot 10.2 points above $s_1 = 57{,}600$ with nothing above it, see the worked
example). This is the rule the class docstring does not mention.

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
dropped: after the fall from 57,610 to 57,549.6 (run 41, 13:51:47) the
57,700 straddle went and $[57500, 57600]$ remained. `active_straddles` is
$\{s_i > 0\}$ in ascending order (the reason text lists it).

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
$W_{PE}$ moves every 51.8 spot points and $W_{CE}$ every 48.3, so most passes
do (run 112, the `FulcrumMulti50` page: 7 of 16 groups were re-sold at the
same strikes). On BANKNIFTY ($g = 500$) a wing moves only every 480–520
points and run 41's 55,500 / 60,000 wings held through three rebuilds.

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
(`BacktestSession._check_risk`). None of the seven runs on these pages set any
(`"risk": {}`; `StopLossPrice` / `TargetPrice` null on all positions).

### What the strategy never does

- It never goes flat on its own: every `CLOSE_GROUP` it emits is followed by
  an `OPEN_GROUP` in the same call. Without a group / overall rule, the stop
  button or the 15:30 close the exposure lasts all day (run 41: 12:47:55 →
  15:31:41).
- It never sets a per-position stop or target.
- It never learns that the platform closed something. A group closed by a
  risk rule, the 15:30 sweep or the stop button stays in `state` as open; the
  next rebuild's `CLOSE_GROUP` names legs that are flat (the API's reduce-only
  path ignores them, `ApplyPositionAsync` returns false; the replay ledger
  counts them as "close legs ignored") and its `OPEN_GROUP` is a fresh entry.
  The same happens after a refused or skipped open: the group is recorded
  in `state` before the signal leaves `on_bar`.
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
   `MarketHoursService` at 15:30 IST (run 41: signal 551, "Market closed
   (15:30 IST)", orders 806–813 at 15:31:42–51).
6. Replay only: `eod_square_off_ist` (15:15 by default) and "End of backtest"
   on the last driver bar.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.5 | $A/\Delta$: how far (in strikes) the spot must be from the centre strike before the group is rebuilt, when a straddle already sits on that side. 0.5 = 50 pts BANKNIFTY, 25 pts NIFTY | Fewer rebuilds; the spot can sit further off-centre; at ≥ 1.0 it can reach the neighbouring strike before re-centring | More rebuilds, each paying the spread on 6–8 legs live; below `minor_steps` the minor rule dominates |
| `minor_steps` | 0.1 | $M/\Delta$: moves under this are ignored outright; a move past it on a side with **no** straddle rebuilds and adds one | The third straddle is added later; fewer wing-only rebuilds | Almost every tick past the centre evaluates; on NIFTY (5 pts) the empty-side add fires on noise |
| `adjustment_threshold`, `minor_threshold` | — | legacy spellings in points on the 100-point grid; read only when the `_steps` key is absent and divided by `LEGACY_GRID` (100), so run 41's `50` / `10` became 0.5 / 0.1 (`steps_from_params`) | as above | as above |
| `direction` | `SELL` | `BUY` / `LONG` / `B` makes every straddle leg a buy (`resolve_direction`); that is the `FulcrumMultiBuy*` pages | n/a | n/a |
| `use_hedges` | `true` when selling, `false` when buying | whether the two wings are held; a hedged long or a naked short is possible by setting it | n/a | n/a |
| `max_hold_minutes` | 45 | BUY only: `long_time_stop_close` closes a long group held this long (`resolve_long_exit`); ignored when selling | — | — |
| `target_steps` | 2.0 | resolved by `resolve_long_exit` but **never read** by this class (only `fulcrum_standard.py` uses `long_exit_reason`) | no effect | no effect |
| `strike_step` | from the chain | overrides the grid (`resolve_step`); wrong values name contracts that do not exist | — | — |
| `lots` (`quantity`) | `default_lots` = 1 | lots per leg on all 6–8 legs; P&L = points × lots × lot size | larger position, same signals | — |

Not parameters: the 3.5 % wing distance, the 5-step wing grid and its 1 %
cap, the 1.5-strike keep rule (`strike_math` constants and the literal
`1.5` in `on_bar`).

## Worked example

Run **41** — LivePaper, `NSE:NIFTYBANK-INDEX`, BANKNIFTY, started 2026-09-04
12:47:51 IST, stopped 15:31:52 by the market-hours sweep. `ParametersJson`
verbatim: `{"adjustment_threshold":50,"minor_threshold":10,"lots":1,
"underlying":"BANKNIFTY","risk":{},"stop_loss":null,"target":null}` — the
legacy point keys, which `steps_from_params` reads as 0.5 and 0.1 strikes;
on the chain's 100-point grid that is $A = 50$, $M = 10$, $N = 150$. Lot size
30 (`instruments.LotSize` for `NSE:BANKNIFTY26SEP57500CE`), expiry 2026-09-29
(the only September expiry in the master). 9 signals, 56 orders, 28 positions.
Signal timestamps are the tick's; fill times are the API's. The spot ticks
between signals are not persisted (`live_ticks` is out of scope), so the
state between them is reconstructed from the code and the four spots the
signals carry.

### Signal 539 — 12:47:55.752, first group → orders 754–759 → positions 386–391

`spot_price 57530.5`, `atm_strike 57500`. Fresh state: $K^- = 57500$,
$K^+ = 57600$ → `Active: [57500, 57600]`. Wings from the spot: 3.5 % is
2,013.6 points; $g = 500$; $W_{PE} = \lfloor 55516.9/500 \rfloor \cdot 500 =
55500$, $W_{CE} = \lceil 59544.1/500 \rceil \cdot 500 = 60000$. Fills at
12:48:21 (the six legs were subscribed on this signal and the runner waited
for their first quotes): `55500PE` BUY 99.40, `60000CE` BUY 64.95,
`57500CE` SELL 838.60, `57500PE` SELL 519.45, `57600CE` SELL 780.60,
`57600PE` SELL 559.55, 1 lot each.

### Signals 540 / 541 — 13:16:45.254, a straddle added → orders 760–773 → positions 392–399

`spot_price 57610.2`, `atm_strike 57600`. Reconstruction: somewhere between
the two signals the spot passed 57,550 ($A$ above $s_1 = 57500$ with a
straddle already at 57,600), the gate fell through, and the `else` branch
set $s_0 = 57500$, $s_1 = 57600$, $s_2 = 0$ — the same legs, so nothing was
emitted. At 57,610.2 the spot is 10.2 above $s_1$ with **no** straddle above
($\hat s_2 = s_1$), which is ≥ $M$ and fails the neighbour test, so the group
is rebuilt: $K^* = 57600 < S$ → $s_1 = 57600$, $s_2 = 57700$, and the old
$s_0 = 57500$ is 110.2 from the spot, under 150, so it is kept:
`Active: [57500, 57600, 57700]`. Wings unchanged (55,500 / 60,000).

Close (540), at the quotes of 13:16:45–46: the 57500 straddle bought back at
882.30 / 489.35, the 57600 at 823.05 / 526.40, wings sold 92.15 / 69.40.
Position 388, `57500CE` SHORT:

$$
(838.60 - 882.30) \times 1 \times 30 = -43.70 \times 30 = -1311.00
$$

Position 389, `57500PE` SHORT: $(519.45 - 489.35) \times 30 = 903.00$;
386, `55500PE` LONG: $(92.15 - 99.40) \times 30 = -217.50$. Group 001 in
full: $-217.50 + 133.50 - 1311.00 + 903.00 - 1273.50 + 994.50 =$ **−771.00**
(the spot rose 80 points against two short straddles). Open (541): the same
six contracts re-sold at the same quotes plus `57700CE` SELL 763.75 and
`57700PE` SELL 567.00 (orders 766–773).

### Signals 543 / 544 — 13:51:47.934, a straddle dropped → orders 775–788 → positions 401–406

`spot_price 57549.6`: 50.4 below $s_1 = 57600$, exactly past $A$ on the
first tick to reach it. $K^* = 57500 < S$ → $s_1 = 57500$, $s_2 = 57600$;
$s_0 = 57500 \ge s_1$ is zeroed and $s_2$'s old value 57,700 is overwritten:
`Active: [57500, 57600]`. Group 002 closed at 13:51:48: 57500 straddle
852.00 / 509.40, 57600 794.85 / 551.25, 57700 740.00 / 590.45, wings 96.55 /
65.55 — position 398, `57700CE` SHORT: $(763.75 - 740.00) \times 30 =
712.50$; group 002 total **+433.50** (positions 392–399). Group 003 re-sold
the 57500 and 57600 straddles at those closing quotes (orders 783–788).

### Signals 546 / 547 — 14:55:07.729, third straddle below → orders 790–803 → positions 408–415

`spot_price 57488.85`: 11.15 below $s_1 = 57500$ with no straddle below
($s_0 = 0$), so the minor rule adds one: `else` branch, $s_0 = 57400$,
$s_1 = 57500$, $s_2 = 57600$ kept (111.15 < 150) →
`Active: [57400, 57500, 57600]`. Wings from 57,488.85: $W_{PE} =
\lfloor 55476.7/500 \rfloor \cdot 500 = 55000$ (moved), $W_{CE} = 60000$.
Group 003 closed at 14:59:23 — position 403, `57500CE` SHORT
$(852.00 - 815.35) \times 30 = 1099.50$; group total **+1,080.00**
(positions 401–406). The runner of that day waited for quotes leg by leg
(replaced on 2026-09-06 by the single 10 s budget in `core/leg_pricing.py`),
so the close filled 4 min 16 s after the tick and the open at 15:00:05–08.
Order 796, `NSE:BANKNIFTY26SEP55000PE` BUY, has `RequestedPrice` null and
`FillPrice` **0.00**: no quote ever arrived for the new wing and the API of
2026-09-04 booked it at zero (position 408, `AveragePrice` 0, `RealizedPnl`
0). Since commit bd5f987 (2026-09-06) the API rejects such a group instead.

### Signal 551 — 15:31:41, market close → orders 806–813

"Market closed (15:30 IST)", `system: true`. The eight legs of group 004
closed at 15:31:42–51: position 414, `57600CE` SHORT $(762.60 - 752.45)
\times 30 = 304.50$; 410, `57400CE` $(879.95 - 875.15) \times 30 = 144.00$;
group total **+430.50** (positions 408–415, the 55000 PE at 0 → 0). Signal
552 `RUN_STOPPED`.

Day total, run 41: $-771.00 + 433.50 + 1080.00 + 430.50 =$ **₹1,173.00**
(sum of `RealizedPnl` over positions 386–415; no charges modelled). Three of
the four groups were profitable and the first, opened into an 80-point rise,
cost more than any of them made.

## Limitations

- **Never flat, never aware.** The strategy holds one group all day and
  replaces it in place; it cannot see a leg closed by a risk rule, a group
  closed at 15:30, a refused open or a stop button. After any of those its
  `state` describes a position that does not exist until the next rebuild
  re-enters (see Position management).
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
  the adjustment threshold; the code rebuilds after a minor move toward a
  side that has no straddle (run 41, signals 541 and 547; 10.2 and 11.15
  points on a 50-point threshold). On NIFTY that is 5 points.
- **"Within 1.5 strikes" means one strike.** The docstring's "keeping a
  nearby straddle when it is still within 1.5 strikes" can only ever keep the
  strike adjacent to the nearest strike on the side away from the spot (see
  Re-centring); the group is therefore two or three straddles on consecutive
  strikes, never wider, and a straddle two strikes away is always closed.
- **The state is not persisted anywhere a reviewer can read.**
  `simulation_signals.Symbol` is empty and `Price` null; `MetadataJson`
  carries `group_id`, `reason` (with the `Active:` list), `spot_price`,
  `atm_strike` — not $(s_0, s_1, s_2)$ or the wings. Live, the intervening
  ticks are not stored with the run, so the exact tick that first passed the
  gate silently (as at 57,550 above) cannot be recovered from the database.
- **Signal names do not carry the direction.** Every signal of this class is
  stamped `FulcrumMultiStraddle50` (`name` + `adjustment_steps × 100`) whether
  it sells or buys; only `simulation_runs.StrategyName` and the run
  parameters tell the Buy twin apart.
- **Fills lag the tick, live.** Signal 539 filled 26 s after its tick, 547
  five minutes after; the API of 2026-09-04 booked an unpriced wing at 0.00
  (order 796) — both behaviours changed on 2026-09-06 (`core/leg_pricing.py`,
  `PaperTradingService` refusal). A refused open today leaves the phantom
  group described above.
- **Expiry choice.** The first expiry on or after today: BANKNIFTY monthly
  (2026-09-29 in run 41), NIFTY weekly — on a Tuesday the same-day contract
  is traded. Live, the expiry is fixed at start-up; a replay picks it per
  day and matches a close after a roll to the contract it opened
  (`_open_symbol_for`).
- **Replay differs from live.** Bar closes only (a threshold crossed and
  un-crossed inside a bar is invisible), fills at the option candle close,
  no slippage, contracts that have expired have no FYERS history and are
  skipped, and a contract whose first candle of the day is late cannot be
  opened until it exists (the NIFTY pages document two such skips). Ticks
  exactly on a strike are ignored (`atm == price`).
- **Dead code.** `ce_value` / `pe_value` are computed and never read;
  `state["straddle_list"]` is initialised and reset but never written;
  `target_steps` is resolved and never used by this class.

## Facts (machine-readable)

```yaml
name: FulcrumMultiStraddle
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
SELECT "Id", "Mode", "Symbol", "StrategyName", "Status",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata', "CompletedUtc" AT TIME ZONE 'Asia/Kolkata', "ParametersJson"
FROM simulation_runs WHERE "Id" = 41;

/* signals 539, 540, 541, 543, 544, 546, 547, 551, 552 (542/545/548-550 belong to run 40) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata', "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 41 ORDER BY "Id";

/* orders 754-813 (Quantity is lots; 796 is the zero fill) */
SELECT "Id", "SimulationSignalId", "Symbol", "Side", "Quantity", "RequestedPrice", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata'
FROM paper_orders WHERE "SimulationRunId" = 41 ORDER BY "Id";

/* positions 386-415, per group and in total (1173.00) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata', "ClosedUtc" AT TIME ZONE 'Asia/Kolkata', "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 41 ORDER BY "Id";
SELECT "GroupId", sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 41 GROUP BY 1 ORDER BY 1;

/* lot size and expiry */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments
WHERE "Symbol" IN ('NSE:BANKNIFTY26SEP57500CE', 'NSE:BANKNIFTY26SEP55000PE');
SELECT "ExpiryDate", count(*) FROM instruments WHERE "Underlying" = 'BANKNIFTY' GROUP BY 1 ORDER BY 1;
-->
