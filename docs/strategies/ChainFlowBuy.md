# ChainFlowBuy

Source: `strategies/directional/chain_flow_buy.py` (`ChainFlowBuyStrategy`).
Every rule below is read from the code; the chain it reads is served by
`GET /api/OptionChain/view` (`OptionChainService.GetViewAsync`), the live
plumbing is `strategies/execution_runner.py` and the backtest's is
`backtest/engine.py`. Where the code and its docstring disagree, the code is
documented and the difference is listed under Limitations. Times are IST
(UTC + 5:30).

## Idea

Buy an index option only when price and the option chain agree on the
direction.

A 5-minute trend candle says which way the index is moving. The chain then
has to say that money is backing that move:
- near the money, option writers are adding puts and not calls (for a call
  buy), or the mirror for a put;
- the at-the-money option of that side is trading well above its recent
  volume;
- the premium of the option being bought is being bid up: long build-up or
  short covering;
- implied volatility has not already spiked.

A buyer who is right on direction still loses when the premium was inflated
by a volatility spike that then unwinds, or when the move was not followed by
positioning. Those two are the losses the chain confirmations are meant to
remove.

This is a hypothesis to test in paper, not a proven edge. It was written on
2026-09-15, the first day the platform recorded Dhan's full option chain (OI,
IV and greeks for every strike, once a minute) for NIFTY, BANKNIFTY and SENSEX.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (e.g. `NSE:NIFTY50-INDEX`) | 5m | `ema_slow` + 2 = 23 bars in the runner's list. Live the newest is still forming, so 22 closed bars: 21 for the slow EMA's seed and the signal bar | ingestor (`live_bars`); `candles` in a backtest |
| option candles | the ATM CE and ATM PE of the run's expiry (`atm_ce`, `atm_pe`), resolved at each tick's ATM strike | 5m | `volume_lookback` + 2 = 8 bars: the signal bar, the 6 before it, and the newest | ingestor (`live_bars`); stored option candles in a backtest |
| option chain OI | the underlying's chain: every listed strike's OI, IV, delta, build-up, LTP and symbol, and the header's capture time, spot, ATM strike, ATM IV and expiry | per-minute capture; live, with live quotes laid over the near strikes | a sample taken at least `flow_lookback_minutes` (15) earlier in the same session | chain poller (`option_chain_snapshots`, Dhan chain recorder), read through `GET /api/OptionChain/view`. A backtest or a recap asks for it with `asOfUtc` (see Timeframe) |

The strategy fetches the chain itself; the runner is not changed, so no
other strategy is affected.
- **Live:** at most once every `chain_refresh_seconds` (20). The view is the
  newest capture with fresh live quotes laid over it, and its spot is the
  index's live quote while that is fresh.
- **Backtest or recap:** once per decision, for that decision's moment. The
  view is the newest capture at or before that moment, with no live overlay.
  For an index, a moment before the platform's first capture of its chain
  gets a chain the API rebuilds from `option_history_bars`; a later moment
  with no capture near it gets the last one before it, which the age check
  refuses.
- A fetch that fails is printed once (`option chain fetch failed: …`) and
  leaves the last chain fetched in place; the age check below then decides
  whether that chain may still be used.

## Timeframe

- **Evaluates on:** the 5-minute bar, once per bar. The signal bar is always
  the second-newest bar in the list (`bars[-2]`), because live the newest is
  still forming: the decision comes on the first tick after the signal bar
  closes. In a recap the runner's bars stop at the replayed tick, so the same
  holds. In a backtest the newest bar has already closed, so the strategy
  decides one bar later than a live run would (see Limitations).
- Warm-up bars (`source: warmup`) are never evaluated. An input whose
  metadata says `chain: none` makes it sit out; no runner sets that today.
- **Entries:** between `entry_start` (09:30) and `entry_end` (14:45) IST,
  both included. The time checked is the close of the signal bar.
  - On the chain's expiry day the last entry is `expiry_day_entry_end` (13:30),
    because late on expiry day theta takes most of a bought premium.
  - The OI flow needs a sample from at least `flow_lookback_minutes` earlier
    in the same session, and samples start with the session's first evaluated
    candle. A run up before the open can first enter on the 09:30–09:35
    candle, decided at 09:35; a run started later, three candles after the
    first one it evaluates.
