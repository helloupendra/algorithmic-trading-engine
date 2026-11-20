# TrendRiderV2OB

Source: `strategies/directional/trend_rider_v2_ob.py` (`TrendRiderV2OBStrategy`, a subclass of
`TrendRiderV2BEStrategy`). Every rule below is read from the code. Times are IST (UTC + 5:30). Read
[TrendRiderV2](TrendRiderV2.md) and [TrendRiderV2BE](TrendRiderV2BE.md) first: this page describes only what this
version adds. TrendRiderV2 and TrendRiderV2BE are unchanged and keep running as they were.

## Idea

On 20 Nov 2026 the owner asked for a way to keep V2's losses small enough to keep trading ("funds khatam"). Eleven
rules were tested against V2 and V2 BE under rules written before any run (private project repo,
`v2-obstop-be-caps-2026-11-20`). Two passed the two years of development data and held on the untouched
Aug 2020 – Aug 2024 data. This version is V2 BE with the first, and with the second available but off by default:

1. **The stop at the order block (OB+BE).**
   - V2 places its index stop 0.5 × the average daily range (ADR) against the entry.
   - V2 OB places it at the *order block*: the last 5-minute candle before the signal candle that closed against
     the trade. For a call, the stop is the low of the last candle that closed down; for a put, the high of the last
     candle that closed up.
   - The distance is clamped to 0.15–0.5 × ADR from the entry, so it is never further away than V2's own stop.
2. **Half the lots after a wide day (WIDEH), off by default.** With `wide_prev_adr: 1.0` (the tested setting),
   every entry on a day after a range of at least 1.0 × ADR buys half the run's lots (1 of 2). The owner trades a
   fixed 2 lots ("size nhi change karni hai", 20 Nov 2026), so by default the run's lots are always bought.

## Data it needs

As [TrendRiderV2BE](TrendRiderV2BE.md#data-it-needs): the run's 5-minute candles (twelve sessions of warm-up, ten
complete sessions for the ADR) and the quotes of the ITM call and put. The order block is read from the same
candles.

## Timeframe

