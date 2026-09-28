/**
 * Pushed prices laid over the answers the pages poll for.
 *
 * A page that moves by the second still reads its rows from the API: a leg's
 * entry, lots, realized P&L and charges, a tile's previous close. What the
 * socket adds is the price, and what follows from the price by the server's
 * own arithmetic. So each helper here takes the polled answer and the newest
 * ticks (lib/live.ts, useLivePrices) and returns the answer as the server
 * would give it now, without asking it again.
 *
 * One rule runs through all of them: a tick replaces a polled price only when
 * it is newer. Received after the answer's request was sent, it is (the
 * `answeredAtMs` each takes is lib/asOf.ts's answerAsOf); received before,
 * the answer already carries it or something newer, since the API marks from
 * the same quotes the hub pushes. Open positions compare the two ages
 * instead, because that answer says how old each mark was.
 */

import type { LiveTick } from './live'
import type {
  LivePosition,
  LiveQuote,
  LiveRunSummary,
  MarketPulseResponse,
  MyWatchlistItem,
  OpenPosition,
  OptionChainPosition,
  RiskExposureResponse,
  StrategyLiveView,
} from './types'

/** What an overlay needs of a tick. */
export type PricedTick = Pick<LiveTick, 'lastTradedPrice' | 'exchangeTimestampUtc' | 'receivedAtMs'>

type Prices = ReadonlyMap<string, PricedTick>

const iso = (ms: number) => new Date(ms).toISOString()

/** The tick's price when it has one and arrived after the answer it would be laid over; null otherwise. */
export function freshPrice(tick: PricedTick | undefined, answeredAtMs: number): number | null {
  if (!tick || tick.receivedAtMs <= answeredAtMs) return null
  const price = tick.lastTradedPrice
  return price != null && price > 0 ? price : null
}

// ---------------------------------------------------------------- open legs (GET /api/Positions/open)

/**
 * A leg's open P&L at a mark, before exit charges: PaperPnl.Unrealized, the
 * function the API books fills with and marks this answer by. Long earns
 * mark − entry, short entry − mark, times lots × lot size, with a lot size
 * under 1 counted as 1 as the server counts it.
 */
export function unrealizedAt(p: Pick<OpenPosition, 'direction' | 'entryPrice' | 'lots' | 'lotSize'>, mark: number): number {
  const units = p.lots * Math.max(1, p.lotSize)
  return String(p.direction).toUpperCase() === 'LONG' ? (mark - p.entryPrice) * units : (p.entryPrice - mark) * units
}

/**
 * The open legs with each mark moved to the newest pushed price when that
 * price is newer than the answer's mark: its price, its time (arrival), its
 * age and the P&L at it. Realized P&L, charges and everything else stay the
 * answer's.
 *
 * Every age is counted to `nowMs`, not to when the answer was read, so a leg
 * nothing has pushed for goes stale on screen at 30 s whether the page polls
 * every five seconds or every minute.
 */
export function withLiveMarks(
  positions: readonly OpenPosition[],
  prices: Prices,
  at: { nowMs: number; answeredAtMs: number },
): OpenPosition[] {
  const sinceAnswer = Math.max(0, at.nowMs - at.answeredAtMs)
  return positions.map((p) => {
    const tick = prices.get(p.symbol)
    const price = tick?.lastTradedPrice
    if (tick && price != null && price > 0) {
      // When the answer's mark was made, on this browser's clock.
      const markedAtMs = p.markAgeSeconds == null ? null : at.answeredAtMs - p.markAgeSeconds * 1000
      if (p.markPrice == null || markedAtMs == null || tick.receivedAtMs > markedAtMs) {
        return {
          ...p,
          markPrice: price,
          markUtc: iso(tick.receivedAtMs),
          markAgeSeconds: Math.max(0, Math.floor((at.nowMs - tick.receivedAtMs) / 1000)),
          unrealizedPnl: unrealizedAt(p, price),
        }
      }
    }
    if (p.markAgeSeconds == null || sinceAnswer < 1000) return p
    return { ...p, markAgeSeconds: p.markAgeSeconds + Math.floor(sinceAnswer / 1000) }
  })
}