- **The decision's clock**, used by the chain's age check: live, the wall
  clock when the decision is made; in a backtest, the signal bar's close; in
  a recap, the replayed tick's exchange time. The chain is fetched as of that
  same moment in a backtest and a recap.
- **At 15:30:** the platform squares off every open position
  (`MarketHoursService`). The strategy has no exit of its own and relies on
  that and on the run's risk rules. A backtest squares off at its
  `eod_square_off_ist` (15:15 by default); a recap takes no entries or exits
  after the replayed 15:30.

## Entry

Let the signal bar close at $C_t$ with open $O_t$, $E^{9}_t$ and $E^{21}_t$ be
the 9- and 21-period EMAs of 5-minute closes (every closed bar the runner
holds, across sessions, seeded with the mean of the first 21), and $O_s$ the
open of the session's first bar.

**Trend candle.**

$$
\text{up} \iff C_t > O_t \land C_t > E^{9}_t > E^{21}_t \land C_t > O_s
\qquad
\text{down} \iff C_t < O_t \land C_t < E^{9}_t < E^{21}_t \land C_t < O_s
$$

**OI flow.** On every evaluated candle whose chain passes the data checks
below, the strategy keeps a sample of call and put OI for the ATM strike and
`flow_strikes` + 2 (5) listed strikes either side; ATM is the chain's ATM
strike, or the index close if the chain has none. Take $S$, the ATM strike
and `flow_strikes` (3) listed strikes either side, counting only strikes
whose call and put OI are both known now and in the earlier sample. The
earlier sample is the newest one taken at least $L$ = `flow_lookback_minutes`
before this decision. $\Delta P$ and $\Delta C$ are the put and call OI added
over $S$ since then, and $P_k$, $C_k$ the OI now:

$$
F = 100 \cdot \frac{\Delta P - \Delta C}{\sum_{k \in S} (C_k + P_k)}
$$

The earlier sample is normally exactly $L$ old. After candles that were
blocked by the data checks it is older: blocked candles add no sample, and
the strategy keeps the last eight (`max(3, 2L/5 + 2)` with $L$ in minutes).
Until a sample is old enough the DATA line reads "building history" and
nothing is bought.

**Volume surge.** $V_t$ is the 5-minute volume of the ATM option of that side
(`atm_ce` for a call, `atm_pe` for a put) on its second-newest bar; $\bar V$
is the mean of the `volume_lookback` (6) bars before that, and must be above
zero. This is the at-the-money contract at the tick's strike, not
necessarily the contract bought, which is chosen by delta.

**IV ceiling.** $\sigma_t$ is the chain's ATM IV: the mean of the ATM call's
and put's IV, or either one when the other has none. $\sigma_{\min}$ is the
lowest ATM IV the run has read this session, $\sigma_t$ included, on candles
whose chain passed the data checks.

A **CE** is bought when every one of these holds:

$$
\text{up} \land F \ge f \land V_t \ge m \cdot \bar V \land \sigma_t \le \sigma_{\min}(1 + r/100) \land \text{build-up} \in \{\text{long build-up}, \text{short covering}\}
$$

- $f$ is `flow_min_pct` (1.0).
- $m$ is `volume_multiple` (1.5).
- $r$ is `iv_max_rise_pct` (20).
- The volume is the ATM call's; the build-up is the chosen contract's (below),
  and is required only while `require_buildup` is true.

A **PE** is bought on the mirror image: down trend, $F \le -f$, the ATM
put's volume and the chosen put's build-up.

In words: buy a call when the index closes a clean up-trend candle, writers
added more puts than calls near the money over the last 15 minutes or more,
the ATM call traded at least 1.5 times its recent volume, the call chosen to
buy reads long build-up or short covering, and IV is within 20% of the day's
low.

