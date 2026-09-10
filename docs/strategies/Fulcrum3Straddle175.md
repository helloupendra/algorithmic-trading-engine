# Fulcrum3Straddle175

Source: `strategies/fulcrum/fulcrum_3_straddle_175.py`
(`Fulcrum3Straddle175Strategy`), registered as `Fulcrum3Straddle175` in
`strategies/variants.py` with no factory defaults (the same class is
registered as `Fulcrum3StraddleBuy175` with `direction="BUY"`; that is a
separate spec). Shared helpers: `strategies/strike_math.py` (`round_to_step`,
`neighbour_strike`, `steps_to_points`, `hedge_strike`),
`strategies/fulcrum/_direction.py`, `strategies/fulcrum/_exit_rules.py`
(`closing_legs`; the time stop is buy-only) and
`strategies/contract_selector.py`. Every rule below is read from the code;
where the code and its docstring or catalogue text disagree — and here they
do, on the re-centring distance — the code is documented and the difference
listed under Limitations.

## Idea

Sell three straddles side by side — at the strike nearest the spot and at
the strike on either side of it — and buy a far out-of-the-money call and put
about 3.5 % away as wings. Six short options collect decay while the spot
stays inside the three-strike band; the band is re-centred on the new nearest
strike when the spot leaves it. In the code the band is lopsided: the
structure re-centres as soon as the nearest strike moves *up* (half a step
above the centre) but holds until the spot is a full step *below* the centre.
Nothing in this repository tests that the decay pays for the rebuilds beyond
the run cited below — run 110 made ₹2,138.50 in one session over five filled
groups, one of which lost ₹1,803.75 on a 59-point rise.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX` in run 110; any of `supported_underlyings` — NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX) | every tick | none — `get_data_requirements` is the base class's empty list: no warm-up, no bars (`warmupBars: 0` in run 110's summary) | ingestor → Redis stream `market:ticks`; the live runner calls `on_bar` on every tick of the spot symbol |
| index candles | the same spot symbol | the run's resolution (5m in run 110) | none | `candles` — the replay driver; the bar close is the spot. Run 110's 2026-09-09 candles are the ingestor's own bars (`SourceKey = live`), not a FYERS backfill (Limitations) |
| option quotes | the eight contracts the strategy names each evaluation: CE and PE at the nearest strike and its two neighbours, one far-OTM PE and one far-OTM CE (logical symbols `NIFTY_CE_23550`, `NIFTY_PE_22700`, …, resolved on demand at the nearest expiry) | latest quote (live) / option candle at the run's resolution (replay) | none | live: `execution_runner.resolve_leg_symbol` → `get_exact_contract`, every leg put on the ingestor watchlist, filled at the latest quote; replay: `ContractResolver.resolve_logical` and option candles from `candles` (FYERS history synced on first use), filled at the candle close of the signal bar |

`get_contract_requirements` is not overridden, so the platform also resolves
`atm_ce` / `atm_pe` on every evaluation; this class never reads
`inp.contracts`. It never reads option candles as an input and never reads
option-chain OI.

## Timeframe

- **Live: every tick, no bar.** The runner evaluates on every spot tick with
  a positive LTP; there is no bar in the strategy.
- **Replay: once per closed driver bar** at the run's resolution (5m in run
  110), with the bar's close as the spot; fills at the option candle close of
  the same bar.
- **Session window: none.** No clock gate in the strategy or the runner: the
  first evaluation builds the first group (09:15 in run 110, skipped for want
  of premium history — see the worked example). The backtest skips entries
  after `eod_square_off_ist` (15:15 in run 110) and squares off at that bar.
- **A spot exactly on a strike is ignored:** `if atm == price: return []`,
  state untouched (`on_bar`, line 86).
- **15:30:** `MarketHoursService` stops every live run with flatten at or
  after 15:30 IST ("Market closed (15:30 IST)"). The strategy relies on it —
  it has no time exit of its own on the SELL side — and is not told about it
  (Position management).

Times in this document are IST; the database stores UTC (IST = UTC + 5:30).

## Entry

### Notation

$S_t$ is the spot (tick LTP live, bar close in replay); $\Delta$ the strike
step from the option chain (`resolve_step(inp.strike_step, params["strike_step"])`:
50 for NIFTY, 100 for BANKNIFTY); $L$ the run's lots. The nearest strike:

$$
K_\circ = \mathrm{round}\!\left(\frac{S_t}{\Delta}\right)\Delta
$$

(`round_to_step`, Python `round`, half-to-even at an exact midpoint); its
neighbours are $K_\circ \pm \Delta$ (`neighbour_strike`). The state holds the band of the open structure as
$(K_0, K_1, K_2)$ = (`st0`, `st1`, `st2`) = centre, lower neighbour, upper
neighbour — all 0 before the first evaluation. The two band constants are
literals in `on_bar` (lines 83–84), in strike steps:

$$
b_{\mathrm{in}} = 1.05\,\Delta,\qquad b_{\mathrm{out}} = 1.95\,\Delta
$$

(52.5 and 97.5 points on NIFTY; 105 and 195 on BANKNIFTY — the comment calls
them "the original 105 and 195").

### Wings (`strike_math.hedge_strike`)

$$
W^{PE}_t = \left\lfloor \frac{S_t\,(1-h)}{g} \right\rfloor g,\qquad
W^{CE}_t = \left\lceil \frac{S_t\,(1+h)}{g} \right\rceil g,\qquad
g = \begin{cases} 5\Delta & \text{if } 5\Delta \le 0.01\,S_t \\ \Delta & \text{otherwise} \end{cases}
$$

with $h$ = `DEFAULT_HEDGE_PCT` = 0.035, 5 = `DEFAULT_HEDGE_GRID_STEPS`, 0.01 =
`MAX_HEDGE_GRID_FRACTION` — module constants, not run parameters; measured
from the spot and always rounded further out. On BANKNIFTY at 57,500 the
coarse grid holds ($500/57500 = 0.87\,\%$): wings on the 500 grid about
2,000 points out. On NIFTY at 23,526 it does not ($250/23526 = 1.06\,\%$):
wings on the 50 grid about 823 points out, moving with every ≈ 48–52 points
of spot (the crossover is $S = 25{,}000$).

### The hold test and the legs (`on_bar`)

Lines 97–107, written out for a band $(K_0, K_0 - \Delta, K_0 + \Delta)$
(which is what every evaluation after the first leaves behind):

$$
\text{hold}_t \iff K_1 < S_t \ \land\ |K_2 - S_t| \ge b_{\mathrm{in}} \ \land\ S_t - K_2 < b_{\mathrm{out}}
$$

For $S_t < K_1$ the code runs the mirrored test ($K_2 - S_t < b_{\mathrm{out}}$),
which cannot hold because $K_2 - S_t > 2\Delta$. Substituting the band:

$$
\text{hold}_t \iff S_t \in \left(K_0 - \Delta,\ K_0 - 0.05\,\Delta\right] \ \cup\ \left[K_0 + 2.05\,\Delta,\ K_0 + 2.95\,\Delta\right)
$$

On NIFTY: hold while the spot is between 50 and 2.5 points *below* the centre
strike (or, only after a one-evaluation gap of more than 2.1 steps, between
102.5 and 147.5 points above it). Before the first evaluation, with the band
at zero, the test never holds. When the test holds nothing happens and the
state is not touched. Otherwise the band is re-derived from the spot and the
leg set rebuilt (lines 109–142):

$$
(K_0, K_1, K_2) \leftarrow (K_\circ,\ K_\circ - \Delta,\ K_\circ + \Delta),\qquad
\mathcal{L}_t = \Big\{
\text{BUY } W^{PE}_t\,\text{PE},\ \text{BUY } W^{CE}_t\,\text{CE}
\Big\} \cup \bigcup_{K \in \{K_0, K_1, K_2\}} \Big\{ \text{SELL } K\,\text{CE},\ \text{SELL } K\,\text{PE} \Big\},\quad \text{each} \times L
$$

$$
\text{open / rebuild} \iff \mathcal{L}_t \ne \mathcal{L}^{\mathrm{open}}
$$

where $\mathcal{L}^{\mathrm{open}}$ is `current_group_legs` and the comparison
is `_legs_differ` (same length and the same set of (symbol, side, lots)).
The first evaluation has no open legs, so it always opens. The signal is one
`OPEN_GROUP` with the eight legs, group id `FULCRUM-3-<timestamp>-<nnn>`
(`_group_id`: ISO timestamp with punctuation stripped, then a three-digit
counter of groups *built*, including ones the platform refuses), reason
`Fulcrum 3 Straddle Adjusted. Active: [K0, K1, K2]` — centre first, then the
lower and upper neighbours (`[23550, 23500, 23600]`).

Read as a rule on the spot: the structure is re-centred on the nearest strike
whenever the spot is above $K_0 - 0.05\Delta$ — which changes the centre once
the nearest strike moves up, i.e. at $S_t \ge K_0 + \Delta/2$ — or below
$K_0 - \Delta$, the lower straddle strike. In between (the lower half-interval
below the centre and the interval below that) it holds. The nearest strike
can move down one step without a rebuild; upward it cannot move at all
without one, except across the gap zone above.

In words: on the first tick, sell the call and put at the nearest strike and
at the strikes 50 (NIFTY) or 100 (BANKNIFTY) points either side of it, and
buy a call and a put about 3.5 % away. `use_hedges` (`True` for a seller
unless the run sets it) adds the wings; without them the group is the six
short legs.

### Contract, expiry, size, fill

- **Contract:** each logical `UNDERLYING_CE_K` / `UNDERLYING_PE_K` is looked
  up in the instrument master at the current expiry — live by
  `resolve_leg_symbol` (a failed lookup leaves the logical symbol on the leg,
  which nothing can price), replay by `ContractResolver.resolve_logical`
  (a missing contract skips the whole open, `[SKIP]`).
- **Expiry:** live, the first listed expiry on or after today's UTC date;
  replay, the earliest expiry on or after the bar's IST date. Run 110 traded
  the 2026-09-15 weekly (`NIFTY26915…`).
- **Quantity:** `lots` (legacy `quantity`) via `BaseStrategy.lots_from`,
  default 1, never below 1, the same on every leg; `paper_orders.Quantity` is
  lots and the API multiplies by `instruments.LotSize` (NIFTY 65).
- **Fill:** live, all eight legs are resolved and subscribed, the runner
  waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for every quote, and the API
  fills all or none and refuses an unpriced opening group. Replay: each leg
  at its option candle's close at the signal bar; one leg without a candle
  skips the whole entry (`no premium history for …`).

## Position management

### The rebuild

Every later evaluation that fails the hold test re-derives the band and
rebuilds when the eight-leg set changed — because the nearest strike moved
(a new centre) or because a wing strike moved while the spot sat in the
re-evaluated zone. A rebuild is two signals from the same tick, in this
order: `CLOSE_GROUP` of the open group with `closing_legs` (same eight
symbols, sides flipped, same lots), reason `Adjusting straddles`, metadata
`group_id` only; then `OPEN_GROUP` of $\mathcal{L}_t$ with a new group id and
counter. Legs common to both sets — the two straddles the old and new band
share — are closed and re-opened at the same price in the same bar.

$$
\text{rebuild}_t \iff \lnot\,\text{hold}_t \ \land\ \mathcal{L}_t \ne \mathcal{L}^{\mathrm{open}}
$$

Run 110: six rebuilds in 5 h 35 min, every one a re-centre by one strike, no
wing-only rebuild (NIFTY's 50-point wing grid makes those possible — see
`Fulcrum2Straddle20` — but on this day every wing move coincided with a
re-centre or fell in a hold zone).

The strategy holds one group at a time (`current_group_id`), never adds to
or reduces it, never adjusts a single leg, and never re-enters after a
platform close until the next rebuild. `straddle_list` is initialised and
never written by the SELL path.

### What the run's risk rules add on top

`parametersJson.risk` carries three levels that `StrategyRiskGuardService`
sweeps every `RiskGuardIntervalSeconds` (3 s) in the order leg → group →
overall, each level stop-loss → trailing stop → target; the backtest mirrors
them once per bar before and after the strategy is called (`_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; closes that
  leg only, which unbalances the structure (a stopped short call leaves five
  shorts and both wings);
