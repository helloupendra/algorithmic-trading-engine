# TrendRiderV2

Source: `strategies/directional/trend_rider_v2.py` (`TrendRiderV2Strategy`, a subclass of
`TrendRiderStrategy`). Every rule below is read from the code. Times are IST (UTC + 5:30). Read
[TrendRider](TrendRider.md) first: this page describes only what V2 changes.

## Idea

TrendRider buys an option one strike in the money on a trending day and holds it while the day keeps
its direction. Watching it, the owner named three things to change (16–17 Nov 2026):

1. **It waits for 09:45.** The entry needs the first half hour's range, so a day that trends from the
   open has often run a long way before the first trade.
2. **It has no stop-loss.** Its only exit is a candle closing back across the day's open, which can be
   far away.
3. **It gives big profits back.** A trade can be well up at midday and close near flat at 15:15,
   because nothing protects the gain until the open is crossed.

V2 answers each one: it enters on **yesterday's high or low** from the first candle; it sells on an
**index stop**; and it sells on an **index trailing exit** once the trade is well in profit. Both exits
are measured on the index, in units of the average daily range (ADR), not on the premium: a premium
stop of 30–40% sold trend days in their first dip.

## Data it needs

The same as TrendRider: 5-minute index candles (`DataRequirement("index", "5m")`), twelve sessions of
warm-up (`warmup_bars` = 900), and the index spot on every call for the two exits. Contracts: `buy_ce`
and `buy_pe`, `itm_steps` strikes in the money.

## Timeframe

Entries are judged once per closed 5-minute candle, from `first_entry` (09:15) to `last_entry` (14:30).
The index stop and the trailing exit are checked on **every call**: live, that is every tick of the
spot, so they act within seconds; in a replay, every candle.

## Entry

With `levels: "prev_day"` (the default), on a closed candle:

- **Call:** the close is above yesterday's high **and** above the day's open.
- **Put:** the close is below yesterday's low **and** below the day's open.

Everything else is TrendRider's, unchanged: one position at a time; after an exit, a new entry in the
same direction only on a new day high (call) or low (put); at most `max_entries` a day; and the day
filters `skip_after_wide`, `max_prev_range` and `max_gap_against_pct`, with the day's open, yesterday's
session and the ADR computed as TrendRider computes them. TrendRider's fourth filter, "today's range is
still below 0.4 × ADR" (`min_range_frac`), is **off** by default: a close beyond yesterday's high or low
is already a range. Of the three combinations replayed for the desk, this one had the smallest deepest
fall, which was the rule chosen before the replay.

With `levels: "opening_range"` the entry is TrendRider's own (a break of the first `or_minutes`), and
V2 adds only the two exits.

The signal's `metadata.trend_rider` carries the levels, the ADR, the entry spot and the exit settings,
and its group id starts `TR2_`.

## Position management

As in TrendRider: one group at a time, synced with the run's open groups, so a position the run closes
(its own risk rule, the square-off, the stop button) frees the strategy from the next candle. After a
V2 exit, no new entry is taken from the candle the exit fell in.

The position remembers its entry spot, the ADR at entry and its best move in favour (`best`, in index
points).

## Exit

The first that fires wins:

1. **The run's risk rules** (leg, group, overall), as for any strategy.
2. **Index stop:** the index has moved `index_stop_adr` × ADR against the entry spot (0.5 × a 300-point
   ADR is 150 points).
3. **Index trail:** the best move in favour has reached `trail_arm_adr` × ADR, and the index has given
   back `trail_giveback_adr` × ADR from that best.
4. **The day's open gives way:** TrendRider's exit, a candle closing back across the open.
5. **End of day:** the run's square-off.

## Parameters

TrendRider's parameters all apply (see its table); V2 changes `first_entry` and adds the rest.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `levels` | `"prev_day"` | `prev_day`: yesterday's high/low; `opening_range`: TrendRider's entry | — | — |
| `first_entry` | `"09:15"` | Earliest candle (by its start) that may enter | Later entries | — |
| `min_range_frac` | 0 | TrendRider's "today's range so far" filter; off in V2 | Waits for a wider day before entering | — |
| `index_stop_adr` | 0.5 | Index move against the entry, × ADR, that sells; 0 turns it off | Fewer stops, bigger losses | Tighter; more whipsaw exits |
| `trail_arm_adr` | 0.5 | Move in favour, × ADR, after which the trail is live; 0 turns it off | Protects only bigger runs | Protects smaller runs, sells more trends early |
| `trail_giveback_adr` | 0.35 | Give-back from the best, × ADR, that sells once armed | Holds through deeper pullbacks | Sells sooner |

## Worked example

NIFTY, ADR 300 points; yesterday's high 25,200, low 25,000 (a 200-point day, 0.67 × ADR: narrow enough for
`max_prev_range`). Today opens at 25,180.

- 09:25 candle closes at 25,215: above yesterday's high and the open. **Buy the call** one strike in the
  money. The entry spot is 25,215. (TrendRider could not have bought before 09:45.)
- The index stop is at 25,215 − 0.5 × 300 = **25,065**.
- By 11:40 NIFTY reaches 25,420: +205 points, above the 150-point arming level, so the trail is live.
- At 13:05 NIFTY trades 25,315, a give-back of 105 = 0.35 × 300. **Sell**, keeping +100 index points,
  where TrendRider would have held until the open (25,180) was crossed or 15:15.

## Limitations

- **Not proven.** The three changes were tested separately on two years of NIFTY replays. The earlier
  entry raised the total but did worse in the most recent period; the index stop and the trail were the
  only exits that kept TrendRider's profit. The combination is on the desk on paper, at the owner's
  request, to be watched live.
- The exits use the index, not the premium: a fast move in implied volatility can still move the
  premium against the position before the index stop is reached.
- In a replay the exits are checked once a candle, so a replay stop can fill up to a candle's move past
  its level; live checks every tick.
- The other day filters stay on. They keep the strategy out of fast, wide-range markets (TrendRider took no
  trade in the 28 Oct – 12 Nov 2026 rally); turning them off is a separate test.
- A one-hour special session (Muhurat trading) counts as a full day in the ADR and as "yesterday".

## Facts (machine-readable)

```yaml
name: TrendRiderV2
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles
instruments: NIFTY
default_lots: 1
built_in_exit: true
added: 2026-11-17
spec_version: 1
```