**The order of the checks.** Cheapest first: the entry window, the day's
entry count, the disarmed side, the OI flow, the IV ceiling, the volume
surge, the contract, the build-up. Only the IV ceiling and the build-up
print a line when they refuse (`… skipped: …`); the others refuse silently,
and the DATA line carries their inputs.

**The contract.** Among the calls (or puts) within `delta_search_strikes`
(3) listed strikes of ATM that have a symbol, a delta and an LTP of at least
`min_premium` (5), the one whose $|\Delta|$ is closest to `target_delta`
(0.5); on a tie, the lower strike. It is the chain's contract, so its expiry
is the chain's. One leg, bought at market, of `lots` lots, as an
`OPEN_GROUP` signal whose metadata carries every number above.

**Blocked when the data is wrong.** Every evaluated candle prints a `DATA`
line, whether or not the chain passes:
- the index close;
- the chain's age;
- its spot price and the gap to the index close;
- ATM IV and the session low;
- live legs;
- the OI flow;
- the trend.

No sample is taken and no entry is made when, in this order:
- there is no chain;
- the chain carries no capture time;
- its capture is more than `max_chain_age_seconds` (180) from the decision's
  clock, either way — older, or captured after it, which in a replay would
  be information the candle could not have had;
- there is no spot price, or the spot is more than `max_spot_gap_pct`
  (0.35%) from the index close. Live, the view's spot is the index's live
  quote, so this mostly checks that the quote and the candle agree; in a
  backtest or recap it is the capture's own spot;
- it has no ATM IV.

The line says which check failed.

## Position management

After an entry, that side is disarmed. It is re-armed when the close is no
longer above both EMAs stacked upwards (for a CE) or below both stacked
downwards (for a PE): only the EMA part of the trend candle. A red candle,
or a close back under the session open, does not re-arm it. One side is
disarmed at a time, so the other stays armed. At most
`max_trades_per_session` (3) entries a day; the count and the disarmed side
reset with the session.

The strategy never looks at its own positions. A re-armed side buys again
while the earlier leg is still open, and a PE can be bought while a CE is
open: up to three legs a day, all open together if no rule closes them.

- **What the run's risk rules add on top.** The strategy relies on them
  entirely for managing a bought premium. Start it with a leg target and a
  leg stop-loss, for example a target of 30% and a stop-loss of 20% of the
  premium, or points that suit the underlying. A group or overall rupee
  limit is optional. The morning plan (`config/morning-plan.txt`) starts it
  on BANKNIFTY, NIFTY and SENSEX with 2 lots and the morning job's default
  leg rule: a 20-point target and, unless `MARKET_OPEN_LEG_STOP_PTS` is set
  on the server, no leg stop-loss.
- **What the strategy itself never does.** It never exits, adjusts, rolls or
  averages. Without a leg rule, a position stays open until the stop button
  or the 15:30 square-off.

## Exit

In order of precedence:
1. Leg / group / overall risk rules from `parametersJson.risk`, swept by the
   API every 3 s (mirrored by the backtest engine).
