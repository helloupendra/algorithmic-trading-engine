# Fulcrum2StraddleBuy20

Source: `strategies/fulcrum/fulcrum_2_straddle_20.py` (`Fulcrum2Straddle20Strategy`),
registered in `strategies/variants.py` as `Fulcrum2StraddleBuy20` — the same
class as `Fulcrum2Straddle20` with the factory defaults `adjustment_steps=0.2`
and `direction="BUY"`. The direction and the absence of wings come from
`strategies/fulcrum/_direction.py` (`resolve_direction`), the time stop from
`strategies/fulcrum/_exit_rules.py` (`long_time_stop_close`), the strike
arithmetic from `strategies/strike_math.py`. Every rule below is read from the
code; where the code and its docstring or catalog text disagree, the code is
documented and the difference listed under Limitations.

## Idea

Buy a straddle on each of the two strikes that bracket the spot — four long
legs — and rebuild the pair around the spot once it has moved at least
`adjustment_steps` of a strike step away from an anchor strike and out of the
bracket. For a buyer that rebuild is where a move is realised: the straddles
were bought with the spot between them, and the spot leaving the bracket is
the move the premium was paid for. A pair that sits inside its bracket for
`max_hold_minutes` is closed as a decay loss and a fresh pair is bought on the
next evaluation.

This is the buy twin of `Fulcrum2Straddle20`, whose short pairs earn the decay
this one pays. `variants.py` keeps the 0.2 threshold "deliberately … the same
as their sell counterparts so the two can be compared on identical data", and
suggests widening it "if the rolling proves too costly". Nothing in this
repository tests the bet beyond the run cited below, which lost ₹162.50 on one
session.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks (live) | the run's spot symbol (`NSE:NIFTY50-INDEX` in run 109) | every tick | none — it enters on the first evaluation | ingestor → Redis stream `market:ticks`; the runner calls `on_bar` per tick |
| option quotes (live) | CE and PE at the two strikes bracketing the spot, named as logical symbols `NIFTY_CE_23500` and resolved to the master's exact contract of the chosen expiry (`execution_runner.resolve_leg_symbol`) | latest quote | none | the runner puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for a quote on an OPEN, 0 s on a CLOSE; the API fills at that quote |
| index candles (replay) | the spot symbol | the run's resolution (5m in run 109) | none; no warm-up (`get_data_requirements` is the base empty list) | `candles` — run 109's 2026-09-09 bars have `SourceKey = live` (built from the ingestor's ticks), 63 session bars |
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
  5-minute bars in run 109): the bar's close is the spot and the bar's *start*
  is the timestamp; a signal on the bar stamped 10:15 fills at the close of
  each contract's 10:15 candle (`feed.option_close_at`). Session bars only
  (`timeutil.in_session`, 09:15 ≤ t < 15:30 IST); a missing bar is simply not
  an evaluation (run 109's time stop of 13:45 read "held 65 minutes" because
  the 13:20–13:40 bars are absent from `candles`).
- **Session window: none of its own.** It buys on the first evaluation
  (run 109: an attempt on the 09:15 bar) and keeps rebuilding until the run
  stops; no last-entry time. In a replay the engine refuses `OPEN_GROUP`
  after `eod_square_off_ist` (15:15 by default) and squares off at the first
  bar at or after it; run 109's bars end at 14:50, so its close was the
  end-of-range "End of backtest" square-off instead.
- **15:30:** live, `MarketHoursService` stops every run with flatten at or
  after 15:30 IST ("Market closed (15:30 IST)"). The strategy relies on it; it
  has no notion of the session.

## Entry

### Notation

$\Delta$ is the strike step (`resolve_step(inp.strike_step, params["strike_step"])`:
the run parameter, else the step the platform read off the option chain, else
50; 50 on NIFTY, 100 on BANKNIFTY). For the spot $S_t$:

