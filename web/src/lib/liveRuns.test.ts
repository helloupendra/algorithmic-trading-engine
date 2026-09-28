/**
 * Lists of runs re-priced from their open legs (lib/liveMarks.ts, the run
 * list section): the Desk's grid and strip, Run history, the trader's
 * Library, a strategy's track record, the risk page's exposure. And the
 * chain page's positions panel, re-priced the same way.
 */

import { describe, expect, it } from 'vitest'

import type { LiveTick } from './live'
import {
  chainPositionsWithTicks,
  exposureLegSymbols,
  exposureWithTicks,
  liveOpenPnl,
  runLegSymbols,
  runLegs,
  runWithTicks,
  runsWithTicks,
  unrealizedAt,
  withLiveMarks,
} from './liveMarks'
import { buildGrid, deskAccounts, liveAccountNets, sumFigures } from './desk'
import { runNetPnl } from './runHistory'
import { openPosition } from './openPositions.fixture'
import type { LiveRunSummary, OptionChainPosition, RiskExposureResponse } from './types'

/** When the legs answer (GET /api/Positions/open) was asked for: 28 Sep 2026, 11:42:04 IST. */
const LEGS_AT = Date.parse('2026-09-28T06:12:04Z')
/** When the run list (GET /api/Strategy/runs) was asked for, a little later. */
const LIST_AT = LEGS_AT + 2_000

function tick(symbol: string, lastTradedPrice: number, receivedAtMs: number): LiveTick {
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
  }
}

const prices = (...ticks: LiveTick[]) => new Map(ticks.map((t) => [t.symbol, t]))

const PE = 'NSE:NIFTY2692923300PE'
const CE = 'NSE:NIFTY2692923600CE'
const CRUDE = 'MCX:CRUDEOIL26OCTFUT'

/** Run 612's two legs: bought 2 × 65 of the PE at 64.80 and sold 2 × 65 of the CE at 88, as the legs answer marks them. */
const bought = openPosition({ positionId: 1, runId: 612, symbol: PE, direction: 'LONG', entryPrice: 64.8, markPrice: 69.57, markAgeSeconds: 4, unrealizedPnl: 620.1 })
const sold = openPosition({ positionId: 2, runId: 612, symbol: CE, direction: 'SHORT', entryPrice: 88, markPrice: 80, markAgeSeconds: 2, unrealizedPnl: 1040 })
/** The admin's manual book (run 700): long one crude future, lot size 100. */
const crude = openPosition({
  positionId: 3,
  runId: 700,
  isManualBook: true,
  strategyName: 'ManualOrders',
  symbol: CRUDE,
  underlying: 'CRUDEOIL',
  direction: 'LONG',
  lots: 1,
  lotSize: 100,
  entryPrice: 5_600,
  markPrice: 5_610,
  markAgeSeconds: 1,
  unrealizedPnl: 1_000,
})

let nextId = 900
function summary(over: Partial<LiveRunSummary> = {}): LiveRunSummary {
  nextId++
  return {
    runId: nextId,
    userId: 1,
    userName: 'admin',
    strategyId: 1,
    strategyName: 'GhostTangentCrossings',
    category: 'Directional',
    underlying: 'NIFTY',
    spotSymbol: 'NSE:NIFTY50-INDEX',
    lots: 2,
    role: null,
    lotSize: 65,
    risk: null,
    status: 'Running',
    isActive: true,
    startedUtc: '2026-09-28T03:45:00Z',
    stoppedUtc: null,
    stopReason: null,
    stoppedBy: null,
    durationSeconds: null,
    netPnl: 0,
    grossPnl: 0,
    charges: 0,
    realizedPnl: 0,
    unrealizedPnl: 0,
    trades: 0,
    openPositions: 0,
    groups: 0,
    ...over,
  }
}

/** Run 612 as the list answers it: ₹2,400 realized, ₹180 charges, its two legs open at the list's marks. */
const ghost = summary({
  runId: 612,
  realizedPnl: 2_400,
  grossPnl: 2_400,
  charges: 180,
  netPnl: 2_220,
  unrealizedPnl: 1_660.1,
  trades: 3,
  openPositions: 2,
})
const book = summary({ runId: 700, strategyName: 'ManualOrders', underlying: 'CRUDEOIL', unrealizedPnl: 1_000, openPositions: 1 })
const stopped = summary({
  runId: 611,
  status: 'Stopped',
  isActive: false,
  realizedPnl: -900,
  grossPnl: -900,
  charges: 120,
  netPnl: -1_020,
  trades: 4,
  openPositions: 0,
})

const legs = runLegs([bought, sold, crude], LEGS_AT)!