- `group` — rupees on one eight-leg group's realized + unrealized P&L; closes
  every open leg of it;
- `overall` — rupees on the run's total P&L; flattens and stops the run
  (replay: per trading day by default, `scope: "run"` for the whole range).

Run 110 carried none (`risk: {}`). The strategy is **not told** when the
platform closes its group (risk rule, EOD square-off, 15:30, manual stop):
its next rebuild sends a `CLOSE_GROUP` for legs that are no longer open —
reduce-only in the API and the ledger, so ignored — and then an `OPEN_GROUP`,
which fills. In run 110 this is visible where the platform *skipped* the
open instead: the strategy's closes for groups 001 and 003 at 09:35 and
10:20 are the summary's "16 close legs ignored".

### What the strategy never does

It never goes flat on its own: every `CLOSE_GROUP` it emits on the SELL side
is the first half of a rebuild. No stop, no target, no time exit
(`long_time_stop_close` returns `[]` at once unless `direction` is `BUY`);
`StopLossPrice` / `TargetPrice` are null on all 40 positions of run 110.

## Exit

Live, in order of precedence at any moment:

1. **The rebuild** — the strategy's own `CLOSE_GROUP`, always followed by a
   new open (Position management). It races the sweep below for the run's
   lock (`SimulationRunLocks.AcquireAsync`).
