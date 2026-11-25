# TrendRider

Source: `strategies/directional/trend_rider.py` (`TrendRiderStrategy`). Every
rule below is read from the code. Times are IST (UTC + 5:30).

## Idea

Buy an option one strike in the money (`itm_steps`) on a day that is
trending, and stay in while the day keeps its direction.

The strategy came out of the Ghost research of 13 Nov 2026, which found three
things on NIFTY:
- Ghost's entries did no better than random ones.
- Its 20-point target cut every trend day short.
- Most of its losses came on sideways days.

So this strategy waits for the one thing a trend day has to do: break out of
its first half hour on the same side of its open. It then holds with no target
until the index closes back across that open. It also stays out on the days
that, in the same data, tended to go sideways:
- the day after a wide day;
- a break while today's range is still small;
- a break against a gap;
- by default, any day whose previous session was not a narrow one.

The bet is the one every trend follower makes: many small losses, paid for by
a few days that run all session. **It is not shown to be profitable.** The
research and the replays below are slightly positive in each period tested,
but the result rests on a handful of trades and on one sensitive threshold. It
needs a forward paper test before anyone trusts it.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (e.g. `NSE:NIFTY50-INDEX`) | 5m | `warmup_bars` = 900 (twelve sessions). The average range needs `adr_days` (10) complete sessions before today, where complete means at least 60 of the 75 candles seen | Backtest: `candles`. Live: the platform's candle store, else broker history, replayed at start (`core/warmup_source.py`) |
| option quotes | the CE and PE `itm_steps` strikes in the money (1 by default; 0 is ATM) of the run's expiry (`buy_ce`, `buy_pe`) | latest quote | none | the runner's contract resolver (`get_contract_requirements`). A replay prices them from `option_history_bars` or the stored option candles |

It reads no volume, no option chain and no option candles. The decision is
made on the index alone.

**Live warm-up.** A live runner replayed the newest 500 bars until 15 Nov
2026, which is six and a half sessions. With that much history this strategy
could never compute its average range, and it skipped every day as
`no_history`. Since then the runner loads as many days as `warmup_bars` needs
and replays that many bars (`warmup_days_for`, `live_warmup_count`).

## Timeframe

- **Bar:** closed 5-minute candles between 09:15 and 15:25, once per candle.
  - A replay hands closed candles only.
  - Live, the forming candle is left out. The decision comes on the first tick
    after a candle closes.
  - Warm-up candles (`source: warmup`) are remembered for the average range
    but never evaluated.
- **Entries:** on a candle stamped from `first_entry` (09:45) to `last_entry`
  (14:30), both included. The stamp is the candle's start, so the first
  possible entry is decided at 09:50, on the close of the 09:45 candle.