describe('runLegs', () => {
  it('groups the open legs by run, with when they were asked for', () => {
    expect(legs.asOfMs).toBe(LEGS_AT)
    expect(legs.byRun.get(612)).toEqual([bought, sold])
    expect(legs.byRun.get(700)).toEqual([crude])
    expect(runLegs(undefined, LEGS_AT)).toBeNull()
  })

  it('asks the hub for the legs of the live runs in the list and nothing else', () => {
    expect(runLegSymbols([ghost, stopped], legs).sort()).toEqual([PE, CE].sort())
    // A stopped run's legs, were any still listed, are not its figure any more.
    const heldByStopped = runLegs([{ ...bought, runId: stopped.runId }], LEGS_AT)
    expect(runLegSymbols([stopped], heldByStopped)).toEqual([])
    expect(runLegSymbols([ghost], null)).toEqual([])
  })
})

describe('liveOpenPnl', () => {
  it('is the sum of the run’s legs at the pushed prices, by the server’s formula', () => {
    const p = prices(tick(PE, 71, LIST_AT + 300), tick(CE, 82.5, LIST_AT + 800))
    const open = liveOpenPnl(ghost, legs, p, LIST_AT)
    expect(open).toBeCloseTo((71 - 64.8) * 130 + (88 - 82.5) * 130, 6)
    expect(open).toBeCloseTo(unrealizedAt(bought, 71) + unrealizedAt(sold, 82.5), 6)
  })

  it('takes a quiet leg at the legs answer’s mark when another leg was pushed', () => {
    const open = liveOpenPnl(ghost, legs, prices(tick(PE, 71, LIST_AT + 300)), LIST_AT)
    expect(open).toBeCloseTo(unrealizedAt(bought, 71) + sold.unrealizedPnl!, 6)
  })

  it('is what the Desk’s legs panel shows for the same legs and prices, summed', () => {
    const p = prices(tick(PE, 70.4, LIST_AT + 100), tick(CE, 79, LIST_AT + 200))
    const panel = withLiveMarks([bought, sold], p, { nowMs: LIST_AT + 1_000, answeredAtMs: LEGS_AT })
    expect(liveOpenPnl(ghost, legs, p, LIST_AT)).toBeCloseTo(panel.reduce((a, l) => a + l.unrealizedPnl!, 0), 6)
  })

  it('keeps the list’s figure while no leg was pushed after the list was asked for', () => {
    // Pushed while the legs were in flight, before the list was sent: the list already priced it.
    const p = prices(tick(PE, 71, LIST_AT - 500), tick(CE, 82.5, LIST_AT))
    expect(liveOpenPnl(ghost, legs, p, LIST_AT)).toBeNull()
    expect(liveOpenPnl(ghost, legs, new Map(), LIST_AT)).toBeNull()
  })

  it('keeps the list’s figure when the two answers disagree on the legs the run holds', () => {
    // The list is from after a fill that opened a third leg the legs answer has not seen yet.
    const p = prices(tick(PE, 71, LIST_AT + 300))
    expect(liveOpenPnl({ ...ghost, openPositions: 3 }, legs, p, LIST_AT)).toBeNull()
    // Or the legs answer is from after a close the list has not seen yet.
    expect(liveOpenPnl({ ...ghost, openPositions: 1 }, legs, p, LIST_AT)).toBeNull()
  })

  it('keeps the list’s figure when a leg has no mark at all: the list counted it at a P&L not known here', () => {
    const unmarked = { ...sold, markPrice: null, markUtc: null, markAgeSeconds: null, unrealizedPnl: null }
    const withUnmarked = runLegs([bought, unmarked], LEGS_AT)
    expect(liveOpenPnl(ghost, withUnmarked, prices(tick(PE, 71, LIST_AT + 300)), LIST_AT)).toBeNull()
    // Once that leg is pushed a price of its own, it has a mark again.
    const both = prices(tick(PE, 71, LIST_AT + 300), tick(CE, 82.5, LIST_AT + 400))
    expect(liveOpenPnl(ghost, withUnmarked, both, LIST_AT)).toBeCloseTo(unrealizedAt(bought, 71) + unrealizedAt(sold, 82.5), 6)
  })

  it('never touches a run that has stopped or holds nothing, whatever is pushed', () => {
    const p = prices(tick(PE, 71, LIST_AT + 300), tick(CE, 82.5, LIST_AT + 800))
    const heldByStopped = runLegs([{ ...bought, runId: stopped.runId }], LEGS_AT)
    expect(liveOpenPnl(stopped, heldByStopped, p, LIST_AT)).toBeNull()
    expect(liveOpenPnl({ ...ghost, openPositions: 0 }, legs, p, LIST_AT)).toBeNull()
    expect(liveOpenPnl(ghost, null, p, LIST_AT)).toBeNull()
  })
})

