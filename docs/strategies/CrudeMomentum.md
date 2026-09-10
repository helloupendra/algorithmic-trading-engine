# CrudeMomentum

Source: `strategies/bullish/crude_momentum_strategy.py` (`CrudeMomentumStrategy`),
with the maths in `strategies/indicators.py` (`vwap`, `ema`, `session_date`).
Every rule below is read from the code; where the code and its docstring or
`description` disagree, the code is documented and the difference listed
under Limitations.

## Idea

Intraday momentum on MCX crude. On the 5-minute chart of the near-month crude
future — the run's "spot", because a commodity has no spot on this feed — the
strategy tracks the volume-weighted average price of the current session and a
9-period EMA, and buys the ATM call when a bullish 5-minute candle closes with
its whole body above both lines. The bet is that a candle whose open and close
both clear the session's fair value *and* the short-term trend marks a push the
call can ride; it is long-only and has no exit of its own — the run's risk
rules are expected to close the leg (the module docstring says so in as many
words). Nothing in this repository tests the bet beyond the two runs cited
below.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's `Symbol`: the nearest unexpired crude future (`MCX:CRUDEOIL26SEPFUT` in runs 99 and 104). `UnderlyingCatalog.SpotSymbolFor` returns nothing for a commodity, so `StrategyController` stores `GetNearestFutureSymbolAsync(underlying)` on the run at launch. | 5m | `on_bar` needs `ema_period + 2` = 11 buckets in the list (`len(bars) < self.ema_period + 2`) and `min_session_bars` = 12 buckets of the signal bar's IST date; all of them come from `live_bars` — the FYERS warm-up bars are fed and ignored (see Timeframe). | Live: `live_bars` 1m rows aggregated to 5m on read (`GET /api/LiveData/bars?resolution=5m&take=500`, `LiveDataService.GetRecentBarsAsync`), newest bucket still forming; volume per bucket is the sum of the 1m rows' `VolumeDelta` (the tick's cumulative volume minus the previously stored one, `LiveDataService`). Backtest: `candles` (backfill). |
| ticks | the same future | every tick | none | ingestor → Redis stream `market:ticks`; each tick is one call of `on_bar`, and its LTP picks the ATM strike |
| option quotes | the ATM CE of the nearest option expiry — the only requirement `get_contract_requirements` declares (`ContractRequirement(key="atm_ce", option_type="CE")`; no put is subscribed) | latest quote | none | the runner puts the resolved contract on the ingestor watchlist (`ensure_contracts_tracked`); fills use `live_quotes_latest` |

It never reads option candles or option-chain OI. `inp.spot_price`,
`inp.atm_strike` and `inp.lot_size` are not used by the rule; the runner uses
them to resolve the contract and stamps `spot_price` / `atm_strike` into the
metadata.

## Timeframe

- **Bar:** 5-minute buckets of the future, cut on the UTC clock by
  `GetRecentBarsAsync` (5 divides 30, so they land on IST :00, :05, … as
  well). The list holds at most 500 buckets and only what the ingestor
  recorded for this symbol: on 2026-09-09 `live_bars` for
  `MCX:CRUDEOIL26SEPFUT` begins at 09:24 IST (run 99 started at 09:24:32 and
  the runner watch-listed the future — nothing tracked it before), on
  2026-09-10 at 19:07 (the ingestor was started at 19:07:50; alert 117).
