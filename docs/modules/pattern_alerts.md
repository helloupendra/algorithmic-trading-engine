# Candle pattern alerts

The platform watches live candles and says when a pattern forms on them: a doji
on BANKNIFTY's 15-minute chart, an engulfing candle on a stock on the recording
list, a morning star on CRUDEOIL. Alerts appear on **Data → Pattern alerts**
(`/admin/data/patterns`) and, for rules that ask for it, on Telegram.

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
`Telegram:BotToken` and `Telegram:ChatId`.

## API

All admin-only, under `/api/PatternAlerts`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `catalog` | Patterns, groups and timeframes a rule can use |
| GET · POST · PUT · DELETE | `rules` | The rules |
| GET | `events` | Today's alerts, filterable by symbol, timeframe, pattern and direction |
| GET | `forming` | The candle forming now for each watched symbol and timeframe |
| GET | `status` | Last scan, symbols watched, symbols with no bars |

## Not built yet

* **Chart patterns** such as head and shoulders. They need swing-point detection
  and a template matcher on top of these same candles.
* **Indicator alerts** (a breakout, an RSI cross). They can reuse this scanner,
  the rules table and the Telegram path.
