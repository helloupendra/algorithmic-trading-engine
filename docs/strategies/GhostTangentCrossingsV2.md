# GhostTangentCrossingsV2

Source: `strategies/ghost_tangent_crossings_v2.py` (`GhostTangentCrossingsV2Strategy`),
a subclass of `GhostTangentCrossingsStrategy`. The geometry (pivots, legs, the
quarter-ellipse, its tangent and the crossing scan) is inherited unchanged and
documented in [GhostTangentCrossings](GhostTangentCrossings.md); this page
documents what V2 changes. Every rule below is read from the code.

## Idea

The original's bet, kept: a close through the ellipse-tangent line of the last
swing says the swing has lost its pace and will reverse far enough for a
long option to pay. V2 exists to test that bet without the faults the
original's spec lists: stale and pre-open signals, a line that moves away
without bound, stacked positions and no exit. Replayed offline on recorded
option premiums on 5 Nov 2026, it loses money, as the original does (see
Limitations). It can be run beside the original, one fix switched on or off
at a time.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, `BSE:SENSEX-INDEX`, …) | 5m (or the run's own when longer, as the original) | $2F+1$ closed in-session candles; warm-up $4F$ bars, as the original | backfill (`candles`) in a backtest; ingestor (`live_bars`) live |
| ticks | the same spot symbol | every tick | none | ingestor → Redis stream `market:ticks` |
| option quotes | ATM CE and ATM PE of the nearest expiry (`get_contract_requirements` default) | latest quote | none | the runner's watchlist live; `option_history_bars` / FYERS history in a backtest |

## Timeframe

- **Bar:** 5-minute index candles, as the original.
- **Closed candles only.** Live candles are checked against the tick's as-of
  time and chart duration. A forming candle is excluded; a final candle that
  has already closed is retained even when no forming candle follows it.
  Warm-up uses its actual cutoff and cannot submit orders. Daily candles are
  complete at the cash close. A replay supplies already closed candles.
- **Configured timeframe.** An explicit `timeframe` also changes the live data
  request. `auto` uses 5m live and the longer run chart in backtests.
- **Continuous reading.** Each unseen closed candle advances the geometry once,
  including candles arriving together after reconnect. A bounded 1,024-candle
  buffer survives checkpoint reloads and rolling API windows. Intermediate
  catch-up candles update pivots and check exits; only the newest candle can
  enter. Old checkpoints or a lost history window rebuild geometry while
  retaining exposure, and suppress entries during the rebuild. Invalid OHLC
  and unreadable timestamps are excluded.
- **Session candles only.** On an intraday chart, candles stamped before
  09:15 IST or from 15:30 IST (`core.market_hours.SESSION_OPEN` /
  `CASH_CLOSE`) are dropped before the geometry sees them: the ingestor's
  pre-open prints became pivots in the original (the 09:00 bucket of
  2026-09-08 and 2026-09-09). A daily chart is not cut (its candles are
  stamped 05:30 IST).
- **Session window:** none of its own; the run's `limits` windows apply in a
  backtest (see Exit).
- **Close:** the platform's close square-off (15:30; 15:45 for options from
  7 Dec 2026) and the backtest's `eod_square_off_ist` close what is open. V2
  also has its own exits (see Exit).

## Entry

Notation as in the original's spec: bars $1,\dots,n$, $n$ the newest closed
candle; a leg from the earlier pivot $A=(x_A,y_A)$ to the later
$B=(x_B,y_B)$; the trigger line $\ell(x)$; the crossing bar $x^*$, the first
bar from $x_B$ whose close is on the far side of $\ell$.

A crossing the original would signal is traded only if every gate passes:

$$
\underbrace{x_B - x_A \ge L_{\min}}_{\texttt{min\_leg\_bars}}
\ \land\
\underbrace{n - x^* \le G}_{\texttt{max\_signal\_age\_bars}}
\ \land\
\underbrace{x^* - x_B \le \lambda\,(x_B - x_A)}_{\texttt{line\_life}}
$$

with a gate switched off when its parameter is 0. Crossing timestamps are
consumed only after an actionable or deliberately ignored decision, not during
geometry detection. Missing contracts can be retried while the candidate stays
fresh. The consumed timestamp survives restarts, so an exited trade cannot be
reopened from the same crossing. Opposing candidates are both examined before
the conflict decision.