$$
A_t = \operatorname{round}\!\left(\tfrac{S_t}{\Delta}\right)\Delta,\qquad
L_t = \left\lfloor \tfrac{S_t}{\Delta} \right\rfloor \Delta,\qquad
U_t = \left\lceil \tfrac{S_t}{\Delta} \right\rceil \Delta,\qquad
\tau = \text{adjustment\_steps} \cdot \Delta
$$

— the nearest, the lower and the upper strike (`round_to_step`,
`round_down_to_step`, `round_up_to_step`; Python's `round`, so an exact
midpoint rounds to the even multiple) and the threshold in points
(`steps_to_points`). At the default 0.2, $\tau$ = 10 points on NIFTY, 20 on
BANKNIFTY, 5 on MIDCPNIFTY (step 25), 20 on SENSEX: the same fifth of the way
to the next strike on every grid. The state keeps an **anchor** strike $a$
(`st1`, 0 when there is no group) and the open group's strikes $\{L_g, U_g\}$
(`current_group_legs`).

### Rule

On the first evaluation (state fresh, $a = 0$) with the spot not exactly on a
strike:

$$
\text{buy } \text{CE}(L_t),\ \text{PE}(L_t),\ \text{CE}(U_t),\ \text{PE}(U_t)
\quad\text{(} \texttt{lots} \text{ of each)},\qquad a \leftarrow L_t
$$

as one `OPEN_GROUP` — group id `FULCRUM2-<timestamp>-<n>`, reason "Fulcrum 2
Straddle Adjusted. Active: [$L_t$, $U_t$]" — with `group_entry_utc` = $t$.
If $A_t = S_t$ (the close sits exactly on a strike) the evaluation does
nothing at all (`if atm == price: return []`).

In words: buy the straddle at the strike just below the spot and the straddle
at the strike just above it, immediately, with no condition on the market.

### Contract and size

- **Expiry:** the earliest expiry in the master on or after the bar's IST date
  (replay, `ContractResolver.expiry_for`) or the runner's start date (live,
  `valid_expiries[0]`, fixed for the run). Run 109 on 2026-09-09 used the
  2026-09-15 weekly (`NSE:NIFTY26915…`).
- **Contract:** the exact CE / PE of the master at (strike, expiry). In a
  replay an OPEN whose leg cannot be resolved *or priced* is skipped as a whole
  ("no premium history for …"), yet the strategy has already recorded the
  group in its state (see Limitations).
- **Quantity:** `lots` (`BaseStrategy.lots_from`, legacy `quantity`, default
  `default_lots` = 1) on every leg; `paper_orders.Quantity` is lots and the API
  multiplies by `Instruments.LotSize` (NIFTY 65; run 109 froze `lot_size: 65`).
- **Fill:** replay — each contract's candle close at the signal bar, no
  slippage, `charges_per_lot` 0 in run 109. Live — the latest quote; an OPEN
  with no quote after 10 s is rejected by the API.

## Position management

### The time stop first (`long_time_stop_close`)

At the top of every evaluation, for a buyer with an open group:

$$
t - t_e \ge \text{max\_hold\_minutes} \;\Longrightarrow\; \text{CLOSE\_GROUP (all legs, SELL)},\ a \leftarrow 0,\ \text{return}
$$

Reason "Time stop: held $h$ minutes without the move this position was opened
for; decay is the only thing working." (`exit: "time_stop"` in the metadata).
Nothing else runs on that evaluation, so the buyer is flat for one evaluation
— the next bar (run 109: closed 11:00, re-entered 11:05) or the next tick —
and then re-enters under the entry rule with a fresh anchor $a = L_t$.

### The rebuild (the class's own logic)

On every other evaluation with $S_t \ne A_t$:

$$
\text{hold} \iff 0 < |S_t - a| < \tau
$$

Otherwise the anchor moves to the nearest strike, $a \leftarrow A_t$ (on a
fresh state, $a \leftarrow L_t$), the wanted pair is $\{L_t, U_t\}$, and

