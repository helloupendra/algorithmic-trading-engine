# Fulcrum2Straddle20

Source: `strategies/fulcrum/fulcrum_2_straddle_20.py`
(`Fulcrum2Straddle20Strategy`), registered as `Fulcrum2Straddle20` in
`strategies/variants.py` with the factory default `adjustment_steps=0.2` (the
same class is registered as `Fulcrum2StraddleBuy20` with `direction="BUY"`;
that is a separate spec). Shared helpers: `strategies/strike_math.py`
(`round_*_to_step`, `steps_to_points`, `hedge_strike`, `steps_from_params`),
`strategies/fulcrum/_direction.py`, `strategies/fulcrum/_exit_rules.py`
(`closing_legs`; the time stop is buy-only) and
`strategies/contract_selector.py`. Every rule below is read from the code;
where the code and its docstring or catalogue text disagree, the code is
documented and the difference listed under Limitations.

## Idea

Sell two straddles at once, on the two strikes that bracket the spot, and buy
a far out-of-the-money call and put about 3.5 % away as wings. Between the two
straddle strikes one call and one put are always in the money and the other
pair out, so the position collects decay on four short options while the spot
oscillates inside a single strike interval; when the spot has moved at least
`adjustment_steps` of a strike (0.2 — 10 points on NIFTY, 20 on BANKNIFTY,
the "20" in the name) away from the anchor strike, the strikes are re-derived
and, if anything in the six-leg set changed, the whole group is closed and
rebuilt. Nothing in this repository tests that the decay pays for the
rebuilds beyond the run cited below — run 108 made ₹1,196.00 in one session,
23 groups, with 17 of its 24 rebuilds caused by a wing strike moving.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTY50-INDEX` in run 108; any of `supported_underlyings` — NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX) | every tick | none — `get_data_requirements` is the base class's empty list: no warm-up, no bars (`warmupBars: 0` in run 108's summary) | ingestor → Redis stream `market:ticks`; the live runner calls `on_bar` on every tick of the spot symbol |
| index candles | the same spot symbol | the run's resolution (5m in run 108) | none | `candles` — the replay driver; the bar close is the spot. Run 108's 2026-09-09 candles are the ingestor's own bars (`SourceKey = live`), not a FYERS backfill (Limitations) |
| option quotes | the six contracts the strategy names each evaluation: CE and PE at the two strikes bracketing the spot, one far-OTM PE and one far-OTM CE (logical symbols `NIFTY_CE_23500`, `NIFTY_PE_22700`, …, resolved on demand at the nearest expiry) | latest quote (live) / option candle at the run's resolution (replay) | none | live: `execution_runner.resolve_leg_symbol` → `get_exact_contract`, every leg put on the ingestor watchlist, filled at the latest quote; replay: `ContractResolver.resolve_logical` and option candles from `candles` (FYERS history synced on first use), filled at the candle close of the signal bar |

`get_contract_requirements` is not overridden, so the platform also resolves
`atm_ce` / `atm_pe` on every evaluation; this class never reads
`inp.contracts`. It never reads option candles as an input and never reads
option-chain OI.

## Timeframe

- **Live: every tick, no bar.** The runner evaluates on every spot tick with
  a positive LTP; there is no bar in the strategy.
- **Replay: once per closed driver bar** at the run's resolution (5m in run
  108), with the bar's close as the spot; fills at the option candle close of
  the same bar.
- **Session window: none.** No clock gate in the strategy or the runner: the
  first evaluation builds the first group (09:15 in run 108, skipped for want
  of premium history — see the worked example). The backtest skips entries
  after `eod_square_off_ist` (15:15 in run 108) and squares off at that bar.
- **A spot exactly on a strike is ignored:** `if atm == price: return []`
  before the gate, state untouched (`on_bar`, line 93).
- **15:30:** `MarketHoursService` stops every live run with flatten at or
  after 15:30 IST ("Market closed (15:30 IST)"). The strategy relies on it —
  it has no time exit of its own on the SELL side — and is not told about it
  (Position management).

Times in this document are IST; the database stores UTC (IST = UTC + 5:30).

## Entry

### Notation

$S_t$ is the spot (tick LTP live, bar close in replay); $\Delta$ the strike
step from the option chain (`resolve_step(inp.strike_step, params["strike_step"])`:
50 for NIFTY, 100 for BANKNIFTY); $a$ = `adjustment_steps` and
$\theta = a\Delta$ the threshold in points (`steps_to_points`: 10 on NIFTY, 20
on BANKNIFTY at the default 0.2); $L$ the run's lots. The strikes around the
spot:

$$
K_- = \left\lfloor \frac{S_t}{\Delta} \right\rfloor \Delta,\qquad
K_+ = \left\lceil \frac{S_t}{\Delta} \right\rceil \Delta,\qquad
K_\circ = \mathrm{round}\!\left(\frac{S_t}{\Delta}\right)\Delta
$$

(`round_down_to_step`, `round_up_to_step`, `round_to_step`; Python `round`,
half-to-even at an exact midpoint). $A$ is the **anchor** — state key `st1`,
0 before the first evaluation.

### Wings (`strike_math.hedge_strike`)

$$
W^{PE}_t = \left\lfloor \frac{S_t\,(1-h)}{g} \right\rfloor g,\qquad
W^{CE}_t = \left\lceil \frac{S_t\,(1+h)}{g} \right\rceil g,\qquad
g = \begin{cases} 5\Delta & \text{if } 5\Delta \le 0.01\,S_t \\ \Delta & \text{otherwise} \end{cases}
$$

with $h$ = `DEFAULT_HEDGE_PCT` = 0.035, 5 = `DEFAULT_HEDGE_GRID_STEPS`, 0.01 =
`MAX_HEDGE_GRID_FRACTION` — module constants, not run parameters. The wing is
measured from the spot, not from a straddle strike (the comment above the
call says why), and always rounded further out. On BANKNIFTY at 57,500 the
coarse grid holds ($500 / 57500 = 0.87\,\%$): wings on the 500 grid about
2,000 points out. On NIFTY at 23,526 it does not ($250 / 23526 = 1.06\,\%$):
wings on the 50 grid about 823 points out, so a NIFTY wing strike moves with
every ≈ 48 points of spot (the crossover is $S = 25{,}000$).

### The gate and the legs (`on_bar`)

$$
\text{evaluate}_t \iff S_t \ne K_\circ \ \land\ \left( A = 0 \ \lor\ |S_t - A| \ge \theta \right)
$$

When the gate holds (lines 106–109) nothing happens and the state is not
touched. When it passes, the anchor and the leg set are recomputed:

$$
A \leftarrow K_\circ \quad (\text{first evaluation: } A \leftarrow K_-),\qquad
\mathcal{L}_t = \Big\{
\text{BUY } W^{PE}_t\,\text{PE},\ \text{BUY } W^{CE}_t\,\text{CE},\
\text{SELL } K_-\,\text{CE},\ \text{SELL } K_-\,\text{PE},\
\text{SELL } K_+\,\text{CE},\ \text{SELL } K_+\,\text{PE}
\Big\} \times L
$$

$$
\text{open / rebuild} \iff \mathcal{L}_t \ne \mathcal{L}^{\mathrm{open}}
$$

where $\mathcal{L}^{\mathrm{open}}$ is `current_group_legs` and the comparison
is `_legs_differ`: same length and the same set of (symbol, side, lots),
order ignored. The first evaluation has no open legs, so it always opens.
The signal is one `OPEN_GROUP` with the six legs above, group id
`FULCRUM2-<timestamp>-<nnn>` (`_group_id`: ISO timestamp with punctuation
stripped, then a three-digit counter of groups *built*, including ones the
platform refuses), reason `Fulcrum 2 Straddle Adjusted. Active: [K-, K+]`.

The code keeps three strike slots (`st0`, `st1`, `st2`) so that a third
straddle could survive a rebuild (lines 111–142), but the keep rule cannot
be met: the extra strike (the old anchor, or the strike one step beyond it)
must lie beyond the new pair on the side the spot came from *and* within
$\theta$ of the spot, while the gate has just required the spot to be at
least $\theta$ from the old anchor, which lies between that strike and the
spot or is that strike. For every $\theta$ the slot is zeroed. The group
is therefore always exactly two straddles plus two wings, six legs, and
`active_straddles` is always `[K-, K+]` (every `OPEN_GROUP` reason in run
108 lists two strikes).

In words: on the first tick, sell the call and put at the strike just below
the spot and at the strike just above it, and buy a call and a put about
3.5 % away. `use_hedges` (`True` for a seller unless the run sets it) adds
the wings; without them the group is the four short legs.

### Contract, expiry, size, fill

- **Contract:** each logical `UNDERLYING_CE_K` / `UNDERLYING_PE_K` is looked
  up in the instrument master at the current expiry — live by
  `resolve_leg_symbol` (a failed lookup leaves the logical symbol on the leg,
  which nothing can price), replay by `ContractResolver.resolve_logical`
  (a missing contract skips the whole open, `[SKIP]`).
- **Expiry:** live, the first listed expiry on or after today's UTC date;
  replay, the earliest expiry on or after the bar's IST date. Run 108 traded
  the 2026-09-15 weekly (`NIFTY26915…`).
- **Quantity:** `lots` (legacy `quantity`) via `BaseStrategy.lots_from`,
  default 1, never below 1, the same on every leg; `paper_orders.Quantity` is
  lots and the API multiplies by `instruments.LotSize` (NIFTY 65).
- **Fill:** live, all six legs are resolved and subscribed, the runner waits
  up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for every quote, and the API fills
  all or none and refuses an unpriced opening group. Replay: each leg at its
  option candle's close at the signal bar; one leg without a candle skips
  the whole entry (`no premium history for …`).

## Position management

### The rebuild

Every later evaluation that passes the gate rebuilds when the six-leg set
changed — for any of three reasons: the spot crossed a strike (a new
$K_-, K_+$ pair), a wing strike moved (the spot moved about $g/(1 \pm h)$
points — ≈ 48 up or ≈ 52 down on NIFTY, ≈ 480 / 520 on BANKNIFTY), or
`lots` changed on a restart. A rebuild is two signals from the same tick, in
this order: `CLOSE_GROUP` of the open group with `closing_legs` (same six
symbols, sides flipped, same lots), reason `Adjusting straddles`, metadata
`group_id` only; then `OPEN_GROUP` of $\mathcal{L}_t$ with a new group id and
counter. Legs common to both sets are closed and re-opened at the same price
in the same bar (run 108, 10:10: five of the six).

$$
\text{rebuild}_t \iff |S_t - A| \ge \theta \ \land\ \mathcal{L}_t \ne \mathcal{L}^{\mathrm{open}}
$$

The threshold gates the *evaluation*, not the rebuild: with $\theta$ = 10
points on NIFTY the gate is passed on most 5-minute bars, and the wings then
decide. Run 108: 24 rebuilds in 5 h 35 min, 17 of them because only a wing
strike moved, 6 because only the straddle pair moved, 1 both; 11 groups
lived one 5m bar.

The strategy holds one group at a time (`current_group_id`), never adds to
or reduces it, never adjusts a single leg, and never re-enters after a
platform close until the next rebuild. `ce_list`, `pe_list`,
`straddle_list` and `last_trade_strike` are written but never read.

### What the run's risk rules add on top

`parametersJson.risk` carries three levels that `StrategyRiskGuardService`
sweeps every `RiskGuardIntervalSeconds` (3 s) in the order leg → group →
overall, each level stop-loss → trailing stop → target; the backtest mirrors
them once per bar before and after the strategy is called (`_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; closes that
  leg only, which unbalances the structure (a stopped short call leaves the
  other three shorts and both wings in place);
