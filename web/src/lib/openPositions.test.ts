import { describe, expect, it } from 'vitest'

import { STALE_AFTER_S, ageText, groupPositions, markState, positionAccounts, positionsCsv, positionsCsvName, totalSums } from './openPositions'
import { openPosition } from './openPositions.fixture'
import type { PositionGreeks } from './types'

const greeks = (thetaRupeesPerDay: number): PositionGreeks => ({
  source: 'chain',
  asOfUtc: null,
  stale: false,
  ivPercent: 12.4,
  delta: 0.5,
  gamma: 0.002,
  theta: -9.8,
  vega: 7.4,
  underlyingPrice: null,
  deltaQuantity: 65,
  deltaRupeesPerPoint: 65,
  thetaRupeesPerDay,
  vegaRupeesPerIvPoint: 962,
})

describe('markState', () => {
  it('calls a mark older than 30 s stale, and no mark none', () => {
    expect(markState({ markPrice: 69.5, markAgeSeconds: 4 })).toBe('fresh')
    expect(markState({ markPrice: 69.5, markAgeSeconds: STALE_AFTER_S })).toBe('fresh')
    expect(markState({ markPrice: 69.5, markAgeSeconds: STALE_AFTER_S + 1 })).toBe('stale')
    // An age the API could not give is not proof of freshness.
    expect(markState({ markPrice: 69.5, markAgeSeconds: null })).toBe('stale')
    expect(markState({ markPrice: null, markAgeSeconds: null })).toBe('none')
  })
})

describe('ageText', () => {
  it('says how old a mark is in the unit that reads', () => {
    expect([4, 75, 240, 5_340, 7_200, 200_000].map(ageText)).toEqual(['4 s', '75 s', '4 min', '89 min', '2 h', '2 d'])
  })
})

describe('groupPositions', () => {
  const adminGhost = openPosition({ positionId: 1, runId: 612, openedUtc: '2026-09-28T05:00:00Z', unrealizedPnl: 620, greeks: greeks(-1274) })
  const adminGhostLater = openPosition({ positionId: 2, runId: 612, underlying: 'NIFTY', openedUtc: '2026-09-28T06:00:00Z', unrealizedPnl: -100 })
  const adminBook = openPosition({ positionId: 3, runId: 540, isManualBook: true, strategyName: 'Manual', underlying: 'CRUDEOIL', unrealizedPnl: 2400, markAgeSeconds: 200_000 })
  const adminFulcrum = openPosition({ positionId: 4, runId: 603, strategyName: 'Fulcrum', underlying: 'SENSEX', markPrice: null, unrealizedPnl: null, markAgeSeconds: null })
  const trader = openPosition({ positionId: 5, runId: 611, userId: 7, userName: 'coderforchange', unrealizedPnl: -474, greeks: greeks(-1274) })
  const all = [trader, adminFulcrum, adminGhostLater, adminBook, adminGhost]

  it('groups by account, then run: the manual book first, then runs by id, legs oldest first', () => {
    const groups = groupPositions(all)
    expect(groups.map((a) => a.name)).toEqual(['admin', 'coderforchange'])
    expect(groups[0].runs.map((r) => r.runId)).toEqual([540, 603, 612])
    expect(groups[0].runs[2].positions.map((p) => p.positionId)).toEqual([1, 2])
    expect(groups[0].runs[0]).toMatchObject({ isManualBook: true, underlyings: ['CRUDEOIL'] })
  })

  it('sums the marked legs, counts the unmarked and the stale apart', () => {
    const [admin, cfc] = groupPositions(all)
    expect(admin).toMatchObject({ legs: 4, openPnl: 2920, unmarked: 1, stale: 1, thetaPerDay: -1274 })
    expect(admin.runs.find((r) => r.runId === 603)).toMatchObject({ openPnl: 0, unmarked: 1, thetaPerDay: null })
    expect(cfc).toMatchObject({ legs: 1, openPnl: -474, unmarked: 0, stale: 0 })
    expect(totalSums(groupPositions(all))).toMatchObject({ legs: 5, openPnl: 2446, unmarked: 1, stale: 1, thetaPerDay: -2548 })
  })

  it('narrows to one account', () => {
    expect(groupPositions(all, 7).map((a) => a.userId)).toEqual([7])
    expect(groupPositions(all, 99)).toEqual([])
    expect(groupPositions([])).toEqual([])
  })

  it('names an account whose user row is gone by its id', () => {
    expect(groupPositions([openPosition({ userId: 12, userName: null })])[0].name).toBe('user 12')
  })

  it('lists the accounts holding a leg, by user id', () => {
    expect(positionAccounts(all)).toEqual([
      { id: 1, name: 'admin' },
      { id: 7, name: 'coderforchange' },
    ])
  })
})

describe('positionsCsv', () => {
  const legs = [
    openPosition(),
    openPosition({
      positionId: 9002, runId: 600, strategyName: 'Manual', isManualBook: true, symbol: 'NSE:NIFTY2692923500CE',
      label: 'NIFTY 23500 CE · 29 Sep', direction: 'SHORT', markPrice: null, markUtc: null, markAgeSeconds: null,
      unrealizedPnl: null, carryForward: true, carriedFromRunId: 598, stopLossPrice: 120, targetPrice: 40,
      greeks: greeks(-1_240),
    }),
    openPosition({ positionId: 9003, userId: 2, userName: 'coderforchange', runId: 640, unrealizedPnl: -2_210.5 }),
  ]
  const lines = positionsCsv(groupPositions(legs)).split('\r\n')
  const header = lines[0].split(',')
  const row = (i: number) => Object.fromEntries(lines[i].split(',').map((v, n) => [header[n], v]))

  it("writes a line per leg in the page's order: account, then the manual book, then the runs", () => {
    expect(lines).toHaveLength(5)
    expect(lines.slice(1, 4).map((l) => l.split(',').slice(0, 3).join(' '))).toEqual([
      'admin Manual book 600',
      'admin GhostTangentCrossings 612',
      'coderforchange GhostTangentCrossings 640',
    ])
  })

  it('writes the mark and its time in IST, the P&L as a number, and the greeks when priced', () => {
    expect(row(2)).toMatchObject({
      Symbol: 'NSE:NIFTY2692923300PE', Side: 'Long', Lots: '2', 'Lot size': '65', Quantity: '130', Entry: '64.8',
      Mark: '69.57', 'Mark time (IST)': '2026-09-28T11:42:04+05:30', 'Open P&L (₹)': '620', Delta: '',
      'Carry forward': 'false', 'Opened (IST)': '2026-09-28T11:12:00+05:30',
    })
    expect(row(3)['Open P&L (₹)']).toBe('-2210.5')
  })

  it('leaves a leg with no mark without a P&L, never 0, and keeps its levels, greeks and carry', () => {
    expect(row(1)).toMatchObject({
      Side: 'Short', Mark: '', 'Mark time (IST)': '', 'Open P&L (₹)': '', 'Stop loss': '120', Target: '40',
      Delta: '0.5', 'IV %': '12.4', 'Theta (₹/day)': '-1240', 'Vega (₹ per IV point)': '962',
      'Carry forward': 'true', 'Carried from run': '598',
    })
  })

  it('names the file by the account picked and the time the legs were read', () => {
    expect(positionsCsvName(null, '2026-10-29T09:02:10Z')).toBe('positions-2026-10-29-1432.csv')
    expect(positionsCsvName('coderforchange', '2026-10-29T09:02:10Z')).toBe('positions-coderforchange-2026-10-29-1432.csv')
  })
})
