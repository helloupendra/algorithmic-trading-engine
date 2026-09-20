# ChainFlowBuy

## Idea

Buy an index option only when price and the option chain agree on the
direction.

A 5-minute trend candle says which way the index is moving. The chain then
has to say that money is backing that move:
- near the money, option writers are adding puts and not calls (for a call
  buy), or the mirror for a put;
- the option being bought is trading well above its recent volume;
- its premium is being bid up: long build-up or short covering;
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
| index candles | the run's spot symbol (e.g. `NSE:NIFTY50-INDEX`) | 5m | 23 closed 5m bars for the 21 EMA | ingestor (`live_bars`) |
| option candles | ATM CE and ATM PE of the traded expiry | 5m | 8 closed 5m bars for the volume mean | ingestor (`live_bars`) |
| option chain OI | the underlying's nearest expiry: every strike's OI, IV, delta, build-up, LTP | per-minute capture with live quotes laid over it | one sample `flow_lookback_minutes` (15) before the decision | chain poller (`option_chain_snapshots`, Dhan chain recorder), read through `GET /api/OptionChain/view` |

The strategy fetches the chain itself, at most once every
`chain_refresh_seconds`. The runner is not changed, so no other strategy is
affected.

## Timeframe

- **Evaluates on:** the 5-minute bar, on bar close. The newest bar is still
  forming, so each decision uses the one before it, once.
- **Entries:** between `entry_start` (09:30) and `entry_end` (14:45) IST.
  - On the chain's expiry day the last entry is `expiry_day_entry_end` (13:30),
    because late on expiry day theta takes most of a bought premium.
  - The time checked is the close of the signal bar.
- **At 15:30:** the platform squares off every open position
  (`MarketHoursService`). The strategy has no exit of its own and relies on
  that and on the run's risk rules.

## Entry

Let the signal bar close at $C_t$ with open $O_t$, $E^{9}_t$ and $E^{21}_t$ be
the 9- and 21-period EMAs of 5-minute closes, and $O_s$ the open of the
session's first bar.

**Trend candle.**

$$
\text{up} \iff C_t > O_t \land C_t > E^{9}_t > E^{21}_t \land C_t > O_s
\qquad
\text{down} \iff C_t < O_t \land C_t < E^{9}_t < E^{21}_t \land C_t < O_s
$$

**OI flow.** Take $S$, the ATM strike and `flow_strikes` (3) listed strikes
either side, counting only strikes whose OI is known now and $L$ =
`flow_lookback_minutes` ago. For each side, $\Delta P$ and $\Delta C$ are the
put and call OI added over those strikes in that time:

$$
F = 100 \cdot \frac{\Delta P - \Delta C}{\sum_{k \in S} (C_k + P_k)}
$$

**Volume surge.** $V_t$ is the traded option's 5-minute volume on the signal
bar; $\bar V$ is the mean of the `volume_lookback` (6) bars before it.

**IV ceiling.** $\sigma_t$ is the chain's ATM IV (the mean of the ATM CE and
PE IV). $\sigma_{\min}$ is the lowest ATM IV the run has sampled this
session.

A **CE** is bought when every one of these holds:

$$
\text{up} \land F \ge f \land V_t \ge m \cdot \bar V \land \sigma_t \le \sigma_{\min}(1 + r/100) \land \text{build-up} \in \{\text{long build-up}, \text{short covering}\}
$$

- $f$ is `flow_min_pct` (1.0).
- $m$ is `volume_multiple` (1.5).
- $r$ is `iv_max_rise_pct` (20).
- The volume and build-up are the call's.

A **PE** is bought on the mirror image: down trend, $F \le -f$, and the put's
volume and build-up.

In words: buy the call when the index closes a clean up-trend candle, writers
added more puts than calls near the money over the last 15 minutes, the call
traded at least 1.5 times its usual volume, its premium is being bid up, and
IV is within 20% of the day's low.

**The contract.** Among the calls (or puts) within `delta_search_strikes`
(3) of ATM that have a delta and an LTP of at least `min_premium` (5), the
one whose $|\Delta|$ is closest to `target_delta` (0.5). One leg, bought, of
`lots` lots.

**Blocked when the data is wrong.** Every closed bar prints a `DATA` line:
- the chain's age;
- its spot price and the gap to the index close;
- ATM IV and the session low;
- live legs;
- the OI flow;
- the trend.

No entry is taken when:
- the chain is more than `max_chain_age_seconds` (180) away from the candle in
  either direction — older, or captured after it, which in a replay would be
  information the candle could not have had;
- its spot is more than `max_spot_gap_pct` (0.35%) from the index close;
- it has no ATM IV.

The line says which check failed.

## Position management

After an entry, that side is disarmed. No second position on the same side
is taken until the trend-candle condition for it stops holding, however long
the confirmation lasts. At most `max_trades_per_session` (3) entries a day.

- **What the run's risk rules add on top.** The strategy relies on them
  entirely for managing a bought premium. Start it with a leg target and a
  leg stop-loss, for example a target of 30% and a stop-loss of 20% of the
  premium, or points that suit the underlying. A group or overall rupee
  limit is optional.
- **What the strategy itself never does.** It never exits, adjusts, rolls or
  averages. Without a leg rule, a position stays open until the stop button
  or the 15:30 square-off.