- `group` — rupees on one six-leg group's realized + unrealized P&L; closes
  every open leg of it;
- `overall` — rupees on the run's total P&L; flattens and stops the run
  (replay: per trading day by default, `scope: "run"` for the whole range).

Run 108 carried none (`risk: {}`). The strategy is **not told** when the
platform closes its group (risk rule, EOD square-off, 15:30, manual stop):
its next rebuild sends a `CLOSE_GROUP` for legs that are no longer open —
reduce-only in the API and the ledger, so ignored — and then an `OPEN_GROUP`,
which fills. In run 108 this is visible where the platform *skipped* the open
instead: the strategy's closes for groups 001 and 003 at 09:35 and 10:05 are
the summary's "12 close legs ignored".

### What the strategy never does

It never goes flat on its own: every `CLOSE_GROUP` it emits on the SELL side
is the first half of a rebuild. No stop, no target, no time exit
(`long_time_stop_close` returns `[]` at once unless `direction` is `BUY`);
`StopLossPrice` / `TargetPrice` are null on all 138 positions of run 108.

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
`eod_square_off_ist` (15:15 in run 108, never reached — the data ends at
14:50), a risk sweep, the strategy's signals, marks, a second sweep; the last
bar of the range squares off "End of backtest" (run 108, signal 1537).

