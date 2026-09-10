# Fulcrum3StraddleBuy175

Source: `strategies/fulcrum/fulcrum_3_straddle_175.py` (`Fulcrum3Straddle175Strategy`),
registered in `strategies/variants.py` as `Fulcrum3StraddleBuy175` — the same
class as `Fulcrum3Straddle175` with the factory default `direction="BUY"`. The
direction and the absence of wings come from `strategies/fulcrum/_direction.py`
(`resolve_direction`), the time stop from `strategies/fulcrum/_exit_rules.py`
(`long_time_stop_close`), the strike arithmetic from `strategies/strike_math.py`.
Every rule below is read from the code; where the code and its docstring or
catalog text disagree — and for this class the re-centre distance in the name,
the docstring and the description is not the one the code applies — the code
is documented and the difference listed under Limitations.

## Idea

Buy three straddles at once — at the nearest strike and at the strike on
either side of it, six long legs — and re-centre the block on the new nearest
strike when the spot has moved far enough from the centre. For a buyer a
re-centre is where a move is realised: the block was bought around the spot,
and the spot leaving it is the move the premium was paid for. A block that
sits still for `max_hold_minutes` is sold as a decay loss and a fresh one is
bought on the next evaluation.

This is the buy twin of `Fulcrum3Straddle175`, whose short block earns the
decay this one pays. `variants.py` keeps the re-centre thresholds "deliberately
… the same as their sell counterparts so the two can be compared on identical
data". Nothing in this repository tests the bet beyond the run cited below,
which lost ₹1,693.25 on one session.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks (live) | the run's spot symbol (`NSE:NIFTY50-INDEX` in run 111) | every tick | none — it enters on the first evaluation | ingestor → Redis stream `market:ticks`; the runner calls `on_bar` per tick |
| option quotes (live) | CE and PE at the centre strike and its two neighbours, named as logical symbols `NIFTY_CE_23550` and resolved to the master's exact contract of the chosen expiry (`execution_runner.resolve_leg_symbol`) | latest quote | none | the runner puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for a quote on an OPEN, 0 s on a CLOSE; the API fills at that quote |
| index candles (replay) | the spot symbol | the run's resolution (5m in run 111) | none; no warm-up (`get_data_requirements` is the base empty list) | `candles` — run 111's 2026-09-09 bars have `SourceKey = live` (built from the ingestor's ticks), 63 session bars |
| option candles (replay) | every contract the legs name | the run's resolution | none | `candles` per contract (`ContractResolver.resolve_logical` → `feed.option_close_at`); a contract with no candle at or before the bar on that IST day cannot be filled and the entry is skipped |

The class also declares the base `atm_ce` / `atm_pe` requirement (it does not
override `get_contract_requirements`), so the platform resolves the ATM pair
on every evaluation; the strategy never reads them — its legs are the logical
symbols above. It never reads bars, OI or any indicator.

## Timeframe

- **No bar of its own.** Nothing is aggregated for it; there is no minimum
  history.
- **Live: every spot tick** (`execution_runner.py`): `spot_price` = the tick's
  LTP, `timestamp_utc` = the tick's `exchangeTimestampUtc`, else `receivedUtc`;
  the time stop is measured on those stamps.
- **Replay: once per driver bar** at the run's resolution (`backtest/engine.py`;
  5-minute bars in run 111): the bar's close is the spot and the bar's *start*
  is the timestamp; a signal on the bar stamped 09:35 fills at the close of
  each contract's 09:35 candle (`feed.option_close_at`). Session bars only
  (`timeutil.in_session`, 09:15 ≤ t < 15:30 IST); a missing bar is not an
  evaluation (run 111 re-entered at 13:45 after its 13:15 time stop because
  the 13:20–13:40 bars are absent from `candles`).
- **Session window: none of its own.** It buys on the first evaluation
  (run 111: an attempt on the 09:15 bar) and keeps re-centring until the run
  stops; no last-entry time. In a replay the engine refuses `OPEN_GROUP` after
  `eod_square_off_ist` (15:15 by default) and squares off at the first bar at
  or after it; run 111's bars end at 14:50, so its close was the end-of-range
  "End of backtest" square-off instead.
- **15:30:** live, `MarketHoursService` stops every run with flatten at or
  after 15:30 IST ("Market closed (15:30 IST)"). The strategy relies on it; it
  has no notion of the session.

