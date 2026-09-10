# FulcrumBuy

Source: `strategies/fulcrum/fulcrum_standard.py` (`FulcrumStrategy`), registered
in `strategies/variants.py` as `FulcrumBuy` — the same class as `Fulcrum` with
the factory default `direction="BUY"`. The direction is resolved by
`strategies/fulcrum/_direction.py` (`resolve_direction`) and the buy-side exit
by `strategies/fulcrum/_exit_rules.py` (`long_exit_reason`). Every rule below
is read from the code; where the code and its docstring or catalog text
disagree, the code is documented and the difference listed under Limitations.

## Idea

A rolling long ATM straddle: buy the at-the-money call and put, hold them
until the spot has travelled `target_steps` strikes away from the strike they
were bought at (the move the premium was paid for) or until `max_hold_minutes`
have passed with no such move (decay is the only thing working), then buy a
fresh straddle at the current ATM in the same evaluation. It is never flat.
The bet is that, over 45-minute windows, the index moves two strikes often
enough to pay for the premium lost in the windows where it does not.

This is the buy twin of `Fulcrum`. The comment in `variants.py` is explicit
that the two are not mirror images: a seller earns time decay and a buyer pays
it, so a roll that realises a decayed straddle is income for the seller and a
loss for the buyer. The thresholds were "deliberately left the same as their
sell counterparts so the two can be compared on identical data". Nothing in
this repository tests the bet beyond the run cited below, which lost ₹577.50
over four sessions.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks (live) | the run's spot symbol (`NSE:NIFTYBANK-INDEX` in run 59; `NSE:NIFTY50-INDEX`, …) | every tick | none — it enters on the first evaluation that has both ATM contracts | ingestor → Redis stream `market:ticks`; the runner calls `on_bar` per tick |
| option quotes (live) | ATM CE and ATM PE of the chosen expiry (`get_contract_requirements`, the base default `atm_ce` / `atm_pe`) | latest quote | none | the runner puts each leg on the ingestor watchlist and waits up to `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for a quote on an OPEN, 0 s on a CLOSE; the API fills at that quote |
| index candles (replay) | the spot symbol | the run's resolution (1m in run 59) | none; there is no warm-up — `get_data_requirements` is the base empty list, so the runner's warm-up feeds it nothing | backfill: `candles` (run 59: `SourceKey = fyers`, 375 bars per session) |
| option candles (replay) | every contract the legs name | the run's resolution | none | backfill: `candles` per contract; a contract with no candle at or before the bar on that IST day cannot be filled and the entry is skipped (`feed.option_close_at`) |

It never reads bars (`inp.bars` is untouched), option-chain OI or any
indicator. The only market inputs it uses are `spot_price`, `atm_strike`,
`strike_step`, `timestamp_utc` and the two ATM contracts.

## Timeframe

- **No bar of its own.** The class declares no data requirement, so nothing
  is aggregated for it and there is no minimum history.
- **Live: every spot tick.** `execution_runner.py` calls `on_bar` on each tick
  of the spot symbol with `spot_price` = the tick's LTP, `atm_strike` =
  `round_to_step(LTP, strike_step)` and `timestamp_utc` = the tick's
  `exchangeTimestampUtc`, else its `receivedUtc`. The time stop is measured on
  those stamps and the target on the LTP, so both fire on the first tick that
  satisfies them.
- **Replay: once per driver bar** at the run's resolution (`backtest/engine.py`;
  1-minute bars in run 59). The strategy is handed the bar's close as the spot
  and the bar's *start* as the timestamp; a signal on the bar stamped 09:15 is
  filled at the close of that contract's 09:15 candle (`feed.option_close_at`).
  Only session bars drive it (`timeutil.in_session`, 09:15 ≤ t < 15:30 IST).
- **Session window: none of its own.** It buys on the first evaluation — the
  first tick after the runner starts, or the 09:15 bar in a replay (run 59:
  signals 933, 951, 967 and 983, one per session, all on the 09:15 bar) — and keeps rolling
  until something outside it stops the run. There is no last-entry time. In a
  replay the engine refuses `OPEN_GROUP` signals after `eod_square_off_ist`
  (15:15 in run 59; "entry after the EOD square-off") and squares off at the
  first bar at or after that time, before the strategy sees the bar.
- **15:30:** live, `MarketHoursService` stops every run with flatten at or
  after 15:30 IST ("Market closed (15:30 IST)"). The strategy relies on it: it
  has no notion of the session and would otherwise hold — and keep rolling —
  indefinitely.

## Entry

### Notation

$\Delta$ is the strike step of the underlying (100 on BANKNIFTY, 50 on NIFTY;
`resolve_step(inp.strike_step, params["strike_step"])` — the run parameter
wins, else the step the platform read off the option chain, else
`DEFAULT_STEP` = 50). $S_t$ is the spot at evaluation $t$ and

$$
K(S) = \operatorname{round}\!\left(\frac{S}{\Delta}\right)\cdot\Delta
$$

is the nearest strike (`round_to_step`; Python's `round`, so an exact midpoint
such as 57450 on a 100 grid rounds to the even multiple). The strategy uses
`inp.atm_strike` when the platform supplies it — both the runner and the replay
do, computed by the same formula — and falls back to $K(S_t)$.

### Rule

$$
\text{enter at } t_0 \iff \text{CE}(K_0) \text{ and } \text{PE}(K_0) \text{ both resolve},\qquad K_0 = K(S_{t_0})
$$

Legs: BUY `lots` of the exact CE at $K_0$ and BUY `lots` of the exact PE at
$K_0$, one `OPEN_GROUP` with group id `FULCRUM-<timestamp>-001` and reason
"Initial Fulcrum long straddle at ATM $K_0$"; the state records
`group_entry_strike` = $K_0$ and `group_entry_utc` = $t_0$.

In words: there is no entry condition. The first evaluation that can name both
ATM contracts buys the straddle.

### Contract and size

- **Expiry:** the earliest expiry in the instrument master on or after the
  bar's IST date (replay, `ContractResolver.expiry_for`) or on or after the UTC
  date the runner started (live, `valid_expiries[0]`, fixed for the run). For
  BANKNIFTY in run 59 the master's only expiry on or after 2026-09-01 was
  2026-09-29, so every contract was the September monthly (`NSE:BANKNIFTY26SEP…`).
- **Contract:** the exact CE / PE row at $(K_0, \text{expiry})$
  (`contracts_for_requirements` → `get_exact_contract`). If either is missing
  `on_bar` returns nothing (`if not atm_ce or not atm_pe: return []`) and tries
  again on the next evaluation; the replay lists the gap once in
  `skippedEntries`.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`, legacy
  key `quantity`, default `default_lots` = 1) on every leg; `paper_orders.Quantity`
  is lots and the API multiplies by `Instruments.LotSize` (BANKNIFTY 30 in the
  September 2026 master; run 59 froze `lot_size: 30` into its parameters).
