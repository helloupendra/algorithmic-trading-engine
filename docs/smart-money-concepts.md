# Market structure (Smart Money Concepts)

Data → **Market structure** draws a candle chart with the marks Smart Money
Concepts teaches: swing points labelled **HH / HL / LH / LL**, a line from every
broken level to the candle that broke it (**BOS** or **CHoCH**), and the
**inducement (IDM)** each leg has to take before a break counts. Three further
marks read the same candles as areas rather than levels: **order blocks**,
**fair-value gaps (FVG)**, and the **order-flow** runs each reading held for.

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
7. **An order block** is the last candle that closed against the move, taken
   from the stretch between the swing that made the broken level and the candle
   *before* the one that broke it: for a bullish break the last down-close
   candle, for a bearish break the last up-close one. The zone is that candle's
   whole range, wick to wick. It is knowable at the break and not before — until
   the break fired, those were only candles. If the stretch holds no opposing
   candle at all, nothing is drawn: saying nothing beats inventing a zone.
   *Note:* the break candle is excluded deliberately. The teaching asks for the
   last opposing candle *before* the move, and the break candle is part of the
   move — nothing stops it closing against its own break's direction while
   breaking, and the block would then be a range price has just finished trading
   through. It is not a rare shape: on a month of 5-minute NIFTYBANK candles it
   was 42 of 212 blocks under `breakOn=wick`, and 5 of 149 under `breakOn=close`,
   where it takes a gap open that gives part of itself back.
8. **A fair-value gap** is a band three candles leave untouched. Reading candles
   *i−2*, *i−1* and *i*: a bullish gap where candle *i*'s low is above candle
   *i−2*'s high, a bearish gap where candle *i*'s high is below candle *i−2*'s
   low. The middle candle is the one that ran through the band, and its body is
   not asked to agree. The gap needs candle *i*'s own extreme, so it is knowable
   at that candle and at no earlier one.
   *Session boundaries:* on an intraday chart the three candles have to be
   contiguous in time. The rows outside 09:15-15:30 are dropped before the
   reader sees them, so yesterday's last candle sits beside this morning's
   first, and a reader that did not know would call every gap open a fair-value
   gap. The reader is told how long a bar is and refuses any trio spanning more
   than two of them. Daily candles are read without that test, because there the
   overnight gap *is* the fair-value gap and filtering it away would be the
   error.