2. **Risk guard** (`StrategyRiskGuardService.SweepAsync`, every 3 s): the
   position's own `StopLossPrice` / `TargetPrice` (never set here), then leg
   rules, then group rules, then overall rules — the last flattens and stops
   the run.
3. **Manual square-off or the run's stop button.**
4. **Market close:** `MarketHoursService` at 15:30 IST, weekdays, once per
   calendar day.

Replay (`backtest/engine.py`), per bar: EOD square-off at
`eod_square_off_ist` (15:15 in run 110, never reached — the data ends at
14:50), a risk sweep, the strategy's signals, marks, a second sweep; the last
bar of the range squares off "End of backtest" (run 110, signal 1567).

## Parameters

`default_params = {}` and the factory adds nothing; the band constants
(1.05 and 1.95 steps) and the wing constants (3.5 %, 5-step grid, 1 % cap)
are literals, not parameters. Every key below is read straight from the
run's `parametersJson`.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `lots` (legacy `quantity`) | `default_lots` = 1 | lots on every leg, wings included — eight legs, so one lot is six short and two long contracts | larger group, same rebuilds | never below 1 |
| `strike_step` | unset → the chain's grid | explicit $\Delta$ for `resolve_step`: moves the three strikes, both bands and the wing grid together | a wider band and coarser strikes than the chain has — legs the master may not carry | finer — same |
| `direction` | `SELL` | `BUY` / `LONG` / `B` buy the straddles, drop the wings and add the time stop — the `Fulcrum3StraddleBuy175` registration | n/a — a choice | |
| `use_hedges` | follows `direction` (`True` for `SELL`) | `False` (or `"false"`, `"0"`, `"no"`) omits the two wing legs: six naked shorts | | |
| `max_hold_minutes` | 45.0 (`resolve_long_exit`) | buy-only time stop; unused when `direction` is `SELL` | none here | none here |
| `target_steps` | 2.0 | parsed by `resolve_long_exit`, not read by this class at all | none | none |