$$
\text{rebuild} \iff \{L_t, U_t\} \ne \{L_g, U_g\}
\iff S_t \notin [\,L_g, U_g\,]
$$

— a `CLOSE_GROUP` of the old four legs (side flipped by `closing_legs`,
reason "Adjusting straddles") and an `OPEN_GROUP` of the new four in the same
evaluation, with `group_entry_utc` reset. If the pair is unchanged the
evaluation only moves the anchor.

In words: the group is always the two strikes around the spot; it is rebuilt
the first time the spot is at least $\tau$ away from the anchor *and* outside
the current bracket. The anchor is the nearest strike as of the last evaluation
that passed the $\tau$ gate — or the lower strike right after an entry.

Consequences the reader should not have to derive:

- **The $\tau$ buffer is relative to the anchor, not to the strike being
  crossed.** When the spot has been in the upper half of the bracket the anchor
  is $U_g$ and a move past $U_g$ by less than $\tau$ holds (run 109, 12:35: spot
  23557.45, anchor 23550, held; 12:40: 23566.15, rebuilt). Right after an entry
  the anchor is $L_g$ whatever half the spot is in, so a spot that opened the
  group in the upper half and then crosses $U_g$ rebuilds at once: run 109's
  group `…-008` was bought at 13:50 with the spot at 23543.45 (anchor 23500) and
  rebuilt at 13:55 with the spot 1.95 points above 23550.
- **A third straddle cannot occur at this threshold.** The code carries slots
  `st0` / `st2` for a strike kept from the previous pair when it is within
  $\tau$ of the spot; a kept strike is a full step from the new pair, so for
  `adjustment_steps` < 1 the group is always exactly two straddles. (The
  variables `ce_value` / `pe_value` are computed and never used.)
- **The rebuild sells the straddle the spot just left and buys one that
  includes the strike it is now near.** Two of the four contracts are often
  the same on both sides of the roll and fill at the same candle close in a
  replay (signal 1544 sold `23500CE`/`23500PE` at 156.60 / 94.05 and signal
  1545 bought them at 156.60 / 94.05).

