import { describe, expect, it } from 'vitest'

import { MANUAL_BOOK, dayOrders, orderCounts, orderSources } from './orders'
import type { PaperOrderRow } from './types'

const run = (runId: number, userId: number, strategyName: string, underlying: string, isActive = false) => ({
  runId,
  userId,
  userName: userId === 2 ? 'admin' : 'coderforchange',
  strategyName,
  underlying,
  isActive,
})

let nextId = 1
function order(runId: number, createdUtc: string, over: Partial<PaperOrderRow> = {}): PaperOrderRow {
  const id = nextId++
  return {
    id,
    simulationRunId: runId,
    simulationSignalId: null,
    strategyName: 'GhostTangentCrossings',
    groupId: 'g1',
    symbol: 'NSE:NIFTY2692923100CE',
    side: 'BUY',
    quantity: 130,
    orderType: 'MARKET',
    status: 'Filled',
    requestedPrice: null,
    fillPrice: 121.4,
    createdUtc,
    filledUtc: createdUtc,
    ...over,
  }
}

describe('orderSources', () => {
  it("reads every run of the day, then the books the list does not hold, each once", () => {
    const sources = orderSources(
      [run(250, 2, 'GhostTangentCrossings', 'NIFTY'), run(260, 2, MANUAL_BOOK, 'MANUAL', true)],
      [
        { runId: 260, userId: 2, userName: 'admin' },
        { runId: 276, userId: 7, userName: 'coderforchange' },
        { runId: 276, userId: 7, userName: 'coderforchange' },
      ],
    )
    expect(sources.map((s) => [s.runId, s.isManualBook, s.live, s.underlying])).toEqual([
      [250, false, false, 'NIFTY'],
      [260, true, true, null],
      [276, true, true, null],
    ])
  })
})

describe('dayOrders', () => {
  const ghost = orderSources([run(250, 2, 'GhostTangentCrossings', 'NIFTY')], [])[0]
  const book = orderSources([], [{ runId: 276, userId: 7, userName: 'coderforchange' }])[0]

  it("merges the ledgers newest first, keeping only the day's orders", () => {
    const lines = dayOrders(
      [
        { source: ghost, orders: [order(250, '2026-09-25T04:10:00Z'), order(250, '2026-09-25T06:00:00Z')] },
        // A book's ledger reaches back over days: Thursday's order stays out.
        { source: book, orders: [order(276, '2026-09-25T15:35:11Z', { symbol: 'MCX:CRUDEOIL26OCTFUT', side: 'SELL' }), order(276, '2026-09-24T09:00:00Z')] },
        { source: ghost, orders: undefined },
      ],
      '2026-09-25',
    )
    expect(lines.map((l) => [l.source.runId, l.order.createdUtc])).toEqual([
      [276, '2026-09-25T15:35:11Z'],
      [250, '2026-09-25T06:00:00Z'],
      [250, '2026-09-25T04:10:00Z'],
    ])
    expect(lines[0].contract).toBe('CRUDEOIL26OCTFUT')
    expect(lines[1].contract).toBe('NIFTY 23100 CE · 29 Sep')
  })

  it('reads the day in IST: 00:30 IST on the 26th is the 26th, though UTC still says the 25th', () => {
    const late = [{ source: book, orders: [order(276, '2026-09-25T19:00:00Z')] }]
    expect(dayOrders(late, '2026-09-25')).toHaveLength(0)
    expect(dayOrders(late, '2026-09-26')).toHaveLength(1)
  })

  it('narrows to one account', () => {
    const ledgers = [
      { source: ghost, orders: [order(250, '2026-09-25T04:10:00Z')] },
      { source: book, orders: [order(276, '2026-09-25T05:10:00Z')] },
    ]
    expect(dayOrders(ledgers, '2026-09-25', 7).map((l) => l.source.userId)).toEqual([7])
  })

  it('counts filled, refused and still open orders, and the runs they came from', () => {
    const lines = dayOrders(
      [
        { source: ghost, orders: [order(250, '2026-09-25T04:10:00Z'), order(250, '2026-09-25T04:11:00Z', { status: 'Rejected' })] },
        { source: book, orders: [order(276, '2026-09-25T05:10:00Z', { status: 'Pending', filledUtc: null })] },
      ],
      '2026-09-25',
    )
    expect(orderCounts(lines)).toEqual({ orders: 3, filled: 1, notFilled: 1, open: 1, runs: 2 })
    expect(lines.find((l) => l.order.status === 'Pending')!.atUtc).toBe('2026-09-25T05:10:00Z')
  })
})