describe('runsWithTicks', () => {
  const list = [ghost, stopped, book]
  const p = prices(tick(PE, 71, LIST_AT + 300), tick(CRUDE, 5_625, LIST_AT + 50))

  it('moves only the live runs’ open book; realized, charges, trades and the realized net stay the list’s', () => {
    const [g, s, b] = runsWithTicks(list, legs, p, LIST_AT)
    expect(g.unrealizedPnl).toBeCloseTo(unrealizedAt(bought, 71) + sold.unrealizedPnl!, 6)
    expect(g).toMatchObject({ realizedPnl: 2_400, charges: 180, netPnl: 2_220, trades: 3, openPositions: 2 })
    expect(runNetPnl(g)).toBeCloseTo(2_220 + g.unrealizedPnl, 6)
    // A manual book is a run like any other: its legs name it.
    expect(b.unrealizedPnl).toBeCloseTo((5_625 - 5_600) * 100, 6)
    // Closed and historical runs stay exactly as answered.
    expect(s).toBe(stopped)
  })

  it('hands back the same list when nothing moved, and every row that did not move as the same object', () => {
    expect(runsWithTicks(list, legs, new Map(), LIST_AT)).toBe(list)
    expect(runsWithTicks(list, null, p, LIST_AT)).toBe(list)
    expect(runsWithTicks(list, legs, prices(tick(PE, 71, LIST_AT - 1)), LIST_AT)).toBe(list)
    const moved = runsWithTicks(list, legs, prices(tick(CRUDE, 5_625, LIST_AT + 50)), LIST_AT)
    expect(moved).not.toBe(list)
    expect(moved[0]).toBe(ghost)
    expect(moved[1]).toBe(stopped)
    expect(moved[2]).not.toBe(book)
  })

  it('hands back the same row when the pushed prices come to the figure it already has', () => {
    // Pushed at the very marks the list priced the legs at.
    const same = prices(tick(PE, 69.57, LIST_AT + 10), tick(CE, 80, LIST_AT + 10))
    const row = { ...ghost, unrealizedPnl: unrealizedAt(bought, 69.57) + unrealizedAt(sold, 80) }
    expect(runWithTicks(row, legs, same, LIST_AT)).toBe(row)
  })
})

describe('the Desk’s grid, re-priced', () => {
  const coder = summary({ runId: 613, userId: 2, userName: 'coderforchange', strategyName: 'SmcStructureBreak', unrealizedPnl: 0, openPositions: 0 })
  const runs = [ghost, stopped, book, coder]
  const accounts = deskAccounts(runs)
  const p = prices(tick(PE, 71, LIST_AT + 300), tick(CE, 82.5, LIST_AT + 800), tick(CRUDE, 5_625, LIST_AT + 50))
  const live = runsWithTicks(runs, legs, p, LIST_AT)
  const grid = buildGrid(live, accounts)

  it('keeps every total the sum of its parts at the pushed prices', () => {
    const rowsNet = grid.rows.reduce((a, r) => a + r.figures.net, 0)
    const accountsNet = grid.totals.reduce((a, t) => a + t.figures.net, 0)
    expect(grid.figures.net).toBeCloseTo(rowsNet, 6)
    expect(grid.figures.net).toBeCloseTo(accountsNet, 6)
    for (const t of grid.totals) {
      const cells = grid.rows.flatMap((r) => Object.values(r.cells).map((cs) => cs[grid.accounts.findIndex((a) => a.id === t.account.id)]))
      const cellsNet = cells.reduce((a, c) => a + (c?.figures.net ?? 0), 0)
      expect(t.figures.net).toBeCloseTo(cellsNet, 6)
      const byUnderlying = Object.values(t.byUnderlying).reduce((a, f) => a + (f?.net ?? 0), 0)
      expect(t.figures.net).toBeCloseTo(byUnderlying, 6)
    }
  })

  it('is the history rows’ own net, run by run: realized after charges plus the open book at the pushed prices', () => {
    expect(grid.figures.net).toBeCloseTo(live.reduce((a, r) => a + runNetPnl(r), 0), 6)
    const open = unrealizedAt(bought, 71) + unrealizedAt(sold, 82.5) + (5_625 - 5_600) * 100
    expect(grid.figures.open).toBeCloseTo(open, 6)
    expect(grid.figures.net).toBeCloseTo(2_220 + -1_020 + open, 6)
  })

  it('moves the grid’s total by exactly what the legs moved', () => {
    const before = buildGrid(runs, accounts)
    // The list priced each run's book at the same marks the legs answer carries.
    const moved =
      unrealizedAt(bought, 71) - bought.unrealizedPnl! + (unrealizedAt(sold, 82.5) - sold.unrealizedPnl!) + (2_500 - crude.unrealizedPnl!)
    expect(grid.figures.net - before.figures.net).toBeCloseTo(moved, 6)
    expect(sumFigures(live).net).toBeCloseTo(grid.figures.net, 6)
  })

  it('carries the Day P&L curve on for the accounts with a run still live, at the grid’s own figure', () => {
    const tips = liveAccountNets(grid, live)
    expect([...tips.keys()].sort()).toEqual([1, 2])
    expect(tips.get(1)).toBeCloseTo(grid.totals.find((t) => t.account.id === 1)!.figures.net, 6)
    const onlyAdminLive = liveAccountNets(grid, live.map((r) => (r.userId === 2 ? { ...r, isActive: false } : r)))
    expect([...onlyAdminLive.keys()]).toEqual([1])
  })
})