## Exit

In order of precedence:
1. Leg / group / overall risk rules from `parametersJson.risk`, swept by the
   API every 3 s.
2. The run's stop button.
3. The 15:30 market close square-off.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `ema_fast` | 9 | Fast EMA of 5m closes | Slower, fewer trend candles | Faster, more (and noisier) candles |
| `ema_slow` | 21 | Slow EMA of 5m closes | Longer trends only | Earlier, weaker trends |
| `flow_strikes` | 3 | Strikes either side of ATM in the OI flow | Wider, slower-moving read | Only the strikes at the money |
| `flow_lookback_minutes` | 15 | How far back the OI flow is measured | Steadier flow, later entries | Faster, noisier flow |
| `flow_min_pct` | 1.0 | Net put-minus-call OI added, % of the window's OI | Stronger positioning required, fewer trades | More trades on weaker flow |
| `volume_multiple` | 1.5 | Signal bar volume vs its recent mean | Only strong surges | Ordinary volume passes |
| `volume_lookback` | 6 | 5m bars in the volume mean | Longer baseline | Shorter baseline |
| `require_buildup` | true | Demand long build-up or short covering on the bought option | — | `false` ignores build-up |
| `iv_max_rise_pct` | 20 | ATM IV ceiling above the session's low, in % | Buys into higher IV | Refuses sooner after an IV rise |
| `target_delta` | 0.5 | Delta the chosen strike should be closest to | Deeper in the money | Further out of the money, cheaper |
| `delta_search_strikes` | 3 | Strikes either side of ATM searched for that delta | Wider search | Near the money only |
| `min_premium` | 5 | Lowest LTP a leg may have | Excludes cheap far strikes | Allows them |
| `entry_start` | 09:30 | First entry (IST, signal bar close) | Skips more of the open | Trades the opening noise |
| `entry_end` | 14:45 | Last entry (IST) | Later entries, less time to work | Earlier stop |
| `expiry_day_entry_end` | 13:30 | Last entry on the chain's expiry day | More expiry-day theta risk | Less |
| `max_trades_per_session` | 3 | Entries per day | More trades | Fewer |
| `max_chain_age_seconds` | 180 | How far from the candle a chain may be captured, either side | Tolerates a lagging recorder | Blocks sooner |
| `max_spot_gap_pct` | 0.35 | Largest chain-spot vs index-close gap | Tolerates a mismatch | Blocks sooner |
| `chain_refresh_seconds` | 20 | Seconds between chain fetches | Fewer API calls | Fresher chain |
| `lots` | 1 | Lots per leg | Larger position | — |

## Worked example

**No run yet.** The first live-paper runs start on 2026-09-15. This section
will walk through one of them signal by signal, with ids from
`simulation_signals`, `paper_orders` and `paper_positions`, once they exist.

Until then, here is the arithmetic of the entry rule on the fixture in
`tests/test_chain_flow_buy.py` (not database rows):
- **Trend.** NIFTY 5m candles rise 6 points each. The signal bar closes at
  24,228 above both EMAs and above the session open.
- **OI flow.** Twenty minutes earlier every strike carried 100,000 call and
  100,000 put OI. Now calls carry 101,000 and puts 106,000. Over the 7 strikes
  from 24,100 to 24,400: $\Delta P = 42{,}000$, $\Delta C = 7{,}000$, and the
  window holds $7 \times 207{,}000 = 1{,}449{,}000$ contracts. So
  $F = 100 \cdot 35{,}000 / 1{,}449{,}000 = +2.42\% \ge 1.0\%$.
- **Volume.** The ATM call traded 3,000 on the signal bar against a 1,000
  mean: $3{,}000 \ge 1.5 \times 1{,}000$.
- **IV.** 14.0 against a session low of 13.5: $14.0 \le 13.5 \times 1.2 = 16.2$.
- **The contract.** The 24,250 CE (delta 0.445, the closest to 0.5), in 2 lots.

## Limitations

- **Live paper only.** A replay has no historical chain to fetch; the
  strategy returns no signals in `OfflineReplay`, so it cannot be backtested
  yet. A backtest would need the chain read as of each bar
  (`/api/OptionChain/view?asOfUtc=`) and OI history for the replayed days. OI
  exists only for sessions the chain recorder ran (from 2026-09-14).
- **The chain is per minute.** OI, IV and greeks come from the per-minute
  capture. Near-money strikes' LTP, volume and OI are overlaid from live
  quotes, which Dhan streams about once a second for ATM ±5.
- **Nearest expiry only.** The recorder captures only the nearest expiry, so
  on an expiry day the strategy buys that day's contract, with the earlier
  cut-off.
- **IV low is the run's.** $\sigma_{\min}$ is the lowest IV the run itself
  sampled. A run started at 13:00 knows nothing of the morning.
- **No exit of its own.** A bought option with no leg stop can lose the whole
  premium.
- **Unproven thresholds.** The defaults (`flow_min_pct` 1.0,
  `volume_multiple` 1.5, `iv_max_rise_pct` 20) are reasoned starting points,
  not fitted to results. Read the DATA lines and the fills before trusting
  or changing them.
- **Index candles have no volume**, so the volume check is on the option
  being bought.

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
spec_version: 1
```
