# FulcrumQtyAdjustment

Source: `strategies/fulcrum/fulcrum_qty_adj.py` (`FulcrumQtyAdjustmentStrategy`),
registered under this name by `strategies/variants.py` with
`adjustment_steps=0.7, minor_steps=0.1` and no `direction`, so it trades as the
seller the class was written as (`_direction.resolve_direction`: `SELL`, wings
on). Shared helpers: `strategies/strike_math.py` (grid arithmetic, wings),
`strategies/fulcrum/_exit_rules.py` (`closing_legs`; the time stop in there is
for the BUY twin only) and `strategies/fulcrum/_direction.py`. Every rule
below is read from the code; where the code and its docstring disagree, the
code is documented and the difference listed under Limitations. The BUY twin
is `FulcrumQtyAdjustmentBuy`, which has its own page.

## Idea

The strategy is short two straddles on the two strikes that bracket the spot,
with a far out-of-the-money call and put bought as wings, and it re-centres
that structure as the spot drifts. What sets it apart from `FulcrumMulti70`
(same class family, same thresholds) is the quantity ladder: whenever the spot
crosses the centre strike a third straddle is sold on the far side and *every*
short leg is carried in double lots; when the spot has drifted 0.7 of a strike
from the centre the structure is cut back to two straddles at single lots. The
bet is that the spot oscillates around a strike more often than it trends
through one — the doubled legs collect twice the decay while it oscillates and
lose twice as fast when the crossing was the start of a move. Every roll
realises the P&L of the group it replaces; the strategy is never flat on its
own. Nothing in this repository tests the bet beyond the runs cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | every tick | none — `get_data_requirements` is not overridden (returns `[]`), so there is no warm-up and the first tick is the first evaluation | ingestor → Redis stream `market:ticks`; `execution_runner.py` calls `on_bar` once per tick with `spot_price` = the tick's LTP |
| option quotes | the legs it builds: `UNDERLYING_CE_<strike>` / `UNDERLYING_PE_<strike>` resolved to the exact contract of the nearest expiry (`resolve_leg_symbol` → `get_exact_contract`) | latest quote | none | the runner puts every leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s, `core/leg_pricing.DEFAULT_WAIT_SECONDS`) for a quote on an `OPEN_GROUP`, 0 s on a `CLOSE_GROUP` (`enrich_signal_leg_prices`); the API fills at that quote |
| index candles (replay only) | the spot symbol | the run's resolution (5m in run 118) | none | `candles` (backfill); `backtest/engine.py` calls `on_bar` once per closed session bar with `spot_price` = the bar's close |
| option candles (replay only) | the same legs | the run's resolution | none | `candles` (backfill, `tools/backfill_options.py`); a leg fills at the close of its candle on the signal bar (`feed.option_close_at`); an `OPEN_GROUP` with any leg lacking a candle is skipped |

The strike step is read once from the option chain of the chosen expiry
(`resolve_strike_step` → `strike_step_from_chain`: the smallest gap between
listed strikes, 50 on NIFTY, 100 on BANKNIFTY; `FALLBACK_STRIKE_STEPS` if the
chain cannot be read) and handed in as `inp.strike_step`. The default
`get_contract_requirements` (ATM CE and PE) is also resolved and watchlisted on
every tick, but this class never reads `inp.contracts` — its legs are built
from strikes, not from the supplied contracts. It never reads bars, option
chain OI or Greeks.

## Timeframe

