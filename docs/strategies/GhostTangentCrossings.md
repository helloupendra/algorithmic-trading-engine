# GhostTangentCrossings

Source: `strategies/ghost_tangent_crossings.py` (`GhostTangentCrossingsStrategy`).
Every rule below is read from the code; where the code and its docstring
disagree, the code is documented and the difference listed under Limitations.

## Idea

The strategy joins the last two ZigZag pivots of the 5-minute index chart with
a quarter-ellipse, takes the tangent at that arc's mid-height and extends it
forward as a moving trigger line. A close on the far side of the line after
the later pivot is read as the swing failing to keep the pace the arc implied:
after an up-swing that is a sell (buy the ATM put), after a down-swing a buy
(buy the ATM call). The bet is that a swing which loses its curvature-implied
momentum reverses far enough for a long option to pay; nothing in this
repository tests that beyond the runs cited below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, `BSE:SENSEX-INDEX`, …) | 5m | `on_bar` evaluates nothing until $2F+1 = 51$ bars exist; a confirmed line needs two confirmed pivots of opposite kind. The runner warms up on the last 500 bars of 15 calendar days (≈ 6.7 sessions). | Warm-up: FYERS history called directly (`core.data_engine.DataEngine.get_historical_bars`), not the `candles` table. Live: `live_bars` 1m rows aggregated to 5m on read (`GET /api/LiveData/bars?resolution=5m&take=500`), newest bar still forming. Backtest: `candles` (backfill). |
| ticks | the same spot symbol | every tick | none | ingestor → Redis stream `market:ticks`; each tick is one evaluation |
| option quotes | ATM CE and ATM PE of the nearest expiry (`get_contract_requirements` default) | latest quote | none | the runner puts both contracts on the ingestor watchlist; fills use the latest quote |

It never reads option candles or option-chain OI.

## Timeframe

- **Bar:** 5-minute index bars. Bars are indexed by a counter $n$
  (`bar_index`) that advances once per distinct newest-bar timestamp string;
  the warm-up leaves it at 500 and live bars continue from there. The bucket
  labels in the worked example (bar 500 = the 09:15 bucket) hold because the
  last warm-up bar — FYERS's 09:15 candle, fetched at 09:17:41 — and the
  first live bucket carry the same string (`…T03:45:00Z` from both sources),
  so the first tick does not advance the counter; a run started before 09:15
  would count its first live bucket as 501.