## Parameters

`default_params = {"adjustment_steps": 0.2}`; the factory passes the same
value, so a run's `parametersJson` always carries it unless overridden. The
other keys are read straight from the run parameters.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.2 | $a$: the spot must be at least $a\Delta$ from the anchor strike before the legs are re-derived — 10 points on NIFTY, 20 on BANKNIFTY (`steps_from_params`, `steps_to_points`) | fewer evaluations; the pair and wings lag the spot by up to $a\Delta$ past a strike; at $a \ge 1$ the spot can cross a strike without a rebuild | more evaluations; 0 or negative falls back to 0.2 (`steps_from_params` accepts only `> 0`) |
| `adjustment_threshold` | unset | legacy points spelling, divided by `LEGACY_GRID` = 100 into steps; read only when `adjustment_steps` is absent or not `> 0` | as above | as above |
| `lots` (legacy `quantity`) | `default_lots` = 1 | lots on every leg, wings included | larger group, same rebuilds | never below 1 |
| `strike_step` | unset → the chain's grid | explicit $\Delta$ for `resolve_step`: moves the strikes, the threshold and the wing grid together | coarser strikes than the chain has — legs the master may not carry | finer — same |
| `direction` | `SELL` | `BUY` / `LONG` / `B` buy the straddles, drop the wings and add the time stop — the `Fulcrum2StraddleBuy20` registration | n/a — a choice | |
| `use_hedges` | follows `direction` (`True` for `SELL`) | `False` (or `"false"`, `"0"`, `"no"`) omits the two wing legs: a four-leg naked group | | |
| `max_hold_minutes` | 45.0 (`resolve_long_exit`) | buy-only time stop; unused when `direction` is `SELL` | none here | none here |
| `target_steps` | 2.0 | parsed by `resolve_long_exit`, not read by this class at all | none | none |

