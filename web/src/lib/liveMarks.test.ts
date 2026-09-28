import { describe, expect, it } from 'vitest'

import type { LiveTick } from './live'
import {
  foldTicks,
  freshPrice,
  pulseBehind,
  pulseWithTicks,
  quotesWithTicks,
  runViewSymbols,
  runViewWithTicks,
  unrealizedAt,
  watchlistBehind,
  watchlistWithTicks,
  withLegMarks,
  withLiveMarks,
} from './liveMarks'
import { groupPositions, markState, totalSums } from './openPositions'
import { liveNet } from './strategyList'
import { openPosition } from './openPositions.fixture'
import type { LivePosition, LiveQuote, MarketPulseItem, MarketPulseResponse, MyWatchlistItem, StrategyLiveView } from './types'

/** When the answer arrived, on this browser's clock: 28 Sep 2026, 11:42:04 IST. */
const ANSWERED = Date.parse('2026-09-28T06:12:04Z')

function tick(symbol: string, lastTradedPrice: number, receivedAtMs: number, over: Partial<LiveTick> = {}): LiveTick {
  return {
    symbol,
    lastTradedPrice,
    bidPrice: null,
    askPrice: null,
    volume: null,
    openInterest: null,
    impliedVolatility: null,
    exchangeTimestampUtc: new Date(receivedAtMs - 50).toISOString(),
    receivedAtMs,
    ...over,
  }
}

const prices = (...ticks: LiveTick[]) => new Map(ticks.map((t) => [t.symbol, t]))

describe('unrealizedAt', () => {
  // PaperPnl.Unrealized (src/AlgoTrading.Infrastructure/Services/PaperPnl.cs):
  // long (mark − entry) × lots × max(1, lotSize); short (entry − mark) × the same.
  it('is the server’s open P&L for a bought and a sold leg', () => {
    const bought = openPosition({ direction: 'LONG', entryPrice: 64.8, lots: 2, lotSize: 65 })
    const sold = openPosition({ direction: 'SHORT', entryPrice: 64.8, lots: 2, lotSize: 65 })
    expect(unrealizedAt(bought, 69.8)).toBeCloseTo(650, 6)
    expect(unrealizedAt(sold, 69.8)).toBeCloseTo(-650, 6)
    expect(unrealizedAt(sold, 60.8)).toBeCloseTo(520, 6)
  })

  it('agrees with the answer at the answer’s own mark', () => {
    const p = openPosition() // bought 2 × 65 at 64.80, marked 69.57, answered +₹620
    expect(unrealizedAt(p, p.markPrice!)).toBeCloseTo(p.unrealizedPnl!, 0)
  })

  it('counts lots × lot size, not the quantity field, and a lot size under 1 as 1', () => {
    // A share bought in the manual book: 10 shares, lot size 1.
    expect(unrealizedAt(openPosition({ direction: 'LONG', entryPrice: 1_500, lots: 10, lotSize: 1 }), 1_512.5)).toBeCloseTo(125, 6)
    expect(unrealizedAt(openPosition({ direction: 'LONG', entryPrice: 100, lots: 3, lotSize: 0 }), 101)).toBeCloseTo(3, 6)
  })
})

