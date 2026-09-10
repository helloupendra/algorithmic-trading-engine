# Fulcrum

Source: `strategies/fulcrum/fulcrum_standard.py` (`FulcrumStrategy`), registered
as `Fulcrum` in `strategies/variants.py` with no factory defaults (the same
class is registered again as `FulcrumBuy` with `direction="BUY"`; that is a
separate spec). Shared helpers: `strategies/fulcrum/_direction.py` (which way
it trades), `strategies/fulcrum/_exit_rules.py` (`closing_legs`; the long-only
exits are not used by this entry), `strategies/strike_math.py` and
`strategies/contract_selector.py` (strike grid and contracts). Every rule below
is read from the code; where the code and its docstring or catalogue text
disagree, the code is documented and the difference listed under Limitations.

## Idea

Sell the at-the-money straddle and keep it at the money: whenever the nearest
strike to the spot changes, buy the old call and put back and sell the pair at
the new strike. The position is always the two options with the most time
value, so in a session where the spot stays inside one strike interval the
seller collects their decay; every roll books the P&L of the previous straddle,
which in a trending or choppy session is mostly the loss on the leg that went
in the money. Nothing in this repository tests that the decay outpaces the
roll losses beyond the runs cited below — run 58 lost ₹1,261.50 over four
sessions.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| ticks | the run's spot symbol (`NSE:NIFTYBANK-INDEX` in runs 23 and 58; any of `supported_underlyings` — NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX) | every tick | none — `get_data_requirements` is the base class's empty list, so the runner fetches no warm-up and passes no bars (`warmupBars: 0` in run 58's summary) | ingestor → Redis stream `market:ticks`; the live runner calls `on_bar` on every tick of the spot symbol (`execution_runner.py`) |
| index candles | the same spot symbol | the run's resolution (1m in run 58) | none | backfill (`candles`, `SourceKey = fyers` for run 58); the replay driver — each bar's close is the spot (`backtest/engine.py`, `_build_input`) |
| option quotes | the ATM CE and ATM PE of the nearest expiry (`get_contract_requirements` — the base-class default, this class does not override it) | latest quote (live) / option candle at the run's resolution (replay) | none | live: the runner puts both contracts on the ingestor watchlist and the API fills at the latest quote (`PaperTradingService`); replay: option candles from `candles`, synced from FYERS history on first use, filled at the candle close of the signal bar (`feed.option_close_at`) |

It never reads option candles as an input, never reads option-chain OI, and
never reads the tick beyond its `lastTradedPrice`.

## Timeframe

- **Live: every tick, no bar.** The runner evaluates on every spot tick with a
  positive LTP; the strategy has no notion of a bar. Rolls can therefore be
  seconds apart: run 23 (LivePaper, 2026-09-03) has signals 9 and 11 —
  two rolls — at 13:12:59.5 and 13:13:00.3 IST.
- **Replay: once per closed driver bar** at the run's resolution (1m in run
  58, so at most one roll per minute), with the bar's close as the spot.
- **Session window: none.** Neither the strategy nor the runner gates on the
  clock: the first tick on which both ATM contracts resolve opens the first
  straddle (run 23's first open, signal 8, is stamped 13:10:31 IST, ten
  seconds after the run row was created), and there is no last-entry time.
  The backtest skips entries after `eod_square_off_ist` (15:15 in run 58)
  and squares off at that bar (`_eod_check`).
- **15:30:** `MarketHoursService` checks once a minute and at or after 15:30
  IST stops every live run with flatten; the API closes each open position
  with a `CLOSE_GROUP` "Market closed (15:30 IST)" at the latest quote. The
  strategy relies on this — it has no time-based exit of its own — and it is
  not told about it (see Position management).

Times in this document are IST; the database stores UTC (IST = UTC + 5:30).

## Entry

### Notation

