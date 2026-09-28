import { describe, expect, it } from 'vitest'

import {
  dayOrderCounts,
  mergeOrderPages,
  orderAccounts,
  orderKey,
  ordersQuery,
  priceSourceLabel,
  readOrdersFilter,
  runName,
  runOptionLabel,
  runOptions,
  sizeText,
  statusOptions,
  statusTone,
  writeOrdersFilter,
} from './orders'
import type { OrderRow, OrderRunFacet } from './types'

function row(kind: OrderRow['kind'], id: number, over: Partial<OrderRow> = {}): OrderRow {
  return {
    kind,
    id,
    atUtc: '2026-09-28T04:00:00Z',
    filledUtc: kind === 'order' ? '2026-09-28T04:00:00Z' : null,
    runId: 612,
    strategyName: 'GhostTangentCrossings',
    underlying: 'NIFTY',
    isManualBook: false,
    userId: 2,
    userName: 'admin',
    symbol: 'NSE:NIFTY2692923100CE',
    side: 'BUY',
    lots: 2,
    lotSize: 65,
    quantity: 130,
    orderType: kind === 'order' ? 'MARKET_SIM' : null,
    status: kind === 'order' ? 'Filled' : 'Rejected',
    requestedPrice: 121,
    fillPrice: kind === 'order' ? 121.4 : null,
    priceRule: kind === 'order' ? 'ask' : null,
    priceNote: kind === 'order' ? 'filled at the ask' : null,
    quoteAgeSeconds: null,
    staleQuote: false,
    groupId: 'G1',
    signalId: null,
    clientSignalId: null,
    reason: kind === 'rejection' ? 'RATE LIMIT EXCEEDED' : null,
    ...over,
  }
}

function facet(runId: number, userId: number, orders: number, filled: number, rejected: number, over: Partial<OrderRunFacet> = {}): OrderRunFacet {
  return {
    runId,
    userId,
    userName: userId === 2 ? 'admin' : 'coderforchange',
    strategyName: 'GhostTangentCrossings',
    underlying: 'NIFTY',
    isManualBook: false,
    runStatus: 'Running',
    orders,
    filled,
    rejected,
    ...over,
  }
}

describe('the filters in the URL', () => {
  it('reads what the URL narrows to and ignores what is malformed', () => {
    expect(readOrdersFilter(new URLSearchParams('date=2026-09-25&user=7&run=612&status=Rejected'))).toEqual({
      date: '2026-09-25',
      userId: 7,
      runId: 612,
      status: 'Rejected',
    })
    expect(readOrdersFilter(new URLSearchParams('date=25-09-2026&user=me&run=-3&status=%3Cb%3E'))).toEqual({
      date: null,
      userId: null,
      runId: null,
      status: null,
    })
  })

  it('round-trips through the URL, clears a filter set to null and keeps the params it does not own', () => {
    const start = new URLSearchParams('tab=x&run=5')
    const set = writeOrdersFilter(start, { date: '2026-09-28', userId: 7, runId: 612, status: 'Filled' })
    expect(readOrdersFilter(set)).toEqual({ date: '2026-09-28', userId: 7, runId: 612, status: 'Filled' })
    expect(set.get('tab')).toBe('x')

    const cleared = writeOrdersFilter(set, { runId: null, status: null })
    expect(cleared.has('run')).toBe(false)
    expect(cleared.has('status')).toBe(false)
    expect(cleared.get('user')).toBe('7')
    // The original is left as it was.
    expect(start.get('run')).toBe('5')
  })
})

describe('ordersQuery', () => {
  it('asks for the day and only the filters that narrow', () => {
    expect(ordersQuery({ date: '2026-09-28' })).toBe('?date=2026-09-28&take=200')
    expect(ordersQuery({ date: '2026-09-28', userId: 7, runId: 612, status: 'Rejected', skip: 200, take: 100 })).toBe(
      '?date=2026-09-28&userId=7&runId=612&status=Rejected&skip=200&take=100',
    )
    expect(ordersQuery({ date: '2026-09-28', userId: null, runId: null, status: null, skip: 0 })).toBe('?date=2026-09-28&take=200')
  })
})