What a rebuild costs a buyer, versus the sell twin: the seller's
`CLOSE_GROUP` buys back four decayed legs and its `OPEN_GROUP` sells four
fresh ones — a rebuild realises decay as income. The buyer's identical signals
sell the four decayed legs (a loss unless the spot's move outran the decay)
and pay four fresh premiums; the debit paid is the most the new group can
lose. The paper engine charges no spread or slippage and run 109 carried
`charges_per_lot` 0; on a real book every rebuild crosses the spread on eight
fills. `_exit_rules.py` says of the family that closing where the spot has just
crossed a strike "takes every winner at one strike step"; at $\tau$ = 0.2 steps
this variant's winner is realised somewhere between 10 points and one strike
plus 10 points from where the pair was bought — 8.50 points of travel for
run 109's group `…-008`, 55.55 for `…-006` (see Limitations).

What changes versus `Fulcrum2Straddle20` and what does not: the side of the
straddle legs (`self.direction`, BUY, so `paper_positions.Direction` is LONG and
P&L is exit − entry); the wings (`use_hedges` is `False` for a buyer, so the
two far-OTM long legs the seller adds at `hedge_strike` — 3.5 % of the spot
away, rounded further out to a 5-step grid when that grid is at most 1 % of
the spot, else to the step — are not built); and the time stop (only a buyer
runs `long_time_stop_close`). The rebuild rule, its threshold and the anchor
logic are identical.

### What the run's risk rules add

`parametersJson.risk` carries three levels that the API's
`StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds` (3 s),
leg → group → overall, each level stop-loss → trailing stop → target; the
replay engine mirrors them once per bar, before and after the strategy runs
(`_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; a trip closes
  that leg only, leaving three legs of the four. The strategy does not notice
  (its `current_group_legs` still lists four); its next `CLOSE_GROUP` for the
  missing leg is ignored by the ledger.
- `group` — rupees on one group's realized + unrealized P&L; closes all four
  legs. The strategy does not notice and will later "close" a group that no
  longer exists, then rebuild.
- `overall` — rupees on the run's total P&L; flattens and ends the run (live)
  or, under the default `scope: "day"`, ends the day (replay). Run 109 set
  none of the three (`risk: {}`).

**What the strategy never does:** it has no premium-based stop or target; a
pair that loses most of its premium inside the bracket is held for the full
`max_hold_minutes`. It never varies `lots`, never trades a single straddle,
never holds a hedge, and is flat only for the one evaluation after a time
stop.

## Exit

Replay, per driver bar, in the order `execute()` runs them:

1. **EOD square-off** at the first bar at or after `eod_square_off_ist`
   (`_eod_check`, 15:15 by default) — not reached in run 109, whose bars end at
   14:50.
2. **Risk sweep** before the strategy sees the bar (leg → group → overall).
3. **Strategy time stop** (`long_time_stop_close`; run 109 signals 1540, 1542,
   1548).
4. **Strategy rebuild** ("Adjusting straddles"; signals 1544, 1546, 1550,
   1552, 1554).
5. **Risk sweep** again after the bar's marks.
6. **End of range** — "End of backtest" at the last driver bar (signal 1556).

Live: the guard's sweep every 3 s, the strategy's time stop then rebuild on
every tick, a manual square-off or the run's stop button, and
`MarketHoursService` at 15:30 IST; the last two run on their own clocks and
whichever takes the run's lock first closes the position.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `adjustment_steps` | 0.2 (factory default; also `default_params`) | $\tau/\Delta$: how far, in strike steps, the spot must be from the anchor before the bracket is re-examined; 20 points on BANKNIFTY, 10 on NIFTY | fewer rebuilds; the spot must leave the bracket by more before the winner is realised (at ≥ 1 a third straddle can be kept) | rebuilds on smaller excursions; at 0 the gate never holds |
| `adjustment_threshold` | unset | legacy spelling in *points on a 100-point grid*; read only when `adjustment_steps` is absent and divided by 100 (`steps_from_params`, `LEGACY_GRID`) | as above | as above |
| `direction` | `"BUY"` (factory default) | side of the straddle legs | n/a — the factory merges `{**defaults, **params}`, so a `parametersJson` with `direction: "SELL"` runs the seller (with wings) under this name | |
| `max_hold_minutes` | 45.0 (`DEFAULT_MAX_HOLD_MINUTES`) | minutes a group may sit inside its bracket before the time stop closes it | fewer time stops, more decay paid per group | more time stops; each re-entry pays four fresh premiums |
| `target_steps` | 2.0 (`DEFAULT_TARGET_STEPS`) | resolved by `resolve_long_exit` and **never read by this class** — the rebuild rule is its only move-based exit | no effect | no effect |
| `use_hedges` | `false` for a buyer | `true` adds the seller's two far-OTM long wings (`hedge_strike`, 3.5 % away) to every group | n/a — a hedged long is "a legitimate structure, just not the default" (`_direction.py`); it adds premium paid and protects nothing a long option does not already cap | |
| `strike_step` | unset (platform value) | explicit grid override in points | n/a — only for an underlying whose chain the platform cannot read | |
| `lots` (legacy `quantity`) | `default_lots` = 1 | lots per leg; P&L = points × lots × lot size | larger positions, same signals | |

Non-positive or non-numeric values fall back to the defaults.

## Worked example

Run **109** — OfflineReplay, `NSE:NIFTY50-INDEX`, NIFTY, 5-minute bars,
2026-09-09 (one session, 63 driver bars), `ParametersJson` verbatim:
`{"adjustment_steps":0.2,"direction":"BUY","lots":1,"underlying":"NIFTY","resolution":"5m",
"eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":65,"lot_size_source":"master",
"risk":{},"stop_loss":null,"target":null}`. Strike step 50 ($\tau$ = 10 points),
expiry 2026-09-15, lot size 65 (`instruments.LotSize` for
`NSE:NIFTY2691523500CE`). Database timestamps are UTC; every time below is IST
(UTC + 5:30). Spot values are the 5-minute closes of `candles`
(`NSE:NIFTY50-INDEX`, resolution `5`). The SQL is at the end of this file.

### 09:15 and 09:45 — two entries the engine could not fill

On the 09:15 bar (close 23500.90) the strategy asked for the pair
[23500, 23550]; `NSE:NIFTY2691523550CE` / `PE` have no candle before 09:35
(their `candles` rows are the ingestor's own bars, `SourceKey = live`), so the
OPEN was skipped — "no premium history for NSE:NIFTY2691523550CE,
NSE:NIFTY2691523550PE" in the summary signal 1557 — while the state recorded
group `…-001` as open with anchor 23500. At 09:45 (close 23498.55, below the
bracket) it "closed" that phantom (four legs ignored: "no matching open
position") and asked for [23450, 23500]; 23450 has no candle before 10:00, so
`…-002` was skipped too. The first booked group is therefore `…-003`.

### Signal 1539 — 10:15: the first fill → orders 2506–2509 → positions 1264–1267

`spot_price 23516.80`, `atm_strike 23500`, reason "Fulcrum 2 Straddle Adjusted.
Active: [23500, 23550]": the phantom `…-002` had strikes [23450, 23500]; the
10:15 close, 16.80 above the anchor 23500, had left that bracket. Fills, BUY 1 lot
each: `NSE:NIFTY2691523500CE` @ 165.15 (order 2506), `…23500PE` @ 96.45 (2507),
`…23550CE` @ 136.15 (2508), `…23550PE` @ 117.55 (2509).

Closed by signal 1540 at 11:00 — nine bars, 45 minutes, later — "Time stop:
held 45 minutes without the move this position was opened for" with the spot
at 23536.95, still inside the bracket: SELL @ 175.10 (order 2510), 89.85
(2511), 145.10 (2512), 109.40 (2513).

$$
\begin{aligned}
\text{1264: } & (175.10 - 165.15) \times 1 \times 65 = 9.95 \times 65 = 646.75 \\
\text{1265: } & (89.85 - 96.45) \times 65 = -6.60 \times 65 = -429.00 \\
\text{1266: } & (145.10 - 136.15) \times 65 = 8.95 \times 65 = 581.75 \\
\text{1267: } & (109.40 - 117.55) \times 65 = -8.15 \times 65 = -529.75
\end{aligned}
$$

= the four `RealizedPnl` values; the group netted +₹269.75 (the calls gained
more than the puts lost as the spot rose 20 points inside the bracket). The
strategy was flat for one bar and re-entered at 11:05 (signal 1541, group
`…-004`, the same pair, closed by the time stop at 11:50 for −₹1,121.25).

### Signals 1544 / 1545 — 12:25: a rebuild by 0.60 points

Group `…-005` [23450, 23500] was bought at 11:55 (signal 1543, spot 23488.50).
The anchor followed the nearest strike on each bar that passed the gate and
stood at 23500 after the 12:20 bar (close 23487.05). The 12:25 close was
23510.60: $|23510.60 - 23500| = 10.60 \ge 10$, and the spot is above
$U_g$ = 23500 — a rebuild. Had the bar closed at 23509.99 the gate would have
held. CLOSE: SELL `23450CE` @ 188.35 (order 2526), `23450PE` @ 76.00 (2527),
`23500CE` @ 156.60 (2528), `23500PE` @ 94.05 (2529).

$$
\begin{aligned}
\text{1272: } & (188.35 - 177.20) \times 65 = 11.15 \times 65 = 724.75 \\
\text{1273: } & (76.00 - 85.40) \times 65 = -9.40 \times 65 = -611.00 \\
\text{1274: } & (156.60 - 146.30) \times 65 = 10.30 \times 65 = 669.50 \\
\text{1275: } & (94.05 - 105.45) \times 65 = -11.40 \times 65 = -741.00
\end{aligned}
$$

net +₹42.25. Signal 1545 opened `…-006` [23500, 23550]: `23500CE` @ 156.60 and
`23500PE` @ 94.05 (orders 2530, 2531 — the prices they were just sold at),
`23550CE` @ 127.75 (2532), `23550PE` @ 116.00 (2533).

### Signals 1546 / 1547 — 12:40: the run's winner → positions 1276–1279

The 12:30 bar closed 23547.85 (anchor → 23550, bracket unchanged); 12:35
closed 23557.45, 7.45 from the anchor — held; 12:40 closed 23566.15: 16.15
from the anchor and above $U_g$ = 23550. CLOSE `…-006`: SELL `23500CE` @ 192.20
(order 2534), `23500PE` @ 77.50 (2535), `23550CE` @ 160.75 (2536), `23550PE` @
95.65 (2537).

$$
\begin{aligned}
\text{1276: } & (192.20 - 156.60) \times 65 = 35.60 \times 65 = 2314.00 \\
\text{1277: } & (77.50 - 94.05) \times 65 = -16.55 \times 65 = -1075.75 \\
\text{1278: } & (160.75 - 127.75) \times 65 = 33.00 \times 65 = 2145.00 \\
\text{1279: } & (95.65 - 116.00) \times 65 = -20.35 \times 65 = -1322.75
\end{aligned}
$$

net +₹2,060.50 on a 55.55-point rise in 15 minutes. The replacement `…-007`
[23550, 23600] (orders 2538–2541) then never closed 10 points from its anchor
23550 (the closes to 13:15 ran 23542.10–23566.00) and was sold by the time stop,
signal 1548 at 13:45 — "held 65 minutes", the 13:20–13:40 bars being absent —
for −₹1,121.25 (positions 1280–1283).

### Signal 1550 — 13:55: the anchor after a re-entry

Signal 1549 re-entered at 13:50 with the spot at 23543.45: nearest strike
23550, but the pair is [23500, 23550] and the fresh anchor is $L$ = 23500. The
13:55 close, 23551.95, was 51.95 from that anchor and above 23550, so the
five-minute-old group was sold (positions 1284–1287, net +₹659.75) and
[23550, 23600] bought (signal 1551). With the anchor at 23550 the same close
would have been 1.95 inside the 10-point gate.

### Signal 1556 — 14:50: end of the stored bars

The last driver bar is 14:50. `…-011` [23450, 23500] (signal 1555 at 14:40,
after the spot fell 11.15 below 23500) was closed by the engine's end-of-range
square-off "End of backtest": positions 1296–1299,
$(183.70 - 179.25) + (76.15 - 79.40) + (151.45 - 148.00) + (94.75 - 98.95) = 0.45$
points × 65 = +₹29.25. The 15:15 EOD square-off never had a bar to run on.

### The run

9 groups booked: 5 rebuilds (net +₹1,781.00, 3 profitable), 3 time stops
(−₹1,972.75, 1 profitable), 1 end-of-range close (+₹29.25). Sum of
`RealizedPnl` over the 36 positions = **−₹162.50** = `realizedPnl` in signal
1557; `charges` 0; "8 close legs ignored" (the two phantoms); no risk rule
tripped.

## Limitations

- **The strategy is not told when the platform declines or closes a group.**
  A skipped OPEN (no premium history, a missing contract, after the EOD
  square-off), a rejected live OPEN, a risk-rule close, the EOD square-off and
  a manual square-off all leave `current_group_id` / `current_group_legs`
  behind. Run 109's first two groups were phantoms; the next CLOSE for each was
  dropped by the ledger and never persisted. Live, the runner prints "The
  strategy still believes this group is open." A `leg` rule leaves three of
  four legs open until the next rebuild.
- **The winner is small by construction.** The rebuild fires within one strike
  plus $\tau$ of where the pair was bought, and can fire within $\tau$;
  `_exit_rules.py` calls closing on
  the ATM change "the most expensive mistake this family can make" for a
  buyer, and `long_time_stop_reason`'s docstring calls the same threshold "the
  winner being realised". The code does the latter; `variants.py` leaves
  widening `adjustment_steps` to the user.
- **The $\tau$ gate protects unevenly.** It is measured from an anchor that
  is the lower strike right after every entry and otherwise the nearest strike
  as of the previous gate-passing evaluation; a pair opened with the spot in
  its upper half can be rebuilt on the next evaluation (13:55 above).
- **Always long premium.** Flat only for one evaluation after a time stop;
  every rebuild pays four fresh premiums, every time-stop re-entry four more.
  No spread, no slippage, no fees in the numbers above.
- **No premium stop.** Without a `leg` / `group` / `overall` rule a pair that
  collapses inside its bracket is held for 45 minutes.
- **`target_steps` is accepted and ignored** by this class.
- **The day's data was the ingestor's, not the broker's.** The 2026-09-09
  index and 23450 / 23500 / 23550 option candles have `SourceKey = live`: they
  begin when the ingestor started watching each contract (23550 at 09:35, 23450
  at 10:00), skip 13:20–13:40, and end at 14:50; 23600 came from FYERS
  (`fyers`, 09:15–15:35). The run therefore missed its first hour, one time
  stop ran 20 minutes late, and the session ended 25 minutes before the EOD
  square-off. The skip reason "no premium history" is the same text whether a
  contract has no candles at all or none yet that day.
- **Catalog text.** The class description still says "Designed for
  BANKNIFTY-scale prices with 100-point strikes" — the threshold now scales
  with the grid — and describes the rebuild as the spot drifting "from the
  active strike", which is the anchor, not necessarily a strike of the pair.
- **`direction` is a run parameter.** A `parametersJson` with
  `direction: "SELL"` turns this run into the seller.
- **Replay differs from live.** One evaluation per closed bar instead of per
  tick (a rebuild can only fire at a bar close and the time stop is quantised
  to the bar); fills at candle closes with no slippage; expired contracts have
  no broker history so replays of past weeks skip their entries; EOD square-off
  at 15:15 instead of 15:30; the overall rule's `scope` is honoured only by the
  replay.

## Facts (machine-readable)

```yaml
name: Fulcrum2StraddleBuy20
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
FROM simulation_runs WHERE "Id" = 109;

/* signals 1539..1557 (1557 is BACKTEST_SUMMARY: skippedEntries, dataNotes, realizedPnl) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 109 ORDER BY "Id";

/* fills 2506..2577 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 109 ORDER BY "Id";

/* positions 1264..1299; sum(RealizedPnl) = -162.50 */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist
FROM paper_positions WHERE "SimulationRunId" = 109 ORDER BY "Id";
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 109;

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
  FROM simulation_signals WHERE "SimulationRunId" = 109 AND "SignalType" = 'CLOSE_GROUP'),
pnl AS (SELECT "GroupId", sum("RealizedPnl") AS net FROM paper_positions WHERE "SimulationRunId" = 109 GROUP BY 1)
SELECT c.why, count(*), sum(p.net), sum(CASE WHEN p.net > 0 THEN 1 ELSE 0 END) AS winners
FROM closes c JOIN pnl p USING ("GroupId") GROUP BY 1;

The phantom groups and every booked signal were reproduced by feeding the 63
closes through Fulcrum2Straddle20Strategy(params={"adjustment_steps": 0.2,
"direction": "BUY"}) with step 50: 21 signals, of which the engine booked 18.
-->