describe('withLiveMarks', () => {
  const bought = openPosition({ positionId: 1, symbol: 'NSE:NIFTY2692923300PE', direction: 'LONG', entryPrice: 64.8, markPrice: 69.57, markAgeSeconds: 4, unrealizedPnl: 620.1 })
  const sold = openPosition({ positionId: 2, symbol: 'NSE:NIFTY2692923600CE', direction: 'SHORT', entryPrice: 88, markPrice: 80, markAgeSeconds: 2, unrealizedPnl: 1040 })

  it('marks each leg at a newer pushed price: its price, time, age and P&L at the server’s formula', () => {
    const now = ANSWERED + 3_000
    const [b, s] = withLiveMarks([bought, sold], prices(tick(bought.symbol, 70.2, ANSWERED + 1_000), tick(sold.symbol, 83, ANSWERED + 2_500)), {
      nowMs: now,
      answeredAtMs: ANSWERED,
    })
    expect(b).toMatchObject({ markPrice: 70.2, markUtc: new Date(ANSWERED + 1_000).toISOString(), markAgeSeconds: 2 })
    expect(b.unrealizedPnl).toBeCloseTo((70.2 - 64.8) * 2 * 65, 6)
    expect(s).toMatchObject({ markPrice: 83, markAgeSeconds: 0 })
    expect(s.unrealizedPnl).toBeCloseTo((88 - 83) * 2 * 65, 6)
    // What a price does not change stays the answer's.
    expect(b).toMatchObject({ entryPrice: 64.8, lots: 2, lotSize: 65, carryForward: false, greeks: null })
  })

  it('keeps the answer’s mark when the push is older than it, and ages it to now', () => {
    // Pushed 6 s before the answer; the answer's mark was 4 s old, so newer than the push.
    const [b] = withLiveMarks([bought], prices(tick(bought.symbol, 69.1, ANSWERED - 6_000)), { nowMs: ANSWERED + 10_000, answeredAtMs: ANSWERED })
    expect(b).toMatchObject({ markPrice: 69.57, unrealizedPnl: 620.1, markAgeSeconds: 14 })
  })

  it('counts a quiet leg stale at 30 s between answers, and a pushed one fresh', () => {
    const now = ANSWERED + 40_000
    const quiet = withLiveMarks([bought, sold], new Map(), { nowMs: now, answeredAtMs: ANSWERED })
    expect(quiet.map(markState)).toEqual(['stale', 'stale'])
    expect(totalSums(groupPositions(quiet)).stale).toBe(2)

    const pushed = withLiveMarks([bought, sold], prices(tick(sold.symbol, 81, now - 1_500)), { nowMs: now, answeredAtMs: ANSWERED })
    expect(pushed.map(markState)).toEqual(['stale', 'fresh'])
    expect(totalSums(groupPositions(pushed)).stale).toBe(1)
  })

  it('marks a leg the answer had no mark for, and ignores a push with no price', () => {
    const unmarked = openPosition({ positionId: 3, markPrice: null, markUtc: null, markAgeSeconds: null, unrealizedPnl: null })
    const [m] = withLiveMarks([unmarked], prices(tick(unmarked.symbol, 66.8, ANSWERED - 60_000)), { nowMs: ANSWERED, answeredAtMs: ANSWERED })
    expect(m.markPrice).toBe(66.8)
    expect(m.unrealizedPnl).toBeCloseTo(2 * 2 * 65, 6)

    const [same] = withLiveMarks([bought], prices(tick(bought.symbol, 0, ANSWERED + 1_000)), { nowMs: ANSWERED, answeredAtMs: ANSWERED })
    expect(same).toBe(bought)
  })
})

describe('withLegMarks', () => {
  it('takes each leg’s LTP and P&L from the marked legs and keeps the answer’s order', () => {
    const legs = [
      { key: '2', label: 'big', ltp: 80, pnl: 1040 },
      { key: '1', label: 'small', ltp: 69.57, pnl: 620 },
    ]
    const marked = [openPosition({ positionId: 1, markPrice: 75, unrealizedPnl: 1326 }), openPosition({ positionId: 2, markPrice: 80, unrealizedPnl: 1040 })]
    const out = withLegMarks(legs, marked)
    expect(out.map((l) => l.label)).toEqual(['big', 'small'])
    expect(out[0]).toBe(legs[0])
    expect(out[1]).toMatchObject({ ltp: 75, pnl: 1326 })
  })
})

function livePosition(over: Partial<LivePosition> = {}): LivePosition {
  return {
    id: 1,
    groupId: 'g1',
    symbol: 'NSE:NIFTY2692923300PE',
    contract: null,
    side: 'BUY',
    lots: 2,
    lotSize: 65,
    quantity: 130,
    status: 'Open',
    entryPrice: 64.8,
    exitPrice: null,
    ltp: 69.57,
    ltpUpdatedUtc: '2026-09-28T06:12:03Z',
    pnl: 620.1,
    openedUtc: '2026-09-28T05:42:00Z',
    closedUtc: null,
    currentValue: 9044.1,
    pnlPoints: 4.77,
    pnlPercent: 7.36,
    stopLossPrice: null,
    targetPrice: null,
    ...over,
  }
}

