# SignalBuilder

## Idea

A strategy written in the console rather than in Python.

The operator lists the conditions that make a long setup, and the ones that
make a short one. On every closed bar the strategy checks them: when all of a
side's conditions hold — and the other side's do not — it emits that side's
signal. Nothing else.

That "nothing else" is the point. When it may trade, how often, where its stop
moves, which strike a plain BUY takes and what a fill costs are the **run's own
rules** (`backtest/rules.py`, `strategies/signal_filters.py`), configured beside
the setup on the New backtest page. So the same setup can be tried under
different rules without editing the setup, and two runs of it stay comparable.

It exists because most ideas a trader wants to test are a handful of conditions
on one chart — "above VWAP, above the 9 EMA, a strong candle, RSI crossing 60" —
and writing a Python class for each of them buries the idea in plumbing.

This is a tool for testing ideas, not an idea. An empty side never trades.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index or stock candles | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:RELIANCE-EQ`, …) | the run's own (`resolution`, default 5m) | enough bars for the longest indicator named in the conditions (an EMA of 21 needs 21, ADX needs 29, Supertrend 12) | the candles table: broker history, the Dhan index history, or the stock importer |
| option premiums | the ATM contract the engine picks for a BUY/SELL | the run's own | — | stored history, or FYERS for contracts that still exist |

On an equity run (`instrument_kind: equity`) there are no option premiums: the
run trades the instrument itself, in shares.

## Timeframe

- **Evaluates on:** the last closed bar of the run's resolution, once per bar.
  In a live run the newest bar is still forming and is dropped; in a replay the
  engine only ever hands over closed bars.
- **Warm-up:** the engine feeds the strategy the stored bars before the range,
  so the indicators are warm on the first signalled bar.

## Entry

A side fires when **every** condition on that side holds on the signal bar.

    close>vwap            closed above the session VWAP (needs traded volume)
    close>ema:9           above the 9 EMA — any period, and `<` for below
    ema:9>ema:21          the fast EMA above the slow one
    rsi>60 / rsi<40       RSI(14) level
    rsi_cross_up:60       RSI crossed 60 on this very bar (the momentum spike)
    rsi_cross_down:40     the same downwards
    body>=0.5             the candle's body is at least half its range
    green / red           closed up / closed down
    supertrend:bullish    Supertrend(10,3) direction
    adx>20                trend strength, whichever way it points
    break_high:15         closed above the first 15 minutes' high (break_low:15 for the low)
    above_open / below_open   against the session's own open
    gap>0.5               the session gapped more than 0.5%
    volume_z>1.5          volume z-score of the bar (an index carries none)

Both sides holding on the same bar is a contradiction, and the strategy stands
aside. A condition whose input cannot be computed yet — an EMA before its
period, VWAP on an index with no volume — counts as false: a setup that cannot
be checked has not happened.

An unreadable condition fails when the run starts, not on the bar it would
first have been checked.

## Position management

None of its own. The signal is a plain BUY or SELL:

- **Options run:** the engine opens the ATM call for a BUY and the ATM put for a
  SELL, in the run's lots. The run's `contract` rules can move that strike in or
  out of the money, or flip the side.
- **Equity run:** the engine buys (or short-sells) the instrument itself, in the
  run's share count.

Everything after the entry — the leg stop and target, the moving stop, the time
exit, the day's limits, the end-of-day square-off — belongs to the run.

## Exit

No exit of its own. A run with no exits holds until the end-of-day square-off,
which is what makes an unfiltered first run easy to read: whatever the P&L is,
it is the setup's, not an exit rule's.

## Parameters

| Parameter | Default | What it does |
|---|---|---|
| `long_conditions` | `close>vwap, close>ema:9, body>=0.5, rsi_cross_up:60` | every condition that must hold for a BUY |
| `short_conditions` | `close<vwap, close<ema:9, body>=0.5, rsi_cross_down:40` | the same for a SELL |
| `resolution` | `5m` | the series the conditions are judged on |
| `instrument_kind` | `options` | `equity` trades the instrument itself |
| `explain_every_bar` | `false` | prints which condition failed, when it changes |

## Worked example

NIFTY, 5-minute, 2 lots, the defaults above, with the run's rules set to two
windows (09:20–11:00 and 13:00–15:15), one trade a day, a 12% leg stop and the
stop moving to entry after +15%.

- **09:45 IST.** The 5-minute candle closes at 23,412: above the session VWAP
  (23,388) and above the 9 EMA (23,401); its body is 62% of its range; RSI(14)
  went 57.8 → 61.4, crossing 60 on this bar. All four hold, none of the short
  ones do.
- The engine buys the ATM 23,400 CE at its close, 2 lots.
- **10:20.** The premium is 15% up; the run's exit moves the stop to entry.
- **11:35.** The premium falls back through entry and the position is closed —
  the entry stop, not the 12% one.
- **13:20.** Another long setup forms, and the day's limit refuses it: one trade
  a day. The refusal is listed in the run's skipped entries.

## Limitations

- **A setup is not an edge.** The defaults above were tested on NIFTY 1-minute
  and 5-minute data in Sep 2026 (`private/research/vwap-ema-rsi/`): before costs
  roughly break-even, after costs a steady loss in every version. Write your own
  conditions and judge them on the results, not on the fact that the platform
  can run them.
- **VWAP needs volume.** An index carries none, so `close>vwap` is false on an
  index until the platform stores a volume series for it. It works on stocks.
- **One leg.** A signal is one contract or one stock; spreads and straddles are
  other strategies.
- **No pyramiding of its own.** A second signal while a position is open opens a
  second position unless the run limits open positions.
- **Conditions are ANDed.** There is no OR and no bracketing; two different
  setups are two runs.

## Facts (machine-readable)

```yaml
name: SignalBuilder
category: Custom
evaluates_on: bar
resolution: 5m
data: index or stock candles, option premiums (options runs)
instruments: NIFTY, BANKNIFTY, SENSEX, FINNIFTY, MIDCPNIFTY, any stock with stored candles
default_lots: 1
built_in_exit: false
added: 2026-09-18
spec_version: 1
```