describe('exposureWithTicks', () => {
  const exposure: RiskExposureResponse = {
    activeRunsCount: 2,
    totalUnrealizedPnL: 1_660.1 + 1_000,
    totalRealizedPnL: 2_400,
    activeRuns: [
      { runId: 612, strategyName: 'GhostTangentCrossings', underlying: 'NIFTY', unrealizedPnL: 1_660.1, realizedPnL: 2_400, riskRules: {} as never },
      { runId: 614, strategyName: 'SmcStructureBreak', underlying: 'BANKNIFTY', unrealizedPnL: 0, realizedPnL: 0, riskRules: {} as never },
    ],
  }

  it('re-prices each live run’s open book from its legs and keeps the total the sum of the rows', () => {
    const next = exposureWithTicks(exposure, legs, prices(tick(PE, 71, LIST_AT + 300)), LIST_AT)
    expect(next.activeRuns[0].unrealizedPnL).toBeCloseTo(unrealizedAt(bought, 71) + sold.unrealizedPnl!, 6)
    // A run the legs answer holds nothing for keeps its figure.
    expect(next.activeRuns[1]).toBe(exposure.activeRuns[1])
    expect(next.totalUnrealizedPnL).toBeCloseTo(next.activeRuns.reduce((n, r) => n + r.unrealizedPnL, 0), 6)
    expect(next.totalRealizedPnL).toBe(2_400)
    expect(exposureLegSymbols(exposure, legs).sort()).toEqual([PE, CE].sort())
  })

  it('is the same answer for pushes older than it, or none', () => {
    expect(exposureWithTicks(exposure, legs, prices(tick(PE, 71, LIST_AT - 300)), LIST_AT)).toBe(exposure)
    expect(exposureWithTicks(exposure, null, prices(tick(PE, 71, LIST_AT + 300)), LIST_AT)).toBe(exposure)
  })
})

describe('chainPositionsWithTicks', () => {
  const held: OptionChainPosition = {
    runId: 612,
    strategyName: 'GhostTangentCrossings',
    isManual: false,
    userName: 'admin',
    groupId: 'g1',
    symbol: CE,
    instrumentType: 'CE',
    strikePrice: 23600,
    expiryDate: '2026-09-29',
    direction: 'SHORT',
    // Lots, on this answer.
    quantity: 2,
    lotSize: 65,
    averagePrice: 88,
    markPrice: 80,
    markUtc: '2026-09-28T06:12:02Z',
    unrealizedPnl: 1_040,
    stopLossPrice: null,
    targetPrice: null,
    openedUtc: '2026-09-28T05:42:00Z',
  }

  it('marks a held leg at a newer pushed price, with its time and the P&L at lots × lot size', () => {
    const [p] = chainPositionsWithTicks([held], prices(tick(CE, 82.5, LEGS_AT + 400)), LEGS_AT)
    expect(p).toMatchObject({ markPrice: 82.5, markUtc: new Date(LEGS_AT + 400).toISOString() })
    expect(p.unrealizedPnl).toBeCloseTo((88 - 82.5) * 2 * 65, 6)
    const long = chainPositionsWithTicks([{ ...held, direction: 'LONG' }], prices(tick(CE, 82.5, LEGS_AT + 400)), LEGS_AT)[0]
    expect(long.unrealizedPnl).toBeCloseTo((82.5 - 88) * 2 * 65, 6)
  })

  it('leaves the list alone for an older push, a push at the same price, or none', () => {
    const list = [held]
    expect(chainPositionsWithTicks(list, prices(tick(CE, 82.5, LEGS_AT - 1)), LEGS_AT)).toBe(list)
    expect(chainPositionsWithTicks(list, prices(tick(CE, 80, LEGS_AT + 1)), LEGS_AT)).toBe(list)
    expect(chainPositionsWithTicks(list, new Map(), LEGS_AT)).toBe(list)
  })
})