Then, on the signals of one call:

- if they point both ways (one candle crossed both lines), nothing is traded;
- a SELL (after an up-leg) buys the ATM PE, a BUY (after a down-leg) the ATM
  CE: one `OPEN_GROUP`, one leg of `lots`, group id
  `GTC2_<tick stamp>_<n>`. V2 emits the `OPEN_GROUP` itself, so the runner's
  `GhostTangentCrossings` branch and the backtest's bare BUY/SELL conversion
  are not used. Non-default `contract.strike_offset` / `contract.flip` settings
  are rejected explicitly instead of silently backtesting ATM contracts.
- A crossing that is back across its trigger, or whose pivot stop has already
  been breached by the latest close/live spot, cannot enter. A pivot-stop exit
  cannot reopen a position on the same candle.
- Live entries require a known, flat run book (or a preceding confirmed close
  in the batch). An untracked open group blocks entries. Data older than one
  chart interval after the last candle's close also blocks new entries.
- Missing, expired, wrong-underlying or wrong-option-side contracts cannot enter.

In words: trade the break only while it is happening, only on a swing of at
least three candles, and only while the line is still near the swing it was
drawn from.

Each `OPEN_GROUP` carries `metadata.ghost`: `kind` (ghost / confirmed), `leg`
(up / down), `a` and `b` (bar and price), `leg_bars`, `leg_points`,
`tangent`, `slope`, `cross_bar`, `cross_time`, `cross_close`,
`trigger_at_cross`, `trigger_now`, `newest_bar`, `signal_age_bars`. This is
what the original does not persist (its Limitations); with it a run can be
analysed from `simulation_signals.MetadataJson` alone.

## Position management

One position at a time, kept in `state["position"]` (group, direction,
symbol, stop level, entry candle, IST day):

- a signal in the held direction is ignored (counted `same_direction`);
- an opposite signal follows `on_opposite`: `"reverse"` sends a
  `CLOSE_GROUP` for the held group, then the new `OPEN_GROUP`; `"close"`
  only closes; `"ignore"` keeps the position;
- it never adds, rolls or adjusts.

**What the run's rules add.** Everything the original relies on still
applies: `parametersJson.risk` (`leg`, `group`, `overall`), and in a backtest
the run rules of `backtest/rules.py` — `exits` (`time_exit_minutes`,
breakeven, trailing, indicator exits), `limits` (trade windows,
`max_trades_per_day`, cooldowns), `costs` (slippage and statutory charges).
V2 does not reimplement any of them.

**Knowing the position is gone.** Replay and live both provide
`metadata.open_groups`. The live runner polls this run's positions at most once
every five seconds and invalidates the cache after its own booked orders.
Risk/manual exits clear held state on the next successful refresh. An API
failure means unknown, never flat; known exits remain available and new entries
wait. A change of date alone never clears exposure. Carried rows transferred to
the manual book are no longer open in this run.

**Booking failures.** The existing runner sends a reversal's close before its
open. A refused or unanswered close restores the strategy state and prevents
the following open. This behavior is covered with V2-specific regression tests.

## Exit

In a backtest, in the order the engine applies them on each candle
(`backtest/engine.py`, the driver loop):

1. The end-of-day square-off (`eod_square_off_ist`), once its time has come.
2. The run's leg, exit and group rules, then the overall rule.
3. V2's own, on the strategy's call for that candle:
   - **Pivot stop** (`stop_at_pivot`): a candle that closed after the entry's
     own closes beyond the later pivot of the entry's leg — below $y_B$ for a
     call, above it for a put. The swing the trade bet against is still
     going. Reason: "Closed … below the leg's later pivot …".
   - **Opposite signal** (`on_opposite` = `"reverse"` or `"close"`).
4. The run's rules again, on the marks after the strategy's orders.

Live, the API's risk guard (every 3 s), a manual square-off and the close
square-off close positions on their own clocks; V2's exits come with the
tick that reads the closing candle.

## Parameters