## Entry

### Notation

$\Delta$ is the strike step (`resolve_step(inp.strike_step, params["strike_step"])`:
the run parameter, else the step the platform read off the option chain, else
50; 50 on NIFTY, 100 on BANKNIFTY). For the spot $S_t$:

$$
A_t = \operatorname{round}\!\left(\tfrac{S_t}{\Delta}\right)\Delta
$$

is the nearest strike (`round_to_step`; Python's `round`, so an exact midpoint
such as 23525 rounds to the even multiple, 23500) and $A_t \pm \Delta$ its
neighbours (`neighbour_strike`). The two bands the code compares against are
literals in strike steps:

$$
b_{\text{in}} = 1.05\,\Delta,\qquad b_{\text{out}} = 1.95\,\Delta
$$

(`steps_to_points(1.05, step)`, `steps_to_points(1.95, step)`; the comment
calls them "the original 105 and 195" of BANKNIFTY's 100-point grid) — 52.5 and
97.5 points on NIFTY, 105 and 195 on BANKNIFTY. The state keeps the block's
centre $A_g$ (`st0`), its lower neighbour (`st1`) and its upper neighbour
$H_g = A_g + \Delta$ (`st2`); all three are 0 when there is no group.

### Rule

On the first evaluation (state fresh) with the spot not exactly on a strike:

$$
\text{buy CE and PE at each of } \{A_t - \Delta,\ A_t,\ A_t + \Delta\}
\quad\text{(} \texttt{lots} \text{ of each)}
$$

as one `OPEN_GROUP` — group id `FULCRUM-3-<timestamp>-<n>`, reason "Fulcrum 3
Straddle Adjusted. Active: [$A_t$, $A_t-\Delta$, $A_t+\Delta$]" (the list
is in the code's slot order: centre, lower, upper) — with `group_entry_utc`
= $t$. If $A_t = S_t$ the evaluation does nothing at all
(`if atm == price: return []`).

In words: buy the ATM straddle and the straddles one strike below and one
strike above it, immediately, with no condition on the market.

### Contract and size

- **Expiry:** the earliest expiry in the master on or after the bar's IST date
  (replay, `ContractResolver.expiry_for`) or the runner's start date (live,
  `valid_expiries[0]`, fixed for the run). Run 111 on 2026-09-09 used the
  2026-09-15 weekly (`NSE:NIFTY26915…`).
- **Contract:** the exact CE / PE of the master at (strike, expiry). In a
  replay an OPEN with any leg that cannot be resolved *or priced* is skipped as
  a whole ("no premium history for …"), yet the strategy has already recorded
  the group in its state (see Limitations).
- **Quantity:** `lots` (`BaseStrategy.lots_from`, legacy `quantity`, default
  `default_lots` = 1) on every leg; `paper_orders.Quantity` is lots and the API
  multiplies by `Instruments.LotSize` (NIFTY 65; run 111 froze `lot_size: 65`).
- **Fill:** replay — each contract's candle close at the signal bar, no
  slippage, `charges_per_lot` 0 in run 111. Live — the latest quote; an OPEN
  with no quote after 10 s is rejected by the API.

## Position management

### The time stop first (`long_time_stop_close`)

At the top of every evaluation, for a buyer with an open group:

$$
t - t_e \ge \text{max\_hold\_minutes} \;\Longrightarrow\; \text{CLOSE\_GROUP (all six legs, SELL)},\ A_g \leftarrow 0,\ \text{return}
$$

Reason "Time stop: held $h$ minutes without the move this position was opened
for; decay is the only thing working." (`exit: "time_stop"`). Nothing else
runs on that evaluation, so the buyer is flat for one evaluation — the next
bar (run 111: closed 11:05, re-entered 11:10) or the next tick — and then
re-enters under the entry rule.

### The re-centre (the class's own logic)

On every other evaluation with $S_t \ne A_t$, the gate is written against the
block's **upper** neighbour $H_g$ whichever way the spot has gone (`st2` in
both `if` branches of the threshold check). Solved for the spot it reads:

$$
\text{hold} \iff A_g - \Delta < S_t \le A_g - 0.05\,\Delta
\quad\lor\quad
A_g + 2.05\,\Delta \le S_t < A_g + 2.95\,\Delta
$$