- **Live: every tick, no bar.** `on_bar` runs on every spot tick the runner
  receives; the only clock the strategy sees is `inp.timestamp_utc` (the
  tick's `exchangeTimestampUtc`, else `receivedUtc`), which it uses for the
  group id and for `group_entry_utc`. Two rolls can therefore be seconds
  apart (the BUY twin's run 100: 12:52:24 → 12:52:48 → 12:55:38 → 12:55:44
  IST).
- **Backtest: once per closed session bar** at the run's resolution
  (`feed.driver_bars`: bars whose start is in 09:15–15:30 IST), with the bar
  close as the spot and the option candle close of the same bar as the fill.
  Run 118 drove 63 five-minute bars, 09:15–14:50 IST (the day's index candles
  stop at 14:50; see Limitations).
- **Session window:** none of its own. It enters on the first evaluation
  (live: the first tick after start, whatever the time; replay: the 09:15 bar)
  and has no last-entry time. The replay skips entries after
  `eod_square_off_ist` (15:15 by default) and squares off there; run 118
  never reached it.
- **15:30:** `MarketHoursService` checks once a minute on weekdays and at or
  after 15:30 IST stops every run with flatten; the API closes each open
  position with a `CLOSE_GROUP` "Market closed (15:30 IST)" at the latest
  quote (BUY twin, run 100: signal 1475 at 15:30:43). The strategy relies on
  it: a rolled group is always replaced by a new one, so without the market
  close (or a risk rule, or the stop button) it is short straddles until the
  next weekday's sweep.

## Entry

### Notation

$S$ is the spot (tick LTP live, bar close in replay); $\delta$ the strike step
(`resolve_step(inp.strike_step, params["strike_step"])`: an explicit run
parameter wins, else the chain's step, else 50). Grid functions from
`strike_math`: $\lfloor S \rfloor_\delta$ (`round_down_to_step`),
$\lceil S \rceil_\delta$ (`round_up_to_step`), $[S]_\delta$ (`round_to_step`,
Python `round`, nearest). The thresholds, in points of this underlying
(`steps_to_points`):

$$
a = \texttt{adjustment\_steps}\cdot\delta,\qquad
m = \texttt{minor\_steps}\cdot\delta,\qquad
k = 1.7\,\delta .
$$

At the defaults on NIFTY ($\delta = 50$): $a = 35$, $m = 5$, $k = 85$; on
BANKNIFTY ($\delta = 100$): $a = 70$, $m = 10$, $k = 170$. The thresholds are
strike multiples, so 0.7 means "seven tenths of the way to the next strike"
on every grid — the same decision, not the same number of points.

The state is three strike slots $L, M, U$ (`st0`, `st1`, `st2`), 0 meaning
empty: $M$ is the centre straddle, $L$ and $U$ the ones below and above it.
Fresh state is $(0, 0, 0)$.

### First evaluation

With $M = 0$ the threshold gate below cannot hold ($S - 0 \ge a$) and the
rebuild takes the `st1 < price` branch without first snapping $M$ to the
nearest strike (that line is guarded by `if st1 != 0`), so the first group is
always the two strikes around the spot with the **floor** as centre:

$$
(L, M, U) \leftarrow \left(0,\ \lfloor S\rfloor_\delta,\ \lceil S\rceil_\delta\right).
$$

In words: on the first tick, sell the straddle at the strike just below the
spot and the one just above it, buy the wings, and remember the lower strike
as the centre — even when the upper one is nearer. (Consequence: if the spot
is already $a$ or more above the floor, the very next evaluation rebuilds;
the BUY twin's run 100 started at 56589.05, 89 points above its floor, and
its second tick 2.5 s later was already the doubled triple.)

### Legs and contracts

For the active straddles $A = \{x \in \{L, M, U\} : x > 0\}$ (always two or
three, see Position management) with straddle lots $q$:

- `BUY` `lots` × `UNDERLYING_PE_`$K_{PE}$ and `BUY` `lots` × `UNDERLYING_CE_`$K_{CE}$
  — the wings, always at the run's `lots`, never doubled;
- for each $x \in A$, ascending: `SELL` $q$ × `UNDERLYING_CE_`$x$ and
  `SELL` $q$ × `UNDERLYING_PE_`$x$.

The wings are placed by moneyness from the spot, not from a straddle strike
(`hedge_strike`, `DEFAULT_HEDGE_PCT` = 3.5 %), on a coarser grid
$g = 5\delta$ unless that is more than 1 % of the spot
(`MAX_HEDGE_GRID_FRACTION`), in which case $g = \delta$:

$$
K_{PE} = \left\lfloor 0.965\,S \right\rfloor_g,\qquad
K_{CE} = \left\lceil 1.035\,S \right\rceil_g,
$$

always rounded further out of the money. On NIFTY at 23,500 the 250-point
grid is 1.06 % of the spot, so the wings snap to the ordinary 50 grid
(run 118: 22700 / 24400 from a spot of 23537.80); on BANKNIFTY at 56,600 the
500 grid is 0.88 % and is used.

**Contract.** A logical leg `NIFTY_CE_23500` is resolved by the runner
(`resolve_leg_symbol`) or the replay (`ContractResolver.resolve_logical`) to
the exact row of the instrument master at that strike for the **first expiry
on or after today** (`valid_expiries[0]` live, on the UTC date;
`expiry_for(day)` in replay, on the bar's IST date) — the 2026-09-15 weekly
in run 118 (`NSE:NIFTY26915…`). A strike the master lacks leaves the leg
unpriced: live, the API refuses the whole group ("No price is available …
rejected rather than filled at zero"); in replay the entry is skipped and
listed in the summary's `skippedEntries`. In both cases the strategy has
already recorded the group as open in its state (the state is written inside
`on_bar`), so its next roll emits a `CLOSE_GROUP` for legs that were never
filled — the ledger ignores those ("no matching open position"; 14 such legs
in run 118).

**Quantity.** `lots` (`BaseStrategy.lots_from`, `default_lots` = 1) is the
wings' quantity and the base of $q$; `paper_orders.Quantity` stores lots and
the API multiplies by the master's lot size (NIFTY 65, BANKNIFTY 30) for P&L.

**Fill.** Live: the API fills a `MARKET_SIM` order per leg at the latest
quote, no slippage; the `CLOSE_GROUP` and the `OPEN_GROUP` of a roll are two
requests, so a leg that is closed and re-opened in the same roll can fill at
two different quotes. Replay: every leg of a bar fills at its candle close, so
the same leg closes and re-opens at one price.

## Position management

Everything after the first group is a rebuild of the whole structure. On each
evaluation the strategy decides whether to touch the position (the gate),
recomputes the three slots and the wings from the current spot, and — only if
the resulting set of (symbol, side, lots) differs from the group it holds
(`_legs_differ`) — emits `CLOSE_GROUP` "Adjusting straddles" for *every* leg
of the old group (`closing_legs`: same contracts, opposite side, same lots)
followed by `OPEN_GROUP` "Fulcrum Qty Adjusted. Active: [...], Qty: n" for the
new set, in one tick. A straddle whose strike survives the rebuild is closed
and sold again like the others; "kept" means its strike stays in the new
group, not that its legs are left alone (see Limitations).

### Gate (`on_bar`, "Check threshold")

Let $L' = L$ if $L \ne 0$ else $M$, and $U' = U$ if $U \ne 0$ else $M$ (an
empty outer slot is treated as the centre for this test only). Nothing happens
on this evaluation when

$$
[S]_\delta = S
\;\lor\;
\Big(S < M \land \big(M - S < m \ \lor\ (M - S < a \land L' = M - \delta)\big)\Big)
\;\lor\;
\Big(S > M \land \big(S - M < m \ \lor\ (S - M < a \land U' = M + \delta)\big)\Big).
$$

In words: a spot exactly on a strike is ignored; a spot less than $m$ (5
points on NIFTY) from the centre is ignored; a spot between $m$ and $a$ (35
points) from the centre is ignored *only if the straddle one strike away on
that side is already held*. So a move of at least $a$ from the centre always
rebuilds, and a move of at least $m$ rebuilds when it is towards a side with
no straddle.

### Rebuild

First $M \leftarrow [S]_\delta$ (the nearest strike; skipped only on the
first evaluation). Then, with $L'$ and $U'$ as above:

$$
\begin{aligned}
&\text{if } M < S:\quad M \leftarrow \lfloor S\rfloor_\delta,\ U \leftarrow \lceil S\rceil_\delta,\
L \leftarrow \begin{cases} L' & L' < M \land |L' - S| < k \\ 0 & \text{otherwise}\end{cases}\\[4pt]
&\text{if } M > S:\quad L \leftarrow \lfloor S\rfloor_\delta,\ M \leftarrow \lceil S\rceil_\delta,\
U \leftarrow \begin{cases} U' & U' > M \land |U' - S| < k \\ 0 & \text{otherwise}\end{cases}
\end{aligned}
$$

($M = S$ was excluded by the gate.) In words: the centre becomes the nearest
strike and the second straddle sits on the other side of the spot; the old
outer straddle beyond the centre is kept if it is still within $k = 1.7$
strikes of the spot, otherwise dropped. The two checks `st0 >= st1` and
`st2 <= st1` in the code drop an old outer slot that the new centre has
absorbed. `ce_value` / `pe_value` are computed here but never used.

### The quantity ladder

$$
q = \texttt{lots} \times \begin{cases} 2 & |A| = 3 \\ 1 & |A| = 2 \end{cases}
$$

That is the whole ladder (`qty_multiplier`): two rungs, no partial additions,
no cap other than $2\,\times$ `lots`, and the wings stay at `lots` (the
inline comment says the original script "doubles up some legs"; here it is
every straddle leg). Because
the rebuild always fills the centre and the strike on the other side of the
spot, $|A|$ is 2 or 3 — never 1, whatever the class description says. Derived
from the rules above (not stated in the code) the ladder moves like this:

- **Pair → triple (lots doubled).** From a pair $\{M, P\}$ the spot leaves
  the interval through the centre $M$ by at least $m$ towards the side with
  no straddle. The rebuild puts the new pair at $M$ and the strike beyond, and
  keeps $P$ (it is one strike from $M$ and within $k$ of the spot), giving
  $\{M-\delta, M, M+\delta\}$ at $2\,\times$ `lots` — the strike the spot
  just crossed is the middle of the three. Run 118 at 11:55: spot 23488.50,
  11.50 below the centre 23500, no 23450 straddle held → $\{23450, 23500,
  23550\}$, 2 lots.
- **Triple → pair (lots single).** From $\{M-\delta, M, M+\delta\}$ both
  neighbours are held, so the gate waits until $|S - M| \ge a$. The nearest
  strike is then the neighbour on that side, the pair is the two strikes
  around the spot, and the old far straddle is either the new centre or
  beyond it and dropped. Run 118 at 12:30: spot 23547.85, 47.85 above 23500
  → $\{23500, 23550\}$, 1 lot.
- **Pair → pair (re-centre).** From a pair the spot moves at least $a$ from
  $M$ while staying inside the interval — within $\delta - a$ (15 points on
  NIFTY) of the other strike: the strikes are unchanged and only the centre
  flips to that strike. For this SELL variant the wings are recomputed from
  the new spot and on NIFTY's 50 grid almost always move, so the *whole*
  group is closed and re-opened anyway (run 118 at 10:35, 10:55 and 11:50:
  three of its nine groups). Were the wings unchanged, `_legs_differ` would
  be false and nothing would be emitted.

Derived consequences. In a steady trend the cut at $M + a$ leaves the centre
on the strike ahead of the spot, and the next doubling comes $m$ past that
strike, $\delta - a + m$ (20 points on NIFTY) later: the structure is single
for $0.4\,\delta$ of every strike travelled and doubled for $0.6\,\delta$, and
the doubled straddles are always the ones the trend is leaving behind
(run 118: cut on the 12:30 bar, threshold 23535, close 23547.85; doubled
again on the 12:35 bar, threshold 23555, close 23557.45).
After a reversal the pair first re-centres ($a$ back from the centre) and is
doubled the same $\delta - a + m$ later. With tick-by-tick evaluation the $k$
test never decides anything (the outer straddle is always exactly one strike
from the new centre, hence within $1.7\,\delta$ of the spot); it can only
drop a straddle after a jump of more than one strike between two
evaluations.

**What the run's risk rules add.** `parametersJson.risk` carries three levels
that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds` (3 s,
`appsettings.json`), in the order leg → group → overall, each level in the
fixed order stop-loss → trailing stop → target:

- `leg` — per open position, points or percent of `AveragePrice` against
  `LastMarkPrice`; a trip closes that leg only. The strategy does not know
  this happened: its state still lists the leg, and its next roll's
  `CLOSE_GROUP` for it is skipped by the API (reduce-only: a `CLOSE_GROUP`
  "may only shrink or close what is open") while the `OPEN_GROUP` rebuilds
  the full structure.
- `group` — rupees on a signal group's realized + unrealized P&L; closes
  every open leg of that group. Because every roll starts a new group, the
  rule measures one structure between two rolls, not the day.
- `overall` — rupees on the run's total realized + unrealized P&L; a trip
  flattens everything and ends the run. The rule's `scope` (`"day"` by
  default, or `"run"`) is honoured by the backtest engine only.

Run 118 carried `risk: {}` — no rule at all. This strategy relies on none of
them; with double-lot short straddles and no leg or group stop, a breakout is
bounded only by the wings (3.5 % away) and the overall rule if one is set.

**What the strategy never does:** it never goes flat by choice (every
`CLOSE_GROUP` it emits is immediately followed by an `OPEN_GROUP`), never
sets `StopLossPrice` / `TargetPrice` (null on all 60 positions of run 118),
never reads its own P&L, never re-enters after an external close (its state
says the group is still open, so it only rolls), and — as the seller — has
no time stop (`long_time_stop_close` returns `[]` unless `direction` is
`BUY`).

## Exit

In order of precedence for one position:

1. **The strategy's roll** — `CLOSE_GROUP` "Adjusting straddles" on the
   evaluation on which the gate passes and the leg set changes; the same
   tick opens the replacement. This is the only exit the strategy itself
   produces, and it is never a net exit.
2. Live, `StrategyRiskGuardService.SweepAsync` every 3 s: the position's own
   `StopLossPrice` / `TargetPrice` (never set here); leg rules
   (`EvaluateLeg`); group rules (`EvaluateGroup`); overall rules
   (`EvaluateOverall`, flattens and stops the run). The replay engine applies
   the same three levels after every bar's marks (`_apply_leg_rules`,
   `_apply_group_rules`, `_check_overall`), plus the day roll-over.
3. Outside the sweep, on their own clocks — whichever takes the run's lock
   first: a manual square-off or the run's stop button; market close
   (`MarketHoursService`, 15:30 IST, weekdays). In replay: the EOD square-off
   at `eod_square_off_ist` (15:15 default) and the end of the range ("End of
   backtest", run 118 signal 1705 at 14:50).

After 2 or 3 the strategy's state is stale (see Position management): the
next passing gate emits a `CLOSE_GROUP` that the ledger ignores and an
`OPEN_GROUP` that re-establishes the full structure.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.7 (`variants.py`; also the class default) | $a$: distance from the centre strike, in strikes, at which a rebuild is forced — the cut from three straddles to two, and the re-centre of a pair. 35 points on NIFTY, 70 on BANKNIFTY | Doubled positions are held further from the centre (at 1.0 the cut waits for the spot to pass the neighbour strike); fewer rolls, each realising a larger move | More frequent cuts and re-centres; below `minor_steps` the minor gate never matters |
| `minor_steps` | 0.1 | $m$: dead band around the centre; a crossing smaller than this is ignored. 5 points on NIFTY, 10 on BANKNIFTY | Fewer doublings on noise at a strike; the third straddle is added later | More doublings on smaller crossings; 0 or a negative value falls back to the default (`steps_from_params` keeps only positive values), so the band cannot be removed |
| `adjustment_threshold`, `minor_threshold` | — | Legacy spellings in points on a 100-point grid (`steps_from_params`, `LEGACY_GRID`): a stored 70 reads as 0.7 strikes. The `_steps` key wins when both are present | | |
| `strike_step` | none (chain) | Overrides the grid $\delta$ (`resolve_step`); every threshold and every strike scales with it | | |
| `direction` | `SELL` (absent) | `BUY`/`LONG`/`B` makes this the buy twin — see `FulcrumQtyAdjustmentBuy` | | |
| `use_hedges` | `true` for a seller | Whether the two wings are bought; `false` removes them and leaves the short straddles naked | | |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | Lots per wing and the base of $q$; straddle legs carry $2\,\times$ `lots` when three are active. P&L = points × lots × lot size | Larger positions, same signals | |
| `max_hold_minutes`, `target_steps` | 45, 2.0 (`resolve_long_exit`) | Parsed for every variant but used only when `direction` is `BUY` (`max_hold_minutes`) or by `FulcrumStrategy` (`target_steps`); no effect on this seller | | |

## Worked example

Run **118** — OfflineReplay, `NSE:NIFTY50-INDEX`, NIFTY, 5m, one session
(2026-09-09), executed 2026-09-11 00:08 IST. `ParametersJson` verbatim:
`{"adjustment_steps":0.7,"minor_steps":0.1,"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Lot size 65 (frozen into the run from the master; `instruments` rows
`NSE:NIFTY2691523500CE` … all 65), expiry 2026-09-15, strike step 50 (every
strike in the orders is on the 50 grid). Timestamps in the tables are UTC;
all times below are IST (UTC + 5:30). Every number is a row: `spot_price` in
`simulation_signals.MetadataJson` is the driver bar's close, the fills are
the option candle closes, and the state transitions were reproduced by
feeding the same 63 closes through the class itself.

**Before the first fill.** The 09:15 bar (close 23500.90) produced group
`…034500-001` — wings 22650 / 24350 and straddles [23500, 23550] — but the
23550 CE/PE candles only start at 09:35, so the replay skipped it ("no
premium history", `skippedEntries[0]`). The 09:55 bar (23488.85, 11.15 below
23500 with no 23450 straddle) rolled into `…042500-002`, three straddles at 2
lots; the 23450 candles start at 10:00, so that was skipped too. The strategy
did not know: its `CLOSE_GROUP`s for those two groups (6 + 8 legs) were
ignored — the summary's "14 close legs ignored: no matching open position".

### Signal 1688 — 10:20, first fill → orders 3314–3319 → positions 1668–1673

Bar close **23537.80**. State $(23450, 23500, 23550)$ from the unfilled group
002: $S - M = 37.80 \ge a = 35$ → rebuild; nearest strike 23550 is above the
spot, so $L = 23500$, $M = 23550$, and the old $U = 23550$ is the new centre
(dropped as a separate slot) → `Active: [23500, 23550], Qty: 1`. Wings from
the spot: $0.035 \times 23537.80 = 823.82$; $\lfloor 22713.98\rfloor_{50} =
22700$, $\lceil 24361.62\rceil_{50} = 24400$. Fills (group
`FULCRUM-QTY-20260909045000-003`): BUY 1 `NIFTY2691522700PE` @ 5.65, BUY 1
`24400CE` @ 3.45, SELL 1 `23500CE` @ 181.75, SELL 1 `23500PE` @ 87.45,
SELL 1 `23550CE` @ 150.55, SELL 1 `23550PE` @ 106.40.

### Signals 1689 + 1690 — 10:35, re-centre with the same strikes → orders 3320–3331

Bar close **23512.05**. State $(23500, 23550, 0)$: $M - S = 37.95 \ge 35$ →
rebuild; nearest 23500 is below the spot, so $M = 23500$, $U = 23550$, and
$L' = 23500$ is absorbed → the same pair [23500, 23550]. The wings, from
23512.05, move to 22650 / 24350, so the leg set differs and the whole group
is closed (orders 3320–3325) and re-opened as group `…050500-004` (orders
3326–3331) at the same candle closes — `23500CE` bought back at 164.20 and
sold again at 164.20. Group 003's realised P&L, position by position:

$$
\begin{aligned}
\texttt{23500CE (1670)}:\ &(181.75 - 164.20) \times 1 \times 65 = 1140.75\\
\texttt{23500PE (1671)}:\ &(87.45 - 97.90) \times 65 = -679.25\\
\texttt{23550CE (1672)}:\ &(150.55 - 135.20) \times 65 = 997.75\\
\texttt{23550PE (1673)}:\ &(106.40 - 118.80) \times 65 = -806.00\\
\texttt{22700PE (1668)}:\ &(6.05 - 5.65) \times 65 = 26.00,\qquad
\texttt{24400CE (1669)}:\ (3.40 - 3.45) \times 65 = -3.25
\end{aligned}
$$

Sum ₹676.00 (short legs: entry − exit; long wings: exit − entry).

### Signals 1695 + 1696 — 11:55, third straddle, lots doubled → orders 3356–3369

The 11:50 bar (23502.30) had re-centred into group `…062000-006`, pair
[23500, 23550] with state $(0, 23500, 23550)$. Bar close **23488.50**:
$M - S = 11.50$ — at least $m = 5$, less than $a = 35$, and $L' = M \ne
23450$ (no straddle below) → rebuild. Nearest 23500 is above the spot, so
$L = 23450$, $M = 23500$; $U' = 23550 > M$ and $|23550 - 23488.50| = 61.50 <
85$ → kept. `Active: [23450, 23500, 23550], Qty: 2`. Group 006 is closed
(orders 3356–3361, ₹198.25 realised) and group `…062500-007` opened with
**`Quantity` 2 on every straddle leg** and 1 on the wings:

| Order | Leg | Side | Quantity (lots) | FillPrice |
|------|-----|------|-----------------|-----------|
| 3362 | `NIFTY2691522650PE` | BUY | 1 | 5.45 |
| 3363 | `NIFTY2691524350CE` | BUY | 1 | 4.15 |
| **3364** | `NIFTY2691523450CE` | SELL | **2** | 177.20 |
| 3365 | `NIFTY2691523450PE` | SELL | 2 | 85.40 |
| 3366 | `NIFTY2691523500CE` | SELL | 2 | 146.30 |
| 3367 | `NIFTY2691523500PE` | SELL | 2 | 105.45 |
| 3368 | `NIFTY2691523550CE` | SELL | 2 | 119.85 |
| 3369 | `NIFTY2691523550PE` | SELL | 2 | 128.00 |

### Signals 1697 + 1698 — 12:30, back to two straddles → orders 3370–3383

Bar close **23547.85**: $S - M = 47.85 \ge 35$ → rebuild; nearest 23550 is
above the spot → $L = 23500$, $M = 23550$, old $U = 23550$ absorbed →
`Active: [23500, 23550], Qty: 1`. Group 007 is closed at the 12:30 closes;
the doubled legs show the lot multiplier in the P&L:

$$
\begin{aligned}
\texttt{23450CE (1694), order 3372 BUY 2 @ 213.80}:\ &(177.20 - 213.80) \times 2 \times 65 = -36.60 \times 130 = -4758.00\\
\texttt{23450PE (1695), order 3373 BUY 2 @ 65.40}:\ &(85.40 - 65.40) \times 130 = 2600.00\\
\texttt{23500CE (1696)}:\ (146.30 - 179.35) \times 130 = -4296.50,\quad
&\texttt{23500PE (1697)}:\ (105.45 - 81.35) \times 130 = 3133.00\\
\texttt{23550CE (1698)}:\ (119.85 - 149.30) \times 130 = -3828.50,\quad
&\texttt{23550PE (1699)}:\ (128.00 - 99.95) \times 130 = 3646.50\\
\texttt{wings (1692, 1693)}:\ -39.00,\ -13.00
\end{aligned}
$$

Group total **−₹3,555.50**: the spot rose 59.35 points through a doubled
short structure in 35 minutes. Group `…070000-008` (orders 3378–3383) opens
the pair at 1 lot; five minutes later (12:35, 23557.45, 7.45 above the new
centre 23550) it is doubled again into `…070500-009` = [23500, 23550,
23600], which is held until 14:30 (23511.55, 38.45 below 23550) and
realises **+₹5,703.75** — the spot came back.

### Signal 1705 — 14:50, "End of backtest" → orders 3426–3433 → positions 1720–1727

14:30 and 14:35 repeat the pattern (pair `…090000-010`, +₹120.25; then
23492.85, 7.15 below 23500 → triple `…090500-011` at 2 lots, orders
3418–3425). The index candles for the day end at 14:50, so the engine's
end-of-range square-off closed group 011 at the 14:50 closes (spot 23489.55):
e.g. `23450CE` (1722) $(182.75 - 183.70) \times 130 = -123.50$, `23450PE`
(1723) $(77.15 - 76.15) \times 130 = 130.00$; group total +₹195.00.

**Run total:** 9 groups, 120 orders, 60 positions;
$676 - 204.75 + 1088.75 + 198.25 - 3555.50 - 884 + 5703.75 + 120.25 + 195 =$
**₹3,337.75** = `sum(RealizedPnl)` = the summary's `realizedPnl`
(`charges_per_lot` 0, no risk rule, no EOD square-off).

## Limitations

- **"Kept" straddles are still round-tripped.** The comment above
  `keep_nearby` says a nearby straddle is kept "rather than closed and
  re-opened, which would pay the spread for nothing"; the code keeps the
  *strike* but closes and re-opens every leg of the group on every rebuild
  (`closing_legs(old_legs)` + all of `new_legs`). Run 118's 10:35 roll closed
  and re-sold four unchanged straddle legs because the wings moved 50 points.
  The replay hides the cost (one candle close for both fills); live, each
  such leg pays the spread twice and fills at two quotes.
- **Never one straddle.** The class description and `legs_summary` say "one
  to three"; the rebuild always fills the centre and the strike across the
  spot, so it is two or three. The description's "100-point strikes" is
  BANKNIFTY's grid; on NIFTY the same rules run on 50.
- **The first centre is the floor, not the nearest strike**, so a run started
  with the spot $a$ or more above the floor rebuilds on the next evaluation.
  Harmless in run 118 (first close 0.90 above the floor); the BUY twin's
  run 100 was doubled 2.5 s after its first fill.
- **Rebuilds only when the spot moves.** The description's "rebuilt when the
  spot drifts past the adjustment threshold" understates it: a crossing of
  the centre by $m$ (5 points on NIFTY) is enough when the far side is empty,
  and that is the event that doubles the short lots. On 2026-09-09 NIFTY
  crossed 23500 / 23550 often enough for nine groups in 4.5 hours.
- **No stop of its own.** Double-lot short straddles with no leg or group
  rule and wings 3.5 % away: a trending session pays $-2 \times$ the move on
  the legs being left behind for $0.6\,\delta$ of every strike. Set `leg` /
  `group` / `overall` rules on the run; the strategy will not.
- **Stale state after any external close.** A risk-rule, manual or 15:30
  close is invisible to the strategy; its next roll emits `CLOSE_GROUP` legs
  for positions that no longer exist (ignored) and rebuilds the full
  structure, so a group stop does not keep it out of the market.
- **The trigger values are not persisted.** `MetadataJson` carries
  `group_id`, `reason` (with the active strikes and the multiplier),
  `spot_price` and `atm_strike`; the thresholds, the slot state and the wing
  arithmetic have to be rebuilt from the code, as this page does.
  `state["straddle_list"]` is initialised and reset but never written.
- **Replay differs from live.** Closed bars only (one evaluation per 5
  minutes instead of per tick, so intra-bar crossings are invisible and the
  rolls land on bar closes); fills at option candle closes with no spread
  and `charges_per_lot` 0; entries need option history — run 118 skipped its
  first two groups for missing candles, and expired contracts have no broker
  history at all; the day's index candles ended at 14:50, so the run
  squared off there rather than at 15:15; the 13:20–13:40 bars are missing
  from the feed (63 bars, not 68).
- **Live fills are two requests per roll.** The close waits 0 s for quotes
  and the open up to 10 s, so the two halves of a roll fill at different
  quotes; a leg the API cannot price refuses the whole `OPEN_GROUP` and the
  strategy believes it holds a group it does not (the runner prints "The
  strategy still believes this group is open").
- **Expiry choice.** The first expiry on or after today (UTC date live, the
  bar's IST date in replay) — on an expiry day the same-day contract is
  traded.

## Facts (machine-readable)

```yaml
name: FulcrumQtyAdjustment
category: Adjustment
evaluates_on: tick
resolution: tick
data: ticks, option quotes, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: false
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"
Times are converted to IST in the queries.

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" = 118;

/* its signals: 1688-1705 (18 posted) + 1706 BACKTEST_SUMMARY (skippedEntries, dataNotes, realizedPnl) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 118 ORDER BY "Id";

/* fills: 3314-3433 (120 rows; Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 118 ORDER BY "Id";

/* positions: 1668-1727 (60 rows) and the per-group totals */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 118 ORDER BY "Id";
SELECT "GroupId", count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 118 GROUP BY 1 ORDER BY 1;

/* the driver bars (63 session bars 09:15-14:50 IST; the pre-09:15 rows are excluded by feed.driver_bars) */
SELECT "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Open", "High", "Low", "Close"
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" >= '2026-09-09 03:45' AND "TimeStampUtc" < '2026-09-09 10:00' ORDER BY 1;

/* the option candles the fills came from, and why groups 001/002 were skipped (first candle 09:35 / 10:00 IST) */
SELECT "Symbol", min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', count(*)
FROM candles WHERE "Resolution" = '5' AND "TimeStampUtc" >= '2026-09-09' AND "TimeStampUtc" < '2026-09-10'
  AND "Symbol" IN ('NSE:NIFTY2691523450CE','NSE:NIFTY2691523450PE','NSE:NIFTY2691523550CE','NSE:NIFTY2691523550PE')
GROUP BY 1 ORDER BY 1;

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:NIFTY2691523450CE';

State transitions: feed the 63 closes to FulcrumQtyAdjustmentStrategy(params={"adjustment_steps":0.7,"minor_steps":0.1,"lots":1})
with StrategyInput(strike_step=50.0, underlying="NIFTY") and read st0/st1/st2 after each on_bar; the emitted
group ids and reasons match simulation_signals rows 1688-1705 plus the two skipped groups 001 and 002.
-->