$S_t$ is the spot (tick LTP live, bar close in replay), $\Delta$ the strike
step of the underlying's option chain (100 for BANKNIFTY, 50 for NIFTY —
`strike_step_from_chain`, else `FALLBACK_STRIKE_STEPS`), $L$ the run's lots.
The ATM strike is the nearest strike:

$$
K_t = \mathrm{round}\!\left(\frac{S_t}{\Delta}\right)\Delta
$$

The runner computes it (`contract_selector.round_to_step`) and hands it over as
`inp.atm_strike`; the strategy's own fallback `strike_math.round_to_step` is
only reached when that is `None`, which the platform never does. Both use
Python's `round`, so a spot exactly on a midpoint rounds half-to-even
(57,450 → 57,400; 57,550 → 57,600).

### First evaluation (`on_bar`, `previous_trade_strike is None`)

$$
\text{open} \iff K^{\mathrm{last}} = \varnothing \ \land\ \texttt{atm\_ce} \in \mathcal{C}_t \ \land\ \texttt{atm\_pe} \in \mathcal{C}_t
$$

where $\mathcal{C}_t$ is the contracts map the platform resolved for $K_t$ at
the current expiry. The signal is one `OPEN_GROUP` with two legs,
`SELL atm_ce × L` and `SELL atm_pe × L`, group id
`FULCRUM-<timestamp>-001` (`_group_id`: the ISO timestamp with punctuation
stripped, then a three-digit counter of groups the strategy has *built*,
including ones the platform later refuses), reason
`Initial Fulcrum short straddle at ATM <K>`, and
$K^{\mathrm{last}} \leftarrow K_t$. If either contract is missing the method
returns `[]` and changes nothing — it waits for a tick where both exist.

In words: on the first tick, sell one lot each of the ATM call and the ATM put
of the nearest expiry.

### Contract, expiry, size, fill

- **Contract:** the exact `CE`/`PE` rows of the instrument master at
  $(K_t, \text{expiry})$ (`ExactContractCache` live,
  `ContractResolver.contract` in replay); the leg symbols are real broker
  symbols (`NSE:BANKNIFTY26SEP57500CE`), not the logical `UNDERLYING_CE_K`
  form the other Fulcrum variants emit.
- **Expiry:** live, the first listed expiry on or after today's UTC date
  (`valid_expiries[0]`); replay, the earliest expiry on or after the bar's IST
  date (`ContractResolver.expiry_for`). Run 58 (1–4 Sep 2026) traded the
  September monthly, `26SEP` = 2026-09-29, because the master carries no
  BANKNIFTY weekly.
- **Direction:** `resolve_direction` reads `direction` (default `SELL`; `BUY`,
  `LONG` or `B` turn the class into the buy variant). `use_hedges` is resolved
  too (`True` for a seller) but this class never builds a wing leg — the
  constructor comment says so: "It holds no wings either way."
- **Quantity:** `lots` (or legacy `quantity`) from the run parameters via
  `BaseStrategy.lots_from`, default `default_lots` = 1, never below 1;
  `paper_orders.Quantity` stores lots and the API multiplies by the master's
  lot size (BANKNIFTY 30, `instruments.LotSize`).