/**
 * A list of legs built from the answer (the Desk's, largest open P&L first)
 * with each one's LTP and P&L taken from the marked legs. The order stays the
 * answer's: rows that swapped places on every tick would be unreadable.
 */
export function withLegMarks<L extends { key: string; ltp: number | null; pnl: number | null }>(
  legs: readonly L[],
  marked: readonly OpenPosition[],
): L[] {
  const byKey = new Map(marked.map((p) => [String(p.positionId), p]))
  return legs.map((leg) => {
    const p = byKey.get(leg.key)
    if (!p || (p.markPrice === leg.ltp && p.unrealizedPnl === leg.pnl)) return leg
    return { ...leg, ltp: p.markPrice, pnl: p.unrealizedPnl }
  })
}

// ---------------------------------------------------------------- a run's live view (the run card)

/**
 * An open row of a run's live view re-priced at a pushed price: LTP, its time
 * and the P&L, by the arithmetic the run card has always used, the server's
 * own (BUY earns mark − entry, SELL entry − mark, times the quantity in
 * units). The derived value columns are cleared so positionValues() works
 * them out from the new mark instead of keeping the ones made at the old one.
 *
 * The whole row moves, not just the price: an LTP that moves while the value
 * and P&L beside it sit behind reads as a bug, and on a trading screen a row
 * that disagrees with itself is worse than one that is uniformly a moment old.
 */
export function repricePosition(p: LivePosition, price: number, atMs: number): LivePosition {
  if (p.status !== 'Open' || price === p.ltp) return p
  const points = p.side === 'BUY' ? price - p.entryPrice : p.entryPrice - price
  return {
    ...p,
    ltp: price,
    // The price's age travels with it: the row says "as of …" from this.
    ltpUpdatedUtc: iso(atMs),
    pnl: points * p.quantity,
    currentValue: null,
    pnlPoints: null,
    pnlPercent: null,
  }
}

/**
 * The symbols a run's view is re-priced from: its open legs and, while the
 * run is live, its spot. The run card and the page totals above the cards ask
 * for the same ones, so both are priced from the same pushes.
 */
export function runViewSymbols(view: StrategyLiveView): string[] {
  return [
    ...view.positions.filter((p) => p.status === 'Open').map((p) => p.symbol),
    ...(view.isActive && view.spotSymbol ? [view.spotSymbol] : []),
  ]
}

/**
 * A run's live view with its open rows and its spot re-priced at the newer
 * pushed prices. The run's unrealized, total and net move by exactly what
 * the rows moved, and so does each group's P&L, so the tiles, the risk
 * meters and the table never disagree. Realized P&L and charges are the
 * answer's: a price does not change them.
 */
export function runViewWithTicks(view: StrategyLiveView, prices: Prices, answeredAtMs: number): StrategyLiveView {
  let shift = 0
  const shiftByGroup = new Map<string, number>()
  const positions = view.positions.map((p) => {
    const tick = prices.get(p.symbol)
    const price = freshPrice(tick, answeredAtMs)
    if (price == null) return p
    const next = repricePosition(p, price, tick!.receivedAtMs)
    const moved = next.pnl - p.pnl
    if (moved !== 0) {
      shift += moved
      shiftByGroup.set(p.groupId, (shiftByGroup.get(p.groupId) ?? 0) + moved)
    }
    return next
  })
  const spot = view.spotSymbol ? prices.get(view.spotSymbol) : undefined
  const spotPrice = freshPrice(spot, answeredAtMs)
  const rowsMoved = positions.some((p, i) => p !== view.positions[i])
  if (!rowsMoved && spotPrice == null) return view

  const pnl =
    shift === 0
      ? view.pnl
      : {
          ...view.pnl,
          unrealized: view.pnl.unrealized + shift,
          total: view.pnl.total + shift,
          net: view.pnl.net == null ? view.pnl.net : view.pnl.net + shift,
        }
  const groups =
    shift === 0 || !view.groups
      ? view.groups
      : view.groups.map((g) => {
          const moved = shiftByGroup.get(g.groupId)
          return moved ? { ...g, pnl: g.pnl + moved } : g
        })
  return {
    ...view,
    positions: rowsMoved ? positions : view.positions,
    spotLtp: spotPrice ?? view.spotLtp,
    spotUpdatedUtc: spotPrice != null ? iso(spot!.receivedAtMs) : view.spotUpdatedUtc,
    pnl,
    groups,
  }
}