Otherwise the centre moves to the nearest strike, $A_g \leftarrow A_t$, the
wanted block is $\{A_t - \Delta, A_t, A_t + \Delta\}$, and

$$
\text{re-centre} \iff A_t \ne A_g
\iff S_t < A_g - \Delta \;\lor\; S_t \ge A_g + \tfrac{1}{2}\Delta
$$

(the second inequality within the pass region, i.e. up to $A_g + 2.05\Delta$
or from $A_g + 2.95\Delta$) — a `CLOSE_GROUP` of the old six legs (side flipped
by `closing_legs`, reason "Adjusting straddles") and an `OPEN_GROUP` of the
new six in the same evaluation, with `group_entry_utc` reset. A pass that
leaves $A_t = A_g$ changes nothing.

In words, on NIFTY (Δ = 50) with the block centred at $A$: the block is held
while the spot is between $A - 50$ (exclusive) and $A - 2.5$; it is sold and
re-bought one strike lower as soon as a close is *below* $A - 50$, and one
strike higher as soon as a close is *at or above* $A + 25$ — half a strike up,
a full strike down. On BANKNIFTY (Δ = 100): hold in $(A-100, A-5]$, re-centre
below $A - 100$ or at $A + 50$ and above. The second hold band
($[A + 102.5, A + 147.5)$ on NIFTY) can only be reached by a single evaluation
that jumps at least $1.55\Delta$ (77.5 points on NIFTY) from the zone where
the centre is unchanged, with no evaluation in between; it is in the code and
not in the run.

Consequences the reader should not have to derive:

- **The trigger is asymmetric and small on the upside.** A block bought with
  the spot just under the rounding midpoint re-centres on the next evaluation
  that closes above it: run 111's group `…-005` was bought at 11:10 with the
  spot at 23524.95 (centre 23500) and sold at 11:15 at 23526.95 — a 2.00-point
  move. Downward it needs a close below the lower neighbour (09:45: 23498.55
  against a centre of 23550, 27.70 points from the entry spot).
- **Two of the three straddles survive every re-centre.** The centre and one
  neighbour are re-bought at the prices they were just sold at in a replay
  (signal 1574 sold `23500CE`/`23500PE`/`23550CE`/`23550PE` at 172.80 / 90.05 /
  142.85 / 109.95; signal 1575 bought them back at the same prices); only the
  far neighbour is swapped for a new one.
- **There is no third-straddle logic left in this class.** The `st0`/`st1`/`st2`
  slots always hold exactly the centre and its two neighbours after a pass;
  `ce_value` / `pe_value` are computed and never used.

