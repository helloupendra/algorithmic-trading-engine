import { describe, expect, it } from 'vitest'

import { STALE_AFTER_S, ageText, groupPositions, markState, positionAccounts, totalSums } from './openPositions'
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
