# Candle pattern alerts

The platform watches live candles and says when a pattern forms on them: a doji
on BANKNIFTY's 15-minute chart, an engulfing candle on a stock on the recording
list, a morning star on CRUDEOIL. Alerts appear on **Markets → Patterns**
(`/markets/patterns`) and, for rules that ask for it, on Telegram. Indicator
crosses (RSI, EMA, Supertrend, VWAP) are watched beside them; see
[Indicator alerts](#indicator-alerts).

## What it reads

The 1-minute bars the API builds from live ticks (`live_bars`), for every symbol
a live feed streams. Those are grouped into 3, 5, 15, 30 or 60-minute candles
aligned to the exchange's open: 09:15 for NSE and BSE, 09:00 for MCX, so NSE's
15-minute candles are 09:15, 09:30 and so on.

A candle counts as **closed** only when its last minute has closed. Only closed
candles raise alerts. The candle still forming is shown on the page as
**forming now**: its OHLC so far, how many of its minutes have passed, and what
it would be if it closed now. It is never sent to Telegram, because a doji at
minute 4 of 15 is often not one at minute 15.

A symbol no feed streams has no bars. The page names it: "no live bars — not
streamed by any feed".

## Patterns

Where the strategies already define a pattern, the alert uses the same
thresholds, so an alert and a strategy never disagree about what a hammer is
(`strategies/indicators.py` in the Python engine).

| Pattern | Rule | Suggests |
| --- | --- | --- |
| Bullish / bearish engulfing | Same thresholds as the strategies | Reversal in the direction of the engulfing candle |
| Hammer | Same thresholds as the strategies (shape only) | Buyers rejected lower prices |
| Shooting star | Same thresholds as the strategies (shape only) | Sellers rejected higher prices |
| Marubozu (bullish / bearish) | Same thresholds as the strategies | One side in control all candle |
| Doji | Body at most 10% of the range | Indecision |
| Dragonfly / gravestone doji | A doji whose upper / lower wick is at most 10% of the range | Rejection of lower / higher prices |
| Hanging man | Hammer shape with the close above the average of the previous 5 closes | Weakness after a rise |
| Inverted hammer | Shooting-star shape with the close below the average of the previous 5 closes | Possible turn after a fall |
| Inside bar | Inside the previous candle, with a smaller, non-zero range | Contraction before a move |
| Morning / evening star | First body at least 50% of its range; middle body at most 30% of the first and beyond its midpoint; third closes past that midpoint | Three-candle reversal |

Candle patterns that span several candles read only candles from the same
session, with no candle missing in between.

## Rules

A rule names symbols (explicit symbols, or a group), timeframes, patterns, and
whether to send Telegram. The groups:
* `indices`;
* index futures;
* stocks on the recording list;
* MCX futures, or one commodity's nearest future (`future:CRUDEOIL`).

Futures are re-resolved to the nearest expiry every scan.

A fresh install starts with four rules, which are then the operator's to edit or
delete:

| Rule | Telegram |
| --- | --- |
| Indices, 15 minutes, every pattern | on |
| Indices, 5 minutes, every pattern | page only |
| Stocks on the recording list, 15 minutes: engulfing, hammer, shooting star, morning and evening star | on |
| CRUDEOIL nearest future, 15 minutes, every pattern | on |

Five-minute index candles are page-only because, replayed over real sessions, they
produced about one message every five minutes all day.

## The scanner

A hosted service in the API, every 20 seconds:
1. Resolves the rules to symbols.
2. Reads today's 1-minute bars for symbols whose exchange is open, and for 3
   minutes after its close.
3. Builds the candles, finds the patterns on the closed ones, and records each
   (symbol, timeframe, candle, pattern) once, stamped with the candle's close. The
   key is unique, so a restart never repeats an alert.
4. Sends Telegram for rules that ask for it. Patterns closing in the same minute
   go out as one message, at most 4 messages in 5 minutes. Candles found late,
   after a restart or when a rule is added, are recorded but not sent.

`PatternAlerts:Enabled=false` turns the scanner off. Telegram uses the platform's
`Telegram:BotToken` and `Telegram:SystemChatId` (from `TELEGRAM_SYSTEM_CHAT_ID`):
patterns are market information, not trades, so they go to the desk's system
channel, and to `Telegram:ChatId` only while no system channel is set.

## API

All admin-only, under `/api/PatternAlerts`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `catalog` | Patterns, groups and timeframes a rule can use |
| GET · POST · PUT · DELETE | `rules` | The rules |
| GET | `events` | Today's alerts, filterable by symbol, timeframe, pattern and direction |
| GET | `forming` | The candle forming now for each watched symbol and timeframe |
| GET | `status` | Last scan, symbols watched, symbols with no bars |

## Indicator alerts

Beside the patterns, a second scanner watches indicators on the same candles:
RSI crossing a level, EMA crosses, Supertrend flips and VWAP crosses. Same live
1-minute bars, same session-aligned candles, same session and holiday gate,
same `alert_events` table (source `indicators`), same Telegram channel (Desk
System). They appear in their own section on **Markets → Patterns**.

### Rules

| Rule | Fires when | Numbers in the alert |
| --- | --- | --- |
| `rsi-above(14,70)` | Wilder's RSI(14) of closes is above 70 at this candle's close and was at or below it at the previous one | RSI before → after |
| `rsi-below(14,30)` | RSI(14) is below 30 and was at or above it | RSI before → after |
| `ema-cross(9,21)` | EMA(9) of closes moves from one side of EMA(21) to the other, either way | both EMAs |
| `supertrend-flip(10,3)` | Supertrend on Wilder's ATR(10), bands 3 ATRs from the median price, changes direction | the band the close went through, the new line |
| `vwap-cross` | The close moves from one side of the session VWAP to the other | VWAP |

The numbers are optional (`rsi-above` is `rsi-above(14,70)`). The formulas are
the strategies' own (`strategies/indicators.py`: `ema` seeded with the SMA of its
first period, Wilder's `rsi`, `supertrend`, `vwap`), ported line for line and
pinned to them by the unit tests, so an alert and a strategy never disagree
about where RSI is.

A tie is not a side: two EMAs that touch and part the same way have not
crossed, and neither has a close that sits exactly on VWAP.

**VWAP needs traded volume.** VWAP is the typical price (high + low + close) / 3
of every 1-minute bar since the open, weighted by its volume. An index
(NIFTY 50, NIFTY BANK, SENSEX) is calculated, not traded, and has no volume, so it
has no VWAP: `vwap-cross` on an index is refused with a warning, never computed
from zeros. It belongs on a future or a stock.

The messages describe what happened on a candle ("RSI(14) crossed above 70"),
never what to do about it.

### The config file

`config/indicator-alerts.txt` (or the file `IndicatorAlerts:ConfigFile` names,
relative to the API's content root). The API re-reads it on every scan when it
has changed, so an edit applies within about twenty seconds, with no restart.

```
cooldown: 30      # minutes; 0 = off
telegram: on      # off = record on the page only
warmup: 250       # candles of earlier sessions read before today's first

# SYMBOLS  TIMEFRAMES  RULE [RULE ...]  [page-only]
NSE:NIFTY50-INDEX,NSE:NIFTYBANK-INDEX,BSE:SENSEX-INDEX  5,15  rsi-above(14,70) rsi-below(14,30) ema-cross(9,21) supertrend-flip(10,3)
# future:NIFTY,future:BANKNIFTY  5,15  rsi-above rsi-below ema-cross supertrend-flip vwap-cross
```

* **Symbols**: comma-separated `EXCHANGE:NAME`, `future:UNDERLYING` for the
  nearest-expiry future (re-resolved every scan, like the pattern groups), or
  one of the pattern groups (`indices`, `index-futures`, …).
* **Timeframes**: 3, 5, 15, 30 or 60 minutes (`5m` works too).
* **`page-only`**: record the line's alerts on the page, never send them. When
  two lines watch the same symbol, timeframe and rule, it is sent if either
  line sends it.
* `#` starts a comment, anywhere on a line.

A line that cannot be read is skipped, and a bad entry on a line (a symbol, a
timeframe, a rule) costs that entry only. The scanner never stops over the file:
it runs on what it could read, and the page lists every warning with its line
number ("Line 7: unknown rule 'macd-cross'; …"). With no file at all nothing is
watched, and the page names the paths it looked in.

The shipped file watches NIFTY, BANKNIFTY and SENSEX spot on 5 and 15 minutes
with the four rules that need no volume, sent to Telegram.

### No repainting

Only **closed** candles are read, 10 seconds behind the clock like the patterns.
The candle still forming is never passed to the indicators, and every value on a
candle is computed from that candle and the ones before it. So an alert, once
recorded, is never contradicted by a later tick: a candle that crosses at
minute 8 and closes back where it started raises nothing, and the scan at 11:00
says exactly what the scan at 10:45:20 said about the 10:30 candle. Each
(symbol, timeframe, candle, rule) is recorded once, under a unique key.

### Warm-up

An EMA or RSI read on today's first candles alone would be wrong for dozens of
candles. So each watch first reads up to `warmup` closed candles from earlier
sessions, built from the stored live 1-minute bars exactly as today's are (each
trading day on its own session window; holidays and weekends skipped; at most
45 days back). A rule then stays quiet until it has read enough candles to have
forgotten where the data began: five periods for RSI and Supertrend (70 and 50
candles at the defaults), four slow periods for the EMAs (84). Crosses among the
earlier sessions' candles feed today's values and are never alerts; a cross
between yesterday's last candle and today's first is an alert on today's first.

The page shows each watch's state: *ready*, *warming up 40 of 84 candles*,
*waiting* (VWAP before anything has traded today) or *skipped* (VWAP on an
index). A fresh install with no stored bars warms up on today's candles alone,
and says so, rather than alerting on an unsettled indicator.

### Cooldown

Per (symbol, timeframe, rule), default 30 minutes. After an alert, the same
rule on the same symbol and timeframe is recorded on the page but not sent until
the cooldown has passed since that alert's candle closed; the next one after
that is sent and starts a new window. Measured from the last alert, not from the
last cross, so a choppy market still gets one message per window rather than
none. Direction does not matter: an EMA crossing up, down and up again inside
30 minutes is one message. Only an alert that reached Telegram (or is about to)
starts a window: one found late, page-only, sent with `telegram: off`, dropped
by the rate limit or refused by Telegram does not, so the next cross is sent.
Until 28 Sep every alert started one, and an API down across a close heard of
neither that cross nor the one back. The cooldown is worked out from the day's
candles and the stored alerts on every scan, so a restart decides every alert
exactly as the first run did.

### Market hours and delivery

Scanned only while the symbol's exchange is in session (the platform's session
and holiday calendar), and for 3 minutes after the close for the candle that
closes with the session. Alerts on candles that closed at the same minute go out
as one Telegram message, one line per symbol and candle with every rule that
fired on it, at most 4 messages in 5 minutes:

```
Indicator alerts · candles closed 10:45 IST
BANKNIFTY 15m at 10:30 IST — close 57,214 · RSI(14) crossed above 70 (RSI 68.4 → 71.2) · EMA(9) crossed above EMA(21) (EMA(9) 57,190.46, EMA(21) 57,188.10)
```

As with patterns, an alert found late (after a restart) is recorded but not
sent. Each recorded alert says why it was not sent when it was not: cooldown,
page-only, `telegram: off`, or found late.

`IndicatorAlerts:Enabled=false` turns this scanner off; when it is not set it
follows `PatternAlerts:Enabled`, so the one switch a second API on the same
database already sets turns both off.

### API

Admin-only, under `/api/PatternAlerts`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `indicators` | The config as read (settings, lines, warnings, the rule catalog), each watch's warm-up state, the scanner's health |
| GET | `indicators/events` | Today's indicator alerts, filterable by symbol and timeframe |

## Not built yet

* **Chart patterns** such as head and shoulders. They need swing-point detection
  and a template matcher on top of these same candles.
* **Price-level alerts** (a breakout above a level of your choosing). They can
  reuse the indicator scanner and its config file.