The wing distance (3.5 %), grid (5 steps) and grid cap (1 % of spot) are
constants in `strike_math.py`, not parameters.

## Worked example

Run **108** — OfflineReplay, `NSE:NIFTY50-INDEX`, resolution `5` (5m), range
2026-09-09 (one session), executed 2026-09-11 00:07:33 IST, `ParametersJson`
verbatim: `{"adjustment_steps":0.2,"lots":1,"underlying":"NIFTY","resolution":"5m",
"eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master",
"risk":{},"stop_loss":null,"target":null}`. Lot size 65 is `instruments.LotSize`
for `NSE:NIFTY2691523500CE`; strike step 50; $\theta$ = 10 points; wings on
the 50 grid (see Entry). The driver was 63 bars, 09:15–14:50 IST, from the
ingestor's own 5m bars (`candles.SourceKey = live`; 13:20–13:40 missing,
nothing after 14:50), so the run ended at 14:50 with an "End of backtest"
square-off, not at the 15:15 EOD. Bar closes below are `candles` rows; the
anchors and wing arithmetic are rebuilt from the code (the strategy persists
neither), and the sequence was checked by feeding the same 63 closes through
`Fulcrum2Straddle20Strategy.on_bar` offline — it reproduces every group id,
timestamp and leg set in the database.

### 09:15 — the first group, built and skipped