2. The run's stop button.
3. The 15:30 market close square-off (`eod_square_off_ist`, 15:15 by
   default, in a backtest).

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `ema_fast` | 9 | Fast EMA of 5m closes | Slower, fewer trend candles | Faster, more (and noisier) candles |
| `ema_slow` | 21 | Slow EMA of 5m closes; also sets the bars needed before the first decision (`ema_slow` + 2) | Longer trends only | Earlier, weaker trends |
| `flow_strikes` | 3 | Listed strikes either side of ATM in the OI flow (samples keep two more each side) | Wider, slower-moving read | Only the strikes at the money |
| `flow_lookback_minutes` | 15 | The OI flow is measured from the newest sample at least this old; also sets how many samples are kept | Steadier flow, later first entry | Faster, noisier flow |
| `flow_min_pct` | 1.0 | Net put-minus-call OI added, % of the window's OI now | Stronger positioning required, fewer trades | More trades on weaker flow |
| `volume_multiple` | 1.5 | The ATM option's signal-bar volume vs its recent mean | Only strong surges | Ordinary volume passes |
| `volume_lookback` | 6 | 5m bars before the signal bar in the volume mean | Longer baseline | Shorter baseline |
| `require_buildup` | true | Demand long build-up or short covering on the contract chosen | — | `false` ignores build-up |
| `iv_max_rise_pct` | 20 | ATM IV ceiling above the session's low, in % | Buys into higher IV | Refuses sooner after an IV rise |
| `target_delta` | 0.5 | Delta the chosen strike should be closest to | Deeper in the money | Further out of the money, cheaper |
| `delta_search_strikes` | 3 | Listed strikes either side of ATM searched for that delta | Wider search | Near the money only |
| `min_premium` | 5 | Lowest LTP a leg may have | Excludes cheap far strikes | Allows them |
| `entry_start` | 09:30 | First entry (IST, signal bar close), included | Skips more of the open | Trades the opening noise |
| `entry_end` | 14:45 | Last entry (IST, signal bar close), included | Later entries, less time to work | Earlier stop |
| `expiry_day_entry_end` | 13:30 | Last entry on the chain's expiry day | More expiry-day theta risk | Less |
| `max_trades_per_session` | 3 | Entries signalled per day | More trades | Fewer |
| `max_chain_age_seconds` | 180 | How far a chain's capture may be from the decision's clock, either side | Tolerates a lagging recorder | Blocks sooner |
| `max_spot_gap_pct` | 0.35 | Largest chain-spot vs index-close gap | Tolerates a mismatch | Blocks sooner |
| `chain_refresh_seconds` | 20 | Live only: seconds a fetched chain is reused. A backtest or recap fetches one per decision | Fewer API calls | Fresher chain |
| `lots` | 1 | Lots of the one leg (the older `quantity` is read too; never below 1) | Larger position | — |

## Worked example

**No run has been walked through yet.** Live-paper runs on BANKNIFTY, NIFTY
and SENSEX have been started every trading morning since 2026-09-15 (the
morning plan, `config/morning-plan.txt`); this section still owes one of
them, signal by signal, with ids from `simulation_signals`, `paper_orders`
and `paper_positions`.

Until then, here is the arithmetic of the entry rule on the fixture in
`tests/test_chain_flow_buy.py` (`Harness`, not database rows), as the code
computes it:
- **Trend.** NIFTY 5m candles open at 24,000 at 09:15 and each closes 6
  points above its open. The signal bar is the 12:25–12:30 candle; it closes
  at 24,234, above the 9 EMA (24,210), which is above the 21 EMA (24,174),
  and above the session open of 24,000. Decided at 12:30.
- **OI flow.** Twenty minutes earlier every strike carried 100,000 call and
  100,000 put OI. Now calls carry 101,000 and puts 106,000. ATM is 24,250,
  so the 7 strikes from 24,100 to 24,400: $\Delta P = 42{,}000$,
  $\Delta C = 7{,}000$, and the window holds
  $7 \times 207{,}000 = 1{,}449{,}000$ contracts. So
  $F = 100 \cdot 35{,}000 / 1{,}449{,}000 = +2.42\% \ge 1.0\%$.
- **Volume.** The ATM call traded 3,000 on the signal bar against a 1,000
  mean: $3{,}000 \ge 1.5 \times 1{,}000$.
