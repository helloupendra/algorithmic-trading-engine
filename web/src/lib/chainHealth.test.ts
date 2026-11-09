import { describe, expect, it } from 'vitest'
import { chainState, writerDetail, type ChainWriterHealth } from './chainHealth'

describe('chain writer health', () => {
  it('distinguishes an unused backup and a closed market from a broken writer', () => {
    expect(chainState('disabled')).toEqual({ label: 'Disabled backup', tone: 'neutral' })
    expect(chainState('idle')).toEqual({ label: 'Market closed', tone: 'neutral' })
    expect(chainState('stopped').tone).toBe('neg')
    expect(chainState('stale').tone).toBe('warn')
    expect(chainState('conflict').tone).toBe('warn')
    expect(chainState('new-state').label).toBe('Unknown')
  })
  it('names the underlying that is stale even with a recent overall capture', () => {
    const writer: ChainWriterHealth = { provider: 'dhan', writer: 'Dhan API recorder', required: true, enabled: true,
      state: 'stale', staleAfterSeconds: 150, captureWindowSeconds: 900, lastCapturedUtc: new Date().toISOString(), captureLagSeconds: 0,
      underlyings: [{ underlying: 'BANKNIFTY', marketOpen: true, lastCapturedUtc: null, captureLagSeconds: null, state: 'stale', lastOutcome: 'failed' }] }
    expect(writerDetail(writer)).toContain('Required writer · enabled')
    expect(writerDetail(writer)).toContain('BANKNIFTY: stale (failed)')
    expect(writerDetail(writer)).toContain('150s while market open')
  })
})