Close 23500.90: $K_\circ = K_- = 23500$, $K_+ = 23550$, $A \leftarrow 23500$;
wings $\lfloor 22678.37/50 \rfloor \cdot 50 = 22650$ and
$\lceil 24323.43/50 \rceil \cdot 50 = 24350$. Group
`FULCRUM2-20260909034500-001` was skipped: "no premium history for
NSE:NIFTY2691523550CE, NSE:NIFTY2691523550PE" (`skippedEntries`) — the stored
23550 series starts at 09:35. The strategy believed it open. 09:20–09:30
(closes 23512.20, 23509.40, 23515.90) all passed the gate
($|S - 23500| \ge 10$) and re-derived the same six legs: no signal.

### Signal 1492 — 09:35, the first filled group → orders 2230–2235 → positions 1126–1131

`MetadataJson`: `spot_price 23526.25`, `atm_strike 23550`, reason
`Fulcrum 2 Straddle Adjusted. Active: [23500, 23550]`. Gate:
$|23526.25 - 23500| = 26.25 \ge 10$; $A \leftarrow 23550$; pair still
$\{23500, 23550\}$; PE wing $0.035 \times 23526.25 = 823.42$,
$\lfloor 22702.83/50 \rfloor \cdot 50 = 22700$ (was 22650), CE wing
$\lceil 24349.67/50 \rceil \cdot 50 = 24350$. The set differs by the PE wing,
so the strategy closed group 001 (six legs ignored: never opened) and opened
`FULCRUM2-20260909040500-002`, filled at the 09:35 candle closes: BUY 1 lot
`NSE:NIFTY2691522700PE` @ 5.65, BUY `…24350CE` @ 3.90, SELL `…23500CE` @
173.90, SELL `…23500PE` @ 95.85, SELL `…23550CE` @ 143.25, SELL `…23550PE` @
116.80. Premium sold 529.80 points, wings cost 9.55.

### Signal 1493 — 09:45, close on a strike crossing → orders 2236–2241

Close 23498.55: $|23498.55 - 23550| = 51.45 \ge 10$; $K_\circ = 23500$,
pair $\{23450, 23500\}$, wings $\lfloor 22676.10/50 \rfloor \cdot 50 = 22650$,
$\lceil 24321.00/50 \rceil \cdot 50 = 24350$. `CLOSE_GROUP` 002: SELL 22700PE
@ 5.65, SELL 24350CE @ 3.90, BUY 23500CE @ 159.00, BUY 23500PE @ 105.60, BUY
23550CE @ 131.35, BUY 23550PE @ 127.85.

$$
(173.90 - 159.00) \times 1 \times 65 = 14.90 \times 65 = 968.50 \quad (\text{position 1128, SHORT 23500CE})
$$

Position 1129 (SHORT 23500PE): $(95.85 - 105.60) \times 65 = -633.75$;
1130 (SHORT 23550CE): $(143.25 - 131.35) \times 65 = 773.50$; 1131 (SHORT
23550PE): $(116.80 - 127.85) \times 65 = -718.25$; the wings closed at their
entry price, 0.00. Group 002: **+390.00** for ten minutes in which the spot
fell 27.7 points — the calls lost more premium than the puts gained. The
matching `OPEN_GROUP` 003 (`[23450, 23500]`) was skipped: no 23450 candle
before 10:00. The run was flat until 10:05 while the strategy held 003 in
state.

### Signals 1495 / 1496 — 10:10, a rebuild because one wing moved → orders 2248–2259

Group 004 had opened at 10:05 (signal 1494, close 23475.80, pair
$\{23450, 23500\}$, wings 22650 / 24300: $\lceil 24297.45/50 \rceil \cdot 50 = 24300$)
— orders 2242–2247: BUY 22650PE @ 5.60, BUY 24300CE @ 4.75, SELL 23450CE @
178.00, SELL 23450PE @ 93.90, SELL 23500CE @ 148.95, SELL 23500PE @ 114.25.
At 10:10 the close was 23484.85: $|23484.85 - 23500| = 15.15 \ge 10$, pair
unchanged, PE wing unchanged ($\lfloor 22662.88/50 \rfloor \cdot 50 = 22650$),
CE wing $\lceil 24306.82/50 \rceil \cdot 50 = 24350 \ne 24300$. One strike in
six differs, so signal 1495 closed all six — SELL 22650PE @ 5.65, SELL 24300CE
@ 4.65, BUY 23450CE @ 183.50, BUY 23450PE @ 90.05, BUY 23500CE @ 153.00, BUY
23500PE @ 109.55 — and signal 1496 opened `FULCRUM2-20260909044000-005`
with the same five contracts at the same prices plus BUY 24350CE @ 4.20.
Positions 1132–1137 booked $0.05 \times 65 = 3.25$, $-0.10 \times 65 = -6.50$,
$-5.50 \times 65 = -357.50$, $3.85 \times 65 = 250.25$,
$-4.05 \times 65 = -263.25$, $4.70 \times 65 = 305.50$: group 004
**−68.25** after five minutes, a rebuild that changed the exposure by one
deep-OTM call.