// ---------------------------------------------------------------- a run list (GET /api/Strategy/runs), from the open legs

/**
 * The open legs of GET /api/Positions/open by run, with when that answer was
 * asked for: what a list of runs is re-priced from.
 *
 * A list of runs cannot be re-priced from itself: a history row carries its
 * open book as one number, not the legs behind it. Fetching every live run's
 * own view (what a run card reads) would be a request per run on a page of a
 * hundred rows. The open legs are one request for every run the viewer may
 * see, the Desk and Positions already hold it, and a fill re-reads it by desk
 * event; each leg is priced there by the function the list's figure is
 * (RunPnl.MarkOpenLegsAsync and OpenPositionsBuilder both value a leg with
 * PaperPnl.Unrealized at its latest quote).
 */
export interface RunLegs {
  byRun: ReadonlyMap<number, readonly OpenPosition[]>
  /** When the legs answer was asked for (lib/asOf.ts). */
  asOfMs: number
}

export function runLegs(positions: readonly OpenPosition[] | undefined, asOfMs: number): RunLegs | null {
  if (!positions) return null
  const byRun = new Map<number, OpenPosition[]>()
  for (const p of positions) {
    const list = byRun.get(p.runId)
    if (list) list.push(p)
    else byRun.set(p.runId, [p])
  }
  return { byRun, asOfMs }
}

/** A run whose figure can move with a price: live, and holding a leg. */
const repriceable = (run: Pick<LiveRunSummary, 'isActive' | 'openPositions'>) => run.isActive && run.openPositions > 0

/** The symbols a list of runs is re-priced from: the open legs of its live runs. */
export function runLegSymbols(runs: ReadonlyArray<Pick<LiveRunSummary, 'runId' | 'isActive' | 'openPositions'>> | undefined, legs: RunLegs | null): string[] {
  if (!runs || !legs) return []
  const out: string[] = []
  for (const run of runs) {
    if (!repriceable(run)) continue
    for (const leg of legs.byRun.get(run.runId) ?? []) out.push(leg.symbol)
  }
  return out
}

/**
 * A live run's open book now, from its legs at their newest marks: the
 * pushed price where it is newer than the legs answer's mark, else that mark
 * (withLiveMarks, the rule the Desk's legs and the Positions page use). Null
 * when the list's own figure stands, which is whenever this one would not be
 * a better number:
 *
 * - The run is not live, or holds no leg: nothing a price moves.
 * - The two answers disagree on how many legs it holds. One of them is from
 *   before a fill the other has seen, and a sum over the wrong legs is a
 *   wrong number, not an old one. The fill's desk event re-reads both, so
 *   this lasts a moment.
 * - No leg has a price pushed after the list was asked for: the list already
 *   has the newest prices there are. So a dropped socket falls back to the
 *   list's figure, which keeps being polled, instead of freezing at the last
 *   pushes.
 * - A leg has no mark at all. The list counted it at its stored P&L, which
 *   is not known here, and a sum that drops it would be a guess.
 */
