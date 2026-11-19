# TrendRiderV2BE

Source: `strategies/directional/trend_rider_v2_be.py` (`TrendRiderV2BEStrategy`, a subclass of
`TrendRiderV2Strategy`). Every rule below is read from the code. Times are IST (UTC + 5:30). Read
[TrendRiderV2](TrendRiderV2.md) first: this page describes only what this version adds. TrendRiderV2 itself is
unchanged and keeps running as it was.

## Idea

TrendRiderV2 buys an option one strike in the money on a day that breaks yesterday's high or low, and sells on an
index stop, an index trailing exit or a candle closing back across the day's open. On 17 Nov 2026 eleven ways of
trailing it were tested against V2 under rules written before any run. One passed both the two years of
development data and the untouched four-year holdout: **BE25**, a breakeven step. This version is V2 with that step
on, for any market the desk trades.

1. **The breakeven step.** Once the underlying has been `breakeven_adr` (0.25) × the average daily range (ADR) in
   the trade's favour, the stop moves to the entry level: the position is sold when the underlying is back at or
   through its entry price. V2's own stop (0.5 × ADR against) and trail are unchanged. The step sells trades that
   went a little way and came all the way back, which V2 held to its stop, the day's open or the close. On V2's worst
   development day (24 Mar 2026, an expiry) the second trade was bought at ₹111, its premium reached ₹174, NIFTY
   never reached the 0.5 × ADR that arms the trail, and V2 sold it at the 15:15 square-off at ₹9; BE25 sold it at
   ₹62.45 (lab, `trailing-2026-11-17`).
2. **Any market.** The candles are read in the session of the run's own market: NSE and BSE 09:15–15:30, MCX
   09:00 to its close. So the strategy can run on index options, stock options (HDFCBANK, ICICIBANK, ...) and MCX
   commodity options (CRUDEOIL, NATURALGAS, ...).
3. **Options or the share itself.** With `instrument_kind: equity` it buys the stock itself (cash equity) instead
   of an option, long only, for testing a stock without options.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's price symbol: an index (`NSE:NIFTY50-INDEX`), a share (`NSE:HDFCBANK-EQ`) or, on MCX, the near-month future the nearest options are written on (`MCX:CRUDEOIL26DECFUT`) | 5m | `warmup_bars`: twelve sessions of the market's candle window, 900 on NSE (75 a day), 2,148 on MCX (179 a day). The average range needs `adr_days` (10) complete sessions before today: at least 80% of the shortest regular session's candles (60 on NSE, 139 on MCX) | Live: the platform's candle store, else broker history, replayed at start (`core/warmup_source.py`). Backtest: `candles` |
| option quotes (`instrument_kind: options`) | the CE and PE `itm_steps` strikes in the money of the run's expiry (`buy_ce`, `buy_pe`) | latest quote | none | the runner's contract resolver (`get_contract_requirements`) |
| the share's quote (`instrument_kind: equity`) | the run's price symbol itself | latest quote | none | the live feed (the run puts its price symbol on the watchlist) |

With `instrument_kind: equity` it declares no contract at all, and the live runner then resolves none
(`resolve_contract_requirements`; before 17 Nov 2026 it fell back to the ATM call and put of any strategy that
declared none).

## Timeframe

- **Bar:** closed 5-minute candles inside the run's market session, once per candle, as TrendRider.
- **The market** (`market`, `auto` by default):
  - **MCX** when the run's underlying is an MCX commodity (CRUDEOIL, CRUDEOILM, NATURALGAS, NATGASMINI, GOLD,
    GOLDM, SILVER, SILVERM) or its price symbol starts `MCX:`. Candles from 09:00 to 23:55 count. MCX closes at
    23:30 while New York is on daylight saving and at 23:55 while it is not (`MarketSessionService`), so on a
    23:30 day there are simply no candles after it. A session counts as complete for the average range with 139
    of its candles, 80% of the shorter (09:00–23:30) session.
  - **NSE** for everything else, BSE included: TrendRider's own 09:15–15:30.
  - `nse` or `mcx` forces one. The parameter is not named `session`: that is the platform's own key in a run's
    parameters (`"session": "recap"` marks a recap run).
- **"Yesterday", the average range, the day's open and the entry window** all follow the market: on MCX yesterday
  is yesterday's 09:00–23:30 (or 23:55) session and the day's open is the open of the 09:00 candle.