The original's parameters (`pivot_forward`, `pivot_type`,
`use_ghost_signals`, `timeframe`, `smc_filter`) keep their defaults and
meaning; see its spec. V2 adds:

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `max_signal_age_bars` | 1 | $G$: closed candles between the crossing and the newest one; 0 = any age (the original) | Older crossings traded, at a premium that has already moved | Only breaks on the newest candle; confirmed legs (found $F$ bars late) mostly drop out |
| `line_life` | 1.0 | $\lambda$: how far past $B$ a crossing may come, in leg lengths; 0 = no limit | Late "breaks" of a line that has run away from price are traded | Only crossings close to the swing |
| `min_leg_bars` | 3 | $L_{\min}$: the shortest leg traded; 0 = every leg | Fewer, larger swings | One- and two-bar legs (degenerate lines) come back |
| `stop_at_pivot` | `true` | close on a candle closing beyond $y_B$ | n/a | `false`: no own stop, only the run's rules |
| `on_opposite` | `"reverse"` | `"reverse"`, `"close"` or `"ignore"` | n/a — a choice | |
| `lots` (run parameter) | `default_lots` = 1 | lots per signal | | |

Construction rejects fractional/negative counts, non-finite or negative
`line_life`, unknown enum values and unsupported timeframes. `pivot_forward`
accepts 1–250; `lots` 1–10,000; age/leg count 0–10,000. Booleans must be actual
booleans or `"true"`/`"false"`; the string `"false"` does not enable a stop.
Zero age still means **no age limit**, preserving existing runs' semantics.

## Worked example

No audited production example is recorded here. The offline
replay of 5 Nov 2026 has not been walked through. A representative run should be
walked through here, signal by signal, with the
template's SQL (docs/strategies/README.md); `MetadataJson.ghost` now carries
the line and crossing values that the original's example had to rebuild from
`live_bars`.

What the tests pin, on their own candles (`tests/test_ghost_v2.py`): an
up-leg from a low of 100 at bar 1 to a high of 160 at bar 7, every close after
bar 7 above the rising line until bar 9 closes 5 points under it. With bar 9
the newest candle the crossing is 0 bars old and V2 buys the put, with
`metadata.ghost` = `{"leg": "up", "a": {"bar": 1, "price": 100.0}, "b": {"bar":
7, "price": 160.0}, "leg_bars": 6, "cross_bar": 9, "signal_age_bars": 0, …}`.
With bar 12 the newest, the same crossing is 3 bars old: the original trades
it, V2 drops it as stale.

## Limitations

- **The previous implementation lost money in historical replay.** Replayed on 5 Nov 2026 with recorded option premiums,
  1 Jan 2025 to 11 Sep 2026, NIFTY, BANKNIFTY and SENSEX together, 2 lots,
  0.5% slippage plus charges and a 20-point target: the original lost
  ₹24,27,695 and V2 ₹15,52,867. V2 loses less only because it trades less;
  per trade it lost ₹489 against the original's ₹456. Those results predate the
  9 Nov execution fixes. Regression tests do not establish profitability; no
  new out-of-sample profitability claim is made.
- **The pivot stop is a close beyond a wick extreme** (`pivot_type` =
  `"Wick"`): tight on a short leg, and checked on closed 5-minute candles,
  so the fill can be well past the level.
- **Ghost legs move.** On a ghost leg $B$ is the provisional extreme; a new
  extreme after the entry is exactly what trips the stop, so ghost entries
  can be stopped quickly in a trend that keeps extending.
- **The backtest's `contract` rules do not apply** (`strike_offset`, `flip`):
  V2 buys ATM options and now refuses incompatible settings.
- **Regular cash sessions only.** The geometry filters 09:15–15:30 IST;
  special evening sessions require a calendar-aware strategy extension.
- **No recovered position details, no new exposure.** If the book contains a
  group absent from a recovered checkpoint, entries wait rather than guessing
  its stop or direction. API risk rules still manage that book.
- **No automatic research promotion.** Slippage, liquidity, gaps, long-option
  decay and ghost-leg instability still require walk-forward evaluation with
  recorded option prices. This change does not tune defaults to old results.

### Execution regression coverage (9 Nov 2026)

`tests/test_ghost_v2.py`, `tests/test_ghost_v2_execution.py` and the live runner
harness cover candle closure, warm-up, catch-up, rolling/recovered state,
malformed configuration/data, candidate consumption, invalidated entries,
external exits, unavailable/untracked books, expired contracts, and reversal
booking failures. The synthetic replay still enforces one open group at a time.

## Facts (machine-readable)

```yaml
name: GhostTangentCrossingsV2
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, ticks
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: true
added: 2026-11-04
spec_version: 2
```