- **Live: every tick, on a forming bar.** The runner calls `on_bar` on every
  spot tick with up to 500 5m bars (`GET /api/LiveData/bars`, `take=500`;
  the list holds only the sessions the ingestor ran, so on 2026-09-09 it was
  235 bars at 09:17 and 297 by the close), and the
  scan includes bar $n$, so a crossing can fire mid-bar. The strategy never
  reads the tick itself (`inp.spot_price` is untouched; the LTP only feeds
  the metadata's `spot_price` / `atm_strike`): the newest bar's close is the
  latest 1m `live_bars` row's close as the bar writer left it, which equals
  the LTP only while the writer is current. No entry of run 96 is an example
  of a mid-bar fire — its four crossing bars were 3, 7, 3 and 17 bars behind
  the newest bar at the time.
- **Backtest: once per closed driver bar** (`backtest/engine.py`; the driver
  runs at the run's resolution, 5m in run 75, and the 5m list holds every
  candle complete by the end of that bar, so the newest bar has closed). The
  list is uncapped there (`feed.bars_upto` only appends), so the 500-bar
  limits under Pivots apply to live only.
- **Session window:** none of its own. It evaluates from the first tick the
  runner receives (runs 82–84 and 95–97 started at 09:17:39–09:17:45 IST)
  until the run stops; there is no last-entry time (run 82 entered at 14:05,
  run 96 at 14:44). The backtest skips entries after `eod_square_off_ist`
  (15:15 in run 75).
- **15:30:** `MarketHoursService` checks once a minute and, at or after
  15:30 IST, stops every run with flatten; the API closes each open position
  with a `CLOSE_GROUP` "Market closed (15:30 IST)" at the latest quote
  (run 96: 15:30:28). Weekdays only, once per calendar day (an in-memory
  flag, so an API started after 15:30 fires it on start-up): a run started
  after the day's sweep holds until the next weekday's sweep. The strategy
  relies on this — it has no exit of its own.

## Entry

### Notation

Bars are $1,\dots,n$ with $n$ the newest; $O_i,H_i,L_i,C_i$ its prices.
$F$ = `pivot_forward`. Pivot prices depend on `pivot_type`:

$$
h_i = \begin{cases} H_i & \text{pivot\_type} = \text{"Wick"} \\ \max(O_i, C_i) & \text{otherwise}\end{cases}
\qquad
l_i = \begin{cases} L_i & \text{pivot\_type} = \text{"Wick"} \\ \min(O_i, C_i) & \text{otherwise}\end{cases}
$$

(The code compares to the exact string `"Wick"`; any other value means body.)

### Pivots (`_get_pivot`)

Only the bar $p = n - F$ is ever a candidate. It is a **pivot high** with left
window $B_H$ when

$$
h_p \ge h_{p-i}\ \ (1 \le i \le B_H) \quad\land\quad h_p > h_{p+i}\ \ (1 \le i \le F),
$$

and a **pivot low** with left window $B_L$ when
$l_p \le l_{p-i}\ (1 \le i \le B_L)$ and $l_p < l_{p+i}\ (1 \le i \le F)$.
The right window includes the forming bar. A pivot is therefore confirmed
$F$ bars ($5F$ minutes, 125 at the default) after it printed. Both windows
start at $F$. Once a leg has been confirmed they are recomputed on every
evaluation from the previous values (the right-hand sides are the values
before the update):

$$
B_H \leftarrow \mathrm{clip}\!\left(n - x_{L^*} - B_L + 1,\ 5,\ 500\right),\qquad
B_L \leftarrow \mathrm{clip}\!\left(n - x_{H^*} - B_H + 1,\ 5,\ 500\right),
$$

where $x_{L^*}$ / $x_{H^*}$ are the bar indices of the pivot low / high that
ended the last confirmed down-leg / up-leg (only the first line runs while no
up-leg has been confirmed, and vice versa). The pair does not settle where
the docstring's ZigZag would put it. Iterated with both legs set — many
ticks per bar live, one per bar in the backtest — the window of the kind that
would *reverse* the last leg collapses to the floor of 5, smaller than the
initial $F$, while the kind that would *extend* it grows: after a down-leg
($x_{L^*} > x_{H^*}$) $B_H = 5$ and $B_L = n - x_{H^*} - 4$; after an
up-leg $B_L = 5$ and $B_H = n - x_{L^*} - 4$ (clipped at 500). So a
reversal pivot needs only five bars to its left that do not beat it, and an extension
pivot must be the extreme since about the last opposite pivot. (Detection
also needs $\text{len(bars)} > B + F$, i.e. the window must stay below
$\text{len(bars)} - F$. Live that is not a theoretical bound: with the 235–297
bars the list held on 2026-09-09 the ceiling was $B < 210$–$272$, and the
settled up-leg window had grown to about 122 by midday before the 497 → 513
leg reset it. A run whose bar list is short and whose last opposite pivot is
old can therefore stop detecting extension pivots.)

### Legs

A **leg** is an earlier pivot $A = (x_A, y_A)$ and a later pivot
$B = (x_B, y_B)$ of opposite kind. An **up-leg** runs low → high, a
**down-leg** high → low. The state keeps a polarity: `True` after a confirmed
up-leg, `False` after a confirmed down-leg.

- **Confirmed leg** — formed on the evaluation that confirms a pivot, when
  that pivot is later than the newest pivot of the other kind, the previous
  leg of the same kind started earlier, and the last confirmed leg was of the
  other kind. $A$ is the pivot that ended the previous leg (normally the
  newest pivot of the other kind). Forming it flips the polarity, so each
  confirmed leg is evaluated once.
- **Ghost leg** (`use_ghost_signals`) — while the polarity waits for the
  next confirmed pivot of kind $K$, and on every evaluation on which no such
  pivot is confirmed, $B$ is the *provisional* extreme: the bar with the
  highest $h$ (ghost up-leg, waiting for a high) or the lowest $l$ (ghost
  down-leg, waiting for a low) among the bars after the newest confirmed
  pivot of $A$'s kind (`pl_current_idx` / `ph_current_idx`), the forming bar
  included. $A$ is the pivot that ended the last confirmed leg
  (`last_down_end` / `last_up_end`), which is that same pivot only when no
  later pivot of its kind has been confirmed since — after a lower low in a
  downtrend, say, $A$ stays at the older low while $B$ is searched only after
  the newer one, and the bars between the two lows are never candidates.
  This leg is redrawn on every tick, so its line moves as new extremes print.

### Ellipse, tangent and trigger line (`_generate_ellipse`, `_ellipse_slope`)

With $a = x_B - x_A$ (bars) and $b = y_A - y_B$ (points; negative for an
up-leg), the arc is the quarter-ellipse centred on the corner $(x_A, y_B)$:

$$
P_\theta = \left(x_A + \lfloor a\cos\theta \rfloor,\ y_B + b\sin\theta\right),\qquad \theta = 0^\circ, 1^\circ, \dots, 90^\circ,
$$

keeping the first point of each distinct integer $x$ and appending the exact
$(x_A, y_A)$. $P_0$ is the later pivot $B$, the last point the earlier pivot
$A$: the arc leaves $A$ horizontally and arrives at $B$ vertically. (When
$a \le 1$ the "arc" is just the two pivots; see Limitations.)

The tangent point $T = P_j$ is the kept point that splits the arc's vertical
travel most evenly, with $d_k = |y_{k+1} - y_k|$ between consecutive kept
points $P_0 \dots P_m$:

$$
j = \arg\min_{1 \le j \le m-1} \left|\ \sum_{k=0}^{j} d_k - \sum_{k=j-1}^{m-1} d_k\ \right|,
$$

i.e. about mid-height of the arc ($\sin\theta \approx \tfrac12$, so
$x_T \approx x_A + 0.87a$). The slope is the ellipse's analytic tangent there,

$$
s = -\,\frac{b^2\,(x_T - x_A)}{a^2\,(y_T - y_B)}\qquad (s = 0 \text{ if the denominator is } 0),
$$

positive after an up-leg, negative after a down-leg, and the **trigger line**
is

$$
\ell(x) = y_T + s\,(x - x_T).
$$

$\ell(n)$ is what the runner prints as the trigger (`[STATUS] … triggers:
BUY CE (up) …, BUY PE (down) …`). Derived by running the class's own
`_generate_ellipse` / `_ellipse_slope` on synthetic legs, not stated in the
code: for legs of 16–50 bars $T$ sits at 48–52 % of the height, $\ell(x_B)$
is 17–25 % of the leg's height back inside the leg and
$|s| \approx 1.7\,|b|/a$ points per bar; short legs (≤ 8 bars) put $T$
higher and the line flatter. Either way the line keeps moving away from the
later pivot at about the swing's own average pace, without bound.

### Crossing (`_check_b`)

Scanning forward from the later pivot to the newest bar,
$x = x_B, x_B+1, \dots, n$, the **crossing bar** $x^*$ is the first bar whose
close is on the far side of the line:

$$
x^* = \min\{\,x \ge x_B : C_x < \ell(x)\,\}\ \text{(up-leg)},\qquad
x^* = \min\{\,x \ge x_B : C_x > \ell(x)\,\}\ \text{(down-leg)}.
$$

$$
\text{signal} \iff x^* \text{ exists} \ \land\ x^* \ne x^*_{\text{last}},
\qquad
\text{SELL (buy ATM PE) after an up-leg},\ \ \text{BUY (buy ATM CE) after a down-leg}.
$$

$x^*_{\text{last}}$ is the crossing bar of the previous signal (`last_signal_idx`,
shared by both directions), so the same crossing bar never fires twice in a
row — only the last crossing bar is remembered — but the scan runs from the
pivot forward: when a confirmed leg is evaluated, $x^*$
can be any of the $F$ bars since the pivot, and the signal fires now for a
close up to $5F$ minutes old. The signal's `reason` names $x^*$
("detected at bar 542") and its `price` is $C_{x^*}$; neither the line nor
$C_{x^*}$ is persisted (see Limitations).

In words: draw the quarter-ellipse through the last two swing points, take
its tangent at mid-height and extend it forward; the first close after the
later swing point that falls under a rising line (or over a falling one) is
the signal, in the direction of the break.

### Contract and size

The runner (`execution_runner.py`, the `GhostTangentCrossings` branch) turns
`BUY`/`SELL` into a one-leg `OPEN_GROUP` — group id `GTC_<unix seconds>`:

- **Strike:** $K = \mathrm{round}(S / \Delta)\cdot\Delta$ — nearest strike
  (`contract_selector.round_to_step`; the ceil in `price_resolver` is not on
  this path) — with $S$ the tick's spot LTP and $\Delta$ the strike step read
  from the chosen expiry's option chain (`strike_step_from_chain`; 50 for
  NIFTY), else `FALLBACK_STRIKE_STEPS`.