export function liveOpenPnl(
  run: Pick<LiveRunSummary, 'runId' | 'isActive' | 'openPositions'>,
  legs: RunLegs | null,
  prices: Prices,
  listAsOfMs: number,
): number | null {
  if (!legs || !repriceable(run)) return null
  const own = legs.byRun.get(run.runId)
  if (!own || own.length !== run.openPositions) return null
  if (!own.some((leg) => freshPrice(prices.get(leg.symbol), listAsOfMs) != null)) return null
  let open = 0
  for (const leg of withLiveMarks(own, prices, { nowMs: legs.asOfMs, answeredAtMs: legs.asOfMs })) {
    if (leg.unrealizedPnl == null) return null
    open += leg.unrealizedPnl
  }
  return open
}

/**
 * A history row with its open book at the pushed prices (liveOpenPnl); the
 * same row when nothing newer is known. Realized P&L, charges, trades and
 * status are the list's: a price does not change them, and `netPnl` stays
 * the realized net it always is (runNetPnl adds the open book).
 */
export function runWithTicks<T extends LiveRunSummary>(run: T, legs: RunLegs | null, prices: Prices, listAsOfMs: number): T {
  const open = liveOpenPnl(run, legs, prices, listAsOfMs)
  return open == null || open === run.unrealizedPnl ? run : { ...run, unrealizedPnl: open }
}

/**
 * When each row of a list was asked for: one time for a list read in one
 * request, or a time per run for a paged one, whose pages are separate
 * requests (a run missing from the map is never taken as older than a push).
 */
export type ListAsOf = number | ReadonlyMap<number, number>

const rowAsOf = (asOf: ListAsOf, runId: number) => (typeof asOf === 'number' ? asOf : (asOf.get(runId) ?? Infinity))

/**
 * A list of runs re-priced row by row; the same array when no row moved, and
 * every row that did not move the same object, so a memoised row or a total
 * built from the list is not rebuilt for a price that did not reach it.
 */
export function runsWithTicks<T extends LiveRunSummary>(runs: readonly T[], legs: RunLegs | null, prices: Prices, listAsOf: ListAsOf): readonly T[] {
  if (!legs || prices.size === 0) return runs
  let changed = false
  const out = runs.map((run) => {
    const next = runWithTicks(run, legs, prices, rowAsOf(listAsOf, run.runId))
    if (next !== run) changed = true
    return next
  })
  return changed ? out : runs
}

/**
 * The risk page's exposure (GET /api/Risk/exposure, admin) with each live
 * run's open book at the pushed prices of its legs, by liveOpenPnl, and the
 * total the sum of the rows. The answer carries no count of a run's legs to
 * check the legs answer against, so a run is re-priced from whatever legs
 * that answer holds for it; a fill re-reads both by desk event. Realized P&L
 * stays the answer's. The same answer when no row moved.
 */
export function exposureWithTicks(exposure: RiskExposureResponse, legs: RunLegs | null, prices: Prices, answeredAtMs: number): RiskExposureResponse {
  if (!legs || prices.size === 0) return exposure
  let changed = false
  const activeRuns = exposure.activeRuns.map((r) => {
    const held = legs.byRun.get(r.runId)?.length ?? 0
    const open = liveOpenPnl({ runId: r.runId, isActive: true, openPositions: held }, legs, prices, answeredAtMs)
    if (open == null || open === r.unrealizedPnL) return r
    changed = true
    return { ...r, unrealizedPnL: open }
  })
  if (!changed) return exposure
  return { ...exposure, activeRuns, totalUnrealizedPnL: activeRuns.reduce((n, r) => n + r.unrealizedPnL, 0) }
}

/** The symbols the exposure's rows are re-priced from: the legs of its live runs. */
export function exposureLegSymbols(exposure: RiskExposureResponse | undefined, legs: RunLegs | null): string[] {
  if (!exposure || !legs) return []
  return exposure.activeRuns.flatMap((r) => (legs.byRun.get(r.runId) ?? []).map((l) => l.symbol))
}

// ---------------------------------------------------------------- the chain's positions panel (GET /api/OptionChain/positions)

/**
 * The chain page's held legs at their newer pushed prices: mark, its time and
 * the P&L at it, by PaperPnl.Unrealized as the endpoint values them there
 * (`quantity` is lots on this answer). The same list when nothing moved.
 */