### Signals 1536 / 1537 — 14:40 and 14:50, the last group and the square-off

Signal 1536 (close 23488.85, $A$ was 23500 since 14:30, $|{-11.15}| \ge 10$,
pair $\{23450, 23500\}$, wings 22650 / 24350) opened
`FULCRUM2-20260909091000-025`: orders 2494–2499, BUY 22650PE @ 5.20, BUY
24350CE @ 3.95, SELL 23450CE @ 179.25, SELL 23450PE @ 79.40, SELL 23500CE @
148.00, SELL 23500PE @ 98.95. Signal 1537, reason `End of backtest`,
`square_off: true`, at the last driver bar 14:50 (close 23489.55): orders
2500–2505 at 5.25, 3.90, 183.70, 76.15, 151.45, 94.75.

$$
0.05 \times 65 - 0.05 \times 65 - 4.45 \times 65 + 3.25 \times 65 - 3.45 \times 65 + 4.20 \times 65 = -29.25
$$

(positions 1258–1263: +3.25, −3.25, −289.25, +211.25, −224.25, +273.00).

### The run

25 groups built, 23 filled (001 and 003 skipped), 138 positions, 276 orders,
every fill 1 lot. Sum of `RealizedPnl` = **+₹1,196.00** (`realizedPnl:
1196.0` in signal 1538's summary), no charges: the four short legs made
+1,231.75, the wings −35.75. Best group 022, `[23550, 23600]` held 12:40 →
14:25 (16 bars, the longest): +1,709.50; worst 021, `[23500, 23550]` 12:30 →
12:40: −1,059.50, and 020 before it (12:25 → 12:30, spot 23510.60 →
23547.85): −1,020.50 — two groups on a 55-point rise (23510.60 → 23566.15)
cost ₹2,080.00 against the ₹3,276.00 the other 21 made.

## Limitations

- **Wing churn on NIFTY.** Below a spot of 25,000 the wing grid falls back to
  the 50-point strike grid, so a wing strike moves with every ≈ 48–52 points
  of spot and each move rebuilds all six legs: 17 of run 108's 24 rebuilds
  changed nothing but a wing worth 3–6 points, re-opening the other five legs at the
  prices they were just closed at. Free on paper; live each is a
  spread-crossing on ten fills. On BANKNIFTY the grid is 500 and the wing
  moves every ≈ 500 points.
- **The threshold does not damp the churn.** $\theta$ = 10 points on NIFTY
  only decides whether a bar is *looked at*; the rebuild is decided by the
  leg set. The docstring's "when the spot drifts more than the adjustment
  threshold … the whole group is closed and rebuilt" is the gate, not the
  trigger.
- **Naked between the wings.** The wings sit ≈ 3.5 % away (823 points in run
  108, bought for 3.90–5.65); inside that band four short options carry the
  move. Group 020 lost ₹1,020.50 on a 37-point rise in five minutes, group
  021 ₹1,059.50 on the next 18 points.
- **No exit of its own.** No stop, target or time exit on the SELL side; a
  `group` or `overall` rule is the only protection, and a `leg` rule that
  trips on one short leaves an unbalanced group.
- **Not told about platform closes.** After a close it did not make, the
  next rebuild's `CLOSE_GROUP` is ignored and the `OPEN_GROUP` fills; live,
  after a `group` stop the run re-enters at the next rebuild with no memory
  of the stop.
- **One unpriced leg refuses the whole open** (live: any of six contracts with
  no quote within 10 s, or a wing strike the master lacks; replay: a leg with
  no candle), and the strategy then believes the group is open until the next
  rebuild — run 108 was flat 09:15–09:35 and 09:45–10:05 for that reason.
- **`legs_summary` says "1-2 straddles"**; the code always sells exactly two
  (Entry). The description's "Designed for BANKNIFTY-scale prices" is true of
  the wing grid: on NIFTY the same constants behave differently.
- **First-bar anchor.** On the first evaluation the anchor is the lower
  bracketing strike, not the nearest one (the `st1 == 0` path skips
  `st1 = atm`); a spot in the upper half of an interval is then judged
  against a strike up to $\Delta$ below it. Afterwards the anchor is always
  the nearest strike.
- **A spot exactly on a strike skips the bar** (`atm == price`); half-to-even
  rounding at an exact midpoint.
- **Run 108's premiums are two different feeds.** The straddle strikes'
  candles are the ingestor's bars from the live runs of 2026-09-09
  (`SourceKey = live`, starting when a live run first subscribed: 23500 from
  09:15, 23550 from 09:35, 23450 from 10:00, all ending 14:50); the wings were
  synced from FYERS (`syncedSymbols`, 77 bars to 15:35). The feed syncs from
  FYERS only for a contract with nothing stored, so the gaps were not filled.
  The index driver is `live` too: no bars 13:20–13:40, none after 14:50, so
  group 022's 105 wall-clock minutes were 16 bars.
- **Backtest differs from live.** One evaluation per bar close instead of per
  tick; fills at candle close, no slippage, `charges_per_lot` 0; expired
  contracts have no FYERS history and are skipped; EOD at 15:15 instead of
  15:30; no pre-open ticks. Live, the runner resolves the six logical symbols
  per signal and waits up to 10 s for quotes, time in which the strategy sees
  no ticks.
- **OI is never read.**

## Facts (machine-readable)

```yaml
name: Fulcrum2Straddle20
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
FROM simulation_runs WHERE "Id" = 108;

/* signals cited: 1492-1496, 1536, 1537, 1538 (BACKTEST_SUMMARY with skippedEntries, dataNotes) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 108 ORDER BY "Id";

/* fills cited: 2230-2259, 2494-2505 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 108 ORDER BY "Id";

/* positions cited: 1126-1137, 1258-1263; totals 138 / 1196.00; wings vs shorts; per group */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 108 ORDER BY "Id";
SELECT count(*), sum("RealizedPnl"), count(DISTINCT "GroupId") FROM paper_positions WHERE "SimulationRunId" = 108;
SELECT "Direction", sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 108 GROUP BY 1;
SELECT "GroupId", min("OpenedUtc") AT TIME ZONE 'Asia/Kolkata', max("ClosedUtc") AT TIME ZONE 'Asia/Kolkata', sum("RealizedPnl")
FROM paper_positions WHERE "SimulationRunId" = 108 GROUP BY 1 ORDER BY 2;

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:NIFTY2691523500CE';

/* the driver bars (spot = 5m close) and their source */
SELECT "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "Close", "SourceKey" FROM candles
WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5' AND "TimeStampUtc"::date = '2026-09-09' ORDER BY 1;

/* option candle coverage that explains the two skipped opens */
SELECT "Symbol", "SourceKey", count(*), min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata'
FROM candles WHERE "Resolution" = '5' AND "TimeStampUtc"::date = '2026-09-09'
  AND "Symbol" IN ('NSE:NIFTY2691523450CE', 'NSE:NIFTY2691523500CE', 'NSE:NIFTY2691523550CE', 'NSE:NIFTY2691522700PE', 'NSE:NIFTY2691524350CE')
GROUP BY 1, 2 ORDER BY 1;

Offline check of the leg sequence: instantiate get_parameterised_strategies()["Fulcrum2Straddle20"]({"lots": 1})
and call on_bar once per driver close with atm_strike = round_to_step(close, 50), strike_step = 50.0;
the OPEN_GROUP reasons and group ids match signals 1492-1536 and the skipped entries.
-->