function runView(positions: LivePosition[]): StrategyLiveView {
  return {
    strategyId: 3,
    name: 'GhostTangentCrossings',
    isActive: true,
    runId: 612,
    underlying: 'NIFTY',
    spotSymbol: 'NSE:NIFTY50-INDEX',
    spotLtp: 23_510,
    spotUpdatedUtc: '2026-09-28T06:12:03Z',
    lots: 2,
    lotSize: 65,
    lotSizeSource: 'master',
    stopLoss: null,
    target: null,
    startedBy: 'admin',
    startedUtc: '2026-09-28T03:50:00Z',
    stoppedUtc: null,
    stopReason: null,
    pnl: { realized: -300, unrealized: 1660.1, total: 1360.1, charges: 120, net: 1240.1 },
    groups: [
      { groupId: 'g1', pnl: 320.1, openLegs: 1, closedLegs: 1 },
      { groupId: 'g2', pnl: 1040, openLegs: 1, closedLegs: 0 },
    ],
    positions,
    activity: [],
    runner: null,
  }
}

describe('runViewWithTicks', () => {
  const bought = livePosition()
  const sold = livePosition({ id: 2, groupId: 'g2', symbol: 'NSE:NIFTY2692923600CE', side: 'SELL', entryPrice: 88, ltp: 80, pnl: 1040 })
  const closed = livePosition({ id: 3, status: 'Closed', symbol: 'NSE:NIFTY2692923300PE', exitPrice: 62.5, ltp: null, pnl: -300 })

  it('re-prices the open rows and the spot, and moves unrealized, total, net and each group by what the rows moved', () => {
    const view = runView([bought, sold, closed])
    const next = runViewWithTicks(
      view,
      prices(tick(bought.symbol, 70.57, ANSWERED + 500), tick(sold.symbol, 82, ANSWERED + 600), tick('NSE:NIFTY50-INDEX', 23_525.5, ANSWERED + 700)),
      ANSWERED,
    )
    const [b, s, c] = next.positions
    expect(b).toMatchObject({ ltp: 70.57, ltpUpdatedUtc: new Date(ANSWERED + 500).toISOString(), currentValue: null, pnlPoints: null })
    expect(b.pnl).toBeCloseTo((70.57 - 64.8) * 130, 6) // +130 on the answer's row
    expect(s.pnl).toBeCloseTo((88 - 82) * 130, 6) // −260
    expect(c).toBe(closed)
    expect(next.pnl.realized).toBe(-300)
    expect(next.pnl.charges).toBe(120)
    expect(next.pnl.unrealized).toBeCloseTo(1660.1 + 130 - 260, 6)
    expect(next.pnl.total).toBeCloseTo(1360.1 - 130, 6)
    expect(next.pnl.net).toBeCloseTo(1240.1 - 130, 6)
    expect(next.groups![0].pnl).toBeCloseTo(320.1 + 130, 6)
    expect(next.groups![1].pnl).toBeCloseTo(1040 - 260, 6)
    expect(next).toMatchObject({ spotLtp: 23_525.5, spotUpdatedUtc: new Date(ANSWERED + 700).toISOString() })
  })

  it('leaves the view alone for pushes the answer already carries', () => {
    const view = runView([bought, sold])
    expect(runViewWithTicks(view, prices(tick(bought.symbol, 70, ANSWERED - 100), tick('NSE:NIFTY50-INDEX', 23_600, ANSWERED)), ANSWERED)).toBe(view)
  })

  it('asks for the open legs and, while the run is live, its spot', () => {
    expect(runViewSymbols(runView([bought, sold, closed]))).toEqual([bought.symbol, sold.symbol, 'NSE:NIFTY50-INDEX'])
    expect(runViewSymbols({ ...runView([bought, closed]), isActive: false })).toEqual([bought.symbol])
  })

  it('gives a page total that is the sum of its run cards between two polls', () => {
    // Two runs on the Live runner; each card re-prices its own view, the header adds the views up.
    const first = runView([bought, sold, closed])
    const second = { ...runView([livePosition({ id: 9, symbol: 'NSE:NIFTY2692923400PE', entryPrice: 50, ltp: 52, pnl: 260 })]), runId: 613 }
    const views = [first, second]
    const pushed = prices(tick(bought.symbol, 71, ANSWERED + 300), tick('NSE:NIFTY2692923400PE', 49, ANSWERED + 400))
    const pushedSymbols = views.flatMap(runViewSymbols)
    expect(pushedSymbols).toEqual(expect.arrayContaining([bought.symbol, 'NSE:NIFTY2692923400PE']))

    const cards = views.map((v) => liveNet(runViewWithTicks(v, pushed, ANSWERED).pnl))
    const header = views.map((v) => runViewWithTicks(v, pushed, ANSWERED)).reduce((n, v) => n + liveNet(v.pnl), 0)
    expect(header).toBeCloseTo(cards[0] + cards[1], 6)
    // And it moved with the pushes: the raw answers would have read 15 s old.
    const raw = views.reduce((n, v) => n + liveNet(v.pnl), 0)
    expect(header - raw).toBeCloseTo((71 - 69.57) * 130 + (49 - 52) * 130, 6)
  })

  it('keeps the net absent when an older API sent none', () => {
    const view = { ...runView([bought]), pnl: { realized: 0, unrealized: 620.1, total: 620.1 } }
    const next = runViewWithTicks(view, prices(tick(bought.symbol, 70.57, ANSWERED + 1)), ANSWERED)
    expect(next.pnl.net).toBeUndefined()
    expect(next.pnl.total).toBeCloseTo(620.1 + 130, 6)
  })
})