There is no `adjustment_steps` here: the "175" in the name is not a parameter
and does not appear in the code (Limitations).

## Worked example

Run **110** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5` (5m), range
2026-09-09 (one session), executed 2026-09-11 00:07:44 IST, `ParametersJson`
verbatim: `{"lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15",
"charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,
"target":null}`. Lot size 65 is `instruments.LotSize` for
`NSE:NIFTY2691523500CE`; strike step 50; hold zone $(K_0 - 50,\ K_0 - 2.5]$;
wings on the 50 grid (see Entry). The driver was 63 bars, 09:15–14:50 IST,
from the ingestor's own 5m bars (`candles.SourceKey = live`; 13:20–13:40
missing, nothing after 14:50), so the run ended at 14:50 with an "End of
backtest" square-off, not at the 15:15 EOD. Bar closes below are `candles`
rows; the band and wing arithmetic are rebuilt from the code (the strategy
persists neither), and the sequence was checked by feeding the same 63 closes
through `Fulcrum3Straddle175Strategy.on_bar` offline — it reproduces every
group id, timestamp and leg set.

### 09:15 — the first group, built and skipped

Close 23500.90: $K_\circ = 23500$, band $(23500, 23450, 23550)$, wings
$\lfloor 22678.37/50 \rfloor \cdot 50 = 22650$ and
$\lceil 24323.43/50 \rceil \cdot 50 = 24350$. Group
`FULCRUM-3-20260909034500-001` was skipped: "no premium history for
NSE:NIFTY2691523450CE, NSE:NIFTY2691523450PE, NSE:NIFTY2691523550CE,
NSE:NIFTY2691523550PE" (`skippedEntries`) — the stored 23550 series starts at
09:35 and 23450 at 10:00. The strategy believed it open. 09:20–09:30 (closes
23512.20, 23509.40, 23515.90, all above $K_0 - 2.5 = 23497.5$) were
re-evaluated: nearest strike still 23500, same wings, no signal.

### Signal 1558 — 09:35, the first filled group → orders 2578–2585 → positions 1300–1307

`MetadataJson`: `spot_price 23526.25`, `atm_strike 23550`, reason
`Fulcrum 3 Straddle Adjusted. Active: [23550, 23500, 23600]`.
$23526.25 > 23497.5$, so re-evaluated; $K_\circ = 23550 \ne 23500$: the band
became $(23550, 23500, 23600)$, wings $0.035 \times 23526.25 = 823.42$,
$\lfloor 22702.83/50 \rfloor \cdot 50 = 22700$,
$\lceil 24349.67/50 \rceil \cdot 50 = 24350$. The strategy closed group 001
(eight legs ignored: never opened) and opened `FULCRUM-3-20260909040500-002`
at the 09:35 candle closes: BUY 1 lot `NSE:NIFTY2691522700PE` @ 5.65, BUY
`…24350CE` @ 3.90, SELL `…23550CE` @ 143.25, SELL `…23550PE` @ 116.80, SELL
`…23500CE` @ 173.90, SELL `…23500PE` @ 95.85, SELL `…23600CE` @ 118.15, SELL
`…23600PE` @ 139.80. Premium sold 787.75 points, wings cost 9.55. At 09:40
the close was 23526.35, inside the hold zone $(23500, 23547.5]$: nothing.

### Signal 1559 — 09:45, close on the downside rule → orders 2586–2593

Close 23498.55 $< K_1 = 23500$: not held; $K_\circ = 23500$, band back to
$(23500, 23450, 23550)$. `CLOSE_GROUP` 002: SELL 22700PE @ 5.65, SELL 24350CE
@ 3.90, BUY 23550CE @ 131.35, BUY 23550PE @ 127.85, BUY 23500CE @ 159.00, BUY
23500PE @ 105.60, BUY 23600CE @ 107.00, BUY 23600PE @ 153.05.

$$
(143.25 - 131.35) \times 1 \times 65 = 11.90 \times 65 = 773.50 \quad (\text{position 1302, SHORT 23550CE})
$$

1303 (SHORT 23550PE): $(116.80 - 127.85) \times 65 = -718.25$; 1304 (SHORT
23500CE): $14.90 \times 65 = 968.50$; 1305 (SHORT 23500PE):
$-9.75 \times 65 = -633.75$; 1306 (SHORT 23600CE): $11.15 \times 65 = 724.75$;
1307 (SHORT 23600PE): $-13.25 \times 65 = -861.25$; wings 0.00. Group 002:
**+253.50** for ten minutes in which the spot fell 27.7 points. The matching
`OPEN_GROUP` 003 (`[23500, 23450, 23550]`) was skipped: no 23450 candle before
10:00. The run was flat until 10:20 while the strategy held 003 in state;
09:50–10:10 (closes 23497.40 … 23484.85) sat in the hold zone
$(23450, 23497.5]$, and 10:15 (23516.80) was re-evaluated to the same legs.

### Signals 1560 / 1561 — 10:20 to 11:55, the longest hold → orders 2594–2609 → positions 1308–1315

Close 23537.80 at 10:20: re-evaluated, $K_\circ = 23550$, band
$(23550, 23500, 23600)$, wings $\lfloor 22713.98/50 \rfloor \cdot 50 = 22700$,
$\lceil 24361.62/50 \rceil \cdot 50 = 24400$. Signal 1560 (after the ignored
close of 003) opened `FULCRUM-3-20260909045000-004`: BUY 22700PE @ 5.65, BUY
24400CE @ 3.45, SELL 23550CE @ 150.55, SELL 23550PE @ 106.40, SELL 23500CE @
181.75, SELL 23500PE @ 87.45, SELL 23600CE @ 124.25, SELL 23600PE @ 128.80.
The next 18 closes (10:25–11:50, between 23502.30 and 23539.65) all lay in
$(23500, 23547.5]$ — the hold zone — so nothing was evaluated for 90 minutes.
At 11:55 the close was 23488.50 $< 23500$: signal 1561 closed the group at
5.85, 3.75, 119.85, 128.00, 146.30, 105.45, 96.10, 154.75:

$$
0.20 \times 65 + 0.30 \times 65 + 30.70 \times 65 - 21.60 \times 65 + 35.45 \times 65 - 18.00 \times 65 + 28.15 \times 65 - 25.95 \times 65 = 1901.25
$$

(positions 1308–1315: +13.00, +19.50, +1,995.50, −1,404.00, +2,304.25,
−1,170.00, +1,829.75, −1,686.75) — the spot drifted 49 points down through
the hold zone to its lower edge, and the three calls gave up more than the
three puts gained. Signal 1562 re-opened at `[23500, 23450, 23550]` (group 005, orders
2610–2617); that one lost **−1,803.75** by 12:30 (signal 1563) as the spot
rose 59.35 points to 23547.85 and the band re-centred up.

### Signals 1566 / 1567 — 14:35 and 14:50, the last group and the square-off

Close 23492.85 $< 23500$ at 14:35: group 006 (`[23550, 23500, 23600]`,
12:30–14:35, +1,690.00) was closed by signal 1565 and signal 1566 opened
`FULCRUM-3-20260909090500-007` at `[23500, 23450, 23550]`, wings 22650 /
24350: orders 2642–2649, BUY 22650PE @ 5.10, BUY 24350CE @ 4.05, SELL
23500CE @ 150.80, SELL 23500PE @ 95.60, SELL 23450CE @ 182.75, SELL 23450PE @
77.15, SELL 23550CE @ 123.20, SELL 23550PE @ 117.15. Signal 1567, reason
`End of backtest`, `square_off: true`, at the last driver bar 14:50 (close
23489.55): orders 2650–2657 at 5.25, 3.90, 151.45, 94.75, 183.70, 76.15,
122.95, 116.15.

$$
0.15 \times 65 - 0.15 \times 65 - 0.65 \times 65 + 0.85 \times 65 - 0.95 \times 65 + 1.00 \times 65 + 0.25 \times 65 + 1.00 \times 65 = 97.50
$$

(positions 1332–1339: +9.75, −9.75, −42.25, +55.25, −61.75, +65.00, +16.25,
+65.00).

### The run

Seven groups built, five filled (001 and 003 skipped), 40 positions, 80
orders, every fill 1 lot. Sum of `RealizedPnl` = **+₹2,138.50**
(`realizedPnl: 2138.5` in signal 1568's summary), no charges: the six short
legs made +2,128.75, the wings +9.75. By group: 002 +253.50, 004 +1,901.25,
005 −1,803.75, 006 +1,690.00, 007 +97.50. The two large winners were the
two long holds (004, 95 minutes; 006, 125 minutes), both centred at 23550
with the spot mostly in the hold zone below it; the loser was the group
re-centred at 23500 at 11:55 and run through from beneath as the spot rose
to 23547.85.

## Limitations

- **The re-centring distance is not what the docstring says.** The class
  docstring ("re-centred on a 1.75-step drift"), the catalogue description
  ("drifted roughly 1.75 strikes from the outer straddle … on NIFTY's 50-point
  grid it is 87.5") and the name all promise 1.75 steps. The code compares
  the spot with the *upper* neighbour against literals of 1.05 and 1.95
  steps, and the result (Entry) is a re-centre half a step above the centre
  and one full step below it. There is no 1.75 anywhere in the file.
- **Lopsided band.** Up-moves are followed immediately (a 25-point rise from
  the centre on NIFTY re-centres), down-moves only after a full strike; the
  structure therefore spends most of its life with the spot in the lower half
  of the band, short the centre and upper straddles in the money on the call
  side. Whether that is an edge or a bug is not tested here.
- **Naked between the wings.** The wings sit ≈ 3.5 % away (823 points in run
  110, bought for 3.45–5.65); inside that band six short options carry the
  move — group 005 lost ₹1,803.75 on 59 points in 35 minutes.
- **Wing churn is possible on NIFTY.** Below a spot of 25,000 the wing grid
  is the 50-point strike grid, and a wing move in the re-evaluated zone
  rebuilds all eight legs (run 110 had none; run 108 of `Fulcrum2Straddle20`
  had 17). On BANKNIFTY the grid is 500.
- **No exit of its own.** No stop, target or time exit on the SELL side; a
  `group` or `overall` rule is the only protection, and a `leg` rule that
  trips on one short leaves an unbalanced group.
- **Not told about platform closes.** After a close it did not make, the
  next rebuild's `CLOSE_GROUP` is ignored and the `OPEN_GROUP` fills; live,
  after a `group` stop the run re-enters at the next rebuild with no memory
  of the stop.
- **One unpriced leg refuses the whole open** (live: any of eight contracts
  with no quote within 10 s, or a wing strike the master lacks; replay: a leg
  with no candle), and the strategy then believes the group is open until the
  next rebuild — run 110 was flat 09:15–09:35 and 09:45–10:20 for that
  reason.
- **A spot exactly on a strike skips the bar** (`atm == price`); half-to-even
  rounding at an exact midpoint. The upper hold zone
  $[K_0 + 2.05\Delta, K_0 + 2.95\Delta)$ is reachable only by a gap of more
  than 2.1 steps between two evaluations.
- **Run 110's premiums are two different feeds.** The 23450 / 23500 / 23550
  candles are the ingestor's bars from the live runs of 2026-09-09
  (`SourceKey = live`, starting when a live run first subscribed: 23500 from
  09:15, 23550 from 09:35, 23450 from 10:00, all ending 14:50); 23600 and the
  wings were synced from FYERS (77 bars to 15:35). The feed syncs from FYERS
  only for a contract with nothing stored, so the gaps were not filled. The
  index driver is `live` too: no bars 13:20–13:40, none after 14:50.
- **Backtest differs from live.** One evaluation per bar close instead of per
  tick; fills at candle close, no slippage, `charges_per_lot` 0; expired
  contracts have no FYERS history and are skipped; EOD at 15:15 instead of
  15:30; no pre-open ticks. Live, the runner resolves the eight logical
  symbols per signal and waits up to 10 s for quotes, time in which the
  strategy sees no ticks.
- **OI is never read.**

## Facts (machine-readable)

```yaml
name: Fulcrum3Straddle175
category: Adjustment
evaluates_on: tick
resolution: tick (replay: run bar)
data: ticks, index candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: false
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status", "FromUtc", "ToUtc",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" = 110;