9. **A zone is used** when price comes back into it: `zones=touch` (the default)
   takes a wick into the near edge, `zones=midpoint` a wick through the zone's
   50% level (ICT's consequent encroachment), `zones=close` a candle closing
   beyond the far side. A zone is entered from the side the impulse left it on —
   a bullish one sits below price and is come back down into, a bearish one sits
   above — so the near edge is the top of the first and the bottom of the
   second. The candle that used it is stamped once and never unstamped, and a
   zone is only tested from the candle after the one that confirmed it: price is
   still inside an order block while the impulse is leaving it, and a gap's near
   edge is drawn by the very wick that confirms the gap.
10. **Order flow** here is the narrative reading — which way the market is being
    delivered — and **not** measured flow. A run starts at the candle that fired
    a BOS or CHoCH, carries that break's direction, and ends at the next break.
    It carries one more stamped fact: the candle that swept that leg's
    inducement, which is where a break of structure became armed. Nothing new is
    measured. What is new is that the reading is drawn along time, so when the
    bias changed and how long it held are visible, instead of only its value at
    the last candle.

## What is drawn

| Mark | How it looks | Where it runs |
|---|---|---|
| Swing point | A dot at the candle, with its label above a high or below a low | — |
| BOS | A solid line in the trend's colour | From the swing that made the level to the candle that closed through it |
| CHoCH | A dashed line in the new trend's colour | The same |
| IDM | A grey dotted line | From the pullback to the candle that took it, or to the right edge while it still stands |
| Order block | A filled box in the break's colour, behind the candles | From the block's own candle to the candle that came back for it, or to the right edge while it still stands |
| Fair-value gap | A lighter filled box in the move's colour | From the first of the three candles to the candle that filled it, or to the right edge while it stands open |
| Order flow | A thin band along the foot of the pane, faint until the leg's inducement is swept and solid after | From the break that started the run to the break that ended it, or to the right edge while it runs |

Only the swings the structure turned on are labelled; **Minor** adds the
pullbacks. A pullback the market never came back for is not drawn: the chart
shows the inducements that were taken and the one still standing.

Each of the three zone layers has its own toggle. The gap layer shows the
unfilled gaps by default: every fast three-bar move prints one, most fill within
a few candles, and drawing them all buries the candles behind them. That is a
choice about what is legible, not a rule — the filled ones are in the response
either way, and one toggle brings them back.

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

**The order block pays that cost hardest.** A block carries two times — its own
candle and the candle that broke structure — and it does not exist until the
second one. That is often several candles after the block candle, and usually
after price has displaced away from the zone, so the box frequently appears at a
moment you could no longer have taken the entry it marks. The popular scripts
hide the lag by drawing the box at its own candle, which shows a mark at a time
the market could not have known it. Two side effects worth knowing: under
`breakOn=wick` a block can appear a candle earlier, because the break itself
fires earlier; and under `method=fractal` the whole event stream shifts by
`strength`, so the blocks shift with it.

**A fair-value gap is one candle later here than in the widely used Python
package.** That package records the gap against the middle candle while reading
the next one through `shift(-1)`, so a consumer reading that row is using a
price the chart had not yet shown — the same class of error as the swing case
above. The cost of doing it honestly is that you cannot act on the gap at the
middle candle's close, which is where those scripts appear to let you in; the
gap is only ours once the third candle has printed.

The box's left edge reaching back to an earlier candle is not a repaint, and
neither is an open zone's right edge tracking the last candle: the mark's
identity, its price bounds and the candle it appeared at are all fixed when it
is stamped. It is the same convention the IDM line already uses.

One limit these marks made visible for the first time: the endpoint reads the
newest 5,000 candles and no more, and every mark is read from those candles
alone. A block or a gap that formed before the window opens is therefore absent
rather than drawn with a truncated edge — which is the right way round, but it
means a zone can disappear from a long chart as history scrolls off the front. A
coarser `resolution`, or a `toDate` that moves the window back, is how to read a
stretch the cap cuts off; a longer `fromDate` is not, because the cap always
keeps the newest candles.

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
| `zones` | `touch` | When price coming back into a block or a gap counts as having used it: `touch`, `midpoint` or `close` |
| `fvgMinSize` | `0` | Report only gaps at least this wide, in points. Zero applies no threshold, because the teaching names no number |
| `standingZonesOnly` | `false` | Report only the zones still standing at the last candle: unmitigated blocks and unfilled gaps. Off by default, so the history comes back whole. Runs are exempt — a finished run is not spent liquidity, it is the record of which way the market was being delivered, and one band alone gives nothing to read it against |
| `includeLive` | `true` | Append today's live bars past the stored history (intraday only) |

The response carries the candles, the swings (with `major` for the ones the
structure turned on, and `confirmedTimeUtc`), the events, the inducements, the
`orderBlocks`, the `gaps` and the `orderFlowRuns`, and the state at the last
candle: `trend`, `protectedLevel` (breaking it turns the trend), `breakLevel`
(breaking it, once induced, is a BOS), `inducementTaken` and `inducementLevel`.
It also echoes the settings it read with, `zones`, `fvgMinSize` and
`standingZonesOnly` among them, so a chart can say what it is showing.

A block and a gap each carry a `confirmedTimeUtc` — the candle they may first be
drawn at — as well as the `timeUtc` of their own candle, and the candle that used
them up where there is one: `mitigatedTimeUtc` for a block, `filledTimeUtc` for a
gap. A run needs no separate confirmation, because the break that starts it is
the confirmation: `fromTimeUtc` is that break, `toTimeUtc` the break that ended
the run, and `inducedTimeUtc` the candle that swept the leg's inducement. A null
in any of those means the zone or the run was still standing at the last candle,
never that it went unchecked.

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
- **Which candle is the order block.** The written ICT teaching: the last
  candle that closed against the move. LuxAlgo's concept page: that candle, or
  the consecutive cluster of them. The `smart-money-concepts` Python package:
  the most extreme low or high between the swing and the breakout, which need
  not be an opposing-close candle at all. LuxAlgo's own shipped *Price Action
  Concepts* indicator anchors blocks at swing points with a lookback — a
  different object again. This module takes the last opposing close, because it
  is the reading the teaching names and the only one the break itself makes
  knowable. The cost is that a one-sided impulse with no opposing candle in the
  range leaves no block at all, where the readings that anchor to an extreme or
  to a swing would still put a box on the chart.
- **The block's bounds.** Wick to wick holds every price that candle traded and
  suits invalidation; body-only gives a tighter zone, entered more precisely and
  missed more often; a common hybrid enters on the body and stops at the wick.
  This module takes wick to wick, for the same reason the inducement sweep is
  wick-inclusive here. The cost is a wider zone, so `zones=touch` spends it
  sooner.
- **What confirms a block.** LuxAlgo accepts a structure break, or a sweep, or
  displacement that leaves a gap. ICT is far stricter: the impulse must take the
  block candle's opposite extreme and close through it, and leave an imbalance,
  and print a lower-timeframe market structure shift. This module takes its own
  BOS or CHoCH, because that is the only confirmation this reader can state
  exactly. It is a looser gate than ICT's and a tighter one than LuxAlgo's, so
  this chart draws blocks that the strict teaching would not admit and misses
  ones a sweep or a displacement alone would have given. LuxAlgo's own page
  closes by saying that none of this is standardized, and that the rules should
  be treated as filters to test rather than laws; that applies to every line in
  this section.
- **The gap's middle candle.** The `smart-money-concepts` package requires the
  middle candle to close in the gap's direction; LuxAlgo's concept page reads
  the three candles geometrically, non-overlap alone. This module reads it
  geometrically, because the gap is a fact about where price did not trade and a
  body rule is a filter laid on top of it. The cost is more gaps than that
  package draws.
- **How big a gap has to be.** Every fast three-bar move prints one. LuxAlgo
  ships a volatility-multiplier threshold and a cap on how many gaps are kept;
  this module ships no threshold at all by default, because a number invented in
  the reader would be worse than an honest crowded chart, and prunes by what the
  market has done instead: `standingZonesOnly` reports the gaps still open and
  the blocks still unmitigated, which is a fact about price rather than a number
  someone chose. That is nearly all of them — on a month of 5-minute NIFTYBANK
  candles, 5 gaps of 647 and 4 blocks of 149 were still standing, so the pruned
  answer is 138 KB smaller. `fvgMinSize` is there for anyone who wants a
  threshold and has a reason for the number.
- **When a zone has been used.** Four readings are in circulation: a touch of
  the near edge, a wick through the 50% level, a full fill to the far edge, and
  a close beyond it. `zones` offers the first, second and fourth. The third — a
  complete fill with no close through — is not offered, and it sits between
  `midpoint` and `close` if you need it.
- **What "order flow" means at all.** Most retail usage means footprint:
  measured bid/ask delta, cumulative delta, volume at price. That is not what
  this draws, and it is not available here — the feed carries no aggressor side,
  and the tick store keeps one conflated snapshot a second, so trades inside a
  second cannot be counted or attributed to a price. What is drawn is the
  narrative reading, inferred from price alone. There is also a narrower ICT
  usage in which "bullish order flow" means the corrective down-close candles
  around a break — which are precisely the candles an order block is cut from.
  The chart shows blocks and runs as two separate layers, so it is worth saying
  which usage each one means: the block is that corrective candle, the run is
  the stretch of time the reading held.

Sources read for this module: the Pine source of *Smart Money Concepts
[LuxAlgo]* and *Market Structure with Inducements & Sweeps [LuxAlgo]*, *Market
Structure Event Trend* (AustrianTradingMachine), the `smart-money-concepts`
Python package, MQL5's *Swing Detector by Pullback (SMC)*, and the written
teaching at dailypriceaction, innercircletrader and liquidityscan. For the three
zone marks, also LuxAlgo's concept pages on order blocks, fair value gaps and
institutional order flow, and the documented behaviour of *Price Action Concepts
[LuxAlgo]*. None is a standards body; they are the most copied, not the most
correct.

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

Equal highs and lows (EQH/EQL) with a tolerance, premium/discount zones, a
second coarser structure series for "internal versus swing" structure, and
alerts when a level breaks.

Four more were left out deliberately while the zone marks were built, rather
than overlooked. **Breaker blocks:** a block that failed outright and is re-read
as the opposite level. Used once and failed are two different states in the
teaching, and only the first is modelled here. **Inversion fair-value gaps:** a
filled gap that flips role the same way. **Volumetric order blocks** and
**footprint order flow**: both need data this platform does not have. The reader
is handed nothing but OHLC, the tick store keeps one conflated snapshot a second
so trades can be neither counted nor attributed to a side, and this page's
default symbol is an index, which reports no volume at all. A volume-tinted
block or a delta ladder here would be blank on the chart you open, or invented.
If the feed ever carries a trade tape, that is the moment to reopen it.

## What a strategy can see

None of this. No strategy calls `/api/Smc`; the two that read structure —
`strategies/directional/smc_structure_break.py` and
`strategies/ghost_tangent_crossings.py` — use the Python port at
`src/AlgoTrading.PythonEngine/strategies/market_structure.py`, which reads one
candle at a time as a live runner does. That port carries the structure rules
only: swings, labels, the inducement, BOS and CHoCH. Order blocks, fair-value
gaps and the order-flow runs are read on the C# side for the chart and a
strategy cannot see them, the same way `method=fractal` has no Python equivalent
today. The debt comes due the day a strategy is asked to trade a block or a gap,
and porting them is a decision with its own design.