As [TrendRiderV2BE](TrendRiderV2BE.md#timeframe):
- closed 5-minute candles in the run's market session;
- entries 09:15–14:30 on NSE and BSE;
- the stops, the breakeven step and the trail on every call (live: every tick);
- the day-open exit on a closed candle;
- the 15:15 square-off.

## Entry

V2 BE's entry, unchanged: a 5-minute close beyond yesterday's high (low) and the day's open buys the call (put) one
strike in the money. V2's day filters apply, among them the skip after a day wider than 1.3 × ADR and after a gap
of more than 0.2% against the trade.

**What is bought.** The run's `lots`. Only with WIDEH turned on (`wide_prev_adr` above 0), after a wide day:

$$
\text{lots}_t = \begin{cases} \max(1, \lfloor \text{lots} \cdot s \rfloor) & \text{if } R_y / ADR \ge w \\ \text{lots} & \text{otherwise} \end{cases}
$$

where $R_y$ is yesterday's range, $w$ is `wide_prev_adr` (0 by default, 1.0 as tested) and $s$ is `wide_prev_size`
(0.5). The ratio is the
signal's own `prev_range_adr`, rounded to 3 places as V2 writes it, and is the same for every entry of the day.

The signal's `metadata.trend_rider` adds:
- `ob_stop`, the index level of the stop;
- `lots`, what was bought;
- `size_rule`, only when it bought fewer lots than the run's.

Its `index_exits.stop_adr` is the order block's own distance × ADR, so the run page draws the stop where it is.
The entry reason starts "Trend Rider V2 OB:".

## Position management

As V2 BE: one group at a time, synced with the run's open groups. The position also remembers its order-block stop
level and the lots it was bought with.

## Exit

The first that fires wins:

1. **The run's risk rules** (leg, group, overall).
2. **Order-block stop (new):** checked on every tick before the rest. The underlying is at or through the level
   $$S = E \mp \operatorname{clamp}(d_{ob},\ m \cdot ADR,\ k \cdot ADR)$$
   where:
   - $E$ is the entry ($-$ for a call, $+$ for a put);
   - $d_{ob}$ is the distance from $E$ to the order block's low (call) or high (put);
   - $m$ is `ob_stop_min_adr` (0.15);
   - $k$ is `index_stop_adr` (0.5).

   If no candle before the signal closed against the trade, the distance is $k \cdot ADR$, V2's stop. The reason
   reads "TrendRiderV2OB index stop (order block): ...".
3. **V2's index stop** at 0.5 × ADR stays as a safety net. It cannot fire first, because the order-block stop is
   at or inside it.
4. **Index trail, breakeven stop, day-open exit, 15:15 square-off, the run's stop button:** V2 BE's, unchanged.

Every sell carries the lots the position was bought with.

## Parameters

V2 BE's parameters all apply. This version adds four:

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `ob_stop` | true | The order-block stop; false gives V2 BE's stop (V2 BE exactly while WIDEH is off) | — | — |
| `ob_stop_min_adr` | 0.15 | The closest the stop may sit to the entry, × ADR (0 to `index_stop_adr`) | Fewer stops on a small wobble | Tighter stops, more shake-outs |
| `wide_prev_adr` | 0 | Yesterday's range, × ADR, from which the day trades smaller; 0 (the default) turns WIDEH off; 1.0 was tested | Fewer smaller days | More smaller days |
| `wide_prev_size` | 0.5 | The share of `lots` bought after such a day (above 0, at most 1; rounded down, at least 1) | Larger trades after wide days | Smaller ones |

## Worked example

**19 Nov 2026, NIFTY** (server bars, ADR 279.6, yesterday 0.89 × ADR, so full size: 2 lots).

- The 09:25 candle closes at 25,116.2, below yesterday's low (25,141.8) and the open (25,186). The put is bought.
- **Order block.** The 09:20 candle closed down, so it does not count. The 09:15 candle closed up, with a high of
  25,255.35. That is 139.2 points above the entry, inside the 0.5 × ADR limit of 139.8, so the stop is
  **25,255.35**.
- NIFTY reached 25,236.1. The stop was not hit, and the 09:40 candle closing back above the open sold the put, as
  in V2.
- At 13:45 the candle closes at 25,395.3, above yesterday's high (25,390.7). The call is bought.
- **Order block.** The last down candle had a low 43.6 points below the entry: 0.156 × ADR, above the 0.15 floor.
  The stop is **25,351.75**.
- NIFTY's lowest point afterwards was 25,382.65, so the call was held to 15:15, as in V2.

**After a wide day.** With `wide_prev_adr: 1.0` and yesterday's range at 1.1 × ADR, the same signals would have
bought 1 lot each, and each sell would have sold 1 lot. By default they buy the run's 2.

## Limitations

- **Tested on NIFTY options only**, simple P&L, 2 lots of 65, no charges:

| | Sep 2024 – Sep 2026 | Deepest fall, % of ₹1 lakh | Aug 2020 – Aug 2024 | Deepest fall |
|---|---|---|---|---|
| V2 | +₹2,53,025 | 65.9% | +₹3,98,320 | 60.8% |
| V2 BE | +₹2,82,867 | 56.5% | +₹4,55,397 | 60.2% |
| **OB+BE (this class, default)** | **+₹3,14,665** | **55.9%** | **+₹4,69,573** | **56.6%** |
| OB+BE with WIDEH (`wide_prev_adr: 1.0`) | +₹3,40,798 | 51.2% | +₹4,61,884 | 45.8% |

- **What was judged:**
  - OB+BE passed against V2 BE, and WIDEH passed as a size rule against V2;
  - the two together were pre-registered as "reported, not judged".
- **The margins are small.** OB+BE beat V2 BE on the holdout by only 3%. Its worst day (24 Mar 2026, −₹8,382 a lot
  in V2) is not helped by the order block.
- **The risk is still large.** At a fixed 2 lots on ₹1 lakh, the deepest fall is 56–57% of the account
  (development and holdout).
- **The lab replays on 1-minute closes; live acts on every tick.** A stop or breakeven level touched only inside a
  minute fires live but not in the lab. On 18 Nov 2026, V2 BE's breakeven sold live at 14:19, but the lab held the
  trade to 15:15.
- **This class reproduces the test trade for trade:** the 405 development trades of OB+BE. With
  `wide_prev_adr: 1.0`, every trade also carries WIDEH's lots.
- Stocks and MCX have not been replayed. Everything listed for [TrendRiderV2BE](TrendRiderV2BE.md#limitations)
  applies.

## Facts (machine-readable)

```yaml
name: TrendRiderV2OB
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, ticks
instruments: NIFTY
default_lots: 1
built_in_exit: true
added: 2026-11-20
spec_version: 1
```