export function chainPositionsWithTicks(positions: OptionChainPosition[], prices: Prices, answeredAtMs: number): OptionChainPosition[] {
  let changed = false
  const out = positions.map((p) => {
    const tick = prices.get(p.symbol)
    const price = freshPrice(tick, answeredAtMs)
    if (price == null || price === p.markPrice) return p
    changed = true
    const leg = { direction: p.direction as OpenPosition['direction'], entryPrice: p.averagePrice, lots: p.quantity, lotSize: p.lotSize }
    return { ...p, markPrice: price, markUtc: iso(tick!.receivedAtMs), unrealizedPnl: unrealizedAt(leg, price) }
  })
  return changed ? out : positions
}

// ---------------------------------------------------------------- latest quotes (GET /api/LiveData/latest/all)

/**
 * A quote with a pushed tick folded in: price, bid/ask, volume, the exchange's
 * stamp, and the arrival as its write time, to match what the REST snapshot
 * puts in `updatedUtc`. It was once given the tick's exchange stamp, so the
 * field meant "when we got it" after a poll and "when it last traded" after a
 * push, and a quiet contract's age jumped between 1 s and minutes.
 */
export function quoteWithTick(q: LiveQuote, tick: LiveTick): LiveQuote {
  return {
    ...q,
    lastTradedPrice: tick.lastTradedPrice ?? q.lastTradedPrice,
    bidPrice: tick.bidPrice ?? q.bidPrice,
    askPrice: tick.askPrice ?? q.askPrice,
    volume: tick.volume ?? q.volume,
    updatedUtc: iso(tick.receivedAtMs),
    exchangeTimestampUtc: tick.exchangeTimestampUtc ?? q.exchangeTimestampUtc,
  }
}

/**
 * One push folded into the quotes list in one pass (one cache write, one
 * render per push, however many symbols it carries). The same list when the
 * push touches none of its symbols.
 */
export function foldTicks(quotes: LiveQuote[], ticks: readonly LiveTick[]): LiveQuote[] {
  const bySymbol = new Map<string, LiveTick>()
  // Last one wins: within a push the newest price for a symbol is the one to show.
  for (const tick of ticks) bySymbol.set(tick.symbol, tick)
  if (!quotes.some((q) => bySymbol.has(q.symbol))) return quotes
  return quotes.map((q) => {
    const tick = bySymbol.get(q.symbol)
    return tick ? quoteWithTick(q, tick) : q
  })
}

/**
 * The polled quotes by symbol, with the newer pushed ticks folded in. A
 * symbol pushed but not yet in the answer (just subscribed) gets a row from
 * the tick alone, so its price shows before the next poll; its previous
 * close, and so its change, waits for that poll.
 */
export function quotesWithTicks(
  quotes: readonly LiveQuote[] | undefined,
  prices: ReadonlyMap<string, LiveTick>,
  answeredAtMs: number,
): Map<string, LiveQuote> {
  const bySymbol = new Map((quotes ?? []).map((q) => [q.symbol, q]))
  for (const [symbol, tick] of prices) {
    if (tick.receivedAtMs <= answeredAtMs) continue
    const q = bySymbol.get(symbol) ?? {
      symbol,
      dataType: '',
      lastTradedPrice: null,
      open: null,
      high: null,
      low: null,
      close: null,
      volume: null,
      updatedUtc: iso(tick.receivedAtMs),
      exchangeTimestampUtc: null,
    }
    bySymbol.set(symbol, quoteWithTick(q, tick))
  }
  return bySymbol
}

// ---------------------------------------------------------------- a day's quote (the pulse, the watchlist)

const IST_OFFSET_MS = 330 * 60_000
const istDayOf = (ms: number) => new Date(ms + IST_OFFSET_MS).toISOString().slice(0, 10)

/** When the tick was traded: the exchange's stamp, else when it arrived. */
function tradedAtMs(tick: PricedTick): number {
  const ms = tick.exchangeTimestampUtc ? Date.parse(tick.exchangeTimestampUtc) : NaN
  return Number.isNaN(ms) ? tick.receivedAtMs : ms
}