function pulseItem(over: Partial<MarketPulseItem> = {}): MarketPulseItem {
  return {
    symbol: 'NSE:NIFTY50-INDEX',
    name: 'NIFTY 50',
    contract: null,
    lastTradedPrice: 23_510,
    previousClose: 23_400,
    open: 23_420,
    high: 23_520,
    low: 23_380,
    volume: null,
    change: 110,
    changePercent: 0.47,
    updatedUtc: '2026-09-28T06:12:03Z',
    isSubscribed: true,
    ...over,
  }
}

describe('pulseWithTicks', () => {
  const pulse: MarketPulseResponse = {
    groups: [
      { key: 'index', title: 'Indices', items: [pulseItem(), pulseItem({ symbol: 'NSE:NIFTYBANK-INDEX', name: 'BANK NIFTY' })] },
      { key: 'equity', title: 'Large caps', items: [pulseItem({ symbol: 'NSE:RELIANCE-EQ', name: 'RELIANCE' })] },
    ],
    latestQuoteUtc: '2026-09-28T06:12:03Z',
  }

  it('moves a tile to its newer price: change on the previous close, the range stretched, the quote time', () => {
    const next = pulseWithTicks(pulse, prices(tick('NSE:NIFTY50-INDEX', 23_540, ANSWERED + 900)), ANSWERED)
    const nifty = next.groups[0].items[0]
    expect(nifty).toMatchObject({ lastTradedPrice: 23_540, change: 140, high: 23_540, low: 23_380, updatedUtc: new Date(ANSWERED + 900).toISOString() })
    expect(nifty.changePercent).toBeCloseTo((140 / 23_400) * 100, 9)
    expect(next.latestQuoteUtc).toBe(new Date(ANSWERED + 900).toISOString())
    // The groups and tiles nothing was pushed for are the answer's own objects.
    expect(next.groups[0].items[1]).toBe(pulse.groups[0].items[1])
    expect(next.groups[1]).toBe(pulse.groups[1])
  })

  it('does not lay a new session’s first prices over yesterday’s row, and says the answer is behind', () => {
    const friday = { ...pulse, groups: [{ ...pulse.groups[0], items: [pulseItem({ updatedUtc: '2026-09-25T10:00:00Z' })] }] }
    const monday = prices(tick('NSE:NIFTY50-INDEX', 23_600, ANSWERED + 1_000))
    expect(pulseWithTicks(friday, monday, ANSWERED)).toBe(friday)
    expect(pulseBehind(friday, monday, ANSWERED)).toBe(true)
    expect(pulseBehind(pulse, monday, ANSWERED)).toBe(false)
  })
})