- **Fill:** live, the runner resolves and subscribes every leg, waits up to
  `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for quotes, then posts
  `/api/Simulator/signals`; the API fills every leg or none (one transaction)
  and refuses an unpriced opening group ("No price is available … rejected
  rather than filled at zero"). Replay: each leg at its option candle's close
  at the signal bar; a leg with no candle skips the whole entry
  (`[SKIP] … no premium history`).

## Position management

### The roll

On every later evaluation (SELL direction):

$$
\text{roll} \iff K_t \ne K^{\mathrm{last}}
$$

A roll is two signals from the same tick, in this order: `CLOSE_GROUP` of the
open group with `closing_legs(current_group_legs)` — the same two symbols,
sides flipped to `BUY`, same lots — reason
`Closing previous group because ATM shifted from <K_last> to <K_t>`, metadata
`previous_atm` / `current_atm`; then `OPEN_GROUP` at $K_t$ with a new group id
(counter + 1), reason `Opening new Fulcrum short straddle at ATM <K_t>`. Then
$K^{\mathrm{last}} \leftarrow K_t$. There is no hysteresis and no minimum hold:
$K_t$ changes whenever the spot crosses a midpoint $K \pm \Delta/2$, so a spot
oscillating around a midpoint rolls on every crossing. Run 58 filled 71
groups on 2026-09-01 alone; 51 of its 181 filled groups lived one bar, the
median three minutes, the longest 67 (group `…070100-093`, 12:31–13:38 on
2026-09-02). Run 23, live, rolled 133 times in ten minutes with the spot
straddling 57,550.

In words: the moment the nearest strike changes, buy the old straddle back and
sell the new one.

The strategy holds one group at a time (`current_group_id`), never adds to it,
never reduces it, never changes lots, and never re-enters after a platform
close until the ATM next changes. The `ce_list` / `pe_list` /
`straddle_list` / `group_entry_*` state keys are written but not read by the
SELL path.

### What the run's risk rules add on top

`parametersJson.risk` carries three levels that `StrategyRiskGuardService`
sweeps every `RiskGuardIntervalSeconds` (3 s, `appsettings.json`) in the order
leg → group → overall, each level stop-loss → trailing stop → target; the
backtest engine mirrors them once per bar, before and after the strategy is
called (`_check_risk`):

- `leg` — points or percent of `AveragePrice` per open position; closes that
  leg only, which leaves the other half of the straddle naked;
- `group` — rupees on one straddle's realized + unrealized P&L; closes both
  legs of that group;
- `overall` — rupees on the run's total P&L; flattens and stops the run
  (replay: per trading day by default, `scope: "run"` for the whole range).

Runs 23 and 58 carried none (`risk: {}`); a seller of naked straddles should
set at least a `group` or `overall` stop, because the strategy has none.

The strategy is **not told** when the platform closes its group (risk rule,
EOD square-off, 15:30, manual stop): its state still names the group, so its
next roll sends a `CLOSE_GROUP` for legs that are no longer open — reduce-only
in the API and the ledger, so they are ignored — and then an `OPEN_GROUP`,
which fills. Run 58 shows the mechanism: the 15:15 square-off closed group
`…094300-071` (signal 711) and the strategy rolled again at 15:29 (skipped,
"entry after the EOD square-off"), then at 09:15 the next morning sent a
close for a group that was never opened (ignored) and opened `…-073`; over the
four days that is the summary's "10 close legs ignored" and the two missing
counters (072, 108). Live, after a `group` rule closes the straddle the run is
flat until the next ATM change and then re-enters at the new strike.

### What the strategy never does

It never goes flat on its own: every `CLOSE_GROUP` it emits is the first half
of a roll. It sets no stop, target or time exit (`StopLossPrice` and
`TargetPrice` are null on all 362 positions of run 58), and without a leg /
group / overall rule, a manual square-off or the 15:30 close a straddle stays
open at whatever the market does.

## Exit

Live, in order of precedence at any moment:

1. **The roll** — the strategy's own `CLOSE_GROUP` on an ATM change, always
   followed by a new open (Position management). It races the sweep below for
   the run's lock (`SimulationRunLocks.AcquireAsync`); whichever takes it
   first closes the legs, the other finds nothing to close.
2. **Risk guard** (`StrategyRiskGuardService.SweepAsync`, every 3 s): the
   position's own `StopLossPrice` / `TargetPrice` (never set here), then leg
   rules, then group rules, then overall rules — the last flattens and stops
   the run.
3. **Manual square-off or the run's stop button** ("Squared off by admin").
4. **Market close:** `MarketHoursService` at 15:30 IST, weekdays, once per
   calendar day.

In the replay (`backtest/engine.py`), per bar: the EOD square-off at
`eod_square_off_ist` (15:15 in run 58, signals 711, 781, 847, 931), a risk
sweep, the strategy's signals, marks, a second risk sweep; the last bar of
the range squares off "End of backtest".

## Parameters

The class declares `default_params = {}` and the factory adds nothing; every
key below is read straight from the run's `parametersJson`.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `lots` (legacy alias `quantity`) | `default_lots` = 1 | lots per leg, both legs; P&L = points × lots × lot size | larger straddles, same rolls | never below 1 (`lots_from`) |
| `direction` | `SELL` | `BUY` / `LONG` / `B` make the class buy the straddle and exit on `target_steps` / `max_hold_minutes` instead of rolling — that is the `FulcrumBuy` registration | n/a — a choice | |
| `use_hedges` | follows `direction` (`True` for `SELL`) | parsed by `resolve_direction`; **no effect** in this class, which builds no wing legs | none | none |
| `strike_step` | unset → the platform's grid | explicit grid override for `resolve_step`; inert for the ATM here because `inp.atm_strike` is always supplied by the platform and wins; it only scales the BUY variant's `target_steps` | none for `Fulcrum` | none for `Fulcrum` |
| `target_steps`, `max_hold_minutes` | 2.0, 45.0 (`_exit_rules.resolve_long_exit`) | buy-side exits; parsed, unused when `direction` is `SELL` | none for `Fulcrum` | none for `Fulcrum` |

There is no threshold parameter: the roll fires on any ATM change, i.e. a
half-step ($\Delta/2$ = 50 points on BANKNIFTY, 25 on NIFTY) from the strike
that was sold.

## Worked example

Run **58** — OfflineReplay, `NSE:NIFTYBANK-INDEX`, resolution `1` (1m),
range 2026-09-01 → 2026-09-04 (four sessions, 1,500 driver bars, 09:15–15:29
IST each day, FYERS backfill), executed 2026-09-07 00:01:58 IST, `ParametersJson`
verbatim: `{"lot_size":30,"lot_size_source":"master","lots":1,"underlying":"BANKNIFTY",
"resolution":"1m","eod_square_off_ist":"15:15","charges_per_lot":0,"risk":{},
"stop_loss":null,"target":null}`. Lot size 30 is `instruments.LotSize` for
`NSE:BANKNIFTY26SEP57500CE`. Strike step 100.

Why not run 23, the only live-paper run: it ran ten minutes on 2026-09-03
(13:10:31–13:20:31 IST) with an older runner and API — status still
`Pending`, `ParametersJson` `{}`, no `StartedUtc`; its signal metadata carries
no `reason` / `spot_price`; 79 of its 133 `CLOSE_GROUP` rows have no fills
because the matching opens were never booked (54 `OPEN_GROUP` rows against
133 closes), and its `RealizedPnl`
reconciles only with a lot size of 15 (position 10: 865.90 − 870.00 = −4.10
points, booked −61.50), not the master's 30. It is cited above for what it
shows about live behaviour, not for numbers.

### Signal 570 — 09:15, first open → orders 817/818 → positions 418/419

`MetadataJson`: `spot_price 57462.05`, `atm_strike 57500`, reason
`Initial Fulcrum short straddle at ATM 57500`, group
`FULCRUM-20260901034500-001`. $57462.05 / 100 = 574.62 \to 575 \to 57500$.
Fills at the 09:15 option candle closes: order 817 SELL 1 lot
`NSE:BANKNIFTY26SEP57500CE` @ 884.35, order 818 SELL 1
`NSE:BANKNIFTY26SEP57500PE` @ 595.30.

### Signal 571/572 — 09:16, the first roll → orders 819–822

One bar later the close was 57446.90: $574.469 \to 574 \to 57400 \ne 57500$.
Signal 571 `CLOSE_GROUP` (`previous_atm 57500`, `current_atm 57400`): order
819 BUY 57500CE @ 880.00, order 820 BUY 57500PE @ 608.40. Position 418 (SHORT
57500CE):

$$
(884.35 - 880.00) \times 1 \times 30 = 4.35 \times 30 = 130.50
$$

Position 419 (SHORT 57500PE): $(595.30 - 608.40) \times 30 = -13.10 \times 30 = -393.00$.
Group −262.50: the spot fell 15 points, the put gained more than the call
lost, and the roll booked it. Signal 572 `OPEN_GROUP`
`FULCRUM-20260901034600-002` at 57400: order 821 SELL 57400CE @ 945.80, order
822 SELL 57400PE @ 567.50 (positions 420/421). At 09:17 the close was
57491.55 → 57500 again: signal 573 closed them at 949.45 / 557.55 for
−109.50 + 298.50 = +189.00, and signal 574 re-sold 57500 (positions 422/423,
+381.00 − 304.50 = +76.50 when it rolled at 09:23). Three rolls in the first
eight minutes.

### Signal 930/931 — 15:14 and 15:15 on 2026-09-04, the last group and the square-off

Signal 930 opened `FULCRUM-20260904094400-183` at 57400 (`spot_price
57437.35`): order 1537 SELL 57400CE @ 857.95, order 1538 SELL 57400PE @
492.75 (positions 778/779). At the 15:15 bar the engine's `_eod_check` ran
before the strategy: signal 931 `CLOSE_GROUP`, reason
`End-of-day square-off 15:15 IST`, `square_off: true`, order 1539 BUY 57400CE
@ 868.95, order 1540 BUY 57400PE @ 492.30.

$$
(857.95 - 868.95) \times 30 = -11.00 \times 30 = -330.00, \qquad
(492.75 - 492.30) \times 30 = 0.45 \times 30 = 13.50
$$

= −316.50 for the last group. The close was 57440.60, ATM still 57400, so the
strategy rolled nothing after the square-off that day.

### The run

181 groups filled (counters 001–183; 072 and 108 were built after the 15:15
square-off and skipped), 362 positions, 724 orders, every fill 1 lot. Sum of
`RealizedPnl` = **−₹1,261.50** (`realizedPnl: -1261.5` in signal 932's
summary), no charges: 2026-09-01 −874.50 over 71 groups, 09-02 −603.00 over
35, 09-03 −475.50 over 33, 09-04 +691.50 over 42. 180 legs won, 181 lost, one
flat; the best group made +787.50 (`…060100-023`, 57700 sold at 11:31 on
2026-09-01 and rolled at 11:39 with the spot down 93 points, 57740.65 →
57647.15: CE +1,857.00, PE −1,069.50), the worst −1,375.50 (`…034500-109`,
57600 sold at 09:15 on 2026-09-03 and rolled at 09:34 with the spot up 97
points, 57555.55 → 57652.65: CE −1,651.50, PE +276.00).

## Limitations

- **No hysteresis.** The roll fires at the midpoint between strikes with no
  band and no minimum hold, so a spot hovering near a midpoint churns: 51
  one-bar groups in run 58, 133 rolls in ten minutes in run 23. Each roll
  crosses the spread twice per leg; the paper fills carry no spread or
  slippage, so the real cost of the churn is not in the numbers above.
- **Every roll realises the losing leg.** In a trend the straddle is rolled
  after each 50-point (BANKNIFTY) move, booking the ITM leg's loss each time;
  the decay it collects has to beat that. Run 58 lost money on three of four
  sessions.
- **No exit of its own.** Naked short straddle, no stop, no target, no time
  stop; the group survives until the platform closes it. A `leg` rule that
  trips on one side leaves the other side open and naked.
- **Not told about platform closes.** After any close it did not make, the
  strategy's next roll sends a close for nothing (ignored) and opens a fresh
  straddle; live, after a `group` stop the run re-enters at the next ATM
  change with no memory of why it was stopped.
- **A missing contract freezes it.** If either ATM contract is absent from the
  master for the current expiry, `on_bar` returns `[]` — it neither rolls nor
  closes the group it holds, and its state does not move, until a tick where
  both resolve.
- **Live, an unpriced open is refused** and the strategy still believes the
  group is open (the runner says so: "The strategy still believes this group
  is open"); the run is then flat until the next ATM change. Run 23 shows the
  pattern: 79 of 133 closes with no matching open.
- **The catalogue text overstates the hedge.** `direction_note` appends "hedged
  by far OTM wings" to this class's description because `use_hedges` is
  `True` for a seller, but `FulcrumStrategy` never builds a wing leg. The
  position is unhedged.
- **Half-to-even rounding** at an exact midpoint (`round`), so 57,450 → 57,400
  but 57,550 → 57,600; harmless, but not the ceiling the older
  `price_resolver.round_to_step` used.
- **Backtest differs from live.** One evaluation per bar close instead of per
  tick (run 58's rolls are at most one a minute; live they can be a second
  apart); fills at the option candle close of the signal bar, no slippage,
  `charges_per_lot` 0; contracts that have expired have no FYERS history and
  are skipped; EOD square-off at 15:15 instead of 15:30; no pre-open ticks.
  Run 58 traded the September monthly because the master lists no BANKNIFTY
  weekly; a live run picks the first expiry on or after today, which on an
  expiry day is the same-day contract.
- **OI is never read**, so nothing here reacts to positioning; and the runner
  hands the strategy no bars, so a run started mid-session has no history and
  simply sells the ATM on its first tick.

## Facts (machine-readable)

```yaml
name: Fulcrum
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
FROM simulation_runs WHERE "Id" = 58;