/* signals cited: 1558-1568 (1568 = BACKTEST_SUMMARY with skippedEntries, dataNotes) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 110 ORDER BY "Id";

/* fills cited: 2578-2657 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 110 ORDER BY "Id";

/* positions cited: 1300-1339; totals 40 / 2138.50; wings vs shorts; per group */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 110 ORDER BY "Id";
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 110;
SELECT "Direction", sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 110 GROUP BY 1;
SELECT "GroupId", min("OpenedUtc") AT TIME ZONE 'Asia/Kolkata', max("ClosedUtc") AT TIME ZONE 'Asia/Kolkata', sum("RealizedPnl")
FROM paper_positions WHERE "SimulationRunId" = 110 GROUP BY 1 ORDER BY 2;

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:NIFTY2691523500CE';

/* the driver bars (spot = 5m close) and their source */
SELECT "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Close", "SourceKey" FROM candles
WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5' AND "TimeStampUtc"::date = '2026-09-09' ORDER BY 1;

/* option candle coverage that explains the two skipped opens */
SELECT "Symbol", "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Resolution" = '5' AND "TimeStampUtc"::date = '2026-09-09'
  AND "Symbol" IN ('NSE:NIFTY2691523450CE', 'NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523550CE', 'NSE:NIFTY2691523600CE',
                   'NSE:NIFTY2691522700PE', 'NSE:NIFTY2691524350CE', 'NSE:NIFTY2691524400CE')
GROUP BY 1, 2 ORDER BY 1;

Offline check of the leg sequence: instantiate get_parameterised_strategies()["Fulcrum3Straddle175"]({"lots": 1})
and call on_bar once per driver close with atm_strike = round_to_step(close, 50), strike_step = 50.0;
the OPEN_GROUP reasons and group ids match signals 1558-1566 and the skipped entries.
-->
