# SmcStructureBreak

## Idea

Smart Money Concepts reads a chart as a sequence of swing points: a leg runs,
the market takes the liquidity behind its last pullback (the *inducement*), and
then either carries on through the previous swing (a *break of structure*) or
turns back through the level that protected the leg (a *change of character*).
This strategy trades the first of those: on a bullish break it buys the ATM
call, on a bearish break the ATM put, and it closes the position when the
structure turns against it.

The behaviour it is supposed to pay for is follow-through: that a market which
has taken the obvious stops and then broken its last swing keeps going far
enough, fast enough, to cover an option's decay and the costs of trading it.
**On NIFTY it does not.** The measurements are under Limitations, and they are
the reason to read this file before running it: every configuration tested lost
money in every year tested, and the break itself moves the index no further than
an average candle does. The strategy is in the platform so the idea can be
checked rather than argued about.

## Data it needs

| What | Symbol(s) | Resolution | History before the first signal | Where the platform gets it |
|------|-----------|------------|---------------------------------|----------------------------|
| index candles | the run's spot symbol (`NSE:NIFTY50-INDEX`, `NSE:NIFTYBANK-INDEX`, …) | the run's resolution, 5m by default (`timeframe`); plus `bias_timeframe` when a bias is asked for | 150 closed candles (`warmup_bars`): a leg needs a swing, its pullback, the sweep and the break, and no swing is confirmed until a candle takes the liquidity of the one that made it | Backtest: `candles` (backfill). Live: `live_bars` 1m rows aggregated on read |
| ticks | the same spot symbol | every tick | none | ingestor → Redis `market:ticks`; a tick only matters because it closes a candle |
| option quotes | ATM CE and ATM PE of the nearest expiry (`strike_steps` moves them out of the money) | latest quote | none | the runner puts both contracts on the watchlist; the replay prices them from `option_history_bars` |

It never reads option chain OI, and never reads the option's own candles: the
structure is read on the index alone.

## Timeframe

- **Bar:** the run's own candles when they are longer than five minutes
  (`timeframe: "auto"`), else the 5-minute chart. An explicit `timeframe`
  ("5m", "15m", "1D") wins over both.
- **Decides on the close.** The replay is handed closed candles. Live, the
  runner calls `on_bar` on every tick with the newest candle still forming, and
  this strategy holds that candle back until it closes — a swing read on a
  forming candle can disappear. So a live signal fires on the first tick after a
  candle closes, not inside it.
- **Session window:** none of its own. It trades from the first closed candle
  the structure can read to the last.
- **15:30:** `MarketHoursService` squares off every open position at the close,
  and a backtest squares off at `eod_square_off_ist` (15:15 by default). The
  strategy expects that: it does not carry a position overnight, and its own
  exit is not designed to.

## Entry

Let $S_t$ be the structure state after candle $t$ (see
`strategies/market_structure.py`), $T_t \in \{\text{bull}, \text{bear},
\text{none}\}$ its trend, $B_t$ the last confirmed swing in that trend's
direction, and $I_t$ the leg's inducement. A break of structure at candle $t$ is

$$
\text{BOS}_t^{\uparrow} \iff T_{t-1} = \text{bull} \;\land\; \text{taken}(I) \;\land\; C_t > B_{t-1}
$$

where $C_t$ is the candle's close (its high when `break_on` is `"wick"`) and
$\text{taken}(I)$ means some candle since the last break traded through the
inducement, wick included. The bearish mirror uses $C_t < B_{t-1}$.

In words: buy when the market has first taken the stops behind its last
pullback and then closed through its last swing, in the direction the structure
is already pointing.

On $\text{BOS}^{\uparrow}$ the ATM call is bought; on $\text{BOS}^{\downarrow}$
the ATM put. `strike_steps` moves both that many strikes out of the money on the
underlying's own grid. `trade` chooses what counts as a signal: continuations
(`"bos"`, the default), reversals (`"choch"`) or both.

With `entry: "retest"` the break is not bought at once: the level becomes an
order that fills only if price comes back to it within `retest_bars` candles,
which is what the method teaches and which passes up the breaks that never come
back.

## Position management

One position at a time. A second break while a position is open is ignored
rather than doubled up on, and the strategy does not add, roll or average.

**What the run's risk rules add on top.** `parametersJson.risk` still applies:
`leg` (points or percent on the contract), `group` (rupees per signal group) and
`overall` (rupees on the run, per trading day unless `scope: "run"`). The API
guard sweeps every three seconds live; the replay applies the same rules on
every bar. A run with a leg stop-loss and target is the usual way to bound a
single trade — this strategy's own exit only fires on a structure turn, which
can be many candles away.

**What the strategy never does.** It does not hedge, it does not scale, and it
does not read its own P&L: it cannot take profit at a number.

**Where it can lose track.** The replay tells a strategy which groups are still
open (`metadata["open_groups"]`), so when a stop-loss, a target or the
square-off closes the position the strategy sees it at once and is free to take
the next break. The live runner does not pass that yet, so live it drops the
position only when a new session starts: if a run's own risk rule closes the
leg at 11:00, this strategy will not re-enter until the next day. Run it live
without leg rules, or accept that.