- **Exits:** on any closed candle, at any time of day.
- **End of day:** the strategy carries nothing overnight.
  - A replay squares off at its `eod_square_off_ist`, 15:15 by default.
  - Live, `MarketHoursService` closes the run at the market close (15:30;
    see [the specs' note on 15:45](README.md) from 7 Dec 2026).

## Entry

Let $O_d$ be the open of the day's first candle. Let $H^{OR}$ and $L^{OR}$ be
the high and low of the first $k$ = `or_minutes` / 5 candles (6 at the
default 30). Let $C_t$ be the close of the candle being evaluated.

$$
\text{call} \iff C_t > H^{OR} \land C_t > O_d
\qquad
\text{put} \iff C_t < L^{OR} \land C_t < O_d
$$

The day must already have more than $k$ candles. At most `max_entries` (3)
entries a day, one position at a time.

**Re-entry.** After an exit there is no entry on the candle that closed the
position. After that, an entry on the side last traded needs both of these:
- the day's high (for a call) or low (for a put) is beyond where it was at the
  last entry;
- this candle made that new extreme.

The first entry of the day, or an entry on the other side, needs only the
break itself.

**Day filters.** These are checked once a break qualifies, in this order. The
first that refuses ends the evaluation, and it is counted in the state's
`skipped` map under the name in brackets.

$A$ is the mean high-low range of the last `adr_days` complete sessions. $R_y$
is yesterday's range, $R_d$ today's range so far, and $C_y$ yesterday's close.

1. No average yet: fewer than `adr_days` complete sessions remembered
   (`no_history`).
2. Yesterday was wide: $R_y / A$ > `skip_after_wide` (1.3)
   (`after_wide_day`).
3. Yesterday was not narrow: $R_y / A \ge$ `max_prev_range` (0.7), when that
   parameter is above zero (`yesterday_not_narrow`).
4. Today is still small: $R_d / A$ < `min_range_frac` (0.40)
   (`range_too_small`).
5. The gap is against the trade: $g < -$`max_gap_against_pct` (0.2), with
   $g = 100\,(O_d - C_y)/C_y$ for a call and its negative for a put
   (`gap_against`).

**The contract.** The run's call (or put) `itm_steps` strikes in the money:
with NIFTY at 22,790 and 50-point strikes, ATM is 22,800, so the call is
22,750 and the put 22,850. It must exist, be of the right
type, and expire today or later (`no_contract` otherwise). One leg, bought at
market, of `lots` lots, as an `OPEN_GROUP` signal. The signal's metadata
`trend_rider` block carries:
- the opening range;
- the day's open;
- the average range;
- yesterday's and today's range as fractions of the average;
- the gap;
- which entry of the day it was.

## Position management

One position at a time. The strategy keeps the group it opened and closes
only that.

**Position sync.** The strategy sets `requires_position_sync`, so both the
live runner and the replay pass the run's open groups
(`metadata["open_groups"]`). When something else closes the position (the
run's stop-loss, the square-off, the stop button), the strategy sees the group
gone on the next candle. It is then free to enter again, from the candle after.

**Its own stop and trail (since 17 Nov 2026).** By default (`index_exits`)
the position is sold when NIFTY moves 0.5 × ADR against the entry, and, once
NIFTY is 0.5 × ADR in favour, when it gives back 0.35 × ADR from its best.
Both are measured on the index and checked on every tick. Replayed minute by
minute, these made more than the strategy without them on Sep 2024 – Sep 2026
and on the untouched Aug 2020 – Aug 2024, with a smaller worst day in both.
`"index_exits": null` switches them off.

**What the run's risk rules add.** **No premium stop is advised**. The first research (13 Nov) used a leg stop
of 10 premium points, but it was checked on 5-minute replays. On 1-minute
premiums (16 Nov) that stop cut the trend days the strategy lives on: at one
strike ITM, Jan 2024 – Sep 2026, before costs, +₹340 a trade with it against
+₹490 without. The strategy's own exit (the day's open) is its stop. A run can
still set `{"risk": {"leg": {"stopLossPoints": …, "targetPoints": …}}}`. The
one pair worth a side-by-side paper test is a 30-point target with a 20-point
stop (+₹283 a trade before costs, t 2.0). It is the best of 40 pairs tried, so
it is not proven. Do not give it a small target: the point of the strategy is
to keep a trend day's whole move.

**What it never does.** It does not hedge, add to a position, average down,
or carry a position overnight.

## Exit

In order of precedence, the first that fires wins:

1. **The run's risk rules:** leg, then group, then overall.
   - Live, the API guard sweeps every three seconds.
   - The replay checks them on every bar. That is coarser, so a 10-point leg
     stop has filled as far as 19 points away in a 5-minute replay.
2. **The index stop and trail** (`index_exits`, on every tick): NIFTY 0.5 × ADR
   against the entry; or, once 0.5 × ADR in favour, a 0.35 × ADR give-back.
3. **The day's open gives way:** a call closes when a candle closes below
   $O_d$, a put when one closes above it. The reason names both prices.
4. **End of day:** 15:15 in a replay, the market close live.
5. **The run's stop button**, which flattens everything.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `itm_steps` | 1 | Strikes in the money for the option bought; 0 is ATM, at most 5 | A higher delta and less time value; a dearer lot | Cheaper; more of the premium is time value |
| `or_minutes` | 30 | Length of the opening range, from 09:15 | A later, surer break; fewer entries | Earlier breaks, more false ones |
| `first_entry` | `"09:45"` | Earliest candle (by its start) that may enter | — | Must not be earlier than the end of the opening range |
| `last_entry` | `"14:30"` | Latest candle that may enter | Late entries have little day left to run | — |
| `max_entries` | 3 | Entries a day | More re-entries on choppy trend days | 1 takes only the first break |
| `adr_days` | 10 | Complete sessions in the average range | A steadier average; more history needed | An average that follows the last few days |
| `skip_after_wide` | 1.3 | Skip the day after a session wider than this × the average | Trades after big days too | Skips more days |
| `min_range_frac` | 0.40 | Today's range so far must be at least this × the average | Fewer, later entries | Takes breaks of a still-quiet day |
| `max_gap_against_pct` | 0.2 | Largest gap against the trade, in % | Fights bigger gaps | Refuses smaller ones |
| `max_prev_range` | 0.7 | Trade only after a session narrower than this × the average; 0 turns the rule off | Trades more days (see Limitations: worse) | Fewer days |
| `day_filters` | `true` | The four day rules above (`skip_after_wide`, `max_prev_range`, `min_range_frac`, `max_gap_against_pct`). `false` (or `off`) trades every break at its level, and the entry's reason names the rule that would have skipped it. The desk's paper runs use `off` from 26 Nov 2026, on the owner's word, to watch what the rules keep out | — | — |
| `timeframe` | `"5m"` | The candle every rule reads: `1m`, `2m`, `3m`, `5m`, `10m` or `15m`. `or_minutes` must be a whole number of candles; live, the API builds any of them from 1-minute bars | Fewer, slower signals | More, noisier signals |
| `index_exits` | stop 0.5, trail 0.5 / 0.35 | Exits on the index, from `strategies/index_exits.py`: `stop_adr`, `trail_arm_adr` + `trail_giveback_adr`, `supertrend`, `ema_close`, `psar`, `chandelier`, `exit_after_ist`. Stop, trail and time act on every tick; the candle rules on closed candles | — | — |
| `lots` | 1 (`default_lots`) | Lots per leg | — | — |

## Worked example

This walkthrough comes from a replay on the owner's research stack, not the
production database. It was made before spec version 2, so it bought ATM and
used a 10-point stop. The run was local run 34 on 13 Nov 2026, with this
setup:
- NIFTY on the 5-minute chart, 13 Aug – 12 Nov 2026;
- one lot (65), with `{"risk": {"leg": {"stopLossPoints": 10}}}`;
- 0.5% slippage a side and the statutory charges.

The prices below are that run's fills.

**15 October 2026.**
- **Before the break:** the day opened at 22,676. The first 30 minutes
  ranged 22,644–22,780. The ten-session average range was 287 points.
  - Yesterday's range was 0.63 × that average: narrow, so the day is
    allowed.
  - The gap was +0.24%, with the trade.
  - Today's range by then was at least 136 points, 0.47 × the average.
- **The entry:** the 09:45 candle closed at 22,787, above the opening range's
  high and above the open. On that close the run bought the 22,800 CE at
  ₹119.54.
- **The hold:** NIFTY never closed back below 22,676, so nothing closed the
  position.
- **The close:** the 15:15 square-off sold it at ₹200.99. That is +81.45
  points × 65 = **+₹5,294** before charges.

That one trade is most of what the strategy made in the whole 3-month period.
The run had 8 trades in all:

| Day | Side | Result |
|---|---|---|
| 17 Aug | PE | −₹892 (stop) |
| 24 Aug | PE | +₹3,196 (held all day) |
| 7 Sep | PE | +₹1,010 |
| 21 Sep | CE | −₹735 (stop) |
| 21 Sep | CE, re-entry | +₹174 |
| 24 Sep | PE | +₹3,892 |
| 15 Oct | CE | +₹5,294 |
| 28 Oct | PE | −₹1,327 (stop) |

In the longer run below (Sep 2025 – Nov 2026), a trader with ₹2,00,000 trading
one lot on every signal would have ended at about ₹2,12,300 after charges.
The worst fall from a peak on the way was ₹14,203. Without that period's five
best trades, they would have ended at about ₹1,67,300.

## Limitations

- **Not shown to be profitable.** These are replays on the research stack:
  one lot, a 10-point leg stop, real premiums, 0.5% slippage and charges.

  | Period | Trades | Win % | Net | Per trade | t | Without the best 5 trades |
  |---|---|---|---|---|---|---|
  | Jan 2024 – Jun 2025 | 132 | 19% | +₹10,098 | +₹76 | 0.27 | −₹54,426 |
  | Sep 2025 – 12 Nov 2026 | 60 | 23% | +₹12,343 | +₹206 | 0.47 | −₹32,683 |
  | 13 Aug – 12 Nov 2026 | 8 | 63% | +₹10,109 | +₹1,264 | 1.43 | −₹3,143 |

  - Every period is positive, and every one turns negative without its five
    best trades.
  - None is statistically different from zero.
  - About 4–7 trades a month.
- **One threshold carries it.**
  - With `max_prev_range` at 0, the two long periods lose: −₹48,709 on 372
    trades and −₹9,112 on 302.
  - In the minute-level research, narrow-day thresholds of 0.6 and 0.8 were
    clearly worse than 0.7.
  - Only 24–39 of 108 nearby settings were positive in all three periods.
- **Written and tested on NIFTY only.** The rules use percentages and the
  average range, so they run on any index. But no other index has been
  tested, and the signal and exit reasons say "NIFTY" whatever the underlying.
- **The replay's stop is late:** it is checked on 5-minute closes. Live, the
  guard checks every three seconds, so live stops should cost less than the
  table shows. They have not been measured, because the strategy has never run
  on paper.
- **Lot size 65 for every period**, as the backtest applies today's master.
  Points are unaffected. Rupees from periods with another lot size are not
  what a trader would have made then.
- **The day's open is the first 5-minute candle's open** in the candle store.
  A missing or late first candle moves the line every entry and exit is
  measured from.

## Facts (machine-readable)

```yaml
name: TrendRider
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles
instruments: NIFTY
default_lots: 1
built_in_exit: true
added: 2026-11-15
spec_version: 2
```