- **Decides once per closed bar, on a tick.** `on_bar` runs on every tick of
  the future but only looks at `bars[-2]`, the last closed bucket
  ("the newest bar is still forming"), and `state["last_evaluated_bar"]`
  makes each bucket's decision happen once. That decision is taken on the
  first tick the runner processes after the next bucket exists in the API
  list — which needs the bar writer to have written that bucket's first 1m
  row — so an entry is stamped a few seconds into the following bucket:
  signal 1422 at 10:50:12.885 IST for the 10:45 bar, 1430 at 11:40:13.475
  for the 11:35 bar, 1480 at 20:15:03.944 for the 20:10 bar (`TimestampUtc`
  is the tick's exchange stamp, or its receipt time when the tick has none).
- **Warm-up is ignored.** The runner fetches 15 calendar days of 5m history
  from FYERS and feeds the last 500 bars with `metadata.source = "warmup"`;
  `on_bar` returns at once on them (line 138, so the day's trade budget is
  not spent on history). The state after warm-up is therefore the fresh one
  from `initialize_state` — `armed` true, no session, zero trades — and the
  EMA is seeded from the first closes the ingestor recorded, not from FYERS
  history.
- **Session:** the IST calendar date of the signal bar (`indicators.session_date`;
  the code's comment: "MCX runs one session a day, 09:00 to 23:30/23:55 IST,
  so an IST calendar date identifies a session exactly" — `MarketSessionService`
  closes MCX at 23:30 IST while New York is on standard time, 23:55 on DST).
  There is no first- or last-entry time of its own (`description`: "no
  time-of-day filter"). A new date resets `trades_this_session` to 0 and
  re-arms. VWAP is anchored to the first bucket of that date **in the list**,
  which is the ingestor's first bar, not 09:00 — see Limitations.
- **15:30 IST:** `MarketHoursService` checks once a minute and, at or after
  15:30 on a weekday, stops every run with flatten
  (`StrategyRunControl.StopAllAsync(MarketClosedReason, flatten: true)` — no
  exchange filter), so a crude run started in the morning is squared off at
  15:30 although MCX trades on; run 100 on 2026-09-09 (another strategy)
  was stopped that way at 15:30:46. The sweep fires once per calendar day per
  API process (an in-memory flag), so a run started after it — run 104 at
  19:25 — is not touched until the next weekday's sweep, or until an API
  process starts while the clock is past 15:30: run 104 was stopped with
  "Market closed (15:30 IST)" at 22:40:15 IST by exactly that (only
  `MarketHoursService` writes this reason, and its loop runs on start-up).
  At the MCX close the same service stops only the **ingestor**
  ("MCX closed", commit b80038d of 2026-09-09); no run is squared off, so a
  position opened in the evening and not closed by a rule is held, marked at
  the last quote, until something else stops the run. The strategy relies on
  the platform for every exit.

## Entry

### Notation

Buckets of the list are $1,\dots,n$, oldest first, with $n$ the forming one;
$O_i, H_i, L_i, C_i, v_i$ are a bucket's open, high, low, close and volume.
The **signal bar** is $s = n-1$. $\mathcal{H} = \{1,\dots,s\}$ is the history
(`history = bars[:len(bars)-1]`) and $\mathcal{S} \subseteq \mathcal{H}$ the
buckets whose IST date equals bar $s$'s (`session_bars`). $p$ =
`ema_period`.

### Lines (`indicators.vwap`, `indicators.ema`)

$$
\mathrm{VWAP}_s = \frac{\sum_{i \in \mathcal{S},\, v_i > 0} \tfrac{H_i + L_i + C_i}{3}\, v_i}{\sum_{i \in \mathcal{S},\, v_i > 0} v_i}
$$

— typical price weighted by volume over the session's buckets; buckets with
no volume are skipped, and the value is `None` (no evaluation) when nothing
in the session traded.

$$
E_p = \frac{1}{p}\sum_{i=1}^{p} C_i,\qquad
E_i = \alpha\, C_i + (1-\alpha)\, E_{i-1}\ \ (p < i \le s),\qquad \alpha = \frac{2}{p+1},
\qquad \mathrm{EMA}_s = E_s
$$

— seeded with the SMA of the **first $p$ closes of the list**, then iterated
over every later close in $\mathcal{H}$, across session boundaries (the list
spans sessions; the EMA does not restart with the session). `None` when
$s < p$.

### Re-arm (line 181)

Before anything else, on every closed bar,

$$
C_s < \mathrm{VWAP}_s \ \lor\ C_s < \mathrm{EMA}_s \implies \text{armed} := \text{true},
$$

and the bar is not an entry. This runs whatever the trade budget or session
count says — it is the only way `armed` returns to true within a session. A
close exactly on a line neither re-arms nor qualifies.

### The rule (lines 185–200)

$$
\text{enter} \iff
\text{armed} \ \land\ |\mathcal{S}| \ge \texttt{min\_session\_bars}
\ \land\ \text{trades}_d < \texttt{max\_trades\_per\_session}
\ \land\ C_s > O_s > \max(\mathrm{VWAP}_s, \mathrm{EMA}_s)
\ \land\ \big(\lnot\texttt{require\_low\_above} \ \lor\ L_s > \max(\mathrm{VWAP}_s, \mathrm{EMA}_s)\big)
$$

The code spells the price condition as `close > open` (bullish) and
`open > vwap and open > ema and close > vwap and close > ema` (body above),
which for a bullish bar is $C_s > O_s > \max(\mathrm{VWAP}_s, \mathrm{EMA}_s)$.
The checks run in the order written: re-arm, session count, budget, armed,
candle shape, contract.

In words: once the session has at least twelve 5-minute bars, buy when a
green candle closes with both its open and its close above the session VWAP
and above the 9 EMA — but only if no entry has been taken since a candle last
closed below one of the lines, and at most three times a day.

### Contract and size

- **Strike:** $K = \mathrm{round}(S/\Delta)\cdot\Delta$ — nearest strike
  (`contract_selector.round_to_step`) — with $S$ the **tick's** LTP of the
  future at evaluation (`spot_price 8928.0` → 8950 in signal 1422; the signal
  bar closed at 8927) and $\Delta$ the smallest strike gap of the chosen
  expiry's chain (`resolve_strike_step` → `strike_step_from_chain`; 50 for
  CRUDEOIL, from the 402 contracts of 2026-09-17 — `FALLBACK_STRIKE_STEPS`
  has no entry for it and would also give the default 50).
- **Expiry:** the first **option** expiry on or after today's UTC date
  (`valid_expiries[0]`; `GetExpiriesAsync` lists CE/PE expiries only —
  2026-09-17, 2026-10-15, 2026-11-17 for CRUDEOIL). The future the lines are
  read from expires later (2026-09-21), so the option traded expires four
  days before its underlying rolls.
- **Contract:** the exact CE row of the instrument master at $(K,
  \text{expiry})$ via `ExactContractCache` → `inp.contracts["atm_ce"]`. If the
  master lacks it the bar is skipped and `armed` stays true (lines 202–207),
  so the next qualifying bar is still taken.
- **Quantity:** `lots` from the run parameters (`BaseStrategy.lots_from`,
  default `default_lots` = 1; runs 99 and 104 used 3) is the leg's
  `quantity`; the API multiplies by the lot size when it fills. For MCX the
  master carries no lot size (`LocalCsvInstrumentImportService` imports the
  COM segment with `masterCarriesLotSize = false`; `Instruments.LotSize` is
  null for `MCX:CRUDEOIL26SEP8950CE`), so `LotSizeResolver` falls back to
  `appsettings.json` → `LotSizes.CRUDEOIL` = 100 (`CRUDEOILM` = 10), source
  "configured". The started-alert of run 99 reads "3 lot × 100 = 300".
- **Fill:** `price` is `None`; the runner waits up to
  `SIGNAL_PRICE_WAIT_SECONDS` (10 s) for the option's live quote
  (`enrich_signal_leg_prices`), stamps `reason`, `spot_price`, `atm_strike`
  into the metadata next to the strategy's own `group_id` (a fresh UUID),
  `direction`, `signal_bar_utc`, `vwap`, `ema`, `session_bars`,
  `underlying_price`, and posts `/api/Simulator/signals`; the API fills a
  `MARKET_SIM` order at that quote, no slippage. An unpriced group is refused
  by the API ("No price is available … rejected rather than filled at zero").
  The state is updated inside `on_bar` before the runner posts (lines
  241–244: `armed = False`, `trades_this_session += 1`), so a refused or
  contract-less-after-the-fact signal still spends one of the day's entries
  and disarms until a close back below a line.

## Position management

The strategy does nothing after entry. It keeps no record of positions:
`state` holds `armed`, `session_date`, `trades_this_session`,
`last_evaluated_bar`, `last_entry_bar` and `last_group_id` (the last two are
written and never read), so

- it does not know when the risk guard closed a leg — re-entry is gated on
  the setup resetting (a close below a line), "not on being flat" (comment at
  line 103);
- groups stack: a second qualifying bar after a re-arm opens a second group
  while the first is open (run 99 held two `8950CE` groups from 11:40:13 to
  11:40:44), and there is no opposite signal to close anything;
- it never rolls the strike, never adds to or reduces a position, never sets
  a per-position stop or target (`StopLossPrice` / `TargetPrice` are null on
  positions 1039, 1052 and 1119).

**What the run's risk rules add.** `parametersJson.risk` carries three levels
that `StrategyRiskGuardService` sweeps every `RiskGuardIntervalSeconds`
(3 s, `appsettings.json`), in the order leg → group → overall, each level in
the fixed order stop-loss → trailing stop → target:

- `leg` — per open position, points or percent of `AveragePrice` against
  `LastMarkPrice`; a trip closes that leg only. The `description` asks the
  trader to set a leg stop-loss, target and trail. Neither cited run did.
- `group` — rupees on a signal group's realized + unrealized P&L; closes
  every open leg of that group.
- `overall` — rupees on the run's total realized + unrealized P&L
  (`GetPortfolioSummaryAsync`); a trip flattens everything and ends the run.
  Run 99 carried `{"overall":{"target":5000,"scope":"day"}}` and ended on it.
  The `scope` is honoured by the backtest engine only; the live guard measures
  from the run's start, which agrees with `day` for a one-session run.

Run 104 carried `"risk":{}` — no rule at all — and its position was closed
only by the market-close sweep.

The guard trips on the mark it sees; the run is then marked Stopping, the
runner process is stopped, and only then are the positions flattened at the
quote read at that moment (`StrategyRunControl.FinishStopAsync`,
`PaperTradingService.CloseOpenPositionsAsync`), so the booked P&L can differ
from the reason text (run 99: "P&L 5,370" tripped, ₹3,270 booked — the
arithmetic is in the worked example).

**What the strategy never does:** it has no built-in exit. Without a leg /
group / overall rule, a manual stop or the 15:30 sweep, a position stays
open — through the MCX close and overnight if the run was started after
15:30 (see Timeframe).

## Exit

Live, `StrategyRiskGuardService.SweepAsync` checks one position's closers in
this order on every sweep:

1. The position's own `StopLossPrice` / `TargetPrice` — never set by this
   strategy.
2. Leg rules: stop-loss points, stop-loss percent, trailing stop points,
   trailing stop percent, target points, target percent (`EvaluateLeg`).
3. Group rules on the group's P&L (`EvaluateGroup`).
4. Overall rules on the run's total P&L (`EvaluateOverall`) — flattens and
   stops the run (run 99, signals 1431/1432 "Target hit: P&L 5,370 ≥ 5,000",
   1433 `RUN_STOPPED` by `risk-guard`).

Two more closers run on their own clocks outside the sweep, with no
precedence relative to it — whichever takes the run's lock first closes the
position:

- a manual square-off or the run's stop button;
- `MarketHoursService` at 15:30 IST on weekdays — or on the first loop of an
  API process started after 15:30 (run 104, signal 1481 "Market closed
  (15:30 IST)" at 22:40:15 IST, 1482 `RUN_STOPPED` by `market-hours`).

An API restart that finds the runner process dead closes the run as an
orphan at the last mark (`StrategyRunControl.ReconcileOrphanedRunsAsync`);
one that finds it alive adopts the run and leaves the positions open.

The backtest mirrors the leg / group / overall rules (`backtest/engine.py`)
with the day's square-off at `eod_square_off_ist`; no replay of this strategy
exists (see Limitations).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `ema_period` | 9 | $p$: EMA length in 5m bars, seeded with the SMA of the first $p$ closes of the list; clamped to ≥ 2 | Slower line, further below price in a rise, so a body above it is a stronger push and the re-arm (close below it) comes later — fewer entries; the list needs $p+2$ buckets before evaluating | Line hugs price; more bars clear it and more close back under it — more entries and more re-arms |
| `min_session_bars` | 12 | Buckets of the signal bar's IST date that must be in the list before an entry — "one hour of the session", so the VWAP is a session's VWAP; clamped to ≥ 1 | Later first entry, VWAP built on more bars | Earlier first entry off a VWAP anchored to a handful of bars (the comment calls that "guessing") |
| `max_trades_per_session` | 3 | Entries per IST date; the counter resets when the date changes; clamped to ≥ 1 | More stacked entries on a trending day | 1 = one entry a day |
| `require_low_above` | `false` | `true` demands $L_s > \max(\mathrm{VWAP}_s, \mathrm{EMA}_s)$ as well — the whole candle, not just the body, above both lines | n/a — a switch. On, none of the three entries below would have fired (lows 8920, 8923, 9482 against lines 8924.16, 8923.45, 9487.10) | |
| `lots` (run parameter) | `default_lots` = 1 | lots per entry; P&L = points × lots × lot size (100 for CRUDEOIL, 10 for CRUDEOILM, both from `appsettings.json`) | Larger positions, same signals | |

## Worked example

Run **99** — LivePaper, `MCX:CRUDEOIL26SEPFUT`, CRUDEOIL, started 2026-09-09
09:24:32 IST by admin, `ParametersJson` verbatim:
`{"ema_period":9,"min_session_bars":12,"max_trades_per_session":3,"require_low_above":false,"lots":3,"underlying":"CRUDEOIL","risk":{"overall":{"target":5000,"scope":"day"}},"stop_loss":null,"target":5000}`.
Times below are IST (the database stores UTC; IST = UTC + 5:30). Every fill,
position and bar value is a database row (the SQL is at the end of this
file); the VWAP and EMA are the values the strategy wrote into
`MetadataJson`, and each one was reproduced to the paisa by running
`indicators.vwap` / `indicators.ema` over the 5m buckets of `live_bars`. The
list the strategy saw that morning starts at the 09:20 bucket (one 1m row,
09:24), so "session bars" counts from 09:20, not 09:00.

### Signal 1422 — 10:50:12, the 10:45 bar → order 2055 → position 1039

`MetadataJson`: `signal_bar_utc 2026-09-09T05:15:00Z` (10:45 IST), `vwap
8922.98`, `ema 8924.16`, `session_bars 18`, `underlying_price 8927.0`,
`spot_price 8928.0`, `atm_strike 8950`; reason "5m body above VWAP and 9 EMA:
O 8925.00 / C 8927.00 vs VWAP 8922.98, EMA 8924.16 (18 session bars). Buying
CE 8950.0."

The 10:45 bucket: O 8925, H 8930, L 8920, C 8927, volume 81. The session's
18 buckets (09:20 … 10:45) carried 2,401 contracts of volume and
$\sum \tfrac{H+L+C}{3} v = 21{,}424{,}078.67$, so
$\mathrm{VWAP} = 21{,}424{,}078.67 / 2{,}401 = 8922.98$. The EMA, seeded on the
first nine closes of the list (SMA 8923.44) and iterated over the other nine,
is 8924.16. $C = 8927 > O = 8925 > 8924.16$: body above both lines, session
count 18 ≥ 12, no trade yet, armed. The seventeen earlier buckets had all
failed — 10:40 (O 8915, C 8925) closed above both lines but opened below
them; the 10:30 bar (O 8921, C 8928) likewise — and 10:00 … 10:10 were also
below `min_session_bars`. The low, 8920, is under the EMA: with
`require_low_above` this bar would not have fired.

Fill: `MCX:CRUDEOIL26SEP8950CE` (expiry 2026-09-17), BUY 3 lots @ 251.60
(10:50:13). `armed` → false, `trades_this_session` → 1.

### Between the two entries

The 10:50 bucket closed at 8923, under the EMA of 8923.93, which re-armed
the strategy on the first tick after 10:55 (a re-arm writes no signal). None of 10:55 …
11:30 had its body above both lines — 11:15 to 11:25 closed under the VWAP,
11:30 (O 8919, C 8923) opened under it.

### Signal 1430 — 11:40:13, the 11:35 bar → order 2073 → position 1052

`vwap 8923.45`, `ema 8923.20`, `session_bars 28`, `underlying_price 8936.0`,
`spot_price 8936.0`, `atm_strike 8950`. The 11:35 bucket: O 8924, H 8938,
L 8923, C 8936, volume 300 (session volume so far 3,442; VWAP =
30,714,525.67 / 3,442 = 8923.45). $8936 > 8924 > 8923.45$. Fill:
`MCX:CRUDEOIL26SEP8950CE`, BUY 3 @ 256.30 (11:40:13) — the same strike as
the first group (the future had moved 8 points), so two groups of the same
contract were open. `trades_this_session` → 2.

### Signals 1431, 1432, 1433 — 11:40:44, the overall target

Thirty-one seconds later the risk guard read the run at "P&L 5,370 ≥ 5,000".
That number is the two open legs marked at one price $M$:
$300\,(M - 251.60) + 300\,(M - 256.30) = 5{,}370 \Rightarrow M = 262.90$.
The runner was stopped first and the flatten then filled both closes at
the quote of that moment, 259.40 (orders 2074, 2075, 11:40:44):

$$
(259.40 - 251.60) \times 3 \times 100 = 7.80 \times 300 = 2{,}340.00
$$

= ₹2,340.00, the `RealizedPnl` of position 1039, and

$$
(259.40 - 256.30) \times 300 = 3.10 \times 300 = 930.00
$$

= ₹930.00 (position 1052). Run total ₹3,270.00 (sum of `RealizedPnl`; the
stopped-alert 83 shows the same), against the ₹5,370 the guard tripped on:
the 3.50 points the option gave back while the runner was being stopped cost
₹2,100 of the reason text. No charges are modelled. Signal 1433 is the
`RUN_STOPPED` row (`by: risk-guard`).

### Run 104 — the same rule on the evening session, no risk rule

Run **104** — LivePaper, the same future, started 2026-09-10 19:25:23 IST,
`lots 3`, `"risk":{}`. `live_bars` for the future that day begins at 19:07
(the ingestor was started at 19:07:50), so the "session" VWAP is anchored to
the 19:05 bucket, ten hours into the MCX session, and the EMA at the first
evening bar was 9092.72 against a price of 9483 because the list still held
the 62 buckets of the previous day (last close 9007 at 14:50); it was still
50 points under the price at the signal bar.

Signal **1480** — 20:15:03, the 20:10 bar → order 2216 → position 1119.
`vwap 9487.1`, `ema 9462.91`, `session_bars 14`, `underlying_price 9513.0`,
`spot_price 9512.0`, `atm_strike 9500`. The 20:10 bucket: O 9489, H 9523,
L 9482, C 9513, volume 635; $9513 > 9489 > 9487.10$. (19:35 and 19:40 had
bodies above both lines too, but the session then held only 7 and 8 buckets,
under `min_session_bars`.) Fill: `MCX:CRUDEOIL26SEP9500CE`, BUY 3 @ 341.30
(20:15:04).

No rule was set, so nothing closed the leg while the future ran to 9730;
the strategy re-armed at 22:10 (C 9680 < EMA 9692.41), and no bar from
22:15 to 22:40 qualified; the next one that did, 22:45, closed after the
run had been stopped. Signal 1481, "Market closed (15:30 IST)" at 22:40:15 — the
market-close sweep of a freshly started API process (Timeframe) — sold 3 @
473.50 (order 2217):

$$
(473.50 - 341.30) \times 300 = 132.20 \times 300 = 39{,}660.00
$$

= ₹39,660.00 (position 1119). Had the API not restarted, the position
would have been held through the MCX close.

## Limitations

- **No exit, no position awareness.** Groups stack (run 99 held two at
  once); a leg is closed only by a run rule, a stop, or the 15:30 sweep. Run
  104 shows the other side: with `"risk":{}` a 132-point winner was kept open
  for two and a half hours by nothing but luck, and would have been carried
  overnight.
- **"Session VWAP" is anchored to the ingestor's first bar, not the MCX
  open.** The `description` says "VWAP is anchored to the MCX session open";
  the code anchors to the first bucket of that IST date in the 500-bucket
  list, which holds only what `live_bars` has for the symbol. Nothing tracks
  the crude future before a run starts it (run 99: from 09:24; run 104: from
  19:07, because the ingestor itself was started then), and the FYERS warm-up
  bars are discarded. Both worked examples therefore trade off a partial
  session's VWAP; the `min_session_bars` guard counts recorded buckets, not
  minutes since 09:00.
- **The EMA crosses sessions and starts cold.** It is seeded on the first
  nine closes the ingestor recorded and iterated across days and gaps (a
  500-point overnight gap on 2026-09-10 left it 390 points under the price
  for the first evening bars). A run that starts the ingestor late has an
  EMA that says nothing for the first $p$ buckets and little for a while
  after.
- **The 15:30 sweep does not know MCX.** `StopAllAsync` stops every run at
  15:30 IST; a crude run started in the morning is squared off with eight
  hours of session left. A run started after 15:30 escapes the day's sweep but
  is stopped by the first loop of any API process started later that evening
  (run 104 at 22:40:15), and there is no square-off at the MCX close at all —
  only the ingestor is stopped then, which leaves the run "waiting for ticks"
  with its positions marked at the last quote.
- **State changes before the API answers.** `armed` and the trade count are
  updated inside `on_bar`; a signal the API refuses (no quote within 10 s) or
  a bar whose contract is missing at resolve time still costs an entry.
- **Marks versus fills.** The guard trips on the mark it reads and the close
  fills after the runner has been stopped: run 99 tripped at ₹5,370 and
  booked ₹3,270. Illiquid crude options can move several points in those
  seconds.
- **Timestamps are the tick's, bars are the writer's.** The decision on a
  closed bucket waits for the first 1m row of the next bucket to be written
  and for the runner's next tick; when the tick pipeline lags (as it did on
  2026-09-09 afternoon — `live_bars` for the future stops at 14:50 although
  the ingestor ran until 15:30:08, alert 101) the decision is late by the
  same amount and the fill is at whatever the option quotes then.
- **Volume is `VolumeDelta`.** The live VWAP weights by the difference between
  consecutive cumulative-volume ticks stored by `LiveDataService`; the first
  tick after a (re)start contributes 0, and a bucket with no positive delta is
  skipped. A backtest would weight by FYERS's per-candle volume instead — a
  different series.
- **The strike follows the tick, the rule follows the bar.** ATM is rounded
  from the LTP at the moment of evaluation, not from the signal bar's close;
  both gave 8950 / 9500 here, but a fast market between the close and the
  tick can move it a strike.
- **Expiry-day contracts.** The nearest option expiry on or after today is
  used, so on 2026-09-17 the same-day contract is traded; the future the
  lines are read from expires on the 21st.
- **Wrong underlying is only a warning.** `supported_underlyings` disables
  other underlyings in the launch dialog, but the API only logs a warning:
  run 103 started this strategy on `NSE:NIFTYBANK-INDEX` (no `underlying`
  parameter) at 18:25 IST, after the NSE close; the runner exited (code 0)
  ten minutes later with no signal. An index has no volume (every
  `live_bars` row of `NSE:NIFTYBANK-INDEX` has `VolumeDelta` 0), so
  `indicators.vwap` returns `None` and the rule can never evaluate there.
- **Lot size is configuration, not the master.** For MCX the lot size comes
  from `appsettings.json` (`LotSizes.CRUDEOIL` = 100), whose comment says it
  "MUST match the exchange's contract specs. Verify before trading an
  instrument that is not already in use." A wrong value there scales every
  rupee figure silently.
- **No backtest of this strategy exists.** `candles` holds 5m bars of
  `MCX:CRUDEOIL26SEPFUT` from 2026-08-20, but no `OfflineReplay` run of
  `CrudeMomentum` has been made; and the replay engine's driver keeps only
  09:15–15:30 IST bars (`backtest/feed.py`, `in_session`), so it could not
  evaluate the evening session that run 104 traded. Fills there would be at
  bar close, no slippage, and expired option contracts have no broker
  history.

## Facts (machine-readable)

```yaml
name: CrudeMomentum
category: Bullish
evaluates_on: bar
resolution: 5m
data: index candles, ticks
instruments: CRUDEOIL, CRUDEOILM
default_lots: 1
built_in_exit: false
added: 2026-09-11
spec_version: 1
```

<!--
Verification SQL for the worked example. Run with
  docker exec algotrading_db psql -U postgres -d algotrading -Atc "<sql>"
Times are converted to IST in the queries.

/* the runs (99: 2026-09-09; 104: 2026-09-10; 103: the BANKNIFTY mis-launch) */
SELECT "Id", "Mode", "Symbol", "StrategyName", "Status",
       "StartedUtc" AT TIME ZONE 'Asia/Kolkata' AS started_ist,
       "CompletedUtc" AT TIME ZONE 'Asia/Kolkata' AS completed_ist, "ParametersJson"
FROM simulation_runs WHERE "Id" IN (99, 103, 104);

/* signals: run 99 = 1422, 1430, 1431, 1432, 1433; run 104 = 1480, 1481, 1482 */
SELECT "Id", "SignalType", "TimestampUtc" AT TIME ZONE 'Asia/Kolkata' AS ist, "GroupId", "MetadataJson"
FROM simulation_signals WHERE "SimulationRunId" IN (99, 104) ORDER BY "Id";

/* fills: 2055, 2073, 2074, 2075 (run 99); 2216, 2217 (run 104) — Quantity is lots */
SELECT "Id", "SimulationSignalId", "GroupId", "Symbol", "Side", "Quantity", "FillPrice",
       "FilledUtc" AT TIME ZONE 'Asia/Kolkata' AS filled_ist
FROM paper_orders WHERE "SimulationRunId" IN (99, 104) ORDER BY "Id";

/* positions: 1039, 1052 (run 99); 1119 (run 104) */
SELECT "Id", "GroupId", "Symbol", "Direction", "AveragePrice", "LastMarkPrice", "RealizedPnl", "Status",
       "OpenedUtc" AT TIME ZONE 'Asia/Kolkata' AS opened_ist, "ClosedUtc" AT TIME ZONE 'Asia/Kolkata' AS closed_ist,
       "StopLossPrice", "TargetPrice"
FROM paper_positions WHERE "SimulationRunId" IN (99, 104) ORDER BY "Id";

/* lot size: the master row carries none for MCX; 100 is appsettings.json LotSizes.CRUDEOIL */
SELECT "Symbol", "LotSize", "ExpiryDate", "StrikePrice" FROM instruments
WHERE "Symbol" IN ('MCX:CRUDEOIL26SEPFUT', 'MCX:CRUDEOIL26SEP8950CE', 'MCX:CRUDEOIL26SEP9500CE');

/* option expiries and the strike grid the runner read */
SELECT "ExpiryDate", count(*) FROM instruments
WHERE "Underlying" = 'CRUDEOIL' AND "OptionType" IN ('CE', 'PE') GROUP BY 1 ORDER BY 1;

/* the 5m buckets the strategy saw (live_bars 1m rows bucketed on the UTC clock, as
   LiveDataService.GetRecentBarsAsync does; volume = sum of VolumeDelta).
   Feed the output to indicators.vwap over the IST date and indicators.ema(closes, 9)
   over everything up to the signal bar: 8922.98 / 8924.16 (05:15Z), 8923.45 / 8923.20
   (06:05Z), 9487.10 / 9462.91 (2026-09-10 14:40Z). */
SELECT to_char(b AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM-DD HH24:MI') AS bar_ist, o, h, l, c, v, n
FROM (
  SELECT date_trunc('hour', "BarStartUtc") + (floor(extract(minute FROM "BarStartUtc") / 5) * 5) * interval '1 min' AS b,
         (array_agg("Open"  ORDER BY "BarStartUtc"))[1]      AS o,
         max("High")                                          AS h,
         min("Low")                                           AS l,
         (array_agg("Close" ORDER BY "BarStartUtc" DESC))[1] AS c,
         sum("VolumeDelta")                                   AS v,
         count(*)                                             AS n
  FROM live_bars
  WHERE "Symbol" = 'MCX:CRUDEOIL26SEPFUT' AND "Resolution" = '1m'
  GROUP BY 1
) t ORDER BY b;

/* the Telegram copies the API wrote for these runs (67, 75, 81, 82, 83; 120-123) */
SELECT "Id", "OccurredUtc" AT TIME ZONE 'Asia/Kolkata', "Title", "Message"
FROM alert_events WHERE "SimulationRunId" IN (99, 104) ORDER BY "Id";
-->