describe('mergeOrderPages', () => {
  it('keeps each row once, as an order and a rejection sharing an id are two rows', () => {
    const first = { orders: [row('order', 9), row('rejection', 9), row('order', 8)] }
    // An order placed between the two reads pushed order 8 onto the next page too.
    const second = { orders: [row('order', 8), row('order', 7)] }
    expect(mergeOrderPages([first, second]).map(orderKey)).toEqual(['order-9', 'rejection-9', 'order-8', 'order-7'])
    expect(mergeOrderPages(undefined)).toEqual([])
  })
})

describe('counts and run options', () => {
  const runs = [
    facet(612, 2, 14, 13, 1),
    facet(613, 2, 4, 4, 0, { strategyName: 'Manual', underlying: null, isManualBook: true }),
    facet(700, 7, 6, 5, 0, { underlying: 'BANKNIFTY' }),
  ]

  it("sums the day's rows for every account, one account or one run", () => {
    expect(dayOrderCounts(runs, null, null)).toEqual({ orders: 24, filled: 22, rejected: 1, other: 1, runs: 3 })
    expect(dayOrderCounts(runs, 2, null)).toEqual({ orders: 18, filled: 17, rejected: 1, other: 0, runs: 2 })
    expect(dayOrderCounts(runs, null, 700)).toEqual({ orders: 6, filled: 5, rejected: 0, other: 1, runs: 1 })
    expect(dayOrderCounts(runs, 2, 700)).toEqual({ orders: 0, filled: 0, rejected: 0, other: 0, runs: 0 })
  })

  it("offers the account's runs, named as the page names them", () => {
    expect(runOptions(runs, 7).map((r) => r.runId)).toEqual([700])
    expect(runOptions(runs, null)).toHaveLength(3)
    expect(runOptionLabel(runs[0], false)).toBe('Ghost Tangent Crossings · NIFTY · #612 (14)')
    expect(runOptionLabel(runs[1], true)).toBe('admin · Manual book · #613 (4)')
    expect(runOptionLabel(runs[2], false)).toBe('Ghost Tangent Crossings · BANK · #700 (6)')
    expect(runName(row('order', 1, { isManualBook: true, underlying: null, strategyName: 'Manual' }))).toBe('Manual book')
    expect(orderAccounts(runs).map((a) => [a.id, a.name, a.tone])).toEqual([
      [2, 'admin', 1],
      [7, 'coderforchange', 2],
    ])
  })

  it('offers the statuses the day holds, Filled first and Rejected last, and the one picked', () => {
    const statuses = [
      { status: 'Rejected', orders: 1 },
      { status: 'Pending', orders: 1 },
      { status: 'Filled', orders: 22 },
    ]
    expect(statusOptions(statuses, null)).toEqual(['Filled', 'Pending', 'Rejected'])
    expect(statusOptions([{ status: 'Filled', orders: 3 }], 'Rejected')).toEqual(['Filled', 'Rejected'])
    expect(statusOptions([], null)).toEqual([])
  })
})

describe('how a row reads', () => {
  it('names the fill price source from the rule the API recorded', () => {
    expect(priceSourceLabel('bid')).toBe('bid')
    expect(priceSourceLabel('ask')).toBe('ask')
    expect(priceSourceLabel('ltp-less-half-spread')).toBe('LTP − ½ spread')
    expect(priceSourceLabel('ltp-plus-half-spread')).toBe('LTP + ½ spread')
    expect(priceSourceLabel('mark-less-half-spread')).toBe('mark − ½ spread')
    expect(priceSourceLabel('signal')).toBe('as asked')
    expect(priceSourceLabel('entry')).toBe('entry price')
    expect(priceSourceLabel('auction')).toBe('auction')
    expect(priceSourceLabel(null)).toBeNull()
  })

  it('sizes a row in lots by lot size, and in lots alone when the lot size is unknown', () => {
    expect(sizeText({ lots: 2, lotSize: 65 })).toBe('2 × 65')
    expect(sizeText({ lots: 1500, lotSize: 1 })).toBe('1,500 × 1')
    expect(sizeText({ lots: 3, lotSize: null })).toBe('3')
    expect(sizeText({ lots: null, lotSize: 65 })).toBeNull()
  })

  it('marks a rejection and an order not done', () => {
    expect(statusTone('Rejected')).toBe('neg')
    expect(statusTone('Cancelled')).toBe('warn')
    expect(statusTone('Filled')).toBeUndefined()
  })
})