- **Expiry:** the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`), so on an expiry day the same-day contract is traded.
- **Contract:** the exact `CE` (BUY) or `PE` (SELL) row of the instrument
  master at $(K, \text{expiry})$ via `ExactContractCache`; if the master
  lacks it the signal is dropped with `ERROR: Could not resolve contract`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  default `default_lots` = 1) is the leg's `quantity`; `paper_orders.Quantity`
  stores lots and the API multiplies by the master's lot size (NIFTY 65) for
  P&L.
- **Fill:** the runner waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for the
  option's live quote, stamps `reason`, `spot_price`, `atm_strike` into the
  metadata (next to the `group_id` and `direction` set above) and posts
  `/api/Simulator/signals`; the API fills a `MARKET_SIM` order at that quote,
  no slippage. If no quote arrives in 10 s the group is sent unpriced and the
  API refuses it ("No price is available … rejected rather than filled at
  zero"); because `last_signal_idx` was written inside `on_bar` before the
  signal left the strategy, a refused signal — or one dropped for a missing
  contract — never re-fires for that crossing bar. The runner's own comment
  says so: "The strategy still believes this group is open."

## Position management

The strategy does nothing after entry. It keeps no record of open positions:
`state` holds pivots, legs and `last_signal_idx` only, so

- a new crossing bar fires a new group even while one is open, in the same
  direction (run 83: three `23650CE` groups at 11:05, 12:30, 14:30) or the
  opposite one (run 83 held the `23700PE` from 09:17 while buying calls);
  opposite signals never close anything;
- it never rolls the strike, never adds to or reduces a position, never
  re-enters deliberately — every entry is an independent signal;
- it never sets a per-position stop or target (`StopLossPrice` /
  `TargetPrice` are null on all 30 positions of runs 82–84 and 95–97).

**What the run's risk rules add.** `parametersJson.risk` carries three levels
that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s, `appsettings.json`), in the order leg → group → overall, each level in
the fixed order stop-loss → trailing stop → target:

- `leg` — per open position, points or percent of `AveragePrice` against
  `LastMarkPrice`; a trip closes that leg only. Runs 95–97 used
  `leg.targetPoints = 20`.
- `group` — rupees on a signal group's realized + unrealized P&L; closes every
  open leg of that group.
- `overall` — rupees on the run's total realized + unrealized P&L
  (`GetPortfolioSummaryAsync`), cumulative since the run started; a trip
  flattens everything and ends the run. The rule's `scope` (`"day"` by
  default, or `"run"`) is honoured by the backtest engine only
  (`backtest/engine.py` measures from the day's opening P&L under `day`);
  the live guard never reads it, which agrees with `day` only because a live
  run is normally one session. Runs 82–84 used `overall.target = 2000`.

The guard trips on the mark it sees and the close fills at the quote the API
reads a moment later, so the booked points can differ from the reason text
(signal 1441 says "+20.4 pts", the fill booked +20.10).

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, a manual square-off or the 15:30 close (weekdays, once
a day — see Timeframe), a position stays open — run 82 closed all five groups
only at 15:30.

## Exit

Live, `StrategyRiskGuardService.SweepAsync` checks one position's closers
in this order on every sweep:

1. The position's own `StopLossPrice` / `TargetPrice` — never set by this
   strategy.
2. Leg rules: stop-loss points, stop-loss percent, trailing stop points,
   trailing stop percent, target points, target percent
   (`EvaluateLeg`; run 96 signals 1416, 1437, 1441).
3. Group rules on the group's P&L (`EvaluateGroup`).
4. Overall rules on the run's total P&L (`EvaluateOverall`) — flattens and
   stops the run (run 83, signal 1373: "Target hit: P&L 2,769 ≥ 2,000").

Two more closers run on their own clocks outside the sweep, with no
precedence relative to it — whichever takes the run's lock
(`SimulationRunLocks.AcquireAsync`) first closes the position:

- a manual square-off or the run's stop button (run 97, signals 1418/1419:
  "Squared off by admin");
- market close: `MarketHoursService` at 15:30 IST (run 96, signal 1472).

The backtest mirrors the same rules (`backtest/engine.py`), with the day's
square-off at `eod_square_off_ist` (15:15 in run 75) instead of 15:30.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `pivot_forward` | 25 | $F$: bars to the right that must not exceed a candidate before it is a pivot; also the initial left window and the minimum-bars guard $2F+1$ | Fewer, larger swings; confirmation lags $5F$ minutes more; confirmed crossings are found further in the past; ghost legs span more bars, so their lines move more slowly | More pivots and legs, faster confirmation, steeper and more often redrawn lines, more signals |
| `pivot_type` | `"Wick"` | `"Wick"`: pivots from $H_i / L_i$; anything else: from $\max/\min(O_i, C_i)$ | n/a — a choice. Body ignores wick-only spikes (the pre-open prints in Limitations would not have become pivots) but moves every other pivot too | |
| `use_ghost_signals` | `true` | evaluate provisional legs on every tick | n/a. `false`: only confirmed legs, so every signal is found at pivot confirmation, up to $F$ bars after the crossing bar; 29 of the 30 entries in runs 82–84 and 95–97 were ghosts | |
| `lots` (run parameter) | `default_lots` = 1 | lots per signal; P&L = points × lots × lot size | larger positions, same signals | |

## Worked example

Run **96** — LivePaper, `NSE:NIFTY50-INDEX`, NIFTY, started 2026-09-09
09:17:41 IST, `ParametersJson` verbatim: `{"pivot_forward":25,"pivot_type":"Wick",
"use_ghost_signals":true,"lots":2,"underlying":"NIFTY","risk":{"leg":{"targetPoints":20.0}},"stop_loss":null,"target":null}`
(the runner logged "Loaded 8 parameter(s)").
From the runner's start-up lines (local log
`logs/api-until-20260909-133430.log`, lines 673–821): expiry 2026-09-15,
strike step 50 from 454 chain contracts, lot size 65, 500 warm-up bars,
fresh state. Every fill, position and bar value below is a database row (the
bar closes are the 5m buckets of `live_bars`; the SQL is at the end of this
file); the trigger and tangent-line values are quoted from the runner log or
rebuilt from the code, because the strategy does not persist them. Times are
IST.

### Signal 1409 — 09:17:45, BUY (Ghost) at bar 497 → order 2042 → position 1031

`MetadataJson`: `direction BUY`, `spot_price 23482.8`, `atm_strike 23500`.
Bar 497 is the 09:00 bucket of `live_bars` — a pre-open print:
O 23548.10, H 23548.10, **L 23036.85**, C 23521.55. Reconstruction: on the
first live evaluation the ghost down-leg ran from the last warm-up pivot high
to that low (the lowest $l$ since); the first bar scanned is the later pivot
itself, and its close 23521.55 sat above the falling line there, so
$x^* = 497$ and the runner printed the signal with `price 23521.55`. The line's value at bar 497 is not
persisted; the current-bar trigger went from 23561.23 (end of warm-up) to
23184.50 after this tick.

Fill: `NSE:NIFTY2691523500CE`, BUY 2 lots @ 158.40 (09:17:47). Exit: signal
1416 at 09:40:40, "Leg target hit: NIFTY 23500 CE +20.0 pts (+12.6%) ≥ 20
pts" → order 2049 SELL 2 @ 178.40.

$$
(178.40 - 158.40) \times 2 \times 65 = 20.00 \times 130 = 2600.00
$$

= ₹2,600.00, the `RealizedPnl` of position 1031.

### Signal 1427 — 11:05:00, SELL (Ghost) at bar 515 → order 2071 → position 1050

`spot_price 23536.1`, `atm_strike 23550`. Bar 515 is the 10:30 bucket,
C **23525.80** (the runner's signal `price`). Reconstructed from the code and
the bars (the runner logs neither pivots nor lines): at 11:05:00 bar 497 was
exactly 25 bars back and its 23036.85 is below every neighbour, so it was
confirmed as a pivot low and the polarity flipped to "down"; on the next
tick the ghost up-leg ran from that low to the highest high since — 23549.00
at 10:20 (bar 513) — with a line rising from there, and the first close under
it was 10:30's 23525.80 ($x^* = 515$, seven bars before the forming 11:05
bar, 522). Immediately after, the runner printed the current-bar trigger
`BUY PE (down) 23944.44` — about 410 points above the spot, the line having risen
for nine bars since the provisional high.

Fill: `NSE:NIFTY2691523550PE`, BUY 2 @ 109.75 (11:05:01). Exit: signal 1437
at 12:05:09, "+20.2 pts (+18.4%) ≥ 20 pts" → order 2087 @ 129.90.

$$
(129.90 - 109.75) \times 130 = 20.15 \times 130 = 2619.50
$$

= ₹2,619.50 (position 1050).

### Signal 1440 — 12:38:36, BUY (Ghost) at bar 535 → order 2098 → position 1063

`spot_price 23487.8`, `atm_strike 23500`. Bar 535 is the 12:10 bucket
(counting from bar 497 = 09:00; `live_bars` has no hole between),
C **23487.80** (the signal `price`). Reconstruction, checked by running the
class's own `_generate_ellipse` / `_ellipse_slope` on the buckets: at
12:38:36 the newest bar in the runner's list was the 12:25 bucket, $n = 538$
— 13 minutes behind the wall clock (the 12:15–12:24 rows of `live_bars` were
all written by 12:38:34; the lag is under Limitations). Bar 513 (10:20,
H 23549.00) was then exactly 25 bars back and was confirmed as a pivot high,
which formed the confirmed up-leg 497 → 513 (its crossing, 515, was already
`last_signal_idx`, so nothing fired) and printed `BUY PE (down) 24802.77` —
$\ell(538)$ of that leg, 1,300 points above the spot. The polarity flipped to
"up", and on the next tick the ghost down-leg was drawn for the first time,
from that high to the lowest low since, 23470.85 at 12:05 (bar 534). Its scan
from 534 stopped at bar 535: $C_{535} = 23487.80 > \ell(535) = 23483.84$, a
crossing three bars (15 minutes) old. The `[STATUS]` line on the same tick
shows `BUY CE (up) 23466.78`, which is $\ell(538)$ — the line at the newest
bar, not at the crossing bar.

Fill: `NSE:NIFTY2691523500CE`, BUY 2 @ 146.35 (12:38:37). Exit: signal 1441
at 12:51:48, "+20.4 pts (+13.9%) ≥ 20 pts" — the mark at the sweep — →
order 2099 @ 166.45.

$$
(166.45 - 146.35) \times 130 = 20.10 \times 130 = 2613.00
$$

= ₹2,613.00 (position 1063).

### Signal 1463 — 14:44:38, SELL (Ghost) at bar 542 → order 2185 → position 1107

`spot_price 23512.1`, `atm_strike 23500`. Bar 542 is the 12:45 bucket,
C **23558.90** (the signal `price`) — the crossing bar was about two hours old
when the sell line was first drawn on this tick (`BUY PE (down)` went from
"waiting for pivot" to 23905.14) and the scan from the later pivot forward
stopped at it. Fill: `NSE:NIFTY2691523500PE`, BUY 2 @ 90.20 (14:44:39). No
rule tripped; the market-close square-off closed it: signal 1472 at 15:30:28,
"Market closed (15:30 IST)" → order 2209 @ 94.75.

$$
(94.75 - 90.20) \times 130 = 4.55 \times 130 = 591.50
$$

= ₹591.50 (position 1107).

Day total, run 96: 2,600 + 2,619.50 + 2,613 + 591.50 = ₹8,424.00 (sum of
`RealizedPnl`, no charges modelled). The three percentages in the
guard's reasons are $20.0/158.40$, $20.2/109.75$, $20.4/146.35$ — the marks
at the sweep against the entry, not the fills.

## Limitations

- **No exit, no position awareness.** Groups stack (run 84 opened eight on
  2026-09-08 and all closed at 15:30); an opposite signal opens a hedge
  instead of closing. A run without a leg / group / overall rule holds until
  15:30.
- **Signals can be stale.** The scan runs from the later pivot forward, so a
  confirmed leg finds its crossing up to $F$ bars in the past and books the
  entry at the *current* option price (signal 1463: crossing at 12:45,
  entry at 14:44). The docstring's "close through the line" is, in the code,
  any close on the far side of a line that moves away from the later pivot
  at $\approx 1.7\,|b|/a$ points per bar without bound — in a flat market an
  up-leg's SELL is a matter of time, not of price (trigger 23905 with the
  spot at 23512).
- **The trigger is not persisted.** `simulation_signals.Symbol` is empty and
  `Price` null for this strategy: `signal_to_request` does not send them, and
  the API would drop them anyway (`PaperTradingService` copies only run id,
  strategy, type, timestamp, group and metadata into the entity).
  `MetadataJson` carries `group_id`, `direction`, `reason`, `spot_price`,
  `atm_strike` — no line value, no crossing close. The line value and
  $C_{x^*}$ exist only in the runner log (`[STATUS]` every 10 s, `SIGNALS`
  block), which is rotated; a verifier has to rebuild them from `live_bars`
  and the code, as the worked example does, with the newest bar at that
  moment inferred rather than recorded.
- **Warm-up and live bars are different series.** Warm-up bars come from
  FYERS history — normally 09:15–15:25, 75 bars a session, though FYERS
  returned a 09:10 IST bar on 2026-09-07 and 2026-09-08 (76 bars) and run
  96's warm-up included them (the logged end-of-warm-up trigger 23561.23
  reproduces only with those bars present). Live bars are `live_bars`
  aggregated on read, which contain the ingestor's pre-open prints (from
  08:45 on 2026-09-08 and 08:44 on 2026-09-09, including the 09:00 spike
  below), holes (13:15–13:45 on 2026-09-09) and only the sessions the
  ingestor ran. Pivot indices from warm-up are addressed by bar count in the
  live list, so they can point at other bars.
- **Pre-open prints become pivots.** The 09:00 bucket of 2026-09-09 printed
  L 23036.85 (over 500 points below its open) and that of 2026-09-08
  H 24190.60; each was the crossing bar ("bar 497") of the day's first signal
  at 09:17:45 (signal 1409 here, 1354 on run 83). With `pivot_type` = body
  these two would have been ignored (their open/close were ordinary), at the
  cost of changing every other pivot.
- **The strategy's clock is the bar writer's, and the ticks were late
  too.** On 2026-09-09 `live_bars.UpdatedUtc` trailed `BarStartUtc` by 15
  minutes at 12:25 and 33 at 12:50; the runner's newest bar at 12:38:36 was
  the 12:25 bucket, 13 minutes behind the wall clock, and no bar after 14:50
  was written before the 15:30 stop. The bar writer files each tick under
  its exchange stamp (`LiveDataService`, `barClockUtc =
  ExchangeTimestampUtc`), so that gap is the arrival delay of the ticks
  themselves, not a slow writer. The tick the runner logged at 12:38:36
  carried an LTP of 23487.80, a price the exchange-stamped 12:38 minute
  (23554.45–23560.75) never printed and the 12:24–12:25 minutes did; the
  runner stamps signals with the tick's `receivedUtc` (the streamer's clock,
  the Redis tick carrying no exchange stamp), so 12:38:36 is wall-clock time
  and its lag gauge (`REDIS_LAG`) measured only the Redis hop — it never
  warned. The whole tick pipeline, not only the bar writer, was behind the
  exchange.
- **Marks versus fills.** The guard trips on `LastMarkPrice` and the close
  fills at the next quote: run 83's overall target read "P&L 2,769" and
  booked ₹2,606.50 (sum of positions 1014, 1016, 1023, 1029).
- **`[STATUS] open_groups=0` is always printed** for this strategy:
  `count_open_groups` does not know its state layout.
- **Expiry-day contracts.** The nearest expiry on or after today is used, so
  on 2026-09-08 run 83 traded `NIFTY2690823700PE` / `23650CE` expiring that
  afternoon.
- **One-bar legs** ($a \le 1$, the provisional extreme is the bar right
  after the pivot) put $T$ on the later pivot itself with slope $\pm|b|$ per
  bar; the first bar scanned is that pivot bar, whose close is inside its own
  range, so the leg fires at once (once, by the dedup). A two-bar leg gives a
  flat line at 87 % of the height.
- **Backtest differs from live.** Closed bars only (no intra-bar fires), no
  pre-open bars, index candles from backfill, option premiums from FYERS
  history so expired contracts are skipped — run 75 (66 sessions,
  2026-06-04 → 2026-09-04) skipped 333 entries (contracts missing from the
  master, no premium history, or after the EOD square-off) and traded 7
  times, all on 2026-09-02 … 04 in the 2026-09-08 expiry; its ₹2,869.75
  reflects three sessions, not 66 — and a capped three: run 75 also carried
  `overall.target = 2000` (day scope), and the daily target closed two of
  those three sessions early (`dayRiskStops: 2`). EOD square-off at 15:15
  instead of 15:30;
  no slippage, `charges_per_lot` 0.

## Facts (machine-readable)

```yaml
name: GhostTangentCrossings
category: Directional
evaluates_on: tick
resolution: 5m
data: index candles, ticks
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: false
added: 2026-09-09
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
FROM simulation_runs WHERE "Id" = 96;