interface DayQuote {
  lastTradedPrice: number | null
  high: number | null
  low: number | null
  updatedUtc: string | null
}

/** Whether a fresh tick is from a later IST day than the row it would move: the answer is yesterday's. */
function behind(item: DayQuote, tick: PricedTick | undefined, answeredAtMs: number): boolean {
  if (freshPrice(tick, answeredAtMs) == null) return false
  const rowMs = item.updatedUtc ? Date.parse(item.updatedUtc) : NaN
  return Number.isNaN(rowMs) || istDayOf(tradedAtMs(tick!)) > istDayOf(rowMs)
}

/**
 * A day's quote moved to a newer pushed price: LTP, the day's high and low
 * stretched to take it in, and the arrival as its time. Only on the row's own
 * IST day: the first ticks of a session are not laid over yesterday's row,
 * whose high, low and previous close are the last session's. The next answer
 * brings today's row, and `pulseBehind` asks for it at once.
 */
function dayQuoteWithTick<T extends DayQuote>(item: T, tick: PricedTick | undefined, answeredAtMs: number): T {
  const price = freshPrice(tick, answeredAtMs)
  if (price == null || behind(item, tick, answeredAtMs)) return item
  return {
    ...item,
    lastTradedPrice: price,
    high: item.high == null ? null : Math.max(item.high, price),
    low: item.low == null ? null : Math.min(item.low, price),
    updatedUtc: iso(tick!.receivedAtMs),
  }
}

/**
 * The market pulse with each tile moved to its newer pushed price: price,
 * change against the previous close (the server's rule), the day's range, and
 * the newest quote time the "prices fresh" cell reads.
 */
export function pulseWithTicks(pulse: MarketPulseResponse, prices: Prices, answeredAtMs: number): MarketPulseResponse {
  let latestMs = pulse.latestQuoteUtc ? Date.parse(pulse.latestQuoteUtc) : NaN
  let changed = false
  const groups = pulse.groups.map((group) => {
    let moved = false
    const items = group.items.map((item) => {
      const next = dayQuoteWithTick(item, prices.get(item.symbol), answeredAtMs)
      if (next === item) return item
      moved = true
      const at = Date.parse(next.updatedUtc!)
      if (Number.isNaN(latestMs) || at > latestMs) latestMs = at
      const prev = item.previousClose
      if (prev == null || prev === 0) return next
      const change = next.lastTradedPrice! - prev
      return { ...next, change, changePercent: (change / prev) * 100 }
    })
    if (!moved) return group
    changed = true
    return { ...group, items }
  })
  if (!changed) return pulse
  return { ...pulse, groups, latestQuoteUtc: Number.isNaN(latestMs) ? pulse.latestQuoteUtc : iso(latestMs) }
}

/** Whether any tile's pushed price is from a later day than its row: the pulse is worth asking for again now. */
export function pulseBehind(pulse: MarketPulseResponse, prices: Prices, answeredAtMs: number): boolean {
  return pulse.groups.some((g) => g.items.some((item) => behind(item, prices.get(item.symbol), answeredAtMs)))
}

/** Whether any watchlist row's pushed price is from a later day than the row: the list is worth asking for again now. */
export function watchlistBehind(items: readonly MyWatchlistItem[], prices: Prices, answeredAtMs: number): boolean {
  return items.some((item) => behind(item, prices.get(item.symbol), answeredAtMs))
}

/** The viewer's watchlist rows moved to their newer pushed prices; the same list when none moved. */
export function watchlistWithTicks(items: MyWatchlistItem[], prices: Prices, answeredAtMs: number): MyWatchlistItem[] {
  let changed = false
  const next = items.map((item) => {
    const moved = dayQuoteWithTick(item, prices.get(item.symbol), answeredAtMs)
    if (moved !== item) changed = true
    return moved
  })
  return changed ? next : items
}