/* signals cited: 570-575, 711, 781, 847, 930, 931, 932 (BACKTEST_SUMMARY) */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" = 58 ORDER BY "Id";

/* fills cited: 817-828, 1537-1540 (Quantity is lots) */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" = 58 ORDER BY "Id";

/* positions cited: 418-423, 778, 779 */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" = 58 ORDER BY "Id";

/* totals: 362 positions, -1261.50; per day; hold times */
SELECT count(*), sum("RealizedPnl") FROM paper_positions WHERE "SimulationRunId" = 58;
SELECT "OpenedUtc"::date, count(*)/2 AS groups, sum("RealizedPnl")
FROM paper_positions WHERE "SimulationRunId" = 58 GROUP BY 1 ORDER BY 1;
SELECT "GroupId", extract(epoch FROM max("ClosedUtc") - min("OpenedUtc"))/60 AS minutes, sum("RealizedPnl")
FROM paper_positions WHERE "SimulationRunId" = 58 GROUP BY 1 ORDER BY minutes DESC;

/* lot size */
SELECT "Symbol", "LotSize", "ExpiryDate" FROM instruments WHERE "Symbol" = 'NSE:BANKNIFTY26SEP57500CE';

/* the driver bars quoted (spot = 1m close) */
SELECT "TimeStampUtc" AT TIME ZONE 'Asia/Kolkata', "Close", "SourceKey" FROM candles
WHERE "Symbol" = 'NSE:NIFTYBANK-INDEX' AND "Resolution" = '1'
  AND "TimeStampUtc" IN ('2026-09-01 03:45', '2026-09-01 03:46', '2026-09-01 03:47', '2026-09-04 09:44', '2026-09-04 09:45')
ORDER BY 1;

/* run 23 (live, not used for numbers): closes without fills, lot size 15 */
SELECT s."SignalType", count(*),
       count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM paper_orders o WHERE o."SimulationSignalId" = s."Id")) AS without_orders
FROM simulation_signals s WHERE s."SimulationRunId" = 23 GROUP BY 1;
SELECT "Id", "Symbol", "AveragePrice", "RealizedPnl" FROM paper_positions WHERE "SimulationRunId" = 23 ORDER BY "Id" LIMIT 2;
-->