## Exit

In order of precedence — the first that fires wins:

1. **The run's risk rules.** Leg, then group, then overall, exactly as the
   engine and the API guard evaluate them.
2. **Structure turned against the position** (`exit_on_turn`, on by default): a
   change of character in the opposite direction closes the group with the
   reason naming the level that gave way. This is the method's own
   invalidation — the level that protected the leg is gone, so the reason to
   hold it is gone.
3. **End of day.** `eod_square_off_ist` in a replay (15:15 by default), and
   `MarketHoursService` at 15:30 live.
4. **The run's stop button**, which flattens everything.

A break in the *same* direction does not exit: the position is already on that
side.

## Parameters

| Name | Default | Meaning | Raise it | Lower it |
|------|---------|---------|----------|----------|
| `timeframe` | `"auto"` | The candles the structure is read on: `"auto"` takes the run's own when they are longer than 5m, else 5m | A longer chart: fewer, larger legs and fewer trades | `"1m"` reads noise as structure |
| `trade` | `"bos"` | Which break is a signal: `"bos"` (continuation), `"choch"` (reversal), `"both"` | `"both"` roughly doubles the trade count | — |
| `entry` | `"break"` | `"break"` buys the candle that broke the level; `"retest"` waits for price to return to it | — | — |
| `retest_bars` | `6` | How many candles a retest may wait before the setup is dropped | More entries, later and further from the level | Fewer entries; many setups expire unfilled |
| `strike_steps` | `0` | Strikes out of the money on the underlying's grid; 0 is ATM | Cheaper premium, lower delta, more of the move needed to pay | — |
| `break_on` | `"close"` | Whether a level is broken by a close or by a wick | `"wick"` fires earlier and more often, on moves that close back inside | — |
| `inducement` | `"last"` | Which pullback must be taken first: the one the leg is on (`"last"`) or the leg's first (`"first"`) | `"first"` is stricter and can stall a whole leg | — |
| `bias` | `"off"` | A higher timeframe as a gate: `"with"` takes only the breaks it agrees with (how SMC teaches multi-timeframe work), `"against"` only the ones it disagrees with | n/a — a choice, and the measurements above say what each did | |
| `bias_timeframe` | `"1D"` | The timeframe the bias is read on, by the same rules | A slower bias that changes less often | A faster one that blocks less |
| `exit_on_turn` | `true` | Close the position when the structure changes character against it | — | `false` leaves the exit entirely to the run's rules and 15:30 |
| `lots` (run parameter) | `default_lots` = 1 | Lots per signal; P&L = points × lots × lot size | Bigger position, same signals | — |

## Worked example

One replay of the 5-minute chart on NIFTY, 1 January to 31 December 2025, one
lot (65), real premiums from `option_history_bars`, 0.5 % slippage a side and
the statutory charges (brokerage ₹20 an order, STT 0.15 % on the sell side,
exchange 0.03503 %, SEBI 0.0001 %, stamp 0.003 % on the buy, GST 18 %):

| | |
|---|---|
| Trades | 400 |
| Won | 148 (37 %) |
| Realised, after slippage | −₹14,714 |
| Charges | ₹25,099 |
| **Net** | **−₹39,813** |

A trader who started the year with ₹2,00,000 and traded one lot on every signal
would have ended it with about ₹1,60,000, having paid ₹25,099 in charges and
given up another ₹26,537 to slippage. The average trade made −₹37 before
charges and −₹100 after them, on a position worth roughly ₹8,000 of premium.