- **Fill:** replay — the option candle's close at the signal bar, no slippage,
  `charges_per_lot` 0 in run 59. Live — the latest quote; an OPEN with no quote
  after 10 s is sent unpriced and the API rejects it ("rejected rather than
  filled at zero"), while the strategy's state already records the group as
  open (see Limitations).

## Position management

### The buy-side exit rule (`long_exit_reason`)

On every evaluation after the first, with $K_e$ the strike the open straddle
was bought at, $t_e$ the time it was bought, $n$ = `target_steps` and $m$ =
`max_hold_minutes`:

$$
\text{close} \iff \underbrace{|S_t - K_e| \ge n\,\Delta}_{\text{Target}}
\;\lor\;
\underbrace{t - t_e \ge m \text{ minutes}}_{\text{Time stop}}
$$

The target is tested first; the reason string names which one fired
("Target: the spot moved 223.65 from the opening strike 57400, past the
2-strike threshold (200.00)." / "Time stop: held 45 minutes with the spot only
35.95 from the opening strike 57400; decay is the only thing working."). On
BANKNIFTY $n\Delta$ = 200 points, on NIFTY 100.

Two things the formula hides:

- The distance is measured from the **strike**, not from the spot at entry.
  The spot at entry lies anywhere within $\Delta/2$ of $K_e$, so the travel the
  spot actually has to make is between $1.5\Delta$ and $2.5\Delta$ (150–250
  points on BANKNIFTY) depending on where inside the strike interval the
  straddle was bought. Run 59's three target exits needed 187.70, 192.10 and
  206.55 points of travel.
- The time is wall-clock on the evaluation stamps. On 1-minute bars the stop
  fires exactly 45 minutes after entry (run 59: every time stop is at
  entry + 45); on 5-minute bars at the first bar at or past 45 minutes; live on
  the first tick past it.

### The roll

When the rule fires the strategy emits, in the same evaluation, a `CLOSE_GROUP`
for the old group (the same two contracts, side flipped to SELL by
`closing_legs`; metadata `previous_atm` = $K_e$, `current_atm` = $K(S_t)$) and
an `OPEN_GROUP` for a new straddle at $K_t = K(S_t)$ — reason "Opening new
Fulcrum long straddle at ATM $K_t$" — resetting `group_entry_strike` and
`group_entry_utc`. It reopens even when $K_t = K_e$: 11 of the 29 strategy
rolls in run 59 re-bought the strike just sold, and in a replay both legs fill
at the same candle close (signal 936 sold `57400CE`/`57400PE` at 919.65 / 565.00
and signal 937 bought them back at 919.65 / 565.00), so such a roll changes
nothing but the clock.

What a roll costs a buyer, versus the sell twin: the seller's `CLOSE_GROUP`
buys back a straddle that has decayed since it was sold and its `OPEN_GROUP`
collects fresh premium — the roll is where its decay is realised. For the buyer
the same two signals *sell* the decayed straddle (a loss unless the spot's move
outran the decay) and *pay* fresh premium for the next one; the debit paid is
the most the new group can lose. The paper engine models neither spread nor
slippage (replay fills at candle closes, live at the latest quote) and run 59
carried `charges_per_lot` 0, so the four market orders of every roll cost
nothing in these numbers; on a real book each roll crosses the spread twice per
leg.

What changes versus `Fulcrum` and what does not: the leg side (`self.direction`
on every leg, BUY instead of SELL, so `paper_positions.Direction` is LONG and
P&L is $(\text{exit} - \text{entry})$ rather than the reverse); the exit rule
(the seller closes as soon as $K(S_t) \ne K_e$ — "ATM shifted" — and the buyer
must not, because that "takes every winner at one strike step", per
`_exit_rules.py`); and nothing else. `FulcrumStrategy` builds no wing legs in
either direction — `resolve_direction` returns `use_hedges = False` for a buyer
and the class does not read it in any case — so "no wings" is a property of
this class, not of this variant.

### What the run's risk rules add

`parametersJson.risk` carries three levels that the API's
`StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds` (3 s,
`appsettings.json`), leg → group → overall, each level stop-loss → trailing
stop → target; the replay engine mirrors them once per bar, before and after
the strategy runs (`backtest/engine.py`, `_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; a trip closes
  that leg only, leaving the other half of the straddle open. The strategy
  does not notice: its state still lists both legs and its next `CLOSE_GROUP`
  for the leg already closed is ignored by the ledger ("no open position").
- `group` — rupees on one group's realized + unrealized P&L; closes the whole
  straddle. The strategy does not notice either and will emit a `CLOSE_GROUP`
  later that finds nothing, then reopen.
- `overall` — rupees on the run's total P&L; flattens and ends the run (live)
  or, under the default `scope: "day"`, ends the day and resumes tomorrow
  (replay). Run 59 set none of the three (`risk: {}`).

**What the strategy never does:** it has no premium-based stop or target
(`_exit_rules.py`: "Premium-based stops and targets are NOT re-derived here");
a straddle that loses most of its premium in 45 minutes is held for the full
45. It never varies `lots`, never adds a leg, never hedges, and is never flat
between the first evaluation and the run's end.

## Exit

Replay, per driver bar, in the order `execute()` runs them:

1. **EOD square-off** at the first bar at or after `eod_square_off_ist`
   (`_eod_check`, "End-of-day square-off 15:15 IST"; run 59 signals 950, 966,
   982, 998), and the day-rollover square-off if a day ended with positions
   open.
2. **Risk sweep** before the strategy sees the bar: leg rules, group rules,
   overall rule (`_check_risk`).
3. **Strategy target** — `|S_t − K_e| ≥ target_steps · Δ` (signals 938, 944,
   946).
4. **Strategy time stop** — `t − t_e ≥ max_hold_minutes` (26 of run 59's 33
   closes).
5. **Risk sweep** again after the bar's marks.
6. **End of range** — "End of backtest" square-off at the last driver bar if
   anything is still open.

Live: the guard's sweep every 3 s (leg → group → overall, closing through
reduce-only `CLOSE_GROUP` signals), the strategy's own target then time stop on
every tick, a manual square-off or the run's stop button, and
`MarketHoursService` at 15:30 IST; the last two run on their own clocks and
whichever takes the run's lock first closes the position.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `direction` | `"BUY"` (factory default in `variants.py`) | which side every leg takes; `"BUY"`, `"LONG"` or `"B"` mean long, anything else short | n/a — but the factory merges `{**defaults, **params}`, so a run of `FulcrumBuy` whose `parametersJson` says `direction: "SELL"` runs the seller under the buyer's name | |
| `target_steps` | 2.0 (`DEFAULT_TARGET_STEPS`) | strikes the spot must be from the entry strike before the straddle is closed as the move having arrived; in points: × Δ (200 BANKNIFTY, 100 NIFTY) | fewer, larger target exits; more groups end on the time stop | winners taken sooner and smaller; at 1 the exit is the seller's "ATM changed" rule the family's comments warn against |
| `max_hold_minutes` | 45.0 (`DEFAULT_MAX_HOLD_MINUTES`) | minutes a straddle is held while the target is not reached | fewer rolls, more decay paid per group; a hold can run to the 15:30 close | more rolls; each pays a fresh straddle |
| `strike_step` | unset (platform value) | explicit grid override, in points | n/a — only for an underlying whose chain the platform cannot read; a wrong value names contracts that do not exist | |
| `lots` (legacy `quantity`) | `default_lots` = 1 | lots per leg; P&L = points × lots × lot size | larger positions, same signals | |
| `use_hedges` | `false` for a buyer (`resolve_direction`) | accepted and stored; **`FulcrumStrategy` never reads it** — there are no wing legs in this class | no effect | no effect |

Values that are not positive numbers fall back to the defaults
(`resolve_long_exit`).

## Worked example

Run **59** — OfflineReplay, `NSE:NIFTYBANK-INDEX`, BANKNIFTY, 1-minute bars,
2026-09-01 → 2026-09-04 (4 sessions, 1500 driver bars), `ParametersJson`
verbatim: `{"direction":"BUY","lots":1,"underlying":"BANKNIFTY","resolution":"1m",
"eod_square_off_ist":"15:15","charges_per_lot":0,"lot_size":30,"lot_size_source":"master",
"risk":{},"stop_loss":null,"target":null}`. Strike step 100 (every ATM in the
signals is a multiple of 100 and the reasons say "2-strike threshold
(200.00)"), expiry 2026-09-29, lot size 30 (`instruments.LotSize` for
`NSE:BANKNIFTY26SEP57500CE`). Database timestamps are UTC; every time below is
IST (UTC + 5:30). The SQL is at the end of this file.

### Signal 933 — 09:15, 2026-09-01: the first straddle → orders 1541, 1542 → positions 780, 781

`MetadataJson`: `spot_price 57462.05`, `atm_strike 57500`, reason "Initial
Fulcrum long straddle at ATM 57500" — the first driver bar, no condition.
Fills: BUY 1 lot `NSE:BANKNIFTY26SEP57500CE` @ 884.35 (order 1541), BUY 1 lot
`NSE:BANKNIFTY26SEP57500PE` @ 595.30 (order 1542).

Closed by signal 934 at 10:00 — exactly 45 minutes later — "Time stop: held 45
minutes with the spot only 128.15 from the opening strike 57500" (spot
57371.85; the target needed 200): SELL @ 844.65 (order 1543) and @ 636.00
(order 1544).

$$
(844.65 - 884.35) \times 1 \times 30 = -39.70 \times 30 = -1191.00
\qquad
(636.00 - 595.30) \times 30 = 40.70 \times 30 = 1221.00
$$

= `RealizedPnl` of positions 780 and 781; the straddle netted +₹30.00. The
same bar's signal 935 opened group `…-002` at the new ATM 57400 (orders 1545
@ 902.95, 1546 @ 594.95).

### Signals 936 / 937 — 10:45: a time-stop roll onto the same strike

Group `…-002` (bought 10:00 at 57400) hit its 45 minutes with the spot at
57435.95, 35.95 from the strike. `current_atm` is still 57400, so the CLOSE
(order 1547 SELL `57400CE` @ 919.65, order 1548 SELL `57400PE` @ 565.00;
position 782: $(919.65 - 902.95) \times 30 = 501.00$, position 783:
$(565.00 - 594.95) \times 30 = -898.50$, net −₹397.50) was followed by an
OPEN of the identical contracts at the identical prices (orders 1549 @ 919.65,
1550 @ 565.00, group `…-003`). In the paper model this roll moved nothing but
`group_entry_utc`.

### Signal 938 — 11:13: the target → orders 1551, 1552 → positions 784, 785

Reason "Target: the spot moved 223.65 from the opening strike 57400, past the
2-strike threshold (200.00)." — `spot_price 57623.65`, 28 minutes into the
group. The spot's own travel since the 10:45 entry was
$57623.65 - 57435.95 = 187.70$ points; the rule measures from the strike.
Fills: SELL `57400CE` @ 1024.95 (order 1551), SELL `57400PE` @ 470.25 (order
1552).

$$
(1024.95 - 919.65) \times 30 = 105.30 \times 30 = 3159.00
\qquad
(470.25 - 565.00) \times 30 = -94.75 \times 30 = -2842.50
$$

= positions 784 and 785, net +₹316.50. Signal 939 re-entered at ATM 57600
(orders 1553 @ 900.95, 1554 @ 543.35). The other two targets: signal 946 at
13:50 (239.30 from 57500, spot down to 57260.70; positions 792/793,
−2836.50 + 3246.00 = +₹409.50) and signal 944 at 13:15 (232.75 from 57700,
spot 57467.25), which *lost*: position 790 $(759.90 - 860.25) \times 30 =
-3010.50$, position 791 $(668.95 - 569.95) \times 30 = 2970.00$, net −₹40.50 —
a target exit is a distance rule, not a profit rule.

### Signal 998 — 15:15, 2026-09-04: the platform's square-off → orders 1671, 1672 → positions 844, 845

Group `…-036` was bought at 14:30 (signal 997, ATM 57600, orders 1669 @ 798.00,
1670 @ 541.00). At the 15:15 bar the engine's EOD square-off ran first:
"End-of-day square-off 15:15 IST", SELL @ 747.75 (order 1671) and @ 572.20
(order 1672).

$$
(747.75 - 798.00) \times 30 = -50.25 \times 30 = -1507.50
\qquad
(572.20 - 541.00) \times 30 = 31.20 \times 30 = 936.00
$$

= positions 844 and 845, net −₹571.50. The strategy's own time stop for this
group fell on the same bar; its CLOSE found no open position (ignored) and its
OPEN `…-037` was refused ("entry after the EOD square-off (15:15 IST)") — the
last of four such refusals in the summary signal 999, which also counts the
"14 close legs ignored" the phantoms produced (see Limitations).

### The run

33 straddles booked: 26 closed by the time stop (net +₹48.00, 9 profitable),
3 by the target (+₹685.50, 2 profitable), 4 by the EOD square-off
(−₹1,311.00, none profitable). Sum of `RealizedPnl` over the 66 positions =
**−₹577.50** = `realizedPnl` in signal 999; `charges` 0; no risk rule tripped.

## Limitations

- **The strategy is not told when the platform closes its position.** Its
  state (`current_group_id`, `current_group_legs`) survives an EOD square-off,
  a risk-rule close, a rejected or skipped OPEN, and a manual square-off. In
  run 59 every day ended the same way: the 15:15 square-off closed the real
  straddle, the strategy's next exit "closed" a straddle that no longer existed
  (all legs "ignored: no matching open position") and opened a phantom
  (`…-010`, `…-019`, `…-028`, `…-037`, all refused after 15:15); next morning
  the phantom's own rule fired on the 09:15 bar ("Target: the spot moved
  381.85 from the opening strike 57300" for `…-010`, "Time stop: held 1080
  minutes" for `…-028` — never persisted, because a CLOSE with every leg
  ignored is not posted) and the day's first real straddle carries the reason
  "Opening new …" rather than "Initial …". Live, the runner's own warning
  applies after an API refusal: "The strategy still believes this group is
  open." A leg closed by a `leg` rule leaves the other leg open until the
  strategy's next roll.
- **Always in the market.** From the first tick to the 15:30 close it holds a
  straddle; the time stop reopens immediately, so there is no cool-off after a
  losing window. Every roll pays a fresh straddle; the paper model charges no
  spread and run 59 no fees, so the −₹577.50 is before both.
- **Target is a distance from the strike, not a profit.** Signal 944 closed a
  group 232.75 points from its strike at a loss. The travel needed varies
  between 1.5 and 2.5 strikes with where inside the interval the straddle was
  bought.
- **No premium stop.** Without a `leg` / `group` / `overall` rule a straddle
  that collapses inside its 45 minutes is held to the minute.
- **Same-strike rolls are free only on paper.** 11 of run 59's 29 rolls re-bought
  the contracts just sold at the same candle close.
- **Expiry choice is fixed and coarse.** BANKNIFTY has only monthly expiries
  in the master, so run 59 traded contracts 25–28 days out at ₹500–1,000 of
  premium each; on NIFTY the same rule picks the weekly, and on an expiry day
  the contract expiring that afternoon. Live the expiry is chosen once at
  start-up.
- **`direction` is a run parameter, not part of the name.** A `parametersJson`
  with `direction: "SELL"` turns a `FulcrumBuy` run into the seller.
- **Catalog text.** `describe_for` rewrites the class description for this
  variant correctly ("holds the straddle until the spot has travelled the
  target number of strikes, or a time stop…"), but the sell twin's note
  ("hedged by far OTM wings", `direction_note`) claims wings that
  `FulcrumStrategy` does not build; `use_hedges` is resolved and never read.
- **Replay differs from live.** One evaluation per closed bar instead of per
  tick (the target can only fire at a bar close; the time stop is quantised to
  the bar); fills at candle closes with no slippage; contracts that have
  expired have no FYERS history and their entries are skipped (run 59's
  September monthlies still existed); EOD square-off at 15:15 instead of
  15:30; the overall rule's `scope` is honoured only by the replay.

## Facts (machine-readable)

```yaml
name: FulcrumBuy
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
FROM simulation_runs WHERE "Id" = 59;

/* signals 933..999 (999 is BACKTEST_SUMMARY: skippedEntries, dataNotes, realizedPnl) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 59 ORDER BY "Id";

/* fills 1541..1672 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 59 ORDER BY "Id";

/* positions 780..845; sum(RealizedPnl) = -577.50 */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist
FROM paper_positions WHERE "SimulationRunId" = 59 ORDER BY "Id";
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 59;

/* lot size and expiry */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:BANKNIFTY26SEP57500CE';
SELECT "ExpiryDate", count(*) FROM instruments
WHERE "Underlying" = 'BANKNIFTY' AND "OptionType" IN ('CE','PE') AND "ExpiryDate" >= '2026-09-01' GROUP BY 1 ORDER BY 1;

/* exits by reason, with each group's net P&L */
WITH closes AS (
  SELECT "GroupId",
         CASE WHEN "MetadataJson" LIKE '%Time stop%' THEN 'time_stop'
              WHEN "MetadataJson" LIKE '%Target:%' THEN 'target'
              WHEN "MetadataJson" LIKE '%End-of-day%' THEN 'eod' ELSE 'other' END AS why
  FROM simulation_signals WHERE "SimulationRunId" = 59 AND "SignalType" = 'CLOSE_GROUP'),
pnl AS (SELECT "GroupId", sum("RealizedPnl") AS net FROM paper_positions WHERE "SimulationRunId" = 59 GROUP BY 1)
SELECT c.why, count(*), sum(p.net), sum(CASE WHEN p.net > 0 THEN 1 ELSE 0 END) AS winners
FROM closes c JOIN pnl p USING ("GroupId") GROUP BY 1;

/* same-strike rolls: previous_atm = current_atm on the CLOSE metadata (11 of 29) */
SELECT count(*) FILTER (WHERE ("MetadataJson"::json->>'previous_atm') = ("MetadataJson"::json->>'current_atm')),
       count(*) FILTER (WHERE ("MetadataJson"::json->>'previous_atm') <> ("MetadataJson"::json->>'current_atm'))
FROM simulation_signals WHERE "SimulationRunId" = 59 AND "SignalType" = 'CLOSE_GROUP' AND "MetadataJson" LIKE '%previous_atm%';

/* the driver bars: 375 FYERS 1m candles per session */
SELECT ("TimeStampUtc" AT TIME ZONE 'Asia/Kolkata')::date, "SourceKey", count(*)
FROM candles WHERE "Symbol" = 'NSE:NIFTYBANK-INDEX' AND "Resolution" = '1'
  AND "TimeStampUtc" BETWEEN '2026-08-31 18:30' AND '2026-09-04 18:30' GROUP BY 1, 2 ORDER BY 1;

The phantom groups and their unposted reasons were reproduced by feeding the
same 1500 closes through FulcrumStrategy(params={"direction": "BUY"}) with
step 100: 73 signals, of which the engine booked 66 (33 opens, 33 closes).
-->