/* its signals: 1409, 1416, 1427, 1437, 1440, 1441, 1463, 1472, 1473 */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 96 ORDER BY "Id";

/* fills: 2042, 2049, 2071, 2087, 2098, 2099, 2185, 2209 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 96 ORDER BY "Id";

/* positions: 1031, 1050, 1063, 1107 */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 96 ORDER BY "Id";

/* the 5m buckets the strategy saw (live_bars 1m rows, bucketed on the UTC clock as
   LiveDataService.GetRecentBarsAsync does); 2026-09-09 03:00-10:00 UTC = 08:30-15:30 IST.
   Bar 497 = 09:00, 513 = 10:20, 515 = 10:30, 522 = 11:05, 534 = 12:05, 535 = 12:10,
   538 = 12:25, 542 = 12:45 IST (bar 500 = the 09:15 bucket; see Timeframe). */
SELECT to_char(b AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS bar_ist, o, h, l, c, n
FROM (
  SELECT date_trunc('hour', "BarStartUtc") + (floor(extract(minute FROM "BarStartUtc") / 5) * 5) * interval '1 min' AS b,
         (array_agg("Open"  ORDER BY "BarStartUtc"))[1]      AS o,
         max("High")                                          AS h,
         min("Low")                                           AS l,
         (array_agg("Close" ORDER BY "BarStartUtc" DESC))[1] AS c,
         count(*)                                             AS n
  FROM live_bars
  WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '1m'
    AND "BarStartUtc" BETWEEN '2026-09-09 03:00' AND '2026-09-09 10:00'
  GROUP BY 1
) t ORDER BY b;

/* tick arrival lag on 2026-09-09 (UpdatedUtc vs BarStartUtc), 12:20-12:50 IST;
   the 12:24 row was last written at 12:38:34 and the runner's list at 12:38:36
   already held the 12:25 bucket (both [STATUS] triggers reproduce only at n = 538) */
SELECT to_char("BarStartUtc" AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS bar,
       to_char("UpdatedUtc"  AT TIME ZONE 'Asia/Kolkata', 'HH24:MI:SS') AS written
FROM live_bars
WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '1m'
  AND "BarStartUtc" BETWEEN '2026-09-09 06:50' AND '2026-09-09 07:20'
ORDER BY "BarStartUtc";

/* run 83 (overall target): reason text vs booked P&L */
SELECT "Id", "SignalType", "MetadataJson" FROM simulation_signals WHERE "Id" IN (1373, 1377);
SELECT sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 83;

/* run 75 (OfflineReplay) summary with dataNotes */
SELECT "MetadataJson" FROM simulation_signals WHERE "SimulationRunId" = 75 AND "SignalType" = 'BACKTEST_SUMMARY';

Runner log lines cited (local, gitignored): logs/api-until-20260909-133430.log
(run 96 start-up at lines 669-825; signals at 873-985, 8986-9100, 16455-16470;
[STATUS] triggers at 827, 991, 8962, 9112, 16445) and logs/engine/runner-96-57819.log
(signal 1463 and the STATUS lines around it).
-->
