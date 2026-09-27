import { describe, expect, it } from 'vitest'
import { launchTarget } from './shared'
import type { StrategyActiveRun, StrategyListItem } from '../../lib/types'

const ADMIN = 1
const CODERFORCHANGE = 7

function run(underlying: string, ownerUserId: number): StrategyActiveRun {
  return {
    runId: Math.floor(Math.random() * 1e6), underlying, spotSymbol: '', lots: 2, stopLoss: null, target: null,
    startedBy: 'admin', startedUtc: '2026-09-28T03:48:00Z', processId: 1, ownerName: null, ownerUserId,
  } as StrategyActiveRun
}

// The morning plan in two accounts, after coderforchange lost its BANKNIFTY runner.
const fulcrum = {
  name: 'Fulcrum',
  activeRuns: [run('NIFTY', ADMIN), run('BANKNIFTY', ADMIN), run('NIFTY', CODERFORCHANGE)],
} as unknown as StrategyListItem

describe('the launch dialog greys out the target account\'s runs', () => {
  it('uses the account the admin picked, not the admin\'s own', () => {
    const target = launchTarget(fulcrum, ADMIN, true, CODERFORCHANGE)
    // Restarting coderforchange's BANKNIFTY must be possible; its NIFTY is taken.
    expect([...target.taken]).toEqual(['NIFTY'])
    expect(target.sendOwnerUserId).toBe(CODERFORCHANGE)
  })

  it('is the admin\'s own account until another is picked', () => {
    const target = launchTarget(fulcrum, ADMIN, true, null)
    expect([...target.taken].sort()).toEqual(['BANKNIFTY', 'NIFTY'])
    // The API reads no owner as the caller's.
    expect(target.sendOwnerUserId).toBeUndefined()
  })

  it('sends no owner when the admin picks their own account', () => {
    expect(launchTarget(fulcrum, ADMIN, true, ADMIN).sendOwnerUserId).toBeUndefined()
  })

  it('never lets a trader launch into another account', () => {
    // A stale pick (or a hand-edited one) is ignored: the API would refuse it anyway.
    const target = launchTarget(fulcrum, CODERFORCHANGE, false, ADMIN)
    expect(target.ownerUserId).toBe(CODERFORCHANGE)
    expect([...target.taken]).toEqual(['NIFTY'])
    expect(target.sendOwnerUserId).toBeUndefined()
  })
})
