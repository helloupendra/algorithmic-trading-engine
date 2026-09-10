# FulcrumQtyAdjustmentBuy

Source: `strategies/fulcrum/fulcrum_qty_adj.py` (`FulcrumQtyAdjustmentStrategy`),
registered under this name by `strategies/variants.py` with
`adjustment_steps=0.7, minor_steps=0.1, direction="BUY"`. The same class as
`FulcrumQtyAdjustment` (the seller), turned into a buyer by
`_direction.resolve_direction`: every straddle leg is `BUY` instead of
`SELL`, and because `use_hedges` defaults to `direction == SELL` it holds no
wings. Shared helpers: `strategies/strike_math.py` (grid arithmetic),
`strategies/fulcrum/_exit_rules.py` (`closing_legs`, `resolve_long_exit`,
`long_time_stop_close` — the buyer's time stop) and
`strategies/fulcrum/_direction.py`. Every rule below is read from the code;
where the code and its docstrings disagree, the code is documented and the
difference listed under Limitations.

## Idea

The strategy is long two straddles on the two strikes that bracket the spot
and re-centres that structure as the spot drifts, using the seller's
thresholds unchanged. Its quantity ladder is what distinguishes it from
`FulcrumMultiBuy70`: whenever the spot crosses the centre strike a third
straddle is bought on the far side and *every* long leg is carried in double
lots; when the spot has drifted 0.7 of a strike from the centre the structure
is cut back to two straddles at single lots, realising the P&L. A group that
has not rolled for 45 minutes is closed by a time stop and re-entered on the
next tick. The bet is that a crossing of a strike is the start of a move
large enough to pay for the premium and the decay of six long legs; the
losing case is a spot that oscillates around a strike, which buys the doubled
triple on every crossing and sells it 0.7 strikes later. Run 100 below is one
such session; nothing in this repository tests the bet beyond it.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTYBANK-INDEX` in run 100, `NSE:NIFTY50-INDEX`, …) | every tick | none — `get_data_requirements` is not overridden (returns `[]`), so there is no warm-up and the first tick is the first evaluation (run 100: the first tick after an 11:03:42 start) | ingestor → Redis stream `market:ticks`; `execution_runner.py` calls `on_bar` once per tick with `spot_price` = the tick's LTP |
| option quotes | the legs it builds: `UNDERLYING_CE_<strike>` / `UNDERLYING_PE_<strike>` resolved to the exact contract of the nearest expiry (`resolve_leg_symbol` → `get_exact_contract`) | latest quote | none | the runner puts every leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s, `core/leg_pricing.DEFAULT_WAIT_SECONDS`) for a quote on an `OPEN_GROUP`, 0 s on a `CLOSE_GROUP` (`enrich_signal_leg_prices`); the API fills at that quote |
| index candles (replay only) | the spot symbol | the run's resolution | none | `candles` (backfill); `backtest/engine.py` calls `on_bar` once per closed session bar with `spot_price` = the bar's close |
| option candles (replay only) | the same legs | the run's resolution | none | `candles` (backfill); a leg fills at the close of its candle on the signal bar (`feed.option_close_at`); an `OPEN_GROUP` with any leg lacking a candle is skipped |

The strike step is read once from the option chain of the chosen expiry
(`resolve_strike_step` → `strike_step_from_chain`: the smallest gap between
listed strikes; run 100 logged "Strike step 100 derived from 714 contracts of
expiry 2026-09-29") and handed in as `inp.strike_step`. The default
`get_contract_requirements` (ATM CE and PE) is resolved and watchlisted on
every tick, but this class never reads `inp.contracts` — its legs are built
from strikes. It never reads bars, option chain OI or Greeks.

## Timeframe

- **Live: every tick, no bar.** `on_bar` runs on every spot tick; the only
  clock the strategy sees is `inp.timestamp_utc` (the tick's
  `exchangeTimestampUtc`, else `receivedUtc`), which stamps the group id,
  `group_entry_utc` and the time stop. Rolls can be seconds apart — run 100:
  12:52:24 → 12:52:48 → 12:55:38 → 12:55:44 IST, four groups in 3½ minutes.
- **Backtest: once per closed session bar** at the run's resolution
  (`feed.driver_bars`: bars whose start is in 09:15–15:30 IST), with the bar
  close as the spot and the option candle close of the same bar as the fill.
  The time stop is measured on bar timestamps there.
- **Session window:** none of its own. It enters on the first evaluation,
  whatever the time (run 100: 11:03:49 IST, seven seconds after the run was
  started), and re-enters on the tick after every time stop; there is no
  last-entry time (run 100's last group opened at 14:57:58). The replay
  skips entries after `eod_square_off_ist` (15:15 by default).
- **15:30:** `MarketHoursService` checks once a minute on weekdays and at or
  after 15:30 IST stops every run with flatten; the API closes each open
  position with a `CLOSE_GROUP` "Market closed (15:30 IST)" at the latest
  quote (run 100: signal 1475 at 15:30:43, then `RUN_STOPPED` by
  `market-hours` at 15:30:46). The strategy's own time stop can leave it
  flat for one tick only, so it relies on the market close to end the day.

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

At the defaults on BANKNIFTY ($\delta = 100$): $a = 70$, $m = 10$, $k = 170$;
on NIFTY ($\delta = 50$): $a = 35$, $m = 5$, $k = 85$. The thresholds are
strike multiples, so 0.7 means "seven tenths of the way to the next strike"
on every grid — the same decision, not the same number of points.

The state is three strike slots $L, M, U$ (`st0`, `st1`, `st2`), 0 meaning
empty: $M$ is the centre straddle, $L$ and $U$ the ones below and above it.
Fresh state — at start and again after every time stop — is $(0, 0, 0)$.

### First evaluation

With $M = 0$ the threshold gate below cannot hold ($S - 0 \ge a$) and the
rebuild takes the `st1 < price` branch without first snapping $M$ to the
nearest strike (that line is guarded by `if st1 != 0`), so the first group is
always the two strikes around the spot with the **floor** as centre:

$$
(L, M, U) \leftarrow \left(0,\ \lfloor S\rfloor_\delta,\ \lceil S\rceil_\delta\right).
$$

In words: on the first tick, buy the straddle at the strike just below the
spot and the one just above it, and remember the lower strike as the centre
— even when the upper one is nearer. When the spot is already $a$ or more
above the floor the very next evaluation rebuilds: run 100 started at
56589.05 (89 above 56500) and its second tick, 2.5 s later at 56610.10, was
already the doubled triple (signals 1425/1426).

### Legs and contracts

For the active straddles $A = \{x \in \{L, M, U\} : x > 0\}$ (always two or
three, see Position management) with straddle lots $q$, and for each
$x \in A$ in ascending order:

`BUY` $q$ × `UNDERLYING_CE_`$x$ and `BUY` $q$ × `UNDERLYING_PE_`$x$ —
four legs for a pair, six for a triple. No wings: `use_hedges` is false for
a buyer unless the run sets `use_hedges: true`, in which case the seller's
two long wings (`hedge_strike`, 3.5 % from the spot, `lots` each) are added
— a hedged long, "a legitimate structure, just not the default".

**Contract.** A logical leg `BANKNIFTY_CE_56500` is resolved by the runner
(`resolve_leg_symbol`) or the replay (`ContractResolver.resolve_logical`) to
the exact row of the instrument master at that strike for the **first expiry
on or after today** (`valid_expiries[0]` live, on the UTC date;
`expiry_for(day)` in replay) — the 2026-09-29 monthly in run 100
(`NSE:BANKNIFTY26SEP…`; the master lists no nearer BANKNIFTY expiry). A
strike the master lacks leaves the leg unpriced: live, the API refuses the
whole group ("No price is available … rejected rather than filled at zero");
in replay the entry is skipped. In both cases the strategy has already
recorded the group as open (the state is written inside `on_bar`), so its
next roll emits a `CLOSE_GROUP` for legs that were never filled, which the
ledger ignores.

**Quantity.** `lots` (`BaseStrategy.lots_from`, `default_lots` = 1; run 100
used 3) is the base of $q$; `paper_orders.Quantity` stores lots and the API
multiplies by the master's lot size (BANKNIFTY 30, NIFTY 65) for P&L.

**Fill.** Live: the API fills a `MARKET_SIM` order per leg at the latest
quote, no slippage; the `CLOSE_GROUP` and the `OPEN_GROUP` of a roll are two
requests, so a leg that is closed and re-bought in the same roll can fill at
two quotes (run 100, 11:03:52: `56500PE` sold at 446.00 by order 2062 and
bought at 445.00 by order 2066). Replay: every leg of a bar fills at its
candle close.

## Position management

Everything after the first group is a rebuild of the whole structure. On each
evaluation the strategy first runs the time stop (below), then decides
whether to touch the position (the gate), recomputes the three slots from the
current spot and — only if the resulting set of (symbol, side, lots) differs
from the group it holds (`_legs_differ`) — emits `CLOSE_GROUP` "Adjusting
straddles" for *every* leg of the old group (`closing_legs`: same contracts,
opposite side, same lots) followed by `OPEN_GROUP` "Fulcrum Qty Adjusted.
Active: [...], Qty: n" for the new set, in one tick. A straddle whose strike
survives the rebuild is sold and bought again like the others; "kept" means
its strike stays in the new group, not that its legs are left alone.

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

In words: a spot exactly on a strike is ignored; a spot less than $m$ (10
points on BANKNIFTY) from the centre is ignored; a spot between $m$ and $a$
(70 points) from the centre is ignored *only if the straddle one strike away
on that side is already held*. So a move of at least $a$ from the centre
always rebuilds, and a move of at least $m$ rebuilds when it is towards a
side with no straddle.

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
no cap other than $2\,\times$ `lots` (6 lots per leg in run 100; the inline
comment says the original script "doubles up some legs" — here it is every
straddle leg). Because the
rebuild always fills the centre and the strike on the other side of the
spot, $|A|$ is 2 or 3 — never 1, whatever the class description says.
Derived from the rules above (not stated in the code):

- **Pair → triple (lots doubled).** From a pair $\{M, P\}$ the spot leaves
  the interval through the centre $M$ by at least $m$ towards the side with
  no straddle. The rebuild puts the new pair at $M$ and the strike beyond,
  and keeps $P$ (one strike from $M$, within $k$ of the spot), giving
  $\{M-\delta, M, M+\delta\}$ at $2\,\times$ `lots` — the strike the spot
  just crossed is the middle of the three. Run 100 at 12:52:48: spot
  56613.75, 13.75 above the centre 56600, no 56700 straddle held →
  $\{56500, 56600, 56700\}$, 6 lots.
- **Triple → pair (lots single).** From $\{M-\delta, M, M+\delta\}$ both
  neighbours are held, so the gate waits until $|S - M| \ge a$. The nearest
  strike is then the neighbour on that side, the pair is the two strikes
  around the spot, and the old far straddle is either the new centre or
  beyond it and dropped. Run 100 at 12:52:24: spot 56570.90, 70.90 above
  56500 → $\{56500, 56600\}$, 3 lots.
- **Pair → pair (re-centre), silent.** From a pair the spot moves at least
  $a$ from $M$ while staying inside the interval — within $\delta - a$ (30
  points on BANKNIFTY) of the other strike: the strikes are unchanged and
  only the centre flips to that strike. With no wings the leg set is
  identical, `_legs_differ` is false and **nothing is emitted**; the state
  changes all the same. (The seller re-centres its wings and rolls; the
  buyer does not, which is why run 100 has 14 groups where the seller would
  have had more.)

Derived consequences. In a steady trend the cut at $M + a$ leaves the centre
on the strike ahead of the spot, and the next doubling comes $m$ past that
strike, $\delta - a + m$ (40 points on BANKNIFTY) later: the structure is
single for $0.4\,\delta$ of every strike travelled and doubled for
$0.6\,\delta$, and the doubled straddles are the ones the spot is moving
away from — for a buyer, the two whose winning leg is gaining and losing
leg is decaying. Run 100, 12:52 to 12:56: cut at 56570.90, doubled at
56613.75, cut at 56676.55, doubled at 56712.60. With tick-by-tick
evaluation the $k$ test never decides anything (the outer straddle is
always exactly one strike from the new centre); it can only drop a straddle
after a jump of more than one strike between two evaluations.

### Time stop (`long_time_stop_close`, `_exit_rules.py`)

Before the gate, on every evaluation while a group is open:

$$
t_{\text{now}} - t_{\text{entry}} \ \ge\ \texttt{max\_hold\_minutes}
\implies \text{CLOSE\_GROUP},\ (L, M, U) \leftarrow (0, 0, 0),
$$

where $t_{\text{entry}}$ is `group_entry_utc`, written on **every**
`OPEN_GROUP` — so each roll restarts the 45-minute clock, and the stop only
fires on a group that has gone 45 minutes without a roll. The reason is
"Time stop: held N minutes without the move this position was opened for;
decay is the only thing working." with `exit: "time_stop"` in the metadata;
`on_bar` returns at once, and the next tick re-enters from the fresh state
(the first-evaluation pair). Run 100: signal 1434 at 11:48:51.747, exactly
45 minutes after group 002 opened at 11:03:51.737, followed by the re-entry
1435 at 11:48:52.106; and signal 1452 at 13:40:44.994 for group 008 (opened
12:55:44.902), re-entry 1453 at 13:40:45.364. `target_steps` — the other
buyer exit `_exit_rules.py` describes — is parsed by `resolve_long_exit` but
this class never calls `long_exit_reason`, so it has no effect here.

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
  rule measures one structure between two rolls.
- `overall` — rupees on the run's total realized + unrealized P&L; a trip
  flattens everything and ends the run. The rule's `scope` (`"day"` by
  default, or `"run"`) is honoured by the backtest engine only.

Run 100 was launched with `overall.target = 5000` (the runner's `[CONFIG]`
line) and changed at 11:05:47 by the admin to `overall: {stopLoss: 25000,
target: 40000, scope: "day"}` (signal 1428, `RISK_UPDATED`); neither
tripped — the run's worst booked point was −₹13,104 (after group 013). The
strategy relies on none of them.

**What the strategy never does:** it never stays flat (a roll re-opens in
the same tick; a time stop re-opens on the next), never sets
`StopLossPrice` / `TargetPrice` (null on all 70 positions of run 100), never
reads its own P&L or the premium paid, never widens its thresholds for a
buyer (they are the seller's, "so the two can be compared on identical
data", `variants.py`), and never re-enters after an external close except by
rolling into a full new structure.

## Exit

In order of precedence for one position:

1. **Time stop** — `CLOSE_GROUP` with `exit: "time_stop"`, checked first on
   every evaluation, 45 minutes (`max_hold_minutes`) after the group's last
   `OPEN_GROUP`. The only net exit the strategy has; flat until the next
   tick.
2. **The strategy's roll** — `CLOSE_GROUP` "Adjusting straddles" on the
   evaluation on which the gate passes and the leg set changes; the same
   tick opens the replacement (11 of run 100's 14 closes).
3. Live, `StrategyRiskGuardService.SweepAsync` every 3 s: the position's own
   `StopLossPrice` / `TargetPrice` (never set here); leg rules
   (`EvaluateLeg`); group rules (`EvaluateGroup`); overall rules
   (`EvaluateOverall`, flattens and stops the run). The replay engine applies
   the same three levels after every bar's marks.
4. Outside the sweep, on their own clocks — whichever takes the run's lock
   first: a manual square-off or the run's stop button; market close
   (`MarketHoursService`, 15:30 IST, weekdays; run 100 signal 1475). In
   replay: the EOD square-off at `eod_square_off_ist` (15:15 default) and
   the end of the range.

After 3 or 4 the strategy's state is stale: the next passing gate (or the
time stop) emits a `CLOSE_GROUP` that the ledger ignores, and the next
rebuild re-establishes the full structure.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.7 (`variants.py`; also the class default) | $a$: distance from the centre strike, in strikes, at which a rebuild is forced — the cut from three straddles to two, and the silent re-centre of a pair. 70 points on BANKNIFTY, 35 on NIFTY | Doubled positions ride further before the cut realises them (at 1.0 the cut waits for the spot to pass the neighbour strike); fewer rolls — `variants.py` suggests widening it "if the rolling proves too costly" | More frequent cuts; below `minor_steps` the minor gate never matters |
| `minor_steps` | 0.1 | $m$: dead band around the centre; a crossing smaller than this is ignored. 10 points on BANKNIFTY, 5 on NIFTY | Fewer doublings on noise at a strike; the third straddle is bought later into the move | More doublings on smaller crossings; 0 or a negative value falls back to the default (`steps_from_params` keeps only positive values), so the band cannot be removed |
| `direction` | `BUY` (`variants.py`) | `BUY`/`LONG`/`B` = this variant; anything else is the seller `FulcrumQtyAdjustment` | | |
| `use_hedges` | `false` for a buyer | `true` adds the seller's two long wings (3.5 % from the spot, `lots` each) to every group | | |
| `max_hold_minutes` | 45 (`DEFAULT_MAX_HOLD_MINUTES`) | Time stop: minutes since the group's last `OPEN_GROUP` before it is closed. A value ≤ 0 or non-numeric falls back to 45 (`resolve_long_exit`), so it cannot be switched off | Longer holds through quiet spells, more decay paid | Earlier exits; the re-entry on the next tick buys the pair again at once |
| `target_steps` | 2.0 | Parsed, unused by this class (see Time stop) | — | — |
| `adjustment_threshold`, `minor_threshold` | — | Legacy spellings in points on a 100-point grid (`steps_from_params`, `LEGACY_GRID`): a stored 70 reads as 0.7 strikes. The `_steps` key wins when both are present | | |
| `strike_step` | none (chain) | Overrides the grid $\delta$ (`resolve_step`); every threshold and every strike scales with it | | |
| `lots` (run parameter; legacy `quantity`) | `default_lots` = 1 | Base lots per leg; every straddle leg carries $2\,\times$ `lots` when three are active (run 100: 3 → 6). P&L = points × lots × lot size | Larger positions, same signals | |

## Worked example

Run **100** — LivePaper, `NSE:NIFTYBANK-INDEX`, BANKNIFTY, started
2026-09-09 11:03:42 IST, stopped 15:30:46 by `market-hours`.
`ParametersJson` (as stored after the 11:05:47 risk edit):
`{"adjustment_steps":0.7,"minor_steps":0.1,"direction":"BUY","lots":3,"underlying":"BANKNIFTY","risk":{"overall":{"stopLoss":25000,"target":40000,"scope":"day"}},"stop_loss":25000,"target":40000}`.
From the runner's start-up lines (local log
`logs/api-until-20260909-133430.log`, lines 8183–8236): "Loaded 8
parameter(s)", expiry 2026-09-29, strike step 100 from 714 chain contracts,
lot size 30, fresh state, no warm-up bars. Lot size 30 also from
`instruments` (`NSE:BANKNIFTY26SEP56500CE`, `…56400CE`, `…56600PE`,
`…56800PE`: all 30, expiry 2026-09-29). Timestamps in the tables are UTC;
all times below are IST (UTC + 5:30). `spot_price` is from
`simulation_signals.MetadataJson`; ticks between signals are not stored, so
the slot state between them is inferred from the rules (see Limitations).
Thirty signal rows (1424–1476: 14 `OPEN_GROUP`, 14 `CLOSE_GROUP`, one
`RISK_UPDATED`, one `RUN_STOPPED`), 140 orders, 70 positions.

### Signal 1424 — 11:03:49, first tick → orders 2057–2060 → positions 1040–1043

Spot **56589.05**. Fresh state: floor 56500, ceiling 56600, `Active: [56500,
56600], Qty: 1` (group `FULCRUM-QTY-20260909053349.169434+0000-001`). Fills,
BUY 3 lots each: `BANKNIFTY26SEP56500CE` @ 885.65, `56500PE` @ 450.90,
`56600CE` @ 824.45, `56600PE` @ 489.35.

### Signals 1425 + 1426 — 11:03:51, third straddle, lots doubled → orders 2061–2070

Spot **56610.10**, 2.5 s later. The centre is the floor, 56500: $S - M =
110.10 \ge a = 70$ → rebuild; nearest 56600 is below the spot, so $M =
56600$, $U = 56700$, and $L' = 56500 < M$ with $|56500 - 56610.10| = 110.10 <
170$ → kept. `Active: [56500, 56600, 56700], Qty: 2`. Group 001 is sold
(orders 2061–2064, SELL 3) and realised, position by position:

$$
\begin{aligned}
\texttt{56500CE (1040), order 2061 @ 892.85}:\ &(892.85 - 885.65) \times 3 \times 30 = 7.20 \times 90 = 648.00\\
\texttt{56500PE (1041) @ 446.00}:\ (446.00 - 450.90) \times 90 = -441.00,\quad
&\texttt{56600CE (1042) @ 827.95}:\ (827.95 - 824.45) \times 90 = 315.00\\
\texttt{56600PE (1043) @ 484.55}:\ &(484.55 - 489.35) \times 90 = -432.00
\end{aligned}
$$

Sum ₹90.00 (long legs: exit − entry). Group `…053351.737239+0000-002` opens
with **`Quantity` 6 on every leg**:

| Order | Leg | Side | Quantity (lots) | FillPrice |
|------|-----|------|-----------------|-----------|
| 2065 | `BANKNIFTY26SEP56500CE` | BUY | **6** | 892.85 |
| 2066 | `BANKNIFTY26SEP56500PE` | BUY | 6 | 445.00 |
| 2067 | `BANKNIFTY26SEP56600CE` | BUY | 6 | 827.95 |
| 2068 | `BANKNIFTY26SEP56600PE` | BUY | 6 | 483.00 |
| 2069 | `BANKNIFTY26SEP56700CE` | BUY | 6 | 772.00 |
| 2070 | `BANKNIFTY26SEP56700PE` | BUY | 6 | 523.55 |

### Signal 1434 — 11:48:51, time stop → orders 2076–2081 → positions 1044–1049

Spot **56580.10**. Group 002 opened at 11:03:51.737 and no rebuild passed
the gate for 45 minutes (both neighbours of 56600 held, so only $|S -
56600| \ge 70$ would have — the spot must have stayed within 56530–56670,
or the gate would have rolled). At
11:48:51.747 the time stop fired first: "Time stop: held 45 minutes without
the move this position was opened for; decay is the only thing working."
Fills SELL 6: `56500CE` @ 897.45, `56500PE` @ 445.35, `56600CE` @ 835.80,
`56600PE` @ 483.10, `56700CE` @ 779.45, `56700PE` @ 524.00.

$$
\texttt{56500CE (1044)}:\ (897.45 - 892.85) \times 6 \times 30 = 4.60 \times 180 = 828.00,\qquad
\texttt{56600CE (1046)}:\ (835.80 - 827.95) \times 180 = 1413.00
$$

Group total ₹3,744.00 (the six legs: 828 + 63 + 1413 + 18 + 1341 + 81). The
state was reset, and the next tick (signal 1435, 11:48:52, spot 56580.35)
re-entered the fresh pair [56500, 56600] at 3 lots — at the same quotes the
time stop had just sold at (orders 2082–2085: 897.45, 445.35, 835.80,
483.10).

### Signals 1438 + 1439 — 12:07:37, doubled again → orders 2088–2097

Spot **56487.55**. The nearest strike 56500 is above the spot, so $L =
56400$, $M = 56500$, and the old upper straddle 56600 ($112.45 < 170$ from
the spot) is kept: `Active: [56400, 56500, 56600], Qty: 2`. (Whether the
intermediate ticks had silently re-centred the pair onto 56600 — a spot of
56570 or more after 11:48:52 does that without a signal — or not, the gate
passed: 112.45 or 12.45 below the centre, with no 56400 straddle held.)
Group 003 is sold at 842.45 / 494.80 / 782.70 / 535.00 (orders 2088–2091,
−₹607.50) and group `…063737.815886+0000-004` bought at 6 lots:
**order 2092 BUY 6 `56400CE` @ 901.55** → position 1057, plus `56400PE`
455.95, `56500CE` 842.45, `56500PE` 493.95, `56600CE` 782.70, `56600PE`
535.00 (orders 2093–2097, positions 1058–1062).

### Signals 1442 + 1443 — 12:52:24, cut back to a pair → orders 2100–2109

Spot **56570.90**: $S - M = 70.90 \ge 70$ → rebuild; nearest 56600 is above
the spot → $L = 56500$, $M = 56600$, old $U = 56600$ absorbed → `Active:
[56500, 56600], Qty: 1`. Group 004 is sold (orders 2100–2105, SELL 6):

$$
\begin{aligned}
\texttt{56400CE (1057), order 2100 @ 953.30}:\ &(953.30 - 901.55) \times 6 \times 30 = 51.75 \times 180 = 9315.00\\
\texttt{56400PE (1058) @ 409.10}:\ &(409.10 - 455.95) \times 180 = -8433.00\\
\texttt{56500CE (1059)}:\ (886.20 - 842.45) \times 180 = 7875.00,\quad
&\texttt{56500PE (1060)}:\ (444.45 - 493.95) \times 180 = -8910.00\\
\texttt{56600CE (1061)}:\ (828.90 - 782.70) \times 180 = 8316.00,\quad
&\texttt{56600PE (1062)}:\ (480.55 - 535.00) \times 180 = -9801.00
\end{aligned}
$$

Group total **−₹1,638.00**: the spot rose 83.35 points, the calls gained
₹25,506 and the puts lost ₹27,144 — a long straddle's move has to beat the
premium on both sides. Group `…072224.423116+0000-005` (orders 2106–2109)
re-buys the pair at 3 lots; 24 seconds later (12:52:48, 56613.75, 13.75
above the new centre) it is doubled into `…006` = [56500, 56600, 56700],
which the 12:55:38 cut (56676.55) realises at **+₹9,414.00** — the one
clean trend leg of the day.

### The rest, and the close

Group `…007` (pair [56600, 56700], 12:55:38–12:55:44, +₹270) was doubled
six seconds later at 56712.60 into `…008` = [56600, 56700, 56800], which the
second time stop closed at 13:40:44 for **−₹12,447.00** (spot back to
56647.90). Then: re-entry `…009` (+₹2,106), triple `…010` at 14:01:44
(56712.50; −₹13,365.00 when cut at 14:16:40 at 56628.65), pair `…011`
(+₹616.50), triple `…012` at 14:42:20 (56588.25; −₹3,546.00 at 14:46:56),
pair `…013` (−₹733.50), and at 14:57:58 (56489.75, 10.25 below 56500)
the last triple `…092758.822915+0000-014` = [56400, 56500, 56600] at 6
lots, orders 2203–2208. Signal 1475 "Market closed (15:30 IST)" sold it at
15:30:43–46 (orders 2210–2215): `56400CE` (1113) $(933.20 - 913.50) \times
180 = 3546.00$, `56400PE` (1114) $(422.35 - 436.30) \times 180 = -2511.00$;
group total +₹999.00.

**Run total:** 14 groups (11 rolls, 2 time stops, 1 market close), 140
orders, 70 positions;
$90 + 3744 - 607.50 - 1638 + 2992.50 + 9414 + 270 - 12447 + 2106 - 13365 +
616.50 - 3546 - 733.50 + 999 =$ **−₹12,105.00** = `sum(RealizedPnl)` (no
charges modelled). Of the seven doubled groups, the three that saw the spot
reverse by 60 points or more before they were closed lost the most (004,
008, 010: −₹27,450 between them, at 6 lots a leg); the two that were cut in
the direction of their own crossing split (006: +₹9,414; 012: −₹3,546 —
58.75 points of continuation in 4½ minutes did not cover the losing legs).

## Limitations

- **The seller's rolls, applied to a buyer.** `_exit_rules.py` explains why
  a long straddle must not be closed on the ATM change ("cut the winners,
  hold the losers") and says a buyer exits on `target_steps` or
  `max_hold_minutes`. This class uses the seller's gate unchanged plus the
  time stop; `target_steps` is parsed and ignored. A winner is realised at
  $a = 0.7$ strikes from the centre, and a move that reverses inside that
  band pays twice (the doubled legs).
- **The time stop re-enters at once.** `long_time_stop_close` resets the
  state and the next tick buys the fresh pair — run 100 re-bought 1–2 s
  after both time stops, at or near the quotes it had just sold at. It is a
  position refresh, not a pause. And because `group_entry_utc` is rewritten
  by every roll, a structure that keeps rolling is never timed out.
- **"Kept" straddles are still round-tripped.** The comment above
  `keep_nearby` says a nearby straddle is kept "rather than closed and
  re-opened, which would pay the spread for nothing"; the code keeps the
  strike but closes and re-buys every leg of the group on every rebuild, at
  two quotes taken a second or so apart.
- **Never one straddle.** The class description and `legs_summary` say "one
  to three"; the rebuild always fills the centre and the strike across the
  spot, so it is two or three. The description's "100-point strikes" is
  BANKNIFTY's grid; on NIFTY the same rules run on 50.
- **The first centre is the floor, not the nearest strike.** Run 100's
  first pair lasted 2.5 s for that reason: the spot was 89 points above the
  floor at the first tick, so the second tick rebuilt into the doubled
  triple.
- **No premium awareness.** Nothing in the strategy reads the premium paid,
  the P&L or the decay; six long legs at 6 lots (run 100, group 002: 3,944.35 points of
  premium × 180 units, about ₹7.1 lakh) are held on a strike-grid rule
  and a clock. Leg / group / overall rules are the run's to set; run 100's
  overall stop (₹25,000) was wide enough never to trip.
- **Stale state after any external close.** A risk-rule, manual or 15:30
  close is invisible to the strategy; its next roll or time stop emits
  `CLOSE_GROUP` legs for positions that no longer exist (ignored) and the
  next rebuild buys the full structure again.
- **The trigger values are not persisted.** `MetadataJson` carries
  `group_id`, `reason` (the active strikes and the multiplier),
  `spot_price`, `atm_strike` and, on a time stop, `exit` and `underlying`;
  the slot state and the gate have to be rebuilt from the code. Ticks
  between signals are not stored, so a silent re-centre (identical legs, no
  signal) between two signals cannot be seen in the database — the worked
  example says where that matters. `state["straddle_list"]` is initialised
  and reset but never written.
- **Signal rows carry the class name.** `simulation_signals.StrategyName`
  for run 100 is `FulcrumQtyAdjustment` (`StrategySignal.strategy_name =
  self.name`), while the run and its `paper_positions` say
  `FulcrumQtyAdjustmentBuy`; filter by `SimulationRunId`, not by name.
  (`FulcrumMultiStraddleStrategy` appends a `variant_label`; this class does
  not.)
- **Replay differs from live.** Closed bars only (one evaluation per bar, so
  intra-bar crossings are invisible and the time stop lands on bar
  boundaries); fills at option candle closes with no spread and
  `charges_per_lot` 0; entries need option history, and expired contracts
  have no broker history at all. No replay of this variant is cited here.
- **Live fills are two requests per roll.** The close waits 0 s for quotes
  and the open up to 10 s; a leg the API cannot price refuses the whole
  `OPEN_GROUP` and the strategy believes it holds a group it does not (the
  runner prints "The strategy still believes this group is open").
- **Expiry choice.** The first expiry on or after today (UTC date live, the
  bar's IST date in replay); for BANKNIFTY that is the monthly (2026-09-29
  on 2026-09-09), for NIFTY the weekly — on an expiry day the same-day
  contract is traded.

## Facts (machine-readable)

```yaml
name: FulcrumQtyAdjustmentBuy
category: Adjustment
evaluates_on: tick
resolution: tick
data: ticks, option quotes, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: true
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"
Times are converted to IST in the queries.

/* the run */
SELECT "Id", "Mode", "Symbol", "StrategyName", "Status",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist,
       "CompletedUtc" AT TIME ZONE 'Asia/Kolkata' AS completed_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" = 100;

/* its 30 signals: 1424-1476 (ids in between belong to other runs) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 100 ORDER BY "Id";

/* fills: 140 rows, 2057-2215 with gaps (Quantity is lots: 3 for a pair, 6 for a triple) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 100 ORDER BY "Id";

/* positions: 70 rows, 1040-1118 with gaps, and the per-group totals */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 100 ORDER BY "Id";
SELECT right("GroupId", 3), count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 100 GROUP BY 1 ORDER BY 1;
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 100;

/* lot size and expiry */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments
WHERE "Symbol" IN ('NSE:BANKNIFTY26SEP56400CE','NSE:BANKNIFTY26SEP56500CE','NSE:BANKNIFTY26SEP56600PE','NSE:BANKNIFTY26SEP56800PE');
SELECT DISTINCT "ExpiryDate" FROM instruments WHERE "Underlying" = 'BANKNIFTY' AND "ExpiryDate" >= '2026-09-09' ORDER BY 1 LIMIT 3;

Runner start-up lines cited (local, gitignored): logs/api-until-20260909-133430.log, lines 8183-8236
([CONFIG] with the launch-time overall target 5000, "Using expiry: 2026-09-29", "Strike step 100 derived
from 714 contracts", "Lot size 30", "[STATE] Fresh strategy state", "Warmup complete").
-->