Three of that year's trades, from the run's own ledger (prices include the
0.5 % slippage the replay charges, so the entry is above and the exit below the
candle's own close):

- **2 January 2025, 09:35 IST.** A bullish break bought `NSE:NIFTY2510223800CE`
  at ₹121.50. Nothing turned the structure that day, so the position ran to the
  15:15 square-off and closed at ₹388.60: +267.09 × 65 = **+₹17,361**. The year's
  second-best trade, and the shape the strategy is hoping for.
- **1 February 2025, 11:30 IST.** A bullish break bought
  `NSE:NIFTY2520623600CE` at ₹254.47. The structure never turned, and the
  square-off closed it at ₹99.30: −155.17 × 65 = **−₹10,086**. The year's worst.
- **3 January 2025, 11:05 IST.** `NSE:NIFTY2510924050CE` at ₹163.36, out at
  ₹133.23 on the square-off: −30.13 × 65 = **−₹1,959**. This is the ordinary
  trade — the average loser gave back ₹1,680 and the average winner made ₹2,762,
  which at a 37 % strike rate is −₹36 a trade before charges.

**What actually closed the positions.** 324 of the 400 trades were closed by the
15:15 square-off and only 76 by the strategy's own structure exit — and those 76
lost ₹1,01,914 between them, against the year's total of −₹14,714. The exit that
is supposed to protect the trade is where the money went: a change of character
is confirmed only after a swing has been taken out, by which time the option has
already given back the move.

## Limitations

- **It does not make money on NIFTY, in any year tested.** One lot, real
  premiums, slippage and charges, net of everything:

  | Configuration | 2024 | 2025 | 2026 (to 16 Sep) |
  |---|---|---|---|
  | 5m, BOS, at the break | −₹212,320 | −₹39,813 | −₹80,907 |
  | 5m, BOS, on the retest | −₹171,669 | −₹40,427 | −₹55,597 |
  | 5m, CHoCH | −₹100,587 | −₹108,938 | −₹12,592 |
  | 5m, both | −₹293,299 | −₹156,976 | −₹71,527 |
  | 15m, BOS | −₹79,067 | −₹118,089 | +₹2,334 |
  | 5m, BOS, leg stop 30 % / target 60 % | −₹151,862 | −₹24,439 | −₹99,730 |

  The one positive cell is a single year of 106 trades, which is what a coin
  looks like when it lands the right way; the same configuration lost in the two
  years before it.

- **The higher timeframe does not rescue it, and the way it is taught is the
  worst of the three.** Smart Money Concepts teaches bias from a higher
  timeframe and entry from a lower one, which `bias` implements. On 15-minute
  entries with the daily structure as bias, one lot, real premiums and costs:

  | `bias` | 2024 | 2025 | 2026 (to 16 Sep) | Per trade |
  |---|---|---|---|---|
  | `"off"` | −₹79,067 | −₹118,089 | +₹2,334 | −₹513 |
  | `"with"` (as taught) | −₹45,545 | −₹92,323 | −₹10,974 | −₹855 |
  | `"against"` | −₹34,101 | −₹17,149 | +₹13,308 | −₹188 |

  Taking only the breaks that *agree* with the day is the worst configuration
  measured anywhere in this file.

- **The one thing that looked like an edge did not survive a holdout.** The
  breaks that disagree with the daily structure do move the index: on the
  15-minute chart, four hours later, +19.3 points on average against +1.4 for
  any candle (n=384, t=+2.20), and the same sign in 2024, 2025 and 2026
  separately. Traded as futures rather than options — no premium to decay, a
  flat two points of cost a round trip, out at the session close —
  2024 to 2026 came to **+₹92,580** on one lot.

  Then the same rule was run on 2020-09 to 2023-12, a period untouched until
  that moment: **−₹118,028**, with 2022 alone at −₹204,769. Over all six years:

  | | 2020 | 2021 | 2022 | 2023 | 2024 | 2025 | 2026 |
  |---|---|---|---|---|---|---|---|
  | Net, 1 futures lot | +₹2,047 | +₹93,165 | −₹204,769 | −₹8,472 | +₹1,399 | +₹30,011 | +₹69,116 |

  793 trades, **−₹17,502 in total, −₹22 a trade**. Two good years, one very bad
  one, and nothing in between: that is what noise looks like when it is measured
  long enough. The research that produced these numbers is in
  `private/research/smc/` (`mtf_quality.py`, `futures_proxy.py`).

- **The break itself carries no edge on the index.** Options decay, so a losing
  option strategy can still hide a good signal. This one does not. Measured on
  the index alone, with no option and no costs, over 2024-01-01 to 2026-09-16:

  | Chart | Marks | 12 candles later | Up | Every candle, same horizon |
  |---|---|---|---|---|
  | 5m BOS | 1,210 | +1.39 pts | 49.5 % | +0.36 pts, 50.7 % |
  | 5m CHoCH | 987 | −2.14 pts | 48.3 % | +0.36 pts, 50.7 % |
  | 15m BOS (16 candles) | 402 | +7.48 pts | 53.0 % | +1.44 pts, 50.9 % |

  On a 24,000 index, +7.48 points is 0.03 %, and split by year the 15-minute
  number is +13.07, +1.57 and +4.76 points — $t$ = +0.65, −0.49, +0.88 against
  the move any candle would have given. That is noise, not an edge.

- **The exit reports rather than predicts, and it costs money.** In the 2025 run
  the 76 trades it closed lost ₹1,01,914 while the 324 the square-off closed made
  ₹87,200 between them. A change of character is confirmed only after a swing has
  been taken out, several candles after the turn began, so it sells the bottom of
  the move it is reporting. `exit_on_turn: false` leaves the exit to the run's
  rules and 15:30, which on this evidence is the better of two losing choices.

- **One reading of SMC, not the reading.** Teachers disagree about what a swing
  is, whether a wick breaks a level, and which pullback is the inducement. The
  switches cover the main disagreements and docs/smart-money-concepts.md cites
  the sources, but a different school's rules would give different marks.

- **Backtest versus live.** The replay fills at the candle's close with 0.5 %
  slippage; live fills at the next quote. The replay knows which groups are
  still open and live does not (see Position management). Contracts the broker
  no longer serves are priced from the stored expired-option history, so a
  strike with no stored premium is a skipped entry rather than a trade.

- **NIFTY only, so far.** BANKNIFTY, FINNIFTY and the stock underlyings have not
  been measured with this strategy.

## Facts (machine-readable)

```yaml
name: SmcStructureBreak
category: Directional
evaluates_on: bar
resolution: 5m
data: index candles, ticks
instruments: NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX
default_lots: 1
built_in_exit: true
added: 2026-09-20
spec_version: 1
```