- **Entries:** on a candle stamped from `first_entry` to `last_entry`, both included. `auto` is 09:15–14:30 on
  NSE and BSE (V2's) and **09:00–22:30 on MCX**: an hour before MCX's earlier close, as 14:30 is an hour before
  15:30. An `HH:MM` set by the run wins in either market.
- **Exits:** the stop, breakeven step and trail on every call (live, every tick of the price); the day-open exit on
  a closed candle.
- **End of day:** the strategy carries nothing overnight.
  - On NSE and BSE it sells an open position on the first tick at or after `square_off_ist` (V2's, `auto` = 15:15;
    from 20 Nov 2026), so a live run ends the day where a replay does. Before that a live run held until the close.
  - On MCX `auto` adds no time exit: `MarketHoursService` stops the run at its market's close (`MarketCloseRules`),
    23:30 or 23:55 as above ("MCX closed (23:55 IST)"). An `HH:MM` set by the run applies on either market.
  - NSE and BSE close at 15:30 (15:45 from 7 Dec 2026, see [the specs' note](README.md)); the platform stops a live
    run there.
  - A replay squares off at its `eod_square_off_ist`, 15:15 by default; that comes first in a replay, so the
    time exit changes no backtest trade.

## Entry

V2's entry, unchanged, on the market's candles. With `levels: "prev_day"`:

$$
\text{call} \iff C_t > H_{y} \land C_t > O_d \qquad \text{put} \iff C_t < L_{y} \land C_t < O_d
$$

where $C_t$ is the close of the candle that just closed, $H_y$ and $L_y$ yesterday's high and low in the same
market's session, and $O_d$ the open of today's first candle. V2's day filters, one position at a time, the
re-entry only on a new day extreme and `max_entries` apply as in V2.

**What is bought.**

- `instrument_kind: options` (default): the call (or put) `itm_steps` (1) strikes in the money of the run's
  expiry, exactly as V2.
- `instrument_kind: equity`: the share itself, the run's price symbol (`NSE:HDFCBANK-EQ` for HDFCBANK), with
  quantity `lots`, which for a share is a number of shares (the platform's lot size for a share is 1). **Long
  only:** a candle that would have bought the put is skipped and counted in the state's `skipped` as
  `equity_long_only`; the share is never sold short. An index or an MCX run has no share to buy, and
  `instrument_kind: equity` there is refused when the strategy is built, with that reason.

The signal's `metadata.trend_rider` carries V2's levels, the ADR, the entry spot and the exit settings (with
`breakeven_adr`), plus `market` and `instrument_kind`. Its reason starts "Trend Rider V2 BE:".

In words: buy when a 5-minute candle closes beyond yesterday's high (or, for an option, low) and the day's open,
in the run's own market hours.

## Position management

As V2: one group at a time, synced with the run's open groups. The position remembers its entry spot, the ADR at
entry and its best move in favour (`best`, in points of the underlying: index points, rupees of the share or of the
crude future). The breakeven step reads the same `best`.

- **What the run's risk rules add on top.** Leg, group and overall rules apply as for any strategy; none is
  needed, the strategy has its own exits.
- **What the strategy itself never does.** It never sells short, never adds to a position and holds nothing
  overnight by itself (the run's close does that).

## Exit

The first that fires wins:

1. **The run's risk rules** (leg, group, overall).
2. **Index stop:** the underlying has moved `index_stop_adr` (0.5) × ADR against the entry.
3. **Index trail:** once `trail_arm_adr` (0.5) × ADR in favour, a give-back of `trail_giveback_adr` (0.35) × ADR
   from the best.
4. **Breakeven stop (new):** the best move in favour has reached `breakeven_adr` (0.25) × ADR, and the underlying
   is now at or through the entry:
   $$\text{sell} \iff \text{best} \ge b \cdot ADR \land \text{favour}_t \le 0$$
   where favour is the price minus the entry for a call or a share (the entry minus the price for a put) and $b$ is
   `breakeven_adr`. The reason reads "breakeven stop: ... is back at the entry ...". Checked after rules 2 and 3
   on the same call, so those keep their own reasons when they fire together.
5. **The day's open gives way:** a candle closes back across $O_d$ (the reason now names the run's underlying,
   where TrendRider always wrote "NIFTY").
6. **End of day:** `square_off_ist` (15:15 on NSE/BSE by default, none on MCX) and then the market close live; `eod_square_off_ist` in a replay.
7. **The run's stop button.**

## Parameters

V2's parameters all apply (see [TrendRiderV2](TrendRiderV2.md)); this version adds four and changes the default of
two.

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `breakeven_adr` | 0.25 | Move in favour, × ADR, after which the stop is the entry; 0 turns it off (V2 exactly) | Fewer breakeven exits; more trades back to V2's stop or the open | More trades sold flat after a small move |
| `market` | `"auto"` | Whose session the candles are read in: `auto`, `nse` (with BSE) or `mcx` | — | — |
| `instrument_kind` | `"options"` | `options`: V2's ITM CE/PE. `equity`: the share itself, long only, `lots` = shares | — | — |
| `first_entry` | `"auto"` | Earliest candle that may enter; `auto` is 09:15 on NSE/BSE, 09:00 on MCX | Later entries | — |
| `last_entry` | `"auto"` | Last candle that may enter; `auto` is 14:30 on NSE/BSE, 22:30 on MCX | Entries nearer the close | Fewer late entries |

There is no `instrument` parameter: the platform's name for it is `instrument_kind`, which the backtest engine and
the backtest dialog already read (an equity backtest prices the share in shares, with no option chain), so a run
naming `instrument` is refused rather than silently trading options.

## Worked example

**Breakeven, NIFTY options** (illustrative numbers). ADR 300 points; yesterday's high 25,200; today opens at
25,180.

- The 09:25 candle closes at 25,215: above yesterday's high and the open. Buy the call one strike in the money;
  the entry is 25,215.
- V2's stop is at 25,215 − 150 = 25,065; the trail arms at 25,365. The breakeven step arms at
  25,215 + 0.25 × 300 = **25,290**.
- NIFTY reaches 25,310 at 10:20 (best +95, above 75: the step is armed) and falls back. At 25,215 or below, the
  call is **sold** ("breakeven stop"). V2 would have held it to 25,065, to a close below 25,180, or to the close.

**CRUDEOIL options** (illustrative). The run's price symbol is the December future. Yesterday's session ran
09:00–23:55 with a high of 6,040 at 09:05 and a low of 5,960 at 23:40; both count. Today's 09:00 candle opens at
6,030 and closes at 6,045, above 6,040 and above the open: buy the CRUDEOIL call one strike in the money at the
09:00 candle's close. Entries are possible until the 22:30 candle; the run is stopped at the MCX close.

**HDFCBANK shares** (`instrument_kind: equity`, `lots: 50`). A 5-minute close above yesterday's high and the open
buys 50 shares of `NSE:HDFCBANK-EQ`; the stop, the breakeven step and the trail are measured on the share's own
price and its own ADR. A close below yesterday's low and the open buys nothing and counts one `equity_long_only`.

## Limitations

- **Tested on NIFTY options only.** The breakeven step was replayed on NIFTY (development Sep 2024 – Sep 2026:
  +₹1,41,434 against V2's +₹1,26,513, one lot of 65, simple P&L; holdout Aug 2020 – Aug 2024: +₹2,27,698 against
  +₹1,99,160) and reported on BANKNIFTY. Its gain is small, its own exits lose money on their own (development: 56
  exits, −₹48,350; it pays only because V2 sold those trades worse), and one pass out of eleven tries can be luck.
  Stocks, shares and MCX have **not** been replayed: stock option history is patchy, and there is no MCX option
  history at all, so crude can only be tested forward, on paper.
- **MCX.** The ADR and the stop are in rupees of the future; the 0.2% gap filter compares 09:00 with the previous
  23:30/23:55 close. A half session (an MCX holiday with only the evening session) is not complete for the average
  range but still counts as "yesterday". A run trades the nearest option expiry, which on MCX is a few days before
  the future's.
- **Shares.** Long only. The live square-off of an NSE run is the derivatives close, which from 7 Dec 2026 is
  15:45, a quarter of an hour after the share stops trading. The launch dialog shows an option lot beside the
  underlying; on an equity run `lots` is shares. A share without listed options cannot be started (the API and
  the runner both look for its option contracts first).
- The run page's text still describes NIFTY's 09:15 entry for every TrendRider-family run.
- Everything listed in [TrendRiderV2](TrendRiderV2.md#limitations) applies.

## Facts (machine-readable)

```yaml
name: TrendRiderV2BE
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, ticks
instruments: NIFTY, BANKNIFTY, SENSEX, HDFCBANK, ICICIBANK, CRUDEOIL, CRUDEOILM, NATURALGAS, NATGASMINI
default_lots: 1
built_in_exit: true
added: 2026-11-17
spec_version: 1
```
