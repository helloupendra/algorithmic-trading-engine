# Market structure (Smart Money Concepts)

Data → **Market structure** draws a candle chart with the marks Smart Money
Concepts teaches: swing points labelled **HH / HL / LH / LL**, a line from every
broken level to the candle that broke it (**BOS** or **CHoCH**), and the
**inducement (IDM)** each leg has to take before a break counts.

The marks are read on the server, in
`src/AlgoTrading.Infrastructure/Smc/MarketStructure.cs`, and served with the
candles they were read from by `GET /api/Smc/structure`, so a chart can never
draw a mark against a different series than it was computed on.

> **SMC is taught, not specified.** Its terms are used differently by different
> teachers, and the popular indicators disagree with each other. What follows is
> one reading, stated exactly, with the choices marked where the schools split.
> None of it is a claim that these marks predict anything.

## The rules

1. **A swing point** stands when a later candle takes the liquidity of the
   candle that made it: after a high, a candle trading below that candle's low;
   after a low, a candle trading above its high. This is the "valid pullback"
   rule — the impulse is over and a correction has begun. Highs and lows
   alternate by construction.
   *Alternative:* `method=fractal` uses the older reading — a candle higher than
   the `strength` candles on each side. It is steadier on a noisy one-minute
   chart, but it cannot mark a swing until `strength` candles have passed.
2. **Labels** compare each swing with the previous swing of its own kind: higher
   high, lower high, higher low, lower low. The first high and the first low
   carry no label, because there is nothing to compare them with. An exactly
   equal high reads as a lower high (the strict comparison), which is why a
   range of equal highs shows as LH.
3. **The inducement** is a pullback inside the current leg — in a rally, a swing
   low above the protected low. It is **taken** when a candle trades through it,
   wick included. Once a leg has been induced it stays induced until the next
   break.
   *Choice:* `inducement=last` (the default) takes the pullback the leg is on
   now, which is what the widely used indicators do. `inducement=first` keeps
   the leg's first pullback, which is how the concept is usually taught — the
   stricter reading, and on a strong trend it stalls: BANKNIFTY's fall of March
   2026 never traded back above its first pullback, so the structure sat
   unbroken for six months.
4. **A break of structure (BOS)** is a close through the last confirmed swing in
   the trend's direction — but only once the leg's inducement has been taken.
   That gate is the point of the inducement: the market takes the stops behind
   the pullback before it carries on, and without the gate a trend label flips on
   every minor poke. After a BOS the leg needs a fresh pullback, taken again,
   before the next one counts.
5. **A change of character (CHoCH)** is a close through the level that protected
   the trend — the pullback the last break came out of. The trend turns there.
   The first break of all, before any trend is known, is recorded as a BOS.
6. **A wick is not a break** unless the chart is set to `breakOn=wick`. A break
   needs a close; taking an inducement needs only a wick. That asymmetry is
   deliberate and is what the teaching says.

## What is drawn

| Mark | How it looks | Where it runs |
|---|---|---|
| Swing point | A dot at the candle, with its label above a high or below a low | — |
| BOS | A solid line in the trend's colour | From the swing that made the level to the candle that closed through it |
| CHoCH | A dashed line in the new trend's colour | The same |
| IDM | A grey dotted line | From the pullback to the candle that took it, or to the right edge while it still stands |

Only the swings the structure turned on are labelled; **Minor** adds the
pullbacks. A pullback the market never came back for is not drawn: the chart
shows the inducements that were taken and the one still standing.

## No mark moves once it is drawn

Every swing carries the candle that confirmed it, and the reader only acts on a
swing from that candle on. Reading a prefix of the candles gives exactly the
marks that reading all of them gives for that prefix — there is a test for it
(`Nothing_is_marked_before_the_candle_that_confirmed_it`). This is not true of
every public implementation: the widely used Python port searches for a break
from the swing's own bar rather than from the bar that confirmed it, so it can
report a break that could not have been known at the time.

The cost of that honesty is a lag: a swing high is only a swing once the
correction has taken the liquidity of the candle that made it, which can be
several candles later.