What a re-centre costs a buyer, versus the sell twin: the seller's
`CLOSE_GROUP` buys back six decayed legs and its `OPEN_GROUP` sells six fresh
ones — a re-centre realises decay as income. The buyer's identical signals
sell the six decayed legs (a loss unless the spot's move outran the decay) and
pay six fresh premiums; the debit paid is the most the new block can lose. The
paper engine charges no spread or slippage and run 111 carried
`charges_per_lot` 0; on a real book every re-centre crosses the spread on
twelve fills. `_exit_rules.py` warns that closing where the nearest strike
changes "takes every winner at one strike step" for a buyer; on the upside
this class closes at half a step (see Limitations).

What changes versus `Fulcrum3Straddle175` and what does not: the side of the
six straddle legs (`self.direction`, BUY, so `paper_positions.Direction` is
LONG and P&L is exit − entry); the wings (`use_hedges` is `False` for a buyer,
so the two far-OTM long legs the seller adds at `hedge_strike` — 3.5 % of the
spot away, rounded further out to a 5-step grid when that grid is at most 1 %
of the spot, else to the step — are not built); and the time stop (only a
buyer runs `long_time_stop_close`). The re-centre rule and its bands are
identical.

### What the run's risk rules add

`parametersJson.risk` carries three levels that the API's
`StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds` (3 s),
leg → group → overall, each level stop-loss → trailing stop → target; the
replay engine mirrors them once per bar, before and after the strategy runs
(`_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; a trip closes
  that leg only, leaving five of six. The strategy does not notice (its
  `current_group_legs` still lists six); its next `CLOSE_GROUP` for the missing
  leg is ignored by the ledger.
- `group` — rupees on one group's realized + unrealized P&L; closes all six
  legs. The strategy does not notice and will later "close" a group that no
  longer exists, then re-centre.
- `overall` — rupees on the run's total P&L; flattens and ends the run (live)
  or, under the default `scope: "day"`, ends the day (replay). Run 111 set
  none of the three (`risk: {}`).

**What the strategy never does:** it has no premium-based stop or target; a
block that loses most of its premium while the spot stays in the hold band is
held for the full `max_hold_minutes` (run 111's group `…-009` lost ₹1,352.00
that way). It never varies `lots`, never trades fewer than three straddles,
never holds a hedge, and is flat only for the one evaluation after a time
stop.

## Exit

Replay, per driver bar, in the order `execute()` runs them:

1. **EOD square-off** at the first bar at or after `eod_square_off_ist`
   (`_eod_check`, 15:15 by default) — not reached in run 111, whose bars end at
   14:50.
2. **Risk sweep** before the strategy sees the bar (leg → group → overall).
3. **Strategy time stop** (`long_time_stop_close`; run 111 signals 1572, 1580,
   1582).
4. **Strategy re-centre** ("Adjusting straddles"; signals 1570, 1574, 1576,
   1578).
5. **Risk sweep** again after the bar's marks.
6. **End of range** — "End of backtest" at the last driver bar (signal 1584).

Live: the guard's sweep every 3 s, the strategy's time stop then re-centre on
every tick, a manual square-off or the run's stop button, and
`MarketHoursService` at 15:30 IST; the last two run on their own clocks and
whichever takes the run's lock first closes the position.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `direction` | `"BUY"` (factory default) | side of the six straddle legs | n/a — the factory merges `{**defaults, **params}`, so a `parametersJson` with `direction: "SELL"` runs the seller (with wings) under this name | |
| `max_hold_minutes` | 45.0 (`DEFAULT_MAX_HOLD_MINUTES`) | minutes a block may sit without a re-centre before the time stop sells it | fewer time stops, more decay paid per block | more time stops; each re-entry pays six fresh premiums |
| `target_steps` | 2.0 (`DEFAULT_TARGET_STEPS`) | resolved by `resolve_long_exit` and **never read by this class** — the re-centre bands are literals | no effect | no effect |
| `use_hedges` | `false` for a buyer | `true` adds the seller's two far-OTM long wings (`hedge_strike`, 3.5 % away) to every block | n/a — "a legitimate structure, just not the default" (`_direction.py`); it adds premium paid and protects nothing a long option does not already cap | |
| `strike_step` | unset (platform value) | explicit grid override in points; the bands scale with it | n/a — only for an underlying whose chain the platform cannot read | |
| `lots` (legacy `quantity`) | `default_lots` = 1 | lots per leg; P&L = points × lots × lot size | larger positions, same signals | |

There is no parameter for the re-centre distance: `default_params` is empty
and 1.05 / 1.95 are literals in `on_bar`.

## Worked example

Run **111** — OfflineReplay, `NSE:NIFTY50-INDEX`, NIFTY, 5-minute bars,
2026-09-09 (one session, 63 driver bars), `ParametersJson` verbatim:
`{"direction":"BUY","lots":1,"underlying":"NIFTY","resolution":"5m","eod_square_off_ist":"15:15",
"charges_per_lot":0,"lot_size":65,"lot_size_source":"master","risk":{},"stop_loss":null,"target":null}`.
Strike step 50 (bands 52.5 / 97.5 points), expiry 2026-09-15, lot size 65
(`instruments.LotSize` for `NSE:NIFTY2691523500CE`). Database timestamps are
UTC; every time below is IST (UTC + 5:30). Spot values are the 5-minute closes
of `candles` (`NSE:NIFTY50-INDEX`, resolution `5`). The SQL is at the end of
this file.

### 09:15 — an entry the engine could not fill

On the 09:15 bar (close 23500.90) the strategy asked for
[23500, 23450, 23550]; `NSE:NIFTY2691523450CE`/`PE` have no candle before 10:00
and `…23550CE`/`PE` none before 09:35 (their `candles` rows are the ingestor's
own bars, `SourceKey = live`), so the OPEN was skipped — "no premium history
for …" in the summary signal 1585 — while the state recorded group `…-001` as
open, centred on 23500.

### Signal 1569 — 09:35: the first fill → orders 2658–2663 → positions 1340–1345

The 09:35 close, 23526.25, rounds to 23550 (470.525 → 471): a new centre, so
the phantom was "closed" (six legs ignored, "no matching open position") and
`…-002` bought — `spot_price 23526.25`, `atm_strike 23550`, reason "Fulcrum 3
Straddle Adjusted. Active: [23550, 23500, 23600]". Fills, BUY 1 lot each:
`NSE:NIFTY2691523550CE` @ 143.25 (order 2658), `…23550PE` @ 116.80 (2659),
`…23500CE` @ 173.90 (2660), `…23500PE` @ 95.85 (2661), `…23600CE` @ 118.15
(2662), `…23600PE` @ 139.80 (2663).

09:40 closed 23526.35 — inside the hold band $(23500, 23547.5]$, nothing.
09:45 closed 23498.55, below the lower neighbour 23500: signal 1570, "Adjusting
straddles", SELL @ 131.35 (order 2664), 127.85 (2665), 159.00 (2666), 105.60
(2667), 107.00 (2668), 153.05 (2669).

$$
\begin{aligned}
\text{1340: } & (131.35 - 143.25) \times 1 \times 65 = -11.90 \times 65 = -773.50 \\
\text{1341: } & (127.85 - 116.80) \times 65 = 11.05 \times 65 = 718.25 \\
\text{1342: } & (159.00 - 173.90) \times 65 = -14.90 \times 65 = -968.50 \\
\text{1343: } & (105.60 - 95.85) \times 65 = 9.75 \times 65 = 633.75 \\
\text{1344: } & (107.00 - 118.15) \times 65 = -11.15 \times 65 = -724.75 \\
\text{1345: } & (153.05 - 139.80) \times 65 = 13.25 \times 65 = 861.25
\end{aligned}
$$

= the six `RealizedPnl` values, net −₹253.50 on a 27.70-point fall in ten
minutes. The replacement `…-003` [23500, 23450, 23550] could not be priced
(23450 still had no candle) — a second phantom — until the 10:20 close of
23537.80 re-centred on 23550 again and `…-004` was booked (signal 1571). That
block sat inside its band and was sold by the time stop at 11:05 (signal
1572, −₹555.75, positions 1346–1351).

### Signals 1574 / 1575 — 11:15: a re-centre by 2.00 points → positions 1352–1357

Signal 1573 re-entered at 11:10 with the spot at 23524.95 — below the midpoint
23525, so centre 23500, block [23500, 23450, 23550]. The 11:15 close was
23526.95: nearest strike 23550, within $b_{\text{in}}$ of the block's upper
neighbour (|23550 − 23526.95| = 23.05 < 52.5), so the gate passed and the
centre changed. CLOSE: SELL `23500CE` @ 172.80 (order 2688), `23500PE` @ 90.05
(2689), `23450CE` @ 206.15 (2690), `23450PE` @ 72.95 (2691), `23550CE` @ 142.85
(2692), `23550PE` @ 109.95 (2693).

$$
\begin{aligned}
\text{1352: } & (172.80 - 168.50) \times 65 = 4.30 \times 65 = 279.50 \\
\text{1353: } & (90.05 - 92.45) \times 65 = -2.40 \times 65 = -156.00 \\
\text{1354: } & (206.15 - 200.55) \times 65 = 5.60 \times 65 = 364.00 \\
\text{1355: } & (72.95 - 75.05) \times 65 = -2.10 \times 65 = -136.50 \\
\text{1356: } & (142.85 - 138.55) \times 65 = 4.30 \times 65 = 279.50 \\
\text{1357: } & (109.95 - 112.90) \times 65 = -2.95 \times 65 = -191.75
\end{aligned}
$$

net +₹438.75 after five minutes. Signal 1575 bought `…-006`
[23550, 23500, 23600]: the 23500 and 23550 legs at the prices just received
(orders 2694–2697) plus `23600CE` @ 115.90 and `23600PE` @ 133.25 (2698, 2699).
That block was sold at 11:55 when the close (23488.50) fell below 23500
(signal 1576, −₹932.75, positions 1358–1363).

### Signals 1578 / 1579 — 12:30: the run's winner → positions 1364–1369

`…-007` [23500, 23450, 23550] was bought at 11:55 (signal 1577, spot 23488.50;
orders 2706–2711 at 146.30 / 105.45 / 177.20 / 85.40 / 119.85 / 128.00). The
12:25 close (23510.60) passed the gate but kept the centre; 12:30 closed
23547.85 — nearest strike 23550, 2.15 from the upper neighbour — and the
block re-centred. CLOSE: SELL `23500CE` @ 179.35 (order 2712), `23500PE` @ 81.35
(2713), `23450CE` @ 213.80 (2714), `23450PE` @ 65.40 (2715), `23550CE` @ 149.30
(2716), `23550PE` @ 99.95 (2717).

$$
\begin{aligned}
\text{1364: } & (179.35 - 146.30) \times 65 = 33.05 \times 65 = 2148.25 \\
\text{1365: } & (81.35 - 105.45) \times 65 = -24.10 \times 65 = -1566.50 \\
\text{1366: } & (213.80 - 177.20) \times 65 = 36.60 \times 65 = 2379.00 \\
\text{1367: } & (65.40 - 85.40) \times 65 = -20.00 \times 65 = -1300.00 \\
\text{1368: } & (149.30 - 119.85) \times 65 = 29.45 \times 65 = 1914.25 \\
\text{1369: } & (99.95 - 128.00) \times 65 = -28.05 \times 65 = -1823.25
\end{aligned}
$$

net +₹1,751.75 on a 59.35-point rise in 35 minutes — the only re-centre of
the day that paid for the ones around it.

### Signals 1580, 1582, 1584 — the time stops and the end

`…-008` [23550, 23500, 23600] (signal 1579) never closed below 23500 or at or
above 23575 and was sold by the time stop at 13:15 (signal 1580, −₹692.25, positions 1370–1375);
the re-entry came at 13:45 (signal 1581), the first bar after the hole.
`…-009`, bought at 13:45 with the spot at 23553.50, watched the spot fall
41.95 points to 23511.55 without a close below 23500 or at or above 23575
and was sold by the time stop at 14:30 (signal 1582): positions 1376–1381,
$(129.05 - 149.50) + (110.85 - 97.65) + (158.50 - 180.90) + (89.95 - 79.20) +
(103.65 - 121.45) + (135.30 - 119.40) = -20.80$ points × 65 = −₹1,352.00. The
last block `…-010` (signal 1583, 14:35) was closed by the engine's end-of-range
"End of backtest" at the last stored bar, 14:50 (signal 1584, positions
1382–1387, −₹97.50); the 15:15 EOD square-off never had a bar to run on.

### The run

8 blocks booked: 4 re-centres (net +₹1,004.25, 2 profitable), 3 time stops
(−₹2,600.00, none profitable), 1 end-of-range close (−₹97.50). Sum of
`RealizedPnl` over the 48 positions = **−₹1,693.25** = `realizedPnl` in signal
1585; `charges` 0; "12 close legs ignored" (the two phantoms); no risk rule
tripped.

## Limitations

- **The distance in the name is not the distance in the code.** The class
  docstring says "re-centred on a 1.75-step drift", the description "once the
  spot has drifted roughly 1.75 strikes from the outer straddle … 175 points
  … 87.5". `on_bar` compares the spot with the *upper* neighbour against 1.05
  and 1.95 steps in both directions, which works out to a re-centre at half a
  step above the centre and one step below it (25 / 50 points on NIFTY,
  50 / 100 on BANKNIFTY); run 111 re-centred on moves of 2.00 and 27.70 points.
  There is no parameter to change this.
- **The winner is small by construction, and the two sides differ.** On the
  upside a block is sold as soon as the nearest strike changes — the exact
  rule `_exit_rules.py` calls "the most expensive mistake this family can
  make" for a buyer — while on the downside it is held a full strike;
  `long_time_stop_reason`'s docstring calls the same threshold "the winner
  being realised". The code does the latter.
- **The strategy is not told when the platform declines or closes a group.**
  A skipped OPEN (no premium history, a missing contract, after the EOD
  square-off), a rejected live OPEN, a risk-rule close, the EOD square-off and
  a manual square-off all leave `current_group_id` / `current_group_legs`
  behind. Run 111's groups `…-001` and `…-003` were phantoms; their CLOSEs were
  dropped by the ledger and never persisted. Live, the runner prints "The
  strategy still believes this group is open." A `leg` rule leaves five of six
  legs open until the next re-centre.
- **Six premiums at a time, always.** Flat only for one evaluation after a
  time stop; every re-centre pays for a new far straddle and re-buys two, every
  time-stop re-entry pays six. No spread, no slippage, no fees in the numbers
  above.
- **No premium stop.** Without a `leg` / `group` / `overall` rule a block that
  collapses inside its hold band is held for 45 minutes (`…-009`).
- **`target_steps` is accepted and ignored** by this class.
- **The day's data was the ingestor's, not the broker's.** The 2026-09-09
  index and 23450 / 23500 / 23550 option candles have `SourceKey = live`: they
  begin when the ingestor started watching each contract (23550 at 09:35, 23450
  at 10:00), skip 13:20–13:40, and end at 14:50; 23600 came from FYERS
  (`fyers`, 09:15–15:35). The run therefore missed its first twenty minutes
  and the 35 minutes after 09:45, re-entered 25 minutes late after the 13:15 time
  stop, and ended 25 minutes before the EOD square-off. The skip reason "no
  premium history" is the same text whether a contract has no candles at all
  or none yet that day.
- **`direction` is a run parameter.** A `parametersJson` with
  `direction: "SELL"` turns this run into the seller.
- **Replay differs from live.** One evaluation per closed bar instead of per
  tick (a re-centre can only fire at a bar close and the time stop is
  quantised to the bar); fills at candle closes with no slippage; expired
  contracts have no broker history so replays of past weeks skip their
  entries; EOD square-off at 15:15 instead of 15:30; the overall rule's
  `scope` is honoured only by the replay.

## Facts (machine-readable)

```yaml
name: Fulcrum3StraddleBuy175
category: Adjustment
evaluates_on: tick
resolution: any (no bar of its own)
data: ticks, index candles, option candles
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: true
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"
Timestamps are stored in UTC; add 5:30 for IST.

/* the run */
SELECT "Id", "Mode", "Symbol", "Resolution", "StrategyName", "Status", "FromUtc", "ToUtc", "ParametersJson"
FROM simulation_runs WHERE "Id" = 111;

/* signals 1569..1585 (1585 is BACKTEST_SUMMARY: skippedEntries, dataNotes, realizedPnl) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 111 ORDER BY "Id";

/* fills 2658..2753 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 111 ORDER BY "Id";

/* positions 1340..1387; sum(RealizedPnl) = -1693.25 */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist
FROM paper_positions WHERE "SimulationRunId" = 111 ORDER BY "Id";
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 111;

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:NIFTY2691523500CE';

/* the driver bars (63 session bars, hole 13:20-13:40, last 14:50) */
SELECT to_char("TimeStampUtc" AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') AS ist, "Close", "SourceKey"
FROM candles WHERE "Symbol" = 'NSE:NIFTY50-INDEX' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-09 03:45' AND '2026-09-09 10:00' ORDER BY "TimeStampUtc";

/* option candle coverage that day */
SELECT "Symbol", "SourceKey", count(*),
       to_char(min("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', 'HH24:MI'),
       to_char(max("TimeStampUtc") AT TIME ZONE 'Asia/Kolkata', 'HH24:MI')
FROM candles WHERE "Symbol" LIKE 'NSE:NIFTY26915%' AND "Resolution" = '5'
  AND "TimeStampUtc" BETWEEN '2026-09-09 03:00' AND '2026-09-09 10:30' GROUP BY 1, 2 ORDER BY 1;

/* exits by reason, with each group's net P&L */
WITH closes AS (
  SELECT "GroupId",
         CASE WHEN "MetadataJson" LIKE '%Time stop%' THEN 'time_stop'
              WHEN "MetadataJson" LIKE '%Adjusting%' THEN 'adjust'
              WHEN "MetadataJson" LIKE '%End of backtest%' THEN 'end' ELSE 'other' END AS why
  FROM simulation_signals WHERE "SimulationRunId" = 111 AND "SignalType" = 'CLOSE_GROUP'),
pnl AS (SELECT "GroupId", sum("RealizedPnl") AS net FROM paper_positions WHERE "SimulationRunId" = 111 GROUP BY 1)
SELECT c.why, count(*), sum(p.net), sum(CASE WHEN p.net > 0 THEN 1 ELSE 0 END) AS winners
FROM closes c JOIN pnl p USING ("GroupId") GROUP BY 1;

The phantom groups and every booked signal were reproduced by feeding the 63
closes through Fulcrum3Straddle175Strategy(params={"direction": "BUY"}) with
step 50: 19 signals, of which the engine booked 16.
-->
