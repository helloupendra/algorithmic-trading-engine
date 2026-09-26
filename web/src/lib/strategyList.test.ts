import { describe, expect, it } from 'vitest'
import { activeAccounts, activeUnderlyings, blockedUnderlyings, liveNet, realizedNet, runOwner, runningSummary } from './strategyList'
import type { StrategyActiveRun, StrategyListItem } from './types'

function run(underlying: string, ownerName: string | null, startedBy = 'admin'): StrategyActiveRun {
  return {
    runId: Math.floor(Math.random() * 1e6), underlying, spotSymbol: '', lots: 2, stopLoss: null, target: null,
    startedBy, startedUtc: '2026-09-28T03:48:00Z', processId: 1, ownerName,
  }
}

function strategy(runs: StrategyActiveRun[]): StrategyListItem {
  return { name: 'Fulcrum', activeRuns: runs } as unknown as StrategyListItem
}

describe('the morning plan in two accounts reads as two accounts, not a double deploy', () => {
  const both = strategy([
    run('BANKNIFTY', 'admin'), run('NIFTY', 'admin'),
    run('BANKNIFTY', 'coderforchange'), run('NIFTY', 'coderforchange'),
  ])

  it('lists each underlying once', () => {
    expect(activeUnderlyings(both)).toEqual(['BANKNIFTY', 'NIFTY'])
  })

  it('counts the accounts', () => {
    expect(activeAccounts(both)).toBe(2)
  })

  it('says so in the summary', () => {
    expect(runningSummary(both)).toBe('Fulcrum · BANKNIFTY, NIFTY (2 accounts)')
  })

  it('says nothing about accounts when there is one', () => {
    expect(runningSummary(strategy([run('NIFTY', 'admin')]))).toBe('Fulcrum · NIFTY')
  })

  it('is just the name when nothing runs', () => {
    expect(runningSummary(strategy([]))).toBe('Fulcrum')
  })
})

describe('runOwner', () => {
  it('prefers the owner over who started it', () => {
    expect(runOwner({ ownerName: 'coderforchange', startedBy: 'admin' })).toBe('coderforchange')
  })

  it('falls back to who started it on an API without owners', () => {
    expect(runOwner({ ownerName: null, startedBy: 'admin' })).toBe('admin')
  })
})

describe('what the launch dialog greys out', () => {
  const owned = (underlying: string, ownerUserId: number | undefined) =>
    ({ ...run(underlying, null), ownerUserId }) as StrategyActiveRun

  it('blocks only the underlyings this account already runs', () => {
    // The admin (1) lost its NIFTY runner; coderforchange (7) still runs NIFTY.
    const s = strategy([owned('NIFTY', 7), owned('BANKNIFTY', 1)])
    expect([...blockedUnderlyings(s, 1)]).toEqual(['BANKNIFTY'])
    expect([...blockedUnderlyings(s, 7)]).toEqual(['NIFTY'])
  })

  it('still blocks a run whose owner the API did not name', () => {
    expect([...blockedUnderlyings(strategy([owned('sensex', undefined)]), 1)]).toEqual(['SENSEX'])
  })
})

describe('live P&L after charges', () => {
  it('is the net when the API sends one, the gross from an older API', () => {
    expect(liveNet({ total: 3000, net: 2911.67 })).toBe(2911.67)
    expect(liveNet({ total: 3000 })).toBe(3000)
  })

  it('takes the charges off what was realized', () => {
    expect(realizedNet({ realized: 3000, charges: 88.33 })).toBeCloseTo(2911.67, 2)
    expect(realizedNet({ realized: -500 })).toBe(-500)
  })
})