## Reading it yourself

```sh
curl -s "$API/api/Smc/structure?symbol=NSE:NIFTYBANK-INDEX&resolution=15m&fromDate=2026-08-01" \
  -H "Authorization: Bearer $TOKEN"
```

| Parameter | Default | Meaning |
|---|---|---|
| `symbol` | — | Broker symbol, for example `NSE:NIFTYBANK-INDEX` |
| `resolution` | `5m` | Any spelling: `1`, `5m`, `15`, `1D` |
| `fromDate`, `toDate` | what is stored | IST trading dates |
| `method` | `validPullback` | or `fractal` |
| `strength` | `2` | Candles either side of a fractal swing; ignored by the pullback rule |
| `breakOn` | `close` | or `wick` |
| `inducement` | `last` | or `first` |
| `includeLive` | `true` | Append today's live bars past the stored history (intraday only) |

The response carries the candles, the swings (with `major` for the ones the
structure turned on, and `confirmedTimeUtc`), the events, the inducements, and
the state at the last candle: `trend`, `protectedLevel` (breaking it turns the
trend), `breakLevel` (breaking it, once induced, is a BOS), `inducementTaken`
and `inducementLevel`.

## Where the schools differ

Researched from the source of the most-copied implementations and the teaching
that names these terms, September 2026:

- **Swing detection.** LuxAlgo's *Smart Money Concepts* and *Market Structure
  with Inducements & Sweeps* use a one-sided leg state machine (a bar higher
  than the next `size` bars); the inducement teaching uses the valid-pullback
  rule this module defaults to; a widely used Python port uses a centred window
  with retroactive de-duplication, which rewrites history.
- **The BOS level.** Some read it as the last confirmed swing (this module, and
  LuxAlgo's SMC script); LuxAlgo's inducement script uses the running extreme
  since the trend turned, which on daily data can leave a leg unbroken for
  months.
- **The first pullback or the current one** as the inducement — see rule 3.
- **Line style.** LuxAlgo's SMC script uses solid for swing structure and dashed
  for internal structure; its inducement script uses solid for BOS and dashed
  for CHoCH. This module follows the second, because it shows one structure
  series rather than two.

Sources read for this module: the Pine source of *Smart Money Concepts
[LuxAlgo]* and *Market Structure with Inducements & Sweeps [LuxAlgo]*, *Market
Structure Event Trend* (AustrianTradingMachine), the `smart-money-concepts`
Python package, MQL5's *Swing Detector by Pullback (SMC)*, and the written
teaching at dailypriceaction, innercircletrader and liquidityscan. None is a
standards body; they are the most copied, not the most correct.

## Several timeframes at once

`GET /api/Smc/ladder?symbol=…&timeframes=1D,15m,5m` reads the same structure on
each timeframe listed, highest first, and returns the last one with its candles
and the ones above it as state and marks. Each is read only from its own closed
candles, so a daily level shown on a five-minute chart is one the day had
already set — the page cannot show a level the market did not know yet.

This is the nested reading the method works from: a day's pullback is an hour's
whole trend, and an hour's pullback is five minutes' whole trend. The three can
disagree without contradicting each other — on 18 September 2026 BANKNIFTY read
bearish on the day (protecting 58,012), bullish on the 15-minute chart
(protecting 56,073) and bullish on the 5-minute one.

The console draws it as a ladder above the chart, one rung per timeframe, and
puts each higher timeframe's protected and break levels on the chart as price
lines.

Whether trading that alignment pays is a separate question, and the answer on
NIFTY is in [docs/strategies/SmcStructureBreak.md](strategies/SmcStructureBreak.md):
taking only the breaks that agree with the day was the worst configuration
measured, and the one that looked like an edge did not survive a holdout.

## Not built yet

Equal highs and lows (EQH/EQL) with a tolerance, order blocks, fair-value gaps,
premium/discount zones, a second coarser structure series for "internal versus
swing" structure, and alerts when a level breaks. The structure is available to
strategies through the same endpoint.