describe('watchlistWithTicks', () => {
  const row: MyWatchlistItem = { symbol: 'NSE:SBIN-EQ', sortOrder: 0, isSubscribed: true, lastTradedPrice: 812, open: 805, high: 815, low: 801, close: 800, volume: 1_000, updatedUtc: '2026-09-28T06:12:00Z' }

  it('moves a row to its newer price and returns the same list when nothing is newer', () => {
    const list = [row]
    expect(watchlistWithTicks(list, prices(tick(row.symbol, 810, ANSWERED - 1)), ANSWERED)).toBe(list)
    const [moved] = watchlistWithTicks(list, prices(tick(row.symbol, 799.5, ANSWERED + 1)), ANSWERED)
    expect(moved).toMatchObject({ lastTradedPrice: 799.5, high: 815, low: 799.5, close: 800 })
  })

  it("says the list is behind when the session's first prices land on yesterday's rows, as the pulse does", () => {
    // 09:15 IST Monday: the rows are Friday's close, the pushes are Monday's.
    const friday = [{ ...row, updatedUtc: '2026-09-25T10:00:00Z' }, { ...row, symbol: 'NSE:TCS-EQ', updatedUtc: '2026-09-25T10:00:00Z' }]
    const monday = prices(tick(row.symbol, 820, ANSWERED + 1_000))
    expect(watchlistWithTicks(friday, monday, ANSWERED)).toBe(friday)
    expect(watchlistBehind(friday, monday, ANSWERED)).toBe(true)
    // Today's rows, or pushes the answer already carries, are not behind.
    expect(watchlistBehind([row], monday, ANSWERED)).toBe(false)
    expect(watchlistBehind(friday, prices(tick(row.symbol, 820, ANSWERED - 1)), ANSWERED)).toBe(false)
  })
})

describe('quotes', () => {
  const quote = (symbol: string, over: Partial<LiveQuote> = {}): LiveQuote => ({
    symbol,
    dataType: 'symbolUpdate',
    lastTradedPrice: 100,
    open: 99,
    high: 101,
    low: 98,
    close: 97,
    volume: 10,
    updatedUtc: '2026-09-28T06:12:00Z',
    exchangeTimestampUtc: '2026-09-28T06:11:59Z',
    ...over,
  })

  it('folds a push into the list in one pass, the last price of a symbol winning, with the arrival as its write time', () => {
    const list = [quote('A'), quote('B')]
    const next = foldTicks(list, [tick('A', 101, ANSWERED, { bidPrice: 100.9 }), tick('A', 102, ANSWERED + 5, { bidPrice: 101.9 })])
    expect(next[0]).toMatchObject({ lastTradedPrice: 102, bidPrice: 101.9, close: 97, updatedUtc: new Date(ANSWERED + 5).toISOString() })
    expect(next[1]).toBe(list[1])
    expect(foldTicks(list, [tick('Z', 1, ANSWERED)])).toBe(list)
  })

  it('gives a symbol pushed before its first poll a row of its own, and keeps older pushes out', () => {
    const bySymbol = quotesWithTicks([quote('A')], prices(tick('A', 90, ANSWERED - 1), tick('NEW', 5, ANSWERED + 1)), ANSWERED)
    expect(bySymbol.get('A')!.lastTradedPrice).toBe(100)
    expect(bySymbol.get('NEW')).toMatchObject({ lastTradedPrice: 5, close: null })
  })
})

describe('freshPrice', () => {
  it('is a price pushed after the answer, never a zero or an older one', () => {
    expect(freshPrice(tick('A', 5, ANSWERED + 1), ANSWERED)).toBe(5)
    expect(freshPrice(tick('A', 5, ANSWERED), ANSWERED)).toBeNull()
    expect(freshPrice(tick('A', 0, ANSWERED + 1), ANSWERED)).toBeNull()
    expect(freshPrice(undefined, ANSWERED)).toBeNull()
  })
})