- **IV.** 14.0 against a session low of 13.5: $14.0 \le 13.5 \times 1.2 = 16.2$.
- **The contract.** The 24,250 CE (delta 0.46, the closest to 0.5; the
  24,200 CE's 0.585 is further), reading long build-up, in 2 lots.

## Limitations

- **Backtests read a recorded or rebuilt chain.** Once the platform has
  recorded an underlying's chain (September 2026 on), a backtest reads those
  minute captures, and a moment with no capture within 180 s is blocked by
  the age check. Before the first capture, for an index, it reads a chain
  the API rebuilds from `option_history_bars`: the nearest weekly expiry,
  only the strikes stored (ATM−5 to ATM+5, and ±10 for NIFTY from August
  2021), OI and IV from Dhan's minute bars, delta from Black-Scholes on the
  stored IV, and build-up measured from that session's open. A moment with
  neither is blocked ("no spot price to compare the chain with").
- **The chain is per minute.** OI, IV and greeks come from the per-minute
  capture. Live, near-money strikes' LTP, volume and OI are overlaid from
  live quotes, which Dhan streams about once a second for ATM ±5; a backtest
  or recap has no overlay.
- **Nearest expiry only.** The recorder captures only the nearest expiry, so
  on an expiry day the strategy buys that day's contract, with the earlier
  cut-off.
- **IV low is the run's.** $\sigma_{\min}$ is the lowest IV the run itself
  read, on candles that passed the data checks. A run started at 13:00 knows
  nothing of the morning.
- **No exit of its own, and no look at its positions.** A bought option with
  no leg stop can lose the whole premium, and a CE and a PE can be open at
  the same time.
- **Unproven thresholds.** The defaults (`flow_min_pct` 1.0,
  `volume_multiple` 1.5, `iv_max_rise_pct` 20) are reasoned starting points,
  not fitted to results. Read the DATA lines and the fills before trusting
  or changing them.
- **Index candles have no volume**, so the volume check is on the ATM option
  of the side being bought.

Where the code and its own docstring or comments disagree (found 1 Oct 2026;
the strategy trades live, so none of these has been changed):

- **A backtest decides one bar late.** `on_bar` always takes `bars[-2]` as
  the signal bar, and `volume_surge` the option's `bars[-2]`, because "the
  newest bar is still forming". That is true live and in a recap, but a
  backtest hands the strategy bars complete by the end of the bar being
  replayed (`backtest/feed.py`, `bars_upto`; `backtest/engine.py` says so
  where it calls the run filters with `newest_bar_is_forming=False`). So in
  a backtest every decision is about the candle before the newest closed
  one, its chain is read as of that earlier close, and the fill comes a
  whole bar after the moment a live run would have bought. Not look-ahead,
  but backtest results are not those of the live rule. SignalBuilder and
  SmcStructureBreak drop the newest bar only when the mode is `LivePaper`.
- **Re-arming reads only the EMAs.** The state comment and the class
  description say a side re-arms when "the trend candle condition" stops
  holding; the code clears it only when the close leaves the EMA stack
  ($C_t > E^9_t > E^{21}_t$, or the mirror), ignoring the candle's colour and
  the session open.
- **The volume is the ATM option's, not the bought one's.** The module
  docstring and the description say "the option being bought" traded heavier
  than usual; the code reads the runner's `atm_ce` / `atm_pe` bars, and the
  contract bought may be up to `delta_search_strikes` strikes away. The
  signal's reason and metadata do call it the ATM volume.
- **The volume bar is found by position, not by time.** `volume_surge` takes
  the option series' `bars[-2]` as the signal bar without checking its time.
  If the option has not yet printed in the new 5 minutes when the index has
  (live, on the first ticks of a bar), or its series has a hole, the volume
  read is that of a different candle.
- **The OI flow can span much more than 15 minutes.** The description says
  "over the last `flow_lookback_minutes`" and a comment says two lookbacks of
  history is all a decision reads, but samples are kept by count, not by
  time, and blocked candles add none. After a 60-minute chain outage, the
  first good candle compares OI with a sample from before the outage, and
  can buy on that hour's flow.
- **The age check allows a capture up to 180 s after the decision.** Its
  comment says a chain captured after the candle is information the candle
  could not have had, but the test is `abs(age) > max_chain_age_seconds`,
  so up to 180 s ahead passes. In practice the view with `asOfUtc` returns
  only captures at or before that moment, so this has no effect today.

## Facts (machine-readable)

```yaml
name: ChainFlowBuy
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, option candles, option chain OI
instruments: NIFTY, BANKNIFTY, SENSEX, FINNIFTY, MIDCPNIFTY
default_lots: 1
built_in_exit: false
added: 2026-09-15
spec_version: 2
```
